use std::collections::BTreeMap;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use msync::transaction::filesystem::{Entry, PublishEvent, Recovery, SnapshotStore};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("transaction-filesystem-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn store(root: &Path) -> SnapshotStore {
    SnapshotStore::new(&root.join("artifacts")).unwrap()
}

fn read(path: &Path) -> String {
    fs::read_to_string(path).unwrap()
}

#[test]
fn snapshots_are_disk_backed_immutable_and_retained_by_entry_values() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    fs::write(&source, "original UTF-8 文本").unwrap();
    let mut store = store(workspace.path());
    let snapshot = store.capture(&source).unwrap();
    let entry = snapshot.entries().get(Path::new("")).unwrap().clone();
    let Entry::File { artifact, .. } = &entry else {
        panic!()
    };
    fs::write(&source, "changed").unwrap();
    assert_eq!(read(artifact.path()), "original UTF-8 文本");
    assert_eq!(artifact.len(), "original UTF-8 文本".len() as u64);
    assert!(!artifact.is_empty());
    drop(snapshot);
    assert_eq!(
        store.cleanup().unwrap_err().cause.kind(),
        io::ErrorKind::ResourceBusy
    );
    drop(entry);
    store.cleanup().unwrap();
    assert!(!store.root().exists());
}

#[test]
fn tree_manifest_retains_empty_directories_and_exact_publication_replaces_the_root() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    fs::create_dir_all(source.join("empty")).unwrap();
    fs::write(source.join("file"), "new").unwrap();
    fs::create_dir_all(target.join("old-subtree")).unwrap();
    fs::write(target.join("old-subtree/old"), "old").unwrap();
    let mut store = store(workspace.path());
    let snapshot = store.capture(&source).unwrap();
    assert!(snapshot.is_directory());
    assert_eq!(
        snapshot.entries().keys().cloned().collect::<Vec<_>>(),
        [PathBuf::new(), "empty".into(), "file".into()]
    );
    let mut publication = store.prepare(&snapshot, &target).unwrap();
    assert!(target.join("old-subtree/old").exists());
    assert!(
        !target.join("file").exists(),
        "preparation must not expose replacement data"
    );
    publication.publish().unwrap();
    assert_eq!(read(&target.join("file")), "new");
    assert!(target.join("empty").is_dir());
    assert!(!target.join("old-subtree").exists());
    assert!(
        !target.join("source").exists(),
        "whole-root replacement is not container insertion"
    );
    publication.rollback().unwrap();
    assert_eq!(read(&target.join("old-subtree/old")), "old");
    assert!(!target.join("file").exists());
    publication.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn checkpoint_rewind_and_origin_restore_preserve_distinct_versions() {
    let workspace = workspace();
    let target = workspace.path().join("target");
    let next = workspace.path().join("next");
    fs::create_dir(&target).unwrap();
    fs::write(target.join("a"), "origin").unwrap();
    let mut store = store(workspace.path());
    let origin = store.capture(&target).unwrap();
    fs::create_dir(&next).unwrap();
    fs::write(next.join("a"), "checkpoint").unwrap();
    fs::write(next.join("created"), "created at checkpoint").unwrap();
    let first = store.capture(&next).unwrap();
    let mut checkpoint = store.prepare(&first, &target).unwrap();
    checkpoint.publish().unwrap();
    checkpoint.finalize().unwrap();
    fs::write(next.join("a"), "uncommitted").unwrap();
    fs::remove_file(next.join("created")).unwrap();
    fs::write(next.join("new"), "pending").unwrap();
    let second = store.capture(&next).unwrap();
    let mut pending = store.prepare(&second, &target).unwrap();
    pending.publish().unwrap();
    assert_eq!(read(&target.join("a")), "uncommitted");
    pending.rollback().unwrap();
    assert_eq!(read(&target.join("a")), "checkpoint");
    assert_eq!(read(&target.join("created")), "created at checkpoint");
    assert!(!target.join("new").exists());
    pending.finalize().unwrap();
    let mut named_rollback = store.prepare(&origin, &target).unwrap();
    named_rollback.publish().unwrap();
    assert_eq!(read(&target.join("a")), "origin");
    assert!(!target.join("created").exists());
    named_rollback.finalize().unwrap();
    drop((origin, first, second));
    store.cleanup().unwrap();
}

#[test]
fn absence_is_a_tombstone_not_an_empty_file_in_both_directions() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    let mut store = store(workspace.path());
    let absent = store.capture(&target).unwrap();
    assert!(absent.is_missing());
    fs::write(&source, "new").unwrap();
    let file = store.capture(&source).unwrap();
    let mut create = store.prepare(&file, &target).unwrap();
    create.publish().unwrap();
    assert_eq!(read(&target), "new");
    create.rollback().unwrap();
    assert!(!target.exists());
    create.finalize().unwrap();
    fs::create_dir(&target).unwrap();
    fs::write(target.join("child"), "retained").unwrap();
    let mut remove = store.prepare(&absent, &target).unwrap();
    remove.publish().unwrap();
    assert!(!target.exists());
    remove.rollback().unwrap();
    assert_eq!(read(&target.join("child")), "retained");
    remove.finalize().unwrap();
    drop((absent, file));
    store.cleanup().unwrap();
}

#[test]
fn publication_failures_restore_before_images_and_retain_retryable_journals() {
    for rejected in [PublishEvent::AfterBackup, PublishEvent::AfterInstall] {
        let workspace = workspace();
        let source = workspace.path().join("source");
        let target = workspace.path().join("target");
        fs::write(&source, "new").unwrap();
        fs::write(&target, "old").unwrap();
        let mut store = store(workspace.path());
        let snapshot = store.capture(&source).unwrap();
        let mut publication = store.prepare(&snapshot, &target).unwrap();
        let failure = publication
            .publish_with(|event| {
                if event == rejected {
                    Err(io::Error::other("injected publication failure"))
                } else {
                    Ok(())
                }
            })
            .unwrap_err();
        assert_eq!(failure.recovery, Recovery::Restored);
        assert_eq!(read(&target), "old");
        assert!(failure.journal.as_ref().unwrap().is_dir());
        assert!(failure.recovery_error.is_none());
        publication.publish().unwrap();
        assert_eq!(read(&target), "new");
        publication.rollback().unwrap();
        assert_eq!(read(&target), "old");
        publication.finalize().unwrap();
        drop(snapshot);
        store.cleanup().unwrap();
    }
}

#[test]
fn failed_compensation_retains_both_versions_and_requires_explicit_recovery() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    fs::write(&source, "new").unwrap();
    fs::write(&target, "old").unwrap();
    let mut store = store(workspace.path());
    let snapshot = store.capture(&source).unwrap();
    let mut publication = store.prepare(&snapshot, &target).unwrap();
    let failure = publication
        .publish_with(|event| {
            if event == PublishEvent::AfterBackup {
                fs::write(&target, "foreign obstruction")?;
                return Err(io::Error::other(
                    "injected failure plus failed compensation",
                ));
            }
            Ok(())
        })
        .unwrap_err();
    assert_eq!(failure.recovery, Recovery::JournalRetained);
    assert!(failure.recovery_error.is_some());
    assert_eq!(read(&target), "foreign obstruction");
    assert_eq!(read(&publication.journal().join("old")), "old");
    assert_eq!(read(&publication.journal().join("new")), "new");
    assert!(
        publication.finalize().is_err(),
        "failed recovery must never discard retained undo"
    );
    fs::remove_file(&target).unwrap();
    publication.recover().unwrap();
    assert_eq!(read(&target), "old");
    publication.publish().unwrap();
    assert_eq!(read(&target), "new");
    publication.rollback().unwrap();
    publication.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn a_native_install_failure_restores_the_original_target_before_returning() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    fs::write(&source, "new").unwrap();
    fs::write(&target, "old").unwrap();
    let mut store = store(workspace.path());
    let snapshot = store.capture(&source).unwrap();
    let mut publication = store.prepare(&snapshot, &target).unwrap();
    fs::remove_file(publication.journal().join("new")).unwrap();
    let failure = publication.publish().unwrap_err();
    assert_eq!(failure.cause.kind(), io::ErrorKind::NotFound);
    assert_eq!(failure.recovery, Recovery::Restored);
    assert_eq!(read(&target), "old");
    assert!(failure.journal.unwrap().exists());
    publication.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[cfg(windows)]
#[test]
fn cleanup_failure_is_visible_retains_the_journal_and_can_be_retried() {
    use std::os::windows::fs::OpenOptionsExt;
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    fs::write(&source, "new").unwrap();
    fs::write(&target, "old").unwrap();
    let mut store = store(workspace.path());
    let snapshot = store.capture(&source).unwrap();
    let mut publication = store.prepare(&snapshot, &target).unwrap();
    publication.publish().unwrap();
    let locked = fs::OpenOptions::new()
        .read(true)
        .share_mode(3)
        .open(publication.journal().join("old"))
        .unwrap();
    let failure = publication.finalize().unwrap_err();
    assert!(failure.journal.as_ref().unwrap().exists());
    assert_eq!(failure.recovery, Recovery::JournalRetained);
    assert_eq!(read(&target), "new");
    assert!(
        publication.rollback().is_err(),
        "irreversible cleanup must not promise rollback after deleting undo entries"
    );
    drop(locked);
    publication.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn another_owners_replacement_is_never_overwritten_during_rollback() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    let moved = workspace.path().join("moved");
    fs::write(&source, "new").unwrap();
    fs::write(&target, "old").unwrap();
    let mut store = store(workspace.path());
    let snapshot = store.capture(&source).unwrap();
    let mut publication = store.prepare(&snapshot, &target).unwrap();
    publication.publish().unwrap();
    fs::rename(&target, &moved).unwrap();
    fs::write(&target, "foreign").unwrap();
    let failure = publication.rollback().unwrap_err();
    assert_eq!(failure.cause.kind(), io::ErrorKind::AlreadyExists);
    assert_eq!(read(&target), "foreign");
    fs::remove_file(&target).unwrap();
    fs::rename(&moved, &target).unwrap();
    publication.rollback().unwrap();
    assert_eq!(read(&target), "old");
    publication.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn root_replacement_scratch_capture_and_ancestor_capture_are_rejected_before_mutation() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    fs::write(&source, "file").unwrap();
    let mut store = store(workspace.path());
    assert!(store.capture(workspace.path()).is_err());
    assert!(store.capture(store.root()).is_err());
    let snapshot = store.capture(&source).unwrap();
    let filesystem_root = workspace.path().ancestors().last().unwrap();
    assert!(store.prepare(&snapshot, filesystem_root).is_err());
    assert!(store.prepare(&snapshot, workspace.path()).is_err());
    assert!(
        store
            .prepare(&snapshot, &store.root().join("target"))
            .is_err()
    );
    assert_eq!(read(&source), "file");
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn cross_store_snapshots_do_not_allow_replacing_their_artifact_owners() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    fs::write(&source, "retained").unwrap();
    let mut first = store(workspace.path());
    let mut second = SnapshotStore::new(&workspace.path().join("other-artifacts")).unwrap();
    let snapshot = second.capture(&source).unwrap();
    assert!(
        first
            .prepare(&snapshot, second.root().parent().unwrap())
            .is_err()
    );
    let target = workspace.path().join("target");
    let mut publication = first.prepare(&snapshot, &target).unwrap();
    publication.publish().unwrap();
    assert_eq!(read(&target), "retained");
    publication.rollback().unwrap();
    publication.finalize().unwrap();
    drop(snapshot);
    first.cleanup().unwrap();
    second.cleanup().unwrap();
}

#[test]
fn immutable_entry_versions_can_be_assembled_without_allowing_path_escape() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    let replacement = workspace.path().join("replacement");
    let target = workspace.path().join("target");
    fs::create_dir(&source).unwrap();
    fs::write(source.join("file"), "original").unwrap();
    fs::write(&replacement, "replacement").unwrap();
    let mut store = store(workspace.path());
    let base = store.capture(&source).unwrap();
    let version = store.capture(&replacement).unwrap();
    let mut entries = base.entries().clone();
    entries.insert("file".into(), version.entries()[Path::new("")].clone());
    let composed = store.from_entries(entries).unwrap();
    let mut publication = store.prepare(&composed, &target).unwrap();
    publication.publish().unwrap();
    assert_eq!(read(&target.join("file")), "replacement");
    assert_eq!(read(&source.join("file")), "original");
    publication.rollback().unwrap();
    publication.finalize().unwrap();
    for key in ["../escaped", "/absolute", "missing-parent/file"] {
        let entries =
            BTreeMap::from([(PathBuf::from(key), version.entries()[Path::new("")].clone())]);
        assert!(store.from_entries(entries).is_err(), "{key}");
    }
    drop((base, version, composed));
    store.cleanup().unwrap();
}

#[cfg(unix)]
#[test]
fn symlink_entries_and_ancestors_are_rejected_without_following_them() {
    use std::os::unix::fs::symlink;
    let workspace = workspace();
    let source = workspace.path().join("source");
    let link = workspace.path().join("link");
    fs::create_dir(&source).unwrap();
    fs::write(source.join("file"), "original").unwrap();
    symlink(&source, &link).unwrap();
    let mut store = store(workspace.path());
    assert!(store.capture(&link.join("file")).is_err());
    assert!(store.capture(&link.join("../source/file")).is_err());
    symlink(source.join("file"), source.join("inside")).unwrap();
    assert!(store.capture(&source).is_err());
    store.cleanup().unwrap();
    assert_eq!(read(&source.join("file")), "original");
}

#[cfg(unix)]
#[test]
fn file_and_directory_modes_survive_snapshot_publication_and_rollback() {
    use std::os::unix::fs::PermissionsExt;
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    fs::create_dir(&source).unwrap();
    fs::write(source.join("file"), "new").unwrap();
    fs::set_permissions(source.join("file"), fs::Permissions::from_mode(0o540)).unwrap();
    fs::set_permissions(&source, fs::Permissions::from_mode(0o550)).unwrap();
    fs::create_dir(&target).unwrap();
    fs::write(target.join("old"), "old").unwrap();
    fs::set_permissions(&target, fs::Permissions::from_mode(0o750)).unwrap();
    let mut store = store(workspace.path());
    let snapshot = store.capture(&source).unwrap();
    let mut publication = store.prepare(&snapshot, &target).unwrap();
    publication.publish().unwrap();
    assert_eq!(
        fs::metadata(&target).unwrap().permissions().mode() & 0o777,
        0o550
    );
    assert_eq!(
        fs::metadata(target.join("file"))
            .unwrap()
            .permissions()
            .mode()
            & 0o777,
        0o540
    );
    publication.rollback().unwrap();
    assert_eq!(
        fs::metadata(&target).unwrap().permissions().mode() & 0o777,
        0o750
    );
    publication.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
    fs::set_permissions(&source, fs::Permissions::from_mode(0o700)).unwrap();
}
