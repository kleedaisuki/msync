use std::fs;
use std::io;
use std::path::Path;

use msync::hir::TransactionId;
use msync::language::Isolation;
use msync::transaction::filesystem::{Entry, PublishEvent, Snapshot};
use msync::transaction::manager::{Lifecycle, Manager, ManagerError};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("transaction-manager-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn text(snapshot: &Snapshot) -> Option<String> {
    match snapshot.entries().get(Path::new("")) {
        Some(Entry::File { artifact, .. }) => Some(fs::read_to_string(artifact.path()).unwrap()),
        None => None,
        _ => panic!("not a file snapshot"),
    }
}

fn payload(manager: &Manager, workspace: &Path, name: &str, bytes: &str) -> Snapshot {
    let path = workspace.join(name);
    fs::write(&path, bytes).unwrap();
    manager.capture(&path).unwrap()
}

#[test]
fn actual_file_checkpoints_pending_rewind_and_named_origin_are_distinct() {
    let workspace = workspace();
    let target = workspace.path().join("target");
    let created = workspace.path().join("created");
    fs::write(&target, "origin").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&target).unwrap();
    manager.enroll(&created).unwrap();
    let first = payload(&manager, workspace.path(), "first", "你好🌟\ncheckpoint");
    let second = payload(&manager, workspace.path(), "second", "pending");
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    manager.replace_snapshot(id, &target, &first).unwrap();
    assert_eq!(fs::read_to_string(&target).unwrap(), "origin");
    assert_eq!(
        text(&manager.read_snapshot(id, &target).unwrap()).as_deref(),
        Some("你好🌟\ncheckpoint")
    );
    let report = manager.checkpoint(id).unwrap();
    assert_eq!(report.published_roots, 1);
    assert!(!report.cleanup_pending());
    assert_eq!(fs::read_to_string(&target).unwrap(), "你好🌟\ncheckpoint");
    manager.replace_snapshot(id, &target, &second).unwrap();
    manager.replace_snapshot(id, &created, &second).unwrap();
    manager.rollback_pending(id).unwrap();
    assert_eq!(
        text(&manager.read_snapshot(id, &target).unwrap()).as_deref(),
        Some("你好🌟\ncheckpoint")
    );
    assert!(manager.read_snapshot(id, &created).unwrap().is_missing());
    manager.replace_snapshot(id, &created, &first).unwrap();
    manager.finish(id).unwrap();
    assert!(created.exists());
    manager.rollback_origin(id).unwrap();
    assert_eq!(fs::read_to_string(&target).unwrap(), "origin");
    assert!(!created.exists());
    assert!(manager.retained_journals().is_empty());
}

#[test]
fn tree_replacement_and_subtree_writes_preserve_unrelated_commits_on_origin_rollback() {
    let workspace = workspace();
    let root = workspace.path().join("root");
    let replacement = workspace.path().join("replacement");
    fs::create_dir_all(root.join("empty")).unwrap();
    fs::write(root.join("a"), "a0").unwrap();
    fs::write(root.join("b"), "b0").unwrap();
    fs::create_dir_all(replacement.join("new-empty")).unwrap();
    fs::write(replacement.join("new"), "new").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let a = payload(&manager, workspace.path(), "a1", "a1");
    let b = payload(&manager, workspace.path(), "b1", "b1");
    manager
        .begin(TransactionId(0), Isolation::ReadCommitted)
        .unwrap();
    manager
        .begin(TransactionId(1), Isolation::ReadCommitted)
        .unwrap();
    manager
        .replace_snapshot(TransactionId(0), &root.join("a"), &a)
        .unwrap();
    manager.finish(TransactionId(0)).unwrap();
    manager
        .replace_snapshot(TransactionId(1), &root.join("b"), &b)
        .unwrap();
    manager.finish(TransactionId(1)).unwrap();
    manager.rollback_origin(TransactionId(0)).unwrap();
    assert_eq!(fs::read_to_string(root.join("a")).unwrap(), "a0");
    assert_eq!(fs::read_to_string(root.join("b")).unwrap(), "b1");
    assert!(root.join("empty").is_dir());
    manager
        .begin(TransactionId(2), Isolation::Serializable)
        .unwrap();
    let tree = manager.capture(&replacement).unwrap();
    manager
        .replace_snapshot(TransactionId(2), &root, &tree)
        .unwrap();
    manager.finish(TransactionId(2)).unwrap();
    assert!(!root.join("a").exists());
    assert!(root.join("new-empty").is_dir());
    manager.rollback_origin(TransactionId(2)).unwrap();
    assert_eq!(fs::read_to_string(root.join("b")).unwrap(), "b1");
    assert!(root.join("empty").is_dir());
}

#[test]
fn visibility_reads_are_immutable_even_while_other_transactions_stage_and_publish() {
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::write(&root, "old").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let new = payload(&manager, workspace.path(), "new", "new");
    for (number, isolation) in [
        Isolation::ReadCommitted,
        Isolation::ReadUncommitted,
        Isolation::ReadCommitted,
        Isolation::RepeatableRead,
    ]
    .into_iter()
    .enumerate()
    {
        manager.begin(TransactionId(number), isolation).unwrap();
    }
    manager
        .replace_snapshot(TransactionId(0), &root, &new)
        .unwrap();
    assert_eq!(
        text(&manager.read_snapshot(TransactionId(1), &root).unwrap()).as_deref(),
        Some("new")
    );
    assert_eq!(
        text(&manager.read_snapshot(TransactionId(2), &root).unwrap()).as_deref(),
        Some("old")
    );
    let retained = manager.read_snapshot(TransactionId(3), &root).unwrap();
    manager.finish(TransactionId(0)).unwrap();
    assert_eq!(text(&retained).as_deref(), Some("old"));
    manager.start_statement(TransactionId(2)).unwrap();
    assert_eq!(
        text(&manager.read_snapshot(TransactionId(2), &root).unwrap()).as_deref(),
        Some("new")
    );
    assert_eq!(
        text(&manager.read_snapshot(TransactionId(3), &root).unwrap()).as_deref(),
        Some("old")
    );
}

#[test]
fn a_failed_second_root_restores_every_before_image_and_keeps_pending_retryable() {
    let workspace = workspace();
    let left = workspace.path().join("left");
    let right = workspace.path().join("right");
    fs::write(&left, "left0").unwrap();
    fs::write(&right, "right0").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&left).unwrap();
    manager.enroll(&right).unwrap();
    let new = payload(&manager, workspace.path(), "new", "both1");
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    manager.replace_snapshot(id, &left, &new).unwrap();
    manager.replace_snapshot(id, &right, &new).unwrap();
    let epoch = manager.epoch();
    let mut exports = 0;
    let failure = manager
        .checkpoint_with(id, |_, event| {
            if event == PublishEvent::AfterInstall {
                exports += 1;
            }
            if exports == 2 {
                return Err(io::Error::other("injected second-root cancellation"));
            }
            Ok(())
        })
        .unwrap_err();
    assert!(matches!(failure, ManagerError::Publication { .. }));
    assert_eq!(manager.epoch(), epoch);
    assert_eq!(fs::read_to_string(&left).unwrap(), "left0");
    assert_eq!(fs::read_to_string(&right).unwrap(), "right0");
    assert_eq!(
        text(&manager.read_snapshot(id, &left).unwrap()).as_deref(),
        Some("both1")
    );
    assert!(manager.recovery_journals().is_empty());
    manager.checkpoint(id).unwrap();
    assert_eq!(fs::read_to_string(&left).unwrap(), "both1");
    assert_eq!(fs::read_to_string(&right).unwrap(), "both1");
}

#[test]
fn failed_compensation_blocks_publication_but_keeps_immutable_reads_and_recovery_receipts() {
    let workspace = workspace();
    let left = workspace.path().join("left");
    let right = workspace.path().join("right");
    let held = workspace.path().join("held-installed-left");
    fs::write(&left, "left0").unwrap();
    fs::write(&right, "right0").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&left).unwrap();
    manager.enroll(&right).unwrap();
    let new = payload(&manager, workspace.path(), "new", "both1");
    let writer = TransactionId(0);
    let reader = TransactionId(1);
    manager.begin(writer, Isolation::Serializable).unwrap();
    manager.begin(reader, Isolation::ReadCommitted).unwrap();
    manager.replace_snapshot(writer, &left, &new).unwrap();
    manager.replace_snapshot(writer, &right, &new).unwrap();
    let epoch = manager.epoch();
    let mut exports = 0;
    let failure = manager
        .checkpoint_with(writer, |_, event| {
            if event != PublishEvent::AfterInstall {
                return Ok(());
            }
            exports += 1;
            if exports == 2 {
                fs::rename(&left, &held)?;
                fs::write(&left, "foreign owner")?;
                return Err(io::Error::other("injected replacement during compensation"));
            }
            Ok(())
        })
        .unwrap_err();
    assert!(matches!(failure, ManagerError::Publication { .. }));
    assert_eq!(manager.epoch(), epoch);
    assert!(!manager.recovery_journals().is_empty());
    assert!(matches!(
        manager.shutdown(),
        Err(ManagerError::RecoveryRequired { .. })
    ));
    assert_eq!(manager.lifecycle(), Lifecycle::Open);
    assert!(matches!(
        manager.checkpoint(writer),
        Err(ManagerError::RecoveryRequired { .. })
    ));
    assert_eq!(
        text(&manager.read_snapshot(reader, &left).unwrap()).as_deref(),
        Some("left0")
    );
    assert_eq!(fs::read_to_string(&left).unwrap(), "foreign owner");
    assert!(
        !manager.recover().is_empty(),
        "foreign identity must never be overwritten"
    );
    fs::remove_file(&left).unwrap();
    fs::rename(&held, &left).unwrap();
    assert!(manager.recover().is_empty());
    assert!(manager.recovery_journals().is_empty());
    assert_eq!(fs::read_to_string(&left).unwrap(), "left0");
    assert_eq!(fs::read_to_string(&right).unwrap(), "right0");
    manager.checkpoint(writer).unwrap();
    assert_eq!(fs::read_to_string(&left).unwrap(), "both1");
}

#[test]
fn serializable_validation_precedes_any_physical_publication() {
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("left"), "left0").unwrap();
    fs::write(root.join("right"), "right0").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let new = payload(&manager, workspace.path(), "new", "new");
    for number in [0, 1] {
        let id = TransactionId(number);
        manager.begin(id, Isolation::Serializable).unwrap();
        manager.read_snapshot(id, &root).unwrap();
    }
    manager
        .replace_snapshot(TransactionId(0), &root.join("left"), &new)
        .unwrap();
    manager
        .replace_snapshot(TransactionId(1), &root.join("right"), &new)
        .unwrap();
    manager.finish(TransactionId(0)).unwrap();
    let epoch = manager.epoch();
    assert!(matches!(
        manager.finish(TransactionId(1)),
        Err(ManagerError::Store(_))
    ));
    assert_eq!(manager.epoch(), epoch);
    assert_eq!(fs::read_to_string(root.join("left")).unwrap(), "new");
    assert_eq!(fs::read_to_string(root.join("right")).unwrap(), "right0");
    assert!(manager.retained_journals().is_empty());
}

#[cfg(windows)]
#[test]
fn committed_cleanup_failure_is_reported_without_claiming_the_write_was_rolled_back() {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Storage::FileSystem::{FILE_SHARE_READ, FILE_SHARE_WRITE};
    let workspace = workspace();
    let target = workspace.path().join("target");
    fs::write(&target, "original").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&target).unwrap();
    let new = payload(&manager, workspace.path(), "new", "committed");
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    manager.replace_snapshot(id, &target, &new).unwrap();
    let mut guard = None;
    let report = manager
        .checkpoint_with(id, |path, event| {
            if event != PublishEvent::AfterInstall {
                return Ok(());
            }
            let journal = fs::read_dir(path.parent().unwrap())?
                .find_map(|entry| {
                    let entry = entry.ok()?;
                    entry
                        .file_name()
                        .to_str()?
                        .starts_with(".msync-transaction-")
                        .then(|| entry.path())
                })
                .unwrap();
            guard = Some(
                fs::OpenOptions::new()
                    .read(true)
                    .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
                    .open(journal.join("old"))?,
            );
            Ok(())
        })
        .unwrap();
    assert!(report.cleanup_pending());
    assert_eq!(report.epoch, manager.epoch());
    assert_eq!(fs::read_to_string(&target).unwrap(), "committed");
    assert_eq!(
        text(&manager.read_snapshot(id, &target).unwrap()).as_deref(),
        Some("committed")
    );
    assert!(!manager.retained_journals().is_empty());
    let finish = manager.finish(id).unwrap();
    assert!(finish.cleanup_pending());
    assert!(matches!(
        manager.shutdown(),
        Err(ManagerError::Cleanup { .. })
    ));
    assert_eq!(manager.lifecycle(), Lifecycle::Open);
    drop(guard);
    assert!(manager.retry_cleanup().is_empty());
    assert!(manager.retained_journals().is_empty());
    manager.rollback_origin(id).unwrap();
    assert_eq!(fs::read_to_string(&target).unwrap(), "original");
}

#[test]
fn enrollment_and_unmanaged_paths_fail_before_any_host_change() {
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir(&root).unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    assert!(manager.enroll(&root.join("child")).is_err());
    assert!(
        manager
            .enroll(&workspace.path().join("missing-parent/root"))
            .is_err()
    );
    assert!(
        manager.capture(&root).is_err(),
        "a live host capture must not bypass the enrolled immutable view"
    );
    manager
        .begin(TransactionId(0), Isolation::Serializable)
        .unwrap();
    assert!(matches!(
        manager.enroll(&workspace.path().join("other")),
        Err(ManagerError::EnrollmentClosed)
    ));
    assert!(
        manager
            .read_snapshot(TransactionId(0), &workspace.path().join("other"))
            .is_err()
    );
    let new = payload(&manager, workspace.path(), "new", "data");
    assert!(
        manager
            .replace_snapshot(TransactionId(0), &root.join("missing/child"), &new)
            .is_err()
    );
    assert_eq!(fs::read_dir(&root).unwrap().count(), 0);
}

#[test]
fn explicit_shutdown_refuses_active_work_and_releases_owned_artifacts_after_finish() {
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::write(&root, "data").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    manager
        .begin(TransactionId(0), Isolation::Serializable)
        .unwrap();
    manager.checkpoint(TransactionId(0)).unwrap();
    assert!(matches!(
        manager.shutdown(),
        Err(ManagerError::ActiveTransactions(_))
    ));
    assert_eq!(manager.lifecycle(), Lifecycle::Open);
    manager.finish(TransactionId(0)).unwrap();
    let epoch = manager.epoch();
    let artifacts = manager.artifact_root().to_owned();
    assert!(artifacts.exists());
    manager.shutdown().unwrap();
    assert_eq!(manager.lifecycle(), Lifecycle::Closed { epoch });
    assert!(!artifacts.exists());
    assert_eq!(fs::read_to_string(&root).unwrap(), "data");
    assert!(matches!(
        manager.begin(TransactionId(1), Isolation::Serializable),
        Err(ManagerError::Lifecycle(_))
    ));
    manager.shutdown().unwrap();
}

#[test]
fn shutdown_with_external_snapshot_is_closing_and_can_be_retried_without_reopening() {
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::write(&root, "data").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    manager
        .begin(TransactionId(0), Isolation::ReadCommitted)
        .unwrap();
    let retained = manager.read_snapshot(TransactionId(0), &root).unwrap();
    manager.finish(TransactionId(0)).unwrap();
    let epoch = manager.epoch();
    assert!(matches!(
        manager.shutdown(),
        Err(ManagerError::Filesystem(_))
    ));
    assert_eq!(manager.lifecycle(), Lifecycle::Closing { epoch });
    assert_eq!(text(&retained).as_deref(), Some("data"));
    assert!(matches!(
        manager.read_snapshot(TransactionId(0), &root),
        Err(ManagerError::Lifecycle(_))
    ));
    assert!(matches!(
        manager.begin(TransactionId(1), Isolation::ReadCommitted),
        Err(ManagerError::Lifecycle(_))
    ));
    assert!(matches!(
        manager.enroll(&workspace.path().join("other")),
        Err(ManagerError::Lifecycle(_))
    ));
    drop(retained);
    manager.shutdown().unwrap();
    assert_eq!(manager.lifecycle(), Lifecycle::Closed { epoch });
    assert!(!manager.artifact_root().exists());
}

#[cfg(unix)]
#[test]
fn enrollment_rejects_symlink_leaves_even_with_directory_suffix_spelling() {
    use std::os::unix::fs::symlink;
    let workspace = workspace();
    let root = workspace.path().join("root");
    let link = workspace.path().join("link");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("keep"), "original").unwrap();
    symlink(&root, &link).unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    assert!(manager.enroll(&link.join(".")).is_err());
    assert!(manager.enroll(&link.join("../root")).is_err());
    manager.enroll(&root).unwrap();
    assert_eq!(fs::read_to_string(root.join("keep")).unwrap(), "original");
}

#[cfg(windows)]
#[test]
fn case_colliding_manifest_is_rejected_before_staging_or_publishing_any_entries() {
    use msync::transaction::filesystem::SnapshotStore;
    use std::collections::BTreeMap;
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("keep"), "old").unwrap();
    let mut manager = Manager::new(&workspace.path().join("manager-artifacts")).unwrap();
    manager.enroll(&root).unwrap();
    manager
        .begin(TransactionId(0), Isolation::Serializable)
        .unwrap();
    let external = SnapshotStore::new(&workspace.path().join("external-artifacts")).unwrap();
    let input = workspace.path().join("input");
    fs::write(&input, "data").unwrap();
    let file = external
        .capture(&input)
        .unwrap()
        .entries()
        .get(Path::new(""))
        .unwrap()
        .clone();
    let tree = external
        .from_entries(BTreeMap::from([
            (
                std::path::PathBuf::new(),
                Entry::Directory {
                    permissions: fs::metadata(&root).unwrap().permissions(),
                },
            ),
            ("A".into(), file.clone()),
            ("a".into(), file),
        ]))
        .unwrap();
    assert!(matches!(
        manager.replace_snapshot(TransactionId(0), &root, &tree),
        Err(ManagerError::InvalidPath { .. })
    ));
    manager.finish(TransactionId(0)).unwrap();
    assert_eq!(fs::read_to_string(root.join("keep")).unwrap(), "old");
    assert_eq!(fs::read_dir(&root).unwrap().count(), 1);
}

#[cfg(windows)]
#[test]
fn windows_case_aliases_share_identity_without_lowercasing_visible_names() {
    let workspace = workspace();
    let root = workspace.path().join("Root");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("OriginalName"), "old").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    manager
        .begin(TransactionId(0), Isolation::Serializable)
        .unwrap();
    let new = payload(&manager, workspace.path(), "new", "new");
    manager
        .replace_snapshot(TransactionId(0), &root.join("originalname"), &new)
        .unwrap();
    manager.finish(TransactionId(0)).unwrap();
    let names = fs::read_dir(&root)
        .unwrap()
        .map(|entry| entry.unwrap().file_name())
        .collect::<Vec<_>>();
    assert_eq!(names, [std::ffi::OsString::from("OriginalName")]);
    manager.rollback_origin(TransactionId(0)).unwrap();
    assert_eq!(
        fs::read_to_string(root.join("OriginalName")).unwrap(),
        "old"
    );
}

#[cfg(windows)]
#[test]
fn a_pending_parent_case_alias_keeps_the_visible_parent_spelling() {
    let workspace = workspace();
    let root = workspace.path().join("root");
    let tree = workspace.path().join("tree");
    fs::create_dir(&root).unwrap();
    fs::create_dir_all(tree.join("NewParent")).unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let snapshot = manager.capture(&tree).unwrap();
    let file = payload(&manager, workspace.path(), "file", "child");
    manager
        .begin(TransactionId(0), Isolation::Serializable)
        .unwrap();
    manager
        .replace_snapshot(TransactionId(0), &root, &snapshot)
        .unwrap();
    manager
        .replace_snapshot(TransactionId(0), &root.join("newparent/Child"), &file)
        .unwrap();
    let view = manager.read_snapshot(TransactionId(0), &root).unwrap();
    assert!(view.entries().contains_key(Path::new("NewParent/Child")));
    manager.finish(TransactionId(0)).unwrap();
    assert_eq!(
        fs::read_to_string(root.join("NewParent/Child")).unwrap(),
        "child"
    );
    assert_eq!(
        fs::read_dir(&root)
            .unwrap()
            .next()
            .unwrap()
            .unwrap()
            .file_name(),
        "NewParent"
    );
}

#[test]
fn dirty_tree_reads_do_not_expose_a_child_beneath_a_file_parent() {
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir_all(root.join("folder")).unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let file = payload(&manager, workspace.path(), "file", "data");
    manager
        .begin(TransactionId(0), Isolation::ReadCommitted)
        .unwrap();
    manager
        .begin(TransactionId(1), Isolation::ReadCommitted)
        .unwrap();
    manager
        .begin(TransactionId(2), Isolation::ReadUncommitted)
        .unwrap();
    manager
        .replace_snapshot(TransactionId(0), &root.join("folder"), &file)
        .unwrap();
    manager
        .replace_snapshot(TransactionId(1), &root.join("folder/child"), &file)
        .unwrap();
    let view = manager.read_snapshot(TransactionId(2), &root).unwrap();
    assert!(matches!(
        view.entries().get(Path::new("folder")),
        Some(Entry::File { .. })
    ));
    assert!(!view.entries().contains_key(Path::new("folder/child")));
    assert!(
        manager
            .read_snapshot(TransactionId(2), &root.join("folder/child"))
            .is_err()
    );
    manager.rollback_pending(TransactionId(0)).unwrap();
    assert_eq!(
        text(
            &manager
                .read_snapshot(TransactionId(2), &root.join("folder/child"))
                .unwrap()
        )
        .as_deref(),
        Some("data")
    );
}
