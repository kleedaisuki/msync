use msync::language::{self, BindingOp, DestinationMode, ExprKind, Isolation, StatementKind};
use proptest::prelude::*;

const EXAMPLE: &str = r##"# 独立的词法环境
config := ~/.codex
macro copy_codex(.codex, des):
	global config
	# Replace the named directory, not its parent.
	.codex/agents -> $pwd::des/agents/.

copy_codex($wsl::~/.codex, wsl)
copy_codex($local::~/.codex, local)
transaction A1(read uncommitted):
	program => $path::bash("python", "run_codex.py", .codex)
	$pwd::prompt.txt -> program::stdin
	program::stdout -> output.txt
	commit
	program::exit_code -> exit_codex.txt
	commit
	program::exit_code -> wsl.txt
	rollback
rollback A1
transaction B2(serializable):
    verifier => $pwd::.venv/Scripts/python.exe(scripts/verify.py, "verify")
    verifier::pid -> pid
"scucess" -> result.txt
"你好\n世界" -> 中文.txt
"# not a comment" -> $local::"a # file.txt"
"" -> empty.txt
"##;

#[test]
fn parses_the_requested_program_without_losing_types() {
    let document = language::parse(EXAMPLE).unwrap();
    let StatementKind::Macro {
        parameters, body, ..
    } = &document.statements[2].kind
    else {
        panic!()
    };
    assert_eq!(
        parameters
            .iter()
            .map(|name| name.text.as_str())
            .collect::<Vec<_>>(),
        [".codex", "des"]
    );
    assert!(matches!(&body[0].kind, StatementKind::Global(names) if names[0].text == "config"));
    let StatementKind::Transfer { destination, .. } = &body[2].kind else {
        panic!()
    };
    assert!(matches!(
        &destination.kind,
        ExprKind::Join {
            mode: DestinationMode::ReplaceTree,
            ..
        }
    ));
    assert_eq!(
        &EXAMPLE[destination.span.start..destination.span.end],
        "$pwd::des/agents/."
    );
    let transaction = document
        .statements
        .iter()
        .find_map(|statement| match &statement.kind {
            StatementKind::Transaction {
                isolation, body, ..
            } => Some((isolation, body)),
            _ => None,
        })
        .unwrap();
    assert_eq!(*transaction.0, Isolation::ReadUncommitted);
    assert!(
        matches!(&transaction.1[0].kind, StatementKind::Binding { op: BindingOp::Space, value, .. } if matches!(&value.kind, ExprKind::Call { arguments, .. } if arguments.len() == 3))
    );
    assert!(
        matches!(document.statements.last().unwrap().kind, StatementKind::Transfer { ref source, .. } if matches!(&source.kind, ExprKind::Literal(text) if text.is_empty()))
    );
}

#[test]
fn canonical_format_is_idempotent_and_preserves_nested_regions() {
    let first = language::format(EXAMPLE).unwrap();
    assert!(!first.contains('\t'));
    assert!(first.contains("macro copy_codex(.codex, des):\n    global config\n"));
    assert!(first.contains("    .codex/agents -> $pwd::des/agents/.\n"));
    assert!(first.contains("\"你好\\n世界\" -> 中文.txt\n"));
    assert_eq!(language::format(&first).unwrap(), first);
    assert_eq!(shape(EXAMPLE), shape(&first));
}

fn shape(source: &str) -> String {
    fn strip(statements: &mut [msync::language::Statement]) {
        for statement in statements {
            statement.span = Default::default();
            match &mut statement.kind {
                StatementKind::Binding { name, value, .. } => {
                    name.span = Default::default();
                    strip_expr(value);
                }
                StatementKind::Transfer {
                    source,
                    destination,
                } => {
                    strip_expr(source);
                    strip_expr(destination);
                }
                StatementKind::Macro {
                    name,
                    parameters,
                    body,
                } => {
                    name.span = Default::default();
                    for parameter in parameters {
                        parameter.span = Default::default();
                    }
                    strip(body);
                }
                StatementKind::Call(value) => strip_expr(value),
                StatementKind::Global(names) => {
                    for name in names {
                        name.span = Default::default();
                    }
                }
                StatementKind::Transaction { name, body, .. } => {
                    name.span = Default::default();
                    strip(body);
                }
                StatementKind::Rollback(Some(name)) => name.span = Default::default(),
                _ => {}
            }
        }
    }
    fn strip_expr(expr: &mut msync::language::Expr) {
        expr.span = Default::default();
        expr.source = msync::syntax::OperandSource::unmapped(Default::default());
        match &mut expr.kind {
            ExprKind::Path(path) => {
                if let Some(name) = &mut path.qualifier {
                    name.span = Default::default();
                }
            }
            ExprKind::Call { callee, arguments } => {
                strip_expr(callee);
                for argument in arguments {
                    strip_expr(argument);
                }
            }
            ExprKind::Join {
                base,
                suffix,
                operator,
                ..
            } => {
                strip_expr(base);
                if let Some(suffix) = suffix {
                    strip_expr(suffix);
                }
                *operator = Default::default();
            }
            _ => {}
        }
    }
    let mut document = language::parse(source).unwrap();
    strip(&mut document.statements);
    format!("{document:?}")
}

#[test]
fn quotes_are_literal_unless_namespace_qualification_supplies_path_context() {
    let document = language::parse(
        r#""a\q" -> "destination with spaces"
alias := "source path"
$local::"source path" -> result
run($wsl::"source path", "text\n流")
"#,
    )
    .unwrap();
    assert!(
        matches!(&document.statements[0].kind, StatementKind::Transfer { source, destination } if matches!(&source.kind, ExprKind::Literal(text) if text == "a\\q") && matches!(&destination.kind, ExprKind::Literal(text) if text == "destination with spaces"))
    );
    assert!(
        matches!(&document.statements[1].kind, StatementKind::Binding { value, .. } if matches!(&value.kind, ExprKind::Literal(text) if text == "source path"))
    );
    assert!(
        matches!(&document.statements[2].kind, StatementKind::Transfer { source, .. } if matches!(&source.kind, ExprKind::Path(path) if path.qualifier.as_ref().unwrap().text == "$local"))
    );
}

#[test]
fn calls_preserve_nested_arguments_commas_and_quoted_delimiters() {
    let source = "macro outer(a, b, ):\n    inner(a, transform(b, \"x,)->#\"), )\n";
    let document = language::parse(source).unwrap();
    let StatementKind::Macro { body, .. } = &document.statements[0].kind else {
        panic!()
    };
    let StatementKind::Call(call) = &body[0].kind else {
        panic!()
    };
    let ExprKind::Call { arguments, .. } = &call.kind else {
        panic!()
    };
    assert_eq!(arguments.len(), 2);
    assert!(matches!(arguments[1].kind, ExprKind::Call { .. }));
    assert_eq!(shape(source), shape(&language::format(source).unwrap()));
}

#[test]
fn adversarial_nesting_reports_a_span_instead_of_exhausting_the_stack() {
    let source = format!("{}x{}\n", "f(".repeat(129), ")".repeat(129));
    let errors = language::parse(&source).unwrap_err();
    assert!(errors[0].diagnostic.message.contains("nesting"));
    assert_eq!(
        &source[errors[0].diagnostic.span.start..errors[0].diagnostic.span.end],
        "("
    );
    let mut blocks = String::new();
    for depth in 0..129 {
        blocks.push_str(&format!("{}transaction t{depth}:\n", "    ".repeat(depth)));
    }
    blocks.push_str(&format!("{}commit\n", "    ".repeat(129)));
    assert!(
        language::parse(&blocks)
            .unwrap_err()
            .iter()
            .any(|report| report.diagnostic.message.contains("nesting"))
    );
}

#[test]
fn supports_all_isolations_and_the_default() {
    for (text, expected) in [
        ("", Isolation::Serializable),
        ("()", Isolation::Serializable),
        ("(read uncommitted)", Isolation::ReadUncommitted),
        ("(read committed)", Isolation::ReadCommitted),
        ("(repeatable read)", Isolation::RepeatableRead),
        ("(serializable)", Isolation::Serializable),
    ] {
        let document = language::parse(&format!("transaction T{text}:\n    commit\n")).unwrap();
        assert!(
            matches!(document.statements[0].kind, StatementKind::Transaction { isolation, .. } if isolation == expected)
        );
    }
}

#[test]
fn header_comments_stay_in_their_regions_and_comment_indentation_is_not_a_block() {
    let source = "macro copy(a): # header\n# unindented explanation\n    a -> b\n# outside cosmetic comment\ncopy(x) # call\n";
    let canonical = language::format(source).unwrap();
    assert!(
        canonical.starts_with(
            "macro copy(a):\n    # header\n    # unindented explanation\n    a -> b\n"
        )
    );
    assert_eq!(language::format(&canonical).unwrap(), canonical);
    assert!(language::parse("macro empty():\n    # comment only\nnext(x)\n").is_err());
}

#[test]
fn diagnoses_ambiguous_indentation_and_nested_macros() {
    for source in [
        "macro a():\n\tcommit\n        commit\n",
        "macro a():\n \tcommit\n",
        "macro a():\n    a -> b\n  b -> c\n",
        "  a -> b\n",
        "macro a():\n    macro b():\n        x -> y\n",
    ] {
        let reports = language::parse(source).unwrap_err();
        assert!(!reports.is_empty());
        assert!(!reports[0].help.is_empty());
    }
}

#[test]
fn diagnostics_point_at_precise_utf8_tokens_and_collect_independent_errors() {
    let source = "你好 -> output\r\ncall(\"流\",, next)\r\ntransaction T(weird):\r\n";
    let errors = language::parse(source).unwrap_err();
    let missing = errors
        .iter()
        .find(|error| error.diagnostic.message.contains("missing argument"))
        .unwrap();
    assert_eq!(
        missing.diagnostic.span.start,
        source.find(",,").unwrap() + 1
    );
    let isolation = errors
        .iter()
        .find(|error| error.diagnostic.message.contains("isolation"))
        .unwrap();
    assert_eq!(
        &source[isolation.diagnostic.span.start..isolation.diagnostic.span.end],
        "weird"
    );
    for report in errors {
        assert!(source.is_char_boundary(report.diagnostic.span.start));
        assert!(source.is_char_boundary(report.diagnostic.span.end));
    }
}

#[test]
fn malformed_calls_and_parameters_have_actionable_reports() {
    for (source, needle) in [
        ("macro a(x, x):\n    x -> y\n", "duplicate"),
        ("run(a,, b)\n", "missing argument"),
        ("run(a\n", "unclosed"),
        ("run(a))\n", "unmatched"),
        ("run(a) trailing\n", "unexpected text"),
        ("macro a(x)\n    x -> y\n", "expected ':'"),
        ("\"流\n", "unterminated"),
    ] {
        let errors = language::parse(source).unwrap_err();
        assert!(
            errors
                .iter()
                .any(|report| report.diagnostic.message.contains(needle)),
            "{source:?}: {errors:?}"
        );
    }
}

#[test]
fn regex_delimiters_are_not_call_syntax_or_comments() {
    let source = "files/<(a->b|c=>d|x:=y|[#>,])> -> output/ # keep\n";
    let canonical = language::format(source).unwrap();
    assert_eq!(shape(source), shape(&canonical));
    assert_eq!(language::format(&canonical).unwrap(), canonical);
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(64))]
    #[test]
    fn arbitrary_utf8_literal_formatting_preserves_payload_and_is_idempotent(value in "[^\\x00\\r\\n]{0,80}") {
        let literal = value.replace('\\', "\\\\").replace('"', "\\\"").replace('\t', "\\t");
        let source = format!("\"{literal}\" -> output\n");
        let canonical = language::format(&source).unwrap();
        prop_assert_eq!(shape(&source), shape(&canonical));
        prop_assert_eq!(language::format(&canonical).unwrap(), canonical);
    }
}
