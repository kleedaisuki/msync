//! Output quota is distinct from a buffer budget and never kills the child.

use std::fs;
use std::path::Path;
use std::process::{Command, Output};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("spool-limit-")
        .tempdir_in(root)
        .unwrap()
}

fn invoke(root: &Path, limit: &str) -> Output {
    Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root)
        .args([
            "run",
            "parent.msync",
            "--spool-limit",
            limit,
            "--color",
            "never",
        ])
        .output()
        .unwrap()
}

#[test]
fn zero_quota_reports_output_failure_only_after_the_child_finishes_its_effects() {
    let root = workspace();
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    fs::write(
        root.path().join("child.msync"),
        "\"child completed\" -> finished.txt\n",
    )
    .unwrap();
    fs::write(
        root.path().join("parent.msync"),
        format!(
            "Program => $local::{executable}(\"run\", \"child.msync\", \"--color\", \"never\")\n"
        ),
    )
    .unwrap();
    let output = invoke(root.path(), "0");
    assert!(!output.status.success());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(error.contains("quota"), "{error}");
    assert_eq!(
        fs::read(root.path().join("finished.txt")).unwrap(),
        b"child completed"
    );
}

#[test]
fn a_nonzero_quota_can_capture_the_complete_program_output() {
    let root = workspace();
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    fs::write(
        root.path().join("parent.msync"),
        format!("Program => $local::{executable}(\"--version\")\nProgram::stdout -> version.txt\n"),
    )
    .unwrap();
    let output = invoke(root.path(), "1KiB");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        fs::read_to_string(root.path().join("version.txt")).unwrap(),
        format!("msync {}\n", env!("CARGO_PKG_VERSION"))
    );
}
