//! Lexical namespace construction and member lookup.

use super::*;

impl Lower<'_> {
    pub(super) fn define_namespace(
        &mut self,
        name: &Name,
        body: &[language::Statement],
        scope: &mut Scope,
        context: &CompileContext,
        region: &mut Region,
    ) -> Result<(), LocatedReport> {
        check_name(name).map_err(|error| self.located(error))?;
        let mut members = scope.clone();
        members.namespace_context = Some(context.clone());
        if let Some(error) = self
            .block(body, &mut members, context, region)
            .into_iter()
            .next()
        {
            return Err(error);
        }
        let names = declared_names(body);
        members.values = exported(&members.values, &names);
        members.symbols = exported(&members.symbols, &names);
        members.macros = exported(&members.macros, &names);
        members.natives = exported(&members.natives, &names);
        scope.cells.remove(&name.text);
        Arc::make_mut(&mut scope.values)
            .insert(name.text.clone(), Value::Module(Arc::new(members)));
        Arc::make_mut(&mut scope.sites).insert(name.text.clone(), self.source.at(name.span));
        if let Some(unit) = scope.current_unit {
            Arc::make_mut(&mut scope.defined_units).insert(name.text.clone(), unit);
        }
        Ok(())
    }
}

fn exported<T: Clone>(
    values: &Arc<imbl::OrdMap<String, T>>,
    names: &BTreeSet<String>,
) -> Arc<imbl::OrdMap<String, T>> {
    Arc::new(
        values
            .iter()
            .filter(|(name, _)| names.contains(*name))
            .map(|(name, value)| (name.clone(), value.clone()))
            .collect(),
    )
}

fn declared_names(body: &[language::Statement]) -> BTreeSet<String> {
    body.iter()
        .filter_map(|statement| match &statement.kind {
            language::StatementKind::Binding { name, .. }
            | language::StatementKind::Scope { name, .. }
            | language::StatementKind::Native { name, .. }
            | language::StatementKind::Macro { name, .. }
            | language::StatementKind::Symbol { name, .. }
            | language::StatementKind::Boolean { name, .. } => Some(name.text.clone()),
            _ => None,
        })
        .collect()
}

pub(super) fn select(
    module: &Scope,
    item: &Expr,
    context: &CompileContext,
) -> Result<Value, Failure> {
    if let ExprKind::Path(path) = &item.kind {
        let name = path.path.split(['/', '\\']).next().unwrap_or(&path.path);
        if path.qualifier.is_none() && !module.values.contains_key(name) {
            return Err(Report::new(format!("scope has no member '{name}'"), item.span).into());
        }
    }
    resolve(
        item,
        module,
        module.namespace_context.as_ref().unwrap_or(context),
    )
}

pub(super) fn materialize(
    value: Value,
    scope: &Scope,
    context: &CompileContext,
    span: Span,
) -> Result<Value, Failure> {
    if let Value::Space(Space::Api(api)) = &value
        && let Some(name) = api.module_name()
    {
        return natives::standard_module(name, scope, context, span);
    }
    Ok(value)
}

pub(super) fn selected_call(namespace: &Expr, item: &Expr, operator: Span) -> Option<Expr> {
    let ExprKind::Call { callee, arguments } = &item.kind else {
        return None;
    };
    Some(Expr {
        kind: ExprKind::Call {
            callee: Box::new(Expr {
                kind: ExprKind::Select {
                    namespace: Box::new(namespace.clone()),
                    item: callee.clone(),
                    operator,
                },
                span: item.span,
                source: item.source.clone(),
            }),
            arguments: arguments.clone(),
        },
        span: item.span,
        source: item.source.clone(),
    })
}

/// Resolve a qualified callee while retaining the caller's argument environment.
pub(super) fn callee(
    expression: &Expr,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Option<(Expr, Scope)>, Failure> {
    match &expression.kind {
        ExprKind::Select {
            namespace, item, ..
        } => {
            let Value::Module(module) = materialize(
                resolve(namespace, scope, context)?,
                scope,
                context,
                namespace.span,
            )?
            else {
                return Ok(None);
            };
            Ok(Some(((**item).clone(), (*module).clone())))
        }
        ExprKind::Path(path) => {
            let Some(qualifier) = &path.qualifier else {
                return Ok(None);
            };
            let value = if let Some(value) = scope.values.get(&qualifier.text) {
                value.clone()
            } else if let Some(space) = Compiler::builtin_space(&qualifier.text) {
                Value::Space(space)
            } else {
                return Ok(None);
            };
            let Value::Module(module) = materialize(value, scope, context, qualifier.span)? else {
                return Ok(None);
            };
            let mut item = expression.clone();
            let ExprKind::Path(path) = &mut item.kind else {
                unreachable!()
            };
            path.qualifier = None;
            Ok(Some((item, (*module).clone())))
        }
        _ => Ok(None),
    }
}

/// Dispatch a selected source member after its namespace has been synthesized.
pub(super) fn call(
    module: &Arc<Scope>,
    expression: &Expr,
    caller: &Scope,
    context: &CompileContext,
) -> Option<Result<Value, Failure>> {
    let ExprKind::Call { callee, .. } = &expression.kind else {
        return None;
    };
    let ExprKind::Path(path) = &callee.kind else {
        return None;
    };
    if path.qualifier.is_some() {
        return None;
    }
    if let Some(native) = module.natives.get(&path.path) {
        let mut scope = caller.clone();
        Arc::make_mut(&mut scope.natives).insert(path.path.clone(), native.clone());
        return natives::resolve_call(expression, &scope, context);
    }
    if let Some(symbol) = module.symbols.get(&path.path) {
        let mut scope = caller.clone();
        Arc::make_mut(&mut scope.symbols).insert(path.path.clone(), symbol.clone());
        return Some(symbols::evaluate(expression, &scope, context));
    }
    None
}
