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

fn without_color(text: &str) -> String {
    regex::Regex::new(r"\x1b\[[0-9;]*m")
        .unwrap()
        .replace_all(text, "")
        .into_owned()
}

#[test]
fn named_reports_have_one_heading_one_location_and_shared_source_rows() {
    use msync::syntax::{Report, render_report_named};
    let source = "# surrounding context\ncat -> dest\nnext -> other\n";
    let start = source.find("dest").unwrap();
    let span = Span {
        start,
        end: start + 4,
    };
    let left = source.find("cat").unwrap();
    let report = Report::new("cannot access destination", span)
        .label(span, "choose an existing parent")
        .label(
            Span {
                start: left,
                end: left + 3,
            },
            "source of this transfer",
        )
        .help("Create the destination parent, then retry the plan.");
    let plain = render_report_named(source, &report, "scripts/deploy.msync", false);
    assert_eq!(plain.matches("error:").count(), 1, "{plain}");
    assert!(plain.contains(" --> scripts/deploy.msync:2:8"), "{plain}");
    assert_eq!(plain.matches("2 | cat -> dest").count(), 1, "{plain}");
    assert!(plain.contains("1 | # surrounding context"), "{plain}");
    assert!(plain.contains("3 | next -> other"), "{plain}");
    assert!(plain.contains("^^^^ choose an existing parent"), "{plain}");
    assert!(plain.contains("--- source of this transfer"), "{plain}");
    assert!(
        plain.contains("= help: Create the destination parent"),
        "{plain}"
    );
    let colored = render_report_named(source, &report, "scripts/deploy.msync", true);
    assert_eq!(without_color(&colored), plain);
    assert!(colored.contains("\u{1b}[1;31m^^^^"));
    assert!(colored.contains("\u{1b}[1;36m---"));
}

#[test]
fn rich_reports_keep_unicode_tab_geometry_and_two_digit_gutters() {
    use msync::syntax::{Report, render_report_named};
    let mut source = "context\n".repeat(9);
    source.push_str("\t猫bad\nfollowing\n");
    let start = source.find("bad").unwrap();
    let report = Report::new(
        "bad token",
        Span {
            start,
            end: start + 3,
        },
    );
    let plain = render_report_named(&source, &report, "猫.msync", false);
    assert!(plain.contains(" --> 猫.msync:10:3"), "{plain}");
    assert!(plain.contains("10 |     猫bad"), "{plain}");
    assert!(plain.contains("   |       ^^^"), "{plain}");
    assert!(plain.contains("11 | following"), "{plain}");
    assert_eq!(
        without_color(&render_report_named(&source, &report, "猫.msync", true)),
        plain
    );
}

#[test]
fn report_names_messages_and_labels_cannot_inject_terminal_controls_or_bidi() {
    use msync::syntax::{Report, render_report_named};
    let source = "a\u{202e} -> b";
    let span = Span {
        start: 1,
        end: "a\u{202e}".len(),
    };
    let report = Report::new("bad\u{1b}[2J\u{202e}", span)
        .label(span, "label\t\u{1b}[31m")
        .help("try\nagain\u{2066}");
    let plain = render_report_named(source, &report, "path\n\t\u{1b}[2J.msync", false);
    assert!(!plain.contains('\u{1b}'));
    assert!(!plain.contains('\u{202e}'));
    assert!(!plain.contains('\u{2066}'));
    assert!(plain.contains(r"path\n\t\u{1b}[2J.msync:1:2"), "{plain}");
    assert!(plain.contains(r"label\t\u{1b}[31m"), "{plain}");
    assert!(
        plain.contains("= help: try\n          again\\u{2066}"),
        "{plain}"
    );
    assert!(plain.contains("^^^^^^^^"), "{plain}");
    let colored = render_report_named(source, &report, "path\n\t\u{1b}[2J.msync", true);
    assert_eq!(without_color(&colored), plain);
    assert!(!colored.contains("\u{1b}[2J"));
}

#[test]
fn rich_reports_preserve_multiline_labels_and_bound_large_context_windows() {
    use msync::syntax::{Report, render_report_named};
    let source = "first\nsecond\nthird\nfourth\nlast\n";
    let span = Span {
        start: 0,
        end: "first\nsecond".len(),
    };
    let report = Report::new("multiline context", span)
        .label(span, "both source lines")
        .label(
            Span {
                start: source.find("last").unwrap(),
                end: source.len() - 1,
            },
            "related output",
        );
    let plain = render_report_named(source, &report, "flow.msync", false);
    assert!(plain.contains("1 | first\n  | ^^^^^"), "{plain}");
    assert!(
        plain.contains("2 | second\n  | ^^^^^^ both source lines"),
        "{plain}"
    );
    assert!(
        plain.contains("5 | last\n  | ---- related output"),
        "{plain}"
    );
    assert_eq!(plain.matches("error:").count(), 1);
    let source = (0..1000)
        .map(|index| format!("row-{index}\n"))
        .collect::<String>();
    let report = Report::new(
        "large external span",
        Span {
            start: 0,
            end: usize::MAX,
        },
    );
    let compact = render_report_named(&source, &report, "large.msync", false);
    assert!(compact.contains("..."), "{compact}");
    assert!(compact.contains("row-0"));
    assert!(compact.contains("row-999"));
    assert!(!compact.contains("row-500"));
    assert!(compact.lines().count() < 20, "{compact}");
}

#[test]
fn rich_reports_show_crlf_and_eof_insertions_without_hidden_columns() {
    use msync::syntax::{Report, render_report_named};
    let source = "a ->\r\n";
    let report = Report::new(
        "missing operand",
        Span {
            start: source.len(),
            end: source.len(),
        },
    );
    let plain = render_report_named(source, &report, "empty.msync", false);
    assert!(plain.contains(" --> empty.msync:2:1"), "{plain}");
    assert!(plain.contains("1 | a ->"), "{plain}");
    assert!(plain.ends_with("  | ^"), "{plain}");
    assert!(!plain.contains("\\r"), "{plain}");
    let report = Report::new(
        "end of line",
        Span {
            start: source.len() - 1,
            end: source.len() - 1,
        },
    );
    let plain = render_report_named(source, &report, "empty.msync", false);
    assert!(plain.contains(" --> empty.msync:1:5"), "{plain}");
}

proptest! {
    #[test]
    fn rich_external_spans_are_safe_and_color_preserves_display_text(
        characters in proptest::collection::vec(any::<char>(), 0..100),
        start in 0_usize..500,
        end in 0_usize..500,
        secondary in 0_usize..500,
    ) {
        use msync::syntax::{Report, render_report_named};
        let source = characters.into_iter().collect::<String>();
        let report = Report::new("generated diagnostic", Span { start, end })
            .label(Span { start: secondary, end }, "related token")
            .help("Check the highlighted source.");
        let plain = render_report_named(&source, &report, "generated.msync", false);
        let colored = render_report_named(&source, &report, "generated.msync", true);
        prop_assert!(!plain.contains('\u{1b}'), "plain output contains ANSI");
        prop_assert_eq!(without_color(&colored), plain);
    }

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
