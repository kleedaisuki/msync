//! Shared String token boundaries, byte provenance and canonical spelling.
//!
//! Backslashes are ordinary data. A doubled double quote represents one quote;
//! no other byte is decoded, including physical newlines inside a String.

use crate::syntax::{OperandSource, Span};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct StringToken {
    pub span: Span,
    pub content: Span,
    pub terminated: bool,
}

/// Scan a String beginning at `start`. An unfinished token extends to EOF so
/// editors and the submission buffer can share the parser's exact boundaries.
pub fn scan_string(source: &str, start: usize) -> Option<StringToken> {
    let bytes = source.as_bytes();
    if bytes.get(start) != Some(&b'"') {
        return None;
    }
    match string_end(source, start + 1) {
        Some(end) => Some(StringToken {
            span: Span { start, end },
            content: Span {
                start: start + 1,
                end: end - 1,
            },
            terminated: true,
        }),
        None => Some(StringToken {
            span: Span {
                start,
                end: source.len(),
            },
            content: Span {
                start: start + 1,
                end: source.len(),
            },
            terminated: false,
        }),
    }
}

/// Scan a String body, including continuation physical lines in the REPL.
pub(crate) fn string_end(source: &str, content_start: usize) -> Option<usize> {
    let bytes = source.as_bytes();
    let mut index = content_start;
    while index < bytes.len() {
        if bytes[index] == b'"' {
            if bytes.get(index + 1) == Some(&b'"') {
                index += 2;
                continue;
            }
            return Some(index + 1);
        }
        index += 1;
    }
    None
}

/// Decode a token returned by `scan_string`. `offset` projects a source fragment
/// into its enclosing document; callers scanning a complete document pass zero.
pub fn decode_string(source: &str, token: StringToken, offset: usize) -> (String, OperandSource) {
    let span = Span {
        start: offset + token.span.start,
        end: offset + token.span.end,
    };
    let content = Span {
        start: offset + token.content.start,
        end: offset + token.content.end,
    };
    let text = &source[token.content.start..token.content.end];
    if !text.contains('"') {
        return (text.to_owned(), OperandSource::verbatim(span, content));
    }
    let mut value = String::with_capacity(text.len());
    let mut offsets = Vec::with_capacity(text.len() + 1);
    let mut chars = text.char_indices();
    while let Some((index, character)) = chars.next() {
        value.push(character);
        offsets.extend((0..character.len_utf8()).map(|byte| content.start + index + byte));
        if character == '"' {
            chars.next();
        }
    }
    offsets.push(content.end);
    (value, OperandSource::mapped(span, content, offsets))
}

/// Write ordinary String syntax without inventing any escape interpretation.
pub fn write_string(output: &mut String, text: &str) {
    output.push('"');
    for character in text.chars() {
        output.push(character);
        if character == '"' {
            output.push('"');
        }
    }
    output.push('"');
}
