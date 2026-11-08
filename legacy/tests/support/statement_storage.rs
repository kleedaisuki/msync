use std::fs;
use std::path::Path;

/// Completed transfers retain neither payloads nor persistent recovery metadata.
pub fn assert_clean(scratch: &Path) {
    assert_clean_with_owner(scratch, None, None);
}

/// A live runtime may keep its exact empty ephemeral directory, without a record.
pub fn assert_clean_with_owner(
    scratch: &Path,
    private_root: Option<&Path>,
    stage_record: Option<&Path>,
) {
    assert!(
        stage_record.is_none(),
        "persistent stage registration remains"
    );
    if !scratch.exists() {
        assert!(private_root.is_none());
        return;
    }
    let mut root_seen = false;
    for entry in fs::read_dir(scratch).unwrap() {
        let entry = entry.unwrap();
        let private = private_root.unwrap_or_else(|| {
            panic!(
                "unexpected retained scratch entry: {}",
                entry.path().display()
            )
        });
        assert!(
            same_file::is_same_file(entry.path(), private).unwrap(),
            "scratch entry is not the current live owner: {}",
            entry.path().display()
        );
        assert_private_root_metadata(&entry.path());
        root_seen = true;
    }
    assert_eq!(root_seen, private_root.is_some());
}

pub fn assert_private_root_metadata(root: &Path) {
    assert!(root.symlink_metadata().unwrap().file_type().is_dir());
    assert!(
        fs::read_dir(root).unwrap().next().is_none(),
        "inactive live owner retains payload/control state: {}",
        root.display()
    );
}
