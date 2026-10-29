//! A killed publisher is recovered before a new CLI submission can execute.
//! This verifies restart recovery, not instantaneous multi-path visibility to other processes.

use std::collections::BTreeMap;
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant};

const CRASH_EXIT: i32 = 86;
const CRASH_STAGES: [&str; 4] = [
    "prepared",
    "after-backup",
    "after-install",
    "before-cleanup",
];

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("barrier-crash-v5-")
        .tempdir_in(root)
        .unwrap()
}

fn command(root: &Path) -> Command {
    command_args(root, &["run", "-"])
}

fn command_args(root: &Path, arguments: &[&str]) -> Command {
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let mut command = Command::new(executable);
    command
        .args(arguments)
        .current_dir(root)
        .env("NO_COLOR", "1")
        .env_remove("MSYNC_TEST_CRASH_AT")
        .env_remove("MSYNC_TEST_CRASH_TARGET")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    command
}

fn wait(mut child: Child) -> Output {
    let deadline = Instant::now() + Duration::from_secs(30);
    loop {
        if child.try_wait().unwrap().is_some() {
            return child.wait_with_output().unwrap();
        }
        if Instant::now() >= deadline {
            child.kill().unwrap();
            let output = child.wait_with_output().unwrap();
            panic!(
                "publisher/recovery timed out: {}",
                String::from_utf8_lossy(&output.stderr)
            );
        }
        thread::sleep(Duration::from_millis(10));
    }
}

fn run(mut command: Command, source: &str) -> Output {
    let mut child = command.spawn().unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(source.as_bytes())
        .unwrap();
    wait(child)
}

fn success(output: &Output) {
    assert!(
        output.status.success(),
        "status: {:?}\nstdout: {}\nstderr: {}",
        output.status,
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );
}

fn crash(root: &Path, target: &Path, source: &str, stage: &str) -> Output {
    let mut invocation = command(root);
    // The runtime resolves existing parents to physical paths. On Windows that
    // uses a verbatim prefix, including when the destination does not exist yet.
    let target = fs::canonicalize(target.parent().unwrap())
        .unwrap()
        .join(target.file_name().unwrap());
    invocation
        .env("MSYNC_TEST_CRASH_AT", stage)
        .env("MSYNC_TEST_CRASH_TARGET", &target);
    let output = run(invocation, source);
    assert_eq!(
        output.status.code(),
        Some(CRASH_EXIT),
        "failpoint {stage} was not reached; stdout: {}; stderr: {}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );
    output
}

fn recover(root: &Path) {
    success(&run(command(root), "\"recovery finished\" -> resumed\n"));
    assert_eq!(
        fs::read(root.join("resumed")).unwrap(),
        b"recovery finished"
    );
    assert_recovered(root);
}

fn assert_recovered(root: &Path) {
    assert_eq!(
        fs::read(root.join("preserved")).unwrap(),
        b"previous statement"
    );
    for entry in fs::read_dir(root).unwrap() {
        let entry = entry.unwrap();
        assert!(
            !entry
                .file_name()
                .to_string_lossy()
                .starts_with(".msync-statement-"),
            "unrecovered coordinator: {}",
            entry.path().display(),
        );
    }
    for entry in fs::read_dir(root.join(".temp/.msync-recovery-v1")).unwrap() {
        let entry = entry.unwrap();
        assert!(
            !entry.file_name().to_string_lossy().starts_with("record-"),
            "unretired recovery record: {}",
            entry.path().display(),
        );
    }
}

#[test]
fn empty_and_declarations_only_submissions_recover_before_returning() {
    for object in [false, true] {
        for source in ["", "unused := $pwd / \"not-created\"\n"] {
            let root = workspace();
            let target = root.path().join("target");
            fs::write(root.path().join("source"), b"new bytes").unwrap();
            fs::write(&target, b"old bytes").unwrap();
            if object {
                success(&run(
                    command_args(root.path(), &["compile", "-", "-o", "empty.mobj"]),
                    source,
                ));
            }
            let before = tree(&target);
            let after = tree(&root.path().join("source"));
            crash(
                root.path(),
                &target,
                "await \"previous statement\" -> preserved\nsource -> target\n",
                "after-install",
            );
            let invocation = if object {
                command_args(root.path(), &["run", "empty.mobj"])
            } else {
                command(root.path())
            };
            success(&run(invocation, if object { "" } else { source }));
            assert_recovered(root.path());
            let actual = tree(&target);
            assert!(
                actual == before || actual == after,
                "mixed target after empty submission: {actual:?}"
            );
            assert!(!root.path().join("resumed").exists());
            assert!(!root.path().join("not-created").exists());
            // A later effect cannot mask a skipped recovery on the empty run.
            recover(root.path());
        }
    }
}

#[derive(Debug, PartialEq, Eq)]
enum Node {
    Directory,
    File(Vec<u8>),
}

fn tree(root: &Path) -> Option<BTreeMap<PathBuf, Node>> {
    if !root.exists() {
        return None;
    }
    let mut result = BTreeMap::new();
    collect(root, Path::new(""), &mut result);
    Some(result)
}

fn collect(root: &Path, relative: &Path, result: &mut BTreeMap<PathBuf, Node>) {
    let path = root.join(relative);
    if path.is_dir() {
        result.insert(relative.to_owned(), Node::Directory);
        for entry in fs::read_dir(path).unwrap() {
            let entry = entry.unwrap();
            collect(root, &relative.join(entry.file_name()), result);
        }
    } else {
        result.insert(relative.to_owned(), Node::File(fs::read(path).unwrap()));
    }
}

#[test]
fn a_crashed_file_statement_recovers_without_undoing_a_completed_statement() {
    for existing in [false, true] {
        for stage in CRASH_STAGES {
            let root = workspace();
            let target = root.path().join("target");
            fs::write(root.path().join("source"), vec![b'N'; 131_073]).unwrap();
            if existing {
                fs::write(&target, vec![b'O'; 65_537]).unwrap();
            }
            let before = tree(&target);
            let after = tree(&root.path().join("source"));
            crash(
                root.path(),
                &target,
                "await \"previous statement\" -> preserved\nsource -> target\n",
                stage,
            );
            recover(root.path());
            let actual = tree(&target);
            assert!(
                actual == before || actual == after,
                "mixed file publication at {stage}: {actual:?}"
            );
        }
    }
}

#[test]
fn a_crashed_tree_statement_recovers_a_whole_tree_including_removals() {
    for existing in [false, true] {
        for stage in CRASH_STAGES {
            let root = workspace();
            let target = root.path().join("target");
            fs::create_dir_all(root.path().join("source/nested/empty")).unwrap();
            fs::write(root.path().join("source/first"), b"new first").unwrap();
            fs::write(
                root.path().join("source/nested/second"),
                vec![b'N'; 131_073],
            )
            .unwrap();
            if existing {
                fs::create_dir_all(target.join("obsolete/empty")).unwrap();
                fs::write(target.join("first"), b"old first").unwrap();
                fs::write(target.join("obsolete/removed"), b"old child").unwrap();
            }
            let before = tree(&target);
            let after = tree(&root.path().join("source"));
            crash(
                root.path(),
                &target,
                "await \"previous statement\" -> preserved\nsource -> target/.\n",
                stage,
            );
            recover(root.path());
            let actual = tree(&target);
            assert!(
                actual == before || actual == after,
                "mixed directory publication at {stage}: {actual:?}"
            );
        }
    }
}
