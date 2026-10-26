use std::fs;
use std::path::Path;
#[cfg(windows)]
use std::path::PathBuf;
use std::time::Duration;

use msync::compiler::{Backend, CopyOp, Location};
use msync::streams::{StreamEnd, transfer};
use msync::syntax::Span;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;
use tokio_util::sync::CancellationToken;

#[tokio::test]
async fn ipv6_endpoint_urls_connect_with_native_unbracketed_addresses() {
    let root = workspace();
    let listener = TcpListener::bind("[::1]:0").await.unwrap();
    let target = root.path().join("ipv6.bin");
    let source = format!(
        "wire => tcp://{}\nwire::/ -> {:?}\n",
        listener.local_addr().unwrap(),
        target.to_string_lossy().replace('\\', "/")
    );
    let program = msync::compiler::Compiler::new()
        .compile(&msync::syntax::parse(&source).unwrap())
        .unwrap();
    assert!(
        program.operations[0]
            .source
            .backend
            .to_string()
            .starts_with("tcp://[::1]:")
    );
    let op = program.operations[0].clone();
    let flow =
        tokio::spawn(async move { transfer(&op, 128, CancellationToken::new()).await.unwrap() });
    let (mut sender, _) = tokio::time::timeout(Duration::from_secs(3), listener.accept())
        .await
        .unwrap()
        .unwrap();
    sender.write_all(b"ipv6 bytes").await.unwrap();
    sender.shutdown().await.unwrap();
    tokio::time::timeout(Duration::from_secs(3), flow)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(target).unwrap(), b"ipv6 bytes");
}

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("streams-")
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
        span: Span { start: 0, end: 1 },
    }
}

#[tokio::test]
async fn tcp_source_to_file_preserves_binary_bytes_and_eof() {
    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let payload: Vec<u8> = (0..131072).map(|index| (index % 251) as u8).collect();
    let expected = payload.clone();
    let operation = operation(tcp(&listener), file(&root.path().join("out/payload.bin")));
    let sender = tokio::spawn(async move {
        let (mut stream, _) = listener.accept().await.unwrap();
        for part in payload.chunks(137) {
            stream.write_all(part).await.unwrap();
        }
        stream.shutdown().await.unwrap();
    });
    let result = tokio::time::timeout(
        Duration::from_secs(5),
        transfer(&operation, 1024, CancellationToken::new()),
    )
    .await
    .unwrap()
    .unwrap();
    sender.await.unwrap();
    assert_eq!(result.bytes, expected.len() as u64);
    assert_eq!(result.end, StreamEnd::SourceClosed);
    assert_eq!(
        fs::read(root.path().join("out/payload.bin")).unwrap(),
        expected
    );
}

#[tokio::test]
async fn file_to_tcp_respects_backpressure_and_finishes_one_flow() {
    let root = workspace();
    let source = root.path().join("input.bin");
    let payload: Vec<u8> = (0..262144).map(|index| (index % 239) as u8).collect();
    fs::write(&source, &payload).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let operation = operation(file(&source), tcp(&listener));
    let receiver = tokio::spawn(async move {
        let (mut stream, _) = listener.accept().await.unwrap();
        let mut received = Vec::new();
        let mut chunk = [0_u8; 257];
        loop {
            let count = stream.read(&mut chunk).await.unwrap();
            if count == 0 {
                break;
            }
            received.extend_from_slice(&chunk[..count]);
            tokio::task::yield_now().await;
        }
        received
    });
    let result = tokio::time::timeout(
        Duration::from_secs(5),
        transfer(&operation, 4096, CancellationToken::new()),
    )
    .await
    .unwrap()
    .unwrap();
    assert_eq!(result.bytes, payload.len() as u64);
    assert_eq!(receiver.await.unwrap(), payload);
}

#[tokio::test]
async fn cancellation_releases_an_idle_source_without_cancelling_another_flow() {
    let root = workspace();
    let slow = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let fast = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let slow_op = operation(tcp(&slow), file(&root.path().join("slow.bin")));
    let fast_op = operation(tcp(&fast), file(&root.path().join("fast.bin")));
    let token = CancellationToken::new();
    let cancel = token.clone();
    let slow_flow = tokio::spawn(async move { transfer(&slow_op, 512, token).await.unwrap() });
    let fast_flow = tokio::spawn(async move {
        transfer(&fast_op, 512, CancellationToken::new())
            .await
            .unwrap()
    });
    let (mut idle, _) = slow.accept().await.unwrap();
    let (mut outgoing, _) = fast.accept().await.unwrap();
    outgoing.write_all(b"fast completed").await.unwrap();
    outgoing.shutdown().await.unwrap();
    let fast_report = tokio::time::timeout(Duration::from_secs(2), fast_flow)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fast_report.end, StreamEnd::SourceClosed);
    assert!(!slow_flow.is_finished());
    cancel.cancel();
    assert_eq!(
        tokio::time::timeout(Duration::from_secs(2), slow_flow)
            .await
            .unwrap()
            .unwrap()
            .end,
        StreamEnd::Cancelled
    );
    let mut byte = [0_u8; 1];
    assert_eq!(
        tokio::time::timeout(Duration::from_secs(2), idle.read(&mut byte))
            .await
            .unwrap()
            .unwrap(),
        0
    );
}

#[tokio::test]
async fn directories_and_zero_buffers_fail_before_destination_mutation() {
    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let op = operation(file(root.path()), tcp(&listener));
    assert!(
        transfer(&op, 16, CancellationToken::new())
            .await
            .unwrap_err()
            .message
            .contains("directories")
    );
    let target = root.path().join("untouched");
    let op = operation(tcp(&listener), file(&target));
    assert!(transfer(&op, 0, CancellationToken::new()).await.is_err());
    assert!(!target.exists());
}

#[tokio::test]
async fn destination_disconnect_ends_only_its_flow() {
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let sink = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let op = operation(tcp(&source), tcp(&sink));
    let flow =
        tokio::spawn(async move { transfer(&op, 4096, CancellationToken::new()).await.unwrap() });
    let (mut writer, _) = source.accept().await.unwrap();
    let (receiver, _) = sink.accept().await.unwrap();
    drop(receiver);
    let producer = tokio::spawn(async move {
        let bytes = [7_u8; 8192];
        while writer.write_all(&bytes).await.is_ok() {
            tokio::task::yield_now().await;
        }
    });
    assert_eq!(
        tokio::time::timeout(Duration::from_secs(3), flow)
            .await
            .unwrap()
            .unwrap()
            .end,
        StreamEnd::DestinationClosed
    );
    producer.abort();
}

#[cfg(windows)]
fn pipe_path() -> PathBuf {
    PathBuf::from(format!(
        r"\\.\pipe\msync-test-{}-{}",
        std::process::id(),
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ))
}

#[cfg(windows)]
#[tokio::test]
async fn native_outbound_named_pipe_to_file_is_asynchronous_and_binary_safe() {
    use tokio::net::windows::named_pipe::ServerOptions;
    let root = workspace();
    let pipe = pipe_path();
    let mut server = ServerOptions::new()
        .first_pipe_instance(true)
        .access_inbound(false)
        .access_outbound(true)
        .create(&pipe)
        .unwrap();
    let op = operation(file(&pipe), file(&root.path().join("received.bin")));
    let transfer =
        tokio::spawn(async move { transfer(&op, 256, CancellationToken::new()).await.unwrap() });
    server.connect().await.unwrap();
    server.write_all(b"named pipe\0binary").await.unwrap();
    server.shutdown().await.unwrap();
    drop(server);
    let result = tokio::time::timeout(Duration::from_secs(3), transfer)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(result.end, StreamEnd::SourceClosed);
    assert_eq!(
        fs::read(root.path().join("received.bin")).unwrap(),
        b"named pipe\0binary"
    );
}

#[cfg(windows)]
#[tokio::test]
async fn file_to_native_inbound_named_pipe_uses_write_only_client_access() {
    use tokio::net::windows::named_pipe::ServerOptions;
    let root = workspace();
    let source = root.path().join("source.bin");
    fs::write(&source, b"file to pipe\0bytes").unwrap();
    let pipe = pipe_path();
    let mut server = ServerOptions::new()
        .first_pipe_instance(true)
        .access_inbound(true)
        .access_outbound(false)
        .create(&pipe)
        .unwrap();
    let op = operation(file(&source), file(&pipe));
    let flow =
        tokio::spawn(async move { transfer(&op, 3, CancellationToken::new()).await.unwrap() });
    server.connect().await.unwrap();
    let mut payload = Vec::new();
    server.read_to_end(&mut payload).await.unwrap();
    assert_eq!(
        tokio::time::timeout(Duration::from_secs(3), flow)
            .await
            .unwrap()
            .unwrap()
            .bytes,
        payload.len() as u64
    );
    assert_eq!(payload, b"file to pipe\0bytes");
}

#[cfg(windows)]
#[tokio::test]
async fn named_pipe_destination_close_releases_an_idle_source_without_probe_writes() {
    use tokio::net::windows::named_pipe::ServerOptions;
    let pipe = pipe_path();
    let server = ServerOptions::new()
        .first_pipe_instance(true)
        .access_inbound(true)
        .access_outbound(false)
        .create(&pipe)
        .unwrap();
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let op = operation(tcp(&source), file(&pipe));
    let cancel = CancellationToken::new();
    let token = cancel.clone();
    let mut flow = tokio::spawn(async move { transfer(&op, 128, token).await.unwrap() });
    let (_idle_source, _) = source.accept().await.unwrap();
    server.connect().await.unwrap();
    drop(server);
    let result = tokio::time::timeout(Duration::from_secs(2), &mut flow).await;
    if result.is_err() {
        cancel.cancel();
        let _ = flow.await;
        panic!("idle source retained a disconnected named-pipe destination");
    }
    assert_eq!(result.unwrap().unwrap().end, StreamEnd::DestinationClosed);
}

#[tokio::test]
async fn one_way_destination_half_close_is_an_explicit_flow_boundary() {
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let sink = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let op = operation(tcp(&source), tcp(&sink));
    let flow =
        tokio::spawn(async move { transfer(&op, 128, CancellationToken::new()).await.unwrap() });
    let (_idle, _) = source.accept().await.unwrap();
    let (mut half_closed, _) = sink.accept().await.unwrap();
    half_closed.shutdown().await.unwrap();
    assert_eq!(
        tokio::time::timeout(Duration::from_secs(2), flow)
            .await
            .unwrap()
            .unwrap()
            .end,
        StreamEnd::DestinationClosed
    );
}

#[tokio::test]
async fn http_body_can_stream_directly_into_a_socket() {
    let http = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let sink = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = http.local_addr().unwrap();
    let payload: Vec<u8> = (0..65536).map(|index| (index % 251) as u8).collect();
    let outgoing = payload.clone();
    let server = tokio::spawn(async move {
        let (mut connection, _) = http.accept().await.unwrap();
        let mut request = [0_u8; 4096];
        let count = connection.read(&mut request).await.unwrap();
        assert!(count > 0);
        connection
            .write_all(
                format!(
                    "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
                    outgoing.len()
                )
                .as_bytes(),
            )
            .await
            .unwrap();
        connection.write_all(&outgoing).await.unwrap();
        connection.shutdown().await.unwrap();
    });
    let op = operation(
        Location {
            backend: Backend::Http {
                base: format!("http://{address}/"),
            },
            path: "payload.bin".into(),
        },
        tcp(&sink),
    );
    let flow =
        tokio::spawn(async move { transfer(&op, 1024, CancellationToken::new()).await.unwrap() });
    let (mut receiver, _) = sink.accept().await.unwrap();
    let mut received = Vec::new();
    tokio::time::timeout(Duration::from_secs(5), receiver.read_to_end(&mut received))
        .await
        .unwrap()
        .unwrap();
    let report = tokio::time::timeout(Duration::from_secs(5), flow)
        .await
        .unwrap()
        .unwrap();
    server.await.unwrap();
    assert_eq!(received, payload);
    assert_eq!(report.bytes, payload.len() as u64);
}

#[cfg(windows)]
#[tokio::test]
#[ignore = "requires WSL and Python 3; execute explicitly"]
async fn real_wsl_source_streams_into_a_native_tcp_destination() {
    let root = workspace();
    let path = root.path().join("source.bin");
    let payload: Vec<u8> = (0..65536).map(|index| (index % 251) as u8).collect();
    fs::write(&path, &payload).unwrap();
    let mapping = std::process::Command::new("wsl")
        .args(["--exec", "wslpath", "-a", "-u"])
        .arg(&path)
        .output()
        .unwrap();
    assert!(mapping.status.success());
    let path = String::from_utf8(mapping.stdout)
        .unwrap()
        .trim_end()
        .to_owned();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let op = operation(
        Location {
            backend: Backend::Wsl,
            path,
        },
        tcp(&listener),
    );
    let flow = tokio::spawn(async move { transfer(&op, 1024, CancellationToken::new()).await });
    let (mut sink, _) = tokio::time::timeout(Duration::from_secs(10), listener.accept())
        .await
        .unwrap()
        .unwrap();
    let mut received = Vec::new();
    tokio::time::timeout(Duration::from_secs(10), sink.read_to_end(&mut received))
        .await
        .unwrap()
        .unwrap();
    let report = tokio::time::timeout(Duration::from_secs(10), flow)
        .await
        .unwrap()
        .unwrap()
        .unwrap();
    assert_eq!(report.bytes, payload.len() as u64);
    assert_eq!(received, payload);
}

#[cfg(unix)]
#[tokio::test]
async fn native_fifo_destination_close_releases_an_idle_source() {
    let root = workspace();
    let pipe = root.path().join("destination.fifo");
    fifo(&pipe);
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let op = operation(tcp(&source), file(&pipe));
    let flow =
        tokio::spawn(async move { transfer(&op, 128, CancellationToken::new()).await.unwrap() });
    let (_idle, _) = source.accept().await.unwrap();
    let reader = tokio::task::spawn_blocking(move || fs::File::open(pipe).unwrap())
        .await
        .unwrap();
    drop(reader);
    assert_eq!(
        tokio::time::timeout(Duration::from_secs(2), flow)
            .await
            .unwrap()
            .unwrap()
            .end,
        StreamEnd::DestinationClosed
    );
}

#[cfg(unix)]
fn fifo(path: &Path) {
    let status = std::process::Command::new("mkfifo")
        .arg(path)
        .status()
        .unwrap();
    assert!(status.success());
}

#[cfg(unix)]
#[tokio::test]
async fn unix_fifo_waits_for_first_writer_then_finishes_at_eof() {
    let root = workspace();
    let pipe = root.path().join("source.fifo");
    fifo(&pipe);
    let op = operation(file(&pipe), file(&root.path().join("result.bin")));
    let flow =
        tokio::spawn(async move { transfer(&op, 128, CancellationToken::new()).await.unwrap() });
    tokio::time::sleep(Duration::from_millis(30)).await;
    assert!(
        !flow.is_finished(),
        "FIFO ended before its first writer connected"
    );
    let writer = tokio::task::spawn_blocking(move || fs::write(pipe, b"fifo bytes\0tail").unwrap());
    writer.await.unwrap();
    let report = tokio::time::timeout(Duration::from_secs(3), flow)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(report.end, StreamEnd::SourceClosed);
    assert_eq!(
        fs::read(root.path().join("result.bin")).unwrap(),
        b"fifo bytes\0tail"
    );
}

#[cfg(unix)]
#[tokio::test]
async fn unix_domain_socket_to_file_finishes_independently() {
    let root = workspace();
    let socket = root.path().join("source.sock");
    let listener = tokio::net::UnixListener::bind(&socket).unwrap();
    let op = operation(file(&socket), file(&root.path().join("result.bin")));
    let flow =
        tokio::spawn(async move { transfer(&op, 128, CancellationToken::new()).await.unwrap() });
    let (mut writer, _) = listener.accept().await.unwrap();
    writer.write_all(b"unix socket\0data").await.unwrap();
    writer.shutdown().await.unwrap();
    assert_eq!(
        tokio::time::timeout(Duration::from_secs(3), flow)
            .await
            .unwrap()
            .unwrap()
            .end,
        StreamEnd::SourceClosed
    );
    assert_eq!(
        fs::read(root.path().join("result.bin")).unwrap(),
        b"unix socket\0data"
    );
}
