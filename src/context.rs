//! Pure lexical instantiation of a validated typed module.
//!
//! This pass substitutes lexical context and fresh program identities only.
//! Filesystem observations and PATH searches belong to the interpreter's current
//! basic block, after the previous block has finished.

use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::fmt;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use crate::compiler::CompileContext;
use crate::compiler::structured::Session;
use crate::endpoint::{Backend, Location};
use crate::hir::graph::{self, PlanGraph};
use crate::hir::{
    self, Argument, Input, Instruction, InstructionId, InstructionKind, Output, ProcessId, Region,
};
use crate::object::ObjectModule;
use crate::relocatable::{Anchor, Export, ExportValue, Relocation, Slot};
use crate::source::{Origins, Source, SourceMap, SourceSpan};

/// Bindings for one module instantiation. Overrides are read only on demand.
#[derive(Clone, Debug)]
pub struct Context {
    pub pwd: PathBuf,
    pub module_file: Option<PathBuf>,
    pub home: Option<PathBuf>,
    pub environment: BTreeMap<String, String>,
    pub modules: BTreeMap<String, PathBuf>,
    pub endpoint_cwds: HashMap<Backend, String>,
}

impl Context {
    pub fn new(context: &CompileContext) -> Self {
        Self {
            pwd: context.pwd.clone(),
            module_file: context.source_file.clone(),
            home: None,
            environment: BTreeMap::new(),
            modules: BTreeMap::new(),
            endpoint_cwds: HashMap::new(),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Obligation {
    OpenEndpoint {
        instruction: InstructionId,
        backend: Backend,
    },
    SearchPath {
        instruction: InstructionId,
        name: String,
    },
    ForeignHome {
        instruction: InstructionId,
        backend: Backend,
    },
    InheritedEndpointCwd {
        instruction: InstructionId,
        backend: Backend,
    },
    ProgramLaunch {
        instruction: InstructionId,
        process: ProcessId,
        backend: Backend,
    },
}

/// Bound semantic IR and owned debug information, never a trusted runtime plan.
#[derive(Clone, Debug)]
pub struct ContextualizedModule {
    pub program: hir::Program,
    pub graph: PlanGraph,
    pub source: Arc<Source>,
    pub sources: SourceMap,
    pub origins: Origins,
    pub obligations: Vec<Obligation>,
}

#[derive(Clone, Debug)]
pub struct ContextError {
    pub message: String,
    pub help: Vec<String>,
    pub instruction: Option<InstructionId>,
    pub site: Option<SourceSpan>,
}

impl ContextError {
    fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
            help: Vec::new(),
            instruction: None,
            site: None,
        }
    }

    fn hint(mut self, hint: impl Into<String>) -> Self {
        self.help.push(hint.into());
        self
    }

    fn at(mut self, id: InstructionId, origins: &Origins) -> Self {
        self.instruction = Some(id);
        self.site = origins.get(&id).map(|origin| origin.site.clone());
        self
    }

    fn required_by(mut self, id: InstructionId) -> Self {
        self.instruction = Some(id);
        self
    }
}

impl fmt::Display for ContextError {
    fn fmt(&self, output: &mut fmt::Formatter<'_>) -> fmt::Result {
        output.write_str(&self.message)
    }
}

impl std::error::Error for ContextError {}

/// Validate, instantiate and atomically install the module's runtime exports.
pub fn contextualize(
    module: &ObjectModule,
    context: &Context,
    session: &mut Session,
) -> Result<ContextualizedModule, ContextError> {
    module
        .validate()
        .map_err(|cause| ContextError::new(format!("invalid object module: {cause}")))?;
    let context = source_module_context(module, context)?;
    validate_context(&context)?;
    let context = &context;
    let (source, sources, mut origins, export_sites) = module
        .debug
        .materialize()
        .map_err(|cause| ContextError::new(format!("invalid object debug information: {cause}")))?;
    let mut candidate = session.clone();
    let (mut program, mut obligations) = bind_program(
        &module.program,
        &module.relocations,
        &module.main_module,
        context,
        &origins,
    )?;
    for removed in hir::rewrite::normalize_fences(&mut program.body) {
        origins.remove(&removed);
    }
    let mut exports = module.exports.clone();
    bind_exports(&mut exports, &module.main_module, context)?;
    let maps = IdMaps::new(
        &program,
        &exports,
        candidate.object_id_bases(),
        candidate.runtime_slot_base(),
    )?;
    maps.program(&mut program)?;
    maps.exports(&mut exports)?;
    candidate.reserve_runtime_slots(maps.next_slot);
    collect_obligations(&program.body, &mut obligations);
    let graph = graph::build(&program);
    candidate
        .import_object_exports(&exports, &export_sites, &program)
        .map_err(|report| {
            let mut error = ContextError::new(report.diagnostic.message);
            error.help = report.help;
            error.site = export_sites
                .values()
                .find(|site| site.span == report.diagnostic.span)
                .cloned();
            error
        })?;
    candidate
        .import_object_consumptions(&program, &origins)
        .map_err(|report| {
            let mut error = ContextError::new(report.diagnostic.message);
            error.help = report.help;
            error
        })?;
    *session = candidate;
    Ok(ContextualizedModule {
        program,
        graph,
        source,
        sources,
        origins,
        obligations,
    })
}

/// Bind compiler-owned semantic IR without capturing or renaming live session
/// identities. Its candidate compiler session remains owned by the caller.
pub fn contextualize_source(
    unit: &crate::pipeline::RelocatableCompilation,
    context: &Context,
    planning: graph::PlanningContext,
) -> Result<ContextualizedModule, ContextError> {
    let mut context = context.clone();
    for (name, file) in &unit.module_bindings {
        context
            .modules
            .entry(name.clone())
            .or_insert_with(|| file.clone());
    }
    validate_context(&context)?;
    let compiled = &unit.compilation;
    let (program, mut obligations) = bind_program(
        &compiled.program,
        &unit.relocations,
        &unit.main_module,
        &context,
        &compiled.origins,
    )?;
    collect_obligations(&program.body, &mut obligations);
    let graph = graph::build_with_context(&program, &planning);
    Ok(ContextualizedModule {
        program,
        graph,
        source: compiled.source.clone(),
        sources: compiled.sources.clone(),
        origins: compiled.origins.clone(),
        obligations,
    })
}

fn source_module_context(
    module: &ObjectModule,
    context: &Context,
) -> Result<Context, ContextError> {
    use crate::object::SourceModulePath;
    let mut context = context.clone();
    let mut pending: Vec<_> = module.source_modules.iter().collect();
    while !pending.is_empty() {
        let before = pending.len();
        let mut remaining = Vec::new();
        for binding in pending {
            if context.modules.contains_key(&binding.name) {
                continue;
            }
            let file = match &binding.path {
                SourceModulePath::Absolute { path } => PathBuf::from(path),
                SourceModulePath::Contextual { path } => {
                    let parent = match &path.anchor {
                        Anchor::ModuleFile { module } | Anchor::ModuleDirectory { module } => {
                            Some(module)
                        }
                        _ => None,
                    };
                    if parent.is_some_and(|parent| {
                        parent != &module.main_module && !context.modules.contains_key(parent)
                    }) {
                        remaining.push(binding);
                        continue;
                    }
                    let location = bound_path(
                        path,
                        &Backend::Local,
                        &module.main_module,
                        &context,
                        None,
                        &mut Vec::new(),
                    )?;
                    PathBuf::from(location.path)
                }
            };
            context.modules.insert(binding.name.clone(), file);
        }
        if remaining.len() == before {
            return Err(ContextError::new("unresolved source-module dependency"));
        }
        pending = remaining;
    }
    Ok(context)
}

fn validate_context(context: &Context) -> Result<(), ContextError> {
    if !context.pwd.is_absolute() {
        return Err(ContextError::new(
            "the evaluation working directory must be absolute",
        ));
    }
    Ok(())
}

fn bind_program(
    program: &hir::Program,
    relocations: &[Relocation],
    main_module: &str,
    context: &Context,
    origins: &Origins,
) -> Result<(hir::Program, Vec<Obligation>), ContextError> {
    let mut program = program.clone();
    let mut obligations = Vec::new();
    let mut relocation_map = BTreeMap::<InstructionId, Vec<&Relocation>>::new();
    for relocation in relocations {
        relocation_map
            .entry(relocation.instruction)
            .or_default()
            .push(relocation);
    }
    let mut binder = ProgramBinder {
        relocations: relocation_map,
        main_module,
        context,
        origins,
        obligations: &mut obligations,
        instructions: Vec::new(),
    };
    crate::object::control::visit_region(&mut program.body, &mut binder)?;
    Ok((program, obligations))
}

struct ProgramBinder<'a> {
    relocations: BTreeMap<InstructionId, Vec<&'a Relocation>>,
    instructions: Vec<InstructionId>,
    main_module: &'a str,
    context: &'a Context,
    origins: &'a Origins,
    obligations: &'a mut Vec<Obligation>,
}

impl crate::object::control::Visitor for ProgramBinder<'_> {
    type Error = ContextError;
    fn instruction(&mut self, node: &mut Instruction) -> Result<(), ContextError> {
        let id = node.id;
        self.instructions.push(id);
        for relocation in self.relocations.get(&id).into_iter().flatten() {
            bind_relocation(
                node,
                relocation,
                self.main_module,
                self.context,
                self.obligations,
            )
            .map_err(|error| error.at(node.id, self.origins))?;
        }
        let id = node.id;
        match &mut node.kind {
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                if let Input::File { location, .. } = source {
                    bind_endpoint(location, self.context, id, self.obligations)?;
                }
                if let Output::File { location, .. } = destination {
                    bind_endpoint(location, self.context, id, self.obligations)?;
                }
            }
            InstructionKind::Spawn {
                executable,
                arguments,
                ..
            } => {
                bind_endpoint(executable, self.context, id, self.obligations)?;
                for argument in arguments {
                    if let Argument::Path(location) = argument {
                        bind_endpoint(location, self.context, id, self.obligations)?;
                    }
                }
            }
            _ => {}
        }
        Ok(())
    }
    fn leave_instruction(&mut self, _node: &mut Instruction) -> Result<(), ContextError> {
        self.instructions.pop();
        Ok(())
    }
    fn expression(
        &mut self,
        expression: &mut crate::hir::control::Expr,
    ) -> Result<(), ContextError> {
        use crate::hir::control::ExprKind;
        if let ExprKind::Space(namespace) = &expression.kind {
            expression.kind = ExprKind::ResolvedSpace {
                namespace: namespace.clone(),
                pwd: self.context.pwd.clone(),
                module_file: self.context.module_file.clone(),
            };
        }
        if let ExprKind::Path {
            location,
            relocation,
            ..
        } = &mut expression.kind
        {
            if let Some(path) = relocation.take() {
                *location = bound_path(
                    &path,
                    &location.backend,
                    self.main_module,
                    self.context,
                    self.instructions.last().copied(),
                    self.obligations,
                )
                .map_err(|error| {
                    if let Some(id) = self.instructions.last() {
                        error.at(*id, self.origins)
                    } else {
                        error
                    }
                })?;
            }
            if let Some(id) = self.instructions.last() {
                bind_endpoint(location, self.context, *id, self.obligations)
                    .map_err(|error| error.at(*id, self.origins))?;
            }
        }
        Ok(())
    }
}

fn bind_endpoint(
    location: &mut Location,
    context: &Context,
    instruction: InstructionId,
    obligations: &mut Vec<Obligation>,
) -> Result<(), ContextError> {
    #[cfg(not(windows))]
    if location.backend == Backend::Local && windows_absolute(&location.path) {
        return Err(ContextError::new("a native Windows absolute path cannot be bound on this platform")
            .hint("Keep absolute literals for their native platform, or use a contextual $pwd/$env path or an explicit remote namespace.").required_by(instruction));
    }
    if !matches!(location.backend, Backend::Wsl | Backend::Ssh { .. })
        || location.path.starts_with('/')
        || location.path == "~"
        || location.path.starts_with("~/")
    {
        return Ok(());
    }
    if let Some(cwd) = context.endpoint_cwds.get(&location.backend) {
        if !cwd.starts_with('/') || cwd.contains('\0') {
            return Err(ContextError::new(
                "an endpoint working-directory binding must be an absolute guest path",
            )
            .required_by(instruction));
        }
        location.path = append(cwd, &location.path);
        return Ok(());
    }
    obligations.push(Obligation::InheritedEndpointCwd {
        instruction,
        backend: location.backend.clone(),
    });
    Ok(())
}

fn bind_relocation(
    node: &mut Instruction,
    relocation: &Relocation,
    main_module: &str,
    context: &Context,
    obligations: &mut Vec<Obligation>,
) -> Result<(), ContextError> {
    if relocation.slot == Slot::Cwd {
        let InstructionKind::Spawn { cwd, .. } = &mut node.kind else {
            return Err(ContextError::new("cwd relocation does not name a program"));
        };
        *cwd = PathBuf::from(
            bound_path(
                &relocation.path,
                &Backend::Local,
                main_module,
                context,
                Some(node.id),
                obligations,
            )?
            .path,
        );
        return Ok(());
    }
    let location = location_mut(node, relocation.slot)?;
    *location = bound_path(
        &relocation.path,
        &location.backend,
        main_module,
        context,
        Some(relocation.instruction),
        obligations,
    )?;
    Ok(())
}

fn location_mut(node: &mut Instruction, slot: Slot) -> Result<&mut Location, ContextError> {
    match (&mut node.kind, slot) {
        (
            InstructionKind::Transfer {
                source: Input::File { location, .. },
                ..
            },
            Slot::Source,
        )
        | (
            InstructionKind::Transfer {
                destination: Output::File { location, .. },
                ..
            },
            Slot::Destination,
        )
        | (
            InstructionKind::Spawn {
                executable: location,
                ..
            },
            Slot::Executable,
        ) => Ok(location),
        (InstructionKind::Spawn { arguments, .. }, Slot::Argument { index }) => {
            match arguments.get_mut(index) {
                Some(Argument::Path(location)) => Ok(location),
                _ => Err(ContextError::new(
                    "argument relocation does not name a path argument",
                )),
            }
        }
        _ => Err(ContextError::new(
            "relocation slot is incompatible with its operation",
        )),
    }
}

fn bound_path(
    path: &crate::relocatable::Path,
    backend: &Backend,
    main_module: &str,
    context: &Context,
    instruction: Option<InstructionId>,
    obligations: &mut Vec<Obligation>,
) -> Result<Location, ContextError> {
    let mut bound_backend = backend.clone();
    let base = match &path.anchor {
        Anchor::WorkingDirectory => utf8(&context.pwd, "working directory")?,
        Anchor::Home if matches!(backend, Backend::Wsl | Backend::Ssh { .. }) => {
            if let Some(instruction) = instruction {
                obligations.push(Obligation::ForeignHome {
                    instruction,
                    backend: backend.clone(),
                });
            }
            "~".into()
        }
        Anchor::Home => local_home(context)?,
        Anchor::ModuleFile { module: name } => {
            utf8(module_file(name, main_module, context)?, "module input")?
        }
        Anchor::ModuleDirectory { module: name } => {
            let file = module_file(name, main_module, context)?;
            utf8(
                file.parent()
                    .ok_or_else(|| ContextError::new("module input has no parent directory"))?,
                "module directory",
            )?
        }
        Anchor::Environment { name } => {
            bound_backend = Backend::Local;
            let value = context
                .environment
                .get(name)
                .cloned()
                .or_else(|| std::env::var(name).ok())
                .ok_or_else(|| {
                    ContextError::new(format!("unresolved environment symbol '{name}'")).hint(
                        "Define the variable in the evaluation environment before loading this module.",
                    )
                })?;
            anchor_local(&value, &context.pwd)?
        }
        Anchor::SearchPath { name } => {
            bound_backend = Backend::Path;
            if let Some(instruction) = instruction {
                obligations.push(Obligation::SearchPath {
                    instruction,
                    name: name.clone(),
                });
            }
            name.clone()
        }
    };
    let path = append(&base, &path.suffix);
    if path.contains('\0') {
        return Err(ContextError::new("a bound path contains NUL"));
    }
    Ok(Location {
        backend: bound_backend,
        path,
    })
}

fn module_file<'a>(
    name: &str,
    main_module: &str,
    context: &'a Context,
) -> Result<&'a Path, ContextError> {
    let file = if name == main_module {
        context.module_file.as_deref()
    } else {
        context.modules.get(name).map(PathBuf::as_path)
    };
    let file = file.ok_or_else(|| ContextError::new(format!("unresolved module-file symbol '{name}'")).hint("Bind this exact module identity in Context.modules; debug filenames are not filesystem bindings."))?;
    if !file.is_absolute() {
        return Err(ContextError::new(format!(
            "module-file symbol '{name}' is not bound to an absolute path"
        ))
        .hint("Anchor this module binding under the evaluation working directory explicitly."));
    }
    Ok(file)
}

fn local_home(context: &Context) -> Result<String, ContextError> {
    if let Some(home) = &context.home {
        return anchor_local(&utf8(home, "home directory")?, &context.pwd);
    }
    for name in ["HOME", "USERPROFILE"] {
        if let Some(value) = context.environment.get(name) {
            return anchor_local(value, &context.pwd);
        }
        if let Ok(value) = std::env::var(name) {
            return anchor_local(&value, &context.pwd);
        }
    }
    Err(ContextError::new("unresolved local home-directory symbol")
        .hint("Set the evaluation home directory or HOME/USERPROFILE."))
}

fn anchor_local(value: &str, pwd: &Path) -> Result<String, ContextError> {
    if value.is_empty() || value.contains('\0') {
        return Err(ContextError::new(
            "a evaluation environment path must be nonempty and NUL-free",
        ));
    }
    if Path::new(value).is_absolute() || windows_absolute(value) {
        return Ok(value.to_owned());
    }
    let bound = pwd.join(value);
    if !bound.is_absolute() {
        return Err(ContextError::new(
            "a evaluation environment path has an unresolved native drive context",
        ));
    }
    utf8(&bound, "environment path")
}

fn windows_absolute(value: &str) -> bool {
    value.starts_with("\\\\")
        || value.as_bytes().get(1) == Some(&b':')
            && value
                .as_bytes()
                .get(2)
                .is_some_and(|byte| matches!(byte, b'/' | b'\\'))
}

fn utf8(path: &Path, subject: &str) -> Result<String, ContextError> {
    path.to_str()
        .map(str::to_owned)
        .ok_or_else(|| ContextError::new(format!("the evaluation {subject} is not UTF-8")))
}

fn append(base: &str, suffix: &str) -> String {
    if suffix.is_empty() {
        return base.to_owned();
    }
    format!(
        "{}/{}",
        base.trim_end_matches(['/', '\\']),
        suffix.trim_start_matches(['/', '\\'])
    )
}

fn bind_exports(
    exports: &mut [Export],
    main_module: &str,
    context: &Context,
) -> Result<(), ContextError> {
    for export in exports {
        let mut binder = ProgramBinder {
            relocations: BTreeMap::new(),
            main_module,
            context,
            origins: &Origins::default(),
            obligations: &mut Vec::new(),
            instructions: Vec::new(),
        };
        match &mut export.value {
            ExportValue::Dynamic { expression } => {
                crate::object::control::visit_expr(expression, &mut binder)?
            }
            ExportValue::Range { range } => {
                crate::object::control::visit_range(range, &mut binder)?
            }
            _ => {}
        }
        let ExportValue::Path {
            backend,
            path,
            relocation: Some(relocation),
            ..
        } = &mut export.value
        else {
            continue;
        };
        if backend.is_none() && relocation.anchor == Anchor::Home {
            *path = append("~", &relocation.suffix);
            continue;
        }
        let bound = bound_path(
            relocation,
            backend.as_ref().unwrap_or(&Backend::Local),
            main_module,
            context,
            None,
            &mut Vec::new(),
        )?;
        *backend = Some(bound.backend);
        *path = bound.path;
        if let ExportValue::Path { relocation, .. } = &mut export.value {
            *relocation = None;
        }
    }
    Ok(())
}

struct IdMaps {
    processes: BTreeMap<ProcessId, ProcessId>,
    slots: BTreeMap<crate::hir::control::SlotId, crate::hir::control::SlotId>,
    next_slot: usize,
}

impl IdMaps {
    fn new(
        program: &hir::Program,
        exports: &[Export],
        base: usize,
        slot_base: usize,
    ) -> Result<Self, ContextError> {
        let mut collector = IdentityCollector::default();
        crate::object::control::read_region(&program.body, &mut collector)?;
        // Exports contribute slot references, not module process definitions.
        let processes = std::mem::take(&mut collector.processes);
        for export in exports {
            match &export.value {
                ExportValue::Dynamic { expression } => {
                    crate::object::control::read_expr(expression, &mut collector)?
                }
                ExportValue::Range { range } => {
                    crate::object::control::read_range(range, &mut collector)?
                }
                _ => {}
            }
        }
        let processes = processes
            .into_iter()
            .enumerate()
            .map(|(index, old)| {
                base.checked_add(index)
                    .map(|id| (old, ProcessId(id)))
                    .ok_or_else(|| ContextError::new("process identity space is exhausted"))
            })
            .collect::<Result<_, _>>()?;
        let next_slot = slot_base
            .checked_add(collector.slots.len())
            .ok_or_else(|| ContextError::new("runtime slot identity space is exhausted"))?;
        let slots = collector
            .slots
            .into_iter()
            .enumerate()
            .map(|(index, old)| (old, crate::hir::control::SlotId(slot_base + index)))
            .collect();
        Ok(Self {
            processes,
            slots,
            next_slot,
        })
    }

    fn process(&self, id: &mut ProcessId) -> Result<(), ContextError> {
        *id = *self
            .processes
            .get(id)
            .ok_or_else(|| ContextError::new("process reference has no local definition"))?;
        Ok(())
    }

    fn program(&self, program: &mut hir::Program) -> Result<(), ContextError> {
        self.region(&mut program.body)
    }

    fn region(&self, region: &mut Region) -> Result<(), ContextError> {
        crate::object::control::visit_region(region, &mut IdMapper(self))
    }

    fn exports(&self, exports: &mut [Export]) -> Result<(), ContextError> {
        for export in exports {
            match &mut export.value {
                ExportValue::Process { process } | ExportValue::Port { process, .. } => {
                    self.process(process)?
                }
                ExportValue::Dynamic { expression } => {
                    crate::object::control::visit_expr(expression, &mut IdMapper(self))?
                }
                ExportValue::Range { range } => {
                    crate::object::control::visit_range(range, &mut IdMapper(self))?
                }
                _ => {}
            }
        }
        Ok(())
    }
}

fn collect_obligations(region: &Region, obligations: &mut Vec<Obligation>) {
    let mut nodes = Vec::new();
    crate::object::control::region_nodes(region, &mut nodes);
    for node in nodes {
        match &node.kind {
            InstructionKind::Control(crate::hir::control::ControlInstruction::Spawn {
                process,
                executable,
                ..
            }) => {
                if let crate::hir::control::ExprKind::Path { location, .. } = &executable.kind {
                    obligations.push(Obligation::ProgramLaunch {
                        instruction: node.id,
                        process: *process,
                        backend: location.backend.clone(),
                    });
                }
            }
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                if let Input::File { location, .. } = source {
                    obligations.push(Obligation::OpenEndpoint {
                        instruction: node.id,
                        backend: location.backend.clone(),
                    });
                }
                if let Output::File { location, .. } = destination {
                    obligations.push(Obligation::OpenEndpoint {
                        instruction: node.id,
                        backend: location.backend.clone(),
                    });
                }
            }
            InstructionKind::Spawn {
                process,
                executable,
                ..
            } => {
                obligations.push(Obligation::ProgramLaunch {
                    instruction: node.id,
                    process: *process,
                    backend: executable.backend.clone(),
                });
            }
            _ => {}
        }
    }
}

#[derive(Default)]
struct IdentityCollector {
    processes: BTreeSet<ProcessId>,
    slots: BTreeSet<crate::hir::control::SlotId>,
}

fn control_slots(
    control: &mut crate::hir::control::ControlInstruction,
    mut visit: impl FnMut(&mut crate::hir::control::SlotId),
) {
    use crate::hir::control::ControlInstruction;
    match control {
        ControlInstruction::Let { slot, .. }
        | ControlInstruction::Set { slot, .. }
        | ControlInstruction::For { slot, .. } => visit(slot),
        _ => {}
    }
}
fn expr_slots(
    expression: &mut crate::hir::control::Expr,
    mut visit: impl FnMut(&mut crate::hir::control::SlotId),
) {
    use crate::hir::control::{ExprKind, RangeQuery};
    match &mut expression.kind {
        ExprKind::Slot(slot) | ExprKind::Let { slot, .. } => visit(slot),
        ExprKind::RangeQuery {
            query:
                RangeQuery::Any { slot, .. }
                | RangeQuery::All { slot, .. }
                | RangeQuery::None { slot, .. }
                | RangeQuery::Find { slot, .. },
            ..
        } => visit(slot),
        _ => {}
    }
}
fn range_slots(
    range: &mut crate::hir::control::RangeExpr,
    mut visit: impl FnMut(&mut crate::hir::control::SlotId),
) {
    use crate::hir::control::RangeStage;
    for stage in &mut range.stages {
        match stage {
            RangeStage::Filter { slot, .. }
            | RangeStage::Transform { slot, .. }
            | RangeStage::TakeWhile { slot, .. }
            | RangeStage::DropWhile { slot, .. } => visit(slot),
            _ => {}
        }
    }
}
impl<'ir> crate::object::control::ReadVisitor<'ir> for IdentityCollector {
    type Error = ContextError;
    fn instruction(&mut self, node: &'ir Instruction) -> Result<(), ContextError> {
        if let InstructionKind::Spawn { process, .. }
        | InstructionKind::Control(crate::hir::control::ControlInstruction::Spawn {
            process,
            ..
        }) = &node.kind
        {
            self.processes.insert(*process);
        }
        Ok(())
    }
    fn control(
        &mut self,
        control: &'ir crate::hir::control::ControlInstruction,
    ) -> Result<(), ContextError> {
        use crate::hir::control::ControlInstruction;
        if let ControlInstruction::Let { slot, .. }
        | ControlInstruction::Set { slot, .. }
        | ControlInstruction::For { slot, .. } = control
        {
            self.slots.insert(*slot);
        }
        Ok(())
    }
    fn expression(
        &mut self,
        expression: &'ir crate::hir::control::Expr,
    ) -> Result<(), ContextError> {
        use crate::hir::control::{ExprKind, RangeQuery};
        match &expression.kind {
            ExprKind::Slot(slot)
            | ExprKind::Let { slot, .. }
            | ExprKind::RangeQuery {
                query:
                    RangeQuery::Any { slot, .. }
                    | RangeQuery::All { slot, .. }
                    | RangeQuery::None { slot, .. }
                    | RangeQuery::Find { slot, .. },
                ..
            } => {
                self.slots.insert(*slot);
            }
            _ => {}
        }
        Ok(())
    }
    fn range(&mut self, range: &'ir crate::hir::control::RangeExpr) -> Result<(), ContextError> {
        use crate::hir::control::RangeStage;
        for stage in &range.stages {
            match stage {
                RangeStage::Filter { slot, .. }
                | RangeStage::Transform { slot, .. }
                | RangeStage::TakeWhile { slot, .. }
                | RangeStage::DropWhile { slot, .. } => {
                    self.slots.insert(*slot);
                }
                _ => {}
            }
        }
        Ok(())
    }
}
struct IdMapper<'a>(&'a IdMaps);
impl crate::object::control::Visitor for IdMapper<'_> {
    type Error = ContextError;
    fn instruction(&mut self, node: &mut Instruction) -> Result<(), ContextError> {
        match &mut node.kind {
            InstructionKind::Spawn { process, .. }
            | InstructionKind::Control(crate::hir::control::ControlInstruction::Spawn {
                process,
                ..
            })
            | InstructionKind::Await {
                process: Some(process),
            } => self.0.process(process)?,
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                if let Input::Port { process, .. } = source {
                    self.0.process(process)?;
                }
                if let Output::Stdin(process) = destination {
                    self.0.process(process)?;
                }
            }
            _ => {}
        }
        Ok(())
    }
    fn control(
        &mut self,
        control: &mut crate::hir::control::ControlInstruction,
    ) -> Result<(), ContextError> {
        control_slots(control, |slot| *slot = self.0.slots[slot]);
        if let crate::hir::control::ControlInstruction::Transfer {
            source,
            destination,
        } = control
        {
            if let crate::hir::control::TransferInput::Port { process, .. } = source {
                self.0.process(process)?;
            }
            if let crate::hir::control::TransferOutput::Stdin(process) = destination {
                self.0.process(process)?;
            }
        }
        Ok(())
    }
    fn expression(
        &mut self,
        expression: &mut crate::hir::control::Expr,
    ) -> Result<(), ContextError> {
        expr_slots(expression, |slot| *slot = self.0.slots[slot]);
        Ok(())
    }
    fn range(&mut self, range: &mut crate::hir::control::RangeExpr) -> Result<(), ContextError> {
        range_slots(range, |slot| *slot = self.0.slots[slot]);
        Ok(())
    }
}
