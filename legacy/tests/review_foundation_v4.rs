//! Independent counterexamples for the v0.4 foundation review.

use std::fs;
use std::path::Path;

use msync::filesystem::SnapshotStore;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("review-foundation-v4-")
        .tempdir_in(root)
        .unwrap()
}

#[cfg(windows)]
#[test]
fn repl_shell_edits_are_not_hidden_by_prior_managed_snapshots() {
    use std::io::Write;
    use std::process::{Command, Stdio};

    let workspace = workspace();
    fs::write(workspace.path().join("source"), b"before shell").unwrap();
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .args(["repl", "--color", "never"])
        .current_dir(workspace.path())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child.stdin.take().unwrap().write_all(
        b"source -> first\n/pwsh Set-Content -LiteralPath 'source' -Value 'after shell' -NoNewline\nsource -> second\n/quit\n"
    ).unwrap();
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        fs::read(workspace.path().join("source")).unwrap(),
        b"after shell"
    );
    assert_eq!(
        fs::read(workspace.path().join("second")).unwrap(),
        b"after shell",
        "REPL copied a pre-shell snapshot; stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

#[test]
fn dead_write_elimination_preserves_metadata_needed_by_a_following_literal() {
    use std::io::Write;
    use std::process::{Command, Stdio};

    let workspace = workspace();
    let source = workspace.path().join("source");
    let target = workspace.path().join("target");
    fs::write(&source, b"source bytes").unwrap();
    fs::write(&target, b"old target").unwrap();
    #[cfg(windows)]
    {
        let mut permissions = fs::metadata(&source).unwrap().permissions();
        permissions.set_readonly(true);
        fs::set_permissions(&source, permissions).unwrap();
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        fs::set_permissions(&source, fs::Permissions::from_mode(0o750)).unwrap();
        fs::set_permissions(&target, fs::Permissions::from_mode(0o600)).unwrap();
    }
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .args(["run", "-", "--color", "never"])
        .current_dir(workspace.path())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(b"source -> target\n\"literal\" -> target\n")
        .unwrap();
    let output = child.wait_with_output().unwrap();
    #[cfg(windows)]
    {
        let after = fs::metadata(&target).unwrap().permissions().readonly();
        // Source and target may both be readonly in the correct execution.
        for path in [&source, &target] {
            let mut permissions = fs::metadata(path).unwrap().permissions();
            #[allow(clippy::permissions_set_readonly_false)]
            permissions.set_readonly(false);
            fs::set_permissions(path, permissions).unwrap();
        }
        assert!(
            after,
            "optimizer erased the permission-producing file assignment; status={} stdout={} stderr={}",
            output.status,
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        assert_eq!(
            fs::metadata(&target).unwrap().permissions().mode() & 0o777,
            0o750,
            "optimizer erased executable metadata; stdout={} stderr={}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
    }
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
