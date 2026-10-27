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
use crate::syntax::{OperandSource, Report, Span};

#[derive(Clone, Debug)]
enum Value {
    Path(Alias),
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
struct Macro {
    name: Name,
    parameters: Vec<Name>,
    body: language::Block,
    globals: Arc<BTreeMap<String, Value>>,
    macros: Arc<BTreeMap<String, Arc<Macro>>>,
    source_file: Option<PathBuf>,
}

#[derive(Clone, Debug, Default)]
struct Scope {
    values: Arc<BTreeMap<String, Value>>,
    macros: Arc<BTreeMap<String, Arc<Macro>>>,
    globals: Option<Arc<BTreeMap<String, Value>>>,
    imported: HashSet<String>,
}

/// Persistent structured compiler environment, independent of executor state.
#[derive(Clone, Debug, Default)]
pub struct Session {
    legacy: Compiler,
    scope: Scope,
    next_process: usize,
    next_transaction: usize,
    transactions: BTreeMap<String, TransactionId>,
    consumed: HashMap<(ProcessId, Port), Span>,
}

impl Session {
    pub fn new() -> Self {
        Self::default()
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
        let mut bindings = self.legacy.bindings();
        for (name, value) in self.scope.values.iter() {
            match value {
                Value::Literal(text) => bindings.push(format!("{name} := {text:?}")),
                Value::Process(id) => bindings.push(format!("{name} => <program {}>", id.0)),
                Value::Port(id, port) => {
                    bindings.push(format!("{name} := <program {} {port:?}>", id.0))
                }
                Value::Path(_) | Value::Space(_) => {}
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
        context.validate().map_err(|report| vec![report])?;
        let mut candidate = self.clone();
        let mut scope = candidate.scope.clone();
        let mut lower = Lower {
            session: &mut candidate,
            program: hir::Program::default(),
            next_instruction: 0,
            selectors: HashSet::new(),
            expansion: Vec::new(),
            calls: Vec::new(),
        };
        let mut body = Region::default();
        let errors = lower.block(&document.statements, &mut scope, context, None, &mut body);
        if !errors.is_empty() {
            return Err(errors);
        }
        lower.program.body = body;
        let program = lower.program;
        candidate.scope = scope;
        candidate.refresh_legacy();
        *self = candidate;
        Ok(program)
    }

    fn refresh_legacy(&mut self) {
        let mut legacy = Compiler::new();
        for (name, value) in self.scope.values.iter() {
            match value {
                Value::Path(path) => {
                    Arc::make_mut(&mut legacy.aliases).insert(name.clone(), path.clone());
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
    calls: Vec<(Arc<Macro>, Span)>,
}

impl Lower<'_> {
    fn block(
        &mut self,
        statements: &[language::Statement],
        scope: &mut Scope,
        context: &CompileContext,
        transaction: Option<TransactionId>,
        region: &mut Region,
    ) -> Vec<Report> {
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
    ) -> Result<(), Report> {
        use language::StatementKind;
        match &statement.kind {
            StatementKind::Comment(_) => Ok(()),
            StatementKind::Binding { name, op, value } => {
                self.bind(name, *op, value, scope, context, region)
            }
            StatementKind::Transfer {
                source,
                destination,
            } => self.transfer(source, destination, scope, context, statement.span, region),
            StatementKind::Macro {
                name,
                parameters,
                body,
            } => self.define_macro(name, parameters, body, scope, context),
            StatementKind::Call(call) => {
                self.expand_macro(call, scope, context, transaction, region)
            }
            StatementKind::Global(names) => import_globals(names, scope),
            StatementKind::Transaction {
                name,
                isolation,
                body,
            } => {
                if transaction.is_some() {
                    return Err(Report::new("nested transactions need an explicit transaction boundary", statement.span)
                        .help("Keep named transactions at the top level; use commit and rollback as checkpoints within one transaction."));
                }
                check_name(name)?;
                if self.session.transactions.contains_key(&name.text) {
                    return Err(Report::new(format!("transaction '{}' is already defined", name.text), name.span)
                        .help("Use a unique transaction name so named rollback has one unambiguous origin."));
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
                let errors = self.block(body, scope, context, Some(id), &mut child);
                if let Some(error) = errors.into_iter().next() {
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
    ) -> Result<(), Report> {
        let source_value = resolve(source, scope, context)?;
        let destination_value = resolve(destination, scope, context)?;
        let mut owned = Vec::new();
        let source = match source_value {
            Value::Literal(text) => Input::Literal(text),
            Value::Path(mut path) => {
                let pattern = super::take_selector(&mut path.path, &mut self.selectors).map_err(|message| {
                    Report::new(message, source.span).help("Use one final '/<regex>' component; selectors match complete entry names.")
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
                let mode = path_mode(&path.path, mode);
                if matches!(path.backend, Some(Backend::Http { .. } | Backend::Path)) {
                    return Err(Report::new("HTTP(S) and PATH endpoints are read-only", destination.span));
                }
                if path.path.contains(['<', '>']) {
                    return Err(Report::new("selectors are only allowed in the source", destination.span));
                }
                if mode == DestinationMode::ReplaceTree {
                    if path.path.ends_with("/.") || path.path.ends_with("\\.") {
                        path.path.pop();
                    }
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
                .help("Use a writable file path or program::stdin; literal and output streams are read-only.")),
        };
        for (key, site) in &owned {
            if let Some(previous) = self.session.consumed.get(key) {
                return Err(Report::new("a live program stream may have only one owner", *site)
                    .label(*previous, "this stream was already connected here")
                    .help("Write the stream to a file once, then read that replayable file from multiple consumers."));
            }
        }
        self.session.consumed.extend(owned);
        self.emit(
            region,
            span,
            InstructionKind::Transfer {
                source,
                destination,
            },
        );
        Ok(())
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
            macros: scope.macros.clone(),
            source_file: context.source_file.clone(),
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
    ) -> Result<(), Report> {
        let ExprKind::Call { callee, arguments } = &call.kind else {
            return Err(Report::new("expected a macro call", call.span));
        };
        let ExprKind::Path(path) = &callee.kind else {
            return Err(Report::new(
                "a macro call needs an unqualified macro name",
                callee.span,
            ));
        };
        if path.qualifier.is_some() || path.path.contains(['/', '\\']) {
            return Err(Report::new(
                "only declared macros can be called as statements",
                callee.span,
            )
            .help("Bind an external program with 'program => executable(arguments)' instead."));
        }
        let definition = match caller.macros.get(&path.path).cloned() {
            Some(definition) => definition,
            None => {
                if let Some((_, previous)) = self
                    .calls
                    .iter()
                    .find(|(definition, _)| definition.name.text == path.path)
                {
                    return Err(recursion_report(callee.span, *previous));
                }
                return Err(Report::new(format!("unknown macro '{}'", path.path), callee.span)
                    .help("Declare the macro before calling it. A macro body can call only macros declared before its definition."));
            }
        };
        if let Some((_, previous)) = self
            .calls
            .iter()
            .find(|(active, _)| Arc::ptr_eq(active, &definition))
        {
            return Err(recursion_report(callee.span, *previous));
        }
        if arguments.len() != definition.parameters.len() {
            return Err(Report::new(
                format!(
                    "macro '{}' expects {} argument(s), got {}",
                    path.path,
                    definition.parameters.len(),
                    arguments.len()
                ),
                call.span,
            )
            .label(definition.name.span, "macro declared here"));
        }
        if self.calls.len() >= 128 {
            return Err(Report::new("macro expansion nesting limit exceeded", call.span)
                .help("Reduce the nesting of macro calls; at most 128 active expansions are supported."));
        }
        let values = definition
            .parameters
            .iter()
            .zip(arguments)
            .map(|(parameter, argument)| {
                Ok((parameter.text.clone(), resolve(argument, caller, context)?))
            })
            .collect::<Result<BTreeMap<_, _>, Report>>()?;
        let mut scope = Scope {
            values: Arc::new(values),
            macros: definition.macros.clone(),
            globals: Some(definition.globals.clone()),
            imported: HashSet::new(),
        };
        let context = CompileContext {
            pwd: context.pwd.clone(),
            source_file: definition.source_file.clone(),
        };
        self.calls.push((definition.clone(), call.span));
        self.expansion.push(definition.name.span);
        let errors = self.block(&definition.body, &mut scope, &context, transaction, region);
        self.expansion.pop();
        self.calls.pop();
        errors.into_iter().next().map_or(Ok(()), |report| {
            Err(report.label(call.span, format!("while expanding macro '{}'", path.path)))
        })
    }

    fn emit(&mut self, region: &mut Region, span: Span, kind: InstructionKind) {
        let id = hir::InstructionId(self.next_instruction);
        self.next_instruction += 1;
        region.instructions.push(Instruction {
            id,
            kind,
            span: self.calls.first().map_or(span, |(_, site)| *site),
            expansion: self.expansion.clone(),
        });
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
        scope.imported.insert(name.text.clone());
    }
    Ok(())
}

fn resolve(expression: &Expr, scope: &Scope, context: &CompileContext) -> Result<Value, Report> {
    match &expression.kind {
        ExprKind::Literal(text) => Ok(Value::Literal(Arc::from(text.as_str()))),
        ExprKind::Path(path) => resolve_path(path, expression.span, scope, context),
        ExprKind::Call { .. } => Err(Report::new(
            "a call is not a path or stream value",
            expression.span,
        )
        .help(
            "Bind a program with 'name => executable(arguments)' or invoke a macro as a statement.",
        )),
    }
}

fn resolve_path(
    path: &PathExpr,
    span: Span,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Report> {
    if path.path.is_empty() || path.path.contains('\0') {
        return Err(Report::new(
            "paths must be nonempty and cannot contain NUL",
            span,
        ));
    }
    if path.qualifier.is_none()
        && let Some(value) = scope.values.get(&path.path)
    {
        return Ok(value.clone());
    }
    let qualifier = match &path.qualifier {
        Some(name) => match scope.values.get(&name.text) {
            Some(Value::Process(process)) => return process_port(*process, &path.path, span),
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
        Some(Space::Pwd) => resolve_pwd(path, span, scope, context),
        Some(Space::File) => {
            let text = format!("$file::{}", path.path);
            Compiler::new()
                .expand(&text, &OperandSource::unmapped(span), context)
                .map(Value::Path)
        }
        Some(Space::Endpoint(backend)) => qualify(
            resolve_component(&path.path, span, scope, context)?,
            backend,
            span,
        ),
        None => resolve_component(&path.path, span, scope, context),
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
        .map(Value::Path)
}

fn resolve_pwd(
    path: &PathExpr,
    span: Span,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Report> {
    let component = path.path.split(['/', '\\']).next().unwrap_or(&path.path);
    if component == "pwd" {
        let text = format!("$pwd::{}", path.path);
        return Compiler::new()
            .expand(&text, &OperandSource::unmapped(span), context)
            .map(Value::Path);
    }
    let value = resolve_component(&path.path, span, scope, context)?;
    let Value::Path(mut value) = value else {
        return Err(Report::new(
            "$pwd requires a path or path-valued parameter",
            span,
        ));
    };
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

fn qualify(value: Value, backend: Backend, span: Span) -> Result<Value, Report> {
    let Value::Path(mut path) = value else {
        return Err(Report::new(
            "an endpoint qualifier requires a path value",
            span,
        ));
    };
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

fn location(path: Alias, context: &CompileContext, span: Span) -> Result<Location, Report> {
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

fn path_mode(path: &str, fallback: DestinationMode) -> DestinationMode {
    if path.ends_with("/.") || path.ends_with("\\.") {
        DestinationMode::ReplaceTree
    } else if path.ends_with(['/', '\\']) {
        DestinationMode::Container
    } else {
        fallback
    }
}

fn reject_root_replacement(path: &Alias, span: Span) -> Result<(), Report> {
    let value = Path::new(&path.path);
    let native_root = matches!(path.backend, None | Some(Backend::Local))
        && value.has_root()
        && !value
            .components()
            .any(|component| matches!(component, Component::Normal(_) | Component::ParentDir));
    let endpoint_root = path.path.chars().all(|ch| ch == '/' || ch == '\\');
    if native_root || endpoint_root {
        return Err(Report::new("a filesystem root cannot be replaced", span)
            .help("Choose an explicit non-root directory before '/.'; root replacement is never redirected to the working directory."));
    }
    Ok(())
}

fn recursion_report(span: Span, previous: Span) -> Report {
    Report::new("recursive macro expansion is not allowed", span)
        .label(previous, "this expansion is already active")
        .help("Use a finite sequence of macro calls; macros are compiler-time inlining, not runtime recursion.")
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
