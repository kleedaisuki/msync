//! Finite remote operations through the default structured live runtime.
#![cfg(windows)]

use std::fs;
use std::path::Path;
use std::process::{Command, Output};

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{Options, RuntimeSession};

fn wsl(script: &str, args: &[&str]) -> Output {
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
    output
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
            .prefix("endpoint-v4-")
            .tempdir_in(scratch)
            .unwrap();
        // This is an exclusively owned remote journal, not an existing user tree.
        let remote = String::from_utf8(wsl("mktemp -d /tmp/msync-v4-test.XXXXXXXXXX", &[]).stdout)
            .unwrap()
            .trim()
            .to_owned();
        Self { local, remote }
    }

    fn path(&self) -> &Path {
        self.local.path()
    }

    async fn run(
        &self,
        text: &str,
    ) -> Result<msync::execution::ExecutionReport, msync::execution::ExecutionError> {
        let program = Session::new()
            .compile(
                &msync::language::parse(text).unwrap(),
                &CompileContext {
                    pwd: self.path().to_owned(),
                    source_file: None,
                },
            )
            .unwrap();
        RuntimeSession::new(&Options::new(self.path().join("scratch")))
            .unwrap()
            .execute(&program)
            .await
    }

    fn remote(&self, suffix: &str) -> String {
        format!("$wsl::\"{}/{suffix}\"", self.remote)
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        assert!(self.remote.starts_with("/tmp/msync-v4-test."));
        wsl(
            "chmod -R u+rwX -- \"$1\"; rm -rf -- \"$1\"",
            &[&self.remote],
        );
    }
}

#[tokio::test]
#[ignore = "explicit real WSL filesystem endpoint journey; no SSH configuration changes"]
async fn real_wsl_upload_download_replace_and_conflict_are_statement_atomic() {
    let fixture = Fixture::new();
    let tree = fixture.path().join("tree ' 可莉");
    fs::create_dir_all(tree.join("nested")).unwrap();
    fs::write(tree.join("nested/hello.txt"), "hello native WSL").unwrap();
    fs::write(tree.join("extra"), "extra").unwrap();
    let upload = format!(
        "$pwd / \"tree ' 可莉\" -> {}\n",
        fixture.remote("target ' 可莉")
    );
    fixture.run(&upload).await.unwrap();
    let readback = format!(
        "{} -> $pwd / \"download\"\n",
        fixture.remote("target ' 可莉")
    );
    fixture.run(&readback).await.unwrap();
    assert_eq!(
        fs::read_to_string(fixture.path().join("download/nested/hello.txt")).unwrap(),
        "hello native WSL"
    );

    fs::remove_file(tree.join("extra")).unwrap();
    fs::write(tree.join("nested/hello.txt"), "replacement").unwrap();
    fixture
        .run(&format!(
            "$pwd / \"tree ' 可莉\" -> {} /.\n",
            fixture.remote("target ' 可莉")
        ))
        .await
        .unwrap();
    let output = wsl(
        "test ! -e \"$1/extra\"; cat \"$1/nested/hello.txt\"",
        &[&format!("{}/target ' 可莉", fixture.remote)],
    );
    assert_eq!(output.stdout, b"replacement");
    assert!(
        fixture.run(&upload).await.is_err(),
        "an existing exact directory must be rejected"
    );
    assert!(
        fixture
            .run(&format!(
                "{} -> {}\n",
                fixture.remote("target ' 可莉"),
                fixture.remote("target ' 可莉")
            ))
            .await
            .is_err(),
        "remote aliases must not bypass self-copy protection"
    );
    fixture
        .run(&format!(
            "\"nested\" -> {}\n",
            fixture.remote("missing/parents/data")
        ))
        .await
        .unwrap();
    assert_eq!(
        wsl("cat \"$1/missing/parents/data\"", &[&fixture.remote]).stdout,
        b"nested"
    );

    fs::write(
        tree.join("aaa-new"),
        "must not publish before late conflict",
    )
    .unwrap();
    let conflict = fixture
        .run(&format!(
            "$pwd / \"tree ' 可莉\" -> {} /\n",
            fixture.remote("container")
        ))
        .await;
    conflict.unwrap();
    fs::write(tree.join("aaa-newer"), "must remain private").unwrap();
    assert!(
        fixture
            .run(&format!(
                "$pwd / \"tree ' 可莉\" -> {} /\n",
                fixture.remote("container")
            ))
            .await
            .is_err()
    );
    wsl(
        "test ! -e \"$1/container/tree ' 可莉/aaa-newer\"; test -z \"$(find \"$1\" -name '.msync-v4.*' -print -quit)\"",
        &[&fixture.remote],
    );
}

#[tokio::test]
#[ignore = "explicit WSL preflight and literal endpoint journey"]
async fn real_wsl_literal_overwrite_and_named_transaction_fail_before_effects() {
    let fixture = Fixture::new();
    fixture
        .run(&format!(
            "\"first\" -> {}\n\"second\" -> {}\n",
            fixture.remote("literal"),
            fixture.remote("literal")
        ))
        .await
        .unwrap();
    assert_eq!(
        wsl("cat \"$1/literal\"", &[&fixture.remote]).stdout,
        b"second"
    );
    let failure = fixture
        .run(&format!(
            "\"must not publish\" -> $pwd / \"before\"\ntransaction A:\n    \"unsafe\" -> {}\n",
            fixture.remote("named")
        ))
        .await;
    assert!(failure.is_err());
    assert!(!fixture.path().join("before").exists());
    wsl("test ! -e \"$1/named\"", &[&fixture.remote]);
}

#[tokio::test]
#[ignore = "explicit WSL COMMITTED versus cleanup-failure acknowledgement journey"]
async fn real_wsl_committed_receipt_survives_readonly_before_image_cleanup_failure() {
    let fixture = Fixture::new();
    fs::create_dir(fixture.path().join("replacement")).unwrap();
    fs::write(fixture.path().join("replacement/new"), "committed").unwrap();
    wsl(
        "mkdir -p \"$1/protected/locked\"; printf old > \"$1/protected/locked/old\"; chmod 555 \"$1/protected/locked\"",
        &[&fixture.remote],
    );
    let report = fixture
        .run(&format!(
            "$pwd / \"replacement\" -> {} /.\n",
            fixture.remote("protected")
        ))
        .await
        .unwrap();
    assert_eq!(
        wsl(
            "cat \"$1/protected/new\"; test ! -e \"$1/protected/locked\"",
            &[&fixture.remote]
        )
        .stdout,
        b"committed"
    );
    assert_eq!(
        report.cleanup.len(),
        1,
        "a known commit must preserve its cleanup warning"
    );
    assert!(
        report.cleanup[0]
            .cause
            .to_string()
            .contains("assignment committed")
    );
    assert!(!report.cleanup[0].resolved);
}

#[test]
fn http_destination_is_rejected_before_any_prior_statement_effect() {
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&fixture).unwrap();
    let root = tempfile::tempdir_in(fixture).unwrap();
    let source = "web => https://example.invalid\n\"never\" -> $pwd / \"before\"\n\"read only\" -> web::\"target\"\n";
    let errors = Session::new()
        .compile(
            &msync::language::parse(source).unwrap(),
            &CompileContext {
                pwd: root.path().to_owned(),
                source_file: None,
            },
        )
        .unwrap_err();
    assert!(
        errors
            .iter()
            .any(|error| error.diagnostic.message.contains("read-only")),
        "{errors:?}"
    );
    assert!(!root.path().join("before").exists());
}
