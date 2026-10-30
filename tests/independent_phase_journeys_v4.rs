//! Independent phase boundaries, including an unused program macro in an object.

use std::fs;
use std::io::Read;
use std::path::Path;
use std::process::{Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use serde_json::Value;

fn workspace() -> tempfile::TempDir {
    let scratch = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&scratch).unwrap();
    tempfile::Builder::new()
        .prefix("independent-phase-")
        .tempdir_in(scratch)
        .unwrap()
}

fn read_output(mut reader: impl Read) -> Vec<u8> {
    let mut bytes = Vec::new();
    reader.read_to_end(&mut bytes).unwrap();
    bytes
}

fn invoke(root: &Path, arguments: &[&str]) -> Output {
    let image =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let mut child = Command::new(image)
        .current_dir(root)
        .args(arguments)
        .env("NO_COLOR", "1")
        .env("PATH", root)
        .env(
            "MSYNC_SANDBOXIE_HOME",
            root.join("unavailable-native-provider"),
        )
        .env("HOME", root.join("home"))
        .env("USERPROFILE", root.join("home"))
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let stdout = child.stdout.take().unwrap();
    let stderr = child.stderr.take().unwrap();
    let output = thread::spawn(move || read_output(stdout));
    let errors = thread::spawn(move || read_output(stderr));
    let deadline = Instant::now() + Duration::from_secs(20);
    let mut timed_out = false;
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        if Instant::now() >= deadline {
            timed_out = true;
            child.kill().unwrap();
            break child.wait().unwrap();
        }
        thread::sleep(Duration::from_millis(5));
    };
    let output = Output {
        status,
        stdout: output.join().unwrap(),
        stderr: errors.join().unwrap(),
    };
    assert!(
        !timed_out,
        "phase journey timed out: {arguments:?}\n{}",
        String::from_utf8_lossy(&output.stderr)
    );
    output
}

fn success(output: &Output) {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

fn records(text: &[u8]) -> Vec<Value> {
    std::str::from_utf8(text)
        .unwrap()
        .lines()
        .filter(|line| !line.trim().is_empty())
        .map(|line| serde_json::from_str(line).unwrap())
        .collect()
}

fn file_records(root: &Path, name: &str) -> Vec<Value> {
    records(&fs::read(root.join(name)).unwrap())
}

fn diagnostic<'a>(trace: &'a [Value], phase: &str) -> &'a Value {
    trace
        .iter()
        .find(|record| record["name"] == "diagnostic" && record["attributes"]["phase"] == phase)
        .unwrap_or_else(|| panic!("missing diagnostic phase {phase}: {trace:?}"))
}

#[test]
fn an_unused_program_macro_cannot_import_a_provider_into_source_or_exported_execution() {
    let root = workspace();
    fs::write(
        root.path().join("code.msync"),
        "macro never_called():\n    p => $path::msync_phase_test_missing_executable()\n    p::stdout -> must-not-exist\n\n\"literal only\" -> result\n",
    )
    .unwrap();
    success(&invoke(root.path(), &["check", "code.msync"]));
    success(&invoke(
        root.path(),
        &["compile", "code.msync", "-o", "module.mobj"],
    ));
    assert!(!root.path().join("result").exists());
    assert!(!root.path().join(".temp").exists());
    for (input, trace) in [
        ("code.msync", "source.jsonl"),
        ("module.mobj", "object.jsonl"),
    ] {
        let output = invoke(root.path(), &["run", input, "--trace", trace]);
        success(&output);
        assert!(
            output.stderr.is_empty(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(
            fs::read(root.path().join("result")).unwrap(),
            b"literal only"
        );
        assert!(!root.path().join("must-not-exist").exists());
        for record in file_records(root.path(), trace) {
            assert_ne!(record["name"], "transport_process", "{record}");
            assert_ne!(record["name"], "transaction.publish", "{record}");
        }
    }
}

#[test]
fn a_macro_operand_failure_is_execution_while_its_unknown_namespace_is_frontend() {
    let root = workspace();
    let source =
        "macro copy(src):\n    src -> $pwd::pwd / \"result\"\n\ncopy($pwd::pwd / \"missing\")\n";
    fs::write(root.path().join("code.msync"), source).unwrap();
    success(&invoke(root.path(), &["check", "code.msync"]));
    assert!(!root.path().join(".temp").exists());
    let failed = invoke(
        root.path(),
        &["run", "code.msync", "--trace", "runtime.jsonl"],
    );
    assert!(!failed.status.success());
    let rendered = String::from_utf8_lossy(&failed.stderr);
    assert!(rendered.starts_with("execution error:"), "{rendered}");
    assert!(rendered.contains("code.msync:4:"), "{rendered}");
    assert!(!rendered.contains("code.msync:1:1"), "{rendered}");
    let trace = file_records(root.path(), "runtime.jsonl");
    let failure = diagnostic(&trace, "execute");
    let operand = "\"missing\"";
    let start = source.find(operand).unwrap() as u64;
    assert_eq!(failure["metrics"]["span.start"].as_u64(), Some(start));
    assert_eq!(
        failure["metrics"]["span.end"].as_u64(),
        Some(start + operand.len() as u64)
    );
    assert!(!root.path().join("result").exists());

    let source =
        "macro copy(src):\n    src -> $pwd::pwd / \"result\"\n\ncopy(unknown_namespace::missing)\n";
    fs::write(root.path().join("code.msync"), source).unwrap();
    let failed = invoke(
        root.path(),
        &["check", "code.msync", "--trace", "frontend.jsonl"],
    );
    assert!(!failed.status.success());
    let trace = file_records(root.path(), "frontend.jsonl");
    let failure = diagnostic(&trace, "frontend");
    assert!(
        failure["metrics"]["span.start"].as_u64().unwrap()
            >= source.find("copy(unknown_namespace").unwrap() as u64
    );
    assert!(!trace.iter().any(|record| record["name"] == "execute"));
    assert!(!root.path().join("result").exists());
}

#[test]
fn a_called_program_macro_runs_source_and_object_without_an_isolation_provider() {
    let root = workspace();
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let executable = executable.to_string_lossy().replace('\\', "\\\\");
    fs::write(
        root.path().join("code.msync"),
        format!("macro launch():\n    p => $local::\"{executable}\"(\"--version\")\n    p::exit_code -> code\nlaunch()\n"),
    )
    .unwrap();
    success(&invoke(
        root.path(),
        &["compile", "code.msync", "-o", "launch.mobj"],
    ));
    assert!(!root.path().join(".temp").exists());
    for input in ["code.msync", "launch.mobj"] {
        let output = invoke(root.path(), &["run", input]);
        success(&output);
        assert_eq!(
            fs::read_to_string(root.path().join("code")).unwrap().trim(),
            "0"
        );
        let error = String::from_utf8_lossy(&output.stderr);
        assert!(!error.contains("Sandboxie"), "{error}");
        assert!(!error.contains("binding error:"), "{error}");
    }
}
#[test]
fn successful_stderr_telemetry_does_not_acquire_a_human_runtime_banner() {
    let root = workspace();
    let payload = "phase-test-private-literal";
    fs::write(
        root.path().join("code.msync"),
        format!("\"{payload}\" -> result\n"),
    )
    .unwrap();
    success(&invoke(
        root.path(),
        &["compile", "code.msync", "-o", "telemetry.mobj"],
    ));
    for input in ["code.msync", "telemetry.mobj"] {
        let output = invoke(root.path(), &["run", input, "--trace", "-"]);
        success(&output);
        assert_eq!(
            fs::read(root.path().join("result")).unwrap(),
            payload.as_bytes()
        );
        let trace = records(&output.stderr);
        assert!(!trace.is_empty());
        assert!(trace.iter().any(|record| record["name"] == "execute"));
        assert!(
            !output
                .stderr
                .windows(payload.len())
                .any(|window| window == payload.as_bytes())
        );
        assert!(!output.stderr.contains(&0x1b));
        assert!(
            !output
                .stderr
                .windows("working:".len())
                .any(|window| window == b"working:")
        );
    }
}
