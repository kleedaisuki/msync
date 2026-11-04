//! Native declarations bind names to typed host capabilities, never executable text.

use super::*;
use crate::stdlib::{NativeParam, NativeResultType};

#[derive(Clone, Debug)]
pub(super) struct Native {
    implementation: String,
}

fn declared_result(result: &language::NativeType) -> Option<NativeResultType> {
    use hir::control::Type as T;
    use language::NativeType as N;
    Some(match result {
        N::Inferred => return None,
        N::Void => NativeResultType::Void,
        N::Bool => NativeResultType::Scalar(T::Bool),
        N::String => NativeResultType::Scalar(T::String),
        N::Path => NativeResultType::Scalar(T::Path),
        N::Scope => NativeResultType::Scalar(T::Space),
        N::Range(item) => match declared_result(item)? {
            NativeResultType::Scalar(ty) => NativeResultType::Range(ty),
            _ => return None,
        },
    })
}

pub(super) fn define(
    name: &Name,
    parameters: &[Name],
    result: &language::NativeType,
    implementation: &str,
    scope: &mut Scope,
) -> Result<(), Report> {
    check_name(name)?;
    if let Some(operation) = implementation.strip_prefix("ranges.") {
        let arity = ranges::native_arity(operation)
            .ok_or_else(|| Report::new(format!("unknown range native '{operation}'"), name.span))?;
        if !matches!(result, language::NativeType::Inferred)
            || parameters.len() != arity
            || (operation == "of"
                && parameters
                    .first()
                    .is_none_or(|parameter| !parameter.text.ends_with("...")))
        {
            return Err(Report::new(
                "range native declarations require inferred results and their registered parameter signature",
                name.span,
            ));
        }
    } else {
        let signature = crate::stdlib::signature(implementation).ok_or_else(|| {
            Report::new(
                format!("unknown native implementation '{implementation}'"),
                name.span,
            )
        })?;
        if signature.parameters.len() != parameters.len()
            || (!matches!(result, language::NativeType::Inferred)
                && declared_result(result).as_ref() != Some(&signature.result))
        {
            return Err(Report::new(
                format!(
                    "native '{implementation}' declaration does not match its implementation signature"
                ),
                name.span,
            ));
        }
    }
    let mut names = HashSet::new();
    for parameter in parameters {
        if implementation == "ranges.of" {
            let mut parameter = parameter.clone();
            parameter.text = parameter.text.trim_end_matches("...").to_owned();
            check_name(&parameter)?;
        } else {
            check_name(parameter)?;
        }

        if !names.insert(&parameter.text) {
            return Err(Report::new("duplicate native parameter", parameter.span));
        }
    }
    Arc::make_mut(&mut scope.natives).insert(
        name.text.clone(),
        Arc::new(Native {
            implementation: implementation.to_owned(),
        }),
    );
    Ok(())
}

fn parts(expression: &Expr) -> Option<(Expr, &[Expr])> {
    match &expression.kind {
        ExprKind::Call { callee, arguments } => Some(((**callee).clone(), arguments)),
        ExprKind::Select {
            namespace,
            item,
            operator,
        } => {
            let ExprKind::Call { callee, arguments } = &item.kind else {
                return None;
            };
            Some((
                Expr {
                    kind: ExprKind::Select {
                        namespace: namespace.clone(),
                        item: callee.clone(),
                        operator: *operator,
                    },
                    span: expression.span,
                    source: expression.source.clone(),
                },
                arguments,
            ))
        }
        _ => None,
    }
}

fn target(
    callee: &Expr,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Option<Arc<Native>>, Failure> {
    if let Some((callee, module)) = namespaces::callee(callee, scope, context)? {
        return target(&callee, &module, context);
    }
    let ExprKind::Path(path) = &callee.kind else {
        return Ok(None);
    };
    Ok(path
        .qualifier
        .is_none()
        .then(|| scope.natives.get(&path.path).cloned())
        .flatten())
}

pub(super) fn is_call(expression: &Expr, scope: &Scope, context: &CompileContext) -> bool {
    parts(expression).is_some_and(|(callee, _)| {
        target(&callee, scope, context).is_ok_and(|value| value.is_some())
    })
}

pub(super) fn is_range_call(expression: &Expr, scope: &Scope, context: &CompileContext) -> bool {
    parts(expression).is_some_and(|(callee, _)| {
        target(&callee, scope, context)
            .ok()
            .flatten()
            .is_some_and(|native| native.implementation.starts_with("ranges."))
    })
}

pub(super) fn lower_call(
    expression: &Expr,
    scope: &Scope,
    context: &CompileContext,
) -> Result<Option<(String, Vec<hir::control::Expr>)>, Failure> {
    let Some((callee, arguments)) = parts(expression) else {
        return Ok(None);
    };
    let Some(native) = target(&callee, scope, context)? else {
        return Ok(None);
    };
    lower_native(&native, arguments, scope, context, expression.span).map(Some)
}

fn lower_native(
    native: &Native,
    arguments: &[Expr],
    scope: &Scope,
    context: &CompileContext,
    span: Span,
) -> Result<(String, Vec<hir::control::Expr>), Failure> {
    let signature =
        crate::stdlib::signature(&native.implementation).expect("validated native declaration");
    if arguments.len() != signature.parameters.len() {
        return Err(Report::new(
            format!(
                "native '{}' expects {} arguments, got {}",
                native.implementation,
                signature.parameters.len(),
                arguments.len()
            ),
            span,
        )
        .into());
    }
    let mut values = Vec::with_capacity(arguments.len());
    for (index, (argument, parameter)) in arguments.iter().zip(signature.parameters).enumerate() {
        let value = v6::scalar(resolve(argument, scope, context)?, context, argument.span)?;
        if !parameter.accepts(&value.ty) {
            return Err(Report::new(
                format!(
                    "native '{}' argument {} expects {parameter}, got {}",
                    native.implementation,
                    index + 1,
                    crate::types::Type::from_control(&value.ty),
                ),
                argument.span,
            )
            .into());
        }
        let value = if *parameter == NativeParam::Path
            && signature.effect != crate::stdlib::NativeEffect::Pure
        {
            v6::filename(value, argument.span)?
        } else {
            value
        };
        values.push(value);
    }
    Ok((native.implementation.clone(), values))
}

pub(super) fn resolve_call(
    expression: &Expr,
    scope: &Scope,
    context: &CompileContext,
) -> Option<Result<Value, Failure>> {
    if !matches!(expression.kind, ExprKind::Call { .. }) {
        return None;
    }
    let (callee, arguments) = parts(expression)?;
    let native = match target(&callee, scope, context) {
        Ok(Some(native)) => native,
        Ok(None) => return None,
        Err(error) => return Some(Err(error)),
    };
    if native.implementation.starts_with("ranges.") {
        return Some(ranges::resolve_native(
            &native.implementation,
            arguments,
            scope,
            context,
            expression.span,
        ));
    }
    let (name, arguments) = match lower_native(&native, arguments, scope, context, expression.span)
    {
        Ok(call) => call,
        Err(error) => return Some(Err(error)),
    };
    let signature = crate::stdlib::signature(&name).expect("validated native call");
    Some(match signature.result {
        NativeResultType::Scalar(ty) => Ok(Value::Dynamic(hir::control::Expr {
            ty,
            kind: hir::control::ExprKind::Native {
                name,
                arguments,
                pwd: None,
            },
        })),
        NativeResultType::Range(item_type) => Ok(Value::Range(hir::control::RangeExpr {
            source: hir::control::RangeSource::Native {
                name,
                arguments,
                item_type: item_type.clone(),
                pwd: None,
            },
            stages: Vec::new(),
            item_type,
        })),
        NativeResultType::Void => Err(Report::new(
            "a Void native call is a statement, not a value",
            expression.span,
        )
        .into()),
    })
}

pub(super) fn standard_module(
    name: &str,
    scope: &Scope,
    context: &CompileContext,
    span: Span,
) -> Result<Value, Failure> {
    static DOCUMENTS: std::sync::OnceLock<HashMap<&'static str, Document>> =
        std::sync::OnceLock::new();
    let documents = DOCUMENTS.get_or_init(|| {
        crate::stdlib::SOURCES
            .iter()
            .map(|(name, text)| {
                (
                    *name,
                    language::parse(text).expect("standard library sources parse"),
                )
            })
            .collect()
    });
    let Some(document) = documents.get(name) else {
        return Err(Report::new(format!("$std has no member '{name}'"), span).into());
    };
    let text = crate::stdlib::SOURCES
        .iter()
        .find(|(module, _)| *module == name)
        .expect("parsed source")
        .1;
    let source = Source::new(
        format!("msync-stdlib:///{}/{name}.msync", env!("CARGO_PKG_VERSION")),
        text,
    );
    let mut session = Session::new();
    session.scope.slots = scope.slots.clone();
    let lowered = session
        .compile_named(document, context, source)
        .map_err(|reports| {
            Failure::located(reports.into_iter().next().expect("compiler reports"))
        })?;
    debug_assert!(lowered.program.body.instructions.is_empty());
    let Some(Value::Module(module)) = session.scope.values.get(name).cloned() else {
        return Err(Report::new("standard library source must declare its module", span).into());
    };
    let mut module = (*module).clone();
    module.builtin_api = crate::relocatable::BuiltinScope::from_module_name(name);
    Ok(Value::Module(Arc::new(module)))
}
