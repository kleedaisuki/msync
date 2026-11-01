#![cfg(unix)]

use std::fs;
use std::io::Write;
use std::path::Path;
use std::process::{Command, Output, Stdio};

fn workspace() -> tempfile::TempDir {
    let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&base).unwrap();
    tempfile::Builder::new()
        .prefix("native-path-")
        .tempdir_in(base)
        .unwrap()
}

fn run(root: &Path, source: &str) -> Output {
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .args(["--no-config", "run", "-"])
        .current_dir(root)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(source.as_bytes())
        .unwrap();
    child.wait_with_output().unwrap()
}

#[test]
fn selected_linked_workspace_supports_reads_and_replacement() {
    let work = workspace();
    let real = work.path().join("real");
    fs::create_dir(&real).unwrap();
    fs::write(real.join("source"), b"selected native bytes").unwrap();
    fs::write(real.join("target"), b"previous bytes").unwrap();
    let alias = work.path().join("alias");
    std::os::unix::fs::symlink(&real, &alias).unwrap();
    let output = run(work.path(), "alias/source -> alias/target\n");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        fs::read(real.join("target")).unwrap(),
        b"selected native bytes"
    );
    assert_eq!(fs::read_link(alias).unwrap(), real);
}

#[test]
fn parent_traversal_after_directory_link_uses_the_native_parent() {
    let work = workspace();
    let real = work.path().join("real");
    fs::create_dir_all(real.join("nested")).unwrap();
    fs::write(real.join("source"), b"native parent bytes").unwrap();
    fs::write(work.path().join("source"), b"lexical decoy").unwrap();
    std::os::unix::fs::symlink(real.join("nested"), work.path().join("alias")).unwrap();
    let output = run(work.path(), "alias/../source -> result\n");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        fs::read(work.path().join("result")).unwrap(),
        b"native parent bytes"
    );
    assert_eq!(
        fs::read(work.path().join("source")).unwrap(),
        b"lexical decoy"
    );
}

#[test]
fn writes_on_different_native_parents_are_not_eliminated_as_lexical_aliases() {
    let work = workspace();
    let real = work.path().join("real");
    fs::create_dir_all(real.join("nested")).unwrap();
    fs::write(work.path().join("first"), b"real-parent value").unwrap();
    fs::write(work.path().join("second"), b"workspace value").unwrap();
    std::os::unix::fs::symlink(real.join("nested"), work.path().join("alias")).unwrap();
    let output = run(work.path(), "first -> alias/../target\nsecond -> target\n");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(fs::read(real.join("target")).unwrap(), b"real-parent value");
    assert_eq!(
        fs::read(work.path().join("target")).unwrap(),
        b"workspace value"
    );
}

#[test]
fn backslash_filename_is_not_forwarded_from_a_directory_path() {
    let work = workspace();
    fs::create_dir(work.path().join("slash")).unwrap();
    fs::write(
        work.path().join("slash/name"),
        b"previous directory-path bytes",
    )
    .unwrap();
    fs::write(work.path().join("slash\\name"), b"literal backslash bytes").unwrap();
    let literal = serde_json::to_string("slash\\name").unwrap();
    let source =
        format!("\"new directory-path bytes\" -> slash/name\n$local::{literal} -> copied\n");
    let output = run(work.path(), &source);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        fs::read(work.path().join("copied")).unwrap(),
        b"literal backslash bytes"
    );
    assert_eq!(
        fs::read(work.path().join("slash/name")).unwrap(),
        b"new directory-path bytes"
    );
}
