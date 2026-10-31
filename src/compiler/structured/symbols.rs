//! Lexically captured pure functions, inlined into constant or runtime expressions.

use super::*;

use std::sync::atomic::{AtomicUsize, Ordering};

const DEPTH_LIMIT: usize = 128;
const INVOCATION_LIMIT: usize = 1_000_000;
const VALUE_LIMIT: usize = 8 * 1024 * 1024;

/// One allowance per compilation, shared across all lexical/script environments.
#[derive(Clone, Debug)]
pub(super) struct Budget(Arc<AtomicUsize>);

impl Default for Budget {
    fn default() -> Self {
        Self(Arc::new(AtomicUsize::new(INVOCATION_LIMIT)))
    }
}

impl Budget {
    pub(super) fn used(&self) -> usize {
        INVOCATION_LIMIT - self.0.load(Ordering::Relaxed)
    }

    fn consume(&self, span: Span) -> Result<(), Failure> {
        self.0.try_update(Ordering::Relaxed, Ordering::Relaxed, |remaining| remaining.checked_sub(1))
            .map(|_| ())
            .map_err(|_| Report::new("symbol expansion work budget exceeded", span)
                .help("A compilation permits at most 1,000,000 symbol invocations, including nested calls and loaded scripts.").into())
    }
}

pub(super) fn check_size(bytes: usize, span: Span, scope: &Scope) -> Result<(), Failure> {
    if !scope.symbol_calls.is_empty() && bytes > VALUE_LIMIT {
        return Err(Report::new("symbol value size limit exceeded (8 MiB)", span)
            .help("Compile-time symbol paths and strings must fit within 8 MiB; ordinary transfer payloads are not subject to this limit.").into());
    }
    Ok(())
}

pub(super) fn check_value(value: &Value, span: Span, scope: &Scope) -> Result<(), Failure> {
    let bytes = match value {
        Value::Path(path) => path
            .path
            .len()
            .max(path.relocation.as_ref().map_or(0, |path| path.suffix.len())),
        Value::Literal(text) => text.len(),
        _ => 0,
    };
    check_size(bytes, span, scope)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Flavor {
    Symbol,
    Boolean,
}

impl Flavor {
    fn keyword(self) -> &'static str {
        match self {
            Self::Symbol => "symbol",
            Self::Boolean => "boolean",
        }
    }
}

#[derive(Clone, Debug)]
pub(super) struct Symbol {
    flavor: Flavor,
    name: Name,
    parameters: Vec<Name>,
    body: language::Block,
    values: Arc<imbl::OrdMap<String, Value>>,
    sites: Arc<imbl::OrdMap<String, SourceSpan>>,
    expansions: Arc<ExpansionMap>,
    symbols: Arc<imbl::OrdMap<String, Arc<Symbol>>>,
    source: Arc<Source>,
    source_file: Option<PathBuf>,
}

impl Symbol {
    pub(super) fn signature(&self) -> String {
        format!(
            "{} {}({})",
            self.flavor.keyword(),
            self.name.text,
            self.parameters
                .iter()
                .map(|name| name.text.as_str())
                .collect::<Vec<_>>()
                .join(", ")
        )
    }
}

pub(super) fn is_call(expression: &Expr, scope: &Scope) -> bool {
    let ExprKind::Call { callee, .. } = &expression.kind else {
        return false;
    };
    let ExprKind::Path(path) = &callee.kind else {
        return false;
    };
    path.qualifier.is_none() && scope.symbols.contains_key(&path.path)
}

impl Lower<'_> {
    pub(super) fn define_symbol(
        &self,
        name: &Name,
        parameters: &[Name],
        body: &[language::Statement],
        scope: &mut Scope,
        context: &CompileContext,
    ) -> Result<(), LocatedReport> {
        define(
            Flavor::Symbol,
            name,
            parameters,
            body,
            scope,
            context,
            &self.source,
        )
        .map_err(|error| self.located(error))
    }

    pub(super) fn define_boolean(
        &self,
        name: &Name,
        parameters: &[Name],
        body: &[language::Statement],
        scope: &mut Scope,
        context: &CompileContext,
    ) -> Result<(), LocatedReport> {
        define(
            Flavor::Boolean,
            name,
            parameters,
            body,
            scope,
            context,
            &self.source,
        )
        .map_err(|error| self.located(error))
    }
}

pub(super) fn captured<T: Clone>(
    parents: Option<&Arc<imbl::OrdMap<String, T>>>,
    locals: &Arc<imbl::OrdMap<String, T>>,
) -> Arc<imbl::OrdMap<String, T>> {
    let Some(parents) = parents else {
        return locals.clone();
    };
    let mut values = (**parents).clone();
    values.extend(
        locals
            .iter()
            .map(|(name, value)| (name.clone(), value.clone())),
    );
    Arc::new(values)
}

fn define(
    flavor: Flavor,
    name: &Name,
    parameters: &[Name],
    body: &[language::Statement],
    scope: &mut Scope,
    context: &CompileContext,
    source: &Arc<Source>,
) -> Result<(), Report> {
    check_name(name)?;
    let mut names = HashSet::new();
    for parameter in parameters {
        check_name(parameter)?;
        if !names.insert(&parameter.text) {
            return Err(Report::new(
                format!("duplicate {} parameter", flavor.keyword()),
                parameter.span,
            ));
        }
    }
    validate_body(body, flavor)?;
    let definition = Symbol {
        flavor,
        name: name.clone(),
        parameters: parameters.to_vec(),
        body: body.to_vec(),
        values: captured(scope.globals.as_ref(), &scope.values),
        sites: captured(scope.global_sites.as_ref(), &scope.sites),
        expansions: captured(scope.global_expansions.as_ref(), &scope.expansions),
        symbols: scope.symbols.clone(),
        source: source.clone(),
        source_file: context.source_file.clone(),
    };
    Arc::make_mut(&mut scope.symbols).insert(name.text.clone(), Arc::new(definition));
    scope.local_templates.insert(name.text.clone());
    Ok(())
}

fn validate_body(body: &[language::Statement], flavor: Flavor) -> Result<(), Report> {
    let mut returned = false;
    for statement in body {
        if matches!(statement.kind, language::StatementKind::Comment(_)) {
            continue;
        }
        if returned {
            return Err(Report::new(
                format!("unreachable statement after {} return", flavor.keyword()),
                statement.span,
            ));
        }
        match &statement.kind {
            language::StatementKind::Binding { op: BindingOp::Value | BindingOp::Assign, .. }
            | language::StatementKind::Global(_)
            | language::StatementKind::Drop(_) => {}
            language::StatementKind::Symbol { body, .. } if flavor == Flavor::Symbol => validate_body(body, flavor)?,
            language::StatementKind::Boolean { body, .. } if flavor == Flavor::Boolean => validate_body(body, flavor)?,
            language::StatementKind::Return(_) => returned = true,
            _ => return Err(Report::new(format!("{} bodies permit only pure bindings, global imports, nested {} declarations and return", flavor.keyword(), flavor.keyword()), statement.span)
                .help("Transfers, program launches, scheduling and script directives are not pure function operations.")),
        }
    }
    if !returned {
        return Err(Report::new(
            format!("a {} must return a value", flavor.keyword()),
            body.last().map_or(Span::default(), |node| node.span),
        )
        .help("End the function body with 'return expression'."));
    }
    Ok(())
}

pub(super) fn evaluate(
    call: &Expr,
    caller: &Scope,
    context: &CompileContext,
) -> Result<Value, Failure> {
    let ExprKind::Call { callee, arguments } = &call.kind else {
        unreachable!("symbol call expression");
    };
    let source = caller
        .source
        .clone()
        .unwrap_or_else(|| Source::new("<ast>", ""));
    let definition = definition(callee, arguments.len(), caller, &source)?;
    let call_site = source.at(call.span);
    instantiate(&definition, arguments, caller, context, call_site.clone()).map_err(|failure| {
        let located = failure
            .locate(definition.source.clone())
            .in_context(call_site.clone())
            .label(
                call_site,
                format!(
                    "while instantiating {} '{}'",
                    definition.flavor.keyword(),
                    definition.name.text
                ),
            )
            .label(
                definition.source.at(definition.name.span),
                format!("{} declared here", definition.flavor.keyword()),
            );
        Failure::located(located)
    })
}

fn definition(
    callee: &Expr,
    arity: usize,
    scope: &Scope,
    source: &Arc<Source>,
) -> Result<Arc<Symbol>, Failure> {
    let error = |report| Failure::located(LocatedReport::new(source.clone(), report));
    let ExprKind::Path(path) = &callee.kind else {
        return Err(error(Report::new(
            "a symbol call requires an unqualified symbol name",
            callee.span,
        )));
    };
    if path.qualifier.is_some() {
        return Err(error(
            Report::new(
                "qualified executable calls are not compile-time symbols",
                callee.span,
            )
            .help("Launch programs with 'name => executable(arguments)'."),
        ));
    }
    let definition = scope.symbols.get(&path.path).cloned().ok_or_else(|| {
        let recursive = scope
            .symbol_calls
            .iter()
            .any(|(symbol, _)| symbol.name.text == path.path);
        error(Report::new(
            if recursive {
                "recursive symbol expansion is not allowed".into()
            } else {
                format!("unknown symbol '{}'", path.path)
            },
            callee.span,
        ))
    })?;
    if scope.symbol_calls.len() >= DEPTH_LIMIT {
        return Err(error(Report::new(
            "symbol expansion nesting limit exceeded",
            callee.span,
        )));
    }
    if arity != definition.parameters.len() {
        return Err(Failure::located(
            LocatedReport::new(
                source.clone(),
                Report::new(
                    format!(
                        "symbol '{}' expects {} argument(s), got {}",
                        path.path,
                        definition.parameters.len(),
                        arity
                    ),
                    callee.span,
                ),
            )
            .label(
                definition.source.at(definition.name.span),
                "symbol declared here",
            ),
        ));
    }
    Ok(definition)
}

fn instantiate(
    definition: &Arc<Symbol>,
    arguments: &[Expr],
    caller: &Scope,
    context: &CompileContext,
    call_site: SourceSpan,
) -> Result<Value, Failure> {
    caller
        .symbol_budget
        .consume(call_site.span)
        .map_err(|failure| Failure::located(failure.locate(call_site.source.clone())))?;
    let mut scope = Scope {
        slots: caller.slots.clone(),
        current_unit: caller.current_unit,
        source: Some(definition.source.clone()),
        symbols: definition.symbols.clone(),
        global_symbols: Some(definition.symbols.clone()),
        symbol_calls: caller.symbol_calls.clone(),
        symbol_budget: caller.symbol_budget.clone(),
        globals: Some(definition.values.clone()),
        global_sites: Some(definition.sites.clone()),
        global_expansions: Some(definition.expansions.clone()),
        ..Scope::default()
    };
    scope
        .symbol_calls
        .push((definition.clone(), call_site.clone()));
    let mut arguments_bindings = Vec::new();
    for (parameter, argument) in definition.parameters.iter().zip(arguments) {
        let value = diagnostics::resolve(argument, caller, context)
            .map_err(|failure| Failure::located(failure.locate(call_site.source.clone())))?;
        check_value(&value, argument.span, &scope)
            .map_err(|failure| Failure::located(failure.locate(call_site.source.clone())))?;
        let trace = ExpansionTrace::new(
            &parameter.text,
            &value,
            call_site.source.at(argument.span),
            ExpansionTrace::inputs(argument, caller),
            ExpansionOrigin::SymbolParameter,
        );
        let value = snapshot(value, &scope, &mut arguments_bindings);
        Arc::make_mut(&mut scope.values).insert(parameter.text.clone(), value);
        Arc::make_mut(&mut scope.sites)
            .insert(parameter.text.clone(), call_site.source.at(argument.span));
        Arc::make_mut(&mut scope.expansions).insert(parameter.text.clone(), trace);
    }
    let context = CompileContext {
        pwd: context.pwd.clone(),
        source_file: definition.source_file.clone(),
    };
    let value = run(definition, &mut scope, &context)?;
    scoped(value, arguments_bindings, &context, call_site.span)
}

fn snapshot(
    value: Value,
    scope: &Scope,
    bindings: &mut Vec<(hir::control::SlotId, hir::control::Expr)>,
) -> Value {
    let Value::Dynamic(expression) = value else {
        return value;
    };
    if matches!(expression.kind, hir::control::ExprKind::Slot(_)) {
        return Value::Dynamic(expression);
    }
    let slot = scope.slot();
    let value = Value::Dynamic(hir::control::Expr {
        ty: expression.ty.clone(),
        kind: hir::control::ExprKind::Slot(slot),
    });
    bindings.push((slot, expression));
    value
}

fn scoped(
    value: Value,
    bindings: Vec<(hir::control::SlotId, hir::control::Expr)>,
    context: &CompileContext,
    span: Span,
) -> Result<Value, Failure> {
    if bindings.is_empty() {
        return Ok(value);
    }
    let mut body = v6::scalar(value, context, span)?;
    for (slot, value) in bindings.into_iter().rev() {
        body = hir::control::Expr {
            ty: body.ty.clone(),
            kind: hir::control::ExprKind::Let {
                slot,
                value: Box::new(value),
                body: Box::new(body),
            },
        };
    }
    Ok(Value::Dynamic(body))
}

fn run(definition: &Symbol, scope: &mut Scope, context: &CompileContext) -> Result<Value, Failure> {
    let mut bindings = Vec::new();
    for statement in &definition.body {
        match &statement.kind {
            language::StatementKind::Comment(_) => {}
            language::StatementKind::Global(names) => import_globals(names, scope)?,
            language::StatementKind::Drop(names) => v6::drop_bindings(names, scope)?,
            language::StatementKind::Binding {
                name,
                op,
                value: expression,
            } => {
                check_name(name)?;
                let value = diagnostics::resolve(expression, scope, context)?;
                v6::check_assignment(*op, &value, expression.span)?;
                if *op == BindingOp::Assign {
                    v6::check_reassignment(name, &value, scope, context)?;
                }
                let trace = ExpansionTrace::new(
                    &name.text,
                    &value,
                    definition.source.at(name.span),
                    ExpansionTrace::inputs(expression, scope),
                    ExpansionOrigin::Item,
                );
                let value = snapshot(value, scope, &mut bindings);
                Arc::make_mut(&mut scope.values).insert(name.text.clone(), value);
                Arc::make_mut(&mut scope.sites)
                    .insert(name.text.clone(), definition.source.at(expression.span));
                Arc::make_mut(&mut scope.expansions).insert(name.text.clone(), trace);
            }
            language::StatementKind::Symbol {
                name,
                parameters,
                body,
            } => {
                define(
                    Flavor::Symbol,
                    name,
                    parameters,
                    body,
                    scope,
                    context,
                    &definition.source,
                )?;
            }
            language::StatementKind::Boolean {
                name,
                parameters,
                body,
            } => {
                define(
                    Flavor::Boolean,
                    name,
                    parameters,
                    body,
                    scope,
                    context,
                    &definition.source,
                )?;
            }
            language::StatementKind::Return(expression) => {
                let value = diagnostics::resolve(expression, scope, context)?;
                if definition.flavor == Flavor::Boolean && !v6::is_bool(&value) {
                    return Err(Report::new(
                        "a boolean function must return bool",
                        expression.span,
                    )
                    .into());
                }
                return scoped(value, bindings, context, expression.span);
            }
            _ => unreachable!("symbol declaration validation"),
        }
    }
    unreachable!("validated mandatory return")
}

/// Retain semantic definition contexts for foreign module anchors. Existing
/// directive-relative entries win, so current includes remain relocatable.
pub(super) fn complete_source_modules(
    scope: &Scope,
    context: &CompileContext,
    relocations: &[Relocation],
    modules: &mut Vec<crate::object::SourceModule>,
) {
    use crate::object::{SourceModule, SourceModulePath};
    let main = context
        .source_file
        .as_ref()
        .map(|path| path.to_string_lossy().into_owned());
    let mut needed = BTreeSet::new();
    for relocation in relocations {
        retain_module_anchor(&relocation.path, main.as_deref(), &mut needed);
    }
    for module in modules.iter() {
        if let SourceModulePath::Contextual { path } = &module.path {
            retain_module_anchor(path, main.as_deref(), &mut needed);
        }
    }
    for (_, value) in scope
        .values
        .iter()
        .filter(|(name, _)| defined_here(scope, name))
    {
        if let Value::Path(path) = value {
            let relocation = path_relocation(
                path.backend.as_ref().unwrap_or(&Backend::Local),
                &path.path,
                captured_path(path, scope).relocation,
            );
            if let Some(path) = relocation {
                retain_module_anchor(&path, main.as_deref(), &mut needed);
            }
        }
    }
    let mut retained = modules
        .iter()
        .map(|module| module.name.clone())
        .collect::<BTreeSet<_>>();
    let mut symbol_queue = scope.symbols.values().cloned().collect::<Vec<_>>();
    let mut macro_queue = scope.macros.values().cloned().collect::<Vec<_>>();
    let mut seen_symbols = HashSet::new();
    let mut seen_macros = HashSet::new();
    let mut files = Vec::new();
    while let Some(definition) = macro_queue.pop() {
        if !seen_macros.insert(Arc::as_ptr(&definition)) {
            continue;
        }
        files.extend(definition.source_file.clone());
        symbol_queue.extend(definition.symbols.values().cloned());
        macro_queue.extend(definition.macros.values().cloned());
    }
    while let Some(definition) = symbol_queue.pop() {
        if !seen_symbols.insert(Arc::as_ptr(&definition)) {
            continue;
        }
        files.extend(definition.source_file.clone());
        symbol_queue.extend(definition.symbols.values().cloned());
    }
    for file in files {
        let name = file.to_string_lossy().into_owned();
        if needed.contains(&name) && retained.insert(name.clone()) {
            modules.push(SourceModule {
                name,
                path: SourceModulePath::Absolute {
                    path: file.to_string_lossy().into_owned(),
                },
            });
        }
    }
    modules.sort_by(|left, right| left.name.cmp(&right.name));
}

fn retain_module_anchor(path: &RelocPath, main: Option<&str>, needed: &mut BTreeSet<String>) {
    if let Anchor::ModuleFile { module } | Anchor::ModuleDirectory { module } = &path.anchor
        && Some(module.as_str()) != main
    {
        needed.insert(module.clone());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn acyclic_calls_share_work_allowance_and_next_compilation_resets_it() {
        let mut session = Session::new();
        let context = CompileContext {
            pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
            source_file: None,
        };
        let mut code = "symbol f0():\n    return leaf\n".to_owned();
        for level in 1..8 {
            code.push_str(&format!(
                "symbol f{level}():\n    discarded := f{}()\n    return f{}()\n",
                level - 1,
                level - 1
            ));
        }
        session
            .compile(&language::parse(&code).unwrap(), &context)
            .unwrap();
        session.scope.symbol_budget = Budget(Arc::new(AtomicUsize::new(12)));
        let call = language::parse("result := f7()\n").unwrap();
        let language::StatementKind::Binding { value, .. } = &call.statements[0].kind else {
            panic!("expected binding");
        };
        let error = evaluate(value, &session.scope, &context)
            .unwrap_err()
            .locate(Source::new("<budget>", "result := f7()\n"));
        assert!(
            error
                .report
                .diagnostic
                .message
                .contains("work budget exceeded")
        );
        assert_eq!(session.scope.symbol_budget.0.load(Ordering::Relaxed), 0);
        session
            .compile(&language::parse("result := f1()\n").unwrap(), &context)
            .unwrap();
        assert_eq!(
            session.scope.symbol_budget.0.load(Ordering::Relaxed),
            INVOCATION_LIMIT - 3
        );
    }
}
