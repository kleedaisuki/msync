//! Tolerant display tokens for live input, examples and diagnostic source lines.
//!
//! This scanner never accepts or rejects a program. The syntax parser remains
//! the execution grammar; incomplete edits still receive useful display tokens.

use crate::syntax::Span;

pub const COMMANDS: &[&str] = &[
    "/export-ir",
    "/export-relocatable",
    "/load-ir",
    "/help",
    "/?",
    "/quit",
    "/exit",
    "/clear",
    "/bash",
    "/pwsh",
    "/pwd",
    "/cd",
    "/bindings",
    "/reset",
    "/dry-run",
    "/load",
    "/check",
    "/plan",
    "/fmt",
    "/limits",
    "/syntax",
    "/jobs",
    "/wait",
    "/stop",
    "/kill",
];

pub const BUILTINS: &[&str] = &[
    "$pwd::pwd",
    "$file::this",
    "$file::parent",
    "$local::",
    "$wsl::",
    "$path::",
    "$env::",
];

pub const KEYWORDS: &[&str] = &[
    "macro", "symbol", "boolean", "return", "global", "drop", "delete", "exists", "include",
    "execute", "if", "else", "for", "in", "loop", "break", "true", "false", "and", "not", "or",
    "release", "acquire", "acqrel", "async", "await",
];

pub const RANGE_ADAPTORS: &[&str] = &[
    "ranges::of",
    "views::single",
    "views::all",
    "views::as_const",
    "views::as_rvalue",
    "views::common",
    "views::filter",
    "views::transform",
    "views::take",
    "views::drop",
    "views::take_while",
    "views::drop_while",
    "views::stride",
    "views::step",
    "views::unique",
    "views::reverse",
    "ranges::empty",
    "ranges::contains",
    "ranges::any_of",
    "ranges::any",
    "ranges::all_of",
    "ranges::none_of",
    "ranges::none",
    "ranges::front",
    "ranges::first",
    "ranges::back",
    "ranges::last",
    "ranges::find",
    "ranges::find_if",
];

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TokenKind {
    Binding,
    Namespace,
    Operator,
    String,
    Selector,
    Comment,
    Endpoint,
    /// REPL commands and structured-language control directives.
    Command,
    Text,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Token {
    pub span: Span,
    pub kind: TokenKind,
}

pub fn token_spans(source: &str) -> Vec<Token> {
    let mut output = Vec::new();
    let mut offset = 0;
    for line in crate::syntax::physical_lines(source) {
        let begin = output.len();
        line_tokens(line, &mut output);
        for token in &mut output[begin..] {
            token.span.start += offset;
            token.span.end += offset;
        }
        offset += line.len();
    }
    output
}

fn line_tokens(source: &str, tokens: &mut Vec<Token>) {
    if shell_tokens(source, tokens) {
        return;
    }
    let mut position = 0;
    let first = source.len() - source.trim_start().len();
    let mut head = true;
    let declaration = ["macro ", "symbol ", "boolean "]
        .iter()
        .any(|keyword| source[first..].starts_with(keyword));
    let mut parameters = false;
    let mut for_name = source[first..].starts_with("for ");
    while position < source.len() {
        let start = position;
        let ch = source[position..].chars().next().unwrap();
        let (end, kind) = match ch {
            '#' if comment_boundary(source, position) => (
                source[position..]
                    .find('\n')
                    .map_or(source.len(), |end| position + end),
                TokenKind::Comment,
            ),
            '"' => (quoted_end(source, position), TokenKind::String),
            '<' if selector_start(source, position) => {
                (selector_end(source, position), TokenKind::Selector)
            }
            '(' | ')' | ',' => {
                if declaration && ch != ',' {
                    parameters = ch == '(';
                }
                (position + 1, TokenKind::Text)
            }
            ':' if header_colon(source, position) => (position + 1, TokenKind::Text),
            _ if operator_at(source, position) => (
                position + operator_len(source, position),
                TokenKind::Operator,
            ),
            _ if ch.is_whitespace() => (whitespace_end(source, position), TokenKind::Text),
            _ => {
                position = word_end(source, position);
                let word = &source[start..position];
                let parameter = parameters || for_name && start != first;
                word_tokens(source, start, position, head, parameter, tokens);
                if start != first && for_name {
                    for_name = false;
                }
                head &= scheduling_word(word);
                continue;
            }
        };
        position = end;
        tokens.push(Token {
            span: Span { start, end },
            kind,
        });
    }
}

fn shell_tokens(source: &str, tokens: &mut Vec<Token>) -> bool {
    let start = source.len() - source.trim_start().len();
    let Some(command) = source[start..].split_whitespace().next() else {
        return false;
    };
    if !matches!(command, "/bash" | "/pwsh" | ":bash" | ":pwsh") {
        return false;
    }
    let end = start + command.len();
    if operator_at(source[end..].trim_start(), 0) {
        return false;
    }
    if start != 0 {
        tokens.push(Token {
            span: Span {
                start: 0,
                end: start,
            },
            kind: TokenKind::Text,
        });
    }
    tokens.push(Token {
        span: Span { start, end },
        kind: TokenKind::Command,
    });
    if end < source.len() {
        tokens.push(Token {
            span: Span {
                start: end,
                end: source.len(),
            },
            kind: TokenKind::Text,
        });
    }
    true
}

fn whitespace_end(source: &str, start: usize) -> usize {
    source[start..]
        .char_indices()
        .find(|(_, ch)| !ch.is_whitespace())
        .map_or(source.len(), |(offset, _)| start + offset)
}

fn operator_at(source: &str, position: usize) -> bool {
    operator_len(source, position) != 0
}

fn operator_len(source: &str, position: usize) -> usize {
    match source.as_bytes().get(position..position + 2) {
        Some(b"->" | b"=>" | b":=" | b"==" | b"!=" | b"<=" | b">=") => 2,
        _ => usize::from(matches!(
            source.as_bytes().get(position),
            Some(b'=' | b'<' | b'>' | b'|')
        )),
    }
}

fn selector_start(source: &str, position: usize) -> bool {
    let suffix = &source[position + 1..];
    if suffix.starts_with('=') || suffix.starts_with(char::is_whitespace) {
        return false;
    }
    // Selectors occur as path components; whitespace-separated '<' compares.
    position == 0
        || source
            .as_bytes()
            .get(position - 1)
            .is_some_and(|byte| matches!(byte, b'/' | b'\\' | b':' | b'(' | b','))
        || source
            .as_bytes()
            .get(position - 1)
            .is_some_and(u8::is_ascii_whitespace)
            && !suffix.starts_with('"')
}

fn comment_boundary(source: &str, position: usize) -> bool {
    position == 0 || source.as_bytes()[position - 1].is_ascii_whitespace()
}

fn quoted_end(source: &str, start: usize) -> usize {
    let mut escaped = false;
    for (offset, ch) in source[start + 1..].char_indices() {
        let end = start + 1 + offset + ch.len_utf8();
        if ch == '\n' {
            return end - 1;
        }
        if ch == '"' && !escaped {
            return end;
        }
        escaped = ch == '\\' && !escaped;
    }
    source.len()
}

fn selector_end(source: &str, start: usize) -> usize {
    let mut escaped = false;
    let mut class = false;
    for (offset, ch) in source[start + 1..].char_indices() {
        let position = start + 1 + offset;
        if ch == '\n' {
            return position;
        }
        if escaped {
            escaped = false;
            continue;
        }
        match ch {
            '\\' => escaped = true,
            '[' => class = true,
            ']' => class = false,
            '>' if !class && selector_boundary(source, position + 1) => return position + 1,
            _ => {}
        }
    }
    source.len()
}

fn selector_boundary(source: &str, position: usize) -> bool {
    position == source.len()
        || source[position..].starts_with(['/', '\\', ',', ')'])
        || source
            .as_bytes()
            .get(position)
            .is_some_and(u8::is_ascii_whitespace)
        || operator_at(source, position)
}

fn word_end(source: &str, start: usize) -> usize {
    for (offset, ch) in source[start..].char_indices() {
        let position = start + offset;
        if ch.is_whitespace()
            || matches!(ch, '"' | '<' | '(' | ')' | ',')
            || operator_at(source, position)
            || ch == ':' && header_colon(source, position)
        {
            return position;
        }
    }
    source.len()
}

fn header_colon(source: &str, position: usize) -> bool {
    source.as_bytes().get(position.wrapping_sub(1)) != Some(&b':')
        && source
            .as_bytes()
            .get(position + 1)
            .is_none_or(|byte| byte.is_ascii_whitespace() || *byte == b'#')
}

fn word_tokens(
    source: &str,
    start: usize,
    end: usize,
    head: bool,
    parameter: bool,
    tokens: &mut Vec<Token>,
) {
    let word = &source[start..end];
    let kind = if word.contains("://") {
        TokenKind::Endpoint
    } else if let Some(separator) = word.find("::") {
        let mut boundary = start + separator + 2;
        if matches!(&word[..separator], "$pwd" | "$file" | "$env") {
            boundary += source[boundary..end]
                .find(['/', '\\'])
                .unwrap_or(end - boundary);
        }
        tokens.push(Token {
            span: Span {
                start,
                end: boundary,
            },
            kind: TokenKind::Namespace,
        });
        if boundary < end {
            tokens.push(Token {
                span: Span {
                    start: boundary,
                    end,
                },
                kind: if source[end..].trim_start().starts_with('(')
                    || matches!(&word[..separator], "views" | "ranges")
                {
                    TokenKind::Binding
                } else {
                    TokenKind::Text
                },
            });
        }
        return;
    } else if word.starts_with('$') {
        TokenKind::Namespace
    } else if control_word(source, word, head, end) {
        TokenKind::Command
    } else if parameter
        || source[end..].trim_start().starts_with('(')
        || ["=>", ":=", "="]
            .iter()
            .any(|operator| source[end..].trim_start().starts_with(operator))
    {
        TokenKind::Binding
    } else {
        TokenKind::Text
    };
    tokens.push(Token {
        span: Span { start, end },
        kind,
    });
}

fn scheduling_word(word: &str) -> bool {
    matches!(word, "release" | "acquire" | "acqrel" | "async" | "await")
}

fn control_word(source: &str, word: &str, head: bool, end: usize) -> bool {
    if head && scheduling_word(word) {
        return true;
    }
    if matches!(
        word,
        "true" | "false" | "and" | "not" | "or" | "exists" | "in" | "if"
    ) {
        return true;
    }
    head && (matches!(
        word,
        "macro"
            | "symbol"
            | "boolean"
            | "return"
            | "execute"
            | "include"
            | "global"
            | "delete"
            | "drop"
            | "if"
            | "else"
            | "for"
            | "loop"
            | "break"
    ) || (is_command(word) && !operator_at(source[end..].trim_start(), 0)))
}

pub fn highlight(source: &str, color: bool) -> String {
    let mut output = String::with_capacity(source.len() + 64);
    if !color {
        visible(source, &mut output);
        return output;
    }
    for token in token_spans(source) {
        let style = match token.kind {
            TokenKind::Binding => "1;34",
            TokenKind::Namespace => "1;35",
            TokenKind::Operator => "1;33",
            TokenKind::String => "32",
            TokenKind::Selector => "35",
            TokenKind::Comment => "2;37",
            TokenKind::Endpoint => "34",
            TokenKind::Command => "1;36",
            TokenKind::Text => "",
        };
        let text = &source[token.span.start..token.span.end];
        if color && token.kind == TokenKind::String {
            quoted_display(text, &mut output);
            continue;
        }
        styled(text, style, color, &mut output);
    }
    output
}

fn styled(text: &str, style: &str, color: bool, output: &mut String) {
    if color && !style.is_empty() {
        output.push_str("\x1b[");
        output.push_str(style);
        output.push('m');
    }
    visible(text, output);
    if color && !style.is_empty() {
        output.push_str("\x1b[0m");
    }
}

fn quoted_display(text: &str, output: &mut String) {
    let mut chars = text.char_indices();
    let mut start = 0;
    while let Some((offset, ch)) = chars.next() {
        if ch != '\\' {
            continue;
        }
        let Some((next, escaped)) = chars.next() else {
            break;
        };
        // Unknown escapes retain their backslash in the grammar. They are
        // ordinary quoted text, not a warning or a recognized escape pair.
        if !matches!(escaped, '\\' | '"' | 'n' | 'r' | 't') {
            continue;
        }
        styled(&text[start..offset], "32", true, output);
        let end = next + escaped.len_utf8();
        styled(&text[offset..end], "1;36", true, output);
        start = end;
    }
    styled(&text[start..], "32", true, output);
}

fn visible(text: &str, output: &mut String) {
    let mut start = 0;
    for (index, ch) in text.char_indices() {
        if !ch.is_control() || matches!(ch, '\n' | '\t') {
            continue;
        }
        output.push_str(&text[start..index]);
        match ch {
            '\r' => output.push_str("\\r"),
            '\u{1b}' => output.push_str("\\x1b"),
            ch => output.extend(ch.escape_unicode()),
        }
        start = index + ch.len_utf8();
    }
    output.push_str(&text[start..]);
}

fn is_command(word: &str) -> bool {
    let Some(name) = word.strip_prefix('/').or_else(|| word.strip_prefix(':')) else {
        return false;
    };
    COMMANDS
        .iter()
        .any(|command| command.strip_prefix('/') == Some(name))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn structured_control_and_call_tokens_reuse_stable_display_roles() {
        for (source, word, role) in [
            ("macro copy(.src, des):", "macro", TokenKind::Command),
            ("macro copy(.src, des):", "copy", TokenKind::Binding),
            (
                "async await program => $path::bash(\"ls\")",
                "await",
                TokenKind::Command,
            ),
            ("copy($wsl::src, \"字节\")", "copy", TokenKind::Binding),
            (
                "program => $path::bash(\"-c\", \"cat\")",
                "bash",
                TokenKind::Binding,
            ),
            ("    await A", "await", TokenKind::Command),
        ] {
            assert!(
                token_spans(source).iter().any(|token| {
                    &source[token.span.start..token.span.end] == word && token.kind == role
                }),
                "{source}"
            );
        }
    }

    #[test]
    fn selectors_inside_arguments_end_before_commas_and_incomplete_calls_keep_progressing() {
        let source = "copy($wsl::dir/<[a-z]+>, \"你好\")";
        let selectors = token_spans(source)
            .into_iter()
            .filter(|token| token.kind == TokenKind::Selector)
            .map(|token| &source[token.span.start..token.span.end])
            .collect::<Vec<_>>();
        assert_eq!(selectors, ["<[a-z]+>"]);
        for source in [
            "copy(",
            "macro copy(.codex,",
            "async await P => f(",
            ")",
            ",",
        ] {
            let mut end = 0;
            for token in token_spans(source) {
                assert_eq!(token.span.start, end);
                assert!(token.span.end > token.span.start);
                end = token.span.end;
            }
            assert_eq!(end, source.len());
        }
    }

    #[test]
    fn tokens_cover_utf8_source_without_overlap_or_gaps() {
        for source in [
            "root := $pwd::pwd/文件",
            "dir/<[>#]+> -> \"out file\" # note",
            "\"unfinished\\",
            "/bash echo a -> b",
            "public => https://example.com/#fragment",
            "root := $pwd::pwd\n/help\n/bash echo a -> b\n",
        ] {
            let tokens = token_spans(source);
            let mut end = 0;
            for token in tokens {
                assert_eq!(token.span.start, end);
                assert!(source.is_char_boundary(token.span.end));
                end = token.span.end;
            }
            assert_eq!(end, source.len());
            assert_eq!(highlight(source, false), source);
        }
    }

    #[test]
    fn quoted_and_selector_operators_are_not_statement_tokens() {
        let source = "\"a->b # c\" -> dir/<[>#]+> # comment";
        let tokens = token_spans(source);
        assert_eq!(
            tokens
                .iter()
                .filter(|token| token.kind == TokenKind::Operator)
                .count(),
            1
        );
        assert_eq!(
            tokens
                .iter()
                .filter(|token| token.kind == TokenKind::Comment)
                .count(),
            1
        );
        assert_eq!(
            tokens
                .iter()
                .filter(|token| token.kind == TokenKind::Selector)
                .count(),
            1
        );
        assert!(highlight(source, true).contains("\x1b[1;33m->\x1b[0m"));
    }

    #[test]
    fn raw_terminal_controls_are_visible_not_executed() {
        assert_eq!(highlight("a\x1b[2J\r\0", false), "a\\x1b[2J\\r\\u{0}");
    }

    #[test]
    fn shell_payload_is_not_misrepresented_as_dsl_syntax() {
        let source = "/bash printf '%s' 'a -> b := c'";
        let tokens = token_spans(source);
        assert_eq!(tokens[0].kind, TokenKind::Command);
        assert_eq!(tokens[1].kind, TokenKind::Text);
        assert_eq!(highlight(source, false), source);
        let path_copy = token_spans("/bash -> output");
        assert!(
            !path_copy
                .iter()
                .any(|token| token.kind == TokenKind::Command)
        );
        assert!(
            path_copy
                .iter()
                .any(|token| token.kind == TokenKind::Operator)
        );
    }

    proptest::proptest! {
        #[test]
        fn arbitrary_edits_have_safe_complete_spans(source in ".*") {
            let tokens = token_spans(&source);
            let pieces: String = tokens.iter().map(|token| &source[token.span.start..token.span.end]).collect();
            proptest::prop_assert_eq!(pieces, source);
        }

        #[test]
        fn styles_never_change_the_visible_text(source in ".*") {
            let colored = highlight(&source, true);
            let styles = regex::Regex::new(r"\x1b\[[0-9;]*m").unwrap();
            proptest::prop_assert_eq!(styles.replace_all(&colored, "").into_owned(), highlight(&source, false));
        }
    }
}
