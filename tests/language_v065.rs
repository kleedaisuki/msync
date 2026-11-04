use msync::language::{self, Expr, ExprKind, StatementKind, Submission, SubmissionBuffer};
use proptest::prelude::*;

fn binding(source: &str) -> Expr {
    let document = language::parse(source).unwrap();
    let StatementKind::Binding { value, .. } = &document.statements[0].kind else {
        panic!("expected binding");
    };
    value.clone()
}

fn reference_name(value: &Expr) -> Option<&str> {
    let ExprKind::Reference { operand, .. } = &value.kind else {
        return None;
    };
    match &operand.kind {
        ExprKind::Path(path) => Some(path.path.as_str()),
        _ => None,
    }
}

#[test]
fn ordered_ranges_parse_normal_expressions_with_original_unicode_spans() {
    let source = "values := [\"可莉,🌟\", true, $local::\"folder name/file\", identity(\"a,b\"), [false, true],]\n";
    let value = binding(source);
    let ExprKind::Range(values) = &value.kind else {
        panic!("expected literal range");
    };
    assert_eq!(values.len(), 5);
    assert!(matches!(&values[0].kind, ExprKind::Literal(text) if text == "可莉,🌟"));
    assert!(matches!(values[1].kind, ExprKind::Bool(true)));
    assert!(
        matches!(&values[2].kind, ExprKind::Select { namespace, item, .. } if reference_name(namespace) == Some("local") && matches!(&item.kind, ExprKind::Literal(text) if text == "folder name/file"))
    );
    assert!(matches!(values[3].kind, ExprKind::Call { .. }));
    assert!(matches!(&values[4].kind, ExprKind::Range(items) if items.len() == 2));
    assert_eq!(
        &source[values[0].span.start..values[0].span.end],
        "\"可莉,🌟\""
    );
    assert_eq!(
        &source[value.span.start..value.span.end],
        source.trim_end().split_once(":= ").unwrap().1
    );
    let formatted = language::format(source).unwrap();
    assert_eq!(language::format(&formatted).unwrap(), formatted);
}

#[test]
fn ranges_shield_nested_argument_delimiters_and_selectors() {
    for source in [
        "values := outer( [true, false], [identity(\"x,y\"), false])\n",
        "values := [root/<[<>#,]|a,b>, true, \"# , ]\"] # actual comment\n",
        "values := [] | $view::reverse\n",
        "values := [true == false, not (true or false), exists($pwd::\"a b\")]\n",
    ] {
        let formatted =
            language::format(source).unwrap_or_else(|errors| panic!("{source}: {errors:?}"));
        assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
    for source in [
        "file[1].txt -> output\n",
        "file[unfinished -> output\n",
        "$local::[filename] -> output\n",
    ] {
        assert!(language::parse(source).is_ok(), "{source}");
    }
}

#[test]
fn selectors_after_spaced_path_operators_shield_regex_tokens() {
    let source = "$pwd::pwd / \"source\" / <.+\\.toml> -> $pwd::pwd / \"target\"/\n";
    let document = language::parse(source).unwrap();
    let StatementKind::Transfer { source: input, .. } = &document.statements[0].kind else {
        panic!()
    };
    let ExprKind::Join {
        suffix: Some(selector),
        ..
    } = &input.kind
    else {
        panic!("expected the final selector path component");
    };
    assert!(matches!(&selector.kind, ExprKind::Path(path) if path.path == "<.+\\.toml>"));
    assert_eq!(
        &source[selector.span.start..selector.span.end],
        "<.+\\.toml>"
    );
    for source in [
        "values := [root / <a/b>, root / <[>#,]|a,b>]\n",
        "values := copy( <a/b>, <[>#,]|a,b>)\n",
        "root / <a # b> -> output/ # actual comment\n",
        "value = \"a\" < \"b\"\n",
    ] {
        let formatted =
            language::format(source).unwrap_or_else(|errors| panic!("{source}: {errors:?}"));
        assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
}

#[test]
fn namespace_qualification_applies_to_value_call_results_without_callee_spelling_rules() {
    for callee in ["codex", "other", "take"] {
        let source = format!("config := $wsl::{callee}(config.toml)\n");
        let value = binding(&source);
        let ExprKind::Select {
            namespace, item, ..
        } = value.kind
        else {
            panic!("expected selection of call result");
        };
        assert!(reference_name(&namespace) == Some("wsl"));
        assert!(matches!(item.kind, ExprKind::Call { .. }));
        assert_eq!(language::format(&source).unwrap(), source);
    }
    let value = binding("config := ($wsl::$pwd)::codex(config.toml)\n");
    assert!(matches!(value.kind, ExprKind::Select { .. }));
    let value = binding("worker => $local::tool(\"argument\")\n");
    let ExprKind::Call { callee, .. } = value.kind else {
        panic!("executable calls keep endpoint qualification on the executable");
    };
    assert!(
        matches!(&callee.kind, ExprKind::Select { namespace, item, .. } if reference_name(namespace) == Some("local") && matches!(&item.kind, ExprKind::Path(path) if path.path == "tool"))
    );
}

#[test]
fn qualified_boolean_values_remain_typed_but_quoted_paths_and_executables_stay_paths() {
    for word in ["true", "false"] {
        let value = binding(&format!("item := $pwd::{word}\n"));
        let ExprKind::Select {
            namespace, item, ..
        } = value.kind
        else {
            panic!("qualification must retain the boolean operand type");
        };
        assert!(reference_name(&namespace) == Some("pwd"));
        assert!(matches!(item.kind, ExprKind::Bool(_)));

        let value = binding(&format!("item := $pwd::\"{word}\"\n"));
        assert!(
            matches!(&value.kind, ExprKind::Select { item, .. } if matches!(&item.kind, ExprKind::Literal(text) if text == word))
        );

        let value = binding(&format!("program => $path::{word}()\n"));
        let ExprKind::Call { callee, .. } = value.kind else {
            panic!()
        };
        assert!(
            matches!(&callee.kind, ExprKind::Select { item, .. } if matches!(&item.kind, ExprKind::Path(path) if path.path == word))
        );
    }
}

#[test]
fn scheduled_conditions_preserve_flags_else_association_and_full_spans() {
    for (keyword, flags) in [
        ("release", (true, false)),
        ("acquire", (false, true)),
        ("acqrel", (true, true)),
    ] {
        let source = format!(
            "if {keyword} true:\n    \"first\" -> output\nelse if acquire false:\n    \"second\" -> output\nelse:\n    \"third\" -> output\n"
        );
        let document = language::parse(&source).unwrap();
        let statement = &document.statements[0];
        let StatementKind::Scheduled {
            asynchronous,
            awaited,
            statement: inner,
        } = &statement.kind
        else {
            panic!("expected scheduled condition");
        };
        assert_eq!((*asynchronous, *awaited), flags);
        assert_eq!(inner.span.end, statement.span.end);
        assert_eq!(statement.span.end, source.trim_end().len());
        let StatementKind::If {
            then_body,
            else_body,
            ..
        } = &inner.kind
        else {
            panic!()
        };
        assert_eq!(then_body.len(), 1);
        let StatementKind::Scheduled {
            statement: branch, ..
        } = &else_body[0].kind
        else {
            panic!()
        };
        let StatementKind::If { else_body, .. } = &branch.kind else {
            panic!()
        };
        assert_eq!(else_body.len(), 1);
        let formatted = language::format(&source).unwrap();
        assert!(formatted.starts_with(&format!("if {keyword} true:\n")));
        assert!(formatted.contains("else if acquire false:\n"));
        assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
}

#[test]
fn scheduled_conditions_buffer_as_one_repl_submission() {
    let mut buffer = SubmissionBuffer::default();
    assert_eq!(
        buffer.push_line("if acqrel true:").unwrap(),
        Submission::Pending
    );
    assert_eq!(
        buffer.push_line("    delete \"stale\"").unwrap(),
        Submission::Pending
    );
    assert_eq!(buffer.push_line("else:").unwrap(), Submission::Pending);
    assert_eq!(
        buffer.push_line("    \"fresh\" -> output").unwrap(),
        Submission::Pending
    );
    let Submission::Ready(source) = buffer.push_line("").unwrap() else {
        panic!()
    };
    assert!(language::parse(&source).is_ok());
}

#[test]
fn malformed_ranges_and_duplicate_conditional_scheduling_report_owned_tokens() {
    for source in [
        "values := [true, false\n",
        "values := [true, , false]\n",
        "values := [identity(true], false)\n",
        "values := [true] unexpected\n",
        "if release release true:\n    delete \"stale\"\n",
        "if acqrel acquire true:\n    delete \"stale\"\n",
        "if release:\n    delete \"stale\"\n",
    ] {
        let errors = language::parse(source).unwrap_err();
        for error in errors {
            let span = error.diagnostic.span;
            assert!(
                span.start <= span.end && span.end <= source.len(),
                "{source}: {error:?}"
            );
            assert!(source.is_char_boundary(span.start) && source.is_char_boundary(span.end));
        }
    }
    let source = format!("values := {}true{}\n", "[".repeat(129), "]".repeat(129));
    assert!(language::parse(&source).is_err());
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(48))]

    #[test]
    fn literal_range_formatting_preserves_generated_order_and_types(
        values in prop::collection::vec(("[A-Za-z 可莉🌟#,$:()\\[\\]]{0,16}", any::<bool>()), 0..9),
    ) {
        let elements = values.iter().flat_map(|(text, value)| [
            serde_json::to_string(text).unwrap(), value.to_string(),
        ]).collect::<Vec<_>>();
        let source = format!("items := [{}]\n", elements.join(", "));
        let formatted = language::format(&source).unwrap();
        prop_assert_eq!(language::format(&formatted).unwrap(), formatted.clone());
        let ExprKind::Range(parsed) = binding(&formatted).kind else { unreachable!() };
        prop_assert_eq!(parsed.len(), values.len() * 2);
        for (pair, (expected_text, expected_bool)) in parsed.as_chunks::<2>().0.iter().zip(values) {
            prop_assert!(matches!(&pair[0].kind, ExprKind::Literal(text) if text == &expected_text));
            prop_assert!(matches!(&pair[1].kind, ExprKind::Bool(value) if *value == expected_bool));
        }
    }
}
