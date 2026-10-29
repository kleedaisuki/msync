//! Actual process launches need no transaction provider; failures retain their runtime phase.

use std::fs;
use std::path::Path;
use std::process::{Command, Output};

use msync::compiler::CompileContext;
use msync::compiler::structured::Session;
use msync::execution::{Options, RuntimeSession};

fn workspace() -> tempfile::TempDir {
    let scratch = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&scratch).unwrap();
    tempfile::Builder::new()
        .prefix("runtime-diagnostic-")
        .tempdir_in(scratch)
        .unwrap()
}

fn invoke(root: &Path, arguments: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root)
        .args(arguments)
        .env("NO_COLOR", "1")
        .env("MSYNC_SANDBOXIE_HOME", root.join("missing-backend"))
        .env("HOME", root.join("home"))
        .env("USERPROFILE", root.join("home"))
        .output()
        .unwrap()
}

fn literal_path(path: &Path) -> String {
    path.to_string_lossy()
        .replace('\\', "\\\\")
        .replace('"', "\\\"")
}

#[test]
fn a_file_macro_has_no_program_runtime_dependency() {
    let root = workspace();
    fs::create_dir_all(root.path().join("home/.codex/agents")).unwrap();
    fs::write(root.path().join("home/.codex/config.toml"), "configuration").unwrap();
    fs::write(root.path().join("home/.codex/agents/a.toml"), "agent").unwrap();
    let source = "macro copy_codex_config(codex, des):\n    codex/config.toml -> des/config.toml\n    codex/agents -> des/agents/.\ncodex := ~/.codex\ncopy_codex_config($local::codex, $pwd::local)\ncopy_codex_config($local::codex, $pwd::second)\n";
    fs::write(root.path().join("code.msync"), source).unwrap();
    for command in ["compile", "run"] {
        let output = invoke(root.path(), &[command, "code.msync"]);
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert!(!String::from_utf8_lossy(&output.stderr).contains("Sandboxie"));
    }
    for directory in ["local", "second"] {
        assert_eq!(
            fs::read_to_string(root.path().join(directory).join("config.toml")).unwrap(),
            "configuration"
        );
        assert_eq!(
            fs::read_to_string(root.path().join(directory).join("agents/a.toml")).unwrap(),
            "agent"
        );
    }
}

#[tokio::test]
async fn an_embedding_program_launch_requires_neither_worker_nor_isolation_provider() {
    let root = workspace();
    let executable = literal_path(Path::new(env!("CARGO_BIN_EXE_msync")));
    let source = format!(
        "macro unused(src, des):\n    src -> des\n\nprogram => $local::\"{executable}\"(\"--version\")\nprogram::stdout -> version\nprogram::exit_code -> result\n"
    );
    let context = CompileContext {
        pwd: root.path().to_owned(),
        source_file: None,
    };
    let compilation =
        msync::pipeline::compile(&mut Session::new(), &source, &context, &[]).unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(root.path().join(".temp"))).unwrap();
    runtime.set_origins(compilation.origins.clone());
    runtime.execute(&compilation.program).await.unwrap();
    runtime.shutdown().await.unwrap();
    assert_eq!(fs::read(root.path().join("result")).unwrap(), b"0\n");
    let version = fs::read_to_string(root.path().join("version")).unwrap();
    assert!(version.starts_with("msync "), "{version}");
}

#[test]
fn a_program_macro_executes_without_a_native_transaction_backend() {
    let root = workspace();
    let executable = literal_path(Path::new(env!("CARGO_BIN_EXE_msync")));
    fs::write(
        root.path().join("code.msync"),
        format!("macro launch():\n    program => $local::\"{executable}\"(\"--version\")\n    program::exit_code -> result\nlaunch()\n"),
    )
    .unwrap();
    for command in ["compile", "run"] {
        let output = invoke(root.path(), &[command, "code.msync"]);
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert!(!String::from_utf8_lossy(&output.stderr).contains("Sandboxie"));
    }
    assert_eq!(fs::read(root.path().join("result")).unwrap(), b"0\n");
}

#[test]
fn a_missing_executable_is_a_runtime_failure_at_the_macro_call() {
    let root = workspace();
    let executable = literal_path(&root.path().join("no-such-executable"));
    fs::write(
        root.path().join("code.msync"),
        format!("macro launch():\n    program => $local::\"{executable}\"()\n\nlaunch()\n"),
    )
    .unwrap();
    let checked = invoke(root.path(), &["check", "code.msync"]);
    assert!(
        checked.status.success(),
        "{}",
        String::from_utf8_lossy(&checked.stderr)
    );
    let output = invoke(root.path(), &["run", "code.msync"]);
    assert!(!output.status.success());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(error.starts_with("execution error:"), "{error}");
    assert!(error.contains("code.msync:4:"), "{error}");
    assert!(!error.contains("code.msync:1:1"), "{error}");
    assert!(error.contains("no-such-executable"), "{error}");
    assert!(error.contains("Cause:"), "{error}");
    assert!(!error.contains("Sandboxie"), "{error}");
}
