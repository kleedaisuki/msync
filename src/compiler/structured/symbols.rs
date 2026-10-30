//! Pure, lexical compile-time functions. No instruction or barrier is emitted.

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

#[derive(Clone, Debug)]
pub(super) struct Symbol {
    name: Name,
    parameters: Vec<Name>,
    body: language::Block,
    values: Arc<BTreeMap<String, Value>>,
    sites: Arc<BTreeMap<String, SourceSpan>>,
    expansions: Arc<ExpansionMap>,
    symbols: Arc<BTreeMap<String, Arc<Symbol>>>,
    source: Arc<Source>,
    source_file: Option<PathBuf>,
}

impl Symbol {
    pub(super) fn signature(&self) -> String {
        format!(
            "symbol {}({})",
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
        if scope.globals.is_some() {
            return Err(self.located(Report::new("a symbol declaration must be at the lexical script level", name.span).help("Nested calls are supported; declare reusable symbols outside a macro or symbol body.")));
        }
        check_name(name).map_err(|error| self.located(error))?;
        let mut names = HashSet::new();
        for parameter in parameters {
            check_name(parameter).map_err(|error| self.located(error))?;
            if !names.insert(&parameter.text) {
                return Err(self.located(Report::new("duplicate symbol parameter", parameter.span)));
            }
        }
        validate_body(body).map_err(|error| self.located(error))?;
        let definition = Symbol {
            name: name.clone(),
            parameters: parameters.to_vec(),
            body: body.to_vec(),
            values: scope.values.clone(),
            sites: scope.sites.clone(),
            expansions: scope.expansions.clone(),
            symbols: scope.symbols.clone(),
            source: self.source.clone(),
            source_file: context.source_file.clone(),
        };
        Arc::make_mut(&mut scope.symbols).insert(name.text.clone(), Arc::new(definition));
        Ok(())
    }
}

fn validate_body(body: &[language::Statement]) -> Result<(), Report> {
    let mut returned = false;
    for statement in body {
        if matches!(statement.kind, language::StatementKind::Comment(_)) {
            continue;
        }
        if returned {
            return Err(Report::new(
                "unreachable statement after symbol return",
                statement.span,
            ));
        }
        match &statement.kind {
            language::StatementKind::Binding { op: BindingOp::Value, .. } | language::StatementKind::Global(_) => {}
            language::StatementKind::Return(_) => returned = true,
            _ => return Err(Report::new("symbol bodies permit only ':=' bindings, global imports and return", statement.span)
                .help("Use pure /, ~, :: expressions and nested symbol calls; transfers, program launches, scheduling and script directives are not symbol operations.")),
        }
    }
    if !returned {
        return Err(Report::new(
            "a symbol must return a value",
            body.last().map_or(Span::default(), |node| node.span),
        )
        .help("End the symbol body with 'return expression'."));
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
                format!("while instantiating symbol '{}'", definition.name.text),
            )
            .label(
                definition.source.at(definition.name.span),
                "symbol declared here",
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
        current_unit: caller.current_unit,
        source: Some(definition.source.clone()),
        symbols: definition.symbols.clone(),
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
        Arc::make_mut(&mut scope.values).insert(parameter.text.clone(), value);
        Arc::make_mut(&mut scope.sites)
            .insert(parameter.text.clone(), call_site.source.at(argument.span));
        Arc::make_mut(&mut scope.expansions).insert(parameter.text.clone(), trace);
    }
    let context = CompileContext {
        pwd: context.pwd.clone(),
        source_file: definition.source_file.clone(),
    };
    run(definition, &mut scope, &context)
}

fn run(definition: &Symbol, scope: &mut Scope, context: &CompileContext) -> Result<Value, Failure> {
    for statement in &definition.body {
        match &statement.kind {
            language::StatementKind::Comment(_) => {}
            language::StatementKind::Global(names) => import_globals(names, scope)?,
            language::StatementKind::Binding {
                name,
                value: expression,
                ..
            } => {
                check_name(name)?;
                let value = diagnostics::resolve(expression, scope, context)?;
                let trace = ExpansionTrace::new(
                    &name.text,
                    &value,
                    definition.source.at(name.span),
                    ExpansionTrace::inputs(expression, scope),
                    ExpansionOrigin::Item,
                );
                Arc::make_mut(&mut scope.values).insert(name.text.clone(), value);
                Arc::make_mut(&mut scope.sites)
                    .insert(name.text.clone(), definition.source.at(expression.span));
                Arc::make_mut(&mut scope.expansions).insert(name.text.clone(), trace);
            }
            language::StatementKind::Return(expression) => {
                return diagnostics::resolve(expression, scope, context);
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
