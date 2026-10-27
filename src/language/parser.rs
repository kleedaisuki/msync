//! Physical lines retain byte spans; only significant code changes block depth.
//! Quoted expressions stay literal data; namespace qualification and slash
//! composition provide explicit path context without filesystem guessing.

use super::{
    BindingOp, Block, DestinationMode, Document, Expr, ExprKind, Isolation, Name, PathExpr,
    Statement, StatementKind,
};
use crate::syntax::{OperandSource, Report, Span};

const MAX_NESTING: usize = 128;

pub(super) fn opens_block(line: &str) -> bool {
    let code = line[..comment_start(line).unwrap_or(line.len())].trim_start();
    code.starts_with("macro ") || code.starts_with("transaction ")
}

#[derive(Clone, Copy)]
struct Line<'a> {
    code: &'a str,
    offset: usize,
    indent: usize,
    alternate: usize,
    comment: Option<(&'a str, Span)>,
}

/// Parse a structured script without inspecting files, resolving names, or running commands.
pub fn parse(source: &str) -> Result<Document, Vec<Report>> {
    let mut errors = Vec::new();
    let lines = lines(source, &mut errors);
    let mut parser = Parser {
        lines,
        cursor: 0,
        errors,
    };
    let statements = parser.block(0, 0, false, 0);
    if parser.errors.is_empty() {
        Ok(Document { statements })
    } else {
        Err(parser.errors)
    }
}

fn lines<'a>(source: &'a str, errors: &mut Vec<Report>) -> Vec<Line<'a>> {
    let mut result = Vec::new();
    let mut offset = 0;
    for physical in source.split_inclusive('\n') {
        let text = physical.trim_end_matches(['\r', '\n']);
        let prefix = text.len() - text.trim_start_matches([' ', '\t']).len();
        let mut indent = 0;
        for byte in text.as_bytes().iter().take(prefix) {
            indent = if *byte == b'\t' {
                (indent / 8 + 1) * 8
            } else {
                indent + 1
            };
        }
        let raw = &text[prefix..];
        let code_offset = offset + prefix;
        let comment = comment_start(raw);
        let code = raw[..comment.unwrap_or(raw.len())].trim_end();
        if !code.is_empty() && text[..prefix].contains(' ') && text[..prefix].contains('\t') {
            errors.push(
                Report::new(
                    "mixed tabs and spaces in indentation",
                    Span { start: offset, end: code_offset },
                )
                .help("Use only spaces or only tabs for an indentation prefix; formatting uses four spaces."),
            );
        }
        result.push(Line {
            code,
            offset: code_offset,
            indent,
            alternate: prefix,
            comment: comment.map(|index| {
                (
                    raw[index + 1..].trim(),
                    Span {
                        start: code_offset + index,
                        end: offset + text.len(),
                    },
                )
            }),
        });
        offset += physical.len();
    }
    result
}

struct Parser<'a> {
    lines: Vec<Line<'a>>,
    cursor: usize,
    errors: Vec<Report>,
}

impl Parser<'_> {
    fn block(&mut self, indent: usize, alternate: usize, in_macro: bool, depth: usize) -> Block {
        let mut body = Vec::new();
        while let Some(line) = self.lines.get(self.cursor).copied() {
            if line.code.is_empty() {
                push_comment(&mut body, line.comment);
                self.cursor += 1;
                continue;
            }
            if line.indent < indent {
                break;
            }
            self.cursor += 1;
            if line.indent != indent || line.alternate != alternate {
                let message = if line.indent == indent {
                    "inconsistent use of tabs and spaces in indentation"
                } else {
                    "unexpected indentation or dedent to an unknown level"
                };
                self.errors.push(Report::new(message, span(line.offset, line.offset + line.code.len()))
                    .help("Align this statement with its enclosing block; only macro and transaction headers introduce a block."));
                continue;
            }
            match statement(line.code, line.offset) {
                Ok(mut statement) => {
                    let is_macro = matches!(statement.kind, StatementKind::Macro { .. });
                    let nested =
                        is_macro || matches!(statement.kind, StatementKind::Transaction { .. });
                    if is_macro && in_macro {
                        self.errors.push(Report::new("a macro cannot declare another macro", statement.span)
                            .help("Move the macro declaration to the top level and call it from this body."));
                    }
                    if nested {
                        let mut child = self.child(line, in_macro || is_macro, depth + 1);
                        if let Some(comment) = line.comment {
                            child.insert(0, comment_statement(comment));
                        }
                        if let Some(last) = child.last() {
                            statement.span.end = last.span.end;
                        }
                        match &mut statement.kind {
                            StatementKind::Macro { body, .. }
                            | StatementKind::Transaction { body, .. } => *body = child,
                            _ => unreachable!(),
                        }
                    }
                    body.push(statement);
                    if !nested {
                        push_comment(&mut body, line.comment);
                    }
                }
                Err(error) => self.errors.push(error),
            }
        }
        body
    }

    fn child(&mut self, header: Line<'_>, in_macro: bool, depth: usize) -> Block {
        if depth >= MAX_NESTING {
            self.errors.push(Report::new("block nesting is too deep", span(header.offset, header.offset + header.code.len()))
                .help("Use fewer than 128 nested blocks; extract repeated operations into top-level macros."));
            while self
                .lines
                .get(self.cursor)
                .is_some_and(|line| line.code.is_empty() || line.indent > header.indent)
            {
                self.cursor += 1;
            }
            return Vec::new();
        }
        let next = self.lines[self.cursor..]
            .iter()
            .find(|line| !line.code.is_empty());
        let Some(next) = next.copied().filter(|line| line.indent > header.indent) else {
            self.errors.push(Report::new("expected an indented block", span(header.offset + header.code.len(), header.offset + header.code.len()))
                .help("Indent at least one statement beneath this header; comments alone do not form a block."));
            return Vec::new();
        };
        if next.alternate <= header.alternate {
            self.errors.push(Report::new("ambiguous indentation between tabs and spaces", span(next.offset, next.offset + next.code.len()))
                .help("Use the same indentation style throughout a block; tabs advance to multiples of eight columns."));
        }
        self.block(next.indent, next.alternate, in_macro, depth)
    }
}

fn span(start: usize, end: usize) -> Span {
    Span { start, end }
}

fn push_comment(body: &mut Block, comment: Option<(&str, Span)>) {
    if let Some(comment) = comment {
        body.push(comment_statement(comment));
    }
}

fn comment_statement((text, span): (&str, Span)) -> Statement {
    Statement {
        kind: StatementKind::Comment(text.to_owned()),
        span,
    }
}

fn statement(code: &str, offset: usize) -> Result<Statement, Report> {
    let kind = if let Some(header) = code.strip_prefix("macro ") {
        let (name, parameters) = macro_header(header, offset + 6)?;
        StatementKind::Macro {
            name,
            parameters,
            body: Vec::new(),
        }
    } else if let Some(header) = code.strip_prefix("transaction ") {
        let (name, isolation) = transaction_header(header, offset + 12)?;
        StatementKind::Transaction {
            name,
            isolation,
            body: Vec::new(),
        }
    } else if code == "commit" {
        StatementKind::Commit
    } else if code == "rollback" {
        StatementKind::Rollback(None)
    } else if let Some(target) = code.strip_prefix("rollback ") {
        StatementKind::Rollback(Some(name(target, offset + 9)?))
    } else if let Some(names) = code.strip_prefix("global ") {
        let names = split_arguments(names, offset + 7)?
            .into_iter()
            .map(|(text, at)| name(text, at))
            .collect::<Result<Vec<_>, _>>()?;
        if names.is_empty() {
            return Err(Report::new(
                "global requires at least one binding name",
                span(offset + 7, offset + code.len()),
            ));
        }
        StatementKind::Global(names)
    } else {
        ordinary_statement(code, offset)?
    };
    Ok(Statement {
        kind,
        span: span(offset, offset + code.len()),
    })
}

fn ordinary_statement(code: &str, offset: usize) -> Result<StatementKind, Report> {
    let scan = scan(code, offset)?;
    match scan.operators.as_slice() {
        [(index, operator)] => {
            let left = &code[..*index];
            let right = &code[index + 2..];
            let right_offset = offset + index + 2;
            match *operator {
                "->" => Ok(StatementKind::Transfer {
                    source: expression(left, offset)?,
                    destination: expression(right, right_offset)?,
                }),
                ":=" | "=>" => Ok(StatementKind::Binding {
                    name: name(left, offset)?,
                    op: if *operator == ":=" { BindingOp::Value } else { BindingOp::Space },
                    value: expression(right, right_offset)?,
                }),
                _ => unreachable!(),
            }
        }
        [] => {
            let value = expression(code, offset)?;
            if matches!(value.kind, ExprKind::Call { .. }) {
                Ok(StatementKind::Call(value))
            } else {
                Err(Report::new("expected a transfer, binding, macro call, or control statement", value.span)
                    .help("Use 'source -> destination', 'name := path', 'name => namespace', or 'macro_name(arguments)'."))
            }
        }
        [_, (index, _), ..] => Err(Report::new("expected one statement per line", span(offset + index, offset + index + 2))
            .help("Put the next statement on another line; quote operator characters when they belong to a path or literal.")),
    }
}

fn macro_header(raw: &str, offset: usize) -> Result<(Name, Vec<Name>), Report> {
    let header = header_body(raw, offset)?;
    let scan = scan(header, offset)?;
    let open = scan.first_call.ok_or_else(|| {
        Report::new(
            "macro parameters require parentheses",
            span(offset, offset + header.len()),
        )
        .help("Write 'macro name(parameter, other):' and indent its body.")
    })?;
    if !header.ends_with(')') {
        return Err(Report::new(
            "unexpected text after macro parameters",
            span(offset + open, offset + header.len()),
        ));
    }
    let macro_name = name(&header[..open], offset)?;
    let parameters = split_arguments(&header[open + 1..header.len() - 1], offset + open + 1)?
        .into_iter()
        .map(|(text, at)| name(text, at))
        .collect::<Result<Vec<_>, _>>()?;
    for (index, parameter) in parameters.iter().enumerate() {
        if let Some(previous) = parameters[..index]
            .iter()
            .find(|previous| previous.text == parameter.text)
        {
            return Err(Report::new("duplicate macro parameter", parameter.span)
                .label(previous.span, "first declared here")
                .help("Give every parameter a distinct name."));
        }
    }
    Ok((macro_name, parameters))
}

fn transaction_header(raw: &str, offset: usize) -> Result<(Name, Isolation), Report> {
    let header = header_body(raw, offset)?;
    let scan = scan(header, offset)?;
    let Some(open) = scan.first_call else {
        return Ok((name(header, offset)?, Isolation::Serializable));
    };
    if !header.ends_with(')') {
        return Err(Report::new(
            "unexpected text after transaction isolation",
            span(offset + open, offset + header.len()),
        ));
    }
    let transaction_name = name(&header[..open], offset)?;
    let isolation_text = header[open + 1..header.len() - 1].trim();
    let isolation = match isolation_text {
        "read uncommitted" => Isolation::ReadUncommitted,
        "read committed" => Isolation::ReadCommitted,
        "repeatable read" => Isolation::RepeatableRead,
        "serializable" | "" => Isolation::Serializable,
        _ => return Err(Report::new("unknown transaction isolation level", span(offset + open + 1, offset + header.len() - 1))
            .help("Choose read uncommitted, read committed, repeatable read, or serializable (the default).")),
    };
    Ok((transaction_name, isolation))
}

fn header_body(raw: &str, offset: usize) -> Result<&str, Report> {
    raw.strip_suffix(':').map(str::trim_end).ok_or_else(|| {
        Report::new(
            "expected ':' after block header",
            span(offset + raw.len(), offset + raw.len()),
        )
        .help("End the header with ':' and indent the following statements.")
    })
}

fn name(raw: &str, offset: usize) -> Result<Name, Report> {
    let (text, start) = trimmed(raw, offset);
    if text.is_empty()
        || text == "."
        || !text
            .chars()
            .all(|ch| ch.is_alphanumeric() || matches!(ch, '.' | '_' | '-'))
    {
        return Err(Report::new("expected a binding name", span(start, start + text.len()))
            .help("Use letters, digits, '.', '_' or '-'; built-in names beginning with '$' cannot be declared."));
    }
    Ok(Name {
        text: text.to_owned(),
        span: span(start, start + text.len()),
    })
}

fn expression(raw: &str, offset: usize) -> Result<Expr, Report> {
    let (text, offset) = trimmed(raw, offset);
    let range = span(offset, offset + text.len());
    if text.is_empty() {
        return Err(Report::new("expected an expression", range)
            .help("Insert a path, UTF-8 string literal, or program call here."));
    }
    let scan = scan(text, offset)?;
    if let Some((index, _)) = scan.operators.first() {
        return Err(Report::new(
            "a statement operator is not an expression",
            span(offset + index, offset + index + 2),
        )
        .help("Place each binding or transfer on its own line."));
    }
    let mut provenance = OperandSource::unmapped(range);
    let kind = if let Some(open) = scan.first_call {
        if !text.ends_with(')') || scan.call_close != Some(text.len() - 1) {
            return Err(Report::new("unexpected text after call", span(offset + scan.call_close.unwrap_or(open) + 1, offset + text.len()))
                .help("A call must end with its closing ')'; quote parentheses that belong to a filename."));
        }
        let callee = expression(&text[..open], offset)?;
        if !matches!(callee.kind, ExprKind::Path(_) | ExprKind::Join { .. }) {
            return Err(Report::new(
                "a call requires a macro name or executable path",
                callee.span,
            ));
        }
        let arguments = split_arguments(&text[open + 1..text.len() - 1], offset + open + 1)?
            .into_iter()
            .map(|(text, at)| expression(text, at))
            .collect::<Result<Vec<_>, _>>()?;
        ExprKind::Call {
            callee: Box::new(callee),
            arguments,
        }
    } else if let Some(operator) = join_operator(text, &scan) {
        if scan.joins.len() > MAX_NESTING {
            return Err(Report::new(
                "path expression nesting is too deep",
                span(offset + operator, offset + operator + 1),
            )
            .help(
                "Bind an intermediate path; one expression supports at most 128 slash operators.",
            ));
        }
        let base = expression(&text[..operator], offset)?;
        let suffix = &text[operator + 1..];
        let mode = if suffix.trim().is_empty() {
            DestinationMode::Container
        } else if suffix.trim() == "." {
            DestinationMode::ReplaceTree
        } else {
            DestinationMode::Exact
        };
        let suffix = if suffix.trim().is_empty() {
            None
        } else {
            Some(Box::new(expression(suffix, offset + operator + 1)?))
        };
        ExprKind::Join {
            base: Box::new(base),
            suffix,
            operator: span(offset + operator, offset + operator + 1),
            mode,
        }
    } else if text.starts_with('"') {
        let (value, source) = quoted(text, offset)?;
        provenance = source;
        ExprKind::Literal(value)
    } else {
        let (qualifier, suffix, suffix_offset) =
            if let Some((qualifier, suffix)) = text.split_once("::").filter(|_| !url_atom(text)) {
                let qualifier_name = qualifier_name(qualifier, offset)?;
                (Some(qualifier_name), suffix, offset + qualifier.len() + 2)
            } else {
                (None, text, offset)
            };
        let value = if suffix.starts_with('"') {
            let (value, source) = quoted(suffix, suffix_offset)?;
            provenance = source;
            value
        } else {
            if let Some(index) = suffix.find('"') {
                return Err(Report::new("quote the complete path or namespace suffix", span(suffix_offset + index, suffix_offset + index + 1))
                    .help("For a file source with spaces, write '$local::\"folder name/file.txt\"'; bare quoted sources are literal streams."));
            }
            if suffix.is_empty() {
                return Err(Report::new(
                    "expected a path after '::'",
                    span(suffix_offset, suffix_offset),
                ));
            }
            provenance = OperandSource::unmapped(span(suffix_offset, suffix_offset + suffix.len()));
            suffix.to_owned()
        };
        path(qualifier, value)
    };
    if let ExprKind::Path(path) = &kind
        && super::path_boundary_whitespace(&path.path)
    {
        return Err(Report::new("path components cannot begin or end with whitespace", provenance.content)
            .help("Keep internal spaces inside a component; boundary whitespace is ignored only outside quotes. Bare quoted sources remain literal data."));
    }
    Ok(Expr {
        kind,
        span: range,
        source: provenance,
    })
}

fn join_operator(text: &str, scan: &Scan) -> Option<usize> {
    // Schemes and UNC roots are atomic endpoint/path identities. Slash tokens
    // within quotes, selectors and call argument lists never enter this list.
    let url = url_atom(text);
    if text.starts_with("//")
        || text
            .split_once("::")
            .is_some_and(|(_, suffix)| suffix.starts_with("//"))
    {
        return None;
    }
    let url_end = if url {
        text.find(char::is_whitespace).unwrap_or(text.len())
    } else {
        0
    };
    scan.joins
        .iter()
        .rev()
        .copied()
        .find(|index| *index != 0 && *index >= url_end && !text[..*index].ends_with("::"))
}

fn url_atom(text: &str) -> bool {
    text.split_once("://").is_some_and(|(scheme, _)| {
        !scheme.is_empty()
            && scheme
                .chars()
                .all(|ch| ch.is_ascii_alphanumeric() || matches!(ch, '+' | '-' | '.'))
    })
}

fn path(qualifier: Option<Name>, path: String) -> ExprKind {
    ExprKind::Path(PathExpr {
        qualifier,
        path,
        mode: DestinationMode::Exact,
    })
}

fn qualifier_name(raw: &str, offset: usize) -> Result<Name, Report> {
    let bare = raw.strip_prefix('$').unwrap_or(raw);
    if bare.is_empty()
        || !bare
            .chars()
            .all(|ch| ch.is_alphanumeric() || matches!(ch, '.' | '_' | '-'))
    {
        return Err(Report::new(
            "expected a namespace or program name before '::'",
            span(offset, offset + raw.len()),
        ));
    }
    Ok(Name {
        text: raw.to_owned(),
        span: span(offset, offset + raw.len()),
    })
}

fn trimmed(raw: &str, offset: usize) -> (&str, usize) {
    (raw.trim(), offset + raw.len() - raw.trim_start().len())
}

fn quoted(text: &str, offset: usize) -> Result<(String, OperandSource), Report> {
    let mut value = String::new();
    let mut offsets = Vec::new();
    let mut chars = text.char_indices();
    chars.next();
    while let Some((index, ch)) = chars.next() {
        if ch == '"' {
            if index + 1 != text.len() {
                return Err(Report::new("unexpected text after quoted expression", span(offset + index + 1, offset + text.len()))
                    .help("Keep the complete path inside its quotes; use a namespace prefix outside them."));
            }
            offsets.push(offset + index);
            return Ok((
                value,
                OperandSource::mapped(
                    span(offset, offset + text.len()),
                    span(offset + 1, offset + index),
                    offsets,
                ),
            ));
        }
        if ch != '\\' {
            value.push(ch);
            offsets.extend((0..ch.len_utf8()).map(|byte| offset + index + byte));
            continue;
        }
        let before = value.len();
        match chars.next() {
            Some((_, 'n')) => value.push('\n'),
            Some((_, 'r')) => value.push('\r'),
            Some((_, 't')) => value.push('\t'),
            Some((_, ch @ ('"' | '\\'))) => value.push(ch),
            Some((_, ch)) => {
                value.push('\\');
                value.push(ch);
            }
            None => break,
        }
        offsets.extend(std::iter::repeat_n(offset + index, value.len() - before));
    }
    Err(
        Report::new("unterminated quoted expression", span(offset, offset + 1))
            .label(
                span(offset + text.len(), offset + text.len()),
                "insert closing quote here",
            )
            .help(
                "Close the expression with a double quote; use \\\" for a quote inside the value.",
            ),
    )
}

struct Scan {
    operators: Vec<(usize, &'static str)>,
    commas: Vec<usize>,
    first_call: Option<usize>,
    call_close: Option<usize>,
    joins: Vec<usize>,
}

fn scan(text: &str, offset: usize) -> Result<Scan, Report> {
    let mut result = Scan {
        operators: Vec::new(),
        commas: Vec::new(),
        first_call: None,
        call_close: None,
        joins: Vec::new(),
    };
    let bytes = text.as_bytes();
    let mut quote = None;
    let mut selector = None;
    let mut escaped = false;
    let mut character_class = false;
    let mut parentheses = Vec::new();
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
            } else if byte == b'[' {
                character_class = true;
            } else if byte == b']' {
                character_class = false;
            } else if byte == b'>' && !character_class && selector_end(bytes, index) {
                selector = None;
            }
            index += 1;
            continue;
        }
        match byte {
            b'/' if parentheses.is_empty() => result.joins.push(index),
            b'"' => quote = Some(index),
            b'<' => selector = Some(index),
            b'(' => {
                if parentheses.len() >= MAX_NESTING {
                    return Err(Report::new("call nesting is too deep", span(offset + index, offset + index + 1))
                        .help("Use no more than 128 nested argument lists; bind an intermediate value instead."));
                }
                if parentheses.is_empty() && result.first_call.is_none() {
                    result.first_call = Some(index);
                }
                parentheses.push(index);
            }
            b')' => {
                if parentheses.pop().is_none() {
                    return Err(Report::new(
                        "unmatched closing ')'",
                        span(offset + index, offset + index + 1),
                    )
                    .help("Remove this ')' or add its matching '(' before the arguments."));
                }
                if parentheses.is_empty() && result.call_close.is_none() {
                    result.call_close = Some(index);
                }
            }
            b',' if parentheses.is_empty() => result.commas.push(index),
            _ if parentheses.is_empty() => {
                let operator = match bytes.get(index..index + 2) {
                    Some(b"=>") => Some("=>"),
                    Some(b":=") => Some(":="),
                    Some(b"->") => Some("->"),
                    _ => None,
                };
                if let Some(operator) = operator {
                    result.operators.push((index, operator));
                    index += 1;
                }
            }
            _ => {}
        }
        index += 1;
    }
    if let Some(start) = quote {
        return Err(Report::new(
            "unterminated quoted expression",
            span(offset + start, offset + start + 1),
        )
        .label(
            span(offset + text.len(), offset + text.len()),
            "insert closing quote here",
        )
        .help("Close the expression with a double quote; escape embedded quotes as \\\"."));
    }
    if let Some(start) = selector {
        return Err(Report::new(
            "unterminated selector",
            span(offset + start, offset + text.len()),
        )
        .help("Close regex character classes with ']' and the selector with '>'."));
    }
    if let Some(start) = parentheses.last() {
        return Err(Report::new(
            "unclosed argument list",
            span(offset + start, offset + start + 1),
        )
        .label(
            span(offset + text.len(), offset + text.len()),
            "insert closing ')' here",
        )
        .help("Every call or parameter list needs a closing ')'."));
    }
    Ok(result)
}

fn selector_end(bytes: &[u8], index: usize) -> bool {
    bytes
        .get(index + 1)
        .is_none_or(|next| next.is_ascii_whitespace() || matches!(next, b'/' | b'\\' | b',' | b')'))
        || matches!(bytes.get(index + 1..index + 3), Some(b"->" | b"=>" | b":="))
}

fn split_arguments(text: &str, offset: usize) -> Result<Vec<(&str, usize)>, Report> {
    let scan = scan(text, offset)?;
    if text.trim().is_empty() {
        return Ok(Vec::new());
    }
    let mut result = Vec::new();
    let mut start = 0;
    for end in scan.commas.into_iter().chain(std::iter::once(text.len())) {
        if text[start..end].trim().is_empty() {
            // A single trailing comma is valid, but an omitted argument is not.
            if end == text.len() && start > 0 {
                break;
            }
            return Err(Report::new(
                "missing argument between commas",
                span(offset + start, offset + end),
            )
            .help("Insert an argument or remove the extra comma."));
        }
        result.push((&text[start..end], offset + start));
        start = end + 1;
    }
    Ok(result)
}

fn comment_start(text: &str) -> Option<usize> {
    let bytes = text.as_bytes();
    let mut quoted = false;
    let mut selector = false;
    let mut escaped = false;
    let mut character_class = false;
    for (index, &byte) in bytes.iter().enumerate() {
        if escaped {
            escaped = false;
            continue;
        }
        if (quoted || selector) && byte == b'\\' {
            escaped = true;
            continue;
        }
        if quoted {
            if byte == b'"' {
                quoted = false;
            }
            continue;
        }
        if selector {
            if byte == b'[' {
                character_class = true;
            } else if byte == b']' {
                character_class = false;
            } else if byte == b'>' && !character_class && selector_end(bytes, index) {
                selector = false;
            }
            continue;
        }
        match byte {
            b'"' => quoted = true,
            b'<' => selector = true,
            b'#' if index == 0 || bytes[index - 1].is_ascii_whitespace() => return Some(index),
            _ => {}
        }
    }
    None
}
