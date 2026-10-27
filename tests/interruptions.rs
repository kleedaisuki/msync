use std::fs;
use std::path::Path;
use std::time::Duration;

use msync::compiler::{Backend, CopyOp, Location};
use msync::io::{FlowEnd, FlowReport, transfer};
use msync::runtime::RuntimeError;
use msync::syntax::Span;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio::task::JoinHandle;
use tokio::time::timeout;
use tokio_util::sync::CancellationToken;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("interruptions-")
        .tempdir_in(root)
        .unwrap()
}

fn file(path: &Path) -> Location {
    Location {
        backend: Backend::Local,
        path: path.to_string_lossy().into_owned(),
    }
}

fn tcp(listener: &TcpListener) -> Location {
    let address = listener.local_addr().unwrap();
    Location {
        backend: Backend::Tcp {
            host: address.ip().to_string(),
            port: address.port(),
        },
        path: "/".into(),
    }
}

fn operation(source: Location, destination: Location) -> CopyOp {
    CopyOp {
        source,
        destination,
        pattern: None,
        span: Span::default(),
    }
}

async fn tcp_flow(
    cancel: CancellationToken,
) -> (
    JoinHandle<Result<FlowReport, RuntimeError>>,
    TcpStream,
    TcpStream,
) {
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let destination = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let operation = operation(tcp(&source), tcp(&destination));
    let flow = tokio::spawn(async move { transfer(&operation, 64, cancel).await });
    let (sender, _) = timeout(Duration::from_secs(3), source.accept())
        .await
        .expect("source must open")
        .unwrap();
    let (receiver, _) = timeout(Duration::from_secs(3), destination.accept())
        .await
        .expect("destination must open")
        .unwrap();
    (flow, sender, receiver)
}

async fn assert_peer_closed(peer: &mut TcpStream) {
    let mut byte = [0_u8; 1];
    let result = timeout(Duration::from_secs(3), peer.read(&mut byte))
        .await
        .expect("dropped flow must physically release the socket, not merely return a status");
    match result {
        Ok(count) => assert_eq!(count, 0, "no extra payload may be invented on close"),
        Err(error) => assert!(
            matches!(
                error.kind(),
                std::io::ErrorKind::ConnectionReset
                    | std::io::ErrorKind::ConnectionAborted
                    | std::io::ErrorKind::BrokenPipe
            ),
            "unexpected close failure: {error}"
        ),
    }
}

#[tokio::test]
async fn already_cancelled_flow_never_truncates_an_existing_destination() {
    let root = workspace();
    let target = root.path().join("target.bin");
    fs::write(&target, b"old destination must survive").unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let operation = operation(tcp(&listener), file(&target));
    let cancel = CancellationToken::new();
    cancel.cancel();
    let report = timeout(Duration::from_secs(3), transfer(&operation, 64, cancel))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(report.end, FlowEnd::Cancelled);
    assert_eq!(report.bytes, 0);
    assert_eq!(fs::read(&target).unwrap(), b"old destination must survive");
    assert_eq!(
        fs::read_dir(root.path()).unwrap().count(),
        1,
        "cancellation must not leave a staging artifact"
    );
}

#[tokio::test]
async fn ordinary_files_share_the_flow_contract_and_initial_cancellation_policy() {
    let root = workspace();
    let source = root.path().join("source.bin");
    let target = root.path().join("target.bin");
    let payload: Vec<u8> = (0..8192).map(|index| (index % 251) as u8).collect();
    fs::write(&source, &payload).unwrap();
    fs::write(&target, b"old committed value").unwrap();
    let operation = operation(file(&source), file(&target));
    let cancel = CancellationToken::new();
    cancel.cancel();
    let cancelled = transfer(&operation, 256, cancel).await.unwrap();
    assert_eq!(cancelled.end, FlowEnd::Cancelled);
    assert_eq!(cancelled.bytes, 0);
    assert_eq!(fs::read(&target).unwrap(), b"old committed value");
    let report = timeout(
        Duration::from_secs(5),
        transfer(&operation, 256, CancellationToken::new()),
    )
    .await
    .unwrap()
    .unwrap();
    assert_eq!(report.end, FlowEnd::SourceClosed);
    assert_eq!(report.bytes, payload.len() as u64);
    assert_eq!(fs::read(&target).unwrap(), payload);
    assert_eq!(fs::read(&source).unwrap(), payload);
    assert_eq!(
        fs::read_dir(root.path()).unwrap().count(),
        2,
        "a completed finite flow must not leak a transaction file"
    );
}

#[tokio::test]
async fn cancelling_an_open_idle_flow_releases_both_physical_connections() {
    let token = CancellationToken::new();
    let (flow, mut sender, mut receiver) = tcp_flow(token.clone()).await;
    token.cancel();
    let report = timeout(Duration::from_secs(3), flow)
        .await
        .unwrap()
        .unwrap()
        .unwrap();
    assert_eq!(report.end, FlowEnd::Cancelled);
    assert_eq!(report.bytes, 0);
    assert_peer_closed(&mut sender).await;
    assert_peer_closed(&mut receiver).await;
}

#[tokio::test]
async fn aborting_a_future_releases_handles_without_cancelling_its_token() {
    let token = CancellationToken::new();
    let (flow, mut sender, mut receiver) = tcp_flow(token.clone()).await;
    flow.abort();
    assert!(flow.await.unwrap_err().is_cancelled());
    assert!(
        !token.is_cancelled(),
        "this test must exercise future-drop ownership, not token callbacks"
    );
    assert_peer_closed(&mut sender).await;
    assert_peer_closed(&mut receiver).await;
}

#[tokio::test]
async fn abort_after_acknowledged_bytes_keeps_exact_prefix_and_releases_connections() {
    let token = CancellationToken::new();
    let (flow, mut sender, mut receiver) = tcp_flow(token.clone()).await;
    let payload = b"acknowledged binary prefix\0\xff";
    sender.write_all(payload).await.unwrap();
    let mut observed = vec![0_u8; payload.len()];
    timeout(Duration::from_secs(3), receiver.read_exact(&mut observed))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(observed, payload);
    flow.abort();
    assert!(flow.await.unwrap_err().is_cancelled());
    assert!(!token.is_cancelled());
    assert_peer_closed(&mut sender).await;
    assert_peer_closed(&mut receiver).await;
}

#[tokio::test]
async fn source_eof_flushes_exact_payload_then_closes_the_destination() {
    let (flow, mut sender, mut receiver) = tcp_flow(CancellationToken::new()).await;
    let payload: Vec<u8> = (0..4096).map(|index| (index % 251) as u8).collect();
    sender.write_all(&payload).await.unwrap();
    sender.shutdown().await.unwrap();
    let mut observed = Vec::new();
    timeout(Duration::from_secs(3), receiver.read_to_end(&mut observed))
        .await
        .unwrap()
        .unwrap();
    let report = timeout(Duration::from_secs(3), flow)
        .await
        .unwrap()
        .unwrap()
        .unwrap();
    assert_eq!(report.end, FlowEnd::SourceClosed);
    assert_eq!(report.bytes, payload.len() as u64);
    assert_eq!(observed, payload);
    assert_peer_closed(&mut sender).await;
}

async fn wait_for_uncommitted_bytes(root: &Path, target: &Path, expected: &[u8]) {
    timeout(Duration::from_secs(5), async {
        loop {
            let staged = fs::read_dir(root).unwrap().any(|entry| {
                let entry = entry.unwrap();
                entry.path() != target
                    && entry.file_type().unwrap().is_file()
                    && fs::read(entry.path()).is_ok_and(|bytes| bytes == expected)
            });
            if staged {
                break;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("the source stays open: wait for real staged bytes, not an assumed timing window");
}

async fn assert_only_published_target_remains(root: &Path, target: &Path, prior: Option<&[u8]>) {
    match prior {
        Some(bytes) => assert_eq!(
            fs::read(target).unwrap(),
            bytes,
            "interruption must preserve the prior committed value"
        ),
        None => assert!(
            !target.exists(),
            "an interrupted new target must remain absent"
        ),
    }
    let expected_files = usize::from(prior.is_some());
    timeout(Duration::from_secs(5), async {
        while fs::read_dir(root).unwrap().count() != expected_files {
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect("all uncommitted stage files and pending I/O ownership must be cleaned up");
}

#[tokio::test]
async fn cancellation_after_real_file_writes_discards_stage_and_preserves_old_target() {
    let root = workspace();
    let target = root.path().join("target.bin");
    let prior = b"previous committed contents";
    fs::write(&target, prior).unwrap();
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let operation = operation(tcp(&source), file(&target));
    let token = CancellationToken::new();
    let flow_token = token.clone();
    let flow = tokio::spawn(async move { transfer(&operation, 256, flow_token).await });
    let (mut sender, _) = timeout(Duration::from_secs(3), source.accept())
        .await
        .unwrap()
        .unwrap();
    let prefix = vec![0xa5; 4096];
    sender.write_all(&prefix).await.unwrap();
    wait_for_uncommitted_bytes(root.path(), &target, &prefix).await;
    assert_eq!(
        fs::read(&target).unwrap(),
        prior,
        "writes before EOF are not publication"
    );
    token.cancel();
    let report = timeout(Duration::from_secs(5), flow)
        .await
        .unwrap()
        .unwrap()
        .unwrap();
    assert_eq!(report.end, FlowEnd::Cancelled);
    assert!(
        report.bytes <= prefix.len() as u64,
        "a cancellation-boundary counter cannot invent accepted bytes"
    );
    assert_only_published_target_remains(root.path(), &target, Some(prior)).await;
    assert_peer_closed(&mut sender).await;
}

#[tokio::test]
async fn regular_file_publication_occurs_only_after_normal_source_eof() {
    let root = workspace();
    let target = root.path().join("target.bin");
    fs::write(&target, b"old committed value").unwrap();
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let operation = operation(tcp(&source), file(&target));
    let flow =
        tokio::spawn(async move { transfer(&operation, 256, CancellationToken::new()).await });
    let (mut sender, _) = timeout(Duration::from_secs(3), source.accept())
        .await
        .unwrap()
        .unwrap();
    let bytes: Vec<u8> = (0..4096).map(|index| (index % 251) as u8).collect();
    sender.write_all(&bytes).await.unwrap();
    wait_for_uncommitted_bytes(root.path(), &target, &bytes).await;
    assert_eq!(fs::read(&target).unwrap(), b"old committed value");
    sender.shutdown().await.unwrap();
    let report = timeout(Duration::from_secs(5), flow)
        .await
        .unwrap()
        .unwrap()
        .unwrap();
    assert_eq!(report.end, FlowEnd::SourceClosed);
    assert_eq!(report.bytes, bytes.len() as u64);
    assert_eq!(fs::read(&target).unwrap(), bytes);
    assert_only_published_target_remains(root.path(), &target, Some(&bytes)).await;
    assert_peer_closed(&mut sender).await;
}

#[tokio::test]
async fn abort_after_real_file_writes_never_publishes_existing_or_new_target() {
    for exists in [false, true] {
        let root = workspace();
        let target = root.path().join("target.bin");
        let prior = b"previous committed contents";
        if exists {
            fs::write(&target, prior).unwrap();
        }
        let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let operation = operation(tcp(&source), file(&target));
        let token = CancellationToken::new();
        let flow_token = token.clone();
        let flow = tokio::spawn(async move { transfer(&operation, 256, flow_token).await });
        let (mut sender, _) = timeout(Duration::from_secs(3), source.accept())
            .await
            .unwrap()
            .unwrap();
        let prefix = vec![0x5a; 4096];
        sender.write_all(&prefix).await.unwrap();
        wait_for_uncommitted_bytes(root.path(), &target, &prefix).await;
        flow.abort();
        assert!(flow.await.unwrap_err().is_cancelled());
        assert!(
            !token.is_cancelled(),
            "future-drop cleanup must not rely on a token callback"
        );
        assert_only_published_target_remains(
            root.path(),
            &target,
            exists.then_some(prior.as_slice()),
        )
        .await;
        assert_peer_closed(&mut sender).await;
    }
}

#[cfg(windows)]
#[tokio::test]
async fn abort_releases_the_write_only_named_pipe_and_its_idle_source() {
    use std::sync::atomic::{AtomicU64, Ordering};
    use tokio::net::windows::named_pipe::ServerOptions;
    static NEXT_PIPE: AtomicU64 = AtomicU64::new(0);
    let name = format!(
        r"\\.\pipe\msync-interruptions-{}-{}",
        std::process::id(),
        NEXT_PIPE.fetch_add(1, Ordering::Relaxed)
    );
    let mut server = ServerOptions::new()
        .first_pipe_instance(true)
        .access_inbound(true)
        .access_outbound(false)
        .create(&name)
        .unwrap();
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let operation = operation(tcp(&source), file(Path::new(&name)));
    let token = CancellationToken::new();
    let flow_token = token.clone();
    let flow = tokio::spawn(async move { transfer(&operation, 64, flow_token).await });
    let (mut sender, _) = timeout(Duration::from_secs(3), source.accept())
        .await
        .unwrap()
        .unwrap();
    timeout(Duration::from_secs(3), server.connect())
        .await
        .unwrap()
        .unwrap();
    sender.write_all(b"pipe prefix").await.unwrap();
    let mut prefix = [0_u8; 11];
    timeout(Duration::from_secs(3), server.read_exact(&mut prefix))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(&prefix, b"pipe prefix");
    flow.abort();
    assert!(flow.await.unwrap_err().is_cancelled());
    assert!(!token.is_cancelled());
    assert_peer_closed(&mut sender).await;
    let mut byte = [0_u8; 1];
    let read = timeout(Duration::from_secs(3), server.read(&mut byte))
        .await
        .expect("named-pipe client and duplicated close watcher must both be dropped");
    match read {
        Ok(size) => assert_eq!(size, 0),
        Err(error) => assert!(
            matches!(error.raw_os_error(), Some(109 | 232 | 233)),
            "{error}"
        ),
    }
}

#[cfg(windows)]
#[tokio::test]
async fn abort_after_file_bytes_releases_the_native_source_file_handle() {
    use std::os::windows::fs::OpenOptionsExt;
    let root = workspace();
    let source = root.path().join("source.bin");
    fs::write(&source, vec![0x5a_u8; 16 * 1024 * 1024]).unwrap();
    let socket = tokio::net::TcpSocket::new_v4().unwrap();
    socket.set_recv_buffer_size(1024).unwrap();
    socket.bind("127.0.0.1:0".parse().unwrap()).unwrap();
    let destination = socket.listen(1).unwrap();
    let operation = operation(file(&source), tcp(&destination));
    let token = CancellationToken::new();
    let flow_token = token.clone();
    let flow = tokio::spawn(async move { transfer(&operation, 64, flow_token).await });
    let (mut receiver, _) = timeout(Duration::from_secs(3), destination.accept())
        .await
        .unwrap()
        .unwrap();
    let mut prefix = [0_u8; 64];
    timeout(Duration::from_secs(3), receiver.read_exact(&mut prefix))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(prefix, [0x5a; 64]);
    let exclusive = || {
        fs::OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(&source)
    };
    assert!(
        exclusive().is_err(),
        "the active source must still hold a physical file handle before abort"
    );
    flow.abort();
    assert!(flow.await.unwrap_err().is_cancelled());
    assert!(!token.is_cancelled());
    timeout(Duration::from_secs(3), async {
        loop {
            if let Ok(handle) = exclusive() {
                break handle;
            }
            tokio::task::yield_now().await;
        }
    })
    .await
    .expect(
        "aborting the flow must release its physical file handle, including any pending Tokio read",
    );
    assert_eq!(fs::metadata(&source).unwrap().len(), 16 * 1024 * 1024);
}

#[cfg(unix)]
#[tokio::test]
async fn abort_releases_unix_socket_writer_and_idle_tcp_source() {
    let root = workspace();
    let path = root.path().join("destination.sock");
    let destination = tokio::net::UnixListener::bind(&path).unwrap();
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let operation = operation(tcp(&source), file(&path));
    let token = CancellationToken::new();
    let flow_token = token.clone();
    let flow = tokio::spawn(async move { transfer(&operation, 64, flow_token).await });
    let (mut sender, _) = timeout(Duration::from_secs(3), source.accept())
        .await
        .unwrap()
        .unwrap();
    let (mut receiver, _) = timeout(Duration::from_secs(3), destination.accept())
        .await
        .unwrap()
        .unwrap();
    sender.write_all(b"socket prefix").await.unwrap();
    let mut prefix = [0_u8; 13];
    timeout(Duration::from_secs(3), receiver.read_exact(&mut prefix))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(&prefix, b"socket prefix");
    flow.abort();
    assert!(flow.await.unwrap_err().is_cancelled());
    assert!(!token.is_cancelled());
    assert_peer_closed(&mut sender).await;
    let mut extra = [0_u8; 1];
    assert_eq!(
        timeout(Duration::from_secs(3), receiver.read(&mut extra))
            .await
            .expect("Unix writer and its close watcher must both be dropped")
            .unwrap(),
        0
    );
}

#[cfg(unix)]
#[tokio::test]
async fn abort_releases_fifo_writer_including_duplicated_close_watch_descriptor() {
    let root = workspace();
    let path = root.path().join("destination.fifo");
    assert!(
        std::process::Command::new("mkfifo")
            .arg(&path)
            .status()
            .unwrap()
            .success(),
        "FIFO test requires a native Unix filesystem; use the documented private .temp tmpfs under WSL"
    );
    let mut receiver = tokio::net::unix::pipe::OpenOptions::new()
        .open_receiver(&path)
        .unwrap();
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let operation = operation(tcp(&source), file(&path));
    let token = CancellationToken::new();
    let flow_token = token.clone();
    let flow = tokio::spawn(async move { transfer(&operation, 64, flow_token).await });
    let (mut sender, _) = timeout(Duration::from_secs(3), source.accept())
        .await
        .unwrap()
        .unwrap();
    sender.write_all(b"fifo prefix").await.unwrap();
    let mut prefix = [0_u8; 11];
    timeout(Duration::from_secs(3), async {
        receiver.readable().await.unwrap();
        receiver.read_exact(&mut prefix).await.unwrap();
    })
    .await
    .unwrap();
    assert_eq!(&prefix, b"fifo prefix");
    flow.abort();
    assert!(flow.await.unwrap_err().is_cancelled());
    assert!(!token.is_cancelled());
    assert_peer_closed(&mut sender).await;
    let mut extra = [0_u8; 1];
    assert_eq!(
        timeout(Duration::from_secs(3), receiver.read(&mut extra))
            .await
            .expect(
                "every FIFO writer descriptor, including close-watcher duplicates, must be released"
            )
            .unwrap(),
        0
    );
}
