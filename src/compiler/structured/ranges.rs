//! Static range specialization. Views retain typed callbacks, not syntax or IO.

use super::*;
use crate::hir::control as ctl;

pub(super) fn selector(
    mut path: PathValue,
    scope: &Scope,
    context: &CompileContext,
    span: Span,
) -> Result<Value, Failure> {
    let original = path.path.len();
    let pattern = super::super::take_selector(&mut path.path, &mut HashSet::new())
        .map_err(|message| Report::new(message, span))?;
    let Some(pattern) = pattern else {
        return Ok(Value::Path(path));
    };
    if matches!(
        path.backend,
        Some(Backend::Http { .. } | Backend::Path | Backend::Tcp { .. })
    ) {
        return Err(Report::new("ranges require a directory-capable endpoint", span).into());
    }
    if let Some(relocation) = &mut path.relocation {
        relocation.suffix.truncate(
            relocation
                .suffix
                .len()
                .saturating_sub(original - path.path.len()),
        );
    }
    let root = v6::scalar(Value::Path(path), context, span)?;
    let _ = scope;
    Ok(Value::Range(ctl::RangeExpr {
        source: ctl::RangeSource::Regex {
            root: Box::new(root),
            pattern,
        },
        stages: Vec::new(),
        item_type: ctl::Type::Path,
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
    let (callee, arguments) = match &right.kind {
        ExprKind::Call { callee, arguments } => (&**callee, arguments.as_slice()),
        _ => (right, &[][..]),
    };
    let name = adaptor_name(callee).ok_or_else(|| {
        Report::new(
            "a range pipeline requires a named range adaptor",
            right.span,
        )
    })?;
    apply(range, &name, arguments, scope, context, right.span)
}

fn adaptor_name(expression: &Expr) -> Option<String> {
    match &expression.kind {
        ExprKind::Path(path) => match &path.qualifier {
            None => Some(path.path.clone()),
            Some(qualifier) if matches!(qualifier.text.as_str(), "views" | "ranges") => {
                Some(path.path.clone())
            }
            _ => None,
        },
        ExprKind::Select {
            namespace, item, ..
        } => {
            let ExprKind::Path(namespace) = &namespace.kind else {
                return None;
            };
            let ExprKind::Path(item) = &item.kind else {
                return None;
            };
            (namespace.qualifier.is_none()
                && matches!(namespace.path.as_str(), "views" | "ranges")
                && item.qualifier.is_none())
            .then(|| item.path.clone())
        }
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
                    if !matches!(expression.ty, ctl::Type::Path | ctl::Type::String) {
                        return Err(Report::new("range transforms must return a path or string", arguments[0].span).into());
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
            if value.ty != range.item_type {
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
    let value = symbols::evaluate(&call, &callback_scope, context)?;
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
    let ExprKind::Call { callee, arguments } = &expression.kind else {
        return None;
    };
    if !qualified_adaptor(callee) {
        return None;
    }
    let name = adaptor_name(callee)?;
    Some(resolve_named_call(
        &name,
        arguments,
        scope,
        context,
        expression.span,
    ))
}

fn qualified_adaptor(callee: &Expr) -> bool {
    match &callee.kind {
        ExprKind::Path(path) => path
            .qualifier
            .as_ref()
            .is_some_and(|qualifier| matches!(qualifier.text.as_str(), "views" | "ranges")),
        ExprKind::Select { namespace, .. } => {
            matches!(&namespace.kind, ExprKind::Path(path) if path.qualifier.is_none() && matches!(path.path.as_str(), "views" | "ranges"))
        }
        _ => false,
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
        let values = arguments
            .iter()
            .map(|argument| {
                let value = resolve(argument, scope, context)?;
                let value = v6::scalar(value, context, argument.span)?;
                if !matches!(value.ty, ctl::Type::Path | ctl::Type::String) {
                    return Err(Report::new(
                        "range elements must be paths or strings",
                        argument.span,
                    )
                    .into());
                }
                Ok(value)
            })
            .collect::<Result<Vec<_>, Failure>>()?;
        let item_type = values
            .first()
            .map_or(ctl::Type::Path, |value| value.ty.clone());
        if values.iter().any(|value| value.ty != item_type) {
            return Err(Report::new("range elements must share one static type", span).into());
        }
        return Ok(Value::Range(ctl::RangeExpr {
            source: ctl::RangeSource::Values(values),
            stages: Vec::new(),
            item_type,
        }));
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
