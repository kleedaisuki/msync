//! Range plans remain typed and lazy across compiler and object boundaries.

use std::path::PathBuf;

use msync::compiler::{CompileContext, structured::Session};
use msync::hir::InstructionKind;
use msync::hir::control::{ControlInstruction, ExprKind, RangeQuery, RangeStage, Type};
use msync::language;

fn compile(code: &str) -> Result<msync::hir::Program, Vec<msync::syntax::Report>> {
    let document = language::parse(code).unwrap();
    Session::new().compile(
        &document,
        &CompileContext {
            pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/ranges-v6-never-created"),
            source_file: None,
        },
    )
}

#[test]
fn pipelines_are_specialized_without_opening_the_directory() {
    let program = compile("boolean present(item):\n    return exists item\nfound = missing/<.*> | views::filter(present) | views::take(2) | ranges::any_of(present)\n").unwrap();
    let mut seen = false;
    for instruction in &program.body.instructions {
        let InstructionKind::Control(ControlInstruction::Let { value, .. }) = &instruction.kind
        else {
            continue;
        };
        let ExprKind::RangeQuery { range, query } = &value.kind else {
            continue;
        };
        assert_eq!(value.ty, Type::Bool);
        assert_eq!(range.item_type, Type::Path);
        assert!(matches!(
            &range.stages[..],
            [RangeStage::Filter { .. }, RangeStage::Take(2)]
        ));
        assert!(matches!(query, RangeQuery::Any { .. }));
        seen = true;
    }
    assert!(seen, "query must be compiled into typed runtime IR");
}

#[test]
fn transform_changes_item_type_statically_and_front_preserves_it() {
    let program = compile("symbol text(item):\n    return \"payload\"\nresult = missing/<.*> | views::transform(text) | views::reverse | ranges::front\n").unwrap();
    let json = serde_json::to_string(&program).unwrap();
    assert!(json.contains("range_query"));
    assert!(json.contains("transform"));
    assert!(json.contains("reverse"));
    assert!(json.contains("\"item_type\":\"string\""));
}

#[test]
fn predicates_and_transforms_reject_wrong_result_types() {
    let errors = compile(
        "symbol text(item):\n    return \"payload\"\nitems := missing/<.*> | views::filter(text)\n",
    )
    .unwrap_err();
    assert!(
        errors.iter().any(|report| report
            .diagnostic
            .message
            .contains("predicate must return bool")),
        "{errors:?}"
    );
    let errors = compile(
        "boolean yes(item):\n    return true\nitems := missing/<.*> | views::transform(yes)\n",
    )
    .unwrap_err();
    assert!(
        errors.iter().any(|report| report
            .diagnostic
            .message
            .contains("transforms must return a path or string")),
        "{errors:?}"
    );
}

#[test]
fn range_counts_and_adaptor_arity_are_checked() {
    for (code, message) in [
        (
            "items := missing/<.*> | views::take(\"-1\")\n",
            "nonnegative integer",
        ),
        (
            "items := missing/<.*> | views::filter()\n",
            "expects 1 argument",
        ),
        (
            "items := missing/<.*> | views::reverse(1)\n",
            "expects 0 argument",
        ),
    ] {
        let errors = compile(code).unwrap_err();
        assert!(
            errors
                .iter()
                .any(|report| report.diagnostic.message.contains(message)),
            "{errors:?}"
        );
    }
}

#[test]
fn finite_ranges_and_direct_adaptor_calls_have_typed_consumers() {
    let program = compile("items := ranges::of(\"a\", \"a\", \"b\", \"c\")\nfiltered := views::take(items, 3) | views::unique | views::stride(2)\nfound = ranges::contains(filtered, \"b\")\nfirst = views::single(\"only\") | ranges::find(\"only\")\n").unwrap();
    let json = serde_json::to_string(&program).unwrap();
    for expected in [
        "stride",
        "unique",
        "contains",
        "find_value",
        "\"item_type\":\"string\"",
    ] {
        assert!(json.contains(expected), "missing {expected}: {json}");
    }
}

#[test]
fn finite_range_element_and_search_types_are_enforced() {
    for (code, message) in [
        (
            "items := ranges::of(\"text\", path)\n",
            "share one static type",
        ),
        (
            "items := ranges::of(true)\n",
            "elements must be paths or strings",
        ),
        (
            "found = views::single(\"text\") | ranges::contains(path)\n",
            "search value must match",
        ),
        (
            "items := views::single(\"text\") | views::stride(0)\n",
            "stride must be positive",
        ),
    ] {
        let errors = compile(code).unwrap_err();
        assert!(
            errors
                .iter()
                .any(|report| report.diagnostic.message.contains(message)),
            "{errors:?}"
        );
    }
}
