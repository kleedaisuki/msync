use std::fs;
use std::path::Path;
#[cfg(windows)]
use std::path::PathBuf;
use std::time::{Duration, UNIX_EPOCH};

use msync::compiler::CompileContext;
use msync::compiler::structured::Session;
use msync::execution::{self, Options, RuntimeSession};
use msync::pipeline;
use msync::transaction::filesystem::{InvocationRoot, SnapshotStore};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("execution-workspace-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn set_modified(path: &Path) {
    let mut options = fs::OpenOptions::new();
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_FLAG_BACKUP_SEMANTICS, FILE_WRITE_ATTRIBUTES,
        };
        options
            .access_mode(FILE_WRITE_ATTRIBUTES)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS);
    }
    #[cfg(not(windows))]
    options.read(true);
    options
        .open(path)
        .unwrap()
        .set_times(
            fs::FileTimes::new().set_modified(UNIX_EPOCH + Duration::from_secs(1_000_000_000)),
        )
        .unwrap();
}

#[test]
fn capture_omits_only_the_current_owned_invocation_and_retains_user_temp_data() {
    let workspace = workspace();
    let source = workspace.path().join("work");
    let scratch = source.join(".temp");
    fs::create_dir_all(&scratch).unwrap();
    fs::write(source.join("source"), b"source").unwrap();
    fs::write(scratch.join("user-sentinel"), b"preserve user temp").unwrap();
    set_modified(&scratch);
    let before = fs::metadata(&scratch).unwrap().modified().unwrap();
    let invocation = InvocationRoot::create(&scratch).unwrap();
    assert!(invocation.scaffold_directory(&scratch).unwrap().is_none());
    let mut store = SnapshotStore::new_in(&invocation.path().join("assets"), &invocation).unwrap();
    assert!(
        store.capture(&source).is_err(),
        "unqualified capture retains its strict self-capture guard"
    );
    let snapshot = store.capture_in(&source, &invocation).unwrap();
    assert!(
        snapshot
            .entries()
            .contains_key(Path::new(".temp/user-sentinel"))
    );
    assert!(snapshot.excludes_invocation(&invocation).unwrap());
    assert!(
        snapshot
            .excludes_private_path(&invocation.path().join("future-output"))
            .unwrap()
    );
    let relative = Path::new(".temp").join(invocation.path().file_name().unwrap());
    assert!(
        !snapshot
            .entries()
            .keys()
            .any(|path| path.starts_with(&relative))
    );
    assert!(store.capture_in(invocation.path(), &invocation).is_err());
    let mut forged = snapshot.entries().clone();
    forged.insert(relative.clone(), snapshot.entries()[Path::new("")].clone());
    assert!(store.from_entries_in(forged, &source, &invocation).is_err());
    #[cfg(windows)]
    {
        let alias = Path::new(".temp").join(
            invocation
                .path()
                .file_name()
                .unwrap()
                .to_string_lossy()
                .to_uppercase(),
        );
        let mut forged = snapshot.entries().clone();
        forged.insert(alias, snapshot.entries()[Path::new("")].clone());
        assert!(store.from_entries_in(forged, &source, &invocation).is_err());
    }
    let entries = snapshot.entries().clone();
    let rebuilt = store
        .from_entries_in(entries, &source, &invocation)
        .unwrap();
    assert!(rebuilt.excludes_invocation(&invocation).unwrap());
    drop(rebuilt);
    drop(snapshot);
    store.cleanup().unwrap();
    invocation.cleanup().unwrap();
    assert_eq!(
        fs::read(scratch.join("user-sentinel")).unwrap(),
        b"preserve user temp"
    );
    assert_eq!(fs::metadata(&scratch).unwrap().modified().unwrap(), before);
    assert_eq!(fs::read_dir(&scratch).unwrap().count(), 1);
}

#[test]
fn fresh_internal_scratch_is_not_invented_as_logical_source_data() {
    let workspace = workspace();
    let source = workspace.path().join("work");
    fs::create_dir(&source).unwrap();
    fs::write(source.join("source"), b"source").unwrap();
    set_modified(&source);
    let before = fs::metadata(&source).unwrap().modified().unwrap();
    let scratch = source.join(".temp");
    let invocation = InvocationRoot::create(&scratch).unwrap();
    assert!(invocation.scaffold_directory(&scratch).unwrap().is_some());
    assert!(invocation.scaffold_directory(&source).unwrap().is_none());
    let mut store = SnapshotStore::new_in(&invocation.path().join("assets"), &invocation).unwrap();
    let snapshot = store.capture_in(&source, &invocation).unwrap();
    assert!(!snapshot.entries().contains_key(Path::new(".temp")));
    assert_eq!(
        snapshot.entries()[Path::new("")].metadata().modified,
        before
    );
    drop(snapshot);
    store.cleanup().unwrap();
    invocation.cleanup().unwrap();
    assert_eq!(fs::metadata(&source).unwrap().modified().unwrap(), before);
    assert!(
        scratch.is_dir(),
        "configured scratch remains available to callers"
    );
    assert_eq!(fs::read_dir(&scratch).unwrap().count(), 0);
}

#[test]
fn replacing_the_owned_identity_does_not_authorize_cleaning_the_foreign_replacement() {
    let workspace = workspace();
    let invocation = InvocationRoot::create(workspace.path()).unwrap();
    let moved = workspace.path().join("retained-original");
    fs::rename(invocation.path(), &moved).unwrap();
    fs::create_dir(invocation.path()).unwrap();
    fs::write(invocation.path().join("foreign"), b"not owned").unwrap();
    assert!(invocation.verify().is_err());
    assert!(invocation.cleanup().is_err());
    assert_eq!(
        fs::read(invocation.path().join("foreign")).unwrap(),
        b"not owned"
    );
}

#[test]
fn live_stores_and_snapshots_prevent_early_invocation_cleanup() {
    let workspace = workspace();
    fs::write(workspace.path().join("source"), b"retained bytes").unwrap();
    let invocation = InvocationRoot::create(workspace.path()).unwrap();
    let mut store = SnapshotStore::new_in(&invocation.path().join("assets"), &invocation).unwrap();
    let snapshot = store.capture(&workspace.path().join("source")).unwrap();
    assert!(invocation.cleanup().is_err());
    assert!(store.cleanup().is_err());
    assert!(invocation.path().exists());
    drop(snapshot);
    store.cleanup().unwrap();
    invocation.cleanup().unwrap();
    assert!(!invocation.path().exists());
    invocation.cleanup().unwrap();
}

fn run(cwd: &Path, source: &str) -> execution::ExecutionReport {
    let context = CompileContext {
        pwd: cwd.to_owned(),
        source_file: None,
    };
    let compilation = pipeline::compile(&mut Session::new(), source, &context, &[]).unwrap();
    execution::execute(&compilation.program, &Options::new(cwd.join(".temp"))).unwrap()
}

#[test]
fn copying_the_cwd_with_default_scratch_preserves_user_temp_without_recursive_runtime_data() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir_all(cwd.join(".temp")).unwrap();
    fs::write(cwd.join("source"), b"copied source").unwrap();
    fs::write(cwd.join(".temp/user-sentinel"), b"copied user temp").unwrap();
    let report = run(&cwd, "$pwd::pwd -> $pwd::pwd/../out/.\n");
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
    assert_eq!(
        fs::read(workspace.path().join("out/source")).unwrap(),
        b"copied source"
    );
    assert_eq!(
        fs::read(workspace.path().join("out/.temp/user-sentinel")).unwrap(),
        b"copied user temp"
    );
    assert_eq!(
        fs::read_dir(workspace.path().join("out/.temp"))
            .unwrap()
            .count(),
        1
    );
    assert_eq!(fs::read_dir(cwd.join(".temp")).unwrap().count(), 1);
}

#[test]
fn origin_undo_restores_the_containing_namespace_with_default_internal_scratch() {
    for existing_scratch in [false, true] {
        let workspace = workspace();
        let cwd = workspace.path().join("work");
        fs::create_dir(&cwd).unwrap();
        if existing_scratch {
            fs::create_dir(cwd.join(".temp")).unwrap();
        }
        fs::write(cwd.join("kept"), b"unchanged").unwrap();
        set_modified(&cwd);
        let before = fs::metadata(&cwd).unwrap().modified().unwrap();
        let report = run(
            &cwd,
            "transaction A:\n    \"new\" -> result\n    commit\n    \"later\" -> other\nrollback A\n",
        );
        assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
        assert!(!cwd.join("result").exists());
        assert!(!cwd.join("other").exists());
        assert_eq!(fs::read(cwd.join("kept")).unwrap(), b"unchanged");
        assert_eq!(fs::metadata(&cwd).unwrap().modified().unwrap(), before);
        assert_eq!(fs::read_dir(cwd.join(".temp")).unwrap().count(), 0);
    }
}

#[test]
fn cleanup_preserves_committed_user_entries_and_namespace_time_in_configured_scratch() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    let scratch = cwd.join(".temp");
    fs::create_dir_all(&scratch).unwrap();
    fs::write(scratch.join("sentinel"), b"existing user data").unwrap();
    set_modified(&scratch);
    let before = fs::metadata(&scratch).unwrap().modified().unwrap();
    let report = run(&cwd, "\"committed\" -> .temp/result\n");
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
    assert_eq!(fs::read(scratch.join("result")).unwrap(), b"committed");
    assert_eq!(
        fs::read(scratch.join("sentinel")).unwrap(),
        b"existing user data"
    );
    assert!(fs::metadata(&scratch).unwrap().modified().unwrap() > before);
    assert_eq!(fs::read_dir(&scratch).unwrap().count(), 2);
}

#[test]
fn fresh_configured_scratch_accepts_an_ordinary_user_target() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    let report = run(&cwd, "\"committed\" -> $pwd::pwd/.temp/user-file\n");
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
    assert_eq!(fs::read(cwd.join(".temp/user-file")).unwrap(), b"committed");
    assert_eq!(fs::read_dir(cwd.join(".temp")).unwrap().count(), 1);
}

#[test]
fn fresh_scratch_user_namespace_can_be_copied_and_rolled_back() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    set_modified(&cwd);
    let before = fs::metadata(&cwd).unwrap().modified().unwrap();
    let report = run(
        &cwd,
        "transaction A:\n    \"committed\" -> $pwd::pwd/.temp/user-file\n    commit\n    $pwd::pwd/.temp -> $pwd::pwd/copied/.\nrollback A\n",
    );
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
    assert!(!cwd.join(".temp/user-file").exists());
    assert!(!cwd.join("copied").exists());
    assert_eq!(fs::read_dir(cwd.join(".temp")).unwrap().count(), 0);
    assert_eq!(fs::metadata(&cwd).unwrap().modified().unwrap(), before);
}

#[tokio::test]
async fn finite_runtime_accepts_fresh_scratch_user_entries_then_named_undo() {
    for existing_scratch in [false, true] {
        let workspace = workspace();
        let cwd = workspace.path().join("work");
        fs::create_dir(&cwd).unwrap();
        if existing_scratch {
            fs::create_dir(cwd.join(".temp")).unwrap();
            fs::write(cwd.join(".temp/sentinel"), b"retain user data").unwrap();
        }
        let context = CompileContext {
            pwd: cwd.clone(),
            source_file: None,
        };
        let mut compiler = Session::new();
        let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
        for script in [
            "transaction A:\n    \"committed\" -> $pwd::pwd/.temp/user-file\n",
            "$pwd::pwd/.temp -> $pwd::pwd/../copied/.\n",
            "rollback A\n",
        ] {
            let compilation = pipeline::compile(&mut compiler, script, &context, &[]).unwrap();
            runtime.execute(&compilation.program).await.unwrap();
            if script.starts_with("transaction") {
                assert_eq!(fs::read(cwd.join(".temp/user-file")).unwrap(), b"committed");
            }
            if script.starts_with("$pwd") {
                assert_eq!(
                    fs::read(workspace.path().join("copied/user-file")).unwrap(),
                    b"committed"
                );
                assert_eq!(
                    fs::read_dir(workspace.path().join("copied"))
                        .unwrap()
                        .count(),
                    if existing_scratch { 2 } else { 1 }
                );
            }
        }
        runtime.shutdown().await.unwrap();
        assert!(!cwd.join(".temp/user-file").exists());
        assert_eq!(
            fs::read_dir(cwd.join(".temp")).unwrap().count(),
            usize::from(existing_scratch)
        );
        assert_eq!(
            fs::read(workspace.path().join("copied/user-file")).unwrap(),
            b"committed"
        );
        if existing_scratch {
            assert_eq!(
                fs::read(cwd.join(".temp/sentinel")).unwrap(),
                b"retain user data"
            );
        }
    }
}

#[test]
#[ignore = "native child fixture, launched only by the mediated process test"]
fn scratch_namespace_child_fixture() {
    fs::create_dir_all(".temp").unwrap();
    fs::write(".temp/user-file", b"native child bytes").unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn native_program_creates_user_scratch_then_copy_and_origin_undo() {
    for existing_scratch in [false, true] {
        let workspace = workspace();
        let cwd = workspace.path().join("work");
        fs::create_dir(&cwd).unwrap();
        if existing_scratch {
            fs::create_dir(cwd.join(".temp")).unwrap();
            fs::write(cwd.join(".temp/sentinel"), b"retain user data").unwrap();
        }
        let executable = std::env::current_exe()
            .unwrap()
            .to_string_lossy()
            .replace('\\', "\\\\")
            .replace('"', "\\\"");
        let launch = format!(
            "transaction A:\n    child => $local::\"{executable}\"(\"--exact\", \"scratch_namespace_child_fixture\", \"--ignored\", \"--nocapture\")\n"
        );
        let context = CompileContext {
            pwd: cwd.clone(),
            source_file: None,
        };
        let mut compiler = Session::new();
        let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
        runtime.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
        for script in [
            launch.as_str(),
            "$pwd::pwd/.temp -> $pwd::pwd/../copied/.\n",
            "rollback A\n",
        ] {
            let compilation = pipeline::compile(&mut compiler, script, &context, &[]).unwrap();
            tokio::time::timeout(
                std::time::Duration::from_secs(30),
                runtime.execute(&compilation.program),
            )
            .await
            .unwrap()
            .unwrap();
        }
        runtime.shutdown().await.unwrap();
        assert!(!cwd.join(".temp/user-file").exists());
        assert_eq!(
            fs::read(workspace.path().join("copied/user-file")).unwrap(),
            b"native child bytes"
        );
        assert_eq!(
            fs::read_dir(cwd.join(".temp")).unwrap().count(),
            usize::from(existing_scratch)
        );
        assert_eq!(
            fs::read_dir(workspace.path().join("copied"))
                .unwrap()
                .count(),
            if existing_scratch { 2 } else { 1 }
        );
        if existing_scratch {
            assert_eq!(
                fs::read(cwd.join(".temp/sentinel")).unwrap(),
                b"retain user data"
            );
        }
    }
}

#[cfg(windows)]
#[test]
fn ordinary_and_verbatim_aliases_cannot_bypass_the_owned_exclusion() {
    let workspace = workspace();
    let invocation = InvocationRoot::create(workspace.path()).unwrap();
    let mut store = SnapshotStore::new_in(&invocation.path().join("assets"), &invocation).unwrap();
    let text = invocation.path().to_string_lossy();
    let ordinary = PathBuf::from(text.strip_prefix(r"\\?\").unwrap_or(&text));
    assert!(store.capture_in(&ordinary, &invocation).is_err());
    let root_text = workspace.path().to_string_lossy();
    let ordinary_root = PathBuf::from(root_text.strip_prefix(r"\\?\").unwrap_or(&root_text));
    let snapshot = store.capture_in(&ordinary_root, &invocation).unwrap();
    assert!(snapshot.excludes_invocation(&invocation).unwrap());
    assert!(
        !snapshot
            .entries()
            .keys()
            .any(|path| path.starts_with(invocation.path().file_name().unwrap()))
    );
    drop(snapshot);
    store.cleanup().unwrap();
    invocation.cleanup().unwrap();
}
