//! Context changes, not filename conventions, determine relocatable execution.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("independent-object-")
        .tempdir_in(root)
        .unwrap()
}

fn quote(path: &Path) -> String {
    path.to_string_lossy()
        .replace('\\', "\\\\")
        .replace('"', "\\\"")
}

fn invoke(cwd: &Path, arguments: &[&str], bindings: &[(&str, &Path)]) -> Output {
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let mut command = Command::new(executable);
    command
        .args(arguments)
        .current_dir(cwd)
        .env("NO_COLOR", "1")
        .env_remove("MSYNC_TRACE")
        .env(
            "MSYNC_SANDBOXIE_HOME",
            cwd.join("unavailable-native-provider"),
        )
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    for (name, value) in bindings {
        command.env(name, value);
    }
    let mut child = command.spawn().unwrap();
    let deadline = Instant::now() + Duration::from_secs(20);
    while child.try_wait().unwrap().is_none() {
        if Instant::now() >= deadline {
            let _ = child.kill();
            let output = child.wait_with_output().unwrap();
            panic!(
                "object command did not complete: {arguments:?}\nstdout={}\nstderr={}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            );
        }
        thread::sleep(Duration::from_millis(5));
    }
    child.wait_with_output().unwrap()
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

fn directory(root: &Path, name: &str) -> PathBuf {
    let path = root.join(name);
    fs::create_dir_all(&path).unwrap();
    path
}

#[test]
fn identical_object_bytes_bind_two_environments_without_rebasing_absolute_constants() {
    let root = workspace();
    let build = directory(root.path(), "build");
    let build_home = directory(&build, "compile-home");
    let build_env = directory(&build, "compile-env");
    let no_tools = directory(root.path(), "no-tools");
    for path in [&build, &build_home, &build_env] {
        fs::write(path.join("input"), b"compile-time poison").unwrap();
    }
    let absolute = build.join("absolute-constant");
    fs::write(&absolute, b"old constant contents must not be captured").unwrap();
    fs::write(build.join("code.msync"), format!(
        "macro copy(src, des):\n    src -> des\ncopy($pwd / \"input\", from-pwd)\ncopy(~/input, from-home)\ncopy($env::MSYNC_OBJECT_INPUT/input, from-env)\n$local::\"{}\" -> from-absolute\n",
        quote(&absolute)
    )).unwrap();
    success(&invoke(
        &build,
        &["compile", "code.msync", "-o", "module.mobj"],
        &[
            ("HOME", &build_home),
            ("USERPROFILE", &build_home),
            ("MSYNC_OBJECT_INPUT", &build_env),
            ("PATH", &no_tools),
        ],
    ));
    assert!(!build.join(".temp").exists());
    assert!(!build.join("from-pwd").exists());
    let object = fs::read(build.join("module.mobj")).unwrap();
    fs::remove_file(build.join("code.msync")).unwrap();
    fs::remove_file(build.join("input")).unwrap();
    fs::remove_file(build_home.join("input")).unwrap();
    fs::remove_file(build_env.join("input")).unwrap();
    fs::write(&absolute, b"runtime explicit absolute value").unwrap();

    for name in ["first 可莉", "second context"] {
        let cwd = directory(root.path(), name);
        let home = directory(&cwd, "home");
        let environment = directory(&cwd, "environment");
        fs::write(cwd.join("input"), format!("{name}: cwd")).unwrap();
        fs::write(home.join("input"), format!("{name}: home")).unwrap();
        fs::write(environment.join("input"), format!("{name}: env")).unwrap();
        fs::write(cwd.join("absolute-constant"), b"a wrongly rebased constant").unwrap();
        fs::write(cwd.join("same-object.data"), &object).unwrap();
        success(&invoke(
            &cwd,
            &["run", "same-object.data"],
            &[
                ("HOME", &home),
                ("USERPROFILE", &home),
                ("MSYNC_OBJECT_INPUT", &environment),
                ("PATH", &no_tools),
            ],
        ));
        for (output, suffix) in [
            ("from-pwd", "cwd"),
            ("from-home", "home"),
            ("from-env", "env"),
        ] {
            assert_eq!(
                fs::read_to_string(cwd.join(output)).unwrap(),
                format!("{name}: {suffix}")
            );
        }
        assert_eq!(
            fs::read(cwd.join("from-absolute")).unwrap(),
            b"runtime explicit absolute value"
        );
        assert_eq!(fs::read(cwd.join("same-object.data")).unwrap(), object);
    }
    assert!(!build.join("from-absolute").exists());
}

#[test]
fn module_file_and_directory_anchors_are_distinct_from_the_invocation_directory() {
    let root = workspace();
    let build = directory(root.path(), "build");
    let package = directory(root.path(), "package");
    let cwd = directory(root.path(), "invocation");
    fs::write(build.join("code.msync"),
        "$file::parent/module-input -> from-module\n$pwd / \"cwd-input\" -> from-cwd\n$file::this -> object-copy\n"
    ).unwrap();
    fs::write(build.join("module-input"), b"compile-time module poison").unwrap();
    success(&invoke(
        &build,
        &["compile", "code.msync", "-o", "module.mobj"],
        &[],
    ));
    let object = fs::read(build.join("module.mobj")).unwrap();
    fs::write(package.join("renamed-object.blob"), &object).unwrap();
    fs::write(package.join("module-input"), b"loaded module directory").unwrap();
    fs::write(package.join("cwd-input"), b"wrong cwd binding").unwrap();
    fs::write(cwd.join("cwd-input"), b"actual invocation directory").unwrap();
    fs::write(cwd.join("module-input"), b"wrong module binding").unwrap();
    fs::remove_file(build.join("code.msync")).unwrap();
    fs::remove_file(build.join("module-input")).unwrap();
    success(&invoke(
        &cwd,
        &["run", package.join("renamed-object.blob").to_str().unwrap()],
        &[],
    ));
    assert_eq!(
        fs::read(cwd.join("from-module")).unwrap(),
        b"loaded module directory"
    );
    assert_eq!(
        fs::read(cwd.join("from-cwd")).unwrap(),
        b"actual invocation directory"
    );
    assert_eq!(fs::read(cwd.join("object-copy")).unwrap(), object);
    assert!(!package.join("from-module").exists());
}

#[test]
fn deleted_source_still_supplies_owned_runtime_debug_locations() {
    let root = workspace();
    let build = directory(root.path(), "build");
    let runtime = directory(root.path(), "runtime");
    let source = "macro publish(src):\n    src -> output\npublish($pwd / \"不存在\")\n";
    fs::write(build.join("diagnostic-script.msync"), source).unwrap();
    success(&invoke(
        &build,
        &["compile", "diagnostic-script.msync", "-o", "module.mobj"],
        &[],
    ));
    fs::rename(build.join("module.mobj"), runtime.join("module.mobj")).unwrap();
    fs::remove_file(build.join("diagnostic-script.msync")).unwrap();
    let failed = invoke(&runtime, &["run", "module.mobj"], &[]);
    assert!(!failed.status.success());
    let error = String::from_utf8_lossy(&failed.stderr);
    assert!(error.starts_with("execution error:"), "{error}");
    assert!(error.contains("diagnostic-script.msync:3:"), "{error}");
    assert!(error.contains("publish($pwd /"), "{error}");
    assert!(error.contains("不存在") && error.contains('^'), "{error}");
    assert!(!runtime.join("output").exists());
}

#[test]
fn source_extension_is_not_a_format_tag_and_objects_cannot_be_script_formatted() {
    let root = workspace();
    fs::write(
        root.path().join("source.mobj"),
        "\"source text\" -> source-output\n",
    )
    .unwrap();
    success(&invoke(root.path(), &["run", "source.mobj"], &[]));
    assert_eq!(
        fs::read(root.path().join("source-output")).unwrap(),
        b"source text"
    );
    success(&invoke(
        root.path(),
        &["compile", "source.mobj", "-o", "real-object.txt"],
        &[],
    ));
    let before = fs::read(root.path().join("real-object.txt")).unwrap();
    let refused = invoke(root.path(), &["fmt", "real-object.txt"], &[]);
    assert!(!refused.status.success());
    assert_eq!(
        fs::read(root.path().join("real-object.txt")).unwrap(),
        before
    );
    let error = String::from_utf8_lossy(&refused.stderr);
    assert!(
        error.contains("object") && error.contains("source"),
        "{error}"
    );
    assert!(
        !error.contains('^'),
        "format dispatch is not malformed source: {error}"
    );
}
