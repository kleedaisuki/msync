//! Independent recovery regression for a directory removed by an explicit shell.

#![cfg(unix)]

use std::fs;
use std::io::Write;
use std::path::Path;
use std::process::{Command, Stdio};

#[test]
fn a_deleted_working_directory_does_not_end_the_repl_or_lose_bindings() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let directory = tempfile::Builder::new()
        .prefix("review-ui-031-")
        .tempdir_in(temporary)
        .unwrap();
    let work = directory.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .args(["repl", "--color", "never"])
        .current_dir(&work)
        .env("HOME", directory.path())
        .env("HISTFILE", directory.path().join(".test_bash_history"))
        .env_remove("BASH_ENV")
        .env_remove("ENV")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let input = format!(
        "keep := saved\n/bash rmdir -- \"$PWD\"\nnext := value\n/cd \"{}\"\n/pwd\n/bindings\n/quit\n",
        directory.path().display()
    );
    child
        .stdin
        .take()
        .unwrap()
        .write_all(input.as_bytes())
        .unwrap();
    let output = child.wait_with_output().unwrap();
    let stdout = String::from_utf8(output.stdout).unwrap();
    let stderr = String::from_utf8(output.stderr).unwrap();
    assert!(output.status.success(), "stdout={stdout}\nstderr={stderr}");
    assert!(
        !work.exists(),
        "the child must actually remove the original cwd"
    );
    assert!(
        stderr.contains("cannot determine working directory"),
        "{stderr}"
    );
    assert!(
        stderr.contains("cause:") && stderr.contains("help:"),
        "{stderr}"
    );
    assert!(
        stdout
            .lines()
            .any(|line| line == directory.path().to_string_lossy()),
        "{stdout}"
    );
    assert!(stdout.contains("keep := \".\"/\"saved\""), "{stdout}");
    assert!(
        !stdout.contains("next :="),
        "a failed submission leaked a binding: {stdout}"
    );
    assert!(!stdout.contains('\x1b') && !stderr.contains('\x1b'));
}
