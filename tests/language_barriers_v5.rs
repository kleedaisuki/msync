use msync::highlight::{self, TokenKind};
use msync::language::{self, BindingOp, ExprKind, StatementKind, Submission, SubmissionBuffer};
use proptest::prelude::*;

#[test]
fn explicit_async_program_retains_the_call_and_precise_operand_spans() {
    let source =
        "async Program => $path::bash(\"ls\")\nProgram::stdout -> stdout.txt\nawait Program\n";
    let document = language::parse(source).unwrap();
    let StatementKind::Scheduled {
        asynchronous,
        awaited,
        statement,
    } = &document.statements[0].kind
    else {
        panic!("expected a scheduled statement")
    };
    assert!(*asynchronous);
    assert!(!*awaited);
    assert_eq!(
        &source[statement.span.start..statement.span.end],
        "Program => $path::bash(\"ls\")"
    );
    let StatementKind::Binding { name, op, value } = &statement.kind else {
        panic!("expected a program binding")
    };
    assert_eq!(name.text, "Program");
    assert_eq!(*op, BindingOp::Space);
    assert!(matches!(value.kind, ExprKind::Call { .. }));
    assert_eq!(
        &source[value.span.start..value.span.end],
        "$path::bash(\"ls\")"
    );
    assert!(
        matches!(&document.statements[2].kind, StatementKind::Await(Some(name)) if name.text == "Program")
    );
}

#[test]
fn independent_prefixes_apply_to_finite_statements_and_macro_calls() {
    for (source, asynchronous, awaited) in [
        ("async input -> output", true, false),
        ("await input -> output", false, true),
        ("async await input -> output", true, true),
        ("await async copy(a, b)", true, true),
        ("await Program => $path::bash(\"ls\")", false, true),
    ] {
        let document = language::parse(source).unwrap();
        assert!(matches!(&document.statements[0].kind,
            StatementKind::Scheduled { asynchronous: actual_async, awaited: actual_await, .. }
                if *actual_async == asynchronous && *actual_await == awaited));
        let formatted = language::format(source).unwrap();
        assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
    assert_eq!(
        language::format("await async a->b").unwrap(),
        "async await a -> b\n"
    );
}

#[test]
fn standalone_barriers_and_macro_bodies_are_preserved() {
    let source = "macro copy(a, b):\n    async\n    async P => $path::cat()\n    a -> b\n    await P\n    await\n";
    let document = language::parse(source).unwrap();
    let StatementKind::Macro { body, .. } = &document.statements[0].kind else {
        panic!()
    };
    assert!(matches!(body[0].kind, StatementKind::Async));
    assert!(matches!(body[4].kind, StatementKind::Await(None)));
    assert_eq!(language::format(source).unwrap(), source);
    let mut buffer = SubmissionBuffer::default();
    assert!(matches!(
        buffer.push_line("macro copy(a, b):").unwrap(),
        Submission::Pending
    ));
    assert!(matches!(
        buffer.push_line("    async a -> b").unwrap(),
        Submission::Pending
    ));
    assert!(matches!(
        buffer.push_line("").unwrap(),
        Submission::Ready(_)
    ));
    assert!(matches!(
        buffer.push_line("await P").unwrap(),
        Submission::Ready(_)
    ));
}

#[test]
fn scheduling_keywords_do_not_consume_similar_filenames_or_quoted_data() {
    for source in [
        "async-file -> output",
        "awaiting -> output",
        "\"async await\" -> output",
        "$local::async -> output",
    ] {
        let document = language::parse(source).unwrap();
        assert!(matches!(
            document.statements[0].kind,
            StatementKind::Transfer { .. }
        ));
    }
}

#[test]
fn malformed_barriers_have_actionable_utf8_safe_ranges() {
    for source in [
        "async async a -> b",
        "await await 程序",
        "async await",
        "async macro copy(a):",
        "await global x",
        "await ../not-a-program",
    ] {
        let reports = language::parse(source).unwrap_err();
        assert!(!reports.is_empty());
        for report in reports {
            assert!(!report.help.is_empty(), "{source}: {report:?}");
            let span = report.diagnostic.span;
            assert!(source.is_char_boundary(span.start));
            assert!(source.is_char_boundary(span.end));
            assert!(span.start <= span.end && span.end <= source.len());
        }
    }
}

#[test]
fn all_scheduling_prefixes_receive_control_highlighting() {
    let source = "async await P => $path::bash(\"ls\")";
    let keywords = highlight::token_spans(source)
        .into_iter()
        .filter(|token| token.kind == TokenKind::Command)
        .map(|token| &source[token.span.start..token.span.end])
        .collect::<Vec<_>>();
    assert_eq!(keywords, ["async", "await"]);
    assert!(highlight::highlight(source, true).contains("\x1b[1;36masync"));
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(48))]
    #[test]
    fn prefixed_literal_formatting_preserves_data_and_is_idempotent(
        value in "[a-zA-Z0-9 中\\t]{0,60}",
        asynchronous in any::<bool>(),
        awaited in any::<bool>(),
    ) {
        let prefix = format!("{}{}", if asynchronous { "async " } else { "" }, if awaited { "await " } else { "" });
        let source = format!("{prefix}\"{}\" -> output\n", value.replace('\t', "\\t"));
        let formatted = language::format(&source).unwrap();
        prop_assert_eq!(language::format(&formatted).unwrap(), formatted.clone());
        let document = language::parse(&formatted).unwrap();
        let kind = match &document.statements[0].kind {
            StatementKind::Scheduled { statement, .. } => &statement.kind,
            kind => kind,
        };
        let StatementKind::Transfer { source, .. } = kind else { panic!() };
        prop_assert!(matches!(&source.kind, ExprKind::Literal(actual) if actual == &value));
    }
}
