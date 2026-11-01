use std::collections::BTreeMap;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use msync::filesystem::{Entry, PublishEvent, SnapshotStore};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("filesystem-v5-")
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
fn assembled_manifest_paths_have_one_canonical_node_spelling() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    fs::write(&source, b"selected file").unwrap();
    let mut store = store(workspace.path());
    let captured = store.capture(&source).unwrap();
    let file = captured.entries().get(Path::new("")).unwrap().clone();
    let directory =
        msync::filesystem::NodeMetadata::capture(&fs::metadata(workspace.path()).unwrap()).unwrap();
    let tree = store
        .from_entries(BTreeMap::from([
            (
                PathBuf::new(),
                Entry::Directory {
                    metadata: directory,
                },
            ),
            (Path::new("config.toml").join(""), file),
        ]))
        .unwrap();
    let key = tree
        .entries()
        .keys()
        .find(|path| !path.as_os_str().is_empty())
        .unwrap();
    assert_eq!(key.as_os_str(), "config.toml");
    let mut publication = store.prepare(&tree, &target).unwrap();
    publication.publish().unwrap();
    publication.finalize().unwrap();
    assert_eq!(
        fs::read(target.join("config.toml")).unwrap(),
        b"selected file"
    );
    drop(tree);
    drop(captured);
    store.cleanup().unwrap();
}

#[test]
fn tree_manifest_retains_empty_directories_and_replacement_has_no_before_image() {
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
    let mut publication = store.prepare(&snapshot, &target).unwrap();
    assert_eq!(read(&target.join("old-subtree/old")), "old");
    publication.publish().unwrap();
    assert_eq!(read(&target.join("file")), "new");
    assert!(target.join("empty").is_dir());
    assert!(!target.join("old-subtree").exists());
    assert!(!publication.journal().join("old").exists());
    assert_eq!(
        publication.rollback().unwrap_err().cause.kind(),
        io::ErrorKind::Unsupported
    );
    publication.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn publishing_an_older_captured_value_is_an_explicit_new_replacement() {
    let workspace = workspace();
    let target = workspace.path().join("target");
    fs::write(&target, "origin").unwrap();
    let mut store = store(workspace.path());
    let origin = store.capture(&target).unwrap();
    fs::write(&target, "later value").unwrap();
    let mut replacement = store.prepare(&origin, &target).unwrap();
    replacement.publish().unwrap();
    assert_eq!(read(&target), "origin");
    replacement.finalize().unwrap();
    drop(origin);
    store.cleanup().unwrap();
}

#[test]
fn an_absent_snapshot_requests_removal_not_an_empty_file() {
    let workspace = workspace();
    let target = workspace.path().join("target");
    let mut store = store(workspace.path());
    let absent = store.capture(&target).unwrap();
    assert!(absent.is_missing());
    fs::create_dir(&target).unwrap();
    fs::write(target.join("child"), "old").unwrap();
    let mut replacement = store.prepare(&absent, &target).unwrap();
    replacement.publish().unwrap();
    assert!(!target.exists());
    replacement.finalize().unwrap();
    drop(absent);
    store.cleanup().unwrap();
}

#[test]
fn observer_failure_preserves_the_actual_replace_outcome_and_retry_uses_a_new_stage() {
    for rejected in [PublishEvent::AfterBackup, PublishEvent::AfterInstall] {
        let workspace = workspace();
        let source = workspace.path().join("source");
        let target = workspace.path().join("target");
        fs::write(&source, "new").unwrap();
        fs::write(&target, "old").unwrap();
        let mut store = store(workspace.path());
        let snapshot = store.capture(&source).unwrap();
        let mut replacement = store.prepare(&snapshot, &target).unwrap();
        replacement
            .publish_with(|event| {
                if event == rejected {
                    Err(io::Error::new(
                        io::ErrorKind::Interrupted,
                        "injected interruption",
                    ))
                } else {
                    Ok(())
                }
            })
            .unwrap_err();
        assert_eq!(
            read(&target),
            if rejected == PublishEvent::AfterBackup {
                "old"
            } else {
                "new"
            }
        );
        replacement.finalize().unwrap();
        let mut retry = store.prepare(&snapshot, &target).unwrap();
        retry.publish().unwrap();
        retry.finalize().unwrap();
        assert_eq!(read(&target), "new");
        drop(snapshot);
        store.cleanup().unwrap();
    }
}

#[test]
fn dropping_an_unpublished_stage_cleans_only_that_stage() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    fs::write(&source, "new").unwrap();
    fs::write(&target, "old").unwrap();
    let mut store = store(workspace.path());
    let snapshot = store.capture(&source).unwrap();
    let stage;
    {
        let replacement = store.prepare(&snapshot, &target).unwrap();
        stage = replacement.journal().to_owned();
        assert!(stage.exists());
    }
    assert!(!stage.exists());
    assert_eq!(read(&target), "old");
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn a_native_failure_before_replace_leaves_the_original_target() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    fs::write(&source, "new").unwrap();
    fs::write(&target, "old").unwrap();
    let mut store = store(workspace.path());
    let snapshot = store.capture(&source).unwrap();
    let mut replacement = store.prepare(&snapshot, &target).unwrap();
    fs::remove_file(replacement.journal().join("new")).unwrap();
    assert_eq!(
        replacement.publish().unwrap_err().cause.kind(),
        io::ErrorKind::NotFound
    );
    assert_eq!(read(&target), "old");
    replacement.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[cfg(windows)]
#[test]
fn transient_stage_cleanup_failure_does_not_undo_a_completed_replace() {
    use std::os::windows::fs::OpenOptionsExt;
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    fs::write(&source, "new").unwrap();
    let mut store = store(workspace.path());
    let snapshot = store.capture(&source).unwrap();
    let mut replacement = store.prepare(&snapshot, &target).unwrap();
    replacement.publish().unwrap();
    let held_path = replacement.journal().join("held-private-file");
    fs::write(&held_path, "cleanup fixture").unwrap();
    let held = fs::OpenOptions::new()
        .read(true)
        .share_mode(3)
        .open(held_path)
        .unwrap();
    assert!(replacement.finalize().is_err());
    assert_eq!(read(&target), "new");
    drop(held);
    replacement.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn a_completed_replace_does_not_keep_authority_to_rewrite_the_target_again() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    fs::write(&source, "new").unwrap();
    let mut store = store(workspace.path());
    let snapshot = store.capture(&source).unwrap();
    let mut replacement = store.prepare(&snapshot, &target).unwrap();
    replacement.publish().unwrap();
    fs::write(&target, "user changed it later").unwrap();
    assert_eq!(
        replacement.rollback().unwrap_err().cause.kind(),
        io::ErrorKind::Unsupported
    );
    assert_eq!(read(&target), "user changed it later");
    replacement.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn a_snapshot_store_does_not_recursively_capture_its_own_evolving_artifacts() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    fs::write(&source, "file").unwrap();
    let mut store = store(workspace.path());
    assert!(store.capture(workspace.path()).is_err());
    assert!(store.capture(store.root()).is_err());
    let snapshot = store.capture(&source).unwrap();
    let filesystem_root = workspace.path().ancestors().last().unwrap();
    assert!(store.prepare(&snapshot, filesystem_root).is_err());
    assert_eq!(read(&source), "file");
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn cross_store_snapshots_keep_their_payload_owners_alive_until_consumed() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    fs::write(&source, "retained").unwrap();
    let mut first = store(workspace.path());
    let mut second = SnapshotStore::new(&workspace.path().join("other-artifacts")).unwrap();
    let snapshot = second.capture(&source).unwrap();
    assert_eq!(
        second.cleanup().unwrap_err().cause.kind(),
        io::ErrorKind::ResourceBusy
    );
    let target = workspace.path().join("target");
    let mut replacement = first.prepare(&snapshot, &target).unwrap();
    replacement.publish().unwrap();
    replacement.finalize().unwrap();
    assert_eq!(read(&target), "retained");
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
    publication.finalize().unwrap();
    for key in ["../escaped", "/absolute", "missing-parent/file"] {
        let entries =
            BTreeMap::from([(PathBuf::from(key), version.entries()[Path::new("")].clone())]);
        assert!(store.from_entries(entries).is_err(), "{key}");
    }
    drop((base, version, composed));
    store.cleanup().unwrap();
}

#[cfg(windows)]
#[test]
fn ordinary_windows_paths_preserve_the_file_root_without_a_trailing_separator() {
    let workspace = workspace();
    let text = workspace.path().to_str().unwrap();
    let ordinary = PathBuf::from(text.strip_prefix(r"\\?\").unwrap_or(text));
    assert!(!ordinary.to_string_lossy().starts_with(r"\\?\"));
    let source = ordinary.join("source");
    let target = ordinary.join("target");
    fs::write(&source, "ordinary file root").unwrap();
    let mut store = store(&ordinary);
    let snapshot = store.capture(&source).unwrap();
    let mut publication = store.prepare(&snapshot, &target).unwrap();
    publication.publish().unwrap();
    assert_eq!(read(&target), "ordinary file root");
    assert!(target.exists());
    publication.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[cfg(unix)]
#[test]
fn selected_ancestor_aliases_work_and_recursive_links_remain_links() {
    use std::os::unix::fs::symlink;
    let workspace = workspace();
    let source = workspace.path().join("source");
    let link = workspace.path().join("link");
    fs::create_dir(&source).unwrap();
    fs::write(source.join("file"), "original").unwrap();
    symlink(&source, &link).unwrap();
    symlink("file", source.join("inside")).unwrap();
    symlink("not-created", source.join("dangling")).unwrap();
    let mut store = store(workspace.path());
    let selected = store.capture(&link.join("file")).unwrap();
    let tree = store.capture(&source).unwrap();
    let mut replacement = store
        .prepare(&tree, &workspace.path().join("copied"))
        .unwrap();
    replacement.publish().unwrap();
    replacement.finalize().unwrap();
    assert_eq!(
        fs::read_link(workspace.path().join("copied/inside")).unwrap(),
        Path::new("file")
    );
    assert_eq!(
        fs::read_link(workspace.path().join("copied/dangling")).unwrap(),
        Path::new("not-created")
    );
    assert_eq!(read(&source.join("file")), "original");
    drop((selected, tree));
    store.cleanup().unwrap();
}

#[cfg(unix)]
#[test]
fn file_and_directory_modes_survive_snapshot_replacement() {
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
    assert_eq!(
        fs::metadata(source.join("file"))
            .unwrap()
            .permissions()
            .mode()
            & 0o777,
        0o540
    );
    publication.finalize().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
    fs::set_permissions(&source, fs::Permissions::from_mode(0o700)).unwrap();
    fs::set_permissions(&target, fs::Permissions::from_mode(0o700)).unwrap();
}
