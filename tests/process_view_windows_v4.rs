#![cfg(windows)]

//! Actual signed-driver journeys. The default target only checks unavailable
//! capabilities; live gates run explicitly on a provisioned ephemeral runner.

use std::ffi::OsString;
use std::fs;
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

use msync::process::ProcessConfig;
use msync::process::view_windows::{ManagedRoot, PreparedView, ProgramSpec, Provider};
use msync::transaction::filesystem::{Entry, Snapshot, SnapshotStore};
use tokio::io::{AsyncReadExt, AsyncWriteExt};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("windows-resource-view-")
        .tempdir_in(root)
        .unwrap()
}

fn provider() -> Provider {
    Provider::probe()
        .unwrap_or_else(|failure| {
            panic!("required native Windows resource-view fixture is unavailable: {failure}")
        })
        .with_worker(env!("CARGO_BIN_EXE_msync"))
        .unwrap()
}

fn spec(mode: &str, cwd: &Path) -> ProgramSpec {
    ProgramSpec {
        executable: std::env::current_exe().unwrap(),
        arguments: [
            "--exact",
            "boxed_child_fixture",
            "--ignored",
            "--nocapture",
            "--skip",
            &format!("mode:{mode}"),
        ]
        .into_iter()
        .map(OsString::from)
        .collect(),
        cwd: cwd.to_owned(),
    }
}

fn view(workspace: &Path, store: &SnapshotStore, root: &Path) -> PreparedView {
    provider()
        .prepare(
            &workspace.join("scratch"),
            vec![ManagedRoot {
                logical: root.to_owned(),
                snapshot: store.capture(root).unwrap(),
            }],
        )
        .unwrap()
}

fn text(snapshot: &Snapshot, path: &str) -> Option<String> {
    match snapshot.entries().get(Path::new(path)) {
        Some(Entry::File { artifact, .. }) => Some(fs::read_to_string(artifact.path()).unwrap()),
        _ => None,
    }
}

#[test]
fn an_unavailable_installation_is_a_precise_capability_error_without_config_side_effects() {
    let workspace = workspace();
    let failure = Provider::from_installation(workspace.path())
        .err()
        .expect("no installed DLL in the owned fixture");
    assert_eq!(failure.phase, "capability probe");
    assert_eq!(failure.cause.kind(), std::io::ErrorKind::NotFound);
    assert_eq!(failure.path, workspace.path());
    assert_eq!(fs::read_dir(workspace.path()).unwrap().count(), 0);
}

#[tokio::test]
#[ignore = "requires installed signed Sandboxie driver/service on an ephemeral Windows runner"]
async fn frozen_roots_preserve_absolute_relative_and_descendant_io_without_host_writes() {
    let workspace = workspace();
    let root = workspace.path().join("work");
    let scratch = workspace.path().join("scratch");
    fs::create_dir_all(&root).unwrap();
    fs::create_dir_all(&scratch).unwrap();
    fs::write(root.join("keep"), "frozen baseline").unwrap();
    fs::write(root.join("delete"), "host must retain this").unwrap();
    let frozen_modified = std::time::UNIX_EPOCH + Duration::from_secs(978_307_240);
    let keep = root.join("keep");
    let mut metadata =
        msync::transaction::filesystem::NodeMetadata::capture(&fs::metadata(&keep).unwrap())
            .unwrap();
    metadata.modified = frozen_modified;
    metadata.apply(&keep).unwrap();
    let mut store = SnapshotStore::new(&scratch).unwrap();
    let prepared = view(workspace.path(), &store, &root);
    let private = prepared.root().to_owned();
    fs::write(root.join("keep"), "changed host after snapshot").unwrap();
    let running = prepared
        .launch(&spec("mutate", &root), ProcessConfig::new(&scratch))
        .await
        .unwrap();
    let pid = running.pid();
    assert_ne!(pid, running.worker_pid());
    let sealed = tokio::time::timeout(Duration::from_secs(30), running.seal(&store))
        .await
        .unwrap()
        .unwrap();
    assert!(sealed.exit.success);
    assert_eq!(sealed.exit.pid, pid);
    let snapshot = &sealed.roots[0].snapshot;
    assert_eq!(
        snapshot
            .entries()
            .get(Path::new("keep"))
            .unwrap()
            .metadata()
            .modified,
        frozen_modified
    );
    assert_eq!(text(snapshot, "keep").as_deref(), Some("frozen baseline"));
    assert_eq!(
        text(snapshot, "absolute").as_deref(),
        Some("absolute write")
    );
    assert_eq!(text(snapshot, "relative").as_deref(), Some("UTF-8 可莉🌟"));
    assert_eq!(
        text(snapshot, "grandchild").as_deref(),
        Some("descendant write")
    );
    assert_eq!(
        text(snapshot, "pid")
            .unwrap()
            .trim()
            .parse::<u32>()
            .unwrap(),
        pid
    );
    assert!(!snapshot.entries().contains_key(Path::new("delete")));
    assert_eq!(
        fs::read_to_string(root.join("keep")).unwrap(),
        "changed host after snapshot"
    );
    assert!(root.join("delete").exists());
    for name in ["absolute", "relative", "grandchild", "pid"] {
        assert!(!root.join(name).exists());
    }
    assert!(!private.exists());
    drop(sealed);
    store.cleanup().unwrap();
}

#[tokio::test]
#[ignore = "requires installed signed Sandboxie driver/service on an ephemeral Windows runner"]
async fn binary_stdio_is_forwarded_and_nonzero_payload_exit_is_replayable_data() {
    let workspace = workspace();
    let root = workspace.path().join("work");
    let scratch = workspace.path().join("scratch");
    fs::create_dir_all(&root).unwrap();
    fs::create_dir_all(&scratch).unwrap();
    let mut store = SnapshotStore::new(&scratch).unwrap();
    let mut running = view(workspace.path(), &store, &root)
        .launch(&spec("echo", &root), ProcessConfig::new(&scratch))
        .await
        .unwrap();
    let bytes = "hello 可莉🌟\n\0\r\n".repeat(4096).into_bytes();
    let mut stdin = running.take_stdin().unwrap();
    stdin.write_all(&bytes).await.unwrap();
    stdin.shutdown().await.unwrap();
    drop(stdin);
    let exit = running.wait().await.unwrap();
    assert_eq!(exit.exit_code, 7);
    assert!(!exit.success);
    assert_eq!(
        running
            .scalar(msync::hir::ReadPort::ExitCode)
            .await
            .unwrap(),
        b"7\n"
    );
    let mut output = Vec::new();
    running
        .take_stdout()
        .unwrap()
        .read_to_end(&mut output)
        .await
        .unwrap();
    let marker = b"PAYLOAD:";
    let at = output
        .windows(marker.len())
        .position(|part| part == marker)
        .unwrap();
    assert_eq!(&output[at + marker.len()..], bytes);
    let mut errors = Vec::new();
    running
        .take_stderr()
        .unwrap()
        .read_to_end(&mut errors)
        .await
        .unwrap();
    assert_eq!(errors, vec![b'e'; 768 * 1024]);
    let sealed = running.seal(&store).await.unwrap();
    assert_eq!(sealed.exit.exit_code, 7);
    drop(sealed);
    store.cleanup().unwrap();
}

#[tokio::test]
#[ignore = "requires installed signed Sandboxie driver/service on an ephemeral Windows runner"]
async fn cancel_joins_the_owned_box_and_never_exports_partial_writes() {
    let workspace = workspace();
    let root = workspace.path().join("work");
    let scratch = workspace.path().join("scratch");
    fs::create_dir_all(&root).unwrap();
    fs::create_dir_all(&scratch).unwrap();
    fs::write(root.join("baseline"), "preserved").unwrap();
    let mut store = SnapshotStore::new(&scratch).unwrap();
    let mut running = view(workspace.path(), &store, &root)
        .launch(&spec("wait", &root), ProcessConfig::new(&scratch))
        .await
        .unwrap();
    let mut stdout = running.take_stdout().unwrap();
    let mut bytes = vec![0; 4096];
    loop {
        let count = tokio::time::timeout(Duration::from_secs(10), stdout.read(&mut bytes))
            .await
            .unwrap()
            .unwrap();
        assert!(count > 0);
        if String::from_utf8_lossy(&bytes[..count]).contains("WAITING_READY") {
            break;
        }
    }
    let exit = tokio::time::timeout(Duration::from_secs(20), running.cancel())
        .await
        .unwrap()
        .unwrap();
    assert!(exit.cancelled);
    assert_eq!(
        fs::read_to_string(root.join("baseline")).unwrap(),
        "preserved"
    );
    assert!(!root.join("partial").exists());
    drop(stdout);
    store.cleanup().unwrap();
}

#[tokio::test]
#[ignore = "requires installed signed Sandboxie driver/service on an ephemeral Windows runner"]
async fn a_foreign_direct_access_rule_in_the_owned_box_invalidates_the_view_before_launch() {
    let workspace = workspace();
    let root = workspace.path().join("work");
    let scratch = workspace.path().join("scratch");
    fs::create_dir_all(&root).unwrap();
    fs::create_dir_all(&scratch).unwrap();
    let mut store = SnapshotStore::new(&scratch).unwrap();
    let prepared = view(workspace.path(), &store, &root);
    let install = std::env::var_os("MSYNC_SANDBOXIE_HOME")
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            PathBuf::from(std::env::var_os("ProgramFiles").unwrap()).join("Sandboxie-Plus")
        });
    let status = Command::new(install.join("SbieIni.exe"))
        .args(["set", prepared.box_name(), "OpenFilePath"])
        .arg(root.join("*"))
        .status()
        .unwrap();
    assert!(status.success());
    let failure = prepared.revalidate().unwrap_err();
    assert_eq!(failure.cause.kind(), io::ErrorKind::PermissionDenied);
    prepared.discard().unwrap();
    store.cleanup().unwrap();
}

#[test]
#[ignore = "subprocess fixture; explicitly invoked only inside the owned sandbox"]
fn boxed_child_fixture() {
    let mode = std::env::args()
        .find_map(|arg| arg.strip_prefix("mode:").map(str::to_owned))
        .unwrap();
    let root = std::env::current_dir().unwrap();
    match mode.as_str() {
        "mutate" => {
            assert_eq!(fs::read_to_string("keep").unwrap(), "frozen baseline");
            fs::write(root.join("absolute"), "absolute write").unwrap();
            fs::write("relative", "UTF-8 可莉🌟").unwrap();
            fs::write("pid", std::process::id().to_string()).unwrap();
            fs::remove_file("delete").unwrap();
            let status = Command::new(std::env::current_exe().unwrap())
                .args([
                    "--exact",
                    "boxed_child_fixture",
                    "--ignored",
                    "--nocapture",
                    "--skip",
                    "mode:grandchild",
                ])
                .status()
                .unwrap();
            assert!(status.success());
        }
        "grandchild" => {
            fs::write("grandchild", "descendant write").unwrap();
        }
        "echo" => {
            std::io::stderr()
                .write_all(&vec![b'e'; 768 * 1024])
                .unwrap();
            let mut bytes = Vec::new();
            std::io::stdin().read_to_end(&mut bytes).unwrap();
            std::io::stdout().write_all(b"PAYLOAD:").unwrap();
            std::io::stdout().write_all(&bytes).unwrap();
            std::process::exit(7);
        }
        "wait" => {
            fs::write("partial", "must never become host data").unwrap();
            println!("WAITING_READY");
            std::io::stdout().flush().unwrap();
            std::thread::sleep(Duration::from_secs(60));
        }
        _ => panic!("unknown fixture mode"),
    }
}
