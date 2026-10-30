use std::fs;
use std::path::Path;
use std::process::Command;

const VARIABLE: &str = "MSYNC_TEST_SOURCE_DIRECTORY_052";

fn invoke(root: &Path, environment: &Path, args: &[&str]) {
    let output = Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root)
        .env(VARIABLE, environment)
        .args(args)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{args:?}: {}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );
}

fn environment_source_and_object_agree(directive: &str, absolute: bool) {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("scripts-env-052-")
        .tempdir_in(temporary)
        .unwrap();
    let root = workspace.path();
    fs::create_dir_all(root.join("envsub")).unwrap();
    fs::create_dir_all(root.join("scripts/envsub")).unwrap();
    fs::write(
        root.join("scripts/entry.msync"),
        format!("{directive} $env::{VARIABLE} / piece.msync\n"),
    )
    .unwrap();
    fs::write(
        root.join("envsub/piece.msync"),
        "\"cwd environment source\" -> $file::parent / output\n",
    )
    .unwrap();
    fs::write(
        root.join("scripts/envsub/piece.msync"),
        "\"wrong containing-file source\" -> $file::parent / output\n",
    )
    .unwrap();
    let environment = if absolute {
        root.join("envsub")
    } else {
        "envsub".into()
    };
    invoke(
        root,
        &environment,
        &["compile", "scripts/entry.msync", "-o", "compiled.mobj"],
    );
    invoke(root, &environment, &["run", "scripts/entry.msync"]);
    let destination = root.join("envsub/output");
    assert_eq!(fs::read(&destination).unwrap(), b"cwd environment source");
    assert!(!root.join("scripts/envsub/output").exists());
    fs::remove_file(&destination).unwrap();
    for source in [
        "scripts/entry.msync",
        "envsub/piece.msync",
        "scripts/envsub/piece.msync",
    ] {
        fs::remove_file(root.join(source)).unwrap();
    }
    invoke(root, &environment, &["run", "compiled.mobj"]);
    assert_eq!(fs::read(destination).unwrap(), b"cwd environment source");
    assert!(!root.join("scripts/envsub/output").exists());
}

#[test]
fn include_environment_paths_match_source_free_objects() {
    for absolute in [false, true] {
        environment_source_and_object_agree("include", absolute);
    }
}

#[test]
fn execute_environment_paths_match_source_free_objects() {
    for absolute in [false, true] {
        environment_source_and_object_agree("execute", absolute);
    }
}
