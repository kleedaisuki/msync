use std::path::PathBuf;

use msync::compiler::{CompileContext, structured::Session};
use msync::hir::{InstructionKind, ProcessId};
use msync::source::Source;

fn lower(text: &str) -> msync::hir::Program {
    let pwd = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/frontend-barriers");
    Session::new()
        .compile_named(
            &msync::language::parse(text).unwrap(),
            &CompileContext {
                pwd: pwd.clone(),
                source_file: Some(pwd.join("input.msync")),
            },
            Source::new("input.msync", text),
        )
        .unwrap()
        .program
}

fn joins(program: &msync::hir::Program) -> Vec<Option<ProcessId>> {
    program
        .body
        .instructions
        .iter()
        .filter_map(|node| match node.kind {
            InstructionKind::Await { process } => Some(process),
            _ => None,
        })
        .collect()
}

#[test]
fn default_program_call_is_exactly_explicit_async_await() {
    let implicit = lower("p => $path::tool(\"arg\")\n");
    let explicit = lower("async await p => $path::tool(\"arg\")\n");
    let kinds = |program: &msync::hir::Program| {
        program
            .body
            .instructions
            .iter()
            .map(|node| match &node.kind {
                InstructionKind::Spawn { process, .. } => format!("spawn:{}", process.0),
                InstructionKind::Await { process } => format!("await:{process:?}"),
                InstructionKind::Barrier { kind } => format!("barrier:{kind:?}"),
                _ => "transfer".into(),
            })
            .collect::<Vec<_>>()
    };
    assert_eq!(kinds(&implicit), kinds(&explicit));
    assert_eq!(joins(&implicit), vec![Some(ProcessId(0))]);
}

#[test]
fn explicit_async_has_three_launch_flow_join_blocks() {
    let program = lower("async p => $path::tool()\np::stdout -> result\nawait p\n");
    assert_eq!(program.blocks().count(), 3);
    assert_eq!(joins(&program), vec![Some(ProcessId(0))]);
}

#[test]
fn macro_exit_joins_only_its_unawaited_programs() {
    let program = lower(
        "async outside => $path::tool()\nmacro work():\n    async a => $path::tool()\n    async b => $path::tool()\n    await a\nwork()\n",
    );
    assert_eq!(
        joins(&program),
        vec![Some(ProcessId(1)), Some(ProcessId(2))]
    );
}

#[test]
fn nested_macro_exit_does_not_duplicate_an_inner_join() {
    let program = lower(
        "macro inner():\n    async p => $path::tool()\nmacro outer():\n    inner()\nouter()\n",
    );
    assert_eq!(joins(&program), vec![Some(ProcessId(0))]);
}

#[test]
fn bare_await_satisfies_macro_pending_programs() {
    let program = lower("macro work():\n    async p => $path::tool()\n    await\nwork()\n");
    assert_eq!(joins(&program), vec![None]);
}

#[test]
fn macro_join_origins_are_valid_definition_spans() {
    let text = "macro work():\n    async p => $path::tool()\n\n\n\n\n\n\n\nwork()\n";
    let pwd = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/frontend-barriers");
    let lowered = Session::new()
        .compile_named(
            &msync::language::parse(text).unwrap(),
            &CompileContext {
                pwd,
                source_file: None,
            },
            Source::new("module", text),
        )
        .unwrap();
    for origin in lowered.origins.values() {
        assert!(origin.definition.span.end <= origin.definition.source.text.len());
        assert!(origin.site.span.end <= origin.site.source.text.len());
    }
}
