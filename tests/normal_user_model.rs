//! End-to-end acceptance for retryable transfers without persistent recovery.

use std::fs;
use std::io::Write;
use std::path::Path;
use std::process::{Command, Output, Stdio};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("normal-user-")
        .tempdir_in(root)
        .unwrap()
}

fn command(root: &Path) -> Command {
    let mut command = Command::new(env!("CARGO_BIN_EXE_msync"));
    command
        .current_dir(root)
        .arg("--no-config")
        .env("NO_COLOR", "1")
        .env_remove("MSYNC_CONFIG")
        .env_remove("MSYNC_STATE_DIR")
        .env_remove("MSYNC_TRACE")
        .env_remove("MSYNC_TEST_CRASH_AT")
        .env_remove("MSYNC_TEST_CRASH_TARGET");
    command
}

fn run(root: &Path, source: &str) -> Output {
    let mut child = command(root)
        .args(["run", "-"])
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

fn success(output: Output) {
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

fn assert_no_recovery_protocol(path: &Path) {
    if !path.is_dir() {
        return;
    }
    for entry in fs::read_dir(path).unwrap() {
        let entry = entry.unwrap();
        let name = entry.file_name();
        let name = name.to_string_lossy();
        assert!(
            name != ".msync-recovery-v1"
                && name != ".msync-remote-recovery-v1"
                && !name.starts_with(".msync-statement-")
                && !name.starts_with(".msync-v4."),
            "normal transfer created legacy protocol state: {}",
            entry.path().display()
        );
        if entry.file_type().unwrap().is_dir() {
            assert_no_recovery_protocol(&entry.path());
        }
    }
}

#[test]
fn ordinary_copy_overwrite_and_literal_use_no_persistent_protocol() {
    let root = workspace();
    fs::write(root.path().join("source"), b"normal bytes\0\n").unwrap();
    fs::write(root.path().join("target"), b"old target").unwrap();
    success(run(root.path(), "source -> target\n"));
    assert_eq!(
        fs::read(root.path().join("target")).unwrap(),
        b"normal bytes\0\n"
    );
    success(run(root.path(), "\"literal bytes\" -> target\n"));
    assert_eq!(
        fs::read(root.path().join("target")).unwrap(),
        b"literal bytes"
    );
    assert_eq!(
        fs::read(root.path().join("source")).unwrap(),
        b"normal bytes\0\n"
    );
    assert_no_recovery_protocol(root.path());
}

#[test]
fn legacy_registries_are_not_execution_prerequisites_or_cleanup_targets() {
    for format in ["source", "object", "ir"] {
        let root = workspace();
        let scratch = root.path().join(".temp");
        let fallback = root.path().join("fallback");
        for parent in [&scratch, &fallback] {
            fs::create_dir(parent).unwrap();
            for name in [".msync-recovery-v1", ".msync-remote-recovery-v1"] {
                let legacy = parent.join(name);
                fs::create_dir(&legacy).unwrap();
                #[cfg(unix)]
                {
                    use std::os::unix::fs::PermissionsExt;
                    fs::set_permissions(&legacy, fs::Permissions::from_mode(0o750)).unwrap();
                }
                fs::write(
                    legacy.join("unrelated-record"),
                    b"legacy state: leave alone",
                )
                .unwrap();
            }
        }
        fs::write(root.path().join("pure.msync"), "value = true\n").unwrap();
        let (action, input) = match format {
            "object" => {
                success(
                    command(root.path())
                        .args(["compile", "pure.msync", "-o", "pure.mobj"])
                        .output()
                        .unwrap(),
                );
                ("run", "pure.mobj")
            }
            "ir" => {
                success(
                    command(root.path())
                        .args(["emit-ir", "pure.msync", "-o", "pure.json"])
                        .output()
                        .unwrap(),
                );
                ("run-ir", "pure.json")
            }
            _ => ("run", "pure.msync"),
        };
        success(
            command(root.path())
                .args([action, input])
                .env("MSYNC_STATE_DIR", &fallback)
                .output()
                .unwrap(),
        );
        fs::write(root.path().join("source"), b"retryable source").unwrap();
        success(run(root.path(), "source -> target\n"));
        assert_eq!(
            fs::read(root.path().join("target")).unwrap(),
            b"retryable source"
        );
        for parent in [&scratch, &fallback] {
            for name in [".msync-recovery-v1", ".msync-remote-recovery-v1"] {
                assert_eq!(
                    fs::read(parent.join(name).join("unrelated-record")).unwrap(),
                    b"legacy state: leave alone",
                    "{format}"
                );
            }
        }
    }
}

#[test]
fn a_failed_transfer_can_be_retried_without_recovery_ritual() {
    let root = workspace();
    fs::write(root.path().join("target"), b"previous bytes").unwrap();
    let failed = run(root.path(), "not-created -> target\n");
    assert!(!failed.status.success());
    assert_eq!(
        fs::read(root.path().join("target")).unwrap(),
        b"previous bytes"
    );
    fs::write(root.path().join("not-created"), b"new bytes").unwrap();
    success(run(root.path(), "not-created -> target\n"));
    assert_eq!(fs::read(root.path().join("target")).unwrap(), b"new bytes");
    assert_no_recovery_protocol(root.path());
}

#[cfg(unix)]
#[test]
fn selected_workspace_alias_and_recursive_link_entries_are_normal_paths() {
    use std::os::unix::fs::{PermissionsExt, symlink};

    let root = workspace();
    fs::create_dir(root.path().join("real")).unwrap();
    fs::write(root.path().join("real/program"), b"program bytes").unwrap();
    fs::set_permissions(
        root.path().join("real/program"),
        fs::Permissions::from_mode(0o751),
    )
    .unwrap();
    symlink("real", root.path().join("alias")).unwrap();
    success(run(root.path(), "alias/program -> executable\n"));
    assert_eq!(
        fs::read(root.path().join("executable")).unwrap(),
        b"program bytes"
    );
    assert_eq!(
        fs::metadata(root.path().join("executable"))
            .unwrap()
            .permissions()
            .mode()
            & 0o777,
        0o751
    );
    symlink("program", root.path().join("real/relative-link")).unwrap();
    symlink("not-created", root.path().join("real/dangling-link")).unwrap();
    success(run(root.path(), "real -> copied/.\n"));
    assert_eq!(
        fs::read_link(root.path().join("copied/relative-link")).unwrap(),
        Path::new("program")
    );
    assert_eq!(
        fs::read_link(root.path().join("copied/dangling-link")).unwrap(),
        Path::new("not-created")
    );
    assert_eq!(
        fs::read(root.path().join("copied/program")).unwrap(),
        b"program bytes"
    );
    assert_no_recovery_protocol(root.path());
}
