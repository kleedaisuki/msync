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
