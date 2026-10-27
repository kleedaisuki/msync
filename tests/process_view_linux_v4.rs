#![cfg(target_os = "linux")]

//! Actual immutable-root / namespace journeys. Run explicitly after provisioning
//! bubblewrap and unprivileged user namespaces; unavailable capability is failure.
//!
//! Development verification: WSL UID 1000, bubblewrap 0.9.0, private native tmpfs
//! mounted at this project's `.temp` in `unshare -Urnm`. All six journeys pass;
//! the same initial binary's directory publications failed with EPERM on DrvFs.
//! This separates the directory-publisher filesystem capability from namespace
//! correctness; it is not a storage-throughput measurement. Compile outside the
//! mount, then run the target with `--ignored --skip view_child_fixture` inside
//! that private namespace. No persistent mounts or host namespace policy changes.

use std::ffi::OsString;
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

use msync::hir::ReadPort;
use msync::process::ProcessConfig;
use msync::process::view_linux::{ManagedRoot, ProgramSpec, Provider};
use msync::transaction::filesystem::{Entry, Snapshot, SnapshotStore};
use tokio::io::{AsyncBufReadExt, AsyncReadExt, BufReader};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("linux-resource-view-")
        .tempdir_in(root)
        .unwrap()
}

fn provider() -> Provider {
    Provider::probe()
        .and_then(|provider| provider.with_worker(env!("CARGO_BIN_EXE_msync")))
        .unwrap_or_else(|failure| {
            panic!("required Linux resource-view fixture is unavailable: {failure}")
        })
}

fn spec(mode: &str, cwd: &Path, extra: Option<&Path>) -> ProgramSpec {
    let mut arguments = vec![
        "--exact".into(),
        "view_child_fixture".into(),
        "--ignored".into(),
        "--nocapture".into(),
        "--skip".into(),
        format!("mode:{mode}").into(),
    ];
    if let Some(path) = extra {
        arguments.extend([
            OsString::from("--skip"),
            format!("path:{}", path.display()).into(),
        ]);
    }
    ProgramSpec {
        executable: std::env::current_exe().unwrap(),
        arguments,
        cwd: cwd.to_owned(),
    }
}

fn snapshot_text(snapshot: &Snapshot, relative: &str) -> Option<String> {
    match snapshot.entries().get(Path::new(relative)) {
        Some(Entry::File { artifact, .. }) => Some(fs::read_to_string(artifact.path()).unwrap()),
        _ => None,
    }
}

#[tokio::test]
#[ignore = "requires bubblewrap and usable unprivileged user/mount/PID/network namespaces"]
async fn absolute_relative_and_grandchild_writes_only_change_the_private_frozen_root() {
    let workspace = workspace();
    let logical = workspace.path().join("work");
    let scratch = workspace.path().join("scratch");
    fs::create_dir_all(&logical).unwrap();
    fs::create_dir_all(&scratch).unwrap();
    fs::write(logical.join("keep"), "frozen before transaction").unwrap();
    fs::write(logical.join("delete"), "delete in private view").unwrap();
    let frozen_modified = std::time::UNIX_EPOCH + Duration::from_secs(978_307_230);
    fs::File::open(logical.join("keep"))
        .unwrap()
        .set_times(fs::FileTimes::new().set_modified(frozen_modified))
        .unwrap();
    let mut store = SnapshotStore::new(&scratch).unwrap();
    let baseline = store.capture(&logical).unwrap();
    let prepared = provider()
        .prepare(
            &store,
            &scratch,
            vec![ManagedRoot {
                logical: logical.clone(),
                snapshot: baseline.clone(),
            }],
        )
        .unwrap();
    let private = prepared.root().to_owned();
    let effects = prepared.effects().clone();
    assert_eq!(effects.footprint().accesses.len(), 2);
    // A mounted live lowerdir would expose this newer host value. The child must
    // instead see the previously captured immutable resource version.
    fs::write(logical.join("keep"), "newer live host value").unwrap();
    let running = prepared
        .launch(
            &spec("mutate", &logical, None),
            ProcessConfig::new(&scratch),
        )
        .unwrap();
    let mut sealed = tokio::time::timeout(Duration::from_secs(15), running.seal(&store))
        .await
        .unwrap()
        .unwrap();
    assert!(sealed.exit.success);
    assert_eq!(sealed.effects, effects);
    assert_eq!(
        fs::read_to_string(logical.join("keep")).unwrap(),
        "newer live host value"
    );
    assert!(logical.join("delete").exists());
    for name in ["absolute", "relative", "grandchild"] {
        assert!(!logical.join(name).exists());
    }
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
    assert_eq!(
        snapshot_text(snapshot, "keep").as_deref(),
        Some("frozen before transaction")
    );
    assert_eq!(
        snapshot_text(snapshot, "absolute").as_deref(),
        Some("absolute write")
    );
    assert_eq!(
        snapshot_text(snapshot, "relative").as_deref(),
        Some("UTF-8 可莉🌟")
    );
    assert_eq!(
        snapshot_text(snapshot, "grandchild").as_deref(),
        Some("descendant write")
    );
    assert!(!snapshot.entries().contains_key(Path::new("delete")));
    let mut output = Vec::new();
    sealed
        .process
        .take_stdout()
        .unwrap()
        .read_to_end(&mut output)
        .await
        .unwrap();
    assert!(String::from_utf8_lossy(&output).contains("PRIVATE_WRITE_COMPLETE"));
    assert!(!private.exists());
    drop(sealed);
    drop(baseline);
    store.cleanup().unwrap();
}

#[tokio::test]
#[ignore = "requires bubblewrap and usable unprivileged user/mount/PID/network namespaces"]
async fn host_writes_are_denied_and_nonzero_program_exit_remains_data() {
    let workspace = workspace();
    let logical = workspace.path().join("work");
    let scratch = workspace.path().join("scratch");
    let outside = workspace.path().join("outside");
    fs::create_dir_all(&logical).unwrap();
    fs::create_dir_all(&scratch).unwrap();
    fs::write(&outside, "host bytes").unwrap();
    let mut store = SnapshotStore::new(&scratch).unwrap();
    let prepared = provider()
        .prepare(
            &store,
            &scratch,
            vec![ManagedRoot {
                logical: logical.clone(),
                snapshot: store.capture(&logical).unwrap(),
            }],
        )
        .unwrap();
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let mut program = spec("denied", &logical, Some(&outside));
    program.arguments.extend([
        OsString::from("--skip"),
        format!("network:{}", listener.local_addr().unwrap()).into(),
    ]);
    let running = prepared
        .launch(&program, ProcessConfig::new(&scratch))
        .unwrap();
    let sealed = tokio::time::timeout(Duration::from_secs(15), running.seal(&store))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(sealed.exit.exit_code, 7);
    assert!(!sealed.exit.success);
    assert_eq!(fs::read_to_string(&outside).unwrap(), "host bytes");
    assert_eq!(
        listener.accept().unwrap_err().kind(),
        std::io::ErrorKind::WouldBlock
    );
    assert_eq!(
        snapshot_text(&sealed.roots[0].snapshot, "before-error").as_deref(),
        Some("still private")
    );
    drop(sealed);
    store.cleanup().unwrap();
}

#[tokio::test]
#[ignore = "requires bubblewrap and usable unprivileged user/mount/PID/network namespaces"]
async fn setup_or_exec_failure_is_not_confused_with_an_executed_program_exit_code() {
    let workspace = workspace();
    let logical = workspace.path().join("work");
    let scratch = workspace.path().join("scratch");
    fs::create_dir_all(&logical).unwrap();
    fs::create_dir_all(&scratch).unwrap();
    let mut store = SnapshotStore::new(&scratch).unwrap();
    let prepared = provider()
        .prepare(
            &store,
            &scratch,
            vec![ManagedRoot {
                logical: logical.clone(),
                snapshot: store.capture(&logical).unwrap(),
            }],
        )
        .unwrap();
    let spec = ProgramSpec {
        executable: logical.join("not-an-executable"),
        arguments: vec![],
        cwd: logical,
    };
    let running = prepared
        .launch(&spec, ProcessConfig::new(&scratch))
        .unwrap();
    let failure = match tokio::time::timeout(Duration::from_secs(15), running.seal(&store))
        .await
        .unwrap()
    {
        Ok(_) => panic!("missing executable must be a view launch failure"),
        Err(failure) => failure,
    };
    assert_eq!(failure.phase, "namespace setup or executable launch");
    assert!(failure.path.ends_with("not-an-executable"));
    assert_eq!(failure.cause.raw_os_error(), Some(libc::ENOENT));
    store.cleanup().unwrap();
}

#[test]
#[ignore = "requires bubblewrap and usable unprivileged user/mount/PID/network namespaces"]
fn resource_layout_rejects_aliases_reserved_roots_and_scratch_overlap() {
    use std::os::unix::fs::symlink;
    let workspace = workspace();
    let logical = workspace.path().join("work");
    let scratch = workspace.path().join("scratch");
    fs::create_dir_all(logical.join("child")).unwrap();
    fs::create_dir_all(&scratch).unwrap();
    let mut store = SnapshotStore::new(&scratch).unwrap();
    let baseline = store.capture(&logical).unwrap();
    let roots = vec![
        ManagedRoot {
            logical: logical.clone(),
            snapshot: baseline.clone(),
        },
        ManagedRoot {
            logical: logical.join("child"),
            snapshot: store.capture(&logical.join("child")).unwrap(),
        },
    ];
    assert!(provider().prepare(&store, &scratch, roots).is_err());
    symlink(&logical, workspace.path().join("alias")).unwrap();
    let root = |path| {
        vec![ManagedRoot {
            logical: path,
            snapshot: baseline.clone(),
        }]
    };
    assert!(
        provider()
            .prepare(&store, &scratch, root(workspace.path().join("alias")))
            .is_err()
    );
    assert!(
        provider()
            .prepare(&store, &scratch, root(scratch.clone()))
            .is_err()
    );
    assert!(
        provider()
            .prepare(&store, &scratch, root(PathBuf::from("/proc")))
            .is_err()
    );
    drop(baseline);
    store.cleanup().unwrap();
}

#[tokio::test]
#[ignore = "requires bubblewrap and usable unprivileged user/mount/PID/network namespaces"]
async fn cancelled_views_join_before_discard_and_cannot_export_partial_versions() {
    let workspace = workspace();
    let logical = workspace.path().join("work");
    let scratch = workspace.path().join("scratch");
    fs::create_dir_all(&logical).unwrap();
    fs::create_dir_all(&scratch).unwrap();
    let mut store = SnapshotStore::new(&scratch).unwrap();
    let prepared = provider()
        .prepare(
            &store,
            &scratch,
            vec![ManagedRoot {
                logical: logical.clone(),
                snapshot: store.capture(&logical).unwrap(),
            }],
        )
        .unwrap();
    let private = prepared.root().to_owned();
    let mut running = prepared
        .launch(
            &spec("cancel", &logical, None),
            ProcessConfig::new(&scratch),
        )
        .unwrap();
    let mut output = BufReader::new(running.process_mut().take_stdout().unwrap());
    tokio::time::timeout(Duration::from_secs(10), async {
        let mut line = String::new();
        while !line.contains("PRIVATE_LIVE") {
            line.clear();
            assert_ne!(output.read_line(&mut line).await.unwrap(), 0);
        }
    })
    .await
    .unwrap();
    running.process_mut().cancel_and_wait().await.unwrap();
    let failure = match running.seal(&store).await {
        Ok(_) => panic!("cancelled partial view must never be exported"),
        Err(failure) => failure,
    };
    assert_eq!(failure.cause.kind(), std::io::ErrorKind::Interrupted);
    assert!(!private.exists());
    assert!(!logical.join("partial").exists());
    drop(output);
    store.cleanup().unwrap();
}

#[tokio::test]
#[ignore = "requires bubblewrap and usable unprivileged user/mount/PID/network namespaces"]
async fn program_pid_is_the_kernel_authenticated_payload_not_the_bubblewrap_supervisor() {
    let workspace = workspace();
    let logical = workspace.path().join("work");
    let scratch = workspace.path().join("scratch");
    fs::create_dir_all(&logical).unwrap();
    fs::create_dir_all(&scratch).unwrap();
    let mut store = SnapshotStore::new(&scratch).unwrap();
    let prepared = provider()
        .prepare(
            &store,
            &scratch,
            vec![ManagedRoot {
                logical: logical.clone(),
                snapshot: store.capture(&logical).unwrap(),
            }],
        )
        .unwrap();
    let program = spec("pid", &logical, None);
    let mut running = prepared
        .launch(&program, ProcessConfig::new(&scratch))
        .unwrap();
    let supervisor = running.process_mut().pid();
    let host = tokio::time::timeout(Duration::from_secs(10), running.program_pid())
        .await
        .unwrap()
        .unwrap();
    let namespace = running.namespace_pid().await.unwrap();
    assert_ne!(host, supervisor, "the monitor is not the target program");
    let status = fs::read_to_string(format!("/proc/{host}/status")).unwrap();
    let namespace_ids = status
        .lines()
        .find_map(|line| line.strip_prefix("NSpid:"))
        .unwrap()
        .split_whitespace()
        .map(|part| part.parse::<u32>().unwrap())
        .collect::<Vec<_>>();
    assert_eq!(namespace_ids.first().copied(), Some(host));
    assert_eq!(namespace_ids.last().copied(), Some(namespace));
    let command_line = fs::read(format!("/proc/{host}/cmdline")).unwrap();
    let fields = command_line
        .split(|byte| *byte == 0)
        .filter(|field| !field.is_empty())
        .collect::<Vec<_>>();
    use std::os::unix::ffi::OsStrExt;
    assert_eq!(fields[0], program.executable.as_os_str().as_bytes());
    assert!(
        !fields
            .iter()
            .any(|field| *field == msync::process::view_linux::WORKER_FLAG.as_bytes())
    );
    assert_eq!(
        running.scalar(ReadPort::Pid).await.unwrap(),
        format!("{host}\n").as_bytes()
    );
    let mut output = BufReader::new(running.process_mut().take_stdout().unwrap());
    tokio::time::timeout(Duration::from_secs(10), async {
        let mut line = String::new();
        loop {
            line.clear();
            assert_ne!(output.read_line(&mut line).await.unwrap(), 0);
            if let Some(pid) = line.trim().strip_prefix("PAYLOAD_NAMESPACE_PID=") {
                assert_eq!(pid.parse::<u32>().unwrap(), namespace);
                break;
            }
        }
    })
    .await
    .unwrap();
    running.process_mut().close_stdin();
    let meta = String::from_utf8(running.scalar(ReadPort::Meta).await.unwrap()).unwrap();
    assert!(meta.contains(&format!("\"pid\":{host},")));
    assert!(meta.contains(&format!("\"supervisor_pid\":{supervisor},")));
    let sealed = running.seal(&store).await.unwrap();
    assert_eq!(sealed.exit.pid, host);
    assert_eq!(sealed.supervisor_pid, supervisor);
    assert_eq!(sealed.namespace_pid, namespace);
    assert_eq!(
        sealed.scalar(ReadPort::Pid).unwrap(),
        format!("{host}\n").as_bytes()
    );
    assert_eq!(sealed.scalar(ReadPort::Meta).unwrap(), meta.as_bytes());
    drop(output);
    drop(sealed);
    store.cleanup().unwrap();
}

#[test]
#[ignore = "subprocess fixture; invoked only by explicit Linux resource-view journeys"]
fn view_child_fixture() {
    let args = std::env::args().collect::<Vec<_>>();
    let mode = args
        .iter()
        .find_map(|arg| arg.strip_prefix("mode:"))
        .unwrap();
    match mode {
        "mutate" => {
            assert_eq!(
                fs::read_to_string("keep").unwrap(),
                "frozen before transaction"
            );
            let cwd = std::env::current_dir().unwrap();
            fs::write(cwd.join("absolute"), "absolute write").unwrap();
            fs::write("relative", "UTF-8 可莉🌟").unwrap();
            fs::remove_file("delete").unwrap();
            let mut child = Command::new(std::env::current_exe().unwrap());
            child.args([
                "--exact",
                "view_child_fixture",
                "--ignored",
                "--nocapture",
                "--skip",
                "mode:grandchild",
            ]);
            assert!(child.status().unwrap().success());
            println!("PRIVATE_WRITE_COMPLETE");
        }
        "grandchild" => fs::write("grandchild", "descendant write").unwrap(),
        "denied" => {
            let outside = args
                .iter()
                .find_map(|arg| arg.strip_prefix("path:"))
                .unwrap();
            let denied = fs::write(outside, "must not reach host").unwrap_err();
            assert!(
                matches!(
                    denied.kind(),
                    std::io::ErrorKind::PermissionDenied | std::io::ErrorKind::ReadOnlyFilesystem
                ),
                "{denied}"
            );
            let network = args
                .iter()
                .find_map(|arg| arg.strip_prefix("network:"))
                .unwrap();
            assert!(
                std::net::TcpStream::connect_timeout(
                    &network.parse().unwrap(),
                    Duration::from_secs(1)
                )
                .is_err()
            );
            fs::write("before-error", "still private").unwrap();
            std::process::exit(7);
        }
        "cancel" => {
            fs::write("partial", "discard this uncommitted version").unwrap();
            println!("PRIVATE_LIVE");
            std::io::stdout().flush().unwrap();
            std::thread::sleep(Duration::from_secs(60));
        }
        "pid" => {
            println!("PAYLOAD_NAMESPACE_PID={}", std::process::id());
            std::io::stdout().flush().unwrap();
            let mut input = Vec::new();
            std::io::stdin().read_to_end(&mut input).unwrap();
        }
        other => panic!("unexpected fixture mode {other}"),
    }
}
