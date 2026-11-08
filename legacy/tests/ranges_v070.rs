//! Scope ranges keep endpoint identity through callbacks and source-free execution.

#[allow(
    dead_code,
    reason = "Shared real-process fixture exposes interactive helpers."
)]
#[path = "support/journey_process.rs"]
mod journey_process;

use std::fs;
use std::path::Path;

use journey_process::Process;
use msync::compiler::{CompileContext, structured::Session};

fn success(root: &Path, args: &[&str]) {
    let output = Process::start(Process::command(root, args)).finish();
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

#[test]
fn endpoint_scope_ranges_compile_without_authenticating_unselected_endpoints() {
    let source = "forge => ssh://forge\nmyvpn => ssh://myvpn\nwsl => $wsl\nmacro copy_to(scope, file):\n    $local::file -> scope::file\nfor file in [~/.codex/AGENTS, ~/.codex/codex.prompt]:\n    for scope in [wsl, myvpn, forge]:\n        copy_to(scope, file)\n";
    Session::new()
        .compile(
            &msync::language::parse(source).unwrap(),
            &CompileContext {
                pwd: Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/v070/scopes-check"),
                source_file: None,
            },
        )
        .unwrap();
}

#[test]
fn scope_range_callbacks_consumers_and_nested_macros_survive_object_and_ir() {
    let parent = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/v070");
    fs::create_dir_all(&parent).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("scope-ranges-")
        .tempdir_in(parent)
        .unwrap();
    let root = workspace.path();
    fs::write(root.join("a"), "first").unwrap();
    fs::write(root.join("b"), "second").unwrap();
    fs::write(root.join("entry.msync"), "symbol identity(scope):\n    return scope\nboolean yes(scope):\n    return true\nmacro copy_to(scope, file):\n    $local::file -> scope::\"copied\" / file\nscopes := [$local, $local] | $view::transform(identity) | $view::filter(yes) | $view::unique\nfor file in [a, b]:\n    for scope in scopes:\n        copy_to(scope, file)\nfirst => scopes | $view::front\n\"selected\" -> first::marker\nif scopes | $view::contains($local):\n    \"found\" -> contains\n").unwrap();
    success(root, &["compile", "entry.msync", "-o", "program.mobj"]);
    success(root, &["emit-ir", "entry.msync", "-o", "program.json"]);
    fs::remove_file(root.join("entry.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        success(root, &args);
        for (name, expected) in [
            ("copied/a", "first"),
            ("copied/b", "second"),
            ("marker", "selected"),
            ("contains", "found"),
        ] {
            assert_eq!(fs::read_to_string(root.join(name)).unwrap(), expected);
            fs::remove_file(root.join(name)).unwrap();
        }
    }
}

#[test]
fn source_range_returns_snapshot_dynamic_arguments_once_and_only_on_demand() {
    let parent = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/v070");
    fs::create_dir_all(&parent).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("bound-ranges-")
        .tempdir_in(parent)
        .unwrap();
    let root = workspace.path();
    fs::write(root.join("input"), "source-backed singleton").unwrap();
    fs::write(root.join("entry.msync"), "fs => $std::fs\nrs => $std::ranges\nsymbol pair(value):\n    return [value, value]\nfor file in rs::single(fs::filename(input)):\n    file -> output\nfor value in pair(fs::create_directory(created)):\n    if value:\n        \"once\" -> snapshot\n    else:\n        \"twice\" -> snapshot\nfor file in rs::single(fs::canonical(missing)) | rs::take(0):\n    file -> unreachable\nfor value in pair(fs::create_directory(untouched)) | rs::take(0):\n    \"wrong\" -> unreachable\n").unwrap();
    success(root, &["compile", "entry.msync", "-o", "program.mobj"]);
    success(root, &["emit-ir", "entry.msync", "-o", "program.json"]);
    fs::remove_file(root.join("entry.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        success(root, &args);
        assert_eq!(
            fs::read_to_string(root.join("output")).unwrap(),
            "source-backed singleton"
        );
        assert_eq!(fs::read_to_string(root.join("snapshot")).unwrap(), "once");
        assert!(root.join("created").is_dir());
        assert!(!root.join("untouched").exists());
        assert!(!root.join("unreachable").exists());
        fs::remove_dir(root.join("created")).unwrap();
    }
}
