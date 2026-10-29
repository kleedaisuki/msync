//! Old runtime observations cannot authorize a later execution's file effects.

use std::{fs, path::Path};

use msync::compiler::CompileContext;
use msync::compiler::structured::Session;
use msync::execution::{Options, RuntimeSession};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("runtime-guard-")
        .tempdir_in(root)
        .unwrap()
}

fn program(root: &Path, text: &str) -> msync::hir::Program {
    Session::new()
        .compile(
            &msync::language::parse(text).unwrap(),
            &CompileContext {
                pwd: root.to_owned(),
                source_file: None,
            },
        )
        .unwrap()
}

#[tokio::test]
async fn fallback_retains_raw_traversal_obligations_erased_by_old_path_resolution() {
    let root = workspace();
    fs::create_dir(root.path().join("step")).unwrap();
    fs::write(root.path().join("source"), "payload").unwrap();
    let original = program(root.path(), "step/../source -> target\n");
    let prepared = msync::preparation::prepare(&original, true).unwrap();
    fs::remove_dir(root.path().join("step")).unwrap();
    fs::write(root.path().join("step"), "not a directory").unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(root.path().join(".temp"))).unwrap();
    let error = runtime.execute_prepared(&prepared).await.unwrap_err();
    assert!(error.to_string().contains("directory"), "{error}");
    assert!(!root.path().join("target").exists());
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn ordinary_owner_cancellation_does_not_poison_the_next_repl_submission() {
    let root = workspace();
    let mut runtime = RuntimeSession::new(&Options::new(root.path().join(".temp"))).unwrap();
    runtime.cancellation().cancel();
    let cancelled = program(root.path(), "\"cancelled\" -> target\n");
    assert!(runtime.submit(&cancelled).await.is_err());
    assert!(!root.path().join("target").exists());
    let next = program(root.path(), "\"next\" -> target\n");
    assert_eq!(runtime.submit(&next).await.unwrap().assignments, 1);
    assert_eq!(
        fs::read_to_string(root.path().join("target")).unwrap(),
        "next"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn stale_optimized_program_resumes_original_ir_after_a_source_disappears() {
    let root = workspace();
    fs::write(root.path().join("first"), "first").unwrap();
    fs::write(root.path().join("last"), "last").unwrap();
    fs::write(root.path().join("target"), "before").unwrap();
    let original = program(root.path(), "first -> target\nlast -> target\n");
    let prepared = msync::preparation::prepare(&original, true).unwrap();
    assert_eq!(prepared.program.body.instructions.len(), 1);
    prepared.verify().unwrap();
    fs::remove_file(root.path().join("first")).unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(root.path().join(".temp"))).unwrap();
    let error = runtime.execute_prepared(&prepared).await.unwrap_err();
    assert!(
        error.to_string().contains("source does not exist"),
        "{error}"
    );
    assert_eq!(
        fs::read_to_string(root.path().join("target")).unwrap(),
        "before"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn stale_missing_target_proof_cannot_replace_a_newly_created_directory() {
    let root = workspace();
    fs::write(root.path().join("first"), "first").unwrap();
    fs::write(root.path().join("last"), "last").unwrap();
    let original = program(root.path(), "first -> target\nlast -> target\n");
    let prepared = msync::preparation::prepare(&original, true).unwrap();
    assert_eq!(prepared.program.body.instructions.len(), 1);
    fs::create_dir(root.path().join("target")).unwrap();
    fs::write(root.path().join("target/keep"), "keep").unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(root.path().join(".temp"))).unwrap();
    let error = runtime.execute_prepared(&prepared).await.unwrap_err();
    assert!(error.to_string().contains("existing directory"), "{error}");
    assert_eq!(
        fs::read_to_string(root.path().join("target/keep")).unwrap(),
        "keep"
    );
    runtime.shutdown().await.unwrap();
}
