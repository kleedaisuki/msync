//! Empty executable units retain recovery without activating unused storage roots.
use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output};

use msync::filesystem::recovery::Registry;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("empty-recovery-v6-")
        .tempdir_in(root)
        .unwrap()
}

fn command(root: &Path) -> Command {
    let mut command = Command::new(env!("CARGO_BIN_EXE_msync"));
    command
        .current_dir(root)
        .env("HOME", root)
        .env("USERPROFILE", root)
        .env("XDG_CONFIG_HOME", root.join("config"))
        .env("APPDATA", root.join("config"))
        .env("NO_COLOR", "1")
        .env_remove("MSYNC_CONFIG")
        .env_remove("MSYNC_STATE_DIR")
        .env_remove("MSYNC_TRACE")
        .env_remove("MSYNC_TEST_CRASH_AT")
        .env_remove("MSYNC_TEST_CRASH_TARGET");
    command
}

fn success(output: Output) {
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

fn orphan(scratch: &Path) -> PathBuf {
    let registry = Registry::new(scratch).unwrap();
    let stage = scratch.join("execute-interrupted");
    fs::create_dir(&stage).unwrap();
    let registration = registry
        .enter()
        .unwrap()
        .register_stage(scratch, &stage)
        .unwrap();
    fs::write(stage.join("payload"), b"private interrupted bytes").unwrap();
    drop(registration);
    stage
}

#[test]
fn empty_source_object_and_ir_recover_fallback_with_an_unused_file_primary() {
    for format in ["source", "object", "ir"] {
        let root = workspace();
        fs::write(root.path().join(".temp"), b"ordinary user file").unwrap();
        fs::write(root.path().join("pure.msync"), "ready = true\n").unwrap();
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
        let fallback = root.path().join("fallback");
        let stage = orphan(&fallback);
        assert!(msync::execution::recovery_required(
            &root.path().join(".temp"),
            Some(&fallback)
        ));
        success(
            command(root.path())
                .args([action, input])
                .env("MSYNC_STATE_DIR", &fallback)
                .output()
                .unwrap(),
        );
        assert!(!stage.exists(), "{format} skipped fallback recovery");
        assert_eq!(
            fs::read(root.path().join(".temp")).unwrap(),
            b"ordinary user file"
        );
        assert!(
            fs::read_dir(fallback.join(".msync-recovery-v1"))
                .unwrap()
                .all(|entry| !entry
                    .unwrap()
                    .file_name()
                    .to_string_lossy()
                    .starts_with("record-"))
        );
    }
}

#[test]
fn empty_units_report_invalid_local_and_remote_registries_in_either_root() {
    for fallback in [false, true] {
        for registry in [".msync-recovery-v1", ".msync-remote-recovery-v1"] {
            let root = workspace();
            fs::write(
                root.path().join("pure.msync"),
                "if false:\n    \"unused\" -> output\n",
            )
            .unwrap();
            let scratch = root
                .path()
                .join(if fallback { "fallback" } else { ".temp" });
            fs::create_dir(&scratch).unwrap();
            let invalid = scratch.join(registry);
            fs::write(&invalid, b"not a registry directory").unwrap();
            let mut invocation = command(root.path());
            invocation.args(["run", "pure.msync"]);
            if fallback {
                invocation.env("MSYNC_STATE_DIR", &scratch);
            }
            let output = invocation.output().unwrap();
            assert!(
                !output.status.success(),
                "fallback={fallback} {registry} was ignored"
            );
            let diagnostic = String::from_utf8_lossy(&output.stderr);
            assert!(diagnostic.contains("recover"), "{diagnostic}");
            assert_eq!(fs::read(invalid).unwrap(), b"not a registry directory");
            assert!(!root.path().join("output").exists());
        }
    }
}

#[tokio::test]
async fn repeated_empty_admission_recovers_orphans_but_retains_active_owners() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    let mut runtime =
        msync::execution::RuntimeSession::new(&msync::execution::Options::new(&scratch)).unwrap();
    let empty = msync::hir::Program::default();
    runtime.execute(&empty).await.unwrap();
    assert!(!scratch.exists());
    let interrupted = orphan(&scratch);
    let registry = Registry::new(&scratch).unwrap();
    let active = scratch.join("execute-active");
    fs::create_dir(&active).unwrap();
    let registration = registry
        .enter()
        .unwrap()
        .register_stage(&scratch, &active)
        .unwrap();
    fs::write(active.join("payload"), b"active bytes").unwrap();
    assert!(runtime.recovery_required());
    runtime.execute(&empty).await.unwrap();
    assert!(!interrupted.exists());
    assert_eq!(fs::read(active.join("payload")).unwrap(), b"active bytes");
    runtime.shutdown().await.unwrap();
    drop(registration);
}
