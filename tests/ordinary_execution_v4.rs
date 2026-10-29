//! Real ordinary statements own short-lived journals, not retained transactions.

#[path = "support/statement_storage.rs"]
mod statement_storage;

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
        msync::filesystem::NodeMetadata::capture(&fs::metadata(path).unwrap()).unwrap();
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
async fn the_config_macro_copies_four_assignments_without_transaction_history() {
    let root = workspace();
    fs::create_dir_all(root.path().join("codex/agents/empty")).unwrap();
    fs::write(root.path().join("codex/config.toml"), "model = '可莉'\n").unwrap();
    fs::write(root.path().join("codex/agents/helper"), "agent").unwrap();
    let report = run(root.path(), "macro copy(codex, des):\n    codex/config.toml -> des/config.toml\n    codex/agents -> des/agents/.\ncopy($pwd / \"codex\", $pwd / \"local\")\ncopy($pwd / \"codex\", $pwd / \"other\")\n").await.unwrap();
    assert_eq!(report.assignments, 4);
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
    statement_storage::assert_clean(&root.path().join(".temp"));
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
    assert_eq!(
        fs::read(root.path().join("third")).unwrap(),
        "value 可莉".as_bytes()
    );
}

#[tokio::test]
async fn adjacent_file_writes_fold_with_live_guards_for_raw_and_prepared_interpretation() {
    for existing in [false, true] {
        for prepared in [false, true] {
            let root = workspace();
            fs::write(root.path().join("first"), b"first bytes").unwrap();
            fs::write(root.path().join("last"), b"last bytes").unwrap();
            if existing {
                fs::write(root.path().join("target"), b"old bytes").unwrap();
            }
            let context = CompileContext {
                pwd: root.path().to_owned(),
                source_file: None,
            };
            let compiled = pipeline::compile(
                &mut Session::new(),
                "first -> target\nlast -> target\n",
                &context,
                &[],
            )
            .unwrap();
            let mut runtime =
                RuntimeSession::new(&Options::new(root.path().join(".temp"))).unwrap();
            let report = if prepared {
                let prepared = msync::preparation::prepare(&compiled.program, true).unwrap();
                assert_eq!(prepared.program.body.instructions.len(), 1);
                runtime.execute_prepared(&prepared).await.unwrap()
            } else {
                runtime.execute(&compiled.program).await.unwrap()
            };
            assert_eq!(report.assignments, 1);
            assert_eq!(report.assigned_files, 1);
            assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
            assert_eq!(fs::read(root.path().join("target")).unwrap(), b"last bytes");
            assert!(fs::read_dir(root.path()).unwrap().all(|entry| {
                !entry
                    .unwrap()
                    .file_name()
                    .to_string_lossy()
                    .starts_with(".msync-publication")
            }));
            statement_storage::assert_clean_with_owner(
                &root.path().join(".temp"),
                runtime.private_root().unwrap(),
                runtime.private_stage_record().unwrap(),
            );
            runtime.shutdown().await.unwrap();
            statement_storage::assert_clean(&root.path().join(".temp"));
        }
    }
}

#[tokio::test]
async fn a_hardlink_source_keeps_its_old_bytes_instead_of_being_forwarded_from_the_target() {
    let root = workspace();
    fs::write(root.path().join("first"), b"new first bytes").unwrap();
    fs::write(root.path().join("target"), b"old linked bytes").unwrap();
    fs::hard_link(root.path().join("target"), root.path().join("alias")).unwrap();
    let report = run(root.path(), "first -> target\nalias -> target\n")
        .await
        .unwrap();
    assert_eq!(report.assignments, 2);
    assert_eq!(
        fs::read(root.path().join("target")).unwrap(),
        b"old linked bytes"
    );
    assert_eq!(
        fs::read(root.path().join("alias")).unwrap(),
        b"old linked bytes"
    );
}

#[tokio::test]
async fn a_maximal_overwrite_run_reaches_one_publication_not_pairwise_copies() {
    for count in [20, 32] {
        for existing in [false, true] {
            let root = workspace();
            let mut source = String::new();
            for index in 0..count {
                fs::write(
                    root.path().join(format!("source-{index}")),
                    format!("value {index}"),
                )
                .unwrap();
                source.push_str(&format!("source-{index} -> target\n"));
            }
            if existing {
                fs::write(root.path().join("target"), b"old").unwrap();
            }
            let report = run(root.path(), &source).await.unwrap();
            assert_eq!(report.assignments, 1);
            assert_eq!(report.assigned_files, 1);
            assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
            assert_eq!(
                fs::read_to_string(root.path().join("target")).unwrap(),
                format!("value {}", count - 1)
            );
            assert!(fs::read_dir(root.path()).unwrap().all(|entry| {
                !entry
                    .unwrap()
                    .file_name()
                    .to_string_lossy()
                    .starts_with(".msync-publication")
            }));
            statement_storage::assert_clean(&root.path().join(".temp"));
        }
    }
}

#[tokio::test]
async fn a_readonly_interior_value_preserves_the_original_partial_failure() {
    let root = workspace();
    fs::write(root.path().join("first"), b"first").unwrap();
    fs::write(root.path().join("readonly"), b"readonly middle").unwrap();
    fs::write(root.path().join("last"), b"last").unwrap();
    fs::write(root.path().join("target"), b"old").unwrap();
    let writable = fs::metadata(root.path().join("readonly"))
        .unwrap()
        .permissions();
    let mut readonly = writable.clone();
    readonly.set_readonly(true);
    fs::set_permissions(root.path().join("readonly"), readonly).unwrap();
    let result = run(
        root.path(),
        "first -> target\nreadonly -> target\nlast -> target\n",
    )
    .await;
    assert!(result.is_err());
    assert_eq!(
        fs::read(root.path().join("target")).unwrap(),
        b"readonly middle"
    );
    assert!(
        fs::metadata(root.path().join("target"))
            .unwrap()
            .permissions()
            .readonly()
    );
    fs::set_permissions(root.path().join("target"), writable.clone()).unwrap();
    fs::set_permissions(root.path().join("readonly"), writable).unwrap();
    statement_storage::assert_clean(&root.path().join(".temp"));
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
    assert_eq!(report.assignments, 1);
    assert_eq!(report.published_roots, 1);
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
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
async fn selector_copies_direct_matches_with_complete_subtrees_and_no_match_has_no_effect() {
    let root = workspace();
    fs::create_dir_all(root.path().join("source/picked/empty")).unwrap();
    fs::write(root.path().join("source/keep.txt"), b"selected").unwrap();
    fs::write(root.path().join("source/picked/nested"), b"tree").unwrap();
    fs::write(root.path().join("source/private"), b"must remain private").unwrap();
    let report = run(root.path(), "source/<keep\\.txt|picked> -> target/\n")
        .await
        .unwrap();
    assert_eq!(report.assigned_files, 2);
    assert_eq!(report.assigned_bytes, 12);
    assert_eq!(
        fs::read(root.path().join("target/keep.txt")).unwrap(),
        b"selected"
    );
    assert_eq!(
        fs::read(root.path().join("target/picked/nested")).unwrap(),
        b"tree"
    );
    assert!(root.path().join("target/picked/empty").is_dir());
    assert!(!root.path().join("target/private").exists());
    let report = run(root.path(), "source/<absent> -> not-created/\n")
        .await
        .unwrap();
    assert_eq!(report.assigned_files, 0);
    assert!(!root.path().join("not-created").exists());
    statement_storage::assert_clean(&root.path().join(".temp"));
}

#[cfg(unix)]
#[tokio::test]
async fn selector_does_not_acquire_unselected_special_or_unreadable_payloads() {
    use std::ffi::CString;
    use std::os::unix::ffi::OsStrExt;
    use std::os::unix::fs::{PermissionsExt, symlink};

    let root = workspace();
    let source = root.path().join("source");
    let private = source.join("private");
    fs::create_dir_all(&private).unwrap();
    fs::write(source.join("keep.txt"), b"chosen").unwrap();
    fs::write(private.join("secret"), b"must not be read").unwrap();
    symlink("missing", source.join("unselected-link")).unwrap();
    let fifo = CString::new(source.join("unselected-fifo").as_os_str().as_bytes()).unwrap();
    // SAFETY: fifo is a valid NUL-terminated fixture path.
    assert_eq!(unsafe { libc::mkfifo(fifo.as_ptr(), 0o600) }, 0);
    fs::set_permissions(&private, fs::Permissions::from_mode(0o000)).unwrap();
    let result = run(root.path(), "source/<keep\\.txt> -> target/\n").await;
    fs::set_permissions(&private, fs::Permissions::from_mode(0o700)).unwrap();
    let report = result.unwrap();
    assert_eq!(report.assigned_files, 1);
    assert_eq!(report.assigned_bytes, 6);
    assert_eq!(
        fs::read(root.path().join("target/keep.txt")).unwrap(),
        b"chosen"
    );
    assert_eq!(fs::read_dir(root.path().join("target")).unwrap().count(), 1);
    statement_storage::assert_clean(&root.path().join(".temp"));
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
    statement_storage::assert_clean(&root.path().join(".temp"));
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
    let sibling_metadata = fs::metadata(root.path().join("out/unrelated")).unwrap();
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
        fs::metadata(root.path().join("out/unrelated"))
            .unwrap()
            .modified()
            .unwrap(),
        sibling_metadata.modified().unwrap()
    );
    assert_eq!(
        fs::metadata(root.path().join("out/unrelated"))
            .unwrap()
            .permissions()
            .readonly(),
        sibling_metadata.permissions().readonly()
    );
    assert_eq!(
        fs::read(root.path().join("out/unrelated")).unwrap(),
        b"unrelated"
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
    assert_eq!(report.assignments, 1);
    assert_eq!(fs::read(root.path().join("output")).unwrap(), b"value");
    use std::os::unix::fs::FileTypeExt;
    assert!(fs::symlink_metadata(fifo).unwrap().file_type().is_fifo());
}

#[tokio::test]
async fn an_overwrite_run_folds_between_independent_prefix_and_suffix_operations() {
    let root = workspace();
    let mut source = String::from("\"prefix\" -> before\n");
    for index in 0..32 {
        let path = root.path().join(format!("source-{index}"));
        fs::write(&path, format!("owned value {index}")).unwrap();
        source.push_str(&format!("source-{index} -> target\n"));
    }
    source.push_str("\"suffix\" -> after\n");
    let last = root.path().join("source-31");
    set_modified(&last, UNIX_EPOCH + Duration::from_secs(1_234_567_890));
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(&last, fs::Permissions::from_mode(0o600)).unwrap();
    }
    let report = run(root.path(), &source).await.unwrap();
    assert_eq!(report.assignments, 3, "prefix, optimized run, suffix");
    assert_eq!(report.published_roots, 3);
    assert_eq!(fs::read(root.path().join("before")).unwrap(), b"prefix");
    assert_eq!(fs::read(root.path().join("after")).unwrap(), b"suffix");
    assert_eq!(
        fs::read(root.path().join("target")).unwrap(),
        b"owned value 31"
    );
    assert_eq!(
        fs::metadata(root.path().join("target"))
            .unwrap()
            .modified()
            .unwrap(),
        fs::metadata(&last).unwrap().modified().unwrap(),
    );
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        assert_eq!(
            fs::metadata(root.path().join("target"))
                .unwrap()
                .permissions()
                .mode()
                & 0o777,
            0o600
        );
    }
    statement_storage::assert_clean(&root.path().join(".temp"));
}

#[tokio::test]
async fn an_explicit_barrier_prevents_coalescing_writes_in_distinct_blocks() {
    let root = workspace();
    fs::write(root.path().join("first"), b"first").unwrap();
    fs::write(root.path().join("last"), b"last").unwrap();
    let report = run(root.path(), "await first -> target\nlast -> target\n")
        .await
        .unwrap();
    assert_eq!(report.assignments, 2);
    assert_eq!(report.published_roots, 2);
    assert_eq!(fs::read(root.path().join("target")).unwrap(), b"last");
    statement_storage::assert_clean(&root.path().join(".temp"));
}
