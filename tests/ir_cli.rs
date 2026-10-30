use std::fs;
use std::io::Write;
use std::path::Path;
use std::process::{Command, Output, Stdio};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("ir-cli-")
        .tempdir_in(root)
        .unwrap()
}

fn run(root: &Path, args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root)
        .args(args)
        .output()
        .unwrap()
}

fn success(output: Output) -> Output {
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    output
}

#[test]
fn export_relocate_bind_and_execute_without_the_original_script() {
    let root = workspace();
    let first = root.path().join("first");
    let second = root.path().join("second");
    fs::create_dir(&first).unwrap();
    fs::create_dir(&second).unwrap();
    fs::write(first.join("script.msync"), "$pwd::pwd/source -> output\n").unwrap();
    fs::write(second.join("source"), b"relocated fresh source").unwrap();
    success(run(
        &first,
        &["emit-ir", "script.msync", "-o", "../portable.json"],
    ));
    fs::remove_file(first.join("script.msync")).unwrap();
    success(run(
        &second,
        &["bind-ir", "../portable.json", "-o", "../bound.json"],
    ));
    success(run(&second, &["plan-ir", "../bound.json"]));
    success(run(&first, &["run-ir", "../bound.json", "--dry-run"]));
    assert!(!second.join("output").exists());
    success(run(&first, &["run-ir", "../bound.json"]));
    assert_eq!(
        fs::read(second.join("output")).unwrap(),
        b"relocated fresh source"
    );
    assert!(!first.join("output").exists());
}

#[test]
fn portable_file_context_is_explicit_and_contextual_emit_uses_named_source() {
    let root = workspace();
    fs::write(
        root.path().join("script.msync"),
        "$file::this -> copied.msync\n",
    )
    .unwrap();
    success(run(
        root.path(),
        &["emit-ir", "script.msync", "-o", "portable.json"],
    ));
    assert!(
        !run(root.path(), &["run-ir", "portable.json"])
            .status
            .success()
    );
    success(run(
        root.path(),
        &["run-ir", "portable.json", "--source-file", "script.msync"],
    ));
    assert_eq!(
        fs::read(root.path().join("copied.msync")).unwrap(),
        fs::read(root.path().join("script.msync")).unwrap()
    );
    success(run(
        root.path(),
        &[
            "emit-ir",
            "script.msync",
            "--ir-stage",
            "contextual",
            "-o",
            "bound.json",
        ],
    ));
    success(run(root.path(), &["run-ir", "bound.json"]));
}

#[test]
fn repl_exports_captured_and_relocatable_transfers_then_reuses_them() {
    let root = workspace();
    fs::create_dir(root.path().join("later")).unwrap();
    fs::write(root.path().join("source"), b"original").unwrap();
    fs::write(root.path().join("later/source"), b"later").unwrap();
    let input = b"/dry-run on\nbase := $pwd::pwd\nbase/source -> $pwd::pwd/output\n/export-ir bound.json\n/export-relocatable portable.json\n/cd later\n/dry-run off\n/load-ir ../bound.json\n/load-ir ../portable.json\n/quit\n";
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root.path())
        .arg("repl")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child.stdin.take().unwrap().write_all(input).unwrap();
    let output = success(child.wait_with_output().unwrap());
    assert!(
        output.stderr.is_empty(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(fs::read(root.path().join("output")).unwrap(), b"original");
    // A path value captured in an earlier submission stays captured even in
    // relocatable IR; only the current submission's $pwd root relocates.
    assert_eq!(
        fs::read(root.path().join("later/output")).unwrap(),
        b"original"
    );
}

#[test]
fn invalid_ir_options_do_not_create_outputs_and_machine_json_is_uncolored() {
    let root = workspace();
    fs::write(root.path().join("source.msync"), "input -> output\n").unwrap();
    for args in [
        vec![
            "emit-ir",
            "source.msync",
            "--ir-stage",
            "bad",
            "-o",
            "bad.json",
        ],
        vec!["emit-ir", "source.msync", "--dry-run", "-o", "bad.json"],
        vec!["check", "source.msync", "--output", "bad.json"],
    ] {
        assert!(!run(root.path(), &args).status.success());
    }
    assert!(!root.path().join("bad.json").exists());
    let output = success(run(
        root.path(),
        &["emit-ir", "source.msync", "--color", "always"],
    ));
    assert!(!output.stdout.contains(&0x1b));
    msync::ir::Artifact::from_json(std::str::from_utf8(&output.stdout).unwrap()).unwrap();
}

#[test]
fn repl_contextual_export_keeps_relative_path_lookup_under_original_cwd() {
    let root = workspace();
    fs::create_dir(root.path().join("bin")).unwrap();
    fs::create_dir(root.path().join("later")).unwrap();
    fs::write(root.path().join("bin/tool"), b"original lookup").unwrap();
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root.path())
        .arg("repl")
        .env("PATH", "bin")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child.stdin.take().unwrap().write_all(
        b"/dry-run on\n$path::tool -> output\n/cd later\n/export-ir ../bound.json\n/dry-run off\n/load-ir ../bound.json\n/quit\n"
    ).unwrap();
    let output = success(child.wait_with_output().unwrap());
    assert!(
        output.stderr.is_empty(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        fs::read(root.path().join("output")).unwrap(),
        b"original lookup"
    );
    assert!(!root.path().join("later/output").exists());
}

#[test]
fn repl_exports_interpreter_snapshot_even_after_a_path_source_disappears() {
    use std::io::{BufRead, BufReader};
    let root = workspace();
    fs::create_dir(root.path().join("bin")).unwrap();
    fs::write(root.path().join("bin/tool"), b"resolved at submission").unwrap();
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root.path())
        .arg("repl")
        .env("PATH", "bin")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let mut input = child.stdin.take().unwrap();
    input.write_all(b"$path::tool -> output\n").unwrap();
    let mut output = BufReader::new(child.stdout.take().unwrap());
    loop {
        let mut line = String::new();
        assert_ne!(output.read_line(&mut line).unwrap(), 0);
        if line.starts_with("done:") {
            break;
        }
    }
    fs::remove_file(root.path().join("bin/tool")).unwrap();
    input.write_all(b"/export-ir bound.json\n/quit\n").unwrap();
    drop(input);
    let result = success(child.wait_with_output().unwrap());
    assert!(
        result.stderr.is_empty(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let artifact = msync::ir::Artifact::from_json(
        &fs::read_to_string(root.path().join("bound.json")).unwrap(),
    )
    .unwrap();
    let bound = artifact
        .bind(&msync::compiler::CompileContext {
            pwd: root.path().to_owned(),
            source_file: None,
        })
        .unwrap();
    assert_eq!(
        bound.program().operations[0].source.backend,
        msync::compiler::Backend::Local
    );
    assert!(bound.program().operations[0].source.path.ends_with("tool"));
}

#[test]
fn repl_artifact_writes_wait_for_background_work_to_stop() {
    let root = workspace();
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let input = format!(
        "wire => tcp://{address}\nwire::/ -> output\n/export-ir denied.json\n/stop\n/export-ir allowed.json\n/quit\n"
    );
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root.path())
        .arg("repl")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(input.as_bytes())
        .unwrap();
    let result = success(child.wait_with_output().unwrap());
    assert!(String::from_utf8_lossy(&result.stderr).contains("use /wait or /stop"));
    assert!(!root.path().join("denied.json").exists());
    assert!(!root.path().join("output").exists());
    let json = fs::read_to_string(root.path().join("allowed.json")).unwrap();
    msync::ir::Artifact::from_json(&json).unwrap();
}
