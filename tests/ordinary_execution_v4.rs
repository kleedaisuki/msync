//! Real ordinary statements own short-lived journals, not retained transactions.

use std::fs;
use std::path::Path;
use std::time::{Duration, UNIX_EPOCH};

use msync::compiler::CompileContext;
use msync::compiler::structured::Session;
use msync::execution::{ExecutionError, ExecutionReport, Options, RuntimeSession};
use msync::pipeline;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("ordinary-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn set_modified(path: &Path, time: std::time::SystemTime) {
    let mut metadata =
        msync::transaction::filesystem::NodeMetadata::capture(&fs::metadata(path).unwrap())
            .unwrap();
    metadata.modified = time;
    metadata.apply(path).unwrap();
}
async fn run(root: &Path, source: &str) -> Result<ExecutionReport, ExecutionError> {
    let context = CompileContext {
        pwd: root.to_owned(),
        source_file: None,
    };
    let compiled = pipeline::compile(&mut Session::new(), source, &context, &[]).unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(root.join(".temp"))).unwrap();
    runtime.set_origins(compiled.origins);
    let result = runtime.execute(&compiled.program).await;
    runtime.shutdown().await.unwrap();
    result
}

#[tokio::test]
async fn the_config_macro_copies_four_assignments_without_implicit_checkpoints() {
    let root = workspace();
    fs::create_dir_all(root.path().join("codex/agents/empty")).unwrap();
    fs::write(root.path().join("codex/config.toml"), "model = '可莉'\n").unwrap();
    fs::write(root.path().join("codex/agents/helper"), "agent").unwrap();
    let report = run(root.path(), "macro copy(codex, des):\n    codex/config.toml -> des/config.toml\n    codex/agents -> des/agents/.\ncopy($pwd / \"codex\", $pwd / \"local\")\ncopy($pwd / \"codex\", $pwd / \"other\")\n").await.unwrap();
    assert_eq!(report.assignments, 4);
    assert_eq!(report.checkpoints, 0);
    assert_eq!(report.rollbacks, 0);
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
    for destination in ["local", "other"] {
        assert_eq!(
            fs::read_to_string(root.path().join(destination).join("config.toml")).unwrap(),
            "model = '可莉'\n"
        );
        assert_eq!(
            fs::read(root.path().join(destination).join("agents/helper")).unwrap(),
            b"agent"
        );
        assert!(root.path().join(destination).join("agents/empty").is_dir());
    }
    assert_eq!(fs::read_dir(root.path().join(".temp")).unwrap().count(), 0);
}

#[tokio::test]
async fn an_ordinary_source_reads_the_previous_completed_assignment() {
    let root = workspace();
    let report = run(
        root.path(),
        "\"value 可莉\" -> first\nfirst -> second\nsecond -> third\n",
    )
    .await
    .unwrap();
    assert_eq!(report.assignments, 3);
    assert_eq!(report.checkpoints, 0);
    assert_eq!(
        fs::read(root.path().join("third")).unwrap(),
        "value 可莉".as_bytes()
    );
}

#[tokio::test]
async fn ordinary_failures_retain_the_semantic_macro_operand_not_the_whole_call() {
    let root = workspace();
    fs::create_dir(root.path().join("directory")).unwrap();
    for (arguments, destination) in [
        ("$pwd / \"missing\", $pwd / \"output\"", false),
        ("\"literal\", $pwd / \"directory\"", true),
    ] {
        let text = format!("macro copy(src, des):\n    src -> des\ncopy({arguments})\n");
        let context = CompileContext {
            pwd: root.path().to_owned(),
            source_file: None,
        };
        let compiled = pipeline::compile(&mut Session::new(), &text, &context, &[]).unwrap();
        let node = compiled.program.body.instructions.first().unwrap();
        let expected = compiled.origins[&node.id].for_operand(destination).site;
        let mut runtime = RuntimeSession::new(&Options::new(root.path().join(".temp"))).unwrap();
        runtime.set_origins(compiled.origins);
        let error = runtime.execute(&compiled.program).await.unwrap_err();
        assert_eq!(error.origin.unwrap().site, expected);
        runtime.shutdown().await.unwrap();
    }
}

#[tokio::test]
async fn tree_replacement_removes_old_members_and_retains_source_metadata() {
    let root = workspace();
    fs::create_dir_all(root.path().join("source/empty")).unwrap();
    fs::create_dir_all(root.path().join("target/stale")).unwrap();
    fs::write(root.path().join("source/new"), b"fresh").unwrap();
    fs::write(root.path().join("target/stale/old"), b"old").unwrap();
    let old = UNIX_EPOCH + Duration::from_secs(978307200);
    set_modified(&root.path().join("source"), old);
    let report = run(root.path(), "source -> target/.\n").await.unwrap();
    assert_eq!(report.checkpoints, 0);
    assert!(!root.path().join("target/stale").exists());
    assert_eq!(fs::read(root.path().join("target/new")).unwrap(), b"fresh");
    assert!(root.path().join("target/empty").is_dir());
    assert_eq!(
        fs::metadata(root.path().join("target"))
            .unwrap()
            .modified()
            .unwrap(),
        old
    );
}

#[tokio::test]
async fn selector_collision_preflights_every_member_before_any_publication() {
    let root = workspace();
    fs::create_dir_all(root.path().join("source")).unwrap();
    fs::create_dir_all(root.path().join("target")).unwrap();
    fs::write(root.path().join("source/a"), b"a").unwrap();
    fs::write(root.path().join("source/b"), b"b").unwrap();
    fs::write(root.path().join("target/b"), b"keep").unwrap();
    assert!(
        run(root.path(), "source/<[ab]> -> target/\n")
            .await
            .is_err()
    );
    assert!(!root.path().join("target/a").exists());
    assert_eq!(fs::read(root.path().join("target/b")).unwrap(), b"keep");
    assert_eq!(fs::read_dir(root.path().join(".temp")).unwrap().count(), 0);
}

#[tokio::test]
async fn container_merge_keeps_unmentioned_members_and_rejects_colliding_files() {
    let root = workspace();
    fs::create_dir_all(root.path().join("source/nested")).unwrap();
    fs::create_dir_all(root.path().join("container/source/nested")).unwrap();
    fs::write(root.path().join("source/nested/new"), b"new").unwrap();
    fs::write(root.path().join("container/source/keep"), b"keep").unwrap();
    run(root.path(), "source -> container/\n").await.unwrap();
    assert_eq!(
        fs::read(root.path().join("container/source/keep")).unwrap(),
        b"keep"
    );
    assert_eq!(
        fs::read(root.path().join("container/source/nested/new")).unwrap(),
        b"new"
    );
    assert!(run(root.path(), "source -> container/\n").await.is_err());
}

#[tokio::test]
async fn a_later_failure_does_not_rewind_a_finished_ordinary_statement() {
    let root = workspace();
    fs::create_dir(root.path().join("directory")).unwrap();
    assert!(
        run(root.path(), "\"first\" -> kept\n\"bad\" -> directory\n")
            .await
            .is_err()
    );
    assert_eq!(fs::read(root.path().join("kept")).unwrap(), b"first");
    assert!(root.path().join("directory").is_dir());
}

#[tokio::test]
async fn hardlink_identity_is_rejected_without_changing_either_alias() {
    let root = workspace();
    fs::write(root.path().join("source"), b"kept").unwrap();
    fs::hard_link(root.path().join("source"), root.path().join("alias")).unwrap();
    assert!(run(root.path(), "source -> alias\n").await.is_err());
    assert_eq!(fs::read(root.path().join("source")).unwrap(), b"kept");
    assert_eq!(fs::read(root.path().join("alias")).unwrap(), b"kept");
}

#[tokio::test]
async fn ordinary_overwrite_does_not_republish_or_touch_an_unrelated_sibling() {
    let root = workspace();
    fs::create_dir(root.path().join("out")).unwrap();
    fs::write(root.path().join("out/target"), b"old").unwrap();
    fs::write(root.path().join("out/unrelated"), b"unrelated").unwrap();
    let retained = same_file::Handle::from_path(root.path().join("out/unrelated")).unwrap();
    let stamp = UNIX_EPOCH + Duration::from_secs(978307200);
    set_modified(&root.path().join("out"), stamp);
    run(root.path(), "\"updated\" -> out/target\n")
        .await
        .unwrap();
    assert_eq!(
        same_file::Handle::from_path(root.path().join("out/unrelated")).unwrap(),
        retained
    );
    assert_eq!(
        fs::metadata(root.path().join("out"))
            .unwrap()
            .modified()
            .unwrap(),
        stamp
    );
}

#[cfg(unix)]
#[tokio::test]
async fn an_unrelated_fifo_is_not_enrolled_or_snapshotted() {
    use std::ffi::CString;
    use std::os::unix::ffi::OsStrExt;
    let root = workspace();
    let fifo = root.path().join("unrelated");
    let name = CString::new(fifo.as_os_str().as_bytes()).unwrap();
    assert_eq!(unsafe { libc::mkfifo(name.as_ptr(), 0o600) }, 0);
    let report = run(root.path(), "\"value\" -> output\n").await.unwrap();
    assert_eq!(report.checkpoints, 0);
    assert_eq!(fs::read(root.path().join("output")).unwrap(), b"value");
    use std::os::unix::fs::FileTypeExt;
    assert!(fs::symlink_metadata(fifo).unwrap().file_type().is_fifo());
}
