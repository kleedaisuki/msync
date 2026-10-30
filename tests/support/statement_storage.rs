use std::fs;
use std::path::Path;

/// Durable control metadata is not a retained payload or an unfinished statement.
pub fn assert_clean(scratch: &Path) {
    assert_clean_with_owner(scratch, None, None);
}

/// Permit only the exact verified runtime root and its still-live registration.
pub fn assert_clean_with_owner(
    scratch: &Path,
    private_root: Option<&Path>,
    stage_record: Option<&Path>,
) {
    if !scratch.exists() {
        assert!(private_root.is_none() && stage_record.is_none());
        return;
    }
    assert_eq!(private_root.is_some(), stage_record.is_some());
    let mut root_seen = false;
    for entry in fs::read_dir(scratch).unwrap() {
        let entry = entry.unwrap();
        assert!(
            entry.file_type().unwrap().is_dir(),
            "unexpected scratch file: {}",
            entry.path().display()
        );
        if private_root.is_some_and(|root| same_file::is_same_file(entry.path(), root).unwrap()) {
            assert_private_root_metadata(&entry.path());
            root_seen = true;
            continue;
        }
        assert_registry_metadata_with_owner(&entry.path(), stage_record);
    }
    assert_eq!(
        root_seen,
        private_root.is_some(),
        "live root missing from its scratch parent"
    );
}

pub fn assert_private_root_metadata(root: &Path) {
    assert!(root.symlink_metadata().unwrap().file_type().is_dir());
    let entries = fs::read_dir(root)
        .unwrap()
        .map(|entry| entry.unwrap())
        .collect::<Vec<_>>();
    assert_eq!(
        entries.len(),
        1,
        "retained payload inside live owner: {}",
        root.display()
    );
    assert_eq!(entries[0].file_name(), ".msync-owned-stage-v1");
    assert!(entries[0].file_type().unwrap().is_file());
    let token = fs::read(entries[0].path()).unwrap();
    assert!(
        !token.is_empty() && token.len() <= 128,
        "invalid owned-stage marker"
    );
}

pub fn assert_registry_metadata_with_owner(registry: &Path, stage_record: Option<&Path>) {
    assert!(registry.symlink_metadata().unwrap().file_type().is_dir());
    let remote = registry.file_name().unwrap() == ".msync-remote-recovery-v1";
    let (marker, contents): (&str, &[u8]) = if remote {
        (".owner", b"MSYNC-REMOTE-REGISTRY-1\n")
    } else {
        assert_eq!(
            registry.file_name().unwrap(),
            ".msync-recovery-v1",
            "retained statement data: {}",
            registry.display()
        );
        (".lock", b"")
    };
    let mut marker_seen = false;
    let mut record_seen = false;
    for entry in fs::read_dir(registry).unwrap() {
        let entry = entry.unwrap();
        assert!(entry.file_type().unwrap().is_file());
        if entry.file_name() == marker {
            assert_eq!(fs::read(entry.path()).unwrap(), contents);
            marker_seen = true;
            continue;
        }
        assert!(
            !remote,
            "unfinished remote record: {}",
            entry.path().display()
        );
        let record = stage_record
            .unwrap_or_else(|| panic!("unfinished recovery record: {}", entry.path().display()));
        assert!(
            same_file::is_same_file(entry.path(), record).unwrap(),
            "unrelated recovery record: {}",
            entry.path().display()
        );
        assert!(!record_seen, "duplicate live stage registration");
        let file = fs::OpenOptions::new()
            .read(true)
            .write(true)
            .open(entry.path())
            .unwrap();
        assert!(
            matches!(file.try_lock(), Err(fs::TryLockError::WouldBlock)),
            "stage registration is not held by a live owner: {}",
            entry.path().display()
        );
        record_seen = true;
    }
    assert!(marker_seen, "missing recovery registry marker");
    if !remote {
        assert_eq!(
            record_seen,
            stage_record.is_some(),
            "live owner registration missing from its registry"
        );
    }
}
