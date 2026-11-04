//! Literal Strings share one source contract across parsing and formatting.

use msync::language::{self, Expr, ExprKind, StatementKind};
use proptest::prelude::*;

fn binding(source: &str) -> Expr {
    let document = language::parse(source).expect("literal binding parses");
    let StatementKind::Binding { value, .. } = &document.statements[0].kind else {
        panic!("expected a value binding");
    };
    value.clone()
}

fn literal(source: &str) -> String {
    let ExprKind::Literal(value) = binding(source).kind else {
        panic!("expected a String literal");
    };
    value
}

#[test]
fn backslashes_are_literal_including_control_spellings_and_trailing_separators() {
    for text in [
        r"C:\Users\STONE\target",
        r"D:\Code\agents\target\xmlsquish\artifacts\codex.prompt",
        r"C:\folder\",
        r"\n\r\t\\",
        r"\\server\share\target",
        r"regex\d+\w*\.[a-z]",
    ] {
        let source = format!("value := \"{text}\"\n");
        assert_eq!(literal(&source), text);
        let formatted = language::format(&source).unwrap();
        assert_eq!(formatted, source, "literal content must not gain escapes");
        assert_eq!(literal(&formatted), text);
        assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
}

#[test]
fn doubled_quotes_keep_operators_comments_and_delimiters_inside_the_string() {
    let source = "value := \"left\"\" # / :: -> (),[] right\"\n";
    let value = "left\" # / :: -> (),[] right";
    assert_eq!(literal(source), value);
    let document = language::parse(source).unwrap();
    assert_eq!(document.statements.len(), 1);
    let formatted = language::format(source).unwrap();
    assert_eq!(formatted, source);
    assert_eq!(literal(&formatted), value);

    let trailing_backslash = r#""path\" -> output"#;
    let document = language::parse(trailing_backslash).unwrap();
    let StatementKind::Transfer { source, .. } = &document.statements[0].kind else {
        panic!("a backslash does not escape the closing delimiter");
    };
    assert!(matches!(&source.kind, ExprKind::Literal(value) if value == "path\\"));
}

#[test]
fn actual_control_characters_roundtrip_as_literal_string_contents() {
    for value in [
        "first\n    second\nthird",
        "first\r\nsecond\tcolumn",
        "\n# not a comment\n    scope not_a_block:\n",
        "a\"b\nc\\d\tend",
    ] {
        let quoted = value.replace('"', "\"\"");
        let source = format!("value := \"{quoted}\"\n");
        assert_eq!(literal(&source), value);
        let formatted = language::format(&source).unwrap();
        assert_eq!(literal(&formatted), value);
        assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
}

#[test]
fn doubled_quote_and_unicode_operand_intervals_keep_original_source_bytes() {
    let source = "value := \"目录\"\"\\target\"\n";
    let value = binding(source);
    let ExprKind::Literal(text) = &value.kind else {
        panic!("String literal");
    };
    assert_eq!(text, "目录\"\\target");
    let unicode = value.source.interval(0, "目录".len());
    assert_eq!(&source[unicode.start..unicode.end], "目录");
    let quote = value.source.interval("目录".len(), "目录\"".len());
    assert_eq!(&source[quote.start..quote.end], "\"\"");
    let slash = value.source.interval("目录\"".len(), "目录\"\\".len());
    assert_eq!(&source[slash.start..slash.end], "\\");
    let suffix = value.source.interval("目录\"".len(), text.len());
    assert_eq!(&source[suffix.start..suffix.end], "\\target");
}

#[test]
fn literal_strings_remain_literals_when_joined_with_path_fragments() {
    let source = r#"target := "D:\Code\agents\target"/xmlsquish/artifacts/codex.prompt"#;
    let value = binding(source);
    fn first_fragment(value: &Expr) -> &Expr {
        match &value.kind {
            ExprKind::Join { base, .. } => first_fragment(base),
            _ => value,
        }
    }
    assert!(
        matches!(&first_fragment(&value).kind, ExprKind::Literal(value) if value == r"D:\Code\agents\target")
    );
    let formatted = language::format(source).unwrap();
    assert_eq!(language::format(&formatted).unwrap(), formatted);
}

#[test]
fn repl_multiline_strings_keep_blank_lines_and_compound_submission_boundaries() {
    use language::{Submission, SubmissionBuffer};

    let mut buffer = SubmissionBuffer::default();
    assert_eq!(
        buffer.push_line("value := \"first").unwrap(),
        Submission::Pending
    );
    assert_eq!(buffer.push_line("").unwrap(), Submission::Pending);
    let Submission::Ready(source) = buffer.push_line("last\"").unwrap() else {
        panic!("closing a root String completes its submission");
    };
    assert_eq!(literal(&source), "first\n\nlast");
    assert!(!buffer.is_pending());

    assert_eq!(
        buffer.push_line("scope profile:").unwrap(),
        Submission::Pending
    );
    assert_eq!(
        buffer.push_line("    value := \"first").unwrap(),
        Submission::Pending
    );
    assert_eq!(buffer.push_line("").unwrap(), Submission::Pending);
    assert_eq!(buffer.push_line("last\"").unwrap(), Submission::Pending);
    let Submission::Ready(source) = buffer.push_line("").unwrap() else {
        panic!("a completed compound scope still waits for its boundary");
    };
    let document = language::parse(&source).unwrap();
    let StatementKind::Scope { body, .. } = &document.statements[0].kind else {
        panic!("scope declaration");
    };
    let StatementKind::Binding { value, .. } = &body[0].kind else {
        panic!("scope String binding");
    };
    assert!(matches!(&value.kind, ExprKind::Literal(value) if value == "first\n\nlast"));

    assert_eq!(
        buffer.push_line("value := \"not a loop:").unwrap(),
        Submission::Pending
    );
    assert!(matches!(
        buffer.push_line("\"").unwrap(),
        Submission::Ready(_)
    ));
}

proptest! {
    #[test]
    fn string_values_roundtrip_without_escape_interpretation(value in any::<String>()) {
        let mut quoted = String::new();
        language::literal::write_string(&mut quoted, &value);
        let source = format!("value := {quoted}\n");
        prop_assert_eq!(literal(&source), value.clone());
        let formatted = language::format(&source).unwrap();
        prop_assert_eq!(literal(&formatted), value);
        prop_assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
}
