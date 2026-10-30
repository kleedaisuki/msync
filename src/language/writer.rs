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
        write_statement(output, &statement.kind, depth);
    }
}

fn write_statement(output: &mut String, kind: &StatementKind, depth: usize) {
    match kind {
        StatementKind::Binding { name, op, value } => {
            output.push_str(&name.text);
            output.push_str(match op {
                BindingOp::Space => " => ",
                BindingOp::Value => " := ",
            });
            expression(output, value);
        }
        StatementKind::Transfer {
            source,
            destination,
        } => {
            expression(output, source);
            output.push_str(" -> ");
            expression(output, destination);
        }
        StatementKind::Macro {
            name,
            parameters,
            body,
        }
        | StatementKind::Symbol {
            name,
            parameters,
            body,
        } => {
            output.push_str(if matches!(kind, StatementKind::Symbol { .. }) {
                "symbol "
            } else {
                "macro "
            });
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
            return;
        }
        StatementKind::Return(value)
        | StatementKind::Execute(value)
        | StatementKind::Include(value) => {
            let keyword = match kind {
                StatementKind::Return(_) => "return ",
                StatementKind::Execute(_) => "execute ",
                StatementKind::Include(_) => "include ",
                _ => unreachable!(),
            };
            output.push_str(keyword);
            expression(output, value);
        }
        StatementKind::Call(call) => expression(output, call),
        StatementKind::Global(names) => {
            output.push_str("global ");
            for (index, name) in names.iter().enumerate() {
                if index != 0 {
                    output.push_str(", ");
                }
                output.push_str(&name.text);
            }
        }
        StatementKind::Scheduled {
            asynchronous,
            awaited,
            statement,
        } => {
            output.push_str(match (*asynchronous, *awaited) {
                (true, true) => "acqrel ",
                (true, false) => "release ",
                (false, true) => "acquire ",
                (false, false) => "",
            });
            write_statement(output, &statement.kind, depth);
            return;
        }
        StatementKind::Async => output.push_str("release"),
        StatementKind::Await(target) => {
            output.push_str("acquire");
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

fn expression(output: &mut String, value: &Expr) {
    match &value.kind {
        ExprKind::Path(path) => {
            if let Some(qualifier) = &path.qualifier {
                output.push_str(&qualifier.text);
                output.push_str("::");
            }
            // An unqualified source/argument was necessarily parsed as a bare
            // path. Keep it bare: inventing `$local` could pin an endpoint-
            // polymorphic macro parameter to the wrong transport.
            if needs_quote(&path.path) && path.qualifier.is_some() {
                quoted(output, &path.path);
            } else {
                output.push_str(&path.path);
            }
        }
        ExprKind::Literal(text) => quoted(output, text),
        ExprKind::Select {
            namespace, item, ..
        } => {
            expression(output, namespace);
            output.push_str("::");
            expression(output, item);
        }
        ExprKind::Join { base, suffix, .. } => {
            expression(output, base);
            output.push('/');
            if let Some(suffix) = suffix {
                expression(output, suffix);
            }
        }
        ExprKind::Call { callee, arguments } => {
            expression(output, callee);
            output.push('(');
            for (index, argument) in arguments.iter().enumerate() {
                if index != 0 {
                    output.push_str(", ");
                }
                expression(output, argument);
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

pub(crate) fn quoted(output: &mut String, text: &str) {
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
