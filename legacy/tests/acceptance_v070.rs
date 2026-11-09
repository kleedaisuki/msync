//! Public v0.7.0 journeys exercise namespace values and actual file outcomes.

#[allow(
    dead_code,
    reason = "The shared process fixture also supports interactive journeys."
)]
#[path = "support/journey_process.rs"]
mod journey_process;

use std::fs;
use std::path::Path;
use std::process::Output;

use journey_process::Process;

fn workspace() -> tempfile::TempDir {
    let parent = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&parent).unwrap();
    let root = tempfile::Builder::new()
        .prefix("acceptance-v070-")
        .tempdir_in(parent)
        .unwrap();
    fs::create_dir(root.path().join("home")).unwrap();
    root
}

fn invoke(root: &Path, args: &[&str]) -> Output {
    Process::start(Process::command(root, args)).finish()
}

fn success(output: Output) -> Output {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    output
}

const USER_SCRIPT: &str = r#"
forge => ssh://forge
myvpn => ssh://myvpn
wsl => $wsl

macro copy_to(scope, file):
    $local::file -> scope::file

macro copy_dir_to(scope, file):
    global file
    if exists scope::file:
        $local::file -> scope::file/.
    else:
        copy_to(scope, file)

for file in [~/.codex/AGENTS, ~/.codex/codex.prompt]:
    for scope in [wsl, myvpn, forge]:
        copy_to(scope, file)
"#;

#[test]
fn supplied_scope_range_script_checks_compiles_and_previews_without_remote_work() {
    let root = workspace();
    fs::write(root.path().join("user.msync"), USER_SCRIPT).unwrap();
    for args in [
        vec!["check", "user.msync"],
        vec!["plan", "user.msync"],
        vec!["compile", "user.msync", "-o", "user.mobj"],
        vec!["emit-ir", "user.msync", "-o", "user.json"],
        vec!["run", "user.msync", "--dry-run"],
    ] {
        success(invoke(root.path(), &args));
    }
    fs::remove_file(root.path().join("user.msync")).unwrap();
    success(invoke(root.path(), &["run", "user.mobj", "--dry-run"]));
    success(invoke(root.path(), &["run-ir", "user.json", "--dry-run"]));
    assert!(!root.path().join("home/.codex").exists());
}

#[test]
fn namespaces_stdlib_and_scope_ranges_execute_from_source_free_artifacts() {
    let root = workspace();
    fs::create_dir(root.path().join("inputs")).unwrap();
    fs::write(root.path().join("inputs/AGENTS"), b"shared guidance\n").unwrap();
    fs::write(root.path().join("inputs/codex.prompt"), b"hello, Klee\n").unwrap();
    let source = r#"
# msync: 0.7
fs => $std::fs
views => $std::ranges
scope hello:
    codex := inputs
    scope names:
        prompt = "codex.prompt"

macro copy_to(endpoint, file):
    global hello
    hello::codex/file -> endpoint::published/file

fs::create_directories(published)
for file in ["AGENTS", hello::names::prompt]:
    for endpoint in [$local, $local] | views::take(1):
        copy_to(endpoint, file)
if fs::is_directory(published) and fs::is_regular_file(published/AGENTS):
    "ready" -> observed
else:
    "wrong" -> observed
"#;
    fs::write(root.path().join("entry.msync"), source).unwrap();
    for args in [
        ["check", "entry.msync"].as_slice(),
        ["plan", "entry.msync"].as_slice(),
        ["run", "entry.msync", "--dry-run"].as_slice(),
    ] {
        success(invoke(root.path(), args));
    }
    assert!(!root.path().join("published").exists());
    success(invoke(
        root.path(),
        &["compile", "entry.msync", "-o", "entry.mobj"],
    ));
    success(invoke(
        root.path(),
        &["emit-ir", "entry.msync", "-o", "entry.json"],
    ));
    let expected: &[(&str, &[u8])] = &[
        ("published/AGENTS", b"shared guidance\n"),
        ("published/codex.prompt", b"hello, Klee\n"),
        ("observed", b"ready"),
    ];
    success(invoke(root.path(), &["run", "entry.msync"]));
    for (name, bytes) in expected {
        assert_eq!(fs::read(root.path().join(name)).unwrap(), *bytes, "{name}");
    }
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for args in [["run", "entry.mobj"], ["run-ir", "entry.json"]] {
        fs::remove_dir_all(root.path().join("published")).unwrap();
        fs::remove_file(root.path().join("observed")).unwrap();
        success(invoke(root.path(), &args));
        for (name, bytes) in expected {
            assert_eq!(fs::read(root.path().join(name)).unwrap(), *bytes, "{name}");
        }
    }
}

#[test]
fn filesystem_observations_are_runtime_values_and_unselected_endpoints_are_inert() {
    let root = workspace();
    let source = r#"
fs => $std::fs
remote => ssh://must-not-be-contacted
if fs::is_directory(changing):
    "directory" -> result
else:
    "not-directory" -> result
if false:
    fs::create_directories(remote::unselected)
"#;
    fs::write(root.path().join("entry.msync"), source).unwrap();
    success(invoke(
        root.path(),
        &["compile", "entry.msync", "-o", "entry.mobj"],
    ));
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for directory in [false, true, false] {
        if directory {
            fs::create_dir(root.path().join("changing")).unwrap();
        }
        success(invoke(root.path(), &["run", "entry.mobj"]));
        assert_eq!(
            fs::read(root.path().join("result")).unwrap(),
            if directory {
                b"directory".as_slice()
            } else {
                b"not-directory".as_slice()
            }
        );
        if directory {
            fs::remove_dir(root.path().join("changing")).unwrap();
        }
    }
}
