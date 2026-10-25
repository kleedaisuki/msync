//! Line-oriented syntax and canonical, comment-preserving formatting for msync.

use std::fmt;
use unicode_width::UnicodeWidthStr;

/// A half-open UTF-8 byte range in the original source.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Span {
    pub start: usize,
    pub end: usize,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Diagnostic {
    pub message: String,
    pub span: Span,
}

impl fmt::Display for Diagnostic {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.message)
    }
}

impl std::error::Error for Diagnostic {}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Script {
    pub statements: Vec<Statement>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Statement {
    pub kind: StatementKind,
    pub span: Span,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum StatementKind {
    Endpoint { name: String, value: String },
    Alias { name: String, value: String },
    Copy { source: String, destination: String },
    Comment(String),
}

#[derive(Clone, Copy)]
enum Operator {
    Endpoint,
    Alias,
    Copy,
}

struct LineScan {
    operators: Vec<(usize, Operator)>,
    comment: Option<usize>,
}

/// Parse all lines, collecting independent errors instead of stopping at the first.
pub fn parse(source: &str) -> Result<Script, Vec<Diagnostic>> {
    let mut trace = crate::telemetry::span("parse");
    let mut script = Script::default();
    let mut diagnostics = Vec::new();
    let mut offset = 0;
    for line in source.split_inclusive('\n') {
        let line = line.trim_end_matches(['\r', '\n']);
        parse_line(line, offset, &mut script, &mut diagnostics);
        // Include the original line terminator in the next statement's span.
        offset += source[offset..]
            .find('\n')
            .map_or(line.len(), |index| index + 1);
    }
    if diagnostics.is_empty() {
        Ok(script)
    } else {
        trace.error();
        Err(diagnostics)
    }
}

fn parse_line(line: &str, offset: usize, script: &mut Script, errors: &mut Vec<Diagnostic>) {
    let scan = match scan_line(line, offset) {
        Ok(scan) => scan,
        Err(error) => {
            errors.push(error);
            return;
        }
    };
    let code_end = scan.comment.unwrap_or(line.len());
    let code = &line[..code_end];
    if !code.trim().is_empty() {
        match parse_statement(code, offset, &scan.operators) {
            Ok(statement) => script.statements.push(statement),
            Err(error) => errors.push(error),
        }
    }
    if let Some(start) = scan.comment {
        script.statements.push(Statement {
            kind: StatementKind::Comment(line[start + 1..].trim().to_owned()),
            span: Span {
                start: offset + start,
                end: offset + line.len(),
            },
        });
    }
}

fn scan_line(line: &str, offset: usize) -> Result<LineScan, Diagnostic> {
    let bytes = line.as_bytes();
    let mut scan = LineScan {
        operators: Vec::new(),
        comment: None,
    };
    let mut quote = None;
    let mut selector = None;
    let mut escaped = false;
    let mut character_class = false;
    let mut index = 0;
    while index < bytes.len() {
        let byte = bytes[index];
        if quote.is_some() {
            if escaped {
                escaped = false;
            } else if byte == b'\\' {
                escaped = true;
            } else if byte == b'"' {
                quote = None;
            }
            index += 1;
            continue;
        }
        if selector.is_some() {
            if escaped {
                escaped = false;
            } else if byte == b'\\' {
                escaped = true;
            } else if byte == b'[' && !character_class {
                character_class = true;
            } else if byte == b']' && character_class {
                character_class = false;
            } else if byte == b'>' && !character_class && selector_end(bytes, index) {
                selector = None;
            }
            index += 1;
            continue;
        }
        match byte {
            b'"' => quote = Some(index),
            b'<' => selector = Some(index),
            b'#' if index == 0 || bytes[index - 1].is_ascii_whitespace() => {
                scan.comment = Some(index);
                break;
            }
            _ => {
                if let Some(operator) = operator_at(&bytes[index..]) {
                    scan.operators.push((index, operator));
                    index += 1;
                }
            }
        }
        index += 1;
    }
    if let Some(start) = quote {
        return Err(error(
            "unterminated quoted operand",
            offset + start,
            offset + line.len(),
        ));
    }
    if let Some(start) = selector {
        return Err(error(
            "unterminated regex selector; expected '>'",
            offset + start,
            offset + line.len(),
        ));
    }
    Ok(scan)
}

fn operator_at(bytes: &[u8]) -> Option<Operator> {
    match bytes.get(..2)? {
        b"=>" => Some(Operator::Endpoint),
        b":=" => Some(Operator::Alias),
        b"->" => Some(Operator::Copy),
        _ => None,
    }
}

fn selector_end(bytes: &[u8], index: usize) -> bool {
    let rest = &bytes[index + 1..];
    rest.first()
        .is_none_or(|byte| byte.is_ascii_whitespace() || matches!(byte, b'/' | b'\\'))
        || operator_at(rest).is_some()
}

fn parse_statement(
    code: &str,
    offset: usize,
    operators: &[(usize, Operator)],
) -> Result<Statement, Diagnostic> {
    let [(position, operator)] = operators else {
        let message = if operators.is_empty() {
            "expected '=>', ':=' or '->'"
        } else {
            "expected one statement per line; quote operands containing operators"
        };
        return Err(error(message, offset, offset + code.len()));
    };
    let left = operand(&code[..*position], offset)?;
    let right = operand(&code[position + 2..], offset + position + 2)?;
    let kind = match operator {
        Operator::Endpoint => StatementKind::Endpoint {
            name: left,
            value: right,
        },
        Operator::Alias => StatementKind::Alias {
            name: left,
            value: right,
        },
        Operator::Copy => StatementKind::Copy {
            source: left,
            destination: right,
        },
    };
    let start = code.len() - code.trim_start().len();
    Ok(Statement {
        kind,
        span: Span {
            start: offset + start,
            end: offset + code.trim_end().len(),
        },
    })
}

fn operand(raw: &str, offset: usize) -> Result<String, Diagnostic> {
    let value = raw.trim();
    let start = offset + raw.len() - raw.trim_start().len();
    let end = start + value.len();
    if value.is_empty() {
        return Err(error("expected a non-empty operand", start, end));
    }
    if !value.starts_with('"') {
        if value.contains('"') {
            return Err(error(
                "quote the entire operand, not individual path components",
                start,
                end,
            ));
        }
        return Ok(value.to_owned());
    }
    let mut decoded = String::with_capacity(value.len() - 2);
    let mut chars = value[1..].char_indices();
    while let Some((index, ch)) = chars.next() {
        if ch == '"' {
            if index + 2 != value.len() {
                return Err(error(
                    "unexpected text after quoted operand",
                    start + index + 2,
                    end,
                ));
            }
            if decoded.is_empty() {
                return Err(error("expected a non-empty operand", start, end));
            }
            return Ok(decoded);
        }
        if ch != '\\' {
            decoded.push(ch);
            continue;
        }
        match chars.next().map(|(_, ch)| ch) {
            Some('"') => decoded.push('"'),
            Some('\\') => decoded.push('\\'),
            Some('n') => decoded.push('\n'),
            Some('r') => decoded.push('\r'),
            Some('t') => decoded.push('\t'),
            Some(ch) => {
                decoded.push('\\');
                decoded.push(ch);
            }
            None => return Err(error("unterminated escape", start, end)),
        }
    }
    Err(error("unterminated quoted operand", start, end))
}

fn error(message: &str, start: usize, end: usize) -> Diagnostic {
    Diagnostic {
        message: message.to_owned(),
        span: Span { start, end },
    }
}

/// Canonicalize spacing and quoting while retaining every comment and operand.
pub fn format(source: &str) -> Result<String, Vec<Diagnostic>> {
    let mut trace = crate::telemetry::span("format");
    let script = parse(source).inspect_err(|_| trace.error())?;
    let mut output = String::with_capacity(source.len());
    for statement in script.statements {
        match statement.kind {
            StatementKind::Endpoint { name, value } => {
                write_statement(&mut output, &name, " => ", &value)
            }
            StatementKind::Alias { name, value } => {
                write_statement(&mut output, &name, " := ", &value)
            }
            StatementKind::Copy {
                source,
                destination,
            } => write_statement(&mut output, &source, " -> ", &destination),
            StatementKind::Comment(comment) => {
                output.push('#');
                if !comment.is_empty() {
                    output.push(' ');
                    output.push_str(&comment);
                }
                output.push('\n');
            }
        }
    }
    Ok(output)
}

fn write_statement(output: &mut String, left: &str, operator: &str, right: &str) {
    write_operand(output, left);
    output.push_str(operator);
    write_operand(output, right);
    output.push('\n');
}

fn write_operand(output: &mut String, value: &str) {
    let needs_quotes = value
        .chars()
        .any(|ch| ch.is_whitespace() || matches!(ch, '"' | '#'))
        || scan_line(value, 0).map_or(true, |scan| !scan.operators.is_empty());
    if !needs_quotes {
        output.push_str(value);
        return;
    }
    output.push('"');
    for ch in value.chars() {
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

/// Render a UTF-8-safe line/column diagnostic, including malformed external spans.
pub fn render_diagnostic(source: &str, diagnostic: &Diagnostic) -> String {
    let mut start = diagnostic.span.start.min(source.len());
    while !source.is_char_boundary(start) {
        start -= 1;
    }
    let line_start = source[..start].rfind('\n').map_or(0, |index| index + 1);
    let line_end = source[start..]
        .find('\n')
        .map_or(source.len(), |index| start + index);
    let line = source[line_start..line_end].trim_end_matches('\r');
    let number = source[..line_start]
        .bytes()
        .filter(|byte| *byte == b'\n')
        .count()
        + 1;
    let column = source[line_start..start].chars().count();
    let mut end = diagnostic.span.end.min(line_end).max(start);
    while !source.is_char_boundary(end) {
        end -= 1;
    }
    let prefix = display_text(&source[line_start..start]);
    let highlighted = display_text(&source[line_start..end]);
    let display_column = UnicodeWidthStr::width(prefix.as_str());
    let width = UnicodeWidthStr::width(highlighted.as_str())
        .saturating_sub(display_column)
        .max(1);
    let line = display_text(line);
    let gutter = number.to_string().len();
    format!(
        "error: {}\n --> {}:{}\n{:gutter$} |\n{} | {}\n{:gutter$} | {}{}",
        display_text(&diagnostic.message),
        number,
        column + 1,
        "",
        number,
        line,
        "",
        " ".repeat(display_column),
        "^".repeat(width)
    )
}

fn display_text(text: &str) -> String {
    let mut output = String::with_capacity(text.len());
    for ch in text.chars() {
        if ch == '\t' {
            let width = 4 - UnicodeWidthStr::width(output.as_str()) % 4;
            output.extend(std::iter::repeat_n(' ', width));
        } else if ch.is_control() {
            let escaped = ch.escape_default().to_string();
            output.push_str(&escaped);
        } else {
            output.push(ch);
        }
    }
    output
}
