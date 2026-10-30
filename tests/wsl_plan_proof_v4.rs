//! Real metadata evidence, not an injected regular-file premise.
#![cfg(windows)]

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{Options, RuntimeSession};
use msync::hir::InstructionKind;
use msync::plan::FileClass;

fn wsl(script: &str, args: &[&str]) -> String {
    let output = Command::new("wsl.exe")
        .env("WSL_UTF8", "1")
        .args(["--exec", "sh", "-c", script, "sh"])
        .args(args)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout).unwrap()
}

struct Fixture {
    local: tempfile::TempDir,
    remote: String,
}

impl Fixture {
    fn new() -> Self {
        let scratch = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&scratch).unwrap();
        let local = tempfile::Builder::new()
            .prefix("wsl-plan-proof-")
            .tempdir_in(scratch)
            .unwrap();
        let remote = wsl(
            "d=$(mktemp -d \"$HOME/.msync-plan-proof.XXXXXXXXXX\"); printf 'wsl final bytes' > \"$d/source\"; printf '%s' \"$d\"",
            &[],
        );
        Self { local, remote }
    }

    fn prepare(&self, cwd: &Path, script: &str) -> msync::preparation::PreparedProgram {
        let program = Session::new()
            .compile(
                &msync::language::parse(script).unwrap(),
                &CompileContext {
                    pwd: cwd.to_owned(),
                    source_file: None,
                },
            )
            .unwrap();
        msync::preparation::prepare(&program, true).unwrap()
    }

    fn work(&self, name: &str) -> PathBuf {
        let path = self.local.path().join(name);
        fs::create_dir(&path).unwrap();
        fs::write(path.join("native"), b"native first bytes").unwrap();
        path
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        assert!(self.remote.contains("/.msync-plan-proof."));
        wsl(
            "case $1 in \"$HOME\"/.msync-plan-proof.*) rm -f -- \"$1/source\"; rmdir -- \"$1\";; *) exit 1;; esac",
            &[&self.remote],
        );
    }
}

#[tokio::test]
#[ignore = "explicit actual Windows NTFS/ReFS and native WSL filesystem metadata/transfer journey"]
async fn actual_wsl_namespace_evidence_fuses_existing_and_missing_native_targets() {
    let fixture = Fixture::new();
    assert_eq!(
        wsl("stat -f -c %t -- \"$1/source\"", &[&fixture.remote]).trim(),
        "ef53"
    );
    let remote_leaf = fixture.remote.rsplit('/').next().unwrap();
    for existing in [false, true] {
        let cwd = fixture.work(if existing { "existing" } else { "missing" });
        if existing {
            fs::write(cwd.join("out"), b"old output").unwrap();
        }
        let script = format!("native -> out\n$wsl::\"~/{remote_leaf}/source\" -> out\n");
        let plan = fixture.prepare(&cwd, &script);
        assert_eq!(plan.original.body.instructions.len(), 2);
        assert_eq!(plan.program.body.instructions.len(), 1, "{plan}");
        plan.verify().unwrap();
        let mut runtime =
            RuntimeSession::new(&Options::new(fixture.local.path().join(if existing {
                "scratch-existing"
            } else {
                "scratch-missing"
            })))
            .unwrap();
        let result = runtime.execute_prepared(&plan).await.unwrap();
        assert_eq!(result.assignments, 1);
        assert_eq!(fs::read(cwd.join("out")).unwrap(), b"wsl final bytes");
        runtime.shutdown().await.unwrap();
    }
    let cwd = fixture.work("cli");
    let home = fixture.local.path().join("native-home");
    fs::create_dir_all(home.join(remote_leaf)).unwrap();
    fs::write(home.join(remote_leaf).join("source"), b"native first bytes").unwrap();
    let script = cwd.join("copy.msync");
    fs::write(&script, format!("config_file := ~/{remote_leaf}/source\ndes := $pwd::pwd\nconfig_file -> des / \"cli-out\"\n$wsl::config_file -> des / \"cli-out\"\n")).unwrap();
    let invoke = |command: &str| {
        Command::new(env!("CARGO_BIN_EXE_msync"))
            .current_dir(&cwd)
            .env("HOME", &home)
            .env("USERPROFILE", &home)
            .env_remove("WSLENV")
            .args([command, "copy.msync", "--color", "never"])
            .output()
            .unwrap()
    };
    let preview = invoke("plan");
    assert!(
        preview.status.success(),
        "{}",
        String::from_utf8_lossy(&preview.stderr)
    );
    let preview = String::from_utf8(preview.stdout).unwrap();
    assert_eq!(
        preview
            .lines()
            .filter(|line| line.ends_with(" transfer"))
            .count(),
        2,
        "{preview}"
    );
    assert!(preview.contains("Static IR"), "{preview}");
    assert!(!cwd.join("cli-out").exists());
    let output = invoke("run");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(fs::read(cwd.join("cli-out")).unwrap(), b"wsl final bytes");
}

#[test]
#[ignore = "explicit actual Windows/WSL projection and opaque-write metadata controls"]
fn wsl_drive_projection_and_opaque_program_remain_unverified() {
    let fixture = Fixture::new();
    let cwd = fixture.work("projection");
    let projected = wsl(
        "wslpath -a -u \"$1\"",
        &[cwd.join("native").to_str().unwrap()],
    );
    let plan = fixture.prepare(
        &cwd,
        &format!("native -> out\n$wsl::\"{}\" -> out\n", projected.trim()),
    );
    assert_eq!(plan.program.body.instructions.len(), 2, "{plan}");
    assert_eq!(
        plan.facts[&plan.program.body.instructions[1].id].source,
        FileClass::Unknown
    );
    assert!(!plan.graph.dependencies.is_empty());
    assert!(!cwd.join("out").exists());
    let executable = std::env::current_exe()
        .unwrap()
        .to_string_lossy()
        .replace('\\', "\\\\");
    let plan = fixture.prepare(
        &cwd,
        &format!(
            "p => $local::\"{executable}\"()\n\"first\" -> out\n$wsl::\"{}/source\" -> out\n",
            fixture.remote
        ),
    );
    assert!(matches!(
        plan.program.body.instructions[0].kind,
        InstructionKind::Spawn { .. }
    ));
    assert_eq!(plan.program.body.instructions.len(), 3);
    assert_eq!(
        plan.facts[&plan.program.body.instructions[2].id].source,
        FileClass::Unknown
    );
    assert!(!cwd.join("out").exists());
}
