//! Hygienic expansion and typed lowering for the structured language.
//!
//! A session owns persistent lexical bindings and identity allocation. Compilation
//! works on a candidate session; failed expansion or analysis cannot alter the
//! environment used by the next REPL submission.

use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;

use super::{Alias, Backend, CompileContext, Compiler, Location, Space};
use crate::hir::{
    self, Argument, Input, Instruction, InstructionKind, Output, ProcessId, ReadPort,
};
use crate::hir::{BarrierKind, Region};
use crate::language::{self, BindingOp, DestinationMode, Document, Expr, ExprKind, Name, PathExpr};
use crate::relocatable::{
    Anchor, Export, ExportValue, Namespace, Path as RelocPath, Relocation, Slot,
};
use crate::source::{InstructionOrigin, LocatedReport, Origins, Source, SourceId, SourceSpan};
use crate::syntax::{OperandSource, Report, Span};

mod diagnostics;
mod ranges;
mod scripts;
mod symbols;
mod v6;
use diagnostics::{ExpansionMap, ExpansionTrace, Failure, Origin as ExpansionOrigin};

#[derive(Clone, Debug)]
enum Value {
    Path(PathValue),
    Literal(Arc<str>),
    Bool(bool),
    Dynamic(hir::control::Expr),
    Range(hir::control::RangeExpr),
    Space(Space),
    Process(ProcessId),
    Port(ProcessId, Port),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
enum Port {
    Stdin,
    Read(ReadPort),
}

#[derive(Clone, Debug)]
struct PathValue {
    backend: Option<Backend>,
    path: String,
    is_file: bool,
    mode: DestinationMode,
    relocation: Option<RelocPath>,
    relocation_unit: Option<SourceId>,
}

impl From<Alias> for PathValue {
    fn from(value: Alias) -> Self {
        Self {
            backend: value.backend,
            path: value.path,
            is_file: value.is_file,
            mode: DestinationMode::Exact,
            relocation: None,
            relocation_unit: None,
        }
    }
}

impl PathValue {
    fn legacy(&self) -> Alias {
        Alias {
            backend: self.backend.clone(),
            path: self.path.clone(),
            is_file: self.is_file,
        }
    }
}

struct TransferValues {
    source: Input,
    destination: Output,
    owned: Vec<((ProcessId, Port), Span)>,
    relocations: Vec<(Slot, RelocPath)>,
}

#[derive(Clone, Debug)]
struct Macro {
    name: Name,
    parameters: Vec<Name>,
    body: language::Block,
    globals: Arc<imbl::OrdMap<String, Value>>,
    global_sites: Arc<imbl::OrdMap<String, SourceSpan>>,
    global_expansions: Arc<ExpansionMap>,
    macros: Arc<imbl::OrdMap<String, Arc<Macro>>>,
    symbols: Arc<imbl::OrdMap<String, Arc<symbols::Symbol>>>,
    source_file: Option<PathBuf>,
    source: Arc<Source>,
}

#[derive(Clone, Debug, Default)]
struct Scope {
    // Macro debug sources change during expansion; the caller's link unit does not.
    slots: Arc<std::sync::atomic::AtomicUsize>,
    cells: imbl::OrdMap<String, hir::control::SlotId>,
    current_unit: Option<SourceId>,
    source: Option<Arc<Source>>,
    symbols: Arc<imbl::OrdMap<String, Arc<symbols::Symbol>>>,
    symbol_calls: Vec<(Arc<symbols::Symbol>, SourceSpan)>,
    symbol_budget: symbols::Budget,
    defined_units: Arc<imbl::OrdMap<String, SourceId>>,
    values: Arc<imbl::OrdMap<String, Value>>,
    macros: Arc<imbl::OrdMap<String, Arc<Macro>>>,
    globals: Option<Arc<imbl::OrdMap<String, Value>>>,
    global_macros: Option<Arc<imbl::OrdMap<String, Arc<Macro>>>>,
    global_symbols: Option<Arc<imbl::OrdMap<String, Arc<symbols::Symbol>>>>,
    local_templates: imbl::OrdSet<String>,
    imported: imbl::OrdSet<String>,
    sites: Arc<imbl::OrdMap<String, SourceSpan>>,
    global_sites: Option<Arc<imbl::OrdMap<String, SourceSpan>>>,
    expansions: Arc<ExpansionMap>,
    global_expansions: Option<Arc<ExpansionMap>>,
}

/// Persistent structured compiler environment, independent of executor state.
#[derive(Clone, Debug, Default)]
pub struct Session {
    legacy: Compiler,
    scope: Scope,
    next_process: usize,
    consumed: imbl::HashMap<(ProcessId, Port), SourceSpan>,
}

#[derive(Clone, Debug)]
pub struct Lowered {
    pub program: hir::Program,
    pub origins: Origins,
    pub relocations: Vec<Relocation>,
    pub source_modules: Vec<crate::object::SourceModule>,
}

impl Session {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn runtime_slot_base(&self) -> usize {
        self.scope.slots.load(std::sync::atomic::Ordering::Relaxed)
    }

    pub fn reserve_runtime_slots(&mut self, next: usize) {
        self.scope
            .slots
            .fetch_max(next, std::sync::atomic::Ordering::Relaxed);
    }

    pub fn from_legacy(compiler: Compiler) -> Self {
        let mut session = Self::new();
        session.merge_legacy(&compiler);
        session
    }

    pub fn try_from_legacy(compiler: &Compiler) -> Result<Self, Report> {
        let mut session = Self::new();
        session.try_merge_legacy(compiler)?;
        Ok(session)
    }

    /// The legacy grammar has separate namespace and item name tables. A mode
    /// switch must diagnose a dual declaration instead of silently discarding it.
    pub fn try_merge_legacy(&mut self, compiler: &Compiler) -> Result<(), Report> {
        if let Some(name) = compiler
            .spaces
            .keys()
            .find(|name| compiler.aliases.contains_key(*name))
        {
            return Err(Report::new(format!("legacy binding '{name}' names both a namespace and a path item"), Span::default())
                .help("Rename one of the two legacy declarations before switching syntax; the structured lexical environment gives each name one value."));
        }
        self.merge_legacy(compiler);
        Ok(())
    }

    /// Import physical/path bindings without discarding structured-only values.
    /// Bindings explicitly present in the legacy environment win that name.
    pub fn merge_legacy(&mut self, compiler: &Compiler) {
        let values = Arc::make_mut(&mut self.scope.values);
        for (name, space) in compiler.spaces.iter() {
            values.insert(name.clone(), Value::Space(space.clone()));
            self.scope.cells.remove(name);
            Arc::make_mut(&mut self.scope.sites).remove(name);
            Arc::make_mut(&mut self.scope.expansions).remove(name);
            Arc::make_mut(&mut self.scope.defined_units).remove(name);
        }
        for (name, path) in compiler.aliases.iter() {
            let mut path: PathValue = path.clone().into();
            path.mode = legacy_path_mode(&path.path);
            values.insert(name.clone(), Value::Path(path));
            self.scope.cells.remove(name);
            Arc::make_mut(&mut self.scope.sites).remove(name);
            Arc::make_mut(&mut self.scope.expansions).remove(name);
            Arc::make_mut(&mut self.scope.defined_units).remove(name);
        }
        self.refresh_legacy();
    }

    /// Legacy path/space bindings for embedders migrating from the flat API.
    /// Literal, process and macro bindings are represented only by this session.
    pub fn legacy(&self) -> &Compiler {
        &self.legacy
    }

    pub fn reset(&mut self) {
        // Runtime identities are never recycled while an executor might still
        // retain an older process.
        let next_process = self.next_process;
        let slots = self.scope.slots.clone();
        *self = Self {
            next_process,
            ..Self::default()
        };
        self.scope.slots = slots;
    }

    pub fn bindings(&self) -> Vec<String> {
        let mut bindings = Vec::new();
        let mut endpoints = HashMap::<Backend, String>::new();
        for (name, value) in self.scope.values.iter() {
            if let Value::Space(space) = value {
                bindings.push(format!("{name} => {space}"));
                if let Space::Endpoint(backend) = space {
                    endpoints
                        .entry(backend.clone())
                        .or_insert_with(|| name.clone());
                }
            }
        }
        for (name, value) in self.scope.values.iter() {
            match value {
                Value::Literal(text) => bindings.push(format!("{name} := {}", literal_text(text))),
                Value::Process(id) => bindings.push(format!("{name} => <program {}>", id.0)),
                Value::Port(id, port) => {
                    bindings.push(format!("{name} := <program {} {port:?}>", id.0))
                }
                Value::Path(path) => {
                    let qualifier = path.backend.as_ref().map(|backend| {
                        binding_qualifier(backend, &self.scope, &mut endpoints, &mut bindings)
                    });
                    bindings.push(format!(
                        "{name} := {}",
                        path_text(path, qualifier.as_deref())
                    ));
                }
                Value::Space(_) => {}
                Value::Bool(value) => bindings.push(format!("{name} = {value}")),
                Value::Dynamic(value) => {
                    bindings.push(format!("{name} = <runtime {:?}>", value.ty))
                }
                Value::Range(_) => bindings.push(format!("{name} := <range>")),
            }
        }
        bindings.extend(self.scope.macros.values().map(|definition| {
            let parameters = definition
                .parameters
                .iter()
                .map(|name| name.text.as_str())
                .collect::<Vec<_>>()
                .join(", ");
            format!("macro {}({parameters})", definition.name.text)
        }));
        bindings.extend(
            self.scope
                .symbols
                .values()
                .map(|definition| definition.signature()),
        );
        bindings
    }

    /// Executable objects export values and runtime symbols, not macro templates.
    pub fn object_exports(&self, program: &hir::Program) -> Vec<Export> {
        let mut processes = HashSet::new();
        local_processes(&program.body, &mut processes);
        self.scope
            .values
            .iter()
            .filter(|(name, _)| defined_here(&self.scope, name))
            .filter_map(|(name, value)| {
                let value = match value {
                    Value::Literal(text) => ExportValue::Literal {
                        text: text.to_string(),
                    },
                    Value::Path(path) => ExportValue::Path {
                        backend: path.backend.clone(),
                        path: path.path.clone(),
                        relocation: path_relocation(
                            path.backend.as_ref().unwrap_or(&Backend::Local),
                            &path.path,
                            captured_path(path, &self.scope).relocation,
                        ),
                        is_file: path.is_file,
                        mode: path.mode,
                    },
                    Value::Space(space) => ExportValue::Namespace {
                        namespace: namespace(space),
                    },
                    Value::Process(process) if processes.contains(process) => {
                        ExportValue::Process { process: *process }
                    }
                    Value::Port(process, port) if processes.contains(process) => {
                        ExportValue::Port {
                            process: *process,
                            port: match port {
                                Port::Stdin => None,
                                Port::Read(port) => Some(*port),
                            },
                        }
                    }
                    Value::Bool(value) => ExportValue::Bool { value: *value },
                    Value::Dynamic(expression) => ExportValue::Dynamic {
                        expression: expression.clone(),
                    },
                    Value::Range(range) => ExportValue::Range {
                        range: range.clone(),
                    },
                    Value::Process(_) | Value::Port(_, _) => return None,
                };
                Some(Export {
                    name: name.clone(),
                    value,
                })
            })
            .collect::<Vec<_>>()
    }

    pub fn object_export_sites(&self) -> BTreeMap<String, SourceSpan> {
        self.scope
            .sites
            .iter()
            .filter(|(name, _)| defined_here(&self.scope, name))
            .map(|(name, site)| {
                let declaration = self
                    .scope
                    .expansions
                    .get(name)
                    .and_then(ExpansionTrace::declaration)
                    .unwrap_or_else(|| site.clone());
                (name.clone(), declaration)
            })
            .collect()
    }

    pub fn object_id_bases(&self) -> usize {
        self.next_process
    }

    /// Install link-bound exports on a candidate environment before committing it.
    pub fn import_object_exports(
        &mut self,
        exports: &[Export],
        sites: &BTreeMap<String, SourceSpan>,
        program: &hir::Program,
    ) -> Result<(), Report> {
        let mut candidate = self.clone();
        let mut local = HashSet::new();
        local_processes(&program.body, &mut local);
        for export in exports {
            let site = sites.get(&export.name);
            let span = site.map_or(Span::default(), |site| site.span);
            check_name(&Name {
                text: export.name.clone(),
                span,
            })?;
            if matches!(&export.value, ExportValue::Process { process } | ExportValue::Port { process, .. } if !local.contains(process))
            {
                return Err(Report::new(
                    "program export has no matching module definition",
                    span,
                ));
            }
            let value = imported_value(&export.value);
            if let Some(site) = site {
                let trace = ExpansionTrace::new(
                    &export.name,
                    &value,
                    site.clone(),
                    ExpansionTrace::default(),
                    ExpansionOrigin::Imported,
                );
                Arc::make_mut(&mut candidate.scope.expansions).insert(export.name.clone(), trace);
            } else {
                Arc::make_mut(&mut candidate.scope.expansions).remove(&export.name);
            }
            candidate.scope.cells.remove(&export.name);
            Arc::make_mut(&mut candidate.scope.values).insert(export.name.clone(), value);
            Arc::make_mut(&mut candidate.scope.defined_units).remove(&export.name);
            if let Some(site) = site {
                Arc::make_mut(&mut candidate.scope.sites).insert(export.name.clone(), site.clone());
            } else {
                Arc::make_mut(&mut candidate.scope.sites).remove(&export.name);
            }
        }
        let mut processes = HashSet::new();
        local_processes(&program.body, &mut processes);
        for id in processes {
            candidate.next_process =
                candidate
                    .next_process
                    .max(id.0.checked_add(1).ok_or_else(|| {
                        Report::new("process identity space is exhausted", Span::default())
                    })?);
        }
        candidate.refresh_legacy();
        *self = candidate;
        Ok(())
    }

    /// Commit linear port ownership using the object's actual debug origins.
    pub fn import_object_consumptions(
        &mut self,
        program: &hir::Program,
        origins: &Origins,
    ) -> Result<(), Report> {
        let mut candidate = self.clone();
        import_consumptions(&mut candidate.consumed, &program.body, origins)?;
        *self = candidate;
        Ok(())
    }

    pub fn compile(
        &mut self,
        document: &Document,
        context: &CompileContext,
    ) -> Result<hir::Program, Vec<Report>> {
        self.compile_named(document, context, Source::new("<ast>", ""))
            .map(|lowered| lowered.program)
            .map_err(|reports| {
                reports
                    .into_iter()
                    .map(LocatedReport::into_report)
                    .collect()
            })
    }

    pub fn compile_named(
        &mut self,
        document: &Document,
        context: &CompileContext,
        source: Arc<Source>,
    ) -> Result<Lowered, Vec<LocatedReport>> {
        context
            .validate()
            .map_err(|report| vec![LocatedReport::new(source.clone(), report)])?;
        let mut candidate = self.clone();
        let mut scope = candidate.scope.clone();
        scope.symbol_budget = symbols::Budget::default();
        scope.current_unit = Some(source.id);
        scope.source = Some(source.clone());
        let mut lower = Lower {
            session: &mut candidate,
            program: hir::Program::default(),
            next_instruction: 0,
            selectors: HashSet::new(),
            expansion: Vec::new(),
            calls: Vec::new(),
            source,
            origins: Origins::default(),
            relocations: Vec::new(),
            pending: Default::default(),
            scripts: scripts::State::new(context.source_file.clone()),
            loops: Vec::new(),
        };
        let mut trace = crate::telemetry::span("compile.lower");
        let slots_before = scope.slots.load(std::sync::atomic::Ordering::Relaxed);
        let mut body = Region::default();
        let errors = lower.block(&document.statements, &mut scope, context, &mut body);
        trace.metric("instructions", lower.origins.len() as u64);
        trace.metric("instruction_ids", lower.next_instruction as u64);
        trace.metric(
            "runtime_slots",
            (scope.slots.load(std::sync::atomic::Ordering::Relaxed) - slots_before) as u64,
        );
        trace.metric(
            "function_specializations",
            scope.symbol_budget.used() as u64,
        );
        trace.metric("errors", errors.len() as u64);
        if !errors.is_empty() {
            trace.finish_result(&Err::<(), _>(&errors));
            return Err(errors);
        }
        trace.finish_result(&Ok::<(), ()>(()));
        drop(trace);
        for removed in hir::rewrite::normalize_fences(&mut body) {
            lower.origins.remove(&removed);
        }
        lower.program.body = body;
        symbols::complete_source_modules(
            &scope,
            context,
            &lower.relocations,
            &mut lower.scripts.modules,
        );
        let result = Lowered {
            program: lower.program,
            origins: lower.origins,
            relocations: lower.relocations,
            source_modules: lower.scripts.modules,
        };
        let values_changed = !Arc::ptr_eq(&candidate.scope.values, &scope.values);
        candidate.scope = scope;
        if values_changed {
            candidate.refresh_legacy();
        }
        *self = candidate;
        Ok(result)
    }

    fn refresh_legacy(&mut self) {
        let mut legacy = Compiler::new();
        for (name, value) in self.scope.values.iter() {
            match value {
                Value::Path(path) => {
                    Arc::make_mut(&mut legacy.aliases).insert(name.clone(), path.legacy());
                }
                Value::Space(space) => {
                    Arc::make_mut(&mut legacy.spaces).insert(name.clone(), space.clone());
                }
                _ => {}
            }
        }
        self.legacy = legacy;
    }
}

fn value_expression(statement: &language::Statement) -> &Expr {
    match &statement.kind {
        language::StatementKind::Binding { value, .. } => value,
        _ => unreachable!("program binding pattern"),
    }
}

fn local_processes(region: &Region, processes: &mut HashSet<ProcessId>) {
    let mut nodes = Vec::new();
    crate::object::control::region_nodes(region, &mut nodes);
    for node in nodes {
        match &node.kind {
            InstructionKind::Spawn { process, .. }
            | InstructionKind::Control(hir::control::ControlInstruction::Spawn {
                process, ..
            }) => {
                processes.insert(*process);
            }
            _ => {}
        }
    }
}

fn defined_here(scope: &Scope, name: &str) -> bool {
    scope
        .current_unit
        .is_some_and(|unit| scope.defined_units.get(name) == Some(&unit))
}

fn import_consumptions(
    consumed: &mut imbl::HashMap<(ProcessId, Port), SourceSpan>,
    region: &Region,
    origins: &Origins,
) -> Result<(), Report> {
    let mut nodes = Vec::new();
    crate::object::control::region_nodes(region, &mut nodes);
    let previous_consumed = consumed.clone();
    for node in nodes {
        let (source, destination) = match &node.kind {
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                let source = match source {
                    Input::Port {
                        process,
                        port: port @ (ReadPort::Stdout | ReadPort::Stderr),
                    } => Some(((*process, Port::Read(*port)), false)),
                    _ => None,
                };
                let destination = match destination {
                    Output::Stdin(process) => Some(((*process, Port::Stdin), true)),
                    _ => None,
                };
                (source, destination)
            }
            InstructionKind::Control(hir::control::ControlInstruction::Transfer {
                source,
                destination,
            }) => {
                let source = match source {
                    hir::control::TransferInput::Port {
                        process,
                        port: port @ (ReadPort::Stdout | ReadPort::Stderr),
                    } => Some(((*process, Port::Read(*port)), false)),
                    _ => None,
                };
                let destination = match destination {
                    hir::control::TransferOutput::Stdin(process) => {
                        Some(((*process, Port::Stdin), true))
                    }
                    _ => None,
                };
                (source, destination)
            }
            _ => continue,
        };
        for (key, destination) in source.into_iter().chain(destination) {
            let origin = origins.get(&node.id).ok_or_else(|| {
                Report::new("object port connection has no debug origin", node.span)
            })?;
            let site = origin.for_operand(destination).site;
            if let Some(previous) = previous_consumed.get(&key) {
                return Err(Report::new(
                    "a live program stream may have only one owner",
                    site.span,
                )
                .help(format!(
                    "This stream was already connected in {} at byte {}.",
                    previous.source.name, previous.span.start
                )));
            }
            consumed.insert(key, site);
        }
    }
    Ok(())
}

fn namespace(space: &Space) -> Namespace {
    match space {
        Space::Pwd => Namespace::WorkingDirectory,
        Space::File => Namespace::Module,
        Space::Endpoint(backend) => Namespace::Endpoint {
            backend: backend.clone(),
        },
    }
}

fn imported_value(value: &ExportValue) -> Value {
    match value {
        ExportValue::Bool { value } => Value::Bool(*value),
        ExportValue::Dynamic { expression } => Value::Dynamic(expression.clone()),
        ExportValue::Range { range } => Value::Range(range.clone()),
        ExportValue::Literal { text } => Value::Literal(Arc::from(text.as_str())),
        ExportValue::Path {
            backend,
            path,
            relocation,
            is_file,
            mode,
        } => Value::Path(PathValue {
            backend: backend.clone(),
            path: path.clone(),
            relocation: relocation.clone(),
            relocation_unit: None,
            is_file: *is_file,
            mode: *mode,
        }),
        ExportValue::Namespace { namespace } => Value::Space(match namespace {
            Namespace::WorkingDirectory => Space::Pwd,
            Namespace::Module => Space::File,
            Namespace::Endpoint { backend } => Space::Endpoint(backend.clone()),
        }),
        ExportValue::Process { process } => Value::Process(*process),
        ExportValue::Port { process, port } => {
            Value::Port(*process, port.map_or(Port::Stdin, Port::Read))
        }
    }
}

struct Lower<'a> {
    session: &'a mut Session,
    program: hir::Program,
    next_instruction: usize,
    selectors: HashSet<String>,
    expansion: Vec<Span>,
    calls: Vec<(Arc<Macro>, SourceSpan)>,
    source: Arc<Source>,
    origins: Origins,
    relocations: Vec<Relocation>,
    pending: BTreeSet<ProcessId>,
    scripts: scripts::State,
    loops: Vec<v6::LoopFrame>,
}

impl Lower<'_> {
    fn block(
        &mut self,
        statements: &[language::Statement],
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Vec<LocatedReport> {
        let mut errors = Vec::new();
        for statement in statements {
            if let Err(report) = self.statement(statement, scope, context, region) {
                errors.push(report);
            }
        }
        errors
    }

    fn statement(
        &mut self,
        statement: &language::Statement,
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        match &statement.kind {
            language::StatementKind::Scheduled {
                asynchronous,
                awaited,
                statement,
            } => self.scheduled(statement, *asynchronous, *awaited, scope, context, region),
            language::StatementKind::Async => {
                self.fence(region, statement.span, BarrierKind::Before);
                Ok(())
            }
            language::StatementKind::Await(target) => {
                let process = target.as_ref().map(|name| {
                    match scope.values.get(&name.text) {
                        Some(Value::Process(process)) => Ok(*process),
                        Some(_) => Err(self.located(Report::new(
                            format!("'{}' is not a program", name.text), name.span,
                        ).help("Await a program bound with 'name => executable(arguments)'."))),
                        None => Err(self.located(Report::new(
                            format!("unknown program '{}'", name.text), name.span,
                        ).help("Declare the program before acquiring it, or use bare 'acquire' to join all pending programs."))),
                    }
                }).transpose()?;
                self.join(region, statement.span, process);
                Ok(())
            }
            _ => self.scheduled(statement, false, false, scope, context, region),
        }
    }

    fn scheduled(
        &mut self,
        statement: &language::Statement,
        asynchronous: bool,
        awaited: bool,
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        let program_name = match &statement.kind {
            language::StatementKind::Binding {
                name,
                op: BindingOp::Space,
                value:
                    Expr {
                        kind: ExprKind::Call { .. },
                        ..
                    },
            } if !symbols::is_call(value_expression(statement), scope) => Some(name),
            _ => None,
        };
        if asynchronous || program_name.is_some() {
            self.fence(region, statement.span, BarrierKind::Before);
        }
        self.operation(statement, scope, context, region)?;
        if let Some(name) = program_name {
            if !asynchronous || awaited {
                let Value::Process(process) = scope.values[&name.text] else {
                    unreachable!("a successful program binding has a process identity")
                };
                self.join(region, statement.span, Some(process));
            }
        } else if awaited {
            self.fence(region, statement.span, BarrierKind::After);
        }
        Ok(())
    }

    fn operation(
        &mut self,
        statement: &language::Statement,
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        use language::StatementKind;
        match &statement.kind {
            StatementKind::Call(call) if symbols::is_call(call, scope) => {
                diagnostics::resolve(call, scope, context)
                    .map(|_| ())
                    .map_err(|error| self.located(error))
            }
            StatementKind::Call(call) => self.expand_macro(call, scope, context, region),
            StatementKind::Transfer {
                source,
                destination,
            } => self.transfer(source, destination, scope, context, statement.span, region),
            StatementKind::Comment(_) => Ok(()),
            StatementKind::Binding { name, op, value } => self
                .bind(name, *op, value, scope, context, region)
                .map_err(|failure| self.located(failure.with_expression(value, scope))),
            StatementKind::Macro {
                name,
                parameters,
                body,
            } => self
                .define_macro(name, parameters, body, scope, context)
                .map_err(|report| self.located(report)),
            StatementKind::Symbol {
                name,
                parameters,
                body,
            } => self.define_symbol(name, parameters, body, scope, context),
            StatementKind::Boolean {
                name,
                parameters,
                body,
            } => self.define_boolean(name, parameters, body, scope, context),
            StatementKind::Delete(_)
            | StatementKind::Drop(_)
            | StatementKind::If { .. }
            | StatementKind::For { .. }
            | StatementKind::Loop { .. }
            | StatementKind::Break(_) => self.control(statement, scope, context, region),
            StatementKind::Return(value) => Err(self.located(Report::new(
                "return is only allowed inside a symbol",
                value.span,
            ))),
            StatementKind::Execute(expression) => {
                self.script(expression, false, scope, context, region)
            }
            StatementKind::Include(expression) => {
                self.script(expression, true, scope, context, region)
            }
            StatementKind::Global(names) => {
                import_globals(names, scope).map_err(|report| self.located(report))
            }
            StatementKind::Scheduled { .. } | StatementKind::Async | StatementKind::Await(_) => {
                self.statement(statement, scope, context, region)
            }
        }
    }

    fn fence(&mut self, region: &mut Region, span: Span, kind: BarrierKind) {
        self.emit(region, span, InstructionKind::Barrier { kind });
    }

    fn join(&mut self, region: &mut Region, span: Span, process: Option<ProcessId>) {
        self.emit(region, span, InstructionKind::Await { process });
        if let Some(process) = process {
            self.pending.remove(&process);
        } else {
            self.pending.clear();
        }
    }

    fn located(&self, report: impl Into<Failure>) -> LocatedReport {
        report.into().locate(self.source.clone())
    }
    fn bind(
        &mut self,
        name: &Name,
        op: BindingOp,
        expression: &Expr,
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<(), Failure> {
        check_name(name)?;
        let span = Span {
            start: name.span.start,
            end: expression.span.end,
        };
        if self.bind_control(name, op, expression, scope, context, region)? {
            return Ok(());
        }
        scope.cells.remove(&name.text);
        let mut value = match (&expression.kind, op) {
            (ExprKind::Call { .. }, _) if symbols::is_call(expression, scope) => {
                let value = diagnostics::resolve(expression, scope, context)?;
                if op == BindingOp::Space && !matches!(value, Value::Space(_) | Value::Process(_)) {
                    return Err(Report::new("'=>' expects a space or program returned by the symbol; use ':=' for an item", expression.span).into());
                }
                value
            }

            (ExprKind::Call { callee, arguments }, BindingOp::Space) => self.spawn_call(callee, arguments, scope, context, region, span)?,
            (ExprKind::Call { .. }, BindingOp::Value | BindingOp::Assign) => {
                return Err((Report::new("a program call binds a program space with '=>'", expression.span)
                    .help("Use 'program => executable(arguments)' and read program::stdout or program::exit_code.")).into());
            }
            (_, BindingOp::Space) => match diagnostics::resolve(expression, scope, context)? {
                Value::Space(space) => Value::Space(space),
                _ => return Err((Report::new("'=>' expects an endpoint namespace or a program call", expression.span)
                    .help("Use ':=' for path values, or '=>' with $local, $wsl, an endpoint URL or executable(arguments).")).into()),
            },
            (_, BindingOp::Value | BindingOp::Assign) => diagnostics::resolve(expression, scope, context)?,
        };
        v6::check_assignment(op, &value, expression.span)?;
        if let Value::Path(path) = &mut value
            && contextual(path.relocation.as_ref())
            && path.relocation_unit.is_none()
        {
            path.relocation_unit = scope.current_unit;
        }
        let trace = ExpansionTrace::new(
            &name.text,
            &value,
            self.source.at(name.span),
            ExpansionTrace::inputs(expression, scope),
            ExpansionOrigin::Item,
        );
        Arc::make_mut(&mut scope.expansions).insert(name.text.clone(), trace);
        let site = self.expression_site(expression, scope);
        Arc::make_mut(&mut scope.sites).insert(name.text.clone(), site);
        if let Some(unit) = scope.current_unit {
            Arc::make_mut(&mut scope.defined_units).insert(name.text.clone(), unit);
        }
        Arc::make_mut(&mut scope.values).insert(name.text.clone(), value);
        Ok(())
    }

    fn transfer(
        &mut self,
        source: &Expr,
        destination: &Expr,
        scope: &mut Scope,
        context: &CompileContext,
        span: Span,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        let values = self
            .transfer_control(source, destination, scope, context, span, region)
            .map_err(|failure| {
                self.located(
                    failure
                        .with_expression(source, scope)
                        .with_expression(destination, scope),
                )
            })?;
        let Some(values) = values else {
            return Ok(());
        };
        let operands = (
            self.expression_site(source, scope),
            self.expression_site(destination, scope),
        );
        let TransferValues {
            source,
            destination,
            owned,
            relocations,
        } = self
            .transfer_values(source, destination, values, context)
            .map_err(|failure| {
                self.located(
                    failure
                        .with_expression(source, scope)
                        .with_expression(destination, scope),
                )
            })?;
        for (key, site) in &owned {
            if let Some(previous) = self.session.consumed.get(key) {
                return Err(self.located(Report::new("a live program stream may have only one owner", *site)
                    .help("Write the stream to a file once, then read that replayable file from multiple consumers."))
                    .label(previous.clone(), "this stream was already connected here"));
            }
        }
        self.session.consumed.extend(
            owned
                .into_iter()
                .map(|(key, span)| (key, self.source.at(span))),
        );
        let id = self.emit(
            region,
            span,
            InstructionKind::Transfer {
                source,
                destination,
            },
        );
        self.origins.get_mut(&id).expect("emitted origin").operands = Some(operands);
        self.relocations
            .extend(relocations.into_iter().map(|(slot, path)| Relocation {
                instruction: id,
                slot,
                path,
            }));
        Ok(())
    }

    fn transfer_values(
        &mut self,
        source: &Expr,
        destination: &Expr,
        values: (Value, Value),
        context: &CompileContext,
    ) -> Result<TransferValues, Failure> {
        let (source_value, destination_value) = values;
        let mut owned = Vec::new();
        let mut relocations = Vec::new();
        let source = match source_value {
            Value::Literal(text) => Input::Literal(text),
            Value::Path(mut path) => {
                let length = path.path.len();
                let pattern = super::take_selector(&mut path.path, &mut self.selectors).map_err(|message| {
                    Report::new(message, selector_site(source)).help("Use one final '/<regex>' component; selectors match complete entry names.")
                })?;
                if let Some(relocation) = &mut path.relocation {
                    relocation.suffix.truncate(
                        relocation
                            .suffix
                            .len()
                            .saturating_sub(length - path.path.len()),
                    );
                }
                if pattern.is_some()
                    && matches!(path.backend, Some(Backend::Http { .. } | Backend::Path))
                {
                    return Err((Report::new(
                        "selectors require a directory-capable source",
                        source.span,
                    )
                    .help(
                        "Select from a local, WSL or SSH directory, not an HTTP or PATH endpoint.",
                    ))
                    .into());
                }
                let (location, relocation) = location(path, context, source.span)?;
                if let Some(path) = relocation {
                    relocations.push((Slot::Source, path));
                }
                Input::File { location, pattern }
            }
            Value::Port(process, Port::Read(port)) => {
                if matches!(port, ReadPort::Stdout | ReadPort::Stderr) {
                    owned.push(((process, Port::Read(port)), source.span));
                }
                Input::Port { process, port }
            }
            _ => {
                return Err(
                    (Report::new("this value is not a readable source", source.span).help(
                        "Use a file, quoted UTF-8 literal, stdout/stderr, pid, exit_code or meta.",
                    ))
                    .into(),
                );
            }
        };
        let mode = expression_mode(destination);
        let destination = match destination_value {
            Value::Path(mut path) => {
                let mode = if mode == DestinationMode::Exact { path.mode } else { mode };
                if matches!(path.backend, Some(Backend::Http { .. } | Backend::Path)) {
                    return Err((Report::new("HTTP(S) and PATH endpoints are read-only", qualifier_site(destination))).into());
                }
                if path.path.contains(['<', '>']) {
                    return Err((Report::new("selectors are only allowed in the source", selector_site(destination))).into());
                }
                if mode == DestinationMode::ReplaceTree {
                    if path.path.ends_with("/.") || path.path.ends_with("\\.") {
                        path.path.pop();
                        if let Some(relocation) = &mut path.relocation { relocation.suffix.pop(); }
                    }
                    reject_root_replacement(&path, destination.span)?;
                } else if mode == DestinationMode::Exact && !matches!(path.backend, Some(Backend::Tcp { .. })) {
                    reject_root_replacement(&path, destination.span)?;
                }
                if matches!(&source, Input::File { pattern: Some(_), .. }) && mode != DestinationMode::Container {
                    return Err((Report::new("a selector requires a destination container ending in '/'", destination.span)
                        .help("Append '/' to preserve each selected entry's basename.")).into());
                }
                let (location, relocation) = location(path, context, destination.span)?;
                if let Some(path) = relocation { relocations.push((Slot::Destination, path)); }
                Output::File { location, mode }
            }
            Value::Port(process, Port::Stdin) => {
                owned.push(((process, Port::Stdin), destination.span));
                Output::Stdin(process)
            }
            _ => return Err((Report::new("this value is not a writable destination", destination.span)
                .help("Use a writable path such as './\"output name\"' or '$pwd::pwd / \"output name\"', or program::stdin. Bare quoted expressions and output streams are read-only.")).into()),
        };
        Ok(TransferValues {
            source,
            destination,
            owned,
            relocations,
        })
    }

    fn define_macro(
        &self,
        name: &Name,
        parameters: &[Name],
        body: &[language::Statement],
        scope: &mut Scope,
        context: &CompileContext,
    ) -> Result<(), Report> {
        check_name(name)?;
        let mut names = HashSet::new();
        for parameter in parameters {
            check_name(parameter)?;
            if !names.insert(parameter.text.as_str()) {
                return Err(Report::new(
                    format!("duplicate macro parameter '{}'", parameter.text),
                    parameter.span,
                ));
            }
        }

        let definition = Macro {
            name: name.clone(),
            parameters: parameters.to_vec(),
            body: body.to_vec(),
            globals: v6::visible_values(scope),
            global_sites: v6::visible_sites(scope),
            global_expansions: v6::visible_expansions(scope),
            macros: scope.macros.clone(),
            symbols: scope.symbols.clone(),
            source_file: context.source_file.clone(),
            source: self.source.clone(),
        };
        Arc::make_mut(&mut scope.macros).insert(name.text.clone(), Arc::new(definition));
        scope.local_templates.insert(name.text.clone());
        Ok(())
    }

    fn expand_macro(
        &mut self,
        call: &Expr,
        caller: &Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        let ExprKind::Call { callee, arguments } = &call.kind else {
            return Err(self.located(Report::new("expected a macro call", call.span)));
        };
        let ExprKind::Path(path) = &callee.kind else {
            return Err(self.located(Report::new(
                "a macro call needs an unqualified macro name",
                callee.span,
            )));
        };
        if path.qualifier.is_some() || path.path.contains(['/', '\\']) {
            return Err(self.located(
                Report::new(
                    "only declared macros can be called as statements",
                    callee.span,
                )
                .help("Bind an external program with 'program => executable(arguments)' instead."),
            ));
        }
        let definition = match caller.macros.get(&path.path).cloned() {
            Some(definition) => definition,
            None => {
                if let Some((_, previous)) = self
                    .calls
                    .iter()
                    .find(|(definition, _)| definition.name.text == path.path)
                {
                    return Err(self.recursion(callee.span, previous.clone()));
                }
                return Err(self.located(Report::new(format!("unknown macro '{}'", path.path), callee.span)
                    .help("Declare the macro before calling it. A macro body can call only macros declared before its definition.")));
            }
        };
        if let Some((_, previous)) = self
            .calls
            .iter()
            .find(|(active, _)| Arc::ptr_eq(active, &definition))
        {
            return Err(self.recursion(callee.span, previous.clone()));
        }
        if arguments.len() != definition.parameters.len() {
            return Err(self
                .located(Report::new(
                    format!(
                        "macro '{}' expects {} argument(s), got {}",
                        path.path,
                        definition.parameters.len(),
                        arguments.len()
                    ),
                    call.span,
                ))
                .label(
                    definition.source.at(definition.name.span),
                    "macro declared here",
                ));
        }
        if self.calls.len() >= 128 {
            return Err(self.located(Report::new("macro expansion nesting limit exceeded", call.span)
                .help("Reduce the nesting of macro calls; at most 128 active expansions are supported.")));
        }
        let values = definition
            .parameters
            .iter()
            .zip(arguments)
            .map(|(parameter, argument)| {
                Ok((
                    parameter.text.clone(),
                    diagnostics::resolve(argument, caller, context)?,
                ))
            })
            .collect::<Result<imbl::OrdMap<_, _>, Failure>>()
            .map_err(|report| self.located(report))?;
        let sites = definition
            .parameters
            .iter()
            .zip(arguments)
            .map(|(parameter, argument)| {
                (
                    parameter.text.clone(),
                    self.expression_site(argument, caller),
                )
            })
            .collect();
        let expansions = definition
            .parameters
            .iter()
            .zip(arguments)
            .map(|(parameter, argument)| {
                let trace = ExpansionTrace::new(
                    &parameter.text,
                    &values[&parameter.text],
                    self.source.at(argument.span),
                    ExpansionTrace::inputs(argument, caller),
                    ExpansionOrigin::Parameter,
                );
                (parameter.text.clone(), trace)
            })
            .collect();
        let mut scope = Scope {
            slots: caller.slots.clone(),
            cells: imbl::OrdMap::new(),
            current_unit: caller.current_unit,
            source: Some(definition.source.clone()),
            symbols: definition.symbols.clone(),
            symbol_calls: caller.symbol_calls.clone(),
            symbol_budget: caller.symbol_budget.clone(),
            defined_units: Arc::new(imbl::OrdMap::new()),
            values: Arc::new(values),
            macros: definition.macros.clone(),
            globals: Some(definition.globals.clone()),
            global_macros: Some(definition.macros.clone()),
            global_symbols: Some(definition.symbols.clone()),
            local_templates: imbl::OrdSet::new(),
            imported: imbl::OrdSet::new(),
            sites: Arc::new(sites),
            global_sites: Some(definition.global_sites.clone()),
            expansions: Arc::new(expansions),
            global_expansions: Some(definition.global_expansions.clone()),
        };
        let context = CompileContext {
            pwd: context.pwd.clone(),
            source_file: definition.source_file.clone(),
        };
        let call_site = self.source.at(call.span);
        self.calls.push((definition.clone(), call_site.clone()));
        self.expansion.push(definition.name.span);
        let caller_source = std::mem::replace(&mut self.source, definition.source.clone());
        let inherited = self.pending.clone();
        let errors = self.block(&definition.body, &mut scope, &context, region);
        if errors.is_empty() {
            let pending = self
                .pending
                .difference(&inherited)
                .copied()
                .collect::<Vec<_>>();
            for process in pending {
                self.join(region, definition.name.span, Some(process));
            }
        }
        self.source = caller_source;
        self.expansion.pop();
        self.calls.pop();
        errors.into_iter().next().map_or(Ok(()), |report| {
            Err(report
                .in_context(call_site.clone())
                .label(call_site, format!("while expanding macro '{}'", path.path)))
        })
    }

    fn recursion(&self, span: Span, previous: SourceSpan) -> LocatedReport {
        self.located(Report::new("recursive macro expansion is not allowed", span)
            .help("Use a finite sequence of macro calls; macros are compiler-time inlining, not runtime recursion."))
            .label(previous, "this expansion is already active")
    }

    fn expression_site(&self, expression: &Expr, scope: &Scope) -> SourceSpan {
        if let ExprKind::Join { base, suffix, .. } = &expression.kind {
            let site = self.expression_site(base, scope);
            if site.source.id != self.source.id || site.span != base.span {
                return site;
            }
            if let Some(suffix) = suffix {
                return self.expression_site(suffix, scope);
            }
        }
        if let ExprKind::Path(path) = &expression.kind {
            let name = path.path.split(['/', '\\']).next().unwrap_or(&path.path);
            if let Some(site) = scope.sites.get(name) {
                return site.clone();
            }
        }
        self.source.at(expression.span)
    }

    fn emit(
        &mut self,
        region: &mut Region,
        span: Span,
        kind: InstructionKind,
    ) -> hir::InstructionId {
        let id = hir::InstructionId(self.next_instruction);
        self.next_instruction += 1;
        let definition = self.source.at(span);
        let site = self
            .calls
            .first()
            .map_or_else(|| definition.clone(), |(_, site)| site.clone());
        self.origins.insert(
            id,
            InstructionOrigin {
                site: site.clone(),
                definition,
                expansion: self
                    .calls
                    .iter()
                    .map(|(definition, _)| definition.source.at(definition.name.span))
                    .collect(),
                operands: None,
            },
        );
        region.instructions.push(Instruction {
            id,
            kind,
            span: site.span,
            expansion: self.expansion.clone(),
        });
        id
    }
}

fn import_globals(names: &[Name], scope: &mut Scope) -> Result<(), Report> {
    let globals = scope.globals.as_ref().ok_or_else(|| {
        Report::new(
            "global is only meaningful inside a macro or symbol",
            names.first().map_or(Span::default(), |name| name.span),
        )
        .help("Top-level bindings already share one lexical environment.")
    })?;
    for name in names {
        check_name(name)?;
        if scope.values.contains_key(&name.text) && !scope.imported.contains(&name.text) {
            return Err(Report::new(
                format!(
                    "global '{}' conflicts with a parameter or local binding",
                    name.text
                ),
                name.span,
            )
            .help(
                "Use another local name; an explicit global import cannot overwrite a parameter.",
            ));
        }
        let value = globals.get(&name.text).cloned();
        if value.is_none() {
            if scope.local_templates.contains(&name.text) && !scope.imported.contains(&name.text) {
                return Err(Report::new(
                    format!("global '{}' conflicts with a local template", name.text),
                    name.span,
                ));
            }
            if let Some(definition) = scope
                .global_macros
                .as_ref()
                .and_then(|definitions| definitions.get(&name.text))
                .cloned()
            {
                Arc::make_mut(&mut scope.macros).insert(name.text.clone(), definition);
                scope.imported.insert(name.text.clone());
                continue;
            }
            if let Some(definition) = scope
                .global_symbols
                .as_ref()
                .and_then(|definitions| definitions.get(&name.text))
                .cloned()
            {
                Arc::make_mut(&mut scope.symbols).insert(name.text.clone(), definition);
                scope.imported.insert(name.text.clone());
                continue;
            }
            return Err(Report::new(format!("global '{}' was not declared before this definition", name.text), name.span)
                .help("Declare the binding before the macro or pure function definition, or pass it as a parameter."));
        }
        let value = value.expect("checked captured value");
        Arc::make_mut(&mut scope.values).insert(name.text.clone(), value);
        if let Some(site) = scope
            .global_sites
            .as_ref()
            .and_then(|sites| sites.get(&name.text))
        {
            Arc::make_mut(&mut scope.sites).insert(name.text.clone(), site.clone());
        }
        if let Some(trace) = scope
            .global_expansions
            .as_ref()
            .and_then(|traces| traces.get(&name.text))
        {
            Arc::make_mut(&mut scope.expansions).insert(name.text.clone(), trace.clone());
        } else {
            Arc::make_mut(&mut scope.expansions).remove(&name.text);
        }
        scope.imported.insert(name.text.clone());
    }
    Ok(())
}

fn resolve(expression: &Expr, scope: &Scope, context: &CompileContext) -> Result<Value, Failure> {
    let value = resolve_value(expression, scope, context)?;
    let value = match value {
        Value::Path(path) if path.path.contains('<') => {
            ranges::selector(path, scope, context, expression.span)?
        }
        value => value,
    };
    symbols::check_value(&value, expression.span, scope)?;
    Ok(value)
}

fn resolve_value(
    expression: &Expr,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Failure> {
    match &expression.kind {
        ExprKind::Literal(text) => Ok(Value::Literal(Arc::from(text.as_str()))),
        ExprKind::Path(path) => {
            let mut value =
                resolve_path(path, expression.span, &expression.source, scope, context)?;
            if path.mode != DestinationMode::Exact
                && let Value::Path(value) = &mut value
            {
                value.mode = path.mode;
            }
            Ok(value)
        }
        ExprKind::Join {
            base,
            suffix,
            operator,
            mode,
        } => resolve_join(base, suffix.as_deref(), *operator, *mode, scope, context),
        ExprKind::Select {
            namespace,
            item,
            operator,
        } => resolve_select(namespace, item, *operator, scope, context),
        ExprKind::Call { .. } => ranges::resolve_call(expression, scope, context)
            .unwrap_or_else(|| symbols::evaluate(expression, scope, context)),
        ExprKind::Bool(_)
        | ExprKind::Not(_)
        | ExprKind::Binary { .. }
        | ExprKind::Exists(_)
        | ExprKind::Loop { .. } => v6::resolve_expression(expression, scope, context),
    }
}

fn contextual(path: Option<&RelocPath>) -> bool {
    path.is_some_and(|path| {
        matches!(
            path.anchor,
            Anchor::WorkingDirectory | Anchor::ModuleFile { .. } | Anchor::ModuleDirectory { .. }
        )
    })
}

fn captured_path(path: &PathValue, scope: &Scope) -> PathValue {
    let mut path = path.clone();
    if contextual(path.relocation.as_ref())
        && path.relocation_unit.is_some()
        && path.relocation_unit != scope.current_unit
    {
        // A value captured by an earlier submission is already instantiated.
        // Only paths constructed in this unit remain symbolic for its object.
        path.relocation = None;
    }
    path
}

fn captured_value(value: &Value, scope: &Scope) -> Value {
    match value {
        Value::Path(path) => Value::Path(captured_path(path, scope)),
        value => value.clone(),
    }
}

fn resolve_join(
    base: &Expr,
    suffix: Option<&Expr>,
    operator: Span,
    mode: DestinationMode,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Failure> {
    let base_value = resolve_value(base, scope, context)?;
    let suffix_value = suffix
        .map(|expression| resolve_value(expression, scope, context))
        .transpose()?;
    if matches!(base_value, Value::Dynamic(_)) || matches!(suffix_value, Some(Value::Dynamic(_))) {
        return v6::join(base_value, suffix_value, mode, context, operator);
    }
    let suffix_site = suffix.map_or(operator, |expression| expression.source.content);
    let mut base_path = path_fragment(base_value, base.source.content)?;
    if base_path.is_file {
        return Err((Report::new(
            "a known file value cannot have child path components",
            operator,
        )
        .help("Join with its containing directory, not a known file value."))
        .into());
    }
    let Some(suffix_value) = suffix_value else {
        if !base_path.path.ends_with(['/', '\\']) {
            symbols::check_size(base_path.path.len().saturating_add(1), operator, scope)?;
            base_path.path.push('/');
            if let Some(relocation) = &mut base_path.relocation {
                relocation.append("/");
            }
        }
        base_path.mode = DestinationMode::Container;
        return Ok(Value::Path(base_path));
    };
    let suffix_path = path_fragment(suffix_value, suffix_site)?;
    if rooted_fragment(&suffix_path.path) {
        return Err((Report::new("a rooted right operand cannot replace a path-join base", operator)
            .label(base.source.content, format!("left operand expands to {}", diagnostics::path_description(&base_path)))
            .label(suffix_site, format!("right operand expands to {}; this is rooted", diagnostics::path_description(&suffix_path)))
            .help("'/' expands both operands before joining; its right path item must be relative. Use the rooted item directly instead of placing it after another path base.")).into());
    }
    if let (Some(left), Some(right)) = (&base_path.backend, &suffix_path.backend)
        && left != right
    {
        return Err(
            (Report::new("joined path operands have conflicting endpoints", operator)
                .label(
                    base.source.content,
                    format!(
                        "left operand expands to {}",
                        diagnostics::path_description(&base_path)
                    ),
                )
                .label(
                    suffix_site,
                    format!(
                        "right operand expands to {}",
                        diagnostics::path_description(&suffix_path)
                    ),
                ))
            .into(),
        );
    }
    let separator = usize::from(!base_path.path.ends_with(['/', '\\']));
    let projected = base_path
        .path
        .len()
        .saturating_add(separator)
        .saturating_add(suffix_path.path.len());
    symbols::check_size(projected, operator, scope)?;
    if base_path.backend.is_none() {
        base_path.backend = suffix_path.backend;
    }
    if !base_path.path.ends_with(['/', '\\']) {
        base_path.path.push('/');
        if let Some(relocation) = &mut base_path.relocation {
            relocation.append("/");
        }
    }
    base_path.path.push_str(&suffix_path.path);
    if let Some(relocation) = &mut base_path.relocation {
        relocation.append(&suffix_path.path);
    }
    base_path.is_file = suffix_path.is_file;
    base_path.mode = if mode == DestinationMode::Exact {
        suffix_path.mode
    } else {
        mode
    };
    Ok(Value::Path(base_path))
}

fn path_fragment(value: Value, span: Span) -> Result<PathValue, Report> {
    let path = match value {
        Value::Path(path) => path,
        Value::Literal(text) => PathValue {
            backend: None,
            path: text.to_string(),
            is_file: false,
            mode: DestinationMode::Exact,
            relocation: None,
            relocation_unit: None,
        },
        value => {
            return Err(Report::new(
                "a path fragment must be an item or string literal, not a space, program or stream",
                span,
            ).label(span, format!("this operand expands to {}", diagnostics::describe(&value)))
            .help("Select a space's item with '::' before composing a path, for example '$pwd::pwd / \"child\"'."));
        }
    };
    if path.path.is_empty()
        || language::path_boundary_whitespace(&path.path)
        || path.path.contains('\0')
    {
        return Err(Report::new("path components must be nonempty, NUL-free and have no boundary whitespace", span)
            .help("Internal spaces are valid. Use a bare quoted source for literal bytes; use '/' explicitly for paths."));
    }
    Ok(path)
}

fn rooted_fragment(path: &str) -> bool {
    absolute_fragment(path) || path == "~" || path.starts_with("~/")
}

fn absolute_fragment(path: &str) -> bool {
    path.starts_with(['/', '\\'])
        || (path.as_bytes().get(1) == Some(&b':')
            && path
                .as_bytes()
                .get(2)
                .is_some_and(|byte| matches!(*byte, b'/' | b'\\')))
}

fn resolve_path(
    path: &PathExpr,
    span: Span,
    site: &OperandSource,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Failure> {
    if path.path.is_empty() || path.path.contains('\0') {
        return Err((Report::new("paths must be nonempty and cannot contain NUL", span)).into());
    }
    let quoted = site.content.start > site.span.start;
    if path.qualifier.is_none()
        && !quoted
        && let Some(value) = scope.values.get(&path.path)
    {
        return Ok(captured_value(value, scope));
    }
    let qualifier = match &path.qualifier {
        Some(name) => match scope.values.get(&name.text) {
            Some(Value::Process(process)) => {
                return process_port(*process, &path.path, site.content).map_err(Into::into);
            }
            Some(Value::Space(space)) => Some(space.clone()),
            Some(Value::Dynamic(value)) if value.ty == hir::control::Type::Space => {
                return v6::select(
                    Value::Dynamic(value.clone()),
                    Value::Literal(Arc::from(path.path.as_str())),
                    context,
                    span,
                );
            }
            Some(_) => return Err((Report::new(
                format!("'{}' is a value, not a namespace or program", name.text),
                name.span,
            )
            .help(
                "Append path components with '/', or qualify with a namespace declared using '=>'.",
            ))
            .into()),
            None => Some(Compiler::new().space(&name.text, name.span)?),
        },
        None => None,
    };
    resolve_namespace(path, span, site, scope, context, qualifier)
}

fn resolve_select(
    namespace: &Expr,
    item: &Expr,
    operator: Span,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Failure> {
    let value = resolve(namespace, scope, context)?;
    if matches!(value, Value::Dynamic(_)) {
        let item = match &item.kind {
            ExprKind::Path(path)
                if path.qualifier.is_none() && !scope.values.contains_key(&path.path) =>
            {
                Value::Literal(Arc::from(path.path.as_str()))
            }
            _ => resolve(item, scope, context)?,
        };
        return v6::select(value, item, context, operator);
    }
    if !matches!(value, Value::Space(_) | Value::Process(_)) {
        return Err(Report::new(
            "the left operand of '::' must be a space or program",
            operator,
        )
        .label(
            namespace.span,
            format!("left operand expands to {}", diagnostics::describe(&value)),
        )
        .into());
    }
    let path = match &item.kind {
        ExprKind::Path(path) if path.qualifier.is_none() => Some(path.clone()),
        ExprKind::Literal(text) => Some(PathExpr {
            qualifier: None,
            path: text.clone(),
            mode: DestinationMode::Exact,
        }),
        _ => None,
    };
    if let Some(path) = path {
        return select_named(namespace, item, path, value, scope, context);
    }
    let fragment = path_fragment(resolve(item, scope, context)?, item.span)?;
    match value {
        Value::Space(Space::Endpoint(backend)) => qualify(Value::Path(fragment), backend, operator),
        Value::Space(Space::Pwd)
            if fragment.backend.is_some()
                || fragment.path.split(['/', '\\']).next() != Some("pwd") =>
        {
            contextualize_pwd_value(Value::Path(fragment), item.span, context)
        }
        Value::Space(_) | Value::Process(_) => {
            if fragment.backend.is_some() {
                return Err(Report::new(
                    "a named namespace member must not carry a physical endpoint",
                    item.span,
                )
                .into());
            }
            let path = PathExpr {
                qualifier: None,
                path: fragment.path,
                mode: fragment.mode,
            };
            select_named(namespace, item, path, value, scope, context)
        }
        _ => unreachable!("validated namespace type"),
    }
}

fn select_named(
    namespace: &Expr,
    item: &Expr,
    mut path: PathExpr,
    value: Value,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Failure> {
    if let Value::Process(process) = value {
        return process_port(process, &path.path, item.span).map_err(Into::into);
    }
    let Value::Space(space) = value else {
        unreachable!("validated namespace type");
    };
    let text = scope
        .source
        .as_ref()
        .and_then(|source| source.text.get(namespace.span.start..namespace.span.end))
        .unwrap_or("symbol result");
    path.qualifier = Some(Name {
        text: text.to_owned(),
        span: namespace.span,
    });
    resolve_namespace(
        &path,
        Span {
            start: namespace.span.start,
            end: item.span.end,
        },
        &item.source,
        scope,
        context,
        Some(space),
    )
}

fn resolve_namespace(
    path: &PathExpr,
    span: Span,
    site: &OperandSource,
    scope: &Scope,
    context: &CompileContext,
    qualifier: Option<Space>,
) -> Result<Value, Failure> {
    let quoted = site.content.start > site.span.start;
    match qualifier {
        Some(Space::Pwd) => resolve_pwd(path, span, site, scope, context),
        Some(Space::File) => {
            let name = path.qualifier.as_ref().expect("qualified file namespace");
            let text = format!("{}::{}", name.text, path.path);
            Compiler::namespace_item(
                Space::File,
                &path.path,
                &text,
                &qualified_source(name, &path.path, site, span),
                context,
            )
            .map(|value| {
                let mut value = PathValue::from(value);
                let item = path.path.split(['/', '\\']).next().unwrap_or(&path.path);
                let module = context
                    .source_file
                    .as_ref()
                    .expect("validated file context")
                    .to_string_lossy()
                    .into_owned();
                let anchor = if item == "this" {
                    Anchor::ModuleFile { module }
                } else {
                    Anchor::ModuleDirectory { module }
                };
                value.relocation = Some(RelocPath::new(
                    anchor,
                    path.path[item.len()..].trim_start_matches(['/', '\\']),
                ));
                Value::Path(value)
            })
            .map_err(Into::into)
        }
        Some(Space::Endpoint(backend)) => qualify(
            if quoted {
                Value::Path(PathValue {
                    backend: None,
                    path: path.path.clone(),
                    is_file: false,
                    mode: DestinationMode::Exact,
                    relocation: None,
                    relocation_unit: None,
                })
            } else {
                resolve_component(&path.path, site.content, scope, context)?
            },
            backend,
            span,
        ),
        None if quoted => Ok(Value::Path(PathValue {
            backend: None,
            path: path.path.clone(),
            is_file: false,
            mode: DestinationMode::Exact,
            relocation: None,
            relocation_unit: None,
        })),
        None => resolve_component(&path.path, site.content, scope, context),
    }
}

fn resolve_component(
    path: &str,
    span: Span,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Failure> {
    let name = path.split(['/', '\\']).next().unwrap_or(path);
    let suffix = &path[name.len()..];
    if let Some(value) = scope.values.get(name) {
        return match value {
            Value::Path(base) => {
                if base.is_file && !suffix.is_empty() {
                    return Err((Report::new(
                        "a known file value cannot have child path components",
                        span,
                    )
                    .help("Use the containing directory value to form child paths."))
                    .into());
                }
                let mut base = captured_path(base, scope);
                if !suffix.is_empty() {
                    base.path = format!("{}{suffix}", base.path.trim_end_matches(['/', '\\']));
                    if let Some(relocation) = &mut base.relocation {
                        relocation.suffix =
                            relocation.suffix.trim_end_matches(['/', '\\']).to_owned();
                        relocation.append(suffix);
                    }
                    base.mode = DestinationMode::Exact;
                }
                Ok(Value::Path(base))
            }
            _ if suffix.is_empty() => Ok(captured_value(value, scope)),
            _ => Err((Report::new(format!("'{name}' is not a path value"), span)
                .help("Only file/directory path values can have '/child' suffixes."))
            .into()),
        };
    }
    if path.starts_with('$') && !path.contains(['/', '\\']) || path.contains("://") {
        return Compiler::new()
            .space(path, span)
            .map(Value::Space)
            .map_err(Into::into);
    }
    Compiler::new()
        .expand(path, &OperandSource::unmapped(span), context)
        .map(|path| Value::Path(path.into()))
        .map_err(Into::into)
}

fn resolve_pwd(
    path: &PathExpr,
    span: Span,
    site: &OperandSource,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Failure> {
    let component = path.path.split(['/', '\\']).next().unwrap_or(&path.path);
    let quoted = site.content.start > site.span.start;
    if component == "pwd" && !quoted {
        let name = path.qualifier.as_ref().expect("qualified pwd namespace");
        let text = format!("{}::{}", name.text, path.path);
        return Compiler::namespace_item(
            Space::Pwd,
            &path.path,
            &text,
            &qualified_source(name, &path.path, site, span),
            context,
        )
        .map(|value| {
            let mut value = PathValue::from(value);
            value.relocation = Some(RelocPath::new(
                Anchor::WorkingDirectory,
                path.path[component.len()..].trim_start_matches(['/', '\\']),
            ));
            Value::Path(value)
        })
        .map_err(Into::into);
    }
    let value = if quoted {
        Value::Path(PathValue {
            backend: None,
            path: path.path.clone(),
            is_file: false,
            mode: DestinationMode::Exact,
            relocation: None,
            relocation_unit: None,
        })
    } else {
        resolve_component(&path.path, site.content, scope, context)?
    };
    contextualize_pwd_value(value, site.content, context)
}

fn contextualize_pwd_value(
    value: Value,
    span: Span,
    context: &CompileContext,
) -> Result<Value, Failure> {
    let mut value = path_fragment(value, span)?;
    if value
        .backend
        .as_ref()
        .is_some_and(|backend| backend != &Backend::Local)
    {
        return Err((Report::new("a remote path cannot be contextualized inside local $pwd", span)
            .help("Remove '$pwd::' to retain the parameter's remote endpoint, or pass a relative path item.")).into());
    }
    if !absolute_fragment(&value.path) {
        let cwd = context
            .pwd
            .to_str()
            .ok_or_else(|| Report::new("working directory is not a UTF-8 path", span))?;
        value.relocation = Some(RelocPath::new(Anchor::WorkingDirectory, value.path.clone()));
        value.path = format!("{}/{}", cwd.trim_end_matches(['/', '\\']), value.path);
    }
    value.backend = Some(Backend::Local);
    Ok(Value::Path(value))
}

fn qualified_source(
    name: &Name,
    decoded: &str,
    suffix: &OperandSource,
    span: Span,
) -> OperandSource {
    let mut offsets = (name.span.start..name.span.end + 2).collect::<Vec<_>>();
    offsets.extend((0..=decoded.len()).map(|index| suffix.interval(index, index).start));
    OperandSource::mapped(span, span, offsets)
}

fn qualify(value: Value, backend: Backend, span: Span) -> Result<Value, Failure> {
    let mut path = path_fragment(value, span)?;
    if path.backend.as_ref().is_some_and(|other| other != &backend) {
        return Err((Report::new("path value has a conflicting endpoint", span)
            .help("Remove the explicit qualifier to retain the value's original endpoint, or pass an unqualified path item.")).into());
    }
    if matches!(backend, Backend::Tcp { .. }) && path.path != "/" {
        return Err((super::tcp_path_report(span)).into());
    }
    path.backend = Some(backend);
    Ok(Value::Path(path))
}

fn process_port(process: ProcessId, path: &str, span: Span) -> Result<Value, Report> {
    let port = match path {
        "stdin" => Port::Stdin,
        "stdout" => Port::Read(ReadPort::Stdout),
        "stderr" => Port::Read(ReadPort::Stderr),
        "exit_code" => Port::Read(ReadPort::ExitCode),
        "pid" => Port::Read(ReadPort::Pid),
        "meta" => Port::Read(ReadPort::Meta),
        _ => return Err(Report::new(format!("unknown program item '{path}'"), span)
            .help("Program items are stdin, stdout, stderr, exit_code, pid and meta; they do not have child path components.")),
    };
    Ok(Value::Port(process, port))
}

fn location(
    path: PathValue,
    context: &CompileContext,
    span: Span,
) -> Result<(Location, Option<RelocPath>), Report> {
    let PathValue {
        backend,
        mut path,
        relocation,
        ..
    } = path;
    let backend = backend.unwrap_or(Backend::Local);
    let mut relocation = path_relocation(&backend, &path, relocation);
    if backend == Backend::Local && !rooted_fragment(&path) {
        let cwd = context
            .pwd
            .to_str()
            .ok_or_else(|| Report::new("working directory is not a UTF-8 path", span))?;
        if relocation.is_none() {
            relocation = Some(RelocPath::new(Anchor::WorkingDirectory, path.clone()));
        }
        path = format!("{}/{}", cwd.trim_end_matches(['/', '\\']), path);
    }
    Ok((Location { backend, path }, relocation))
}

fn path_relocation(
    backend: &Backend,
    path: &str,
    existing: Option<RelocPath>,
) -> Option<RelocPath> {
    if existing.is_some() {
        return existing;
    }
    let (head, tail) = path.split_once(['/', '\\']).unwrap_or((path, ""));
    match backend {
        Backend::Env => Some(RelocPath::new(
            Anchor::Environment {
                name: head.to_owned(),
            },
            tail,
        )),
        Backend::Path => Some(RelocPath::new(
            Anchor::SearchPath {
                name: path.to_owned(),
            },
            "",
        )),
        Backend::Local | Backend::Wsl | Backend::Ssh { .. }
            if path == "~" || path.starts_with("~/") =>
        {
            Some(RelocPath::new(Anchor::Home, tail))
        }
        _ => None,
    }
}

fn literal_text(text: &str) -> String {
    let mut output = String::new();
    language::write_literal(&mut output, text);
    output
}

fn binding_qualifier(
    backend: &Backend,
    scope: &Scope,
    endpoints: &mut HashMap<Backend, String>,
    bindings: &mut Vec<String>,
) -> String {
    if matches!(
        backend,
        Backend::Local | Backend::Wsl | Backend::Path | Backend::Env
    ) {
        return backend.to_string();
    }
    if let Some(name) = endpoints.get(backend) {
        return name.clone();
    }
    let mut index = endpoints.len();
    let name = loop {
        let candidate = format!("_msync_endpoint_{index}");
        index += 1;
        if !scope.values.contains_key(&candidate)
            && !endpoints.values().any(|name| *name == candidate)
        {
            break candidate;
        }
    };
    bindings.push(format!("{name} => {backend}"));
    endpoints.insert(backend.clone(), name.clone());
    name
}

fn path_text(path: &PathValue, qualifier: Option<&str>) -> String {
    let mut raw = path.path.as_str();
    match path.mode {
        DestinationMode::Container if !raw.chars().all(|ch| matches!(ch, '/' | '\\')) => {
            raw = raw.trim_end_matches(['/', '\\'])
        }
        DestinationMode::Container => {}
        DestinationMode::ReplaceTree => {
            raw = raw
                .strip_suffix("/.")
                .or_else(|| raw.strip_suffix("\\."))
                .unwrap_or(raw);
        }
        DestinationMode::Exact => {}
    }
    let mut output = if let Some(qualifier) = qualifier {
        format!(
            "{qualifier}::{}",
            literal_text(if raw.is_empty() { "/" } else { raw })
        )
    } else if raw.starts_with("//") || raw.starts_with('\\') {
        format!("{}/\".\"", literal_text(raw))
    } else if raw.starts_with('/') {
        let tail = raw.trim_start_matches('/');
        if tail.is_empty() {
            "\"/\"/\".\"".to_owned()
        } else {
            format!("\"/\"/{}", literal_text(tail))
        }
    } else if raw == "~" {
        "\"~\"/\".\"".to_owned()
    } else if let Some(tail) = raw.strip_prefix("~/") {
        format!("\"~\"/{}", literal_text(tail))
    } else if raw.as_bytes().get(1) == Some(&b':') && raw.as_bytes().get(2) == Some(&b'/') {
        format!("{}/{}", literal_text(&raw[..2]), literal_text(&raw[3..]))
    } else {
        format!("\".\"/{}", literal_text(raw))
    };
    match path.mode {
        DestinationMode::Container => output.push('/'),
        DestinationMode::ReplaceTree => output.push_str("/."),
        DestinationMode::Exact => {}
    }
    output
}

fn legacy_path_mode(path: &str) -> DestinationMode {
    if path.ends_with(['/', '\\']) {
        DestinationMode::Container
    } else {
        DestinationMode::Exact
    }
}

fn reject_root_replacement(path: &PathValue, span: Span) -> Result<(), Report> {
    let value = Path::new(&path.path);
    let native_root = matches!(path.backend, None | Some(Backend::Local))
        && value.has_root()
        && !value
            .components()
            .any(|component| matches!(component, Component::Normal(_) | Component::ParentDir));
    let endpoint_root = path.path.starts_with(['/', '\\'])
        && path
            .path
            .split(['/', '\\'])
            .all(|component| component.is_empty() || component == ".");
    if native_root || endpoint_root {
        return Err(Report::new("a filesystem root cannot be replaced", span)
            .help("Choose an explicit non-root directory before '/.'; root replacement is never redirected to the working directory."));
    }
    Ok(())
}

fn qualifier_site(expression: &Expr) -> Span {
    match &expression.kind {
        ExprKind::Path(path) => path
            .qualifier
            .as_ref()
            .map_or(expression.source.content, |name| name.span),
        _ => expression.span,
    }
}

fn selector_site(expression: &Expr) -> Span {
    if let ExprKind::Join {
        suffix: Some(suffix),
        ..
    } = &expression.kind
    {
        return selector_site(suffix);
    }
    if let ExprKind::Path(path) = &expression.kind
        && let Some(start) = path.path.rfind('<')
    {
        return expression
            .source
            .interval(start + 1, path.path.len().saturating_sub(1));
    }
    expression.source.content
}

fn expression_mode(expression: &Expr) -> DestinationMode {
    match &expression.kind {
        ExprKind::Path(path) => path.mode,
        ExprKind::Join { mode, .. } => *mode,
        _ => DestinationMode::Exact,
    }
}

fn check_name(name: &Name) -> Result<(), Report> {
    super::validate_name(&name.text).map_err(|message| {
        Report::new(message, name.span).help("Use letters, digits, '.', '_' or '-' for a binding name; built-ins begin with '$' and cannot be rebound.")
    })
}

#[cfg(test)]
mod performance_tests {
    use super::*;
    use proptest::prelude::*;

    fn context() -> CompileContext {
        CompileContext {
            pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
            source_file: None,
        }
    }

    #[test]
    fn static_transfer_specializes_each_function_operand_once() {
        let source = "symbol child(base):\n    return base / leaf\nchild(input) -> child(output)\n";
        let mut session = Session::new();
        let program = session
            .compile(&language::parse(source).unwrap(), &context())
            .unwrap();
        assert_eq!(session.scope.symbol_budget.used(), 2);
        assert_eq!(program.body.instructions.len(), 1);
        assert!(
            matches!(&program.body.instructions[0].kind, InstructionKind::Transfer { source: Input::File { location, .. }, .. } if location.path.ends_with("input/leaf"))
        );
    }

    #[test]
    fn transfer_only_submission_reuses_unchanged_legacy_tables() {
        let mut session = Session::new();
        let setup = "root := input\nsymbol child(base):\n    return base / leaf\n";
        session
            .compile(&language::parse(setup).unwrap(), &context())
            .unwrap();
        let aliases = session.legacy.aliases.clone();
        let spaces = session.legacy.spaces.clone();
        session
            .compile(
                &language::parse("child(root) -> output\n").unwrap(),
                &context(),
            )
            .unwrap();
        assert!(Arc::ptr_eq(&aliases, &session.legacy.aliases));
        assert!(Arc::ptr_eq(&spaces, &session.legacy.spaces));
    }

    #[test]
    fn lexical_capture_shares_root_maps_and_nearest_ancestors_shadow_without_mutation() {
        let mut session = Session::new();
        let code = "root := input\nmacro snapshot():\n    global root\n    root -> output\n";
        session
            .compile(&language::parse(code).unwrap(), &context())
            .unwrap();
        let definition = &session.scope.macros["snapshot"];
        assert!(Arc::ptr_eq(&definition.globals, &session.scope.values));
        assert!(Arc::ptr_eq(&definition.global_sites, &session.scope.sites));
        assert!(Arc::ptr_eq(
            &definition.global_expansions,
            &session.scope.expansions
        ));
        let grandparents = Arc::new(
            [("shared".to_owned(), 1usize), ("ancestor".to_owned(), 2)]
                .into_iter()
                .collect::<imbl::OrdMap<String, usize>>(),
        );
        let parents = Arc::new(
            [("shared".to_owned(), 3usize)]
                .into_iter()
                .collect::<imbl::OrdMap<String, usize>>(),
        );
        let captured = symbols::captured::<usize>(Some(&grandparents), &parents);
        assert_eq!(captured["shared"], 3usize);
        assert_eq!(captured["ancestor"], 2usize);
        assert_eq!(grandparents["shared"], 1usize);
    }

    proptest! {
        #[test]
        fn branch_checkpoints_keep_exactly_live_instruction_origins(prefix in 0usize..40, selected in any::<bool>()) {
            let mut text = (0..prefix).map(|index| format!("\"prefix\" -> out{index}\n")).collect::<String>();
            text.push_str(&format!("if exists guard:\n    if {selected}:\n        \"yes\" -> selected\n    else:\n        \"no\" -> selected\nelse:\n    if {}:\n        \"other\" -> selected\n    else:\n        \"fallback\" -> selected\n", !selected));
            let source = Source::new("checkpoints.msync", text);
            let document = language::parse(&source.text).unwrap();
            let lowered = Session::new().compile_named(&document, &context(), source).unwrap();
            let mut nodes = Vec::new();
            crate::object::control::region_nodes(&lowered.program.body, &mut nodes);
            let ids = nodes.into_iter().map(|node| node.id).collect::<BTreeSet<_>>();
            prop_assert_eq!(ids, lowered.origins.keys().copied().collect::<BTreeSet<_>>());
        }
    }
}
