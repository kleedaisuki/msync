//! Publication staging must be discoverable before copying its first payload.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output};

use msync::filesystem::SnapshotStore;
use msync::filesystem::recovery::{self, Registry};

const ROOT: &str = "MSYNC_STAGE_CRASH_TEST_ROOT";
const ACTION: &str = "MSYNC_STAGE_CRASH_TEST_ACTION";
const USER_DIRECTORY: &str = ".msync-publication-user-data";

fn workspace() -> tempfile::TempDir {
    let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&base).unwrap();
    tempfile::Builder::new()
        .prefix("storage-stage-crash-")
        .tempdir_in(base)
        .unwrap()
}

fn invoke(root: &Path, action: &str) -> Output {
    let mut child = Command::new(std::env::current_exe().unwrap());
    child
        .args([
            "--exact",
            "storage_stage_crash_child",
            "--ignored",
            "--nocapture",
        ])
        .env(ROOT, root)
        .env(ACTION, action);
    if action == "stage" {
        child
            .env("MSYNC_TEST_CRASH_AT", "stage-materializing")
            .env("MSYNC_TEST_CRASH_TARGET", root.join("target"));
    } else {
        child
            .env_remove("MSYNC_TEST_CRASH_AT")
            .env_remove("MSYNC_TEST_CRASH_TARGET");
    }
    child.output().unwrap()
}

fn detail(output: &Output) -> String {
    format!(
        "status={}\nstdout={}\nstderr={}",
        output.status,
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    )
}

fn stages(root: &Path) -> Vec<PathBuf> {
    let mut paths: Vec<_> = fs::read_dir(root)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|path| {
            let name = path.file_name().unwrap().to_string_lossy();
            path.is_dir()
                && path.file_name().unwrap() != USER_DIRECTORY
                && (name.starts_with(".msync-publication-")
                    || name.starts_with(".msync-transaction-")
                    || name.starts_with(".msync-statement-"))
        })
        .collect();
    paths.sort();
    paths
}

fn has_file_named(path: &Path, name: &str) -> bool {
    fs::read_dir(path).unwrap().any(|entry| {
        let path = entry.unwrap().path();
        if path.is_dir() {
            has_file_named(&path, name)
        } else {
            path.file_name().unwrap() == name
        }
    })
}

fn assert_target_unchanged(root: &Path) {
    assert_eq!(
        fs::read(root.join("target/old")).unwrap(),
        b"old target data"
    );
    assert_eq!(fs::read_dir(root.join("target")).unwrap().count(), 1);
    assert_eq!(
        fs::read(root.join(USER_DIRECTORY).join("keep")).unwrap(),
        b"unrelated user data"
    );
}

#[test]
fn crash_mid_stage_is_discovered_and_only_the_owned_stage_is_removed() {
    let root = workspace();
    fs::create_dir(root.path().join("source")).unwrap();
    fs::write(root.path().join("source/00-first"), vec![b'x'; 512 * 1024]).unwrap();
    fs::write(
        root.path().join("source/99-last"),
        b"must not be copied before crash",
    )
    .unwrap();
    fs::create_dir(root.path().join("target")).unwrap();
    fs::write(root.path().join("target/old"), b"old target data").unwrap();
    fs::create_dir(root.path().join(USER_DIRECTORY)).unwrap();
    fs::write(
        root.path().join(USER_DIRECTORY).join("keep"),
        b"unrelated user data",
    )
    .unwrap();

    let output = invoke(root.path(), "stage");
    assert_eq!(output.status.code(), Some(86), "{}", detail(&output));
    assert_target_unchanged(root.path());
    let pending = stages(root.path());
    assert_eq!(
        pending.len(),
        1,
        "expected precisely one interrupted private stage: {pending:?}"
    );
    assert!(
        has_file_named(&pending[0], "00-first"),
        "crash must occur after actual payload copy"
    );
    assert!(
        !has_file_named(&pending[0], "99-last"),
        "crash must interrupt, not follow, complete materialization"
    );
    let registry = root.path().join("scratch/.msync-recovery-v1");
    assert!(
        fs::read_dir(&registry).unwrap().any(|entry| entry
            .unwrap()
            .file_name()
            .to_string_lossy()
            .starts_with("record-")),
        "stage must be registered before payload materialization"
    );

    for _ in 0..2 {
        let output = invoke(root.path(), "recover");
        assert!(output.status.success(), "{}", detail(&output));
        assert_target_unchanged(root.path());
        assert!(
            stages(root.path()).is_empty(),
            "owned stage/coordinator survived recovery"
        );
        for entry in fs::read_dir(&registry).unwrap() {
            assert_eq!(
                entry.unwrap().file_name(),
                ".lock",
                "stage recovery record survived"
            );
        }
    }
}

#[test]
#[ignore = "fixture subprocess invoked by the parent test"]
fn storage_stage_crash_child() {
    let Some(root) = std::env::var_os(ROOT) else {
        return;
    };
    let root = PathBuf::from(root);
    let scratch = root.join("scratch");
    match std::env::var(ACTION).unwrap().as_str() {
        "stage" => {
            let registry = Registry::new(&scratch).unwrap();
            let lease = registry.enter().unwrap();
            let store = SnapshotStore::new(&scratch).unwrap();
            let source = store.capture(&root.join("source")).unwrap();
            let _staged = store
                .prepare_registered(&source, &root.join("target"), &lease)
                .unwrap();
            panic!("stage crash hook did not interrupt payload materialization");
        }
        "recover" => {
            recovery::recover(&scratch).unwrap();
        }
        action => panic!("unknown child action {action}"),
    }
}
