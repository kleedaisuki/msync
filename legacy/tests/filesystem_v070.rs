//! Source-free filesystem standard-library journeys observe real native files.

#[allow(
    dead_code,
    reason = "Shared process support also serves interactive journeys."
)]
#[path = "support/journey_process.rs"]
mod journey_process;

use std::fs;
use std::path::Path;
use std::process::Output;

use journey_process::Process;

fn workspace() -> tempfile::TempDir {
    let parent = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/native-fs-v070");
    fs::create_dir_all(&parent).unwrap();
    tempfile::tempdir_in(parent).unwrap()
}

fn invoke(root: &Path, args: &[&str]) -> Output {
    let output = Process::start(Process::command(root, args)).finish();
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    output
}

#[test]
fn lexical_and_permission_values_survive_source_free_execution() {
    let root = workspace();
    fs::write(root.path().join("input"), b"payload").unwrap();
    let source = "\
fs => $std::fs
views => $std::ranges
if fs::string(fs::filename(\"nested/file.txt\")) == \"file.txt\" and not fs::has_root_path(\"relative\"):
    \"lexical\" -> lexical_result
fs::set_permissions(input, \"owner_read|owner_write\")
if fs::permissions(input) | views::contains(\"owner_read\"):
    \"permission\" -> permission_result
fs::set_readonly(input, true)
if fs::is_readonly(input):
    \"readonly\" -> readonly_result
fs::set_readonly(input, false)
fs::create_directories(tree/nested)
fs::copy_file(input, tree/nested/copied)
for item in fs::recursive_directory_iterator(tree):
    if fs::is_regular_file(item):
        item -> copied_result
for part in fs::components(\"relative/final\"):
    fs::string(part) -> component_result
if fs::remove_all(tree):
    \"removed\" -> removed_result
";
    fs::write(root.path().join("entry.msync"), source).unwrap();
    invoke(root.path(), &["check", "entry.msync"]);
    invoke(root.path(), &["compile", "entry.msync", "-o", "entry.mobj"]);
    invoke(root.path(), &["emit-ir", "entry.msync", "-o", "entry.json"]);
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for args in [["run", "entry.mobj"], ["run-ir", "entry.json"]] {
        invoke(root.path(), &args);
        for (name, expected) in [
            ("lexical_result", b"lexical".as_slice()),
            ("permission_result", b"permission".as_slice()),
            ("readonly_result", b"readonly".as_slice()),
            ("copied_result", b"payload".as_slice()),
            ("component_result", b"final".as_slice()),
            ("removed_result", b"removed".as_slice()),
        ] {
            assert_eq!(
                fs::read(root.path().join(name)).unwrap(),
                expected,
                "{name}"
            );
        }
        assert!(!root.path().join("tree").exists());
    }
}

#[cfg(unix)]
#[test]
fn relative_symlink_targets_are_stored_not_cwd_resolved() {
    let root = workspace();
    fs::create_dir(root.path().join("folder")).unwrap();
    fs::write(root.path().join("folder/target"), b"linked payload").unwrap();
    fs::write(root.path().join("entry.msync"), "fs => $std::fs\nfs::create_symlink(\"target\", folder/link)\nfs::copy_symlink(folder/link, folder/duplicate)\n").unwrap();
    invoke(root.path(), &["compile", "entry.msync", "-o", "entry.mobj"]);
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    invoke(root.path(), &["run", "entry.mobj"]);
    assert_eq!(
        fs::read_link(root.path().join("folder/link")).unwrap(),
        Path::new("target")
    );
    assert_eq!(
        fs::read_link(root.path().join("folder/duplicate")).unwrap(),
        Path::new("target")
    );
    assert_eq!(
        fs::read(root.path().join("folder/link")).unwrap(),
        b"linked payload"
    );
}
