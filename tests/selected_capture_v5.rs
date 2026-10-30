use std::fs;
use std::path::Path;

use msync::filesystem::{Entry, InvocationRoot, Snapshot, SnapshotStore};
use regex::Regex;

fn workspace() -> tempfile::TempDir {
    let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&base).unwrap();
    tempfile::Builder::new()
        .prefix("selected-capture-v5-")
        .tempdir_in(base)
        .unwrap()
}

fn owner(root: &Path) -> (InvocationRoot, SnapshotStore) {
    let invocation = InvocationRoot::create(&root.join("scratch")).unwrap();
    let store = SnapshotStore::new_in(&invocation.path().join("assets"), &invocation).unwrap();
    (invocation, store)
}

fn bytes(snapshot: &Snapshot) -> u64 {
    snapshot
        .entries()
        .values()
        .map(|entry| match entry {
            Entry::File { artifact, .. } => artifact.len(),
            Entry::Directory { .. } => 0,
        })
        .sum()
}

#[test]
fn copies_only_selected_payload_and_complete_selected_subtrees() {
    let root = workspace();
    let source = root.path().join("source");
    fs::create_dir_all(source.join("picked/empty")).unwrap();
    fs::write(source.join("keep.txt"), b"selected").unwrap();
    fs::write(source.join("picked/nested"), b"tree").unwrap();
    fs::File::create(source.join("unselected-large"))
        .unwrap()
        .set_len(16 * 1024 * 1024)
        .unwrap();
    let (invocation, store) = owner(root.path());
    let regex = Regex::new(r"\A(?:keep\.txt|picked)\z").unwrap();

    let snapshot = store
        .capture_selected_in(&source, &invocation, &regex)
        .unwrap();
    assert!(snapshot.is_directory());
    assert_eq!(bytes(&snapshot), 12);
    assert_eq!(fs::read_dir(store.root()).unwrap().count(), 2);
    assert!(snapshot.entries().contains_key(Path::new("picked/empty")));
    assert!(snapshot.entries().contains_key(Path::new("picked/nested")));
    assert!(
        !snapshot
            .entries()
            .contains_key(Path::new("unselected-large"))
    );
}

#[test]
fn no_match_preserves_a_directory_root_without_creating_artifacts() {
    let root = workspace();
    let source = root.path().join("source");
    fs::create_dir(&source).unwrap();
    fs::write(source.join("not-selected"), b"must not be copied").unwrap();
    let (invocation, store) = owner(root.path());
    let regex = Regex::new(r"\Aabsent\z").unwrap();

    let snapshot = store
        .capture_selected_in(&source, &invocation, &regex)
        .unwrap();
    assert!(snapshot.is_directory());
    assert_eq!(snapshot.entries().len(), 1);
    assert_eq!(bytes(&snapshot), 0);
    assert_eq!(fs::read_dir(store.root()).unwrap().count(), 0);
}

#[test]
fn missing_root_remains_missing_and_non_directory_root_never_copies_payload() {
    let root = workspace();
    let (invocation, store) = owner(root.path());
    let regex = Regex::new(r"\A.*\z").unwrap();
    assert!(
        store
            .capture_selected_in(&root.path().join("missing"), &invocation, &regex)
            .unwrap()
            .is_missing()
    );
    let source = root.path().join("ordinary-file");
    fs::write(&source, b"not a selector directory").unwrap();
    let error = store
        .capture_selected_in(&source, &invocation, &regex)
        .unwrap_err();
    assert!(
        error
            .to_string()
            .contains("selector source must be a directory")
    );
    assert_eq!(fs::read_dir(store.root()).unwrap().count(), 0);
}

#[test]
fn containing_source_retains_user_temp_data_but_excludes_exact_private_owners() {
    use msync::filesystem::recovery::Registry;

    let root = workspace();
    let scratch = root.path().join("scratch");
    let registry = Registry::new(&scratch).unwrap();
    fs::create_dir_all(root.path().join("user/.temp")).unwrap();
    fs::write(root.path().join("user/.temp/keep"), b"user data").unwrap();
    let (invocation, mut store) = owner(root.path());
    store.exclude_registry(&registry).unwrap();
    let regex = Regex::new(r"\A.*\z").unwrap();

    let snapshot = store
        .capture_selected_in(root.path(), &invocation, &regex)
        .unwrap();
    assert!(
        snapshot
            .entries()
            .contains_key(Path::new("user/.temp/keep"))
    );
    assert!(
        !snapshot
            .entries()
            .keys()
            .any(|path| path.starts_with("scratch/.msync-recovery-v1"))
    );
    let invocation_relative = invocation.path().strip_prefix(root.path()).unwrap();
    assert!(
        !snapshot
            .entries()
            .keys()
            .any(|path| path.starts_with(invocation_relative))
    );
    assert!(snapshot.excludes_invocation(&invocation).unwrap());
}

#[cfg(unix)]
#[test]
fn unselected_fifo_and_symlink_do_not_enter_metadata_or_payload_capture() {
    use std::ffi::CString;
    use std::os::unix::ffi::OsStrExt;
    use std::os::unix::fs::symlink;

    let root = workspace();
    let source = root.path().join("source");
    fs::create_dir(&source).unwrap();
    fs::write(source.join("keep.txt"), b"chosen").unwrap();
    let fifo = CString::new(source.join("unselected-fifo").as_os_str().as_bytes()).unwrap();
    // SAFETY: fifo is a valid NUL-terminated local fixture path.
    assert_eq!(unsafe { libc::mkfifo(fifo.as_ptr(), 0o600) }, 0);
    symlink("missing-target", source.join("unselected-link")).unwrap();
    let (invocation, store) = owner(root.path());
    let regex = Regex::new(r"\Akeep\.txt\z").unwrap();

    let snapshot = store
        .capture_selected_in(&source, &invocation, &regex)
        .unwrap();
    assert_eq!(bytes(&snapshot), 6);
    assert_eq!(snapshot.entries().len(), 2);
    assert!(
        store
            .capture_selected_in(
                &source,
                &invocation,
                &Regex::new(r"\Aunselected-link\z").unwrap()
            )
            .is_err()
    );
    assert!(
        store
            .capture_selected_in(
                &source,
                &invocation,
                &Regex::new(r"\Aunselected-fifo\z").unwrap()
            )
            .is_err()
    );
}

#[cfg(unix)]
#[test]
fn unselected_unreadable_directory_is_not_traversed() {
    use std::os::unix::fs::PermissionsExt;

    let root = workspace();
    let source = root.path().join("source");
    let secret = source.join("unselected-secret");
    fs::create_dir_all(&secret).unwrap();
    fs::write(source.join("keep.txt"), b"chosen").unwrap();
    fs::write(secret.join("unreadable"), b"private").unwrap();
    fs::set_permissions(&secret, fs::Permissions::from_mode(0o000)).unwrap();
    let (invocation, store) = owner(root.path());
    let regex = Regex::new(r"\Akeep\.txt\z").unwrap();

    let result = store.capture_selected_in(&source, &invocation, &regex);
    // Restore traversal even if the assertion below fails, so fixtures stay clean.
    fs::set_permissions(&secret, fs::Permissions::from_mode(0o700)).unwrap();
    let snapshot = result.unwrap();
    assert_eq!(bytes(&snapshot), 6);
    assert!(
        !snapshot
            .entries()
            .contains_key(Path::new("unselected-secret"))
    );
}
