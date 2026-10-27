//! Independent checks of metadata observable after a named origin rollback.

use std::fs::{self, File, FileTimes};
use std::path::Path;
use std::time::{Duration, SystemTime};

use msync::hir::TransactionId;
use msync::language::Isolation;
use msync::transaction::manager::Manager;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("review-metadata-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn set_modified(path: &Path, seconds: u64) {
    let time = SystemTime::UNIX_EPOCH + Duration::from_secs(seconds);
    #[cfg(windows)]
    let file = {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_FLAG_BACKUP_SEMANTICS, FILE_WRITE_ATTRIBUTES,
        };
        File::options()
            .access_mode(FILE_WRITE_ATTRIBUTES)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
            .open(path)
            .unwrap()
    };
    #[cfg(not(windows))]
    let file = File::open(path).unwrap();
    file.set_times(FileTimes::new().set_modified(time)).unwrap();
}

fn modified(path: &Path) -> SystemTime {
    fs::metadata(path).unwrap().modified().unwrap()
}

#[test]
fn named_origin_rollback_restores_the_original_file_modification_time() {
    let workspace = workspace();
    let target = workspace.path().join("target.txt");
    let input = workspace.path().join("input.txt");
    fs::write(&target, "origin").unwrap();
    fs::write(&input, "checkpoint").unwrap();
    set_modified(&target, 978_307_201);
    let before = modified(&target);
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&target).unwrap();
    let snapshot = manager.capture(&input).unwrap();
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    manager.replace_snapshot(id, &target, &snapshot).unwrap();
    manager.finish(id).unwrap();
    assert_eq!(fs::read_to_string(&target).unwrap(), "checkpoint");
    manager.rollback_origin(id).unwrap();
    assert_eq!(fs::read_to_string(&target).unwrap(), "origin");
    let after = modified(&target);
    drop(snapshot);
    manager.shutdown().unwrap();
    assert_eq!(
        after, before,
        "named rollback changed the file's observable mtime"
    );
}

#[test]
fn named_origin_rollback_restores_directory_and_empty_directory_modification_times() {
    let workspace = workspace();
    let target = workspace.path().join("target");
    let input = workspace.path().join("input");
    fs::create_dir_all(target.join("empty")).unwrap();
    fs::create_dir(&input).unwrap();
    fs::write(target.join("origin.txt"), "origin").unwrap();
    fs::write(input.join("new.txt"), "checkpoint").unwrap();
    set_modified(&target.join("origin.txt"), 978_307_202);
    set_modified(&target.join("empty"), 978_307_203);
    set_modified(&target, 978_307_204);
    let paths = ["origin.txt", "empty", ""];
    let before = paths.map(|relative| modified(&target.join(relative)));
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&target).unwrap();
    let snapshot = manager.capture(&input).unwrap();
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    manager.replace_snapshot(id, &target, &snapshot).unwrap();
    manager.checkpoint(id).unwrap();
    manager.finish(id).unwrap();
    assert!(target.join("new.txt").exists());
    assert!(!target.join("origin.txt").exists());
    manager.rollback_origin(id).unwrap();
    assert_eq!(
        fs::read_to_string(target.join("origin.txt")).unwrap(),
        "origin"
    );
    assert!(target.join("empty").is_dir());
    assert!(!target.join("new.txt").exists());
    let after = paths.map(|relative| modified(&target.join(relative)));
    drop(snapshot);
    manager.shutdown().unwrap();
    assert_eq!(
        after, before,
        "root and empty-directory mtimes must return to their OS-observed origin values; paths={paths:?}"
    );
}

#[test]
fn adding_a_child_updates_parent_mtime_and_origin_undo_restores_it() {
    let workspace = workspace();
    let target = workspace.path().join("target");
    let input = workspace.path().join("input");
    fs::create_dir(&target).unwrap();
    fs::write(&input, "new child").unwrap();
    set_modified(&target, 978_307_205);
    let before = modified(&target);
    let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
    manager.enroll(&target).unwrap();
    let snapshot = manager.capture(&input).unwrap();
    let id = TransactionId(0);
    manager.begin(id, Isolation::Serializable).unwrap();
    manager
        .replace_snapshot(id, &target.join("child"), &snapshot)
        .unwrap();
    let pending = manager.read_snapshot(id, &target).unwrap();
    assert!(
        pending
            .entries()
            .get(Path::new(""))
            .unwrap()
            .metadata()
            .modified
            > before
    );
    assert_eq!(
        modified(&target),
        before,
        "pending namespace changes must remain private"
    );
    manager.finish(id).unwrap();
    assert!(modified(&target) > before);
    manager.rollback_origin(id).unwrap();
    assert_eq!(modified(&target), before);
    assert!(!target.join("child").exists());
    drop(pending);
    drop(snapshot);
    manager.shutdown().unwrap();
}

#[test]
fn deletion_and_child_kind_change_update_parent_metadata_without_losing_origin() {
    for replace_with_file in [false, true] {
        let workspace = workspace();
        let target = workspace.path().join("target");
        fs::create_dir_all(target.join("folder")).unwrap();
        fs::write(target.join("folder/origin"), "retained").unwrap();
        let input = workspace.path().join("input");
        if replace_with_file {
            fs::write(&input, "replacement file").unwrap();
        }
        set_modified(&target, 978_307_250);
        let before = modified(&target);
        let mut manager = Manager::new(&workspace.path().join("scratch")).unwrap();
        manager.enroll(&target).unwrap();
        let replacement = manager.capture(&input).unwrap();
        let id = TransactionId(0);
        manager.begin(id, Isolation::Serializable).unwrap();
        manager
            .replace_snapshot(id, &target.join("folder"), &replacement)
            .unwrap();
        manager.finish(id).unwrap();
        assert!(modified(&target) > before);
        if replace_with_file {
            assert!(target.join("folder").is_file());
        } else {
            assert!(!target.join("folder").exists());
        }
        manager.rollback_origin(id).unwrap();
        assert_eq!(modified(&target), before);
        assert_eq!(
            fs::read_to_string(target.join("folder/origin")).unwrap(),
            "retained"
        );
        drop(replacement);
        manager.shutdown().unwrap();
    }
}
