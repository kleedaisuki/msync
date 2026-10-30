//! Crash cuts bypass Rust unwinding; recovery runs in a fresh process.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::process::{Command, Output};

use msync::filesystem::SnapshotStore;
use msync::filesystem::durable::{Batch, Event, recover_parent};

const CRASH_STATUS: i32 = 73;
const ROOT_ENV: &str = "MSYNC_STORAGE_CRASH_TEST_ROOT";
const ACTION_ENV: &str = "MSYNC_STORAGE_CRASH_TEST_ACTION";
const CUT_ENV: &str = "MSYNC_STORAGE_CRASH_TEST_CUT";

fn workspace() -> tempfile::TempDir {
    let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&base).unwrap();
    tempfile::Builder::new()
        .prefix("storage-crash-v5-")
        .tempdir_in(base)
        .unwrap()
}

fn fixture(root: &Path) {
    for name in ["left", "right"] {
        fs::create_dir_all(root.join(name).join("old-empty")).unwrap();
        fs::write(root.join(name).join("old.txt"), format!("old-{name}")).unwrap();
        fs::create_dir_all(root.join("sources").join(name).join("new-empty")).unwrap();
        fs::write(
            root.join("sources").join(name).join("new.txt"),
            format!("new-{name}"),
        )
        .unwrap();
    }
    fs::write(root.join("sources/new-file"), b"new-previously-absent").unwrap();
    fs::write(root.join("existing"), b"old-existing-file").unwrap();
    fs::write(root.join("sources/existing"), b"new-existing-file").unwrap();
}

fn child(root: &Path, action: &str, cut: &str) -> Output {
    Command::new(std::env::current_exe().unwrap())
        .args(["--exact", "storage_crash_child", "--ignored", "--nocapture"])
        .env(ROOT_ENV, root)
        .env(ACTION_ENV, action)
        .env(CUT_ENV, cut)
        .output()
        .unwrap()
}

fn describe(output: &Output) -> String {
    format!(
        "status={}\nstdout={}\nstderr={}",
        output.status,
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    )
}

fn snapshot(root: &Path) -> Vec<(PathBuf, Option<Vec<u8>>)> {
    fn walk(base: &Path, current: &Path, output: &mut Vec<(PathBuf, Option<Vec<u8>>)>) {
        let mut children: Vec<_> = fs::read_dir(current)
            .unwrap()
            .map(|entry| entry.unwrap().path())
            .collect();
        children.sort();
        for path in children {
            let relative = path.strip_prefix(base).unwrap().to_owned();
            if path.is_dir() {
                output.push((relative, None));
                walk(base, &path, output);
            } else {
                output.push((relative, Some(fs::read(path).unwrap())));
            }
        }
    }
    let mut output = Vec::new();
    for name in ["left", "right", "created", "existing"] {
        let path = root.join(name);
        if !path.exists() {
            continue;
        }
        if path.is_dir() {
            output.push((PathBuf::from(name), None));
            walk(root, &path, &mut output);
        } else {
            output.push((PathBuf::from(name), Some(fs::read(path).unwrap())));
        }
    }
    output
}

fn expected_new() -> Vec<(PathBuf, Option<Vec<u8>>)> {
    let mut output = Vec::new();
    for name in ["left", "right"] {
        output.push((PathBuf::from(name), None));
        output.push((PathBuf::from(name).join("new-empty"), None));
        output.push((
            PathBuf::from(name).join("new.txt"),
            Some(format!("new-{name}").into_bytes()),
        ));
    }
    output.push((
        PathBuf::from("created"),
        Some(b"new-previously-absent".to_vec()),
    ));
    output.push((
        PathBuf::from("existing"),
        Some(b"new-existing-file".to_vec()),
    ));
    output
}

fn crash_and_recover(cut: &str, committed: bool) {
    let root = workspace();
    fixture(root.path());
    let before = snapshot(root.path());
    let output = child(root.path(), "publish", cut);
    assert_eq!(
        output.status.code(),
        Some(CRASH_STATUS),
        "cut={cut}: {}",
        describe(&output)
    );
    let output = child(root.path(), "recover", "");
    assert!(output.status.success(), "cut={cut}: {}", describe(&output));
    let expected = if committed { expected_new() } else { before };
    assert_eq!(snapshot(root.path()), expected, "cut={cut}");
    let output = child(root.path(), "recover", "");
    assert!(
        output.status.success(),
        "second recovery cut={cut}: {}",
        describe(&output)
    );
    assert_eq!(
        snapshot(root.path()),
        expected,
        "repeated recovery cut={cut}"
    );
}

#[test]
fn crash_before_decision_restores_the_entire_statement() {
    for cut in [
        "prepared",
        "backup-0",
        "install-0",
        "backup-1",
        "install-1",
        "install-2",
        "backup-3",
        "install-3",
        "before-commit",
    ] {
        crash_and_recover(cut, false);
    }
}

#[test]
fn crash_after_decision_keeps_the_entire_statement() {
    crash_and_recover("committed", true);
}

#[test]
fn normal_publication_and_recovery_keep_the_entire_new_state() {
    let root = workspace();
    fixture(root.path());
    let output = child(root.path(), "publish", "never");
    assert!(output.status.success(), "{}", describe(&output));
    assert_eq!(snapshot(root.path()), expected_new());
    let output = child(root.path(), "recover", "");
    assert!(output.status.success(), "{}", describe(&output));
    assert_eq!(snapshot(root.path()), expected_new());
}

#[cfg(unix)]
#[test]
fn interrupted_directory_mode_adjustment_is_repaired() {
    use std::os::unix::fs::PermissionsExt;

    let root = workspace();
    let target = root.path().join("target");
    let source = root.path().join("source");
    fs::create_dir(&target).unwrap();
    fs::create_dir(&source).unwrap();
    fs::write(target.join("old"), b"old").unwrap();
    fs::write(source.join("new"), b"new").unwrap();
    fs::set_permissions(&target, fs::Permissions::from_mode(0o555)).unwrap();
    let store = SnapshotStore::new(&root.path().join("scratch")).unwrap();
    let snapshot = store.capture(&source).unwrap();
    let batch = Batch::prepare(vec![store.prepare(&snapshot, &target).unwrap()]).unwrap();
    // Simulate the interval after the rename helper adds owner-write access but
    // before it restores that mode. Identity has not changed at this crash cut.
    fs::set_permissions(&target, fs::Permissions::from_mode(0o755)).unwrap();
    drop(batch);
    assert_eq!(recover_parent(root.path()).unwrap(), 1);
    assert_eq!(
        fs::metadata(&target).unwrap().permissions().mode() & 0o7777,
        0o555
    );
    assert_eq!(fs::read(target.join("old")).unwrap(), b"old");
    assert!(!target.join("new").exists());
    fs::set_permissions(&target, fs::Permissions::from_mode(0o755)).unwrap();
}

#[test]
#[ignore = "fixture subprocess, invoked by the parent tests"]
fn storage_crash_child() {
    let Some(root) = std::env::var_os(ROOT_ENV) else {
        return;
    };
    let root = PathBuf::from(root);
    match std::env::var(ACTION_ENV).unwrap().as_str() {
        "recover" => {
            recover_parent(&root).unwrap();
        }
        "publish" => {
            let store = SnapshotStore::new(&root.join("scratch")).unwrap();
            let mut publications = Vec::new();
            for (source, target) in [
                ("left", "left"),
                ("right", "right"),
                ("new-file", "created"),
                ("existing", "existing"),
            ] {
                let source = store.capture(&root.join("sources").join(source)).unwrap();
                publications.push(store.prepare(&source, &root.join(target)).unwrap());
            }
            let cut = std::env::var(CUT_ENV).unwrap();
            let mut batch = Batch::prepare(publications).unwrap();
            batch
                .publish_with(|event| -> io::Result<()> {
                    if event == Event::AfterBackup(3) {
                        assert_eq!(
                            fs::read(root.join("existing")).unwrap(),
                            b"old-existing-file"
                        );
                    }
                    if event == Event::AfterInstall(3) {
                        assert_eq!(
                            fs::read(root.join("existing")).unwrap(),
                            b"new-existing-file"
                        );
                    }
                    let phase = match event {
                        Event::Prepared => "prepared".to_owned(),
                        Event::AfterBackup(index) => format!("backup-{index}"),
                        Event::AfterInstall(index) => format!("install-{index}"),
                        Event::BeforeCommit => "before-commit".to_owned(),
                        Event::Committed => "committed".to_owned(),
                    };
                    if phase == cut {
                        std::process::exit(CRASH_STATUS);
                    }
                    Ok(())
                })
                .unwrap();
            batch.finalize().unwrap();
        }
        action => panic!("unsupported child action {action}"),
    }
}
