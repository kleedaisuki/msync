//! Static acceptance independent of the compiler's internal expression representation.

use std::path::PathBuf;

use msync::compiler::{CompileContext, structured::Session};
use msync::hir::{Input, InstructionKind};
use msync::language;
use proptest::prelude::*;

fn context() -> CompileContext {
    let pwd = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/static-v060");
    CompileContext {
        source_file: Some(pwd.join("entry.msync")),
        pwd,
    }
}

fn rejects(source: &str) {
    let Ok(document) = language::parse(source) else {
        return;
    };
    assert!(
        Session::new().compile(&document, &context()).is_err(),
        "unexpectedly accepted:\n{source}"
    );
}

#[test]
fn conditions_and_logical_operators_never_apply_truthiness_coercions() {
    for source in [
        "if \"true\":\n    \"wrong\" -> output\n",
        "if path:\n    \"wrong\" -> output\n",
        "value = not \"false\"\n",
        "value = true and \"true\"\n",
        "value = true == \"true\"\n",
        "value = true < false\n",
        "boolean bad():\n    return \"true\"\nbad()\n",
        "boolean missing():\n    value = true\n",
        "break true\n",
    ] {
        rejects(source);
    }
}

#[test]
fn unselected_constant_branches_still_receive_static_type_checks() {
    for source in [
        "if false:\n    if \"not a bool\":\n        \"wrong\" -> output\n",
        "if true:\n    \"ok\" -> output\nelse:\n    invalid = true and \"not a bool\"\n",
        "value = loop:\n    if exists marker:\n        break true\n    else:\n        break \"string\"\n",
    ] {
        rejects(source);
    }
}

#[test]
fn dropped_and_nested_template_names_cannot_escape_their_lexical_scope() {
    for source in [
        "macro gone():\n    \"wrong\" -> output\ndrop gone\ngone()\n",
        "symbol gone(value):\n    return value\ndrop gone\ngone(\"wrong\") -> output\n",
        "macro outer():\n    macro inner():\n        \"wrong\" -> output\n    inner()\nouter()\ninner()\n",
    ] {
        rejects(source);
    }
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(48))]

    #[test]
    fn string_lexical_comparisons_fold_like_rust_utf8_order(
        left in "[a-zA-Z0-9可莉é]{0,12}",
        right in "[a-zA-Z0-9可莉é]{0,12}",
        operator in prop::sample::select(vec!["<", "<=", ">", ">=", "==", "!="]),
    ) {
        let expected = match operator {
            "<" => left < right,
            "<=" => left <= right,
            ">" => left > right,
            ">=" => left >= right,
            "==" => left == right,
            "!=" => left != right,
            _ => unreachable!(),
        };
        let source = format!(
            "left = {}\nright = {}\nif (left {operator} right):\n    \"true\" -> output\nelse:\n    \"false\" -> output\n",
            serde_json::to_string(&left).unwrap(),
            serde_json::to_string(&right).unwrap(),
        );
        let document = language::parse(&source).unwrap();
        let program = Session::new().compile(&document, &context()).unwrap();
        let literals = program.body.instructions.iter().filter_map(|instruction| {
            if let InstructionKind::Transfer { source: Input::Literal(text), .. } = &instruction.kind {
                Some(&**text)
            } else {
                None
            }
        }).collect::<Vec<_>>();
        prop_assert_eq!(literals, vec![if expected { "true" } else { "false" }]);
    }
}

#[test]
fn mutation_requires_same_scalar_type_and_cannot_export_branch_locals() {
    for source in [
        "value = false\nvalue = \"string\"\n",
        "value = \"string\"\nvalue = true\n",
        "value = false\nif exists marker:\n    value = \"string\"\n",
        "if exists marker:\n    branch_local = \"hidden\"\ndrop branch_local\n",
    ] {
        rejects(source);
    }
}
