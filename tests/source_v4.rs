use std::path::PathBuf;

use msync::compiler::structured::Session;
use msync::compiler::{CompileContext, Compiler};
use msync::hir::InstructionKind;
use msync::hir::graph::PlanningContext;
use msync::language::{self, ExprKind, StatementKind};
use msync::pipeline;
use msync::syntax::{Report, Span};

fn context(name: &str) -> CompileContext {
    CompileContext {
        pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
        source_file: Some(PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(name)),
    }
}

#[test]
fn quoted_suffix_provenance_maps_decoded_bytes_back_to_utf8_and_escapes() {
    let text = "$local::\"目录/<\\\\z[>\" -> output/\n";
    let document = language::parse(text).unwrap();
    let StatementKind::Transfer { source, .. } = &document.statements[0].kind else {
        panic!()
    };
    let ExprKind::Path(path) = &source.kind else {
        panic!()
    };
    let start = path.path.find('<').unwrap() + 1;
    let site = source.source.interval(start, path.path.len() - 1);
    assert_eq!(&text[site.start..site.end], "\\\\z[");
    let errors = pipeline::compile_named(
        &mut Session::new(),
        text,
        "unicode.msync",
        &context("unicode.msync"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    assert_eq!(errors[0].report.diagnostic.span, site);
    assert!(errors[0].report.diagnostic.message.contains("regex"));
}

#[test]
fn foreign_macro_diagnostics_keep_owned_definition_text_and_one_error_heading() {
    let definition =
        "# retained Unicode source\nmacro bad():\n    $absent::\"目录/file\" -> output\n";
    let mut session = Session::new();
    pipeline::compile_named(
        &mut session,
        definition,
        "one/sync.msync",
        &context("one/sync.msync"),
        &PlanningContext::default(),
    )
    .unwrap();
    let errors = pipeline::compile_named(
        &mut session,
        "bad()\n",
        "two/sync.msync",
        &context("two/sync.msync"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    let error = &errors[0];
    assert_eq!(&*error.source.name, "one/sync.msync");
    assert_eq!(&*error.source.text, definition);
    assert_eq!(
        &definition[error.report.diagnostic.span.start..error.report.diagnostic.span.end],
        "$absent"
    );
    assert_eq!(&*error.related[0].site.source.name, "two/sync.msync");
    assert_eq!(error.related[0].site.span, Span { start: 0, end: 5 });
    let rendered = error.render(false);
    assert_eq!(rendered.matches("error:").count(), 1, "{rendered}");
    assert!(rendered.contains("one/sync.msync:3:"), "{rendered}");
    assert!(rendered.contains("two/sync.msync:1:"), "{rendered}");
    assert!(
        rendered.contains("while expanding macro 'bad'"),
        "{rendered}"
    );
    assert!(rendered.contains("目录/file"));
    let basic = error.clone().into_report();
    assert_eq!(basic.diagnostic.span, Span { start: 0, end: 5 });
    assert!(
        basic
            .help
            .iter()
            .any(|help| help.contains("one/sync.msync"))
    );
}

#[test]
fn runtime_origin_retains_call_argument_and_definition_without_hir_layout_changes() {
    let mut session = Session::new();
    pipeline::compile_named(
        &mut session,
        "macro copy(src, dst):\n    src/file -> dst\n",
        "defs.msync",
        &context("defs.msync"),
        &PlanningContext::default(),
    )
    .unwrap();
    let caller = "copy($pwd::missing, output)\n";
    let compiled = pipeline::compile_named(
        &mut session,
        caller,
        "<repl>",
        &context("caller.msync"),
        &PlanningContext::default(),
    )
    .unwrap();
    let node = &compiled.program.body.instructions[0];
    assert!(matches!(node.kind, InstructionKind::Transfer { .. }));
    let report = compiled.instruction_report(
        node.id,
        Report::new("source missing", node.span),
        Some(false),
    );
    assert_eq!(&*report.source.name, "<repl>");
    assert_eq!(
        &caller[report.report.diagnostic.span.start..report.report.diagnostic.span.end],
        "$pwd::missing"
    );
    assert_eq!(&*report.related[0].site.source.name, "defs.msync");
    assert_eq!(compiled.sources.iter().count(), 2);
}

#[test]
fn named_compile_failure_remains_atomic_and_legacy_import_does_not_parse_display_text() {
    let mut compiler = Compiler::new();
    compiler
        .compile(&msync::syntax::parse("root := \"path with spaces\"\nremote => $wsl\n").unwrap())
        .unwrap();
    let mut session = Session::from_legacy(compiler.clone());
    pipeline::compile_named(
        &mut session,
        "macro keep():\n    global root\n    root -> output\n",
        "definition",
        &context("definition"),
        &PlanningContext::default(),
    )
    .unwrap();
    let before = session.bindings();
    assert!(
        pipeline::compile_named(
            &mut session,
            "leak := value\nunknown()\n",
            "bad",
            &context("bad"),
            &PlanningContext::default()
        )
        .is_err()
    );
    assert_eq!(session.bindings(), before);
    compiler
        .compile(&msync::syntax::parse("root := changed\n").unwrap())
        .unwrap();
    session.merge_legacy(&compiler);
    assert!(
        session
            .bindings()
            .iter()
            .any(|binding| binding.starts_with("macro keep"))
    );
    let compiled = pipeline::compile_named(
        &mut session,
        "keep()\n",
        "caller",
        &context("caller"),
        &PlanningContext::default(),
    )
    .unwrap();
    let InstructionKind::Transfer {
        source: msync::hir::Input::File { location, .. },
        ..
    } = &compiled.program.body.instructions[0].kind
    else {
        panic!()
    };
    assert!(location.path.ends_with("/path with spaces"));
}

#[test]
fn a_legacy_dual_namespace_item_name_is_diagnosed_without_changing_the_session() {
    let mut legacy = Compiler::new();
    legacy
        .compile(&msync::syntax::parse("same => $local\nsame := path\n").unwrap())
        .unwrap();
    let mut session = Session::new();
    let before = session.bindings();
    let error = session.try_merge_legacy(&legacy).unwrap_err();
    assert!(
        error
            .diagnostic
            .message
            .contains("both a namespace and a path")
    );
    assert_eq!(session.bindings(), before);
}

#[test]
fn named_arity_and_previous_stream_owner_labels_keep_their_original_source() {
    let mut session = Session::new();
    pipeline::compile_named(
        &mut session,
        "macro save(a):\n    a -> output\n",
        "macro-definition",
        &context("defs"),
        &PlanningContext::default(),
    )
    .unwrap();
    let error = pipeline::compile_named(
        &mut session,
        "save()\n",
        "caller",
        &context("caller"),
        &PlanningContext::default(),
    )
    .unwrap_err()
    .remove(0);
    assert_eq!(&*error.source.name, "caller");
    assert_eq!(&*error.related[0].site.source.name, "macro-definition");
    pipeline::compile_named(
        &mut session,
        "p => $path::python()\np::stdout -> output\n",
        "first-owner",
        &context("first"),
        &PlanningContext::default(),
    )
    .unwrap();
    let error = pipeline::compile_named(
        &mut session,
        "p::stdout -> other\n",
        "second-owner",
        &context("second"),
        &PlanningContext::default(),
    )
    .unwrap_err()
    .remove(0);
    assert_eq!(&*error.source.name, "second-owner");
    assert_eq!(&*error.related[0].site.source.name, "first-owner");
}

#[test]
fn qualified_quoted_members_map_errors_through_escaped_utf8_suffixes() {
    let source = "f => $file\nf::\"th\\t字\" -> out\n";
    let errors = pipeline::compile_named(
        &mut Session::new(),
        source,
        "members",
        &context("members"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    let span = errors[0].report.diagnostic.span;
    assert_eq!(&source[span.start..span.end], "th\\t字");
    let source = "p => $path::python()\np::stdot -> out\n";
    let errors = pipeline::compile_named(
        &mut Session::new(),
        source,
        "ports",
        &context("ports"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    let span = errors[0].report.diagnostic.span;
    assert_eq!(&source[span.start..span.end], "stdot");
}

#[test]
fn preparation_reports_name_the_exact_source_or_destination_operand() {
    let temporary = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    std::fs::create_dir_all(&temporary).unwrap();
    let root = tempfile::tempdir_in(temporary).unwrap();
    std::fs::write(root.path().join("source"), "bytes").unwrap();
    std::fs::create_dir(root.path().join("destination")).unwrap();
    let context = CompileContext {
        pwd: root.path().into(),
        source_file: None,
    };
    for (text, operand, start, end) in [
        (
            "source -> destination\n",
            msync::preparation::Operand::Destination,
            10,
            21,
        ),
        (
            "missing -> target\n",
            msync::preparation::Operand::Source,
            0,
            7,
        ),
    ] {
        let compiled = pipeline::compile_named(
            &mut Session::new(),
            text,
            "transfer.msync",
            &context,
            &PlanningContext::default(),
        )
        .unwrap();
        let errors = msync::preparation::prepare_detailed(&compiled.program, false).unwrap_err();
        assert_eq!(errors[0].operand, operand);
        let located = compiled.preparation_report(errors.into_iter().next().unwrap());
        assert_eq!(located.report.diagnostic.span, Span { start, end });
        assert_eq!(&*located.source.name, "transfer.msync");
        if operand == msync::preparation::Operand::Destination {
            let rendered = located.render(false);
            assert!(rendered.contains("1:11"), "{rendered}");
            assert!(rendered.contains("append '/'"), "{rendered}");
        }
    }
}

#[test]
fn preparation_macro_operand_errors_keep_the_actual_argument_source() {
    let temporary = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    std::fs::create_dir_all(&temporary).unwrap();
    let root = tempfile::tempdir_in(temporary).unwrap();
    std::fs::write(root.path().join("source"), "bytes").unwrap();
    std::fs::create_dir(root.path().join("destination")).unwrap();
    let context = CompileContext {
        pwd: root.path().into(),
        source_file: None,
    };
    let mut session = Session::new();
    pipeline::compile_named(
        &mut session,
        "macro copy(src, dst):\n    src -> dst\n",
        "definition.msync",
        &context,
        &PlanningContext::default(),
    )
    .unwrap();
    let text = "copy(source, destination)\n";
    let compiled = pipeline::compile_named(
        &mut session,
        text,
        "caller.msync",
        &context,
        &PlanningContext::default(),
    )
    .unwrap();
    let errors = msync::preparation::prepare_detailed(&compiled.program, false).unwrap_err();
    let located = compiled.preparation_report(errors.into_iter().next().unwrap());
    assert_eq!(&*located.source.name, "caller.msync");
    let span = located.report.diagnostic.span;
    assert_eq!(&text[span.start..span.end], "destination");
    let rendered = located.render(false);
    assert!(rendered.contains("definition.msync"), "{rendered}");
    assert_eq!(rendered.matches("error:").count(), 1, "{rendered}");
}

#[test]
fn failed_join_shows_expanded_operands_and_owned_item_definitions() {
    let mut session = Session::new();
    let definitions = "remote => ssh://Example\n.codex := ~/.codex\n";
    pipeline::compile_named(
        &mut session,
        definitions,
        "definitions.msync",
        &context("definitions.msync"),
        &PlanningContext::default(),
    )
    .unwrap();
    let expression = "remote::~/.codex/config.toml -> record.txt\n";
    let errors = pipeline::compile_named(
        &mut session,
        expression,
        "<repl>",
        &context("use.msync"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    let error = &errors[0];
    assert_eq!(
        &expression[error.report.diagnostic.span.start..error.report.diagnostic.span.end],
        "/"
    );
    let output = error.render(false);
    for expected in [
        "left operand expands to path item ssh://Example::\"~\"",
        "right operand expands to endpoint-relative path item \"~/.codex\"",
        "item '.codex' expands to",
        "definitions.msync:1:",
        "<repl>:1:",
    ] {
        assert!(output.contains(expected), "missing {expected:?}: {output}");
    }
    assert_eq!(output.matches("error:").count(), 1, "{output}");
    assert!(!output.contains('\x1b'));
    let basic = error.clone().into_report();
    assert!(basic.help.iter().any(
        |help| help.contains("item '.codex' expands to") && help.contains("definitions.msync")
    ));
}

#[test]
fn expansion_trace_retains_alias_snapshots_and_macro_parameter_calls() {
    let mut session = Session::new();
    let definitions = "base := ~/.codex\nmiddle := base\nleaf := middle\nmacro inner(component):\n    root/component -> output\nmacro outer(argument):\n    inner(argument)\nbase := safe\n";
    pipeline::compile_named(
        &mut session,
        definitions,
        "defs.msync",
        &context("defs.msync"),
        &PlanningContext::default(),
    )
    .unwrap();
    let errors = pipeline::compile_named(
        &mut session,
        "outer(leaf)\n",
        "use.msync",
        &context("use.msync"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    let error = &errors[0];
    let output = error.render(false);
    for expected in [
        "item 'leaf' expands to",
        "item 'middle' expands to",
        "item 'base' expands to endpoint-relative path item \"~/.codex\"",
        "macro parameter 'component' expands to",
        "macro parameter 'argument' expands to",
        "while expanding macro 'inner'",
        "while expanding macro 'outer'",
        "use.msync:1:",
    ] {
        assert!(output.contains(expected), "missing {expected:?}: {output}");
    }
    // Inspect structured related locations rather than inferring a declaration
    // from the renderer's surrounding context lines.
    assert!(
        error
            .related
            .iter()
            .any(|label| label.message.starts_with("item 'base' expands to")
                && label.site.span.start == 0)
            || error
                .report
                .labels
                .iter()
                .any(|label| label.message.starts_with("item 'base' expands to")
                    && label.span.start == 0)
    );
    assert_eq!(output.matches("error:").count(), 1, "{output}");
}

#[test]
fn stored_expansion_traces_are_bounded_across_long_rebinding_chains() {
    let mut source = String::from("item := ~/root\n");
    for _ in 0..2000 {
        source.push_str("item := item\n");
    }
    source.push_str("base/item -> output\n");
    let errors = pipeline::compile_named(
        &mut Session::new(),
        &source,
        "long.msync",
        &context("long.msync"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    let output = errors[0].render(false);
    assert_eq!(output.matches("; bound here").count(), 16, "{output}");
    assert!(
        output.contains("Further item expansion frames omitted (limit 16)"),
        "{output}"
    );
    assert!(
        output.len() < 20_000,
        "unexpected expansion flood: {} bytes",
        output.len()
    );
}

#[test]
fn expansion_labels_escape_controls_and_preserve_literal_join_semantics() {
    let source = "bad := \"/root/file\\n\\tend\"\nbase/bad -> output\n";
    let errors = pipeline::compile_named(
        &mut Session::new(),
        source,
        "controls.msync",
        &context("controls.msync"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    let output = errors[0].render(false);
    assert!(
        output.contains("item 'bad' expands to string literal"),
        "{output}"
    );
    assert!(output.contains("\\n\\t"), "{output}");
    assert!(!output.contains('\x1b'));
    assert!(
        output.contains("right operand expands to endpoint-relative path item"),
        "{output}"
    );
}

mod expansion_properties {
    use super::*;
    use proptest::prelude::*;

    proptest! {
        #![proptest_config(ProptestConfig::with_cases(64))]
        #[test]
        fn alias_frames_are_bounded_unicode_safe_and_color_invariant(count in 1usize..65, tail in "[a-z一-龥]{1,20}") {
            let mut source = format!("item_0 := ~/{tail}\n");
            for index in 1..count { source.push_str(&format!("item_{index} := item_{}\n", index - 1)); }
            source.push_str(&format!("base/item_{} -> output\n", count - 1));
            let errors = pipeline::compile_named(&mut Session::new(), &source, "property.msync", &context("property.msync"), &PlanningContext::default()).unwrap_err();
            let error = &errors[0];
            prop_assert_eq!(&source[error.report.diagnostic.span.start..error.report.diagnostic.span.end], "/");
            let plain = error.render(false);
            prop_assert_eq!(plain.matches("; bound here").count(), count.min(16));
            prop_assert_eq!(plain.contains("Further item expansion frames omitted"), count > 16);
            prop_assert!(plain.contains(&tail));
            let styled = error.render(true);
            let sgr = regex::Regex::new(r"\x1b\[[0-9;]*m").unwrap();
            let unstyled = sgr.replace_all(&styled, "");
            prop_assert_eq!(unstyled.as_ref(), plain.as_str());
        }
    }
}

#[test]
fn macro_global_expansion_uses_the_definition_time_binding_not_a_later_rebind() {
    let mut session = Session::new();
    let definitions = "root := ~/original\nmacro bad():\n    global root\n    base/root -> output\nroot := relative\n";
    pipeline::compile_named(
        &mut session,
        definitions,
        "snapshot.msync",
        &context("snapshot.msync"),
        &PlanningContext::default(),
    )
    .unwrap();
    let errors = pipeline::compile_named(
        &mut session,
        "bad()\n",
        "use.msync",
        &context("use.msync"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    let output = errors[0].render(false);
    assert!(
        output.contains("item 'root' expands to endpoint-relative path item \"~/original\""),
        "{output}"
    );
    assert!(
        !output.contains("item 'root' expands to endpoint-relative path item \"relative\""),
        "{output}"
    );
    assert!(output.contains("while expanding macro 'bad'"), "{output}");
}

#[test]
fn builtin_module_items_do_not_borrow_unrelated_same_named_user_origins() {
    for item in ["parent", "this"] {
        let source = format!("{item} := ~/not-used\nbase/$file::{item} -> output\n");
        let errors = pipeline::compile_named(
            &mut Session::new(),
            &source,
            "builtin.msync",
            &context("builtin.msync"),
            &PlanningContext::default(),
        )
        .unwrap_err();
        let report = &errors[0];
        let false_origin = format!("item '{item}' expands to");
        assert!(
            !report
                .report
                .labels
                .iter()
                .any(|label| label.message.starts_with(&false_origin))
        );
        assert!(
            !report
                .related
                .iter()
                .any(|label| label.message.starts_with(&false_origin))
        );
        assert!(
            report
                .render(false)
                .contains("right operand expands to path item $local::")
        );
    }
}

#[test]
fn failed_qualifier_does_not_claim_an_unvisited_rhs_item_was_expanded() {
    for qualifier in ["missing", "space"] {
        let source =
            format!("space := value\nhidden := ~/not-used\n{qualifier}::hidden -> output\n");
        let errors = pipeline::compile_named(
            &mut Session::new(),
            &source,
            "qualifier.msync",
            &context("qualifier.msync"),
            &PlanningContext::default(),
        )
        .unwrap_err();
        let error = &errors[0];
        assert!(
            !error
                .report
                .labels
                .iter()
                .any(|label| label.message.starts_with("item 'hidden' expands to"))
        );
        assert!(
            !error
                .related
                .iter()
                .any(|label| label.message.starts_with("item 'hidden' expands to"))
        );
    }
}

#[test]
fn object_alias_exports_keep_own_declarations_and_imported_origins_are_honest() {
    let mut session = Session::new();
    let source = "a := ~/old\nb := a\n";
    let module = pipeline::compile_relocatable_named(
        &mut session,
        source,
        "aliases.msync",
        &context("aliases.msync"),
        &PlanningContext::default(),
    )
    .unwrap()
    .into_object()
    .unwrap();
    let mut imported = Session::new();
    msync::context::contextualize(
        &module,
        &msync::context::Context::new(&context("use.msync")),
        &mut imported,
    )
    .unwrap();
    let errors = pipeline::compile_named(
        &mut imported,
        "base/b -> output\n",
        "use.msync",
        &context("use.msync"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    let error = &errors[0];
    let origin = error
        .related
        .iter()
        .find(|label| label.message.starts_with("imported item 'b' expands to"))
        .unwrap();
    assert_eq!(
        &origin.site.source.text[origin.site.span.start..origin.site.span.end],
        "b"
    );
    assert_eq!(origin.site.span.start, source.find("b :=").unwrap());
    assert!(origin.message.ends_with("source origin"));
    assert!(!origin.message.ends_with("bound here"));
}

#[test]
fn rejected_call_values_do_not_claim_to_expand_unvisited_callees_or_arguments() {
    for expression in ["base/hidden() -> output\n", "bad := hidden()\n"] {
        let source = format!("hidden := ~/not-used\n{expression}");
        let errors = pipeline::compile_named(
            &mut Session::new(),
            &source,
            "calls.msync",
            &context("calls.msync"),
            &PlanningContext::default(),
        )
        .unwrap_err();
        let error = &errors[0];
        assert!(
            !error
                .report
                .labels
                .iter()
                .any(|label| label.message.starts_with("item 'hidden' expands to"))
        );
        assert!(
            !error
                .related
                .iter()
                .any(|label| label.message.starts_with("item 'hidden' expands to"))
        );
    }
}
