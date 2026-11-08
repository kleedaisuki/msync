use msync::language::{
    self, BinaryOp, BindingOp, ExprKind, StatementKind, Submission, SubmissionBuffer,
};

#[test]
fn bool_operations_have_stable_precedence_and_owned_utf8_spans() {
    let source = "可莉 = true or false and not (\"a\" >= \"b\")\n";
    let document = language::parse(source).unwrap();
    let StatementKind::Binding {
        name,
        op: BindingOp::Assign,
        value,
    } = &document.statements[0].kind
    else {
        panic!()
    };
    assert_eq!(&source[name.span.start..name.span.end], "可莉");
    let ExprKind::Binary {
        op: BinaryOp::Or,
        right,
        operator,
        ..
    } = &value.kind
    else {
        panic!()
    };
    assert_eq!(&source[operator.start..operator.end], "or");
    let ExprKind::Binary {
        op: BinaryOp::And,
        right,
        ..
    } = &right.kind
    else {
        panic!()
    };
    let ExprKind::Not(operand) = &right.kind else {
        panic!()
    };
    assert!(matches!(
        operand.kind,
        ExprKind::Binary {
            op: BinaryOp::Ge,
            ..
        }
    ));
    assert_eq!(
        language::format(&language::format(source).unwrap()).unwrap(),
        language::format(source).unwrap()
    );
}

#[test]
fn not_wraps_comparisons_and_range_reducers_bind_before_logic() {
    for source in [
        "ready = not \"a\" == \"b\"\n",
        "ready = not files/<.+> | ranges::empty\n",
    ] {
        let document = language::parse(source).unwrap();
        let StatementKind::Binding { value, .. } = &document.statements[0].kind else {
            panic!()
        };
        assert!(matches!(value.kind, ExprKind::Not(_)), "{source}");
    }
    let document = language::parse("ready = files/<.+> | ranges::empty or true\n").unwrap();
    let StatementKind::Binding { value, .. } = &document.statements[0].kind else {
        panic!()
    };
    let ExprKind::Binary {
        left,
        op: BinaryOp::Or,
        ..
    } = &value.kind
    else {
        panic!()
    };
    assert!(matches!(
        left.kind,
        ExprKind::Binary {
            op: BinaryOp::Pipe,
            ..
        }
    ));
}

#[test]
fn lexical_declarations_and_loop_values_are_real_indented_blocks() {
    let source = "macro outer():\n    macro inner():\n        delete \"stale\"\n    boolean ready(x):\n        boolean yes(y):\n            return y\n        return yes(x)\n    symbol identity(x):\n        symbol same(y):\n            return y\n        return same(x)\n    result = loop:\n        if ready(true):\n            break \"done\"\n        else if exists input:\n            break input\n        else:\n            inner()\n";
    let document = language::parse(source).unwrap();
    let StatementKind::Macro { body, .. } = &document.statements[0].kind else {
        panic!()
    };
    assert!(matches!(body[0].kind, StatementKind::Macro { .. }));
    assert!(matches!(body[1].kind, StatementKind::Boolean { .. }));
    let StatementKind::Binding { value, .. } = &body[3].kind else {
        panic!()
    };
    let ExprKind::Loop { body } = &value.kind else {
        panic!()
    };
    let StatementKind::If { else_body, .. } = &body[0].kind else {
        panic!()
    };
    assert!(matches!(else_body[0].kind, StatementKind::If { .. }));
    assert!(document.statements[0].span.end >= source.find("inner()\n").unwrap() + "inner()".len());
    let formatted = language::format(source).unwrap();
    assert_eq!(language::format(&formatted).unwrap(), formatted);
}

#[test]
fn selectors_and_filename_atoms_do_not_become_bool_operators() {
    for source in [
        "source=1 -> output=2\n",
        "source/or/and/not -> destination\n",
        "for file in root/<[<>]|a=b> | views::take(2):\n    delete file\n",
        "files := <.+>|views::reverse()\n",
        "ready = \"a\"<\"b\" and \"b\">=\"a\" # comment\n",
        "ready = exists (root/<.+>)\n",
        "ready = not(false)and(true)or(false)\n",
        "ready = exists(input)\n",
        "ready = myexists < \"z\"\n",
    ] {
        let formatted =
            language::format(source).unwrap_or_else(|error| panic!("{source}: {error:?}"));
        assert_eq!(language::format(&formatted).unwrap(), formatted, "{source}");
    }
    let document = language::parse("source=1 -> output=2\n").unwrap();
    let StatementKind::Transfer { source, .. } = &document.statements[0].kind else {
        panic!()
    };
    assert!(matches!(&source.kind, ExprKind::Path(path) if path.path == "source=1"));
}

#[test]
fn every_compound_header_is_buffered_as_one_repl_submission() {
    for header in [
        "boolean any(a):",
        "if true:",
        "for file in root/<.+>:",
        "loop:",
        "result = loop:",
    ] {
        let mut buffer = SubmissionBuffer::default();
        assert_eq!(buffer.push_line(header).unwrap(), Submission::Pending);
        assert_eq!(
            buffer.push_line("    break true").unwrap(),
            Submission::Pending
        );
        assert!(matches!(
            buffer.push_line("").unwrap(),
            Submission::Ready(_)
        ));
    }
}

#[test]
fn malformed_control_flow_is_rejected_with_source_ranges() {
    for source in [
        "if true\n    delete file\n",
        "else:\n    delete file\n",
        "for file files:\n    delete file\n",
        "boolean f(a, a):\n    return a\n",
        "delete\n",
        "drop\n",
        "ready = true and\n",
    ] {
        let errors = language::parse(source).unwrap_err();
        for error in errors {
            let span = error.diagnostic.span;
            assert!(
                span.start <= span.end && span.end <= source.len(),
                "{source}: {span:?}"
            );
            assert!(source.is_char_boundary(span.start) && source.is_char_boundary(span.end));
        }
    }
}

#[test]
fn recovering_parser_keeps_valid_ast_and_real_error_spans() {
    let source = "flag = true\nboolean ready(x):\n    return x\nresult = ready(\n";
    let (document, reports) = language::parse_recovering(source);
    assert_eq!(document.statements.len(), 2);
    assert!(!reports.is_empty());
    assert!(language::parse(source).is_err());
    assert!(matches!(
        document.statements[1].kind,
        StatementKind::Boolean { .. }
    ));
    for report in reports {
        assert!(report.diagnostic.span.end <= source.len());
    }
}

#[test]
fn cr_line_endings_and_loop_expression_spans_match_the_document() {
    let source = "# msync: 0.6\rresult = loop:\r    local = \"done\"\r    break local\r";
    language::select_version(source, None, language::Version::default()).unwrap();
    let document = language::parse(source).unwrap();
    let StatementKind::Binding { value, .. } = &document.statements[1].kind else {
        panic!()
    };
    let ExprKind::Loop { body } = &value.kind else {
        panic!()
    };
    assert_eq!(value.span.end, body.last().unwrap().span.end);
    assert_eq!(
        language::format(source).unwrap(),
        "# msync: 0.6\nresult = loop:\n    local = \"done\"\n    break local\n"
    );
}
