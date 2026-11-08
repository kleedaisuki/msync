//! Independent end-to-end regression probes for effect planning and scheduling.

#[cfg(windows)]
#[test]
fn possible_wsl_same_file_copy_is_not_eliminated_before_its_error() {
    use msync::compiler::{Backend, CopyOp, Location, Program};
    use msync::plan::{FileClass, OperationKinds, Plan};
    use msync::syntax::Span;
    use std::{fs, path::Path};

    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let shared = directory.path().join("shared");
    let input = directory.path().join("input");
    fs::write(&shared, "original").unwrap();
    fs::write(&input, "replacement").unwrap();
    let path = shared.to_string_lossy().replace('\\', "/");
    let remote = format!(
        "/mnt/{}/{}",
        path[..1].to_ascii_lowercase(),
        path[3..].trim_start_matches('/')
    );
    let native = |path: &Path| Location {
        backend: Backend::Local,
        path: path.to_string_lossy().into_owned(),
    };
    let destination = Location {
        backend: Backend::Wsl,
        path: remote,
    };
    let plan = Plan::build(
        &Program {
            operations: vec![
                CopyOp {
                    source: native(&shared),
                    destination: destination.clone(),
                    pattern: None,
                    span: Span::default(),
                },
                CopyOp {
                    source: native(&input),
                    destination,
                    pattern: None,
                    span: Span::default(),
                },
            ],
        },
        &[OperationKinds {
            source: FileClass::Regular,
            destination: FileClass::Regular,
        }; 2],
    );
    assert_eq!(
        plan.nodes.len(),
        2,
        "a potential cross-backend same-file error must not be erased by DSE"
    );
}

#[test]
fn unresolved_remote_home_container_effect_covers_its_actual_basename() {
    use msync::compiler::{Backend, CopyOp, Location, Program};
    use msync::plan::{FileClass, OperationKinds, Plan};
    use msync::syntax::Span;

    let local = |path: &str| Location {
        backend: Backend::Local,
        path: path.into(),
    };
    let plan = Plan::build(
        &Program {
            operations: vec![
                CopyOp {
                    source: Location {
                        backend: Backend::Wsl,
                        path: "~/".into(),
                    },
                    destination: local(".temp/container/"),
                    pattern: None,
                    span: Span::default(),
                },
                CopyOp {
                    source: local(".temp/container/actual-home-name/config"),
                    destination: local(".temp/output"),
                    pattern: None,
                    span: Span::default(),
                },
            ],
        },
        &[
            OperationKinds {
                source: FileClass::Directory,
                destination: FileClass::Missing,
            },
            OperationKinds {
                source: FileClass::Missing,
                destination: FileClass::Missing,
            },
        ],
    );
    assert_eq!(
        plan.nodes[1].dependencies,
        vec![0],
        "unknown remote home basename must retain the broad container footprint"
    );
}

#[tokio::test]
async fn destination_close_releases_an_idle_tcp_source() {
    use msync::compiler::{Backend, CopyOp, Location};
    use msync::streams::{StreamEnd, transfer};
    use msync::syntax::Span;
    use std::time::Duration;
    use tokio::{net::TcpListener, sync::oneshot, time::timeout};
    use tokio_util::sync::CancellationToken;

    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let sink = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let tcp = |listener: &TcpListener| {
        let address = listener.local_addr().unwrap();
        Location {
            backend: Backend::Tcp {
                host: address.ip().to_string(),
                port: address.port(),
            },
            path: "/".into(),
        }
    };
    let operation = CopyOp {
        source: tcp(&source),
        destination: tcp(&sink),
        pattern: None,
        span: Span::default(),
    };
    let (release, held) = oneshot::channel::<()>();
    let sender = tokio::spawn(async move {
        let (connection, _) = source.accept().await.unwrap();
        let _ = held.await;
        drop(connection);
    });
    let receiver = tokio::spawn(async move {
        let (connection, _) = sink.accept().await.unwrap();
        drop(connection);
    });
    let cancel = CancellationToken::new();
    let token = cancel.clone();
    let mut flow = tokio::spawn(async move { transfer(&operation, 1024, token).await });
    receiver.await.unwrap();
    let outcome = timeout(Duration::from_secs(1), &mut flow).await;
    let stopped = outcome.is_ok();
    if !stopped {
        cancel.cancel();
        let _ = flow.await;
    }
    let _ = release.send(());
    sender.await.unwrap();
    assert!(
        stopped,
        "closed destination kept a source connection and flow slot alive while source was idle"
    );
    let report = outcome.unwrap().unwrap().unwrap();
    assert_eq!(report.end, StreamEnd::DestinationClosed);
}

#[cfg(windows)]
#[test]
#[ignore = "requires an installed WSL distribution; execute explicitly"]
fn wsl_mounted_destination_orders_a_native_read_of_the_same_file() {
    use msync::compiler::{Backend, CopyOp, Location, Program};
    use msync::scheduler::{ResourceLimits, execute_plan, prepare};
    use msync::syntax::Span;
    use std::{fs, path::Path, process::Command};

    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let input = directory.path().join("input");
    let shared = directory.path().join("shared");
    let output = directory.path().join("output");
    fs::write(&input, "new version").unwrap();
    fs::write(&shared, "old version").unwrap();
    let mapping = Command::new("wsl")
        .args(["--exec", "wslpath", "-a", "-u"])
        .arg(&shared)
        .output()
        .unwrap();
    assert!(mapping.status.success());
    let remote = String::from_utf8(mapping.stdout).unwrap().trim().to_owned();
    let native = |path: &Path| Location {
        backend: Backend::Local,
        path: path.to_string_lossy().into_owned(),
    };
    let program = Program {
        operations: vec![
            CopyOp {
                source: native(&input),
                destination: Location {
                    backend: Backend::Wsl,
                    path: remote,
                },
                pattern: None,
                span: Span::default(),
            },
            CopyOp {
                source: native(&shared),
                destination: native(&output),
                pattern: None,
                span: Span::default(),
            },
        ],
    };
    let plan = prepare(&program, true).unwrap();
    execute_plan(
        &plan,
        &ResourceLimits {
            jobs: 2,
            ..ResourceLimits::default()
        },
    )
    .unwrap();
    assert_eq!(fs::read_to_string(&shared).unwrap(), "new version");
    assert_eq!(fs::read_to_string(&output).unwrap(), "new version");
}
