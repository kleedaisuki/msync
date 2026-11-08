//! Canonical output changes spelling and indentation, never endpoint ownership.

use super::{BinaryOp, BindingOp, Block, Document, Expr, ExprKind, StatementKind};
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

fn native_type(output: &mut String, ty: &super::NativeType) {
    use super::NativeType;
    match ty {
        NativeType::Inferred => output.push_str("inferred"),
        NativeType::Void => output.push_str("Void"),
        NativeType::Bool => output.push_str("Bool"),
        NativeType::String => output.push_str("String"),
        NativeType::Path => output.push_str("Path"),
        NativeType::Scope => output.push_str("Scope"),
        NativeType::Range(item) => {
            output.push_str("Range<");
            native_type(output, item);
            output.push('>');
        }
    }
}

fn write_statement(output: &mut String, kind: &StatementKind, depth: usize) {
    match kind {
        StatementKind::Scope { name, body } => {
            output.push_str("scope ");
            output.push_str(&name.text);
            output.push_str(":\n");
            block(output, body, depth + 1);
            return;
        }
        StatementKind::Native {
            name,
            parameters,
            result,
            implementation,
        } => {
            output.push_str("native ");
            output.push_str(&name.text);
            output.push('(');
            output.push_str(
                &parameters
                    .iter()
                    .map(|name| name.text.as_str())
                    .collect::<Vec<_>>()
                    .join(", "),
            );
            output.push(')');
            if !matches!(result, super::NativeType::Inferred) {
                output.push_str(" -> ");
                native_type(output, result);
            }
            output.push_str(" = ");
            quoted(output, implementation);
        }
        StatementKind::Binding { name, op, value } => {
            output.push_str(&name.text);
            output.push_str(match op {
                BindingOp::Space => " => ",
                BindingOp::Value => " := ",
                BindingOp::Assign => " = ",
            });
            expression(output, value, depth);
            if matches!(value.kind, ExprKind::Loop { .. }) {
                return;
            }
        }
        StatementKind::Transfer {
            source,
            destination,
        } => {
            expression(output, source, depth);
            output.push_str(" -> ");
            expression(output, destination, depth);
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
        }
        | StatementKind::Boolean {
            name,
            parameters,
            body,
        } => {
            output.push_str(match kind {
                StatementKind::Symbol { .. } => "symbol ",
                StatementKind::Boolean { .. } => "boolean ",
                _ => "macro ",
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
        | StatementKind::Include(value)
        | StatementKind::Delete(value) => {
            let keyword = match kind {
                StatementKind::Return(_) => "return ",
                StatementKind::Execute(_) => "execute ",
                StatementKind::Include(_) => "include ",
                StatementKind::Delete(_) => "delete ",
                _ => unreachable!(),
            };
            output.push_str(keyword);
            expression(output, value, depth);
        }
        StatementKind::Call(call) => expression(output, call, depth),
        StatementKind::Global(names) | StatementKind::Drop(names) => {
            output.push_str(if matches!(kind, StatementKind::Global(_)) {
                "global "
            } else {
                "drop "
            });
            for (index, name) in names.iter().enumerate() {
                if index != 0 {
                    output.push_str(", ");
                }
                output.push_str(&name.text);
            }
        }
        StatementKind::If {
            condition,
            then_body,
            else_body,
        } => {
            conditional(output, condition, then_body, else_body, depth, "");
            return;
        }
        StatementKind::For { name, range, body } => {
            output.push_str("for ");
            output.push_str(&name.text);
            output.push_str(" in ");
            expression(output, range, depth);
            output.push_str(":\n");
            block(output, body, depth + 1);
            return;
        }
        StatementKind::Loop { body } => {
            output.push_str("loop:\n");
            block(output, body, depth + 1);
            return;
        }
        StatementKind::Break(value) => {
            output.push_str("break");
            if let Some(value) = value {
                output.push(' ');
                expression(output, value, depth);
            }
        }
        StatementKind::Scheduled {
            asynchronous,
            awaited,
            statement,
        } => {
            let prefix = schedule_prefix(*asynchronous, *awaited);
            if let StatementKind::If {
                condition,
                then_body,
                else_body,
            } = &statement.kind
            {
                conditional(output, condition, then_body, else_body, depth, prefix);
                return;
            }
            output.push_str(prefix);
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

fn schedule_prefix(asynchronous: bool, awaited: bool) -> &'static str {
    match (asynchronous, awaited) {
        (true, true) => "acqrel ",
        (true, false) => "release ",
        (false, true) => "acquire ",
        (false, false) => "",
    }
}

fn is_conditional(kind: &StatementKind) -> bool {
    match kind {
        StatementKind::If { .. } => true,
        StatementKind::Scheduled { statement, .. } => is_conditional(&statement.kind),
        _ => false,
    }
}

fn conditional(
    output: &mut String,
    condition: &Expr,
    then_body: &Block,
    else_body: &Block,
    depth: usize,
    prefix: &str,
) {
    output.push_str("if ");
    output.push_str(prefix);
    expression(output, condition, depth);
    output.push_str(":\n");
    block(output, then_body, depth + 1);
    if else_body.is_empty() {
        return;
    }
    output.push_str(&"    ".repeat(depth));
    if let [branch] = else_body.as_slice()
        && is_conditional(&branch.kind)
    {
        output.push_str("else ");
        write_statement(output, &branch.kind, depth);
    } else {
        output.push_str("else:\n");
        block(output, else_body, depth + 1);
    }
}

fn expression(output: &mut String, value: &Expr, depth: usize) {
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
        ExprKind::Reference { operand, .. } => {
            output.push('$');
            if matches!(operand.kind, ExprKind::Path(_)) {
                expression(output, operand, depth);
            } else {
                output.push('(');
                expression(output, operand, depth);
                output.push(')');
            }
        }
        ExprKind::Bool(value) => output.push_str(if *value { "true" } else { "false" }),
        ExprKind::Range(values) => {
            output.push('[');
            for (index, value) in values.iter().enumerate() {
                if index != 0 {
                    output.push_str(", ");
                }
                expression(output, value, depth);
            }
            output.push(']');
        }
        ExprKind::Not(value) => {
            output.push_str("not (");
            expression(output, value, depth);
            output.push(')');
        }
        ExprKind::Exists(value) => {
            output.push_str("exists (");
            expression(output, value, depth);
            output.push(')');
        }
        ExprKind::Binary {
            left, op, right, ..
        } => {
            output.push('(');
            expression(output, left, depth);
            output.push_str(match op {
                BinaryOp::And => " and ",
                BinaryOp::Or => " or ",
                BinaryOp::Eq => " == ",
                BinaryOp::Ne => " != ",
                BinaryOp::Lt => " < ",
                BinaryOp::Le => " <= ",
                BinaryOp::Gt => " > ",
                BinaryOp::Ge => " >= ",
                BinaryOp::Pipe => " | ",
            });
            expression(output, right, depth);
            output.push(')');
        }
        ExprKind::Loop { body } => {
            output.push_str("loop:\n");
            block(output, body, depth + 1);
        }
        ExprKind::Select {
            namespace, item, ..
        } => {
            // Selection binds more tightly than slash, and associates left.
            grouped_expression(
                output,
                namespace,
                depth,
                matches!(namespace.kind, ExprKind::Join { .. }),
            );
            output.push_str("::");
            grouped_expression(
                output,
                item,
                depth,
                matches!(item.kind, ExprKind::Select { .. } | ExprKind::Join { .. }),
            );
        }
        ExprKind::Join { base, suffix, .. } => {
            expression(output, base, depth);
            output.push('/');
            if let Some(suffix) = suffix {
                grouped_expression(
                    output,
                    suffix,
                    depth,
                    matches!(suffix.kind, ExprKind::Join { .. }),
                );
            }
        }
        ExprKind::Call { callee, arguments } => {
            expression(output, callee, depth);
            output.push('(');
            for (index, argument) in arguments.iter().enumerate() {
                if index != 0 {
                    output.push_str(", ");
                }
                expression(output, argument, depth);
            }
            output.push(')');
        }
    }
}

fn grouped_expression(output: &mut String, value: &Expr, depth: usize, grouped: bool) {
    if grouped {
        output.push('(');
    }
    expression(output, value, depth);
    if grouped {
        output.push(')');
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
    super::literal::write_string(output, text);
}
