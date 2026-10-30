//! Line-oriented syntax and canonical, comment-preserving formatting for msync.

use std::fmt;
use unicode_width::UnicodeWidthStr;

/// A half-open UTF-8 byte range in the original source.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
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

/// Structured presentation data, separate from the stable basic diagnostic API.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Report {
    pub diagnostic: Diagnostic,
    pub labels: Vec<Label>,
    pub help: Vec<String>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Label {
    pub span: Span,
    pub message: String,
}

impl Report {
    pub fn new(message: impl Into<String>, span: Span) -> Self {
        Self {
            diagnostic: Diagnostic {
                message: message.into(),
                span,
            },
            labels: Vec::new(),
            help: Vec::new(),
        }
    }

    pub fn label(mut self, span: Span, message: impl Into<String>) -> Self {
        self.labels.push(Label {
            span,
            message: message.into(),
        });
        self
    }

    pub fn help(mut self, help: impl Into<String>) -> Self {
        self.help.push(help.into());
        self
    }
}

impl From<Diagnostic> for Report {
    fn from(diagnostic: Diagnostic) -> Self {
        Self {
            diagnostic,
            labels: Vec::new(),
            help: Vec::new(),
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Script {
    pub statements: Vec<Statement>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Statement {
    pub kind: StatementKind,
    pub span: Span,
    pub operands: Option<Operands>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Operands {
    pub left: OperandSource,
    pub operator: Span,
    pub right: OperandSource,
}

/// Maps semantic operand byte intervals back to original source, including escapes.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct OperandSource {
    pub span: Span,
    pub content: Span,
    decoded_offsets: Option<Vec<usize>>,
}

impl OperandSource {
    pub fn unmapped(span: Span) -> Self {
        Self {
            span,
            content: span,
            decoded_offsets: None,
        }
    }

    pub fn interval(&self, start: usize, end: usize) -> Span {
        match &self.decoded_offsets {
            Some(offsets) => Span {
                start: offsets[start.min(offsets.len() - 1)],
                end: offsets[end.min(offsets.len() - 1)],
            },
            None => Span {
                start: (self.content.start + start).min(self.content.end),
                end: (self.content.start + end).min(self.content.end),
            },
        }
    }
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
    parse_rich(source).map_err(|reports| {
        reports
            .into_iter()
            .map(|report| report.diagnostic)
            .collect()
    })
}

pub fn parse_rich(source: &str) -> Result<Script, Vec<Report>> {
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

fn parse_line(line: &str, offset: usize, script: &mut Script, errors: &mut Vec<Report>) {
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
            operands: None,
        });
    }
}

fn scan_line(line: &str, offset: usize) -> Result<LineScan, Report> {
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
            offset + start + 1,
        )
        .label(
            Span {
                start: offset + line.len(),
                end: offset + line.len(),
            },
            "insert closing quote here",
        )
        .help("Close the operand with a double quote; escape embedded quotes as \\\"."));
    }
    if let Some(start) = selector {
        let end = recovery_operand_end(line, start);
        let regex_start = (start + 1).min(end);
        let regex_end = end
            .saturating_sub(usize::from(line[..end].ends_with('>')))
            .max(regex_start);
        let message = if character_class {
            "unterminated regex character class or selector"
        } else {
            "unterminated regex selector; expected '>'"
        };
        return Err(error(message, offset + regex_start, offset + regex_end)
            .help("Close regex character classes with ']' and the selector with '>'; escape a literal greater-than sign as \\>."));
    }
    Ok(scan)
}

fn recovery_operand_end(line: &str, start: usize) -> usize {
    let bytes = line.as_bytes();
    for index in start + 1..bytes.len() {
        if bytes[index - 1].is_ascii_whitespace() && operator_at(&bytes[index..]).is_some() {
            return line[..index].trim_end().len();
        }
    }
    line.trim_end().len()
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
) -> Result<Statement, Report> {
    let [(position, operator)] = operators else {
        let message = if operators.is_empty() {
            "expected '=>', ':=' or '->'"
        } else {
            "expected one statement per line; quote operands containing operators"
        };
        let span = if let Some((position, _)) = operators.get(1) {
            Span {
                start: offset + position,
                end: offset + position + 2,
            }
        } else {
            malformed_operator_span(code, offset)
        };
        return Err(Report::new(message, span).help("Use 'source -> destination' to copy, 'name => endpoint' to bind an endpoint, or 'name := path' to define a path alias."));
    };
    let (left, left_source) = operand(&code[..*position], offset)?;
    let (right, right_source) = operand(&code[position + 2..], offset + position + 2)?;
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
        operands: Some(Operands {
            left: left_source,
            operator: Span {
                start: offset + position,
                end: offset + position + 2,
            },
            right: right_source,
        }),
    })
}

fn malformed_operator_span(code: &str, offset: usize) -> Span {
    let mut cursor = 0;
    for word in code.split_whitespace() {
        let relative = code[cursor..].find(word).unwrap_or(0) + cursor;
        if word.chars().all(|ch| "-:=<>".contains(ch)) {
            return Span {
                start: offset + relative,
                end: offset + relative + word.len(),
            };
        }
        cursor = relative + word.len();
    }
    Span {
        start: offset + code.trim_end().len(),
        end: offset + code.trim_end().len(),
    }
}

fn operand(raw: &str, offset: usize) -> Result<(String, OperandSource), Report> {
    let value = raw.trim();
    let start = offset + raw.len() - raw.trim_start().len();
    let end = start + value.len();
    if value.is_empty() {
        return Err(error("expected a non-empty operand", start, end).help("Insert a path or binding name here; use '$pwd::relative/path' for a path in the current directory."));
    }
    if !value.starts_with('"') {
        if value.contains('"') {
            return Err(error(
                "quote the entire operand, not individual path components",
                start,
                end,
            ));
        }
        let span = Span { start, end };
        return Ok((
            value.to_owned(),
            OperandSource {
                span,
                content: span,
                decoded_offsets: None,
            },
        ));
    }
    let mut decoded = String::with_capacity(value.len() - 2);
    let mut decoded_offsets = Vec::with_capacity(value.len());
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
            decoded_offsets.push(start + index + 1);
            return Ok((
                decoded,
                OperandSource {
                    span: Span { start, end },
                    content: Span {
                        start: start + 1,
                        end: end - 1,
                    },
                    decoded_offsets: Some(decoded_offsets),
                },
            ));
        }
        let before = decoded.len();
        if ch != '\\' {
            decoded.push(ch);
            decoded_offsets.extend((0..ch.len_utf8()).map(|byte| start + index + 1 + byte));
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
        decoded_offsets.extend(std::iter::repeat_n(
            start + index + 1,
            decoded.len() - before,
        ));
    }
    Err(error("unterminated quoted operand", start, end))
}

fn error(message: &str, start: usize, end: usize) -> Report {
    Report::new(message, Span { start, end })
}

/// Canonicalize spacing and quoting while retaining every comment and operand.
pub fn format(source: &str) -> Result<String, Vec<Diagnostic>> {
    format_rich(source).map_err(|reports| {
        reports
            .into_iter()
            .map(|report| report.diagnostic)
            .collect()
    })
}

pub fn format_rich(source: &str) -> Result<String, Vec<Report>> {
    let mut trace = crate::telemetry::span("format");
    let script = parse_rich(source).inspect_err(|_| trace.error())?;
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

pub(crate) fn write_operand(output: &mut String, value: &str) {
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
    render_primary(source, diagnostic, false)
}

/// Render structured labels and constructive help; callers own terminal policy.
pub fn render_report(source: &str, report: &Report, color: bool) -> String {
    render_rich(source, report, None, color)
}

/// Render a report with one named location, without a second outer error banner.
pub fn render_report_named(source: &str, report: &Report, name: &str, color: bool) -> String {
    render_rich(source, report, Some(name), color)
}

struct SourceLine<'a> {
    start: usize,
    text: &'a str,
}

struct Annotation<'a> {
    span: Span,
    messages: Vec<&'a str>,
    primary: bool,
}

fn render_rich(source: &str, report: &Report, name: Option<&str>, color: bool) -> String {
    let lines = source_lines(source);
    let annotations = report_annotations(source, report);
    let primary = annotations[0].span;
    let first = line_number_at(source, primary.start);
    let visible_start = primary
        .start
        .min(lines[first].start + lines[first].text.len());
    let column = source[lines[first].start..visible_start].chars().count() + 1;
    let location = match name {
        Some(name) => format!("{}:{}:{column}", display_metadata(name), first + 1),
        None => format!("{}:{column}", first + 1),
    };
    let mut output = format!(
        "{} {}\n {} {}",
        paint("error:", "1;31", color),
        display_metadata(&report.diagnostic.message),
        paint("-->", "1;34", color),
        paint(&location, "1;34", color),
    );
    let selected = report_lines(source, &annotations, lines.len());
    let gutter = (selected.last().copied().unwrap_or(0) + 1)
        .to_string()
        .len();
    output.push_str(&format!("\n{:gutter$} {}", "", paint("|", "1;34", color)));
    let mut previous = None;
    for index in selected {
        if previous.is_some_and(|last| index > last + 1) {
            output.push_str(&format!("\n{:gutter$} {}", "", paint("...", "2;34", color)));
        }
        render_source_line(
            &mut output,
            source,
            &lines[index],
            index,
            gutter,
            &annotations,
            color,
        );
        previous = Some(index);
    }
    for help in &report.help {
        render_help(&mut output, help, gutter, color);
    }
    output
}

fn render_help(output: &mut String, help: &str, gutter: usize, color: bool) {
    let mut lines = help.split_inclusive('\n');
    let first = without_line_ending(lines.next().unwrap_or_default());
    output.push_str(&format!(
        "\n{:gutter$} = {} {}",
        "",
        paint("help:", "1;36", color),
        display_metadata(first),
    ));
    for line in lines {
        output.push_str(&format!(
            "\n{}{}",
            " ".repeat(gutter + 9),
            display_metadata(without_line_ending(line)),
        ));
    }
}

fn source_lines(source: &str) -> Vec<SourceLine<'_>> {
    let mut lines = Vec::new();
    let mut start = 0;
    for piece in source.split_inclusive('\n') {
        let text = without_line_ending(piece);
        lines.push(SourceLine { start, text });
        start += piece.len();
    }
    if lines.is_empty() || source.ends_with('\n') {
        lines.push(SourceLine { start, text: "" });
    }
    lines
}

fn without_line_ending(piece: &str) -> &str {
    match piece.strip_suffix('\n') {
        Some(line) => line.strip_suffix('\r').unwrap_or(line),
        None => piece,
    }
}

fn report_annotations<'a>(source: &str, report: &'a Report) -> Vec<Annotation<'a>> {
    let mut annotations = vec![Annotation {
        span: normalized_span(source, report.diagnostic.span),
        messages: report
            .labels
            .iter()
            .filter(|label| label.span == report.diagnostic.span)
            .map(|label| label.message.as_str())
            .collect(),
        primary: true,
    }];
    annotations.extend(
        report
            .labels
            .iter()
            .filter(|label| label.span != report.diagnostic.span)
            .map(|label| Annotation {
                span: normalized_span(source, label.span),
                messages: vec![label.message.as_str()],
                primary: false,
            }),
    );
    annotations
}

fn normalized_span(source: &str, span: Span) -> Span {
    let mut start = span.start.min(source.len());
    while !source.is_char_boundary(start) {
        start -= 1;
    }
    let mut end = span.end.min(source.len()).max(start);
    while !source.is_char_boundary(end) {
        end -= 1;
    }
    Span { start, end }
}

fn annotation_lines(source: &str, span: Span) -> (usize, usize) {
    let last = if span.end > span.start {
        span.end - 1
    } else {
        span.start
    };
    (
        line_number_at(source, span.start),
        line_number_at(source, last),
    )
}

fn report_lines(
    source: &str,
    annotations: &[Annotation<'_>],
    count: usize,
) -> std::collections::BTreeSet<usize> {
    let mut selected = std::collections::BTreeSet::new();
    for annotation in annotations {
        let (first, last) = annotation_lines(source, annotation.span);
        selected.extend(first.saturating_sub(1)..=(first + 1).min(count - 1));
        selected.extend(last.saturating_sub(1)..=(last + 1).min(count - 1));
        if last - first <= 3 {
            selected.extend(first..=last);
        }
    }
    selected
}

fn render_source_line(
    output: &mut String,
    source: &str,
    line: &SourceLine<'_>,
    index: usize,
    gutter: usize,
    annotations: &[Annotation<'_>],
    color: bool,
) {
    let displayed = display_text(line.text);
    let code = crate::highlight::highlight(&displayed, color);
    output.push_str(&format!(
        "\n{:gutter$} {} {code}",
        index + 1,
        paint("|", "1;34", color)
    ));
    for annotation in annotations {
        let (first, last) = annotation_lines(source, annotation.span);
        if !(first..=last).contains(&index) {
            continue;
        }
        let start = annotation
            .span
            .start
            .saturating_sub(line.start)
            .min(line.text.len());
        let end = annotation
            .span
            .end
            .saturating_sub(line.start)
            .min(line.text.len())
            .max(start);
        let column = UnicodeWidthStr::width(display_text(&line.text[..start]).as_str());
        let right = UnicodeWidthStr::width(display_text(&line.text[..end]).as_str());
        let (marker, style) = if annotation.primary {
            ('^', "1;31")
        } else {
            ('-', "1;36")
        };
        let mark = marker
            .to_string()
            .repeat(right.saturating_sub(column).max(1));
        output.push_str(&format!(
            "\n{:gutter$} {} {}{}",
            "",
            paint("|", "1;34", color),
            " ".repeat(column),
            paint(&mark, style, color)
        ));
        if index == last && !annotation.messages.is_empty() {
            let message = annotation
                .messages
                .iter()
                .map(|text| display_metadata(text))
                .collect::<Vec<_>>()
                .join("; ");
            output.push_str(&format!(" {message}"));
        }
    }
}

fn line_number_at(source: &str, position: usize) -> usize {
    source.as_bytes()[..position.min(source.len())]
        .iter()
        .filter(|byte| **byte == b'\n')
        .count()
}

fn paint(text: &str, style: &str, color: bool) -> String {
    if color {
        format!("\x1b[{style}m{text}\x1b[0m")
    } else {
        text.to_owned()
    }
}

fn render_primary(source: &str, diagnostic: &Diagnostic, color: bool) -> String {
    let span = normalized_span(source, diagnostic.span);
    let start = span.start;
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
    let end = span.end.min(line_end).max(start);
    let prefix = display_text(&source[line_start..start]);
    let highlighted = display_text(&source[line_start..end]);
    let display_column = UnicodeWidthStr::width(prefix.as_str());
    let width = UnicodeWidthStr::width(highlighted.as_str())
        .saturating_sub(display_column)
        .max(1);
    let mut line = display_text(line);
    if color && end > start {
        let before = prefix.len().min(line.len());
        let after = highlighted.len().min(line.len());
        line = format!(
            "{}{}{}",
            &line[..before],
            paint(&line[before..after], "1;31", true),
            &line[after..]
        );
    }
    let gutter = number.to_string().len();
    format!(
        "{} {}\n {} {}:{}\n{:gutter$} {}\n{} {} {}\n{:gutter$} {} {}{}",
        paint("error:", "1;31", color),
        display_text(&diagnostic.message),
        paint("-->", "1;34", color),
        number,
        column + 1,
        "",
        paint("|", "1;34", color),
        number,
        paint("|", "1;34", color),
        line,
        "",
        paint("|", "1;34", color),
        " ".repeat(display_column),
        paint(&"^".repeat(width), "1;31", color)
    )
}

fn display_text(text: &str) -> String {
    let mut output = String::with_capacity(text.len());
    for ch in text.chars() {
        if ch == '\t' {
            let width = 4 - UnicodeWidthStr::width(output.as_str()) % 4;
            output.extend(std::iter::repeat_n(' ', width));
        } else if ch.is_control() || bidi_control(ch) {
            let escaped = ch.escape_default().to_string();
            output.push_str(&escaped);
        } else {
            output.push(ch);
        }
    }
    output
}

fn display_metadata(text: &str) -> String {
    text.chars()
        .map(|ch| {
            if ch.is_control() || bidi_control(ch) {
                ch.escape_default().to_string()
            } else {
                ch.to_string()
            }
        })
        .collect()
}

fn bidi_control(ch: char) -> bool {
    matches!(ch, '\u{061c}' | '\u{200e}' | '\u{200f}' | '\u{202a}'..='\u{202e}' | '\u{2066}'..='\u{2069}')
}
