//! Static range specialization. Views retain typed callbacks, not syntax or IO.

use super::*;
use crate::hir::control as ctl;

pub(super) fn selector(
    root: Value,
    selector: SourceSelector,
    context: &CompileContext,
) -> Result<Value, Failure> {
    let span = selector.span;
    if matches!(&root, Value::Path(path) if matches!(path.backend, Some(Backend::Http { .. } | Backend::Path | Backend::Tcp { .. })))
    {
        return Err(Report::new("ranges require a directory-capable endpoint", span).into());
    }
    let root = v6::scalar(root, context, span)?;
    if root.ty != ctl::Type::Path {
        return Err(Report::new("a selector requires a directory path", span).into());
    }
    Ok(Value::Range(ctl::RangeExpr {
        source: ctl::RangeSource::Regex {
            root: Box::new(root),
            pattern: selector.pattern,
        },
        stages: Vec::new(),
        item_type: ctl::Type::Path,
    }))
}

pub(super) fn literal(
    elements: &[Expr],
    scope: &Scope,
    context: &CompileContext,
    span: Span,
) -> Result<Value, Failure> {
    let mut values = Vec::with_capacity(elements.len());
    for element in elements {
        let value = v6::scalar(resolve(element, scope, context)?, context, element.span)?;
        if !value.ty.is_range_item() {
            return Err(Report::new(
                "range elements must be paths, strings, bool values or scopes",
                element.span,
            )
            .into());
        }
        values.push(value);
    }
    let item_type = if values.is_empty() {
        ctl::Type::Path
    } else {
        ctl::Type::join(values.iter().map(|value| value.ty.clone())).ok_or_else(|| {
            Report::new(
                "a scope range cannot mix scopes with paths, strings or bool values",
                span,
            )
        })?
    };
    if !item_type.is_range_item() {
        return Err(Report::new("invalid range element type", span).into());
    }
    Ok(Value::Range(ctl::RangeExpr {
        source: ctl::RangeSource::Values(values),
        stages: Vec::new(),
        item_type,
    }))
}

pub(super) fn resolve_pipe(
    left: &Expr,
    right: &Expr,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Value, Failure> {
    let Value::Range(range) = resolve(left, scope, context)? else {
        return Err(Report::new("the left operand of '|' must be a range", left.span).into());
    };
    let (callee, arguments) = call_parts(right).unwrap_or((right, &[][..]));
    let name = adaptor_name(callee, scope, context, true).ok_or_else(|| {
        Report::new(
            "a range pipeline requires a named range adaptor",
            right.span,
        )
    })?;
    apply(range, &name, arguments, scope, context, right.span)
}

fn adaptor_name(
    expression: &Expr,
    scope: &Scope,
    context: &CompileContext,
    allow_bare: bool,
) -> Option<String> {
    match &expression.kind {
        ExprKind::Path(path) => match &path.qualifier {
            None if allow_bare => Some(path.path.clone()),
            Some(qualifier) if api_name(&qualifier.text, scope) => Some(path.path.clone()),
            _ => None,
        },
        ExprKind::Select {
            namespace, item, ..
        } => {
            if api_namespace(namespace, scope, context) {
                adaptor_name(item, scope, context, true)
            } else {
                None
            }
        }
        ExprKind::Call { callee, .. } => adaptor_name(callee, scope, context, allow_bare),
        _ => None,
    }
}

fn apply(
    mut range: ctl::RangeExpr,
    name: &str,
    arguments: &[Expr],
    scope: &Scope,
    context: &CompileContext,
    span: Span,
) -> Result<Value, Failure> {
    match name {
        "all" | "as_const" | "as_rvalue" | "common" => {
            arity(name, arguments, 0, span)?;
        }
        "take" | "drop" => {
            arity(name, arguments, 1, span)?;
            let count = count(&arguments[0], scope, context)?;
            range.stages.push(if name == "take" { ctl::RangeStage::Take(count) } else { ctl::RangeStage::Drop(count) });
        }
        "stride" | "step" => {
            arity(name, arguments, 1, span)?;
            let count = count(&arguments[0], scope, context)?;
            if count == 0 {
                return Err(Report::new("range stride must be positive", arguments[0].span).into());
            }
            range.stages.push(ctl::RangeStage::Stride(count));
        }
        "unique" => {
            arity(name, arguments, 0, span)?;
            range.stages.push(ctl::RangeStage::Unique);
        }
        "reverse" => {
            arity(name, arguments, 0, span)?;
            range.stages.push(ctl::RangeStage::Reverse);
        }
        "filter" | "transform" | "take_while" | "drop_while" => {
            arity(name, arguments, 1, span)?;
            let (slot, expression) = callback(&arguments[0], &range.item_type, scope, context)?;
            let stage = match name {
                "transform" => {
                    if !expression.ty.is_range_item() {
                        return Err(Report::new("range transforms must return a path, string, bool or scope", arguments[0].span).into());
                    }
                    range.item_type = expression.ty.clone();
                    ctl::RangeStage::Transform { slot, expression }
                }
                "filter" => {
                    predicate(&expression, arguments[0].span)?;
                    ctl::RangeStage::Filter { slot, predicate: expression }
                }
                "take_while" => {
                    predicate(&expression, arguments[0].span)?;
                    ctl::RangeStage::TakeWhile { slot, predicate: expression }
                }
                _ => {
                    predicate(&expression, arguments[0].span)?;
                    ctl::RangeStage::DropWhile { slot, predicate: expression }
                }
            };
            range.stages.push(stage);
        }
        "empty" | "front" | "first" | "back" | "last" => {
            arity(name, arguments, 0, span)?;
            let (query, ty) = match name {
                "empty" => (ctl::RangeQuery::Empty, ctl::Type::Bool),
                "front" | "first" => (ctl::RangeQuery::First, range.item_type.clone()),
                _ => (ctl::RangeQuery::Last, range.item_type.clone()),
            };
            return Ok(query_value(range, query, ty));
        }
        "any_of" | "all_of" | "none_of" | "any" | "none" | "find_if" => {
            arity(name, arguments, 1, span)?;
            let (slot, expression) = callback(&arguments[0], &range.item_type, scope, context)?;
            predicate(&expression, arguments[0].span)?;
            let predicate = Box::new(expression);
            let (query, ty) = match name {
                "any_of" | "any" => (ctl::RangeQuery::Any { slot, predicate }, ctl::Type::Bool),
                "all_of" => (ctl::RangeQuery::All { slot, predicate }, ctl::Type::Bool),
                "none_of" | "none" => (ctl::RangeQuery::None { slot, predicate }, ctl::Type::Bool),
                _ => (ctl::RangeQuery::Find { slot, predicate }, range.item_type.clone()),
            };
            return Ok(query_value(range, query, ty));
        }
        "contains" | "find" => {
            arity(name, arguments, 1, span)?;
            let value = resolve(&arguments[0], scope, context)?;
            let value = v6::scalar(value, context, arguments[0].span)?;
            if !range.item_type.accepts(&value.ty) {
                return Err(Report::new("a range search value must match the range element type", arguments[0].span).into());
            }
            let (query, ty) = if name == "contains" {
                (ctl::RangeQuery::Contains { value: Box::new(value) }, ctl::Type::Bool)
            } else {
                (ctl::RangeQuery::FindValue { value: Box::new(value) }, range.item_type.clone())
            };
            return Ok(query_value(range, query, ty));
        }
        _ => return Err(Report::new(format!("unknown range adaptor '{name}'"), span)
            .help("Use filter, transform, take, drop, reverse, take_while, drop_while, empty, front, back, any_of, all_of, none_of or find_if.").into()),
    }
    Ok(Value::Range(range))
}

fn query_value(range: ctl::RangeExpr, query: ctl::RangeQuery, ty: ctl::Type) -> Value {
    Value::Dynamic(ctl::Expr {
        ty,
        kind: ctl::ExprKind::RangeQuery {
            range: Box::new(range),
            query,
        },
    })
}

fn callback(
    function: &Expr,
    ty: &ctl::Type,
    scope: &Scope,
    context: &CompileContext,
) -> Result<(ctl::SlotId, ctl::Expr), Failure> {
    let slot = scope.slot();
    let mut callback_scope = scope.clone();
    let name = format!("__range_item_{}", slot.0);
    Arc::make_mut(&mut callback_scope.values).insert(
        name.clone(),
        Value::Dynamic(ctl::Expr {
            ty: ty.clone(),
            kind: ctl::ExprKind::Slot(slot),
        }),
    );
    let argument = Expr {
        kind: ExprKind::Path(PathExpr {
            qualifier: None,
            path: name,
            mode: DestinationMode::Exact,
        }),
        span: function.span,
        source: function.source.clone(),
    };
    let call = Expr {
        kind: ExprKind::Call {
            callee: Box::new(function.clone()),
            arguments: vec![argument],
        },
        span: function.span,
        source: function.source.clone(),
    };
    let value = resolve(&call, &callback_scope, context)?;
    Ok((slot, v6::scalar(value, context, function.span)?))
}

fn predicate(expression: &ctl::Expr, span: Span) -> Result<(), Failure> {
    if expression.ty == ctl::Type::Bool {
        return Ok(());
    }
    Err(Report::new("a range predicate must return bool", span).into())
}

fn arity(name: &str, arguments: &[Expr], expected: usize, span: Span) -> Result<(), Failure> {
    if arguments.len() == expected {
        return Ok(());
    }
    Err(Report::new(
        format!(
            "range adaptor '{name}' expects {expected} argument(s), got {}",
            arguments.len()
        ),
        span,
    )
    .into())
}

fn count(expression: &Expr, scope: &Scope, context: &CompileContext) -> Result<usize, Failure> {
    let text = match &expression.kind {
        ExprKind::Path(path)
            if path.qualifier.is_none() && path.path.bytes().all(|byte| byte.is_ascii_digit()) =>
        {
            path.path.clone()
        }
        _ => match resolve(expression, scope, context)? {
            Value::Literal(text) => text.to_string(),
            Value::Path(path)
                if path.backend.is_none()
                    && path.path.bytes().all(|byte| byte.is_ascii_digit()) =>
            {
                path.path
            }
            _ => {
                return Err(Report::new(
                    "range take/drop counts must be nonnegative decimal constants",
                    expression.span,
                )
                .into());
            }
        },
    };
    text.parse().map_err(|_| {
        Report::new(
            "range count is not a supported nonnegative integer",
            expression.span,
        )
        .into()
    })
}

/// Recognize only explicit standard-library namespaces so user functions with
/// names such as `filter` or `single` retain their ordinary lexical meaning.
pub(super) fn resolve_call(
    expression: &Expr,
    scope: &Scope,
    context: &CompileContext,
) -> Option<Result<Value, Failure>> {
    let (callee, arguments) = call_parts(expression)?;
    if namespaces::callee(callee, scope, context).ok().flatten().is_some_and(|(callee, module)| {
        matches!(&callee.kind, ExprKind::Path(path) if module.builtin_api == Some(crate::relocatable::BuiltinScope::Ranges) && module.symbols.contains_key(&path.path))
    }) { return None }
    let name = adaptor_name(callee, scope, context, false)?;
    Some(resolve_named_call(
        &name,
        arguments,
        scope,
        context,
        expression.span,
    ))
}

pub(super) fn resolve_legacy_selection(
    namespace: &Expr,
    item: &Expr,
    scope: &Scope,
    context: &CompileContext,
) -> Option<Result<Value, Failure>> {
    let ExprKind::Path(path) = &namespace.kind else {
        return None;
    };
    if path.qualifier.is_some()
        || !matches!(path.path.as_str(), "views" | "ranges")
        || scope.values.contains_key(&path.path)
    {
        return None;
    }
    let (name, arguments) = selected_arguments(item)?;
    Some(resolve_named_call(
        name, arguments, scope, context, item.span,
    ))
}

pub(super) fn resolve_api_selection(
    namespace: &Value,
    item: &Expr,
    scope: &Scope,
    context: &CompileContext,
) -> Option<Result<Value, Failure>> {
    if !is_api(namespace) {
        return None;
    }
    Some(match selected_arguments(item) {
        Some((name, arguments)) => resolve_named_call(name, arguments, scope, context, item.span),
        None => Err(Report::new(
            "a built-in API scope requires a named API member call",
            item.span,
        )
        .into()),
    })
}

fn selected_arguments(item: &Expr) -> Option<(&str, &[Expr])> {
    let ExprKind::Call { callee, arguments } = &item.kind else {
        return None;
    };
    let ExprKind::Path(path) = &callee.kind else {
        return None;
    };
    path.qualifier
        .is_none()
        .then_some((path.path.as_str(), arguments))
}

fn call_parts(expression: &Expr) -> Option<(&Expr, &[Expr])> {
    match &expression.kind {
        ExprKind::Call { callee, arguments } => Some((callee, arguments)),
        ExprKind::Select { item, .. } => {
            let ExprKind::Call { arguments, .. } = &item.kind else {
                return None;
            };
            Some((expression, arguments))
        }
        _ => None,
    }
}

fn resolve_named_call(
    name: &str,
    arguments: &[Expr],
    scope: &Scope,
    context: &CompileContext,
    span: Span,
) -> Result<Value, Failure> {
    if matches!(name, "of" | "single") {
        if name == "single" {
            arity(name, arguments, 1, span)?;
        }
        return literal(arguments, scope, context, span);
    }
    let Some((range, arguments)) = arguments.split_first() else {
        return Err(Report::new(
            format!("direct range adaptor '{name}' requires a range argument"),
            span,
        )
        .into());
    };
    let Value::Range(range) = resolve(range, scope, context)? else {
        return Err(Report::new("the first argument must be a range", range.span).into());
    };
    apply(range, name, arguments, scope, context, span)
}

fn is_api(value: &Value) -> bool {
    use crate::relocatable::BuiltinScope;
    match value {
        Value::Space(Space::Api(BuiltinScope::View | BuiltinScope::Ranges)) => true,
        Value::Module(module) => module.builtin_api == Some(BuiltinScope::Ranges),
        _ => false,
    }
}

fn api_name(name: &str, scope: &Scope) -> bool {
    if let Some(value) = scope.values.get(name) {
        return is_api(value);
    }
    if matches!(name, "views" | "ranges") {
        return true;
    }
    Compiler::builtin_space(name).is_some_and(|space| is_api(&Value::Space(space)))
}

fn api_namespace(expression: &Expr, scope: &Scope, context: &CompileContext) -> bool {
    if let ExprKind::Path(path) = &expression.kind
        && path.qualifier.is_none()
    {
        return api_name(&path.path, scope);
    }
    resolve(expression, scope, context).is_ok_and(|value| is_api(&value))
}

/// Compiler-native range parameters retain their dependent, specialized types.
pub(super) fn native_arity(name: &str) -> Option<usize> {
    match name {
        "of" | "single" | "all" | "as_const" | "as_rvalue" | "common" | "reverse" | "unique"
        | "empty" | "front" | "first" | "back" | "last" => Some(1),
        "take" | "drop" | "stride" | "step" | "filter" | "transform" | "take_while"
        | "drop_while" | "contains" | "find" | "any" | "any_of" | "all_of" | "none" | "none_of"
        | "find_if" => Some(2),
        _ => None,
    }
}

pub(super) fn resolve_native(
    implementation: &str,
    arguments: &[Expr],
    scope: &Scope,
    context: &CompileContext,
    span: Span,
) -> Result<Value, Failure> {
    resolve_named_call(
        implementation
            .strip_prefix("ranges.")
            .expect("range native identity"),
        arguments,
        scope,
        context,
        span,
    )
}
