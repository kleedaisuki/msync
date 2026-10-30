//! User-facing journeys for the transaction-free, barrier-based language.

use msync::language::{self, ExprKind, StatementKind};
use std::fs;
use std::path::Path;
use std::process::{Command, Output};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("cli-barriers-v5-")
        .tempdir_in(root)
        .unwrap()
}

fn executable() -> std::ffi::OsString {
    std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into())
}

fn command(root: &Path, arguments: &[&str]) -> Command {
    let mut command = Command::new(executable());
    command
        .current_dir(root)
        .args(arguments)
        .env("NO_COLOR", "1");
    command
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

fn no_effects(root: &Path) {
    for name in ["created.txt", "launched.txt", "stdout.txt", ".temp"] {
        assert!(
            !root.join(name).exists(),
            "unexpected execution effect: {name}"
        );
    }
}

#[test]
fn forced_color_help_explains_barriers_without_obsolete_dependencies() {
    let root = workspace();
    let output = command(root.path(), &["--help", "--color", "always"])
        .env_remove("NO_COLOR")
        .output()
        .unwrap();
    success(&output);
    let text = String::from_utf8(output.stdout).unwrap();
    assert!(text.contains("\u{1b}["), "help has no color: {text}");
    let plain = regex::Regex::new(r"\x1b\[[0-9;]*m")
        .unwrap()
        .replace_all(&text, "");
    for word in ["release", "acquire", "acqrel", "basic blocks", "0.5"] {
        assert!(plain.contains(word), "help lacks {word}: {text}");
    }
    assert!(plain.contains("  symbol home(space):\n      return space::~\n"));
    let symbol = language::parse("symbol home(space):\n    return space::~\n").unwrap();
    assert!(matches!(
        symbol.statements[0].kind,
        StatementKind::Symbol { .. }
    ));
    let execute = plain
        .lines()
        .find(|line| line.starts_with("  execute "))
        .unwrap()
        .trim_start()
        .split("  ")
        .next()
        .unwrap();
    let example = language::parse(execute).unwrap();
    let StatementKind::Execute(expression) = &example.statements[0].kind else {
        panic!("help example is not execute: {execute}");
    };
    let ExprKind::Join {
        suffix: Some(suffix),
        ..
    } = &expression.kind
    else {
        panic!("help selector is not a joined path: {execute}");
    };
    let ExprKind::Path(selector) = &suffix.kind else {
        panic!("help selector must not be a literal: {execute}");
    };
    assert_eq!(selector.path, r"<.+\.msync>");
    let plain = plain.to_ascii_lowercase();
    for obsolete in ["transaction", "sandboxie", "bubblewrap"] {
        assert!(
            !plain.contains(obsolete),
            "help advertises {obsolete}: {text}"
        );
    }
    assert_eq!(fs::read_dir(root.path()).unwrap().count(), 0);
}

#[test]
fn removed_syntax_version_is_rejected_before_file_effects() {
    let root = workspace();
    let script = root.path().join("copy.msync");
    for (arguments, source) in [
        (
            vec!["--syntax", "0.4", "run", "copy.msync"],
            "\"must not execute\" -> created.txt\n",
        ),
        (
            vec!["run", "copy.msync"],
            "# msync: 0.4\n\"must not execute\" -> created.txt\n",
        ),
    ] {
        fs::write(&script, source).unwrap();
        let output = command(root.path(), &arguments).output().unwrap();
        assert!(!output.status.success());
        let diagnostic = String::from_utf8_lossy(&output.stderr);
        assert!(diagnostic.contains("0.4"), "{diagnostic}");
        assert!(diagnostic.contains("0.5"), "{diagnostic}");
        assert!(diagnostic.contains("no longer supported"), "{diagnostic}");
        assert_eq!(fs::read_to_string(&script).unwrap(), source);
        no_effects(root.path());
    }
    assert_eq!(fs::read_dir(root.path()).unwrap().count(), 1);
}

#[test]
fn static_async_compile_and_plan_show_blocks_without_launching_child() {
    let root = workspace();
    let child_name = if cfg!(windows) {
        "child-msync.exe"
    } else {
        "child-msync"
    };
    fs::copy(executable(), root.path().join(child_name)).unwrap();
    fs::write(
        root.path().join("child.msync"),
        "\"started\" -> launched.txt\n",
    )
    .unwrap();
    let source = format!(
        "async Program => ./\"{child_name}\"(\"run\", \"child.msync\", \"--color\", \"never\")\nProgram::stdout -> stdout.txt\nawait Program\n"
    );
    fs::write(root.path().join("barriers.msync"), &source).unwrap();
    for mode in ["compile", "plan"] {
        let output = command(root.path(), &[mode, "barriers.msync"])
            .output()
            .unwrap();
        success(&output);
        let text = String::from_utf8(output.stdout).unwrap();
        assert!(text.contains("Static IR preview"), "{text}");
        assert!(text.contains("ordered basic blocks"), "{text}");
        let blocks = text
            .lines()
            .filter(|line| line.starts_with("block "))
            .count();
        assert!(
            blocks >= 3,
            "expected launch, output, and await blocks: {text}"
        );
        assert!(text.contains("acquire program"), "{text}");
        assert!(text.contains(child_name), "{text}");
        no_effects(root.path());
        assert_eq!(
            fs::read_to_string(root.path().join("barriers.msync")).unwrap(),
            source
        );
    }
    assert_eq!(fs::read_dir(root.path()).unwrap().count(), 3);
}

#[test]
fn removed_transaction_statements_offer_barrier_migration_without_partial_execution() {
    let root = workspace();
    let script = root.path().join("migration.msync");
    for removed in [
        "transaction old:\n    \"never\" -> nested.txt",
        "commit",
        "rollback",
    ] {
        let source = format!("\"earlier statement must not execute\" -> created.txt\n{removed}\n");
        fs::write(&script, &source).unwrap();
        let output = command(root.path(), &["run", "migration.msync"])
            .output()
            .unwrap();
        assert!(!output.status.success());
        let diagnostic = String::from_utf8_lossy(&output.stderr);
        assert!(
            diagnostic.contains("transaction syntax has been removed"),
            "{diagnostic}"
        );
        for hint in ["async", "await", "atomic"] {
            assert!(
                diagnostic.contains(hint),
                "missing migration hint {hint}: {diagnostic}"
            );
        }
        no_effects(root.path());
        assert!(!root.path().join("nested.txt").exists());
        assert_eq!(fs::read_to_string(&script).unwrap(), source);
    }
    assert_eq!(fs::read_dir(root.path()).unwrap().count(), 1);
}
