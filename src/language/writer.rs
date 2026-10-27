//! Canonical output changes spelling and indentation, never endpoint ownership.

use super::{BindingOp, Block, Document, Expr, ExprKind, StatementKind};
use crate::syntax::Report;

/// Canonicalize a structured script without resolving bindings or executing it.
pub fn format(source: &str) -> Result<String, Vec<Report>> {
    let document = super::parse(source)?;
    Ok(write(&document))
}

fn write(document: &Document) -> String {
    let mut output = String::new();
    block(&mut output, &document.statements, 0);
    output
}

fn block(output: &mut String, statements: &Block, depth: usize) {
    for statement in statements {
        output.push_str(&"    ".repeat(depth));
        match &statement.kind {
            StatementKind::Binding { name, op, value } => {
                output.push_str(&name.text);
                output.push_str(match op {
                    BindingOp::Space => " => ",
                    BindingOp::Value => " := ",
                });
                expression(output, value, false);
            }
            StatementKind::Transfer {
                source,
                destination,
            } => {
                expression(output, source, true);
                output.push_str(" -> ");
                expression(output, destination, false);
            }
            StatementKind::Macro {
                name,
                parameters,
                body,
            } => {
                output.push_str("macro ");
                output.push_str(&name.text);
                output.push('(');
                for (index, parameter) in parameters.iter().enumerate() {
                    if index != 0 {
                        output.push_str(", ");
                    }
                    output.push_str(&parameter.text);
                }
                output.push_str("):\n");
                block(output, body, depth + 1);
                continue;
            }
            StatementKind::Call(call) => expression(output, call, true),
            StatementKind::Global(names) => {
                output.push_str("global ");
                for (index, name) in names.iter().enumerate() {
                    if index != 0 {
                        output.push_str(", ");
                    }
                    output.push_str(&name.text);
                }
            }
            StatementKind::Transaction {
                name,
                isolation,
                body,
            } => {
                output.push_str("transaction ");
                output.push_str(&name.text);
                output.push('(');
                output.push_str(isolation.as_str());
                output.push_str("):\n");
                block(output, body, depth + 1);
                continue;
            }
            StatementKind::Commit => output.push_str("commit"),
            StatementKind::Rollback(target) => {
                output.push_str("rollback");
                if let Some(target) = target {
                    output.push(' ');
                    output.push_str(&target.text);
                }
            }
            StatementKind::Comment(comment) => {
                output.push('#');
                if !comment.is_empty() {
                    output.push(' ');
                    output.push_str(comment);
                }
            }
        }
        output.push('\n');
    }
}

fn expression(output: &mut String, value: &Expr, literal_position: bool) {
    match &value.kind {
        ExprKind::Path(path) => {
            if let Some(qualifier) = &path.qualifier {
                output.push_str(&qualifier.text);
                output.push_str("::");
            }
            // An unqualified source/argument was necessarily parsed as a bare
            // path. Keep it bare: inventing `$local` could pin an endpoint-
            // polymorphic macro parameter to the wrong transport.
            if needs_quote(&path.path) && (path.qualifier.is_some() || !literal_position) {
                quoted(output, &path.path);
            } else {
                output.push_str(&path.path);
            }
        }
        ExprKind::Literal(text) => quoted(output, text),
        ExprKind::Call { callee, arguments } => {
            expression(output, callee, false);
            output.push('(');
            for (index, argument) in arguments.iter().enumerate() {
                if index != 0 {
                    output.push_str(", ");
                }
                expression(output, argument, true);
            }
            output.push(')');
        }
    }
}

fn needs_quote(path: &str) -> bool {
    path.is_empty()
        || path
            .chars()
            .any(|ch| ch.is_whitespace() || matches!(ch, '"' | '#' | ',' | '(' | ')'))
        || path.contains("->")
        || path.contains("=>")
        || path.contains(":=")
}

fn quoted(output: &mut String, text: &str) {
    output.push('"');
    for ch in text.chars() {
        match ch {
            '"' => output.push_str("\\\""),
            '\\' => output.push_str("\\\\"),
            '\n' => output.push_str("\\n"),
            '\r' => output.push_str("\\r"),
            '\t' => output.push_str("\\t"),
            _ => output.push(ch),
        }
    }
    output.push('"');
}
