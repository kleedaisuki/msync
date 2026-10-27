//! Real default-v0.4 command and persistent REPL journeys.

use std::fs;
use std::io::Write;
use std::path::Path;
use std::process::{Command, Output, Stdio};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("cli-structured-")
        .tempdir_in(root)
        .unwrap()
}

fn invoke(root: &Path, arguments: &[&str], source: &str) -> Output {
    let mut process = Command::new(env!("CARGO_BIN_EXE_msync"))
        .args(arguments)
        .current_dir(root)
        .env("NO_COLOR", "1")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    process
        .stdin
        .take()
        .unwrap()
        .write_all(source.as_bytes())
        .unwrap();
    process.wait_with_output().unwrap()
}

fn success(output: &Output) {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(
        output.stderr.is_empty(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
}

#[test]
fn default_quotes_are_data_and_explicit_join_produces_a_path_without_existence_guessing() {
    let root = workspace();
    fs::write(root.path().join("file name"), "actual file payload").unwrap();
    let output = invoke(
        root.path(),
        &["run", "-"],
        "text := \"file name\"\ntext -> literal.txt\n$pwd / \"file name\" -> copied.txt\n\"  可莉  \" -> whitespace.txt\n",
    );
    success(&output);
    assert_eq!(
        fs::read(root.path().join("literal.txt")).unwrap(),
        b"file name"
    );
    assert_eq!(
        fs::read_to_string(root.path().join("copied.txt")).unwrap(),
        "actual file payload"
    );
    assert_eq!(
        fs::read_to_string(root.path().join("whitespace.txt")).unwrap(),
        "  可莉  "
    );
    let legacy = invoke(
        root.path(),
        &["--syntax", "0.3", "run", "-"],
        "\"file name\" -> legacy.txt\n",
    );
    success(&legacy);
    assert_eq!(
        fs::read_to_string(root.path().join("legacy.txt")).unwrap(),
        "actual file payload"
    );
}

#[test]
fn macro_check_and_plan_are_pure_then_explicit_directory_replacement_executes() {
    let root = workspace();
    fs::create_dir_all(root.path().join("source/agents")).unwrap();
    fs::create_dir_all(root.path().join("target/agents")).unwrap();
    fs::write(root.path().join("source/agents/new"), "new").unwrap();
    fs::write(root.path().join("target/agents/old"), "old").unwrap();
    let source = "macro copy(src, des):\n    src / \"agents\" -> des / \"agents\" / .\ncopy($pwd / \"source\", $pwd / \"target\")\n";
    success(&invoke(root.path(), &["check", "-"], source));
    let plan = invoke(root.path(), &["plan", "-"], source);
    success(&plan);
    assert!(String::from_utf8_lossy(&plan.stdout).contains("ReplaceTree"));
    assert_eq!(
        fs::read_to_string(root.path().join("target/agents/old")).unwrap(),
        "old"
    );
    assert!(!root.path().join(".temp").exists());
    success(&invoke(root.path(), &["run", "-"], source));
    assert_eq!(
        fs::read_to_string(root.path().join("target/agents/new")).unwrap(),
        "new"
    );
    assert!(!root.path().join("target/agents/old").exists());
    assert!(!root.path().join("target/agents/agents").exists());
}

#[test]
fn actual_repl_retains_macros_and_named_origins_across_separate_submissions() {
    let root = workspace();
    let source = "macro emit(value):\n    value -> result.txt\n\nemit(\"first\")\ntransaction A:\n    \"second\" -> result.txt\n    commit\n\nrollback A\n/bindings\n/quit\n";
    let output = invoke(root.path(), &["repl"], source);
    success(&output);
    assert_eq!(
        fs::read_to_string(root.path().join("result.txt")).unwrap(),
        "first"
    );
    assert!(String::from_utf8_lossy(&output.stdout).contains("macro emit(value)"));
    assert!(
        fs::read_dir(root.path().join(".temp"))
            .unwrap()
            .next()
            .is_none()
    );
}

#[test]
fn structured_format_edits_the_script_and_stdout_preview_remains_machine_text() {
    let root = workspace();
    let source = "macro copy(x):\n\tx->./\"output name\"\ncopy(\"value\")\n";
    fs::write(root.path().join("script.msync"), source).unwrap();
    success(&invoke(root.path(), &["fmt", "script.msync"], ""));
    let formatted = fs::read_to_string(root.path().join("script.msync")).unwrap();
    assert!(formatted.contains("    x ->"));
    assert!(!root.path().join("output name").exists());
    let preview = invoke(
        root.path(),
        &["fmt", "script.msync", "--stdout", "--color", "always"],
        "",
    );
    success(&preview);
    assert_eq!(preview.stdout, formatted.as_bytes());
    assert!(!preview.stdout.contains(&0x1b));
}

#[test]
fn a_quoted_readonly_destination_has_a_token_diagnostic_and_never_writes() {
    let root = workspace();
    let source = "\"value\" -> \"not a path\"\n";
    let output = invoke(root.path(), &["run", "-", "--color", "always"], source);
    assert!(!output.status.success());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(error.contains("not a path"), "{error}");
    assert!(error.contains("^"), "{error}");
    assert!(error.contains("help:"), "{error}");
    assert!(!root.path().join("not a path").exists());
    assert!(!root.path().join(".temp").exists());
}

#[test]
fn command_alias_cannot_change_the_grammar_of_a_pending_macro() {
    let root = workspace();
    let output = invoke(
        root.path(),
        &["repl"],
        "macro copy(src, des):\n    src -> des\n:syntax 0.3\n\ncopy(\"data\", $pwd / \"out\")\n/quit\n",
    );
    assert!(output.status.success());
    let errors = String::from_utf8_lossy(&output.stderr);
    assert!(
        errors.contains("finish or cancel the current block"),
        "{errors}"
    );
    assert_eq!(fs::read(root.path().join("out")).unwrap(), b"data");
}
