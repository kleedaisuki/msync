//! The public compile adapter returns executable controls without losing portability elsewhere.

use std::fs;
use std::path::Path;

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{self, Options};
use msync::hir::graph::PlanningContext;
use msync::pipeline;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("pipeline-context-v065-")
        .tempdir_in(root)
        .unwrap()
}

fn context(root: &Path) -> CompileContext {
    CompileContext {
        pwd: root.to_path_buf(),
        source_file: Some(root.join("source.msync")),
    }
}

#[test]
fn public_compile_contextualizes_nested_control_and_lazy_callbacks_before_execution() {
    let root = workspace();
    fs::create_dir(root.path().join("inputs")).unwrap();
    fs::write(root.path().join("inputs/payload"), b"nested bytes").unwrap();
    fs::write(root.path().join("marker"), b"").unwrap();
    let source = "boolean present(path):\n    return exists path\nitems := inputs/<.+> | views::filter(present)\nif exists marker:\n    for item in items:\n        item -> output/\n";
    let compiled =
        pipeline::compile(&mut Session::new(), source, &context(root.path()), &[]).unwrap();
    compiled.verify().unwrap();
    assert!(!root.path().join("output").exists());
    assert!(!root.path().join("scratch").exists());
    execution::execute(
        &compiled.program,
        &Options::new(root.path().join("scratch")),
    )
    .unwrap();
    assert_eq!(
        fs::read(root.path().join("output/payload")).unwrap(),
        b"nested bytes"
    );
}

#[test]
fn contextualization_failure_keeps_the_lexical_session_atomic_and_portable_api_unbound() {
    let root = workspace();
    let context = context(root.path());
    let variable = format!(
        "MSYNC_TEST_UNBOUND_{}",
        root.path()
            .file_name()
            .unwrap()
            .to_string_lossy()
            .replace('-', "_")
    );
    assert!(std::env::var_os(&variable).is_none());
    let source = format!(
        "leaked := candidate\nmacro leaked_macro():\n    \"not executed\" -> output\nobserved = exists $env::{variable}\n"
    );
    let planning = PlanningContext::default();
    let portable = pipeline::compile_relocatable_named(
        &mut Session::new(),
        &source,
        "unbound.msync",
        &context,
        &planning,
    )
    .unwrap();
    assert!(
        serde_json::to_string(&portable.compilation.program)
            .unwrap()
            .contains(&variable)
    );

    let mut session = Session::new();
    pipeline::compile(&mut session, "retained := original\n", &context, &[]).unwrap();
    let before = session.bindings();
    let errors =
        pipeline::compile_named(&mut session, &source, "unbound.msync", &context, &planning)
            .unwrap_err();
    assert!(errors.iter().any(|error| {
        error
            .report
            .diagnostic
            .message
            .contains("unresolved environment symbol")
    }));
    assert_eq!(session.bindings(), before);
    assert!(pipeline::compile(&mut session, "leaked_macro()\n", &context, &[]).is_err());
    assert!(!root.path().join("output").exists());
    assert!(!root.path().join("scratch").exists());
}
