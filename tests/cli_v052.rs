use std::fs;
use std::io::Write;
use std::path::Path;
use std::process::{Command, Output, Stdio};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("cli-052-")
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
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    output
}

#[test]
fn symbols_release_acquire_and_nested_sources_run_as_one_real_compiled_program() {
    let root = workspace();
    fs::create_dir(root.path().join("parts")).unwrap();
    fs::write(root.path().join("shared.msync"), "symbol identity(value):\n    return value\nsymbol input(root, leaf):\n    return identity(root) / identity(leaf)\n").unwrap();
    fs::write(root.path().join("parts/b.msync"), "\"B\" -> order\n").unwrap();
    fs::write(root.path().join("parts/a.msync"), "\"A\" -> order\n").unwrap();
    fs::write(
        root.path().join("input file.txt"),
        "real static composition 可莉\n",
    )
    .unwrap();
    let exe = format!("{:?}", env!("CARGO_BIN_EXE_msync"));
    let source = format!(
        "include \"shared.msync\"\nsymbol executable():\n    return $local::{exe}\nacqrel input(identity($pwd)::pwd, \"input file.txt\") -> output\nexecute \"parts/<.+\\\\.msync>\"\nrelease version => executable()(\"--version\")\nversion::stdout -> version.txt\nacquire version\n"
    );
    fs::write(root.path().join("entry.msync"), source).unwrap();
    success(run(root.path(), &["check", "entry.msync"]));
    success(run(root.path(), &["run", "entry.msync", "--dry-run"]));
    assert!(!root.path().join("output").exists());
    assert!(!root.path().join("order").exists());
    assert!(!root.path().join("version.txt").exists());
    success(run(root.path(), &["run", "entry.msync"]));
    assert_eq!(
        fs::read(root.path().join("output")).unwrap(),
        fs::read(root.path().join("input file.txt")).unwrap()
    );
    assert_eq!(fs::read(root.path().join("order")).unwrap(), b"B");
    assert_eq!(
        fs::read_to_string(root.path().join("version.txt"))
            .unwrap()
            .trim(),
        concat!("msync ", env!("CARGO_PKG_VERSION"))
    );
    success(run(
        root.path(),
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    success(run(
        root.path(),
        &["emit-ir", "entry.msync", "-o", "portable.json"],
    ));
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    fs::remove_file(root.path().join("shared.msync")).unwrap();
    fs::remove_dir_all(root.path().join("parts")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "portable.json"]] {
        fs::remove_file(root.path().join("output")).unwrap();
        fs::remove_file(root.path().join("order")).unwrap();
        success(run(root.path(), &args));
        assert_eq!(fs::read(root.path().join("order")).unwrap(), b"B");
        assert_eq!(
            fs::read(root.path().join("output")).unwrap(),
            fs::read(root.path().join("input file.txt")).unwrap()
        );
    }
}

#[test]
fn repl_accumulates_symbol_bodies_and_continues_after_instantiation_failure() {
    let root = workspace();
    fs::write(root.path().join("input file.txt"), b"repl symbol payload").unwrap();
    let source = "symbol join(base, leaf):\n    return base / leaf\n\njoin($pwd, \"bad\") -> rejected\njoin($pwd::pwd, \"input file.txt\") -> output\n/quit\n";
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
        .write_all(source.as_bytes())
        .unwrap();
    let output = success(child.wait_with_output().unwrap());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(
        error.contains("symbol") && error.contains("not a space"),
        "{error}"
    );
    assert!(!root.path().join("rejected").exists());
    assert_eq!(
        fs::read(root.path().join("output")).unwrap(),
        b"repl symbol payload"
    );
}
