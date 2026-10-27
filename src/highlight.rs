//! Tolerant display tokens for live input, examples and diagnostic source lines.
//!
//! This scanner never accepts or rejects a program. The syntax parser remains
//! the execution grammar; incomplete edits still receive useful display tokens.

use crate::syntax::Span;

pub const COMMANDS: &[&str] = &[
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
    "/jobs",
    "/wait",
    "/stop",
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
    for line in source.split_inclusive('\n') {
        output.extend(line_tokens(line).into_iter().map(|mut token| {
            token.span.start += offset;
            token.span.end += offset;
            token
        }));
        offset += line.len();
    }
    output
}

fn line_tokens(source: &str) -> Vec<Token> {
    if let Some(tokens) = shell_tokens(source) {
        return tokens;
    }
    let mut tokens = Vec::new();
    let mut position = 0;
    let first = source.len() - source.trim_start().len();
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
            '<' => (selector_end(source, position), TokenKind::Selector),
            '(' | ')' | ',' => (position + 1, TokenKind::Text),
            _ if operator_at(source, position) => (position + 2, TokenKind::Operator),
            _ if ch.is_whitespace() => (whitespace_end(source, position), TokenKind::Text),
            _ => {
                position = word_end(source, position);
                word_tokens(source, start, position, start == first, &mut tokens);
                continue;
            }
        };
        position = end;
        tokens.push(Token {
            span: Span { start, end },
            kind,
        });
    }
    tokens
}

fn shell_tokens(source: &str) -> Option<Vec<Token>> {
    let start = source.len() - source.trim_start().len();
    let command = source[start..].split_whitespace().next()?;
    if !matches!(command, "/bash" | "/pwsh" | ":bash" | ":pwsh") {
        return None;
    }
    let end = start + command.len();
    if operator_at(source[end..].trim_start(), 0) {
        return None;
    }
    let mut tokens = Vec::new();
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
    Some(tokens)
}

fn whitespace_end(source: &str, start: usize) -> usize {
    source[start..]
        .char_indices()
        .find(|(_, ch)| !ch.is_whitespace())
        .map_or(source.len(), |(offset, _)| start + offset)
}

fn operator_at(source: &str, position: usize) -> bool {
    ["->", "=>", ":="]
        .iter()
        .any(|operator| source[position..].starts_with(operator))
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
        {
            return position;
        }
    }
    source.len()
}

fn word_tokens(source: &str, start: usize, end: usize, first: bool, tokens: &mut Vec<Token>) {
    let word = &source[start..end];
    let kind = if word.contains("://") {
        TokenKind::Endpoint
    } else if let Some(separator) = word.find("::") {
        let boundary = start + separator + 2;
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
                kind: if source[end..].trim_start().starts_with('(') {
                    TokenKind::Binding
                } else {
                    TokenKind::Text
                },
            });
        }
        return;
    } else if word.starts_with('$') {
        TokenKind::Namespace
    } else if control_word(source, word, first, end) {
        TokenKind::Command
    } else if source[end..].trim_start().starts_with('(')
        || ["=>", ":="]
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

fn control_word(source: &str, word: &str, first: bool, end: usize) -> bool {
    if source.trim_start().starts_with("transaction ")
        && matches!(
            word,
            "read" | "uncommitted" | "committed" | "repeatable" | "serializable"
        )
    {
        return true;
    }
    first
        && (matches!(
            word,
            "macro" | "transaction" | "commit" | "rollback" | "global"
        ) || (COMMANDS.contains(&word) && !operator_at(source[end..].trim_start(), 0)))
}

pub fn highlight(source: &str, color: bool) -> String {
    let mut output = String::with_capacity(source.len() + 64);
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
        if color && !style.is_empty() {
            output.push_str(&format!("\x1b[{style}m"));
        }
        visible(&source[token.span.start..token.span.end], &mut output);
        if color && !style.is_empty() {
            output.push_str("\x1b[0m");
        }
    }
    output
}

fn visible(text: &str, output: &mut String) {
    for ch in text.chars() {
        match ch {
            '\n' | '\t' => output.push(ch),
            '\r' => output.push_str("\\r"),
            '\u{1b}' => output.push_str("\\x1b"),
            ch if ch.is_control() => output.extend(ch.escape_unicode()),
            ch => output.push(ch),
        }
    }
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
                "transaction A(read committed):",
                "committed",
                TokenKind::Command,
            ),
            ("copy($wsl::src, \"字节\")", "copy", TokenKind::Binding),
            (
                "program => $path::bash(\"-c\", \"cat\")",
                "bash",
                TokenKind::Binding,
            ),
            ("    rollback A", "rollback", TokenKind::Command),
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
        for source in ["copy(", "macro copy(.codex,", "transaction A(", ")", ","] {
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
