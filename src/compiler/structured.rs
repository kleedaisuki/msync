//! Hygienic expansion and typed lowering for the structured language.
//!
//! A session owns persistent lexical bindings and identity allocation. Compilation
//! works on a candidate session; neither a partial macro expansion nor an invalid
//! transaction can alter the environment used by the next REPL submission.

use std::collections::{BTreeMap, HashMap, HashSet};
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;

use super::{Alias, Backend, CompileContext, Compiler, Location, Space};
use crate::hir::{
    self, Argument, Input, Instruction, InstructionKind, Output, ProcessId, ReadPort,
};
use crate::hir::{Region, RollbackScope, TransactionId, TransactionInfo};
use crate::language::{self, BindingOp, DestinationMode, Document, Expr, ExprKind, Name, PathExpr};
use crate::source::{InstructionOrigin, LocatedReport, Origins, Source, SourceSpan};
use crate::syntax::{OperandSource, Report, Span};

#[derive(Clone, Debug)]
enum Value {
    Path(PathValue),
    Literal(Arc<str>),
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
}

impl From<Alias> for PathValue {
    fn from(value: Alias) -> Self {
        Self {
            backend: value.backend,
            path: value.path,
            is_file: value.is_file,
            mode: DestinationMode::Exact,
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

type TransferValues = (Input, Output, Vec<((ProcessId, Port), Span)>);

#[derive(Clone, Debug)]
struct Macro {
    name: Name,
    parameters: Vec<Name>,
    body: language::Block,
    globals: Arc<BTreeMap<String, Value>>,
    global_sites: Arc<BTreeMap<String, SourceSpan>>,
    macros: Arc<BTreeMap<String, Arc<Macro>>>,
    source_file: Option<PathBuf>,
    source: Arc<Source>,
}

#[derive(Clone, Debug, Default)]
struct Scope {
    values: Arc<BTreeMap<String, Value>>,
    macros: Arc<BTreeMap<String, Arc<Macro>>>,
    globals: Option<Arc<BTreeMap<String, Value>>>,
    imported: HashSet<String>,
    sites: Arc<BTreeMap<String, SourceSpan>>,
    global_sites: Option<Arc<BTreeMap<String, SourceSpan>>>,
}

/// Persistent structured compiler environment, independent of executor state.
#[derive(Clone, Debug, Default)]
pub struct Session {
    legacy: Compiler,
    scope: Scope,
    next_process: usize,
    next_transaction: usize,
    transactions: BTreeMap<String, TransactionId>,
    consumed: HashMap<(ProcessId, Port), SourceSpan>,
}

#[derive(Clone, Debug)]
pub struct Lowered {
    pub program: hir::Program,
    pub origins: Origins,
}

impl Session {
    pub fn new() -> Self {
        Self::default()
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
            Arc::make_mut(&mut self.scope.sites).remove(name);
        }
        for (name, path) in compiler.aliases.iter() {
            let mut path: PathValue = path.clone().into();
            path.mode = legacy_path_mode(&path.path);
            values.insert(name.clone(), Value::Path(path));
            Arc::make_mut(&mut self.scope.sites).remove(name);
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
        // retain an older process or named rollback record.
        let next_process = self.next_process;
        let next_transaction = self.next_transaction;
        *self = Self {
            next_process,
            next_transaction,
            ..Self::default()
        };
    }

    pub fn bindings(&self) -> Vec<String> {
        let mut bindings = Vec::new();
        for (name, value) in self.scope.values.iter() {
            match value {
                Value::Literal(text) => bindings.push(format!("{name} := {}", literal_text(text))),
                Value::Process(id) => bindings.push(format!("{name} => <program {}>", id.0)),
                Value::Port(id, port) => {
                    bindings.push(format!("{name} := <program {} {port:?}>", id.0))
                }
                Value::Path(path) => bindings.push(format!("{name} := {}", path_text(path))),
                Value::Space(space) => bindings.push(format!("{name} => {space}")),
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
        bindings
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
        let mut lower = Lower {
            session: &mut candidate,
            program: hir::Program::default(),
            next_instruction: 0,
            selectors: HashSet::new(),
            expansion: Vec::new(),
            calls: Vec::new(),
            source,
            origins: Origins::default(),
        };
        let mut body = Region::default();
        let errors = lower.block(&document.statements, &mut scope, context, None, &mut body);
        if !errors.is_empty() {
            return Err(errors);
        }
        lower.program.body = body;
        let result = Lowered {
            program: lower.program,
            origins: lower.origins,
        };
        candidate.scope = scope;
        candidate.refresh_legacy();
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

struct Lower<'a> {
    session: &'a mut Session,
    program: hir::Program,
    next_instruction: usize,
    selectors: HashSet<String>,
    expansion: Vec<Span>,
    calls: Vec<(Arc<Macro>, SourceSpan)>,
    source: Arc<Source>,
    origins: Origins,
}

impl Lower<'_> {
    fn block(
        &mut self,
        statements: &[language::Statement],
        scope: &mut Scope,
        context: &CompileContext,
        transaction: Option<TransactionId>,
        region: &mut Region,
    ) -> Vec<LocatedReport> {
        let mut errors = Vec::new();
        for statement in statements {
            if let Err(report) = self.statement(statement, scope, context, transaction, region) {
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
        transaction: Option<TransactionId>,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        match &statement.kind {
            language::StatementKind::Call(call) => {
                self.expand_macro(call, scope, context, transaction, region)
            }
            language::StatementKind::Transfer {
                source,
                destination,
            } => self.transfer(source, destination, scope, context, statement.span, region),
            language::StatementKind::Transaction { .. } => {
                self.transaction(statement, scope, context, transaction, region)
            }
            _ => self
                .simple_statement(statement, scope, context, transaction, region)
                .map_err(|report| self.located(report)),
        }
    }

    fn located(&self, report: Report) -> LocatedReport {
        LocatedReport::new(self.source.clone(), report)
    }

    fn simple_statement(
        &mut self,
        statement: &language::Statement,
        scope: &mut Scope,
        context: &CompileContext,
        transaction: Option<TransactionId>,
        region: &mut Region,
    ) -> Result<(), Report> {
        use language::StatementKind;
        match &statement.kind {
            StatementKind::Comment(_) => Ok(()),
            StatementKind::Binding { name, op, value } => {
                self.bind(name, *op, value, scope, context, region)
            }
            StatementKind::Transfer { .. }
            | StatementKind::Call(_)
            | StatementKind::Transaction { .. } => {
                unreachable!("structured dispatch precedes simple statements")
            }
            StatementKind::Macro {
                name,
                parameters,
                body,
            } => self.define_macro(name, parameters, body, scope, context),
            StatementKind::Global(names) => import_globals(names, scope),
            StatementKind::Commit => {
                let transaction = require_transaction(transaction, statement.span, "commit")?;
                self.emit(
                    region,
                    statement.span,
                    InstructionKind::Commit { transaction },
                );
                Ok(())
            }
            StatementKind::Rollback(target) => {
                let (transaction, rollback_scope) = match target {
                    Some(name) => {
                        if transaction.is_some() {
                            return Err(Report::new("named rollback is not allowed inside a transaction", name.span)
                                .help("Use bare rollback to restore this transaction's last checkpoint, or move named rollback to the top level."));
                        }
                        let id = self.session.transactions.get(&name.text).copied().ok_or_else(|| {
                            Report::new(format!("unknown preceding transaction '{}'", name.text), name.span)
                                .help("Named rollback must refer to a transaction already declared in this session.")
                        })?;
                        (id, RollbackScope::Begin)
                    }
                    None => (
                        require_transaction(transaction, statement.span, "rollback")?,
                        RollbackScope::Checkpoint,
                    ),
                };
                self.emit(
                    region,
                    statement.span,
                    InstructionKind::Rollback {
                        transaction,
                        scope: rollback_scope,
                    },
                );
                Ok(())
            }
        }
    }

    fn transaction(
        &mut self,
        statement: &language::Statement,
        scope: &mut Scope,
        context: &CompileContext,
        current: Option<TransactionId>,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        let language::StatementKind::Transaction {
            name,
            isolation,
            body,
        } = &statement.kind
        else {
            unreachable!()
        };
        if current.is_some() {
            return Err(self.located(Report::new("nested transactions need an explicit transaction boundary", statement.span)
                .help("Keep named transactions at the top level; use commit and rollback as checkpoints within one transaction.")));
        }
        check_name(name).map_err(|report| self.located(report))?;
        if self.session.transactions.contains_key(&name.text) {
            return Err(self.located(
                Report::new(
                    format!("transaction '{}' is already defined", name.text),
                    name.span,
                )
                .help(
                    "Use a unique transaction name so named rollback has one unambiguous origin.",
                ),
            ));
        }
        let id = TransactionId(self.session.next_transaction);
        self.session.next_transaction += 1;
        self.session.transactions.insert(name.text.clone(), id);
        self.program.transactions.push(TransactionInfo {
            id,
            name: name.text.clone(),
            isolation: *isolation,
            span: statement.span,
        });
        let mut child = Region::default();
        if let Some(error) = self
            .block(body, scope, context, Some(id), &mut child)
            .into_iter()
            .next()
        {
            return Err(error);
        }
        self.emit(
            region,
            statement.span,
            InstructionKind::Transaction {
                transaction: id,
                body: child,
            },
        );
        Ok(())
    }

    fn bind(
        &mut self,
        name: &Name,
        op: BindingOp,
        expression: &Expr,
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<(), Report> {
        check_name(name)?;
        let span = Span {
            start: name.span.start,
            end: expression.span.end,
        };
        let value = match (&expression.kind, op) {
            (ExprKind::Call { callee, arguments }, BindingOp::Space) => {
                let executable = file_value(resolve(callee, scope, context)?, callee.span, context)?;
                if !matches!(executable.backend, Backend::Local | Backend::Path | Backend::Wsl | Backend::Ssh { .. }) {
                    return Err(Report::new("this endpoint cannot launch a program", callee.span)
                        .help("Use a local, PATH, WSL or SSH executable endpoint."));
                }
                let arguments = arguments.iter().map(|argument| match resolve(argument, scope, context)? {
                    Value::Literal(text) => {
                        if text.contains('\0') {
                            return Err(Report::new("program arguments cannot contain NUL", argument.span)
                                .help("Pass these bytes through program::stdin or a file instead; native argv strings cannot contain NUL."));
                        }
                        Ok(Argument::Text(text.to_string()))
                    }
                    Value::Path(path) => Ok(Argument::Path(location(path, context, argument.span)?)),
                    _ => Err(Report::new("program arguments must be text or file paths", argument.span)
                        .help("Quote literal argv text; pass a path binding to retain its endpoint identity.")),
                }).collect::<Result<Vec<_>, _>>()?;
                let id = ProcessId(self.session.next_process);
                self.session.next_process += 1;
                self.emit(region, span, InstructionKind::Spawn { process: id, executable, arguments, cwd: context.pwd.clone() });
                Value::Process(id)
            }
            (ExprKind::Call { .. }, BindingOp::Value) => {
                return Err(Report::new("a program call binds a program space with '=>'", expression.span)
                    .help("Use 'program => executable(arguments)' and read program::stdout or program::exit_code."));
            }
            (_, BindingOp::Space) => match resolve(expression, scope, context)? {
                Value::Space(space) => Value::Space(space),
                _ => return Err(Report::new("'=>' expects an endpoint namespace or a program call", expression.span)
                    .help("Use ':=' for path values, or '=>' with $local, $wsl, an endpoint URL or executable(arguments).")),
            },
            (_, BindingOp::Value) => resolve(expression, scope, context)?,
        };
        let site = self.expression_site(expression, scope);
        Arc::make_mut(&mut scope.sites).insert(name.text.clone(), site);
        Arc::make_mut(&mut scope.values).insert(name.text.clone(), value);
        Ok(())
    }

    fn transfer(
        &mut self,
        source: &Expr,
        destination: &Expr,
        scope: &Scope,
        context: &CompileContext,
        span: Span,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        let operands = (
            self.expression_site(source, scope),
            self.expression_site(destination, scope),
        );
        let (source, destination, owned) = self
            .transfer_values(source, destination, scope, context)
            .map_err(|report| self.located(report))?;
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
        Ok(())
    }

    fn transfer_values(
        &mut self,
        source: &Expr,
        destination: &Expr,
        scope: &Scope,
        context: &CompileContext,
    ) -> Result<TransferValues, Report> {
        let source_value = resolve(source, scope, context)?;
        let destination_value = resolve(destination, scope, context)?;
        let mut owned = Vec::new();
        let source = match source_value {
            Value::Literal(text) => Input::Literal(text),
            Value::Path(mut path) => {
                let pattern = super::take_selector(&mut path.path, &mut self.selectors).map_err(|message| {
                    Report::new(message, selector_site(source)).help("Use one final '/<regex>' component; selectors match complete entry names.")
                })?;
                if pattern.is_some()
                    && matches!(path.backend, Some(Backend::Http { .. } | Backend::Path))
                {
                    return Err(Report::new(
                        "selectors require a directory-capable source",
                        source.span,
                    )
                    .help(
                        "Select from a local, WSL or SSH directory, not an HTTP or PATH endpoint.",
                    ));
                }
                Input::File {
                    location: location(path, context, source.span)?,
                    pattern,
                }
            }
            Value::Port(process, Port::Read(port)) => {
                if matches!(port, ReadPort::Stdout | ReadPort::Stderr) {
                    owned.push(((process, Port::Read(port)), source.span));
                }
                Input::Port { process, port }
            }
            _ => {
                return Err(
                    Report::new("this value is not a readable source", source.span).help(
                        "Use a file, quoted UTF-8 literal, stdout/stderr, pid, exit_code or meta.",
                    ),
                );
            }
        };
        let mode = expression_mode(destination);
        let destination = match destination_value {
            Value::Path(mut path) => {
                let mode = if mode == DestinationMode::Exact { path.mode } else { mode };
                if matches!(path.backend, Some(Backend::Http { .. } | Backend::Path)) {
                    return Err(Report::new("HTTP(S) and PATH endpoints are read-only", qualifier_site(destination)));
                }
                if path.path.contains(['<', '>']) {
                    return Err(Report::new("selectors are only allowed in the source", selector_site(destination)));
                }
                if mode == DestinationMode::ReplaceTree {
                    if path.path.ends_with("/.") || path.path.ends_with("\\.") {
                        path.path.pop();
                    }
                    reject_root_replacement(&path, destination.span)?;
                } else if mode == DestinationMode::Exact && !matches!(path.backend, Some(Backend::Tcp { .. })) {
                    reject_root_replacement(&path, destination.span)?;
                }
                if matches!(&source, Input::File { pattern: Some(_), .. }) && mode != DestinationMode::Container {
                    return Err(Report::new("a selector requires a destination container ending in '/'", destination.span)
                        .help("Append '/' to preserve each selected entry's basename."));
                }
                Output::File { location: location(path, context, destination.span)?, mode }
            }
            Value::Port(process, Port::Stdin) => {
                owned.push(((process, Port::Stdin), destination.span));
                Output::Stdin(process)
            }
            _ => return Err(Report::new("this value is not a writable destination", destination.span)
                .help("Use a writable path such as './\"output name\"' or '$pwd / \"output name\"', or program::stdin. Bare quoted expressions and output streams are read-only.")),
        };
        Ok((source, destination, owned))
    }

    fn define_macro(
        &self,
        name: &Name,
        parameters: &[Name],
        body: &[language::Statement],
        scope: &mut Scope,
        context: &CompileContext,
    ) -> Result<(), Report> {
        if scope.globals.is_some() {
            return Err(
                Report::new("a macro cannot declare another macro", name.span).help(
                    "Declare this macro outside the enclosing macro, then call it inside the body.",
                ),
            );
        }
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
        reject_nested_macro(body)?;
        let definition = Macro {
            name: name.clone(),
            parameters: parameters.to_vec(),
            body: body.to_vec(),
            globals: scope.values.clone(),
            global_sites: scope.sites.clone(),
            macros: scope.macros.clone(),
            source_file: context.source_file.clone(),
            source: self.source.clone(),
        };
        Arc::make_mut(&mut scope.macros).insert(name.text.clone(), Arc::new(definition));
        Ok(())
    }

    fn expand_macro(
        &mut self,
        call: &Expr,
        caller: &Scope,
        context: &CompileContext,
        transaction: Option<TransactionId>,
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
                Ok((parameter.text.clone(), resolve(argument, caller, context)?))
            })
            .collect::<Result<BTreeMap<_, _>, Report>>()
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
        let mut scope = Scope {
            values: Arc::new(values),
            macros: definition.macros.clone(),
            globals: Some(definition.globals.clone()),
            imported: HashSet::new(),
            sites: Arc::new(sites),
            global_sites: Some(definition.global_sites.clone()),
        };
        let context = CompileContext {
            pwd: context.pwd.clone(),
            source_file: definition.source_file.clone(),
        };
        let call_site = self.source.at(call.span);
        self.calls.push((definition.clone(), call_site.clone()));
        self.expansion.push(definition.name.span);
        let caller_source = std::mem::replace(&mut self.source, definition.source.clone());
        let errors = self.block(&definition.body, &mut scope, &context, transaction, region);
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
            "global is only meaningful inside a macro",
            names.first().map_or(Span::default(), |name| name.span),
        )
        .help("Top-level and transaction bindings already share one lexical environment.")
    })?;
    for name in names {
        check_name(name)?;
        if scope.values.contains_key(&name.text) && !scope.imported.contains(&name.text) {
            return Err(Report::new(format!("global '{}' conflicts with a parameter or local binding", name.text), name.span)
                .help("Use another local name; an explicit global import cannot overwrite a macro parameter."));
        }
        let value = globals.get(&name.text).cloned().ok_or_else(|| {
            Report::new(
                format!("global '{}' was not declared before this macro", name.text),
                name.span,
            )
            .help("Declare the binding before the macro definition, or pass it as a parameter.")
        })?;
        Arc::make_mut(&mut scope.values).insert(name.text.clone(), value);
        if let Some(site) = scope
            .global_sites
            .as_ref()
            .and_then(|sites| sites.get(&name.text))
        {
            Arc::make_mut(&mut scope.sites).insert(name.text.clone(), site.clone());
        }
        scope.imported.insert(name.text.clone());
    }
    Ok(())
}

fn resolve(expression: &Expr, scope: &Scope, context: &CompileContext) -> Result<Value, Report> {
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
        ExprKind::Call { .. } => Err(Report::new(
            "a call is not a path or stream value",
            expression.span,
        )
        .help(
            "Bind a program with 'name => executable(arguments)' or invoke a macro as a statement.",
        )),
    }
}

fn resolve_join(
    base: &Expr,
    suffix: Option<&Expr>,
    operator: Span,
    mode: DestinationMode,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Report> {
    let base_value = resolve(base, scope, context)?;
    let suffix_value = suffix
        .map(|expression| resolve(expression, scope, context))
        .transpose()?;
    let suffix_site = suffix.map_or(operator, |expression| expression.source.content);
    if let Value::Space(Space::Endpoint(backend)) = base_value {
        let mut path = path_fragment(
            suffix_value.ok_or_else(|| {
                Report::new("a physical namespace needs a path after '/'", operator)
            })?,
            suffix_site,
        )?;
        if mode != DestinationMode::Exact {
            path.mode = mode;
        }
        return qualify(Value::Path(path), backend, operator);
    }
    let mut base_path = match base_value {
        Value::Space(Space::Pwd) => PathValue {
            backend: Some(Backend::Local),
            path: context.pwd.to_string_lossy().into_owned(),
            is_file: false,
            mode: DestinationMode::Exact,
        },
        value => path_fragment(value, base.source.content)?,
    };
    if base_path.is_file {
        return Err(Report::new(
            "a known file value cannot have child path components",
            operator,
        )
        .help("Join with its containing directory, not a known file value."));
    }
    let Some(suffix_value) = suffix_value else {
        if !base_path.path.ends_with(['/', '\\']) {
            base_path.path.push('/');
        }
        base_path.mode = DestinationMode::Container;
        return Ok(Value::Path(base_path));
    };
    let suffix_path = path_fragment(suffix_value, suffix_site)?;
    if rooted_fragment(&suffix_path.path) {
        return Err(Report::new("a rooted right operand cannot replace a path-join base", operator)
            .label(suffix_site, "this operand is rooted")
            .help("Use the rooted path directly, or qualify it with a physical namespace such as '$local / \"/absolute/path\"'."));
    }
    if let (Some(left), Some(right)) = (&base_path.backend, &suffix_path.backend)
        && left != right
    {
        return Err(Report::new(
            "joined path operands have conflicting endpoints",
            operator,
        ));
    }
    if base_path.backend.is_none() {
        base_path.backend = suffix_path.backend;
    }
    if !base_path.path.ends_with(['/', '\\']) {
        base_path.path.push('/');
    }
    base_path.path.push_str(&suffix_path.path);
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
        },
        _ => {
            return Err(Report::new(
                "'/' joins path values or string fragments, not programs or streams",
                span,
            ));
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
    path.starts_with(['/', '\\'])
        || path == "~"
        || path.starts_with("~/")
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
) -> Result<Value, Report> {
    if path.path.is_empty() || path.path.contains('\0') {
        return Err(Report::new(
            "paths must be nonempty and cannot contain NUL",
            span,
        ));
    }
    let quoted = site.content.start > site.span.start;
    if path.qualifier.is_none()
        && !quoted
        && let Some(value) = scope.values.get(&path.path)
    {
        return Ok(value.clone());
    }
    let qualifier = match &path.qualifier {
        Some(name) => match scope.values.get(&name.text) {
            Some(Value::Process(process)) => {
                return process_port(*process, &path.path, site.content);
            }
            Some(Value::Space(space)) => Some(space.clone()),
            Some(_) => return Err(Report::new(
                format!("'{}' is a value, not a namespace or program", name.text),
                name.span,
            )
            .help(
                "Append path components with '/', or qualify with a namespace declared using '=>'.",
            )),
            None => Some(Compiler::new().space(&name.text, name.span)?),
        },
        None => None,
    };
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
            .map(|path| Value::Path(path.into()))
        }
        Some(Space::Endpoint(backend)) => qualify(
            if quoted {
                Value::Path(PathValue {
                    backend: None,
                    path: path.path.clone(),
                    is_file: false,
                    mode: DestinationMode::Exact,
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
        })),
        None => resolve_component(&path.path, site.content, scope, context),
    }
}

fn resolve_component(
    path: &str,
    span: Span,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Report> {
    let name = path.split(['/', '\\']).next().unwrap_or(path);
    let suffix = &path[name.len()..];
    if let Some(value) = scope.values.get(name) {
        return match value {
            Value::Path(base) => {
                if base.is_file && !suffix.is_empty() {
                    return Err(Report::new(
                        "a known file value cannot have child path components",
                        span,
                    )
                    .help("Use the containing directory value to form child paths."));
                }
                let mut base = base.clone();
                if !suffix.is_empty() {
                    base.path = format!("{}{suffix}", base.path.trim_end_matches(['/', '\\']));
                    base.mode = DestinationMode::Exact;
                }
                Ok(Value::Path(base))
            }
            _ if suffix.is_empty() => Ok(value.clone()),
            _ => Err(Report::new(format!("'{name}' is not a path value"), span)
                .help("Only file/directory path values can have '/child' suffixes.")),
        };
    }
    if path.starts_with('$') && !path.contains(['/', '\\']) || path.contains("://") {
        return Compiler::new().space(path, span).map(Value::Space);
    }
    Compiler::new()
        .expand(path, &OperandSource::unmapped(span), context)
        .map(|path| Value::Path(path.into()))
}

fn resolve_pwd(
    path: &PathExpr,
    span: Span,
    site: &OperandSource,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Report> {
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
        .map(|path| Value::Path(path.into()));
    }
    let value = if quoted {
        Value::Path(PathValue {
            backend: None,
            path: path.path.clone(),
            is_file: false,
            mode: DestinationMode::Exact,
        })
    } else {
        resolve_component(&path.path, site.content, scope, context)?
    };
    let mut value = path_fragment(value, site.content)?;
    if value
        .backend
        .as_ref()
        .is_some_and(|backend| backend != &Backend::Local)
    {
        return Err(Report::new("a remote path cannot be contextualized inside local $pwd", span)
            .help("Remove '$pwd::' to retain the parameter's remote endpoint, or pass a relative path item."));
    }
    if !Path::new(&value.path).is_absolute() {
        let cwd = context
            .pwd
            .to_str()
            .ok_or_else(|| Report::new("working directory is not a UTF-8 path", span))?;
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

fn qualify(value: Value, backend: Backend, span: Span) -> Result<Value, Report> {
    let mut path = path_fragment(value, span)?;
    if path.backend.as_ref().is_some_and(|other| other != &backend) {
        return Err(Report::new("path value has a conflicting endpoint", span)
            .help("Remove the explicit qualifier to retain the value's original endpoint, or pass an unqualified path item."));
    }
    if matches!(backend, Backend::Tcp { .. }) && path.path != "/" {
        return Err(super::tcp_path_report(span));
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

fn location(path: PathValue, context: &CompileContext, span: Span) -> Result<Location, Report> {
    let backend = path.backend.unwrap_or(Backend::Local);
    let mut path = path.path;
    if backend == Backend::Local
        && !Path::new(&path).is_absolute()
        && !path.starts_with("~/")
        && path != "~"
    {
        let cwd = context
            .pwd
            .to_str()
            .ok_or_else(|| Report::new("working directory is not a UTF-8 path", span))?;
        path = format!("{}/{}", cwd.trim_end_matches(['/', '\\']), path);
    }
    Ok(Location { backend, path })
}

fn file_value(value: Value, span: Span, context: &CompileContext) -> Result<Location, Report> {
    match value {
        Value::Path(path) => location(path, context, span),
        _ => Err(Report::new(
            "an executable must be a path, not a namespace, literal or program stream",
            span,
        )
        .help("Use '$path::python', '$pwd::tool' or an explicit executable path.")),
    }
}

fn literal_text(text: &str) -> String {
    let mut output = String::new();
    language::write_literal(&mut output, text);
    output
}

fn path_text(path: &PathValue) -> String {
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
    let mut output = if let Some(backend) = &path.backend {
        format!(
            "{backend} / {}",
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

fn reject_nested_macro(body: &[language::Statement]) -> Result<(), Report> {
    for statement in body {
        match &statement.kind {
            language::StatementKind::Macro { name, .. } => {
                return Err(Report::new("a macro cannot declare another macro", name.span)
                    .help("Declare the inner macro at the top level before this macro, then call it from the body."));
            }
            language::StatementKind::Transaction { body, .. } => reject_nested_macro(body)?,
            _ => {}
        }
    }
    Ok(())
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

fn require_transaction(
    transaction: Option<TransactionId>,
    span: Span,
    command: &str,
) -> Result<TransactionId, Report> {
    transaction.ok_or_else(|| {
        Report::new(format!("{command} requires an enclosing transaction"), span)
            .help("Place this statement inside a transaction block; use 'rollback NAME' at the top level for complete named rollback.")
    })
}
