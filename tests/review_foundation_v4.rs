//! Independent counterexamples for the v0.4 foundation review.

use std::fs;
use std::path::Path;

use msync::transaction::filesystem::SnapshotStore;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("review-foundation-v4-")
        .tempdir_in(root)
        .unwrap()
}

#[test]
fn finalizing_a_replaced_tree_must_not_change_permissions_through_external_hardlinks() {
    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    let external = workspace.path().join("external");
    fs::create_dir(&source).unwrap();
    fs::create_dir(&target).unwrap();
    fs::write(source.join("new"), b"new bytes").unwrap();
    fs::write(target.join("old"), b"unrelated alias retains this inode").unwrap();
    fs::hard_link(target.join("old"), &external).unwrap();
    let mut permissions = fs::metadata(&external).unwrap().permissions();
    permissions.set_readonly(true);
    fs::set_permissions(&external, permissions).unwrap();
    let before = fs::metadata(&external).unwrap().permissions().readonly();
    let mut store = SnapshotStore::new(&workspace.path().join("scratch")).unwrap();
    let snapshot = store.capture(&source).unwrap();
    let mut publication = store.prepare(&snapshot, &target).unwrap();
    publication.publish().unwrap();
    publication.finalize().unwrap();
    let after = fs::metadata(&external).unwrap().permissions().readonly();
    drop(snapshot);
    store.cleanup().unwrap();
    // Permit cleanup even while retaining a failing assertion for the bug.
    let mut permissions = fs::metadata(&external).unwrap().permissions();
    #[cfg(windows)]
    {
        // Windows clears a file attribute; Unix restores explicit owner-only bits below.
        #[allow(clippy::permissions_set_readonly_false)]
        permissions.set_readonly(false);
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        permissions.set_mode(0o600);
    }
    fs::set_permissions(&external, permissions).unwrap();
    assert!(before);
    assert_eq!(
        after, before,
        "journal cleanup changed a non-managed hard-link alias"
    );
}

#[cfg(unix)]
#[test]
fn finalizing_an_old_hardlink_does_not_add_execute_bits_to_its_external_alias() {
    use std::os::unix::fs::PermissionsExt;

    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    let external = workspace.path().join("external");
    fs::create_dir(&source).unwrap();
    fs::create_dir(&target).unwrap();
    fs::write(source.join("new"), b"new").unwrap();
    fs::write(target.join("old"), b"not executable").unwrap();
    fs::hard_link(target.join("old"), &external).unwrap();
    fs::set_permissions(&external, fs::Permissions::from_mode(0o640)).unwrap();
    let mut store = SnapshotStore::new(&workspace.path().join("scratch")).unwrap();
    let snapshot = store.capture(&source).unwrap();
    let mut publication = store.prepare(&snapshot, &target).unwrap();
    publication.publish().unwrap();
    publication.finalize().unwrap();
    let after = fs::metadata(&external).unwrap().permissions().mode() & 0o777;
    drop(snapshot);
    store.cleanup().unwrap();
    assert_eq!(after, 0o640);
}
