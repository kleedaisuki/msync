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
fn child_publication_preserves_read_root_and_unrelated_open_hardlinked_siblings() {
    use same_file::Handle;
    use std::io::Read;
    let workspace = workspace();
    let root = workspace.path().join("root");
    let external = workspace.path().join("external");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("a"), "old a").unwrap();
    fs::write(root.join("b"), "untouched b").unwrap();
    fs::hard_link(root.join("b"), &external).unwrap();
    let root_identity = Handle::from_path(&root).unwrap();
    let b_identity = Handle::from_path(&external).unwrap();
    let mut held = fs::File::open(root.join("b")).unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let new = payload(&manager, workspace.path(), "new-a", "new a");
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    manager.replace_snapshot(id, &root.join("a"), &new).unwrap();
    manager.finish(id).unwrap();
    assert_eq!(Handle::from_path(&root).unwrap(), root_identity);
    assert_eq!(Handle::from_path(root.join("b")).unwrap(), b_identity);
    let mut content = String::new();
    held.read_to_string(&mut content).unwrap();
    assert_eq!(content, "untouched b");
    manager.rollback_origin(id).unwrap();
    assert_eq!(fs::read_to_string(root.join("a")).unwrap(), "old a");
    assert_eq!(Handle::from_path(&root).unwrap(), root_identity);
    assert_eq!(Handle::from_path(root.join("b")).unwrap(), b_identity);
}

#[test]
fn child_creation_compensation_restores_parent_time_after_journal_cleanup() {
    use msync::transaction::filesystem::NodeMetadata;
    use std::time::{Duration, UNIX_EPOCH};
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir(&root).unwrap();
    let permissions = fs::metadata(&root).unwrap().permissions();
    NodeMetadata::new(permissions, UNIX_EPOCH + Duration::from_secs(978307200))
        .apply(&root)
        .unwrap();
    let before = fs::metadata(&root).unwrap().modified().unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let new = payload(&manager, workspace.path(), "new", "new");
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    manager
        .replace_snapshot(id, &root.join("created"), &new)
        .unwrap();
    let failure = manager
        .checkpoint_with(id, |_, event| {
            if event == PublishEvent::AfterInstall {
                return Err(io::Error::other("cancel after child install"));
            }
            Ok(())
        })
        .unwrap_err();
    assert!(matches!(failure, ManagerError::Publication { .. }));
    assert!(!root.join("created").exists());
    assert_eq!(fs::metadata(&root).unwrap().modified().unwrap(), before);
    assert!(manager.retained_journals().is_empty());
    let expected = manager.read_snapshot(id, &root).unwrap().entries()[Path::new("")]
        .metadata()
        .modified;
    manager.finish(id).unwrap();
    assert_eq!(fs::metadata(&root).unwrap().modified().unwrap(), expected);
}

#[test]
fn sealed_views_merge_disjoint_byte_changes_without_republishing_read_siblings() {
    use msync::transaction::filesystem::SnapshotStore;
    use same_file::Handle;
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("a"), "a0").unwrap();
    fs::write(root.join("b"), "b0").unwrap();
    fs::write(root.join("held"), "unchanged").unwrap();
    let held = fs::File::open(root.join("held")).unwrap();
    let held_identity = Handle::from_path(root.join("held")).unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    let before = manager.read_snapshot(id, &root).unwrap();
    let mut assets = SnapshotStore::new(&workspace.path().join("assets")).unwrap();
    let mut views = Vec::new();
    for (index, (name, bytes)) in [("a", "a1"), ("b", "b1")].into_iter().enumerate() {
        let path = workspace.path().join(format!("view-{index}"));
        let mut publication = assets.prepare(&before, &path).unwrap();
        publication.publish().unwrap();
        publication.finalize().unwrap();
        fs::write(path.join(name), bytes).unwrap();
        // Identical size and timestamp are not evidence of identical bytes.
        before.entries()[Path::new(name)]
            .metadata()
            .apply(&path.join(name))
            .unwrap();
        views.push(assets.capture(&path).unwrap());
    }
    manager
        .stage_view_delta(id, &root, &before, &views[0])
        .unwrap();
    manager
        .stage_view_delta(id, &root, &before, &views[1])
        .unwrap();
    manager.finish(id).unwrap();
    assert_eq!(fs::read_to_string(root.join("a")).unwrap(), "a1");
    assert_eq!(fs::read_to_string(root.join("b")).unwrap(), "b1");
    assert_eq!(Handle::from_path(root.join("held")).unwrap(), held_identity);
    drop(held);
    manager.rollback_origin(id).unwrap();
    assert_eq!(fs::read_to_string(root.join("a")).unwrap(), "a0");
    assert_eq!(fs::read_to_string(root.join("b")).unwrap(), "b0");
    drop(views);
    drop(before);
    manager.shutdown().unwrap();
    assets.cleanup().unwrap();
}

#[test]
fn overlapping_sealed_view_is_rejected_without_losing_the_first_staged_delta() {
    use msync::transaction::filesystem::SnapshotStore;
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("a"), "a0").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    let before = manager.read_snapshot(id, &root).unwrap();
    let mut assets = SnapshotStore::new(&workspace.path().join("assets")).unwrap();
    let path = workspace.path().join("view");
    let mut publication = assets.prepare(&before, &path).unwrap();
    publication.publish().unwrap();
    publication.finalize().unwrap();
    fs::write(path.join("a"), "a1").unwrap();
    let first = assets.capture(&path).unwrap();
    fs::write(path.join("a"), "a2").unwrap();
    let second = assets.capture(&path).unwrap();
    manager
        .stage_view_delta(id, &root, &before, &first)
        .unwrap();
    assert!(
        manager
            .stage_view_delta(id, &root, &before, &second)
            .is_err()
    );
    manager.finish(id).unwrap();
    assert_eq!(fs::read_to_string(root.join("a")).unwrap(), "a1");
    drop((first, second, before));
    manager.shutdown().unwrap();
    assets.cleanup().unwrap();
}

#[test]
fn metadata_only_view_changes_have_compensating_receipts_without_root_replacement() {
    use msync::transaction::filesystem::{NodeMetadata, SnapshotStore};
    use same_file::Handle;
    use std::time::{Duration, UNIX_EPOCH};
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("kept"), "unchanged").unwrap();
    let identity = Handle::from_path(&root).unwrap();
    let before_time = fs::metadata(&root).unwrap().modified().unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    let before = manager.read_snapshot(id, &root).unwrap();
    let mut assets = SnapshotStore::new(&workspace.path().join("assets")).unwrap();
    let view = workspace.path().join("view");
    let mut publication = assets.prepare(&before, &view).unwrap();
    publication.publish().unwrap();
    publication.finalize().unwrap();
    NodeMetadata::new(
        fs::metadata(&view).unwrap().permissions(),
        UNIX_EPOCH + Duration::from_secs(1293840000),
    )
    .apply(&view)
    .unwrap();
    let after = assets.capture(&view).unwrap();
    manager
        .stage_view_delta(id, &root, &before, &after)
        .unwrap();
    let epoch = manager.epoch();
    let failure = manager
        .checkpoint_with(id, |path, event| {
            assert_eq!(Handle::from_path(path)?, identity);
            assert_eq!(event, PublishEvent::AfterInstall);
            Err(io::Error::other("cancel after metadata install"))
        })
        .unwrap_err();
    assert!(matches!(failure, ManagerError::Publication { .. }));
    assert_eq!(manager.epoch(), epoch);
    assert_eq!(
        fs::metadata(&root).unwrap().modified().unwrap(),
        before_time
    );
    assert!(manager.retained_journals().is_empty());
    let report = manager.finish(id).unwrap();
    assert_eq!(report.published_roots, 0);
    assert_eq!(
        fs::metadata(&root).unwrap().modified().unwrap(),
        after.entries()[Path::new("")].metadata().modified
    );
    assert_eq!(Handle::from_path(&root).unwrap(), identity);
    manager.rollback_origin(id).unwrap();
    assert_eq!(
        fs::metadata(&root).unwrap().modified().unwrap(),
        before_time
    );
    drop((before, after));
    manager.shutdown().unwrap();
    assets.cleanup().unwrap();
}

#[test]
fn disjoint_domains_can_be_enrolled_between_finished_submissions_without_losing_named_undo() {
    let workspace = workspace();
    let first = workspace.path().join("first");
    let second = workspace.path().join("second");
    fs::write(&first, "first0").unwrap();
    fs::write(&second, "second0").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&first).unwrap();
    let new = payload(&manager, workspace.path(), "new", "new");
    manager
        .begin(TransactionId(0), Isolation::Serializable)
        .unwrap();
    manager
        .replace_snapshot(TransactionId(0), &first, &new)
        .unwrap();
    manager.finish(TransactionId(0)).unwrap();
    manager.enroll(&second).unwrap();
    manager
        .begin(TransactionId(1), Isolation::Serializable)
        .unwrap();
    manager
        .replace_snapshot(TransactionId(1), &second, &new)
        .unwrap();
    manager.finish(TransactionId(1)).unwrap();
    manager.rollback_origin(TransactionId(0)).unwrap();
    assert_eq!(fs::read_to_string(&first).unwrap(), "first0");
    assert_eq!(fs::read_to_string(&second).unwrap(), "new");
}

#[test]
fn an_enrolled_read_ancestor_can_contain_its_owned_invocation_without_being_renamed() {
    use msync::transaction::filesystem::InvocationRoot;
    use same_file::Handle;
    let workspace = workspace();
    let root = workspace.path().join("work");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("a"), "a0").unwrap();
    let invocation = InvocationRoot::create(&root.join(".temp")).unwrap();
    let root_identity = Handle::from_path(&root).unwrap();
    let mut manager = Manager::new_in(&invocation.path().join("manager"), &invocation).unwrap();
    manager.enroll(&root).unwrap();
    let new = payload(&manager, workspace.path(), "new", "a1");
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    let view = manager.read_snapshot(id, &root).unwrap();
    assert!(view.excludes_invocation(&invocation).unwrap());
    assert!(manager.read_snapshot(id, invocation.path()).is_err());
    drop(view);
    manager.replace_snapshot(id, &root.join("a"), &new).unwrap();
    manager.finish(id).unwrap();
    invocation.verify().unwrap();
    assert_eq!(Handle::from_path(&root).unwrap(), root_identity);
    manager.rollback_origin(id).unwrap();
    assert_eq!(fs::read_to_string(root.join("a")).unwrap(), "a0");
    drop(new);
    manager.shutdown().unwrap();
    invocation.cleanup().unwrap();
}

#[cfg(windows)]
#[test]
fn metadata_cleanup_cannot_overwrite_a_later_directory_version() {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Storage::FileSystem::{FILE_SHARE_READ, FILE_SHARE_WRITE};
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir(&root).unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let new = payload(&manager, workspace.path(), "new", "bytes");
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    manager.replace_snapshot(id, &root.join("a"), &new).unwrap();
    let store = manager.artifact_root().to_owned();
    let mut guard = None;
    let report = manager
        .checkpoint_with(id, |_, event| {
            if event == PublishEvent::AfterInstall {
                let journal = fs::read_dir(&store)?
                    .filter_map(Result::ok)
                    .find(|entry| entry.file_name().to_string_lossy().starts_with("metadata-"))
                    .expect("metadata journal prepared before data")
                    .path();
                guard = Some(
                    fs::OpenOptions::new()
                        .read(true)
                        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
                        .open(journal)?,
                );
            }
            Ok(())
        })
        .unwrap();
    assert!(report.cleanup_pending());
    let first_epoch = manager.epoch();
    manager.replace_snapshot(id, &root.join("b"), &new).unwrap();
    assert!(matches!(
        manager.checkpoint(id),
        Err(ManagerError::Cleanup { .. })
    ));
    assert_eq!(manager.epoch(), first_epoch);
    assert!(!root.join("b").exists());
    let expected = manager.read_snapshot(id, &root).unwrap().entries()[Path::new("")]
        .metadata()
        .modified;
    drop(guard);
    assert!(manager.retry_cleanup().is_empty());
    manager.finish(id).unwrap();
    assert_eq!(fs::metadata(&root).unwrap().modified().unwrap(), expected);
    assert!(root.join("b").exists());
    manager.rollback_origin(id).unwrap();
    assert!(!root.join("a").exists());
    assert!(!root.join("b").exists());
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
                    metadata: msync::transaction::filesystem::NodeMetadata::capture(
                        &fs::metadata(&root).unwrap(),
                    )
                    .unwrap(),
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

#[cfg(windows)]
#[test]
fn flagged_windows_case_sensitive_domains_are_refused_before_names_can_alias() {
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_ADD_FILE, FILE_ADD_SUBDIRECTORY, FILE_CASE_SENSITIVE_INFO, FILE_DELETE_CHILD,
        FILE_FLAG_BACKUP_SEMANTICS, FILE_READ_ATTRIBUTES, FILE_SHARE_DELETE, FILE_SHARE_READ,
        FILE_SHARE_WRITE, FILE_WRITE_ATTRIBUTES, FileCaseSensitiveInfo, SetFileInformationByHandle,
    };

    fn enable(path: &Path, account: &str) {
        // Grant only this newly created empty fixture directory. Setting the
        // flag also requires add-file/add-directory/delete-child handle rights.
        let grant = std::process::Command::new("icacls.exe")
            .arg(path)
            .arg("/grant")
            .arg(format!("{account}:(OI)(CI)F"))
            .output()
            .unwrap();
        assert!(
            grant.status.success(),
            "{}",
            String::from_utf8_lossy(&grant.stderr)
        );
        let directory = fs::OpenOptions::new()
            .access_mode(
                FILE_READ_ATTRIBUTES
                    | FILE_WRITE_ATTRIBUTES
                    | FILE_ADD_FILE
                    | FILE_ADD_SUBDIRECTORY
                    | FILE_DELETE_CHILD,
            )
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
            .open(path)
            .unwrap();
        let info = FILE_CASE_SENSITIVE_INFO { Flags: 1 };
        // SAFETY: The typed flag is applied only to a newly owned fixture directory.
        assert_ne!(
            unsafe {
                SetFileInformationByHandle(
                    directory.as_raw_handle(),
                    FileCaseSensitiveInfo,
                    (&info as *const FILE_CASE_SENSITIVE_INFO).cast(),
                    std::mem::size_of::<FILE_CASE_SENSITIVE_INFO>() as u32,
                )
            },
            0,
            "native case-sensitive fixture capability: {}",
            io::Error::last_os_error()
        );
    }

    let workspace = workspace();
    let account = std::process::Command::new("whoami.exe").output().unwrap();
    assert!(account.status.success());
    let account = String::from_utf8(account.stdout).unwrap();
    let account = account.trim();
    let sensitive = workspace.path().join("sensitive");
    let changed = workspace.path().join("changed-after-enrollment");
    fs::create_dir(&sensitive).unwrap();
    fs::create_dir(&changed).unwrap();
    enable(&sensitive, account);
    fs::write(sensitive.join("A"), "original-sensitive").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    let error = manager.enroll(&sensitive).unwrap_err();
    assert!(matches!(
        error,
        ManagerError::Io {
            action: "inspect native directory case policy",
            ..
        }
    ));
    assert_eq!(
        fs::read_to_string(sensitive.join("A")).unwrap(),
        "original-sensitive"
    );
    assert!(!sensitive.join("a").exists());

    let container = workspace.path().join("container");
    let child = container.join("sensitive-child");
    fs::create_dir_all(&child).unwrap();
    enable(&child, account);
    fs::write(child.join("A"), "preserve-nested-policy").unwrap();
    assert!(
        matches!(
            manager.enroll(&container),
            Err(ManagerError::Io {
                action: "inspect native directory case policy",
                ..
            })
        ),
        "an insensitive root must not hide a case-sensitive descendant"
    );
    assert!(
        matches!(
            manager.capture(&container),
            Err(ManagerError::Io {
                action: "inspect native directory case policy",
                ..
            })
        ),
        "external directory import must not discard descendant policy metadata"
    );
    assert_eq!(
        fs::read_to_string(child.join("A")).unwrap(),
        "preserve-nested-policy"
    );
    assert!(!child.join("a").exists());

    manager.enroll(&changed).unwrap();
    manager
        .begin(TransactionId(0), Isolation::Serializable)
        .unwrap();
    let file = payload(&manager, workspace.path(), "file", "must-not-publish");
    enable(&changed, account);
    fs::write(changed.join("A"), "preserve-after-policy-change").unwrap();
    let error = manager
        .replace_snapshot(TransactionId(0), &changed.join("a"), &file)
        .unwrap_err();
    assert!(matches!(
        error,
        ManagerError::Io {
            action: "inspect native directory case policy",
            ..
        }
    ));
    assert!(
        manager
            .read_snapshot(TransactionId(0), &changed.join("a"))
            .is_err()
    );
    assert_eq!(
        fs::read_to_string(changed.join("A")).unwrap(),
        "preserve-after-policy-change"
    );
    assert!(!changed.join("a").exists());
    drop(file);
    manager.finish(TransactionId(0)).unwrap();
    manager.shutdown().unwrap();
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
        .replace_snapshot(TransactionId(1), &root.join("folder/child"), &file)
        .unwrap();
    // Child creation writes a complete parent directory metadata version too.
    // Make the file replacement the newest tentative parent version.
    manager
        .replace_snapshot(TransactionId(0), &root.join("folder"), &file)
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

#[test]
fn sparse_namespace_preserves_unknown_siblings_and_named_origins() {
    let workspace = workspace();
    let root = workspace.path().join("namespace");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("unknown"), "not observed").unwrap();
    let identity = same_file::Handle::from_path(&root).unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll_namespace(&root).unwrap();
    manager.enroll_namespace(&root).unwrap();
    manager.enroll_entry(&root.join("out")).unwrap();
    let new = payload(&manager, workspace.path(), "payload", "first");
    let first = TransactionId(81);
    manager.begin(first, Isolation::Serializable).unwrap();
    assert!(matches!(
        manager.read_entry(first, &root).unwrap(),
        Some(Entry::Directory { .. })
    ));
    assert!(manager.read_entry(first, &root.join("unknown")).is_err());
    assert!(matches!(
        manager.enroll_entry(&root.join("unknown")),
        Err(ManagerError::EnrollmentClosed)
    ));
    assert!(manager.read_snapshot(first, &root).is_err());
    assert!(manager.read_snapshot(first, &root.join("unknown")).is_err());
    assert!(
        manager
            .replace_snapshot(first, &root.join("unknown"), &new)
            .is_err()
    );
    assert!(
        manager
            .read_snapshot(first, &root.join("out"))
            .unwrap()
            .is_missing()
    );
    manager
        .replace_snapshot(first, &root.join("out"), &new)
        .unwrap();
    manager.finish(first).unwrap();
    manager.enroll_entry(&root.join("out")).unwrap();
    manager.enroll_entry(&root.join("later")).unwrap();
    manager.rollback_origin(first).unwrap();
    let second = TransactionId(82);
    manager.begin(second, Isolation::Serializable).unwrap();
    manager
        .replace_snapshot(second, &root.join("later"), &new)
        .unwrap();
    manager.finish(second).unwrap();
    manager.rollback_origin(second).unwrap();
    assert!(!root.join("out").exists());
    assert!(!root.join("later").exists());
    assert_eq!(
        fs::read_to_string(root.join("unknown")).unwrap(),
        "not observed"
    );
    assert_eq!(same_file::Handle::from_path(&root).unwrap(), identity);
}

#[cfg(unix)]
#[test]
fn sparse_namespace_can_publish_beside_a_fifo_without_reading_it() {
    use std::ffi::CString;
    use std::os::unix::ffi::OsStrExt;
    use std::os::unix::fs::FileTypeExt;
    let workspace = workspace();
    let root = workspace.path().join("namespace");
    fs::create_dir(&root).unwrap();
    let fifo = root.join("pipe");
    let native = CString::new(fifo.as_os_str().as_bytes()).unwrap();
    assert_eq!(unsafe { libc::mkfifo(native.as_ptr(), 0o600) }, 0);
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    assert!(manager.enroll(&root).is_err());
    manager.enroll_namespace(&root).unwrap();
    assert!(manager.enroll_entry(&fifo).is_err());
    manager.enroll_entry(&root.join("out")).unwrap();
    manager.refresh_host().unwrap();
    let content = payload(&manager, workspace.path(), "payload", "stream output");
    let id = TransactionId(83);
    manager.begin(id, Isolation::Serializable).unwrap();
    assert!(manager.read_snapshot(id, &fifo).is_err());
    manager
        .replace_snapshot(id, &root.join("out"), &content)
        .unwrap();
    manager.finish(id).unwrap();
    assert_eq!(
        fs::read_to_string(root.join("out")).unwrap(),
        "stream output"
    );
    assert!(fs::symlink_metadata(&fifo).unwrap().file_type().is_fifo());
}

#[test]
fn ancestor_enrollment_preserves_loaded_context_versions_and_named_undo() {
    use msync::transaction::filesystem::SnapshotStore;
    let workspace = workspace();
    let root = workspace.path().join("cwd");
    let first = root.join("first");
    let second = root.join("second");
    fs::create_dir_all(&first).unwrap();
    fs::create_dir_all(&second).unwrap();
    fs::write(first.join("asset"), "original asset").unwrap();
    fs::write(second.join("asset"), "second asset").unwrap();
    let first_identity = same_file::Handle::from_path(&first).unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&first).unwrap();
    let updated = payload(&manager, workspace.path(), "updated", "saved version");
    let saved = TransactionId(91);
    manager.begin(saved, Isolation::Serializable).unwrap();
    let before = manager.read_snapshot(saved, &first).unwrap();
    manager
        .replace_snapshot(saved, &first.join("asset"), &updated)
        .unwrap();
    manager.finish(saved).unwrap();
    manager.enroll(&second).unwrap();
    // Extending coverage must not reinterpret existing resources as fresh host
    // captures, even if ambient bytes changed after their immutable enrollment.
    fs::write(first.join("asset"), "external drift").unwrap();
    #[cfg(windows)]
    let held = {
        use std::os::windows::fs::OpenOptionsExt;
        fs::OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(first.join("asset"))
            .unwrap()
    };
    manager.enroll(&root).unwrap();
    #[cfg(windows)]
    drop(held);
    let capture = TransactionId(92);
    manager.begin(capture, Isolation::Serializable).unwrap();
    assert_eq!(
        text(
            &manager
                .read_snapshot(capture, &first.join("asset"))
                .unwrap()
        )
        .as_deref(),
        Some("saved version")
    );
    let source = manager
        .read_snapshot(capture, &first.join("asset"))
        .unwrap();
    manager
        .replace_snapshot(capture, &root.join("captured.txt"), &source)
        .unwrap();
    manager.finish(capture).unwrap();
    assert_eq!(
        fs::read_to_string(root.join("captured.txt")).unwrap(),
        "saved version"
    );
    manager.rollback_origin(saved).unwrap();
    assert_eq!(
        fs::read_to_string(first.join("asset")).unwrap(),
        "original asset"
    );
    // A view sealed under the old child descriptor still imports under the new
    // ancestor descriptor using identical resource keys.
    let assets = SnapshotStore::new(&workspace.path().join("assets")).unwrap();
    let view = workspace.path().join("view");
    let mut publication = assets.prepare(&before, &view).unwrap();
    publication.publish().unwrap();
    publication.finalize().unwrap();
    fs::write(view.join("asset"), "program delta").unwrap();
    let after = assets.capture(&view).unwrap();
    let edit = TransactionId(93);
    manager.begin(edit, Isolation::Serializable).unwrap();
    manager
        .stage_view_delta(edit, &first, &before, &after)
        .unwrap();
    manager.finish(edit).unwrap();
    assert_eq!(
        fs::read_to_string(first.join("asset")).unwrap(),
        "program delta"
    );
    manager.rollback_origin(edit).unwrap();
    assert_eq!(
        fs::read_to_string(first.join("asset")).unwrap(),
        "original asset"
    );
    assert_eq!(
        same_file::Handle::from_path(&first).unwrap(),
        first_identity
    );
}

#[test]
fn sparse_ancestor_addition_retains_existing_child_origin_identity() {
    let workspace = workspace();
    let parent = workspace.path().join("parent");
    let child = parent.join("loaded");
    fs::create_dir_all(&child).unwrap();
    fs::write(child.join("asset"), "old").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&child).unwrap();
    let new = payload(&manager, workspace.path(), "new", "new");
    let original = TransactionId(94);
    manager.begin(original, Isolation::Serializable).unwrap();
    manager
        .replace_snapshot(original, &child.join("asset"), &new)
        .unwrap();
    manager.finish(original).unwrap();
    manager.enroll_namespace(&parent).unwrap();
    manager.enroll_entry(&parent.join("out")).unwrap();
    let later = TransactionId(95);
    manager.begin(later, Isolation::Serializable).unwrap();
    assert!(manager.read_snapshot(later, &parent).is_err());
    assert_eq!(
        text(&manager.read_snapshot(later, &child.join("asset")).unwrap()).as_deref(),
        Some("new")
    );
    manager
        .replace_snapshot(later, &parent.join("out"), &new)
        .unwrap();
    manager.finish(later).unwrap();
    manager.rollback_origin(original).unwrap();
    assert_eq!(fs::read_to_string(child.join("asset")).unwrap(), "old");
    assert_eq!(fs::read_to_string(parent.join("out")).unwrap(), "new");
}

#[test]
fn root_origin_undo_preserves_a_parent_enrolled_after_the_original_write() {
    let workspace = workspace();
    let parent = workspace.path().join("parent");
    let child = parent.join("child");
    let replacement = workspace.path().join("replacement");
    fs::create_dir_all(&child).unwrap();
    fs::create_dir(&replacement).unwrap();
    fs::write(child.join("old"), "old").unwrap();
    fs::write(replacement.join("new"), "new").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&child).unwrap();
    let snapshot = manager.capture(&replacement).unwrap();
    let id = TransactionId(96);
    manager.begin(id, Isolation::Serializable).unwrap();
    manager.replace_snapshot(id, &child, &snapshot).unwrap();
    manager.finish(id).unwrap();
    manager.enroll(&parent).unwrap();
    let before = fs::metadata(&parent).unwrap().modified().unwrap();
    manager.rollback_origin(id).unwrap();
    assert_eq!(fs::metadata(&parent).unwrap().modified().unwrap(), before);
    assert_eq!(fs::read_to_string(child.join("old")).unwrap(), "old");
    assert!(!child.join("new").exists());
}

#[test]
fn foreign_refresh_observes_same_stat_bytes_and_protects_named_origins() {
    use msync::transaction::filesystem::NodeMetadata;
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir(&root).unwrap();
    let path = root.join("source");
    fs::write(&path, "initial").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let managed = payload(&manager, workspace.path(), "payload", "managed");
    let saved = TransactionId(101);
    manager.begin(saved, Isolation::Serializable).unwrap();
    manager.replace_snapshot(saved, &path, &managed).unwrap();
    manager.finish(saved).unwrap();
    let immutable = managed.clone();
    let metadata = NodeMetadata::capture(&fs::metadata(&path).unwrap()).unwrap();
    fs::write(&path, "foreign").unwrap();
    metadata.apply(&path).unwrap();
    manager.refresh_host().unwrap();
    let reader = TransactionId(102);
    manager.begin(reader, Isolation::Serializable).unwrap();
    assert_eq!(
        text(&manager.read_snapshot(reader, &path).unwrap()).as_deref(),
        Some("foreign")
    );
    manager.finish(reader).unwrap();
    assert_eq!(text(&immutable).as_deref(), Some("managed"));
    assert!(matches!(
        manager.rollback_origin(saved),
        Err(ManagerError::Store(_))
    ));
    assert_eq!(fs::read_to_string(&path).unwrap(), "foreign");
}

#[test]
fn foreign_refresh_updates_membership_without_advancing_unchanged_versions() {
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("removed"), "old").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&root).unwrap();
    let epoch = manager.epoch();
    assert_eq!(manager.refresh_host().unwrap(), epoch);
    fs::remove_file(root.join("removed")).unwrap();
    fs::write(root.join("added"), "new").unwrap();
    assert!(manager.refresh_host().unwrap() > epoch);
    let id = TransactionId(103);
    manager.begin(id, Isolation::Serializable).unwrap();
    assert!(matches!(
        manager.refresh_host(),
        Err(ManagerError::ActiveTransactions(_))
    ));
    let snapshot = manager.read_snapshot(id, &root).unwrap();
    assert!(snapshot.entries().contains_key(Path::new("added")));
    assert!(!snapshot.entries().contains_key(Path::new("removed")));
    manager.finish(id).unwrap();
    let epoch = manager.epoch();
    assert_eq!(manager.refresh_host().unwrap(), epoch);
}

#[test]
fn failed_foreign_refresh_does_not_partially_import_other_roots() {
    let workspace = workspace();
    let first = workspace.path().join("first");
    let second = workspace.path().join("second");
    fs::write(&first, "before").unwrap();
    fs::create_dir(&second).unwrap();
    let leaf = second.join("leaf");
    fs::write(&leaf, "second").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&first).unwrap();
    manager.enroll(&second).unwrap();
    let epoch = manager.epoch();
    fs::write(&first, "after").unwrap();
    #[cfg(windows)]
    let fault = {
        use std::os::windows::fs::OpenOptionsExt;
        fs::OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(&leaf)
            .unwrap()
    };
    #[cfg(unix)]
    let fault = std::os::unix::net::UnixListener::bind(second.join("live")).unwrap();
    assert!(manager.refresh_host().is_err());
    assert_eq!(manager.epoch(), epoch);
    drop(fault);
    #[cfg(unix)]
    fs::remove_file(second.join("live")).unwrap();
    let id = TransactionId(104);
    manager.begin(id, Isolation::Serializable).unwrap();
    assert_eq!(
        text(&manager.read_snapshot(id, &first).unwrap()).as_deref(),
        Some("before")
    );
    manager.finish(id).unwrap();
    manager.refresh_host().unwrap();
    let id = TransactionId(105);
    manager.begin(id, Isolation::Serializable).unwrap();
    assert_eq!(
        text(&manager.read_snapshot(id, &first).unwrap()).as_deref(),
        Some("after")
    );
    manager.finish(id).unwrap();
}

#[test]
fn foreign_refresh_preserves_sparse_unknown_coverage() {
    let workspace = workspace();
    let root = workspace.path().join("root");
    fs::create_dir(&root).unwrap();
    fs::write(root.join("known"), "old").unwrap();
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll_namespace(&root).unwrap();
    manager.enroll_entry(&root.join("known")).unwrap();
    fs::write(root.join("known"), "new").unwrap();
    fs::write(root.join("unknown"), "not enrolled").unwrap();
    manager.refresh_host().unwrap();
    let id = TransactionId(106);
    manager.begin(id, Isolation::Serializable).unwrap();
    assert_eq!(
        text(&manager.read_snapshot(id, &root.join("known")).unwrap()).as_deref(),
        Some("new")
    );
    assert!(manager.read_entry(id, &root.join("unknown")).is_err());
    assert!(manager.read_snapshot(id, &root).is_err());
    assert!(matches!(
        manager.read_entry(id, &root).unwrap(),
        Some(Entry::Directory { .. })
    ));
    manager.finish(id).unwrap();
}
