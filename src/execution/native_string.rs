//! Unicode text primitives; declarations live in the embedded standard library.

use super::super::error::{ExecutionError, Result};
use super::control::Value;
use super::native_fs::NativeResult;
use crate::syntax::Span;

pub(super) fn call(name: &str, arguments: Vec<Value>, span: Span) -> Result<NativeResult> {
    let values = arguments
        .into_iter()
        .map(|value| match value {
            Value::String(text) => Ok(text),
            _ => Err(ExecutionError::new(
                "string native requires String arguments",
                span,
            )),
        })
        .collect::<Result<Vec<_>>>()?;
    let name = name.strip_prefix("string.").unwrap_or(name);
    let text = &values[0];
    let value = match name {
        "contains" => Value::Bool(text.contains(&values[1])),
        "starts_with" => Value::Bool(text.starts_with(&values[1])),
        "ends_with" => Value::Bool(text.ends_with(&values[1])),
        "concat" => Value::String(format!("{text}{}", values[1])),
        "trim" => Value::String(text.trim().to_owned()),
        "trim_start" => Value::String(text.trim_start().to_owned()),
        "trim_end" => Value::String(text.trim_end().to_owned()),
        "to_lowercase" => Value::String(text.to_lowercase()),
        "to_uppercase" => Value::String(text.to_uppercase()),
        "replace" => Value::String(text.replace(&values[1], &values[2])),
        "split" => {
            return Ok(NativeResult::Range(
                text.split(&values[1])
                    .map(|part| Value::String(part.to_owned()))
                    .collect(),
            ));
        }
        _ => {
            return Err(ExecutionError::new(
                format!("unknown string native '{name}'"),
                span,
            ));
        }
    };
    Ok(NativeResult::Value(value))
}
