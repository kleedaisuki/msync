//! Real default-language command and persistent REPL journeys.

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
    invoke_command(command(root, arguments), source)
}

fn command(root: &Path, arguments: &[&str]) -> Command {
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let mut command = Command::new(executable);
    command
        .args(arguments)
        .current_dir(root)
        .env("NO_COLOR", "1")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    command
}

fn invoke_command(mut command: Command, source: &str) -> Output {
    let mut process = command.spawn().unwrap();
    process
        .stdin
        .take()
        .unwrap()
        .write_all(source.as_bytes())
        .unwrap();
    process.wait_with_output().unwrap()
}

#[test]
fn static_macro_compilation_never_inspects_endpoints_or_starts_remote_helpers() {
    let root = workspace();
    let helpers = root.path().join("unusable-helpers");
    fs::create_dir(&helpers).unwrap();
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    for helper in ["wsl", "ssh", "curl"] {
        let name = if cfg!(windows) {
            format!("{helper}.exe")
        } else {
            helper.to_owned()
        };
        // The msync CLI rejects transport-helper argv. An accidental helper
        // invocation must fail compilation rather than reach a real endpoint.
        fs::copy(&executable, helpers.join(name)).unwrap();
    }
    let source = "macro copy_codex_config(codex, des):\n\tcodex/config.toml -> des/config.toml\n\tcodex/agents -> des/agents/.\n\ncodex := ~/.codex\n\ncopy_codex_config($local::codex, $pwd::local)\ncopy_codex_config($wsl::codex, $pwd::wsl)\n";
    for arguments in [
        vec!["check", "-"],
        vec!["compile", "-"],
        vec!["plan", "-"],
        vec!["run", "-", "--dry-run"],
    ] {
        let mut arguments = arguments;
        arguments.extend(["--trace", "static.jsonl"]);
        let mut invocation = command(root.path(), &arguments);
        invocation.env("PATH", &helpers);
        let output = invoke_command(invocation, source);
        success(&output);
        let trace = fs::read_to_string(root.path().join("static.jsonl")).unwrap();
        for forbidden in ["prepare", "execute"] {
            assert!(
                !trace.contains(&format!("\"name\":\"{forbidden}\"")),
                "static command entered {forbidden}: {trace}"
            );
        }
        if arguments[0] != "check" {
            let text = String::from_utf8(output.stdout).unwrap();
            assert_eq!(text.matches(" transfer\n").count(), 4, "{text}");
            for expected in [
                "Static IR preview",
                "effects:",
                "definition:",
                "Runtime obligations:",
                "file kinds: unknown until runtime",
            ] {
                assert!(text.contains(expected), "missing {expected}: {text}");
            }
        }
        assert!(!root.path().join(".temp").exists());
        assert!(!root.path().join("local").exists());
        assert!(!root.path().join("wsl").exists());
    }
}

#[test]
fn static_ir_does_not_change_when_endpoint_file_kinds_change() {
    let root = workspace();
    let source = "source -> target\n\"data\" -> target\n";
    fs::write(root.path().join("source"), "before").unwrap();
    fs::write(root.path().join("target"), "keep").unwrap();
    let before = invoke(root.path(), &["compile", "-"], source);
    success(&before);
    let ir = String::from_utf8_lossy(&before.stdout);
    assert!(ir.contains("guarded candidate: 0000 -> 0001"), "{ir}");
    assert_eq!(
        ir.lines()
            .filter(|line| line.ends_with(" transfer"))
            .count(),
        2
    );
    fs::remove_file(root.path().join("source")).unwrap();
    fs::remove_file(root.path().join("target")).unwrap();
    fs::create_dir(root.path().join("source")).unwrap();
    fs::create_dir(root.path().join("target")).unwrap();
    let after = invoke(root.path(), &["compile", "-"], source);
    success(&after);
    assert_eq!(before.stdout, after.stdout);
    assert!(!root.path().join(".temp").exists());
    assert!(root.path().join("target").is_dir());
}

#[test]
fn static_compile_keeps_program_path_lookup_and_native_capabilities_unresolved() {
    let root = workspace();
    let source = "tool => $path::msync_static_compile_nonexistent_executable(\"exact argv\")\ntool::stdout -> captured\ntool::meta -> metadata\n";
    let mut invocation = command(root.path(), &["compile", "-"]);
    invocation.env("PATH", root.path());
    let output = invoke_command(invocation, source);
    success(&output);
    let text = String::from_utf8(output.stdout).unwrap();
    for expected in [
        "msync_static_compile_nonexistent_executable",
        "program byte stream",
        "program scalar",
        "external: unknown",
        "Runtime obligations:",
    ] {
        assert!(text.contains(expected), "missing {expected}: {text}");
    }
    assert!(!root.path().join(".temp").exists());
    assert!(!root.path().join("captured").exists());
    assert!(!root.path().join("metadata").exists());
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
        "text := \"file name\"\ntext -> literal.txt\n$pwd::pwd / \"file name\" -> copied.txt\n\"  可莉  \" -> whitespace.txt\n",
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
    let legacy = invoke(root.path(), &["--syntax", "0.3", "run", "-"], "");
    assert!(!legacy.status.success());
    assert!(String::from_utf8_lossy(&legacy.stderr).contains("0.5"));
}

#[test]
fn macro_check_and_plan_are_pure_then_explicit_directory_replacement_executes() {
    let root = workspace();
    fs::create_dir_all(root.path().join("source/agents")).unwrap();
    fs::create_dir_all(root.path().join("target/agents")).unwrap();
    fs::write(root.path().join("source/agents/new"), "new").unwrap();
    fs::write(root.path().join("target/agents/old"), "old").unwrap();
    let source = "macro copy(src, des):\n    src / \"agents\" -> des / \"agents\" / .\ncopy($pwd::pwd / \"source\", $pwd::pwd / \"target\")\n";
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
fn actual_repl_retains_macros_and_bindings_across_separate_submissions() {
    let root = workspace();
    let source = "macro emit(value):\n    value -> result.txt\n\nvalue := \"first\"\nemit(value)\nvalue := \"second\"\nemit(value)\n/bindings\n/quit\n";
    let output = invoke(root.path(), &["repl"], source);
    success(&output);
    assert_eq!(
        fs::read_to_string(root.path().join("result.txt")).unwrap(),
        "second"
    );
    assert!(String::from_utf8_lossy(&output.stdout).contains("macro emit(value)"));
    assert!(String::from_utf8_lossy(&output.stdout).contains("value :="));
}

#[test]
fn repl_keeps_compiled_bindings_after_io_failure_but_discards_frontend_error_candidates() {
    let root = workspace();
    fs::write(
        root.path().join("runtime-failure.msync"),
        "keep := \"compiled value\"\nmissing.txt -> ignored.txt\n",
    )
    .unwrap();
    fs::write(
        root.path().join("frontend-failure.msync"),
        "poison := \"must not survive\"\nunknown::source -> discarded.txt\n",
    )
    .unwrap();
    let source = "/load runtime-failure.msync\nkeep -> recovered.txt\n/load frontend-failure.msync\npoison -> rejected.txt\nkeep -> after.txt\n/bindings\n/quit\n";
    let output = invoke(root.path(), &["repl"], source);
    assert!(output.status.success());
    let errors = String::from_utf8_lossy(&output.stderr);
    assert!(errors.contains("runtime-failure.msync"), "{errors}");
    assert!(errors.contains("frontend-failure.msync"), "{errors}");
    assert!(errors.contains("unknown"), "{errors}");
    for name in ["recovered.txt", "after.txt"] {
        assert_eq!(
            fs::read_to_string(root.path().join(name)).unwrap(),
            "compiled value"
        );
    }
    for name in ["ignored.txt", "discarded.txt", "rejected.txt"] {
        assert!(!root.path().join(name).exists());
    }
    let bindings = String::from_utf8_lossy(&output.stdout);
    assert!(bindings.contains("keep :="), "{bindings}");
    assert!(!bindings.contains("poison :="), "{bindings}");
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
        "macro copy(src, des):\n    src -> des\n:syntax 0.3\n\ncopy(\"data\", $pwd::pwd / \"out\")\n/quit\n",
    );
    assert!(output.status.success());
    let errors = String::from_utf8_lossy(&output.stderr);
    assert!(
        errors.contains("finish or cancel the current block"),
        "{errors}"
    );
    assert_eq!(fs::read(root.path().join("out")).unwrap(), b"data");
}
