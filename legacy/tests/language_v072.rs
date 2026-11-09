use msync::language::{self, Expr, ExprKind, StatementKind};

fn binding(source: &str) -> Expr {
    let document = language::parse(source).expect("expression parses");
    let StatementKind::Binding { value, .. } = &document.statements[0].kind else {
        panic!("expected a value binding");
    };
    value.clone()
}

#[test]
fn references_are_unary_expressions_inside_ordinary_call_arguments() {
    for source in [
        "copy($local, .codex/codex.prompt)\n",
        "copy($ local, .codex / codex.prompt)\n",
        "copy($(local), .codex/codex.prompt)\n",
    ] {
        let document = language::parse(source).unwrap();
        let StatementKind::Call(value) = &document.statements[0].kind else {
            panic!("expected ordinary call");
        };
        let ExprKind::Call { arguments, .. } = &value.kind else {
            panic!("expected call expression");
        };
        let ExprKind::Reference { operand, operator } = &arguments[0].kind else {
            panic!("expected unary reference, not a path containing a dollar");
        };
        assert!(matches!(&operand.kind, ExprKind::Path(path) if path.path == "local"));
        assert_eq!(&source[operator.start..operator.end], "$");
        assert!(matches!(arguments[1].kind, ExprKind::Join { .. }));
        let formatted = language::format(source).unwrap();
        assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
}

#[test]
fn quoted_windows_path_join_retains_string_type_and_operator_source() {
    let source = r#"config := "C:\Users\STONE\.codex"/codex.prompt"#;
    let value = binding(source);
    let ExprKind::Join {
        base,
        suffix,
        operator,
        ..
    } = value.kind
    else {
        panic!("expected binary slash");
    };
    assert!(matches!(&base.kind, ExprKind::Literal(text) if text == r"C:\Users\STONE\.codex"));
    assert!(matches!(&suffix.unwrap().kind, ExprKind::Path(path) if path.path == "codex.prompt"));
    assert_eq!(&source[operator.start..operator.end], "/");
}

#[test]
fn selection_has_the_same_binary_ast_for_direct_and_computed_scopes() {
    for source in [
        "file := $local::codex.prompt\n",
        "file := $local :: codex.prompt\n",
        "file := (local)::codex.prompt\n",
        "file := identity($local)::codex.prompt\n",
        "file := $local::\"codex.prompt\"\n",
    ] {
        let value = binding(source);
        let ExprKind::Select { item, operator, .. } = value.kind else {
            panic!("all selection uses a binary expression");
        };
        assert_eq!(&source[operator.start..operator.end], "::");
        assert!(
            matches!(&item.kind, ExprKind::Path(path) if path.qualifier.is_none())
                || matches!(&item.kind, ExprKind::Literal(_))
        );
        let formatted = language::format(source).unwrap();
        assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
}

#[test]
fn endpoint_and_selector_atoms_do_not_acquire_internal_operators() {
    for atom in [
        "ssh://host/root/child",
        "ssh://[::1]/root/child",
        "//server/share/file",
        "<name/with::operators>",
    ] {
        let value = binding(&format!("value := {atom}\n"));
        assert!(
            matches!(&value.kind, ExprKind::Path(path) if path.path == atom),
            "{atom}: {value:?}"
        );
    }
}

#[test]
fn formatting_preserves_grouped_binary_operator_association() {
    for source in [
        "value := (a/b)::c\n",
        "value := a::(b::c)\n",
        "value := a::(b/c)\n",
        "value := a/(b/c)\n",
    ] {
        let formatted = language::format(source).unwrap();
        assert_eq!(formatted, source);
        assert_eq!(language::format(&formatted).unwrap(), formatted);
    }
}

#[test]
fn reference_operands_preserve_boolean_spelled_binding_names() {
    for name in ["true", "false"] {
        for reference in [
            format!("${name}"),
            format!("$ {name}"),
            format!("$({name})"),
        ] {
            let value = binding(&format!("value := {reference}\n"));
            let ExprKind::Reference { operand, .. } = value.kind else {
                panic!("expected reference operator");
            };
            assert!(
                matches!(&operand.kind, ExprKind::Path(path) if path.path == name),
                "{reference}"
            );
            let source = format!("value := {reference}\n");
            let formatted = language::format(&source).unwrap();
            assert_eq!(formatted, format!("value := ${name}\n"));
            assert_eq!(language::format(&formatted).unwrap(), formatted);
        }
        assert!(matches!(
            binding(&format!("value := {name}\n")).kind,
            ExprKind::Bool(_)
        ));
    }
}
