use msync::syntax::{Diagnostic, Span, StatementKind, format, parse, render_diagnostic};
use proptest::prelude::*;

fn kinds(source: &str) -> Vec<StatementKind> {
    parse(source)
        .unwrap()
        .statements
        .into_iter()
        .map(|statement| statement.kind)
        .collect()
}

#[test]
fn parses_seed_example_and_preserves_comments() {
    let source = include_str!("../docs/dsl/spread.msync");
    let script = parse(source).unwrap();
    assert_eq!(
        script
            .statements
            .iter()
            .filter(|statement| matches!(statement.kind, StatementKind::Copy { .. }))
            .count(),
        5
    );
    let formatted = format(source).unwrap();
    assert_eq!(kinds(source), kinds(&formatted));
    assert_eq!(format(&formatted).unwrap(), formatted);
    assert!(formatted.contains("如果文件已经存在则报错"));
}

#[test]
fn canonical_spacing_and_inline_comments() {
    let source = "  remote=>$wsl  # endpoint\r\n\r\n a  ->  b/\r\n#\r\n";
    assert_eq!(
        format(source).unwrap(),
        "remote => $wsl\n# endpoint\na -> b/\n#\n"
    );
}

#[test]
fn operators_and_comments_in_selectors_are_not_delimiters() {
    let source = "files/<(a->b|c=>d|x:=y|[#>])> -> output/ # comment\n";
    let statements = kinds(source);
    assert_eq!(
        statements[0],
        StatementKind::Copy {
            source: "files/<(a->b|c=>d|x:=y|[#>])>".into(),
            destination: "output/".into()
        }
    );
    assert_eq!(kinds(&format(source).unwrap()), statements);
}

#[test]
fn quotes_support_windows_unicode_and_preserve_unknown_escapes() {
    let source = r#""C:\\Users\\猫\\file #1.txt" -> "D:\\new folder\\file.txt""#;
    assert_eq!(
        kinds(source),
        vec![StatementKind::Copy {
            source: "C:\\Users\\猫\\file #1.txt".into(),
            destination: "D:\\new folder\\file.txt".into()
        }]
    );
    let unknown = r#""a\q" -> b"#;
    assert_eq!(
        kinds(unknown)[0],
        StatementKind::Copy {
            source: r"a\q".into(),
            destination: "b".into()
        }
    );
    assert_eq!(kinds(source), kinds(&format(source).unwrap()));
}

#[test]
fn escaped_control_characters_roundtrip() {
    let source = r#""a\n\t\r\"\\b" -> c"#;
    let formatted = format(source).unwrap();
    assert_eq!(kinds(source), kinds(&formatted));
    assert_eq!(format(&formatted).unwrap(), formatted);
}

#[test]
fn url_fragments_are_not_comments() {
    assert_eq!(
        kinds("web => https://example.org/#section")[0],
        StatementKind::Endpoint {
            name: "web".into(),
            value: "https://example.org/#section".into()
        }
    );
}

#[test]
fn malformed_lines_collect_diagnostics_with_original_offsets() {
    let source = "猫 -> dest\r\nmissing\nsource ->\n\"unterminated\n";
    let errors = parse(source).unwrap_err();
    assert_eq!(errors.len(), 3);
    assert_eq!(errors[0].span.start, "猫 -> dest\r\nmissing".len());
    let message = render_diagnostic(source, &errors[0]);
    assert!(message.contains(" --> 2:8"), "{message}");
    assert!(message.contains("missing"));
}

#[test]
fn invalid_partial_quotes_and_multiple_statements_fail() {
    for source in [
        "a\"b\" -> c",
        "\"a\" tail -> c",
        "a -> b -> c",
        "a :=",
        "\"\" -> b",
        "a/<x -> b",
    ] {
        assert!(parse(source).is_err(), "{source}");
    }
}

#[test]
fn external_invalid_unicode_spans_do_not_panic() {
    let diagnostic = Diagnostic {
        message: "test".into(),
        span: Span {
            start: 1,
            end: usize::MAX,
        },
    };
    let rendered = render_diagnostic("猫\n", &diagnostic);
    assert!(rendered.contains(" --> 1:1"));
}

#[test]
fn diagnostic_carets_align_with_wide_characters_and_tab_stops() {
    let source = "猫\tbad";
    let diagnostic = Diagnostic {
        message: "test".into(),
        span: Span {
            start: "猫\t".len(),
            end: source.len(),
        },
    };
    let rendered = render_diagnostic(source, &diagnostic);
    assert!(rendered.contains(" --> 1:3"));
    assert!(rendered.contains("1 | 猫  bad"), "{rendered}");
    assert!(rendered.ends_with("  |     ^^^"), "{rendered}");
}

#[test]
fn diagnostics_escape_terminal_control_sequences() {
    let source = "a\u{1b}[31mb";
    let diagnostic = Diagnostic {
        message: "bad endpoint \u{1b}[31m".into(),
        span: Span {
            start: 0,
            end: source.len(),
        },
    };
    let rendered = render_diagnostic(source, &diagnostic);
    assert!(!rendered.contains('\u{1b}'));
    assert!(rendered.contains(r"\u{1b}"));
}

#[test]
fn empty_input_stays_empty() {
    assert!(parse("").unwrap().statements.is_empty());
    assert_eq!(format(" \n\r\n").unwrap(), "");
}

#[test]
fn malformed_operators_and_missing_operands_have_exact_spans() {
    let source = "source - > destination\n";
    let report = &msync::syntax::parse_rich(source).unwrap_err()[0];
    assert_eq!(
        &source[report.diagnostic.span.start..report.diagnostic.span.end],
        "-"
    );
    assert!(!report.help.is_empty());
    let source = "source ->   \n";
    let report = &msync::syntax::parse_rich(source).unwrap_err()[0];
    assert_eq!(report.diagnostic.span.start, source.len() - 1);
    assert_eq!(report.diagnostic.span.start, report.diagnostic.span.end);
    let source = "files/<[> -> $pwd::out/\n";
    let report = &msync::syntax::parse_rich(source).unwrap_err()[0];
    assert_eq!(
        &source[report.diagnostic.span.start..report.diagnostic.span.end],
        "["
    );
}

#[test]
fn rich_renderer_has_labels_help_and_color_only_when_requested() {
    let source = "\"open -> target";
    let report = &msync::syntax::parse_rich(source).unwrap_err()[0];
    let plain = msync::syntax::render_report(source, report, false);
    assert!(plain.contains("insert closing quote here"));
    assert!(plain.contains("help:"));
    assert!(!plain.contains('\u{1b}'));
    let colored = msync::syntax::render_report(source, report, true);
    assert!(colored.contains("\u{1b}[1;31m"));
    assert!(colored.contains("\u{1b}[1;34m"));
}

proptest! {
    #[test]
    fn formatter_preserves_arbitrary_quoted_operands(left in ".{1,100}", right in ".{1,100}") {
        fn quoted(value: &str) -> String {
            format!("\"{}\"", value.replace('\\', "\\\\").replace('"', "\\\"").replace('\n', "\\n").replace('\r', "\\r").replace('\t', "\\t"))
        }
        let source = format!("{} -> {} # generated\n", quoted(&left), quoted(&right));
        let formatted = format(&source).unwrap();
        prop_assert_eq!(kinds(&source), kinds(&formatted));
        prop_assert_eq!(format(&formatted).unwrap(), formatted);
    }

    #[test]
    fn arbitrary_input_never_panics(source in ".{0,300}") {
        match parse(&source) {
            Ok(_) => {
                let formatted = format(&source).unwrap();
                prop_assert_eq!(kinds(&source), kinds(&formatted));
                prop_assert_eq!(format(&formatted).unwrap(), formatted);
            }
            Err(errors) => for error in errors { let _ = render_diagnostic(&source, &error); },
        }
    }
}
