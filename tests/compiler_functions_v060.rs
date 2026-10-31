//! Pure function specialization and nested lexical declaration journeys.

use std::path::PathBuf;

use msync::compiler::{CompileContext, structured::Session};
use msync::hir::{Input, InstructionKind};
use msync::language;

fn context() -> CompileContext {
    CompileContext {
        pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
        source_file: None,
    }
}

fn compile(source: &str) -> msync::hir::Program {
    Session::new()
        .compile(&language::parse(source).unwrap(), &context())
        .unwrap()
}

#[test]
fn nested_symbol_imports_parent_parameter_without_leaking_local_declaration() {
    let mut session = Session::new();
    let source = "symbol outer(base):\n    symbol child(leaf):\n        global base\n        return base / leaf\n    return child(\"config\")\nouter(root) -> output\n";
    let program = session
        .compile(&language::parse(source).unwrap(), &context())
        .unwrap();
    assert!(matches!(
        &program.body.instructions[0].kind,
        InstructionKind::Transfer { source: Input::File { location, .. }, .. }
            if location.path.ends_with("root/config")
    ));
    assert!(
        !session
            .bindings()
            .iter()
            .any(|binding| binding.contains("symbol child("))
    );
}

#[test]
fn nested_symbol_global_reaches_ancestors_not_imported_by_parent() {
    let program = compile(
        "root := base\nsymbol outer():\n    symbol child():\n        global root\n        return root / \"config\"\n    return child()\nouter() -> output\n",
    );
    assert!(matches!(
        &program.body.instructions[0].kind,
        InstructionKind::Transfer { source: Input::File { location, .. }, .. }
            if location.path.ends_with("base/config")
    ));
}

#[test]
fn nested_boolean_specializes_string_comparison_and_logic() {
    let program = compile(
        "boolean outer(a, b):\n    boolean ordered(x):\n        global b\n        return x < b\n    return ordered(a) and not false\nif outer(\"alpha\", \"beta\"):\n    \"selected\" -> output\nelse:\n    \"wrong\" -> output\n",
    );
    assert_eq!(program.body.instructions.len(), 1);
    assert!(matches!(
        &program.body.instructions[0].kind,
        InstructionKind::Transfer { source: Input::Literal(text), .. } if &**text == "selected"
    ));
}

#[test]
fn boolean_must_return_bool_and_definitions_require_return() {
    for source in [
        "boolean invalid():\n    value = true\n",
        "boolean invalid():\n    return \"not bool\"\nresult = invalid()\n",
        "boolean invalid():\n    symbol nested():\n        return leaf\n    return true\n",
        "symbol invalid():\n    boolean nested():\n        return true\n    return leaf\n",
    ] {
        let parsed = language::parse(source).unwrap();
        assert!(
            Session::new().compile(&parsed, &context()).is_err(),
            "{source}"
        );
    }
}

#[test]
fn boolean_truth_table_is_fully_constant_folded() {
    for a in [false, true] {
        for b in [false, true] {
            for c in [false, true] {
                let source = format!(
                    "boolean any(a, b, c):\n    return a or b or c\nif any({a}, {b}, {c}):\n    \"yes\" -> result\nelse:\n    \"no\" -> result\n"
                );
                let program = compile(&source);
                assert_eq!(program.body.instructions.len(), 1);
                assert!(matches!(
                    &program.body.instructions[0].kind,
                    InstructionKind::Transfer { source: Input::Literal(text), .. }
                        if &**text == if a || b || c { "yes" } else { "no" }
                ));
            }
        }
    }
}

#[test]
fn runtime_boolean_local_is_evaluated_once_before_repeated_use() {
    use msync::hir::control::{ControlInstruction, ExprKind};

    let program = compile(
        "boolean stable():\n    present = exists \"candidate\"\n    return present == present\nif stable():\n    \"yes\" -> result\n",
    );
    let condition = program
        .body
        .instructions
        .iter()
        .find_map(|instruction| match &instruction.kind {
            InstructionKind::Control(ControlInstruction::If { condition, .. }) => Some(condition),
            _ => None,
        })
        .expect("runtime condition");
    let ExprKind::Let { slot, value, body } = &condition.kind else {
        panic!("the local predicate needs one lexical runtime snapshot: {condition:?}");
    };
    assert!(matches!(&value.kind, ExprKind::Exists(_)));
    let ExprKind::Binary { left, right, .. } = &body.kind else {
        panic!("expected bool equality");
    };
    assert!(matches!(left.kind, ExprKind::Slot(id) if id == *slot));
    assert!(matches!(right.kind, ExprKind::Slot(id) if id == *slot));
}

#[test]
fn global_imports_the_prior_symbol_definition_not_a_later_redefinition() {
    let program = compile(
        "symbol prior():\n    return \"old\"\nsymbol consumer():\n    global prior\n    return prior()\nsymbol prior():\n    return \"new\"\nconsumer() -> result\n",
    );
    assert!(matches!(&program.body.instructions[0].kind,
        InstructionKind::Transfer { source: Input::Literal(text), .. } if &**text == "old"));
}

#[test]
fn global_imports_the_prior_boolean_definition_not_a_later_redefinition() {
    let program = compile(
        "boolean prior():\n    return true\nboolean consumer():\n    global prior\n    return prior()\nboolean prior():\n    return false\nif consumer():\n    \"old\" -> result\nelse:\n    \"new\" -> result\n",
    );
    assert_eq!(program.body.instructions.len(), 1);
    assert!(matches!(&program.body.instructions[0].kind,
        InstructionKind::Transfer { source: Input::Literal(text), .. } if &**text == "old"));
}

#[test]
fn global_cannot_import_forward_templates_or_replace_local_templates() {
    for source in [
        "symbol consumer():\n    global later\n    return later()\nsymbol later():\n    return leaf\nconsumer()\n",
        "boolean consumer():\n    global later\n    return later()\nboolean later():\n    return true\nconsumer()\n",
        "symbol prior():\n    return leaf\nsymbol consumer():\n    symbol prior():\n        return other\n    global prior\n    return prior()\nconsumer()\n",
    ] {
        let document = language::parse(source).unwrap();
        assert!(
            Session::new().compile(&document, &context()).is_err(),
            "unexpectedly accepted:\n{source}"
        );
    }
}
