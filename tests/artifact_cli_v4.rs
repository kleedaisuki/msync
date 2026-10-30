//! Relocatable objects are executable inputs, not cached filesystem observations.

use std::fs;
use std::path::Path;
use std::process::{Command, Output};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("artifact-cli-")
        .tempdir_in(root)
        .unwrap()
}

fn invoke(cwd: &Path, arguments: &[&str]) -> Output {
    Command::new(
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into()),
    )
    .current_dir(cwd)
    .args(arguments)
    .env("NO_COLOR", "1")
    .output()
    .unwrap()
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
fn exported_macro_object_moves_and_binds_pwd_without_reopening_source() {
    let root = workspace();
    let build = root.path().join("build");
    let runtime = root.path().join("runtime");
    fs::create_dir(&build).unwrap();
    fs::create_dir(&runtime).unwrap();
    fs::write(
        build.join("input"),
        b"build-time value must not be captured",
    )
    .unwrap();
    fs::write(runtime.join("input"), b"runtime selected value").unwrap();
    fs::write(build.join("code.msync"), "base := $pwd::pwd / \"output\"\nmacro copy(value):\n    global base\n    value -> base / \"result\"\ncopy($pwd::pwd / \"input\")\n").unwrap();
    let compiled = invoke(&build, &["compile", "code.msync", "-o", "module.mobj"]);
    success(&compiled);
    assert!(build.join("module.mobj").metadata().unwrap().len() > 0);
    assert!(!build.join("output").exists());
    assert!(!build.join(".temp").exists());
    fs::rename(build.join("module.mobj"), runtime.join("relocated.data")).unwrap();
    fs::remove_file(build.join("code.msync")).unwrap();
    fs::remove_file(build.join("input")).unwrap();
    let ran = invoke(&runtime, &["run", "relocated.data"]);
    success(&ran);
    assert_eq!(
        fs::read(runtime.join("output/result")).unwrap(),
        b"runtime selected value"
    );
    assert!(!build.join("output").exists());
}

#[test]
fn object_publication_never_overwrites_the_source_or_an_existing_object_on_compile_failure() {
    let root = workspace();
    fs::write(root.path().join("code.msync"), "\"value\" -> output\n").unwrap();
    let same = invoke(root.path(), &["compile", "code.msync", "-o", "code.msync"]);
    assert!(!same.status.success());
    assert!(String::from_utf8_lossy(&same.stderr).contains("input file"));
    assert_eq!(
        fs::read_to_string(root.path().join("code.msync")).unwrap(),
        "\"value\" -> output\n"
    );
    fs::write(root.path().join("module.mobj"), b"previous object").unwrap();
    fs::write(root.path().join("code.msync"), "unknown::input -> output\n").unwrap();
    let failed = invoke(
        root.path(),
        &["compile", "code.msync", "--output", "module.mobj"],
    );
    assert!(!failed.status.success());
    assert_eq!(
        fs::read(root.path().join("module.mobj")).unwrap(),
        b"previous object"
    );
    assert!(!root.path().join("output").exists());
}

#[test]
fn stdout_object_payload_is_exact_machine_input_even_when_color_is_forced() {
    let root = workspace();
    fs::write(root.path().join("code.msync"), "\"payload\" -> result\n").unwrap();
    let object = invoke(
        root.path(),
        &[
            "compile",
            "code.msync",
            "--output",
            "-",
            "--color",
            "always",
        ],
    );
    success(&object);
    assert!(!object.stdout.is_empty());
    assert!(!object.stdout.contains(&0x1b));
    assert!(!root.path().join("result").exists());
    fs::write(root.path().join("unusual-extension.txt"), &object.stdout).unwrap();
    fs::remove_file(root.path().join("code.msync")).unwrap();
    success(&invoke(root.path(), &["run", "unusual-extension.txt"]));
    assert_eq!(fs::read(root.path().join("result")).unwrap(), b"payload");
}
