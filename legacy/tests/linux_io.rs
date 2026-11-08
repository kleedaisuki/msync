#![cfg(target_os = "linux")]

use std::fs;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;
use std::time::Duration;

use msync::io::{FlowEnd, KernelProgress};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio_util::sync::CancellationToken;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("linux-io-")
        .tempdir_in(root)
        .unwrap()
}

#[tokio::test]
async fn kernel_file_to_socket_uses_the_existing_connection_and_file_position() {
    let root = workspace();
    let path = root.path().join("payload");
    let bytes: Vec<u8> = (0..4 * 1024 * 1024)
        .map(|index| (index % 251) as u8)
        .collect();
    fs::write(&path, &bytes).unwrap();
    let mut source = fs::File::open(path).unwrap();
    source.seek(SeekFrom::Start(109)).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let destination = TcpStream::connect(listener.local_addr().unwrap())
        .await
        .unwrap();
    let receiver = tokio::spawn(async move {
        let (mut socket, _) = listener.accept().await.unwrap();
        let mut received = Vec::new();
        socket.read_to_end(&mut received).await.unwrap();
        received
    });
    let report = tokio::time::timeout(
        Duration::from_secs(3),
        msync::io::file_to_tcp(&source, &destination, 65536, &CancellationToken::new()),
    )
    .await
    .unwrap()
    .unwrap();
    assert_eq!(report.end, Some(FlowEnd::SourceClosed));
    assert_eq!(report.bytes, bytes.len() as u64 - 109);
    assert_eq!(source.stream_position().unwrap(), 109);
    assert_eq!(receiver.await.unwrap(), bytes[109..]);
}

#[tokio::test]
async fn virtual_zero_length_file_falls_back_without_reopening_the_peer() {
    let mut source = fs::File::open("/proc/self/cmdline").unwrap();
    assert_eq!(source.metadata().unwrap().len(), 0);
    let mut expected = Vec::new();
    source.read_to_end(&mut expected).unwrap();
    assert!(!expected.is_empty());
    source.rewind().unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut destination = TcpStream::connect(listener.local_addr().unwrap())
        .await
        .unwrap();
    let receiver = tokio::spawn(async move {
        let (mut socket, _) = listener.accept().await.unwrap();
        let mut received = Vec::new();
        socket.read_to_end(&mut received).await.unwrap();
        assert!(
            tokio::time::timeout(Duration::from_millis(30), listener.accept())
                .await
                .is_err()
        );
        received
    });
    let progress = msync::io::file_to_tcp(&source, &destination, 4096, &CancellationToken::new())
        .await
        .unwrap();
    assert_eq!(
        progress,
        KernelProgress {
            bytes: 0,
            end: None
        }
    );
    source.seek(SeekFrom::Start(progress.bytes)).unwrap();
    let mut reader = tokio::fs::File::from_std(source);
    tokio::io::copy(&mut reader, &mut destination)
        .await
        .unwrap();
    destination.shutdown().await.unwrap();
    assert_eq!(receiver.await.unwrap(), expected);
}

#[test]
fn finite_kernel_copy_is_byte_exact_and_cancellation_does_not_publish_success() {
    let root = workspace();
    let source = root.path().join("source.bin");
    let stage = root.path().join("stage.bin");
    let bytes: Vec<u8> = (0..262144).map(|index| (index % 239) as u8).collect();
    fs::write(&source, &bytes).unwrap();
    fs::write(&stage, b"discarded stage contents").unwrap();
    assert_eq!(
        msync::io::copy_file(&source, &stage, 8192, &CancellationToken::new()).unwrap(),
        bytes.len() as u64
    );
    assert_eq!(fs::read(&stage).unwrap(), bytes);
    let cancel = CancellationToken::new();
    cancel.cancel();
    let failed = msync::io::copy_file(&source, &stage, 8192, &cancel).unwrap_err();
    assert_eq!(failed.kind(), std::io::ErrorKind::Interrupted);
    assert_eq!(msync::io::transferred_bytes(&failed), 0);
    assert_eq!(fs::read(stage).unwrap(), bytes);
}

fn process_cpu_ms() -> f64 {
    let mut usage = std::mem::MaybeUninit::<libc::rusage>::uninit();
    // SAFETY: getrusage initializes the complete output on success.
    assert_eq!(
        unsafe { libc::getrusage(libc::RUSAGE_SELF, usage.as_mut_ptr()) },
        0
    );
    let usage = unsafe { usage.assume_init() };
    (usage.ru_utime.tv_sec + usage.ru_stime.tv_sec) as f64 * 1000.0
        + (usage.ru_utime.tv_usec + usage.ru_stime.tv_usec) as f64 / 1000.0
}

async fn timed_socket_flow(source: &Path, bytes: usize, kernel: bool) -> (f64, f64) {
    let source = fs::File::open(source).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut destination = TcpStream::connect(listener.local_addr().unwrap())
        .await
        .unwrap();
    let receiver = tokio::spawn(async move {
        let (mut socket, _) = listener.accept().await.unwrap();
        let mut buffer = vec![0_u8; 65536];
        let mut received = 0;
        loop {
            let count = socket.read(&mut buffer).await.unwrap();
            if count == 0 {
                break;
            }
            received += count;
        }
        received
    });
    let started = std::time::Instant::now();
    let cpu = process_cpu_ms();
    if kernel {
        let result =
            msync::io::file_to_tcp(&source, &destination, 65536, &CancellationToken::new())
                .await
                .unwrap();
        assert_eq!(result.end, Some(FlowEnd::SourceClosed));
        assert_eq!(result.bytes, bytes as u64);
    } else {
        let mut source = tokio::fs::File::from_std(source);
        source.set_max_buf_size(65536);
        let mut buffer = vec![0_u8; 65536];
        loop {
            let count = source.read(&mut buffer).await.unwrap();
            if count == 0 {
                break;
            }
            destination.write_all(&buffer[..count]).await.unwrap();
        }
        destination.shutdown().await.unwrap();
    }
    assert_eq!(receiver.await.unwrap(), bytes);
    (
        started.elapsed().as_secs_f64() * 1000.0,
        process_cpu_ms() - cpu,
    )
}

#[test]
#[ignore = "release-only controlled performance experiment; run alone"]
fn benchmark_buffered_and_sendfile_socket_delivery() {
    if cfg!(debug_assertions) {
        eprintln!("performance timings skipped in debug builds; use --release");
        return;
    }
    let root = workspace();
    let source = root.path().join("benchmark-source");
    let bytes = 32 * 1024 * 1024;
    fs::write(&source, vec![0x5a_u8; bytes]).unwrap();
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap();
    runtime.block_on(async {
        timed_socket_flow(&source, bytes, false).await;
        timed_socket_flow(&source, bytes, true).await;
        let mut buffered = Vec::new();
        let mut kernel = Vec::new();
        for _ in 0..4 {
            buffered.push(timed_socket_flow(&source, bytes, false).await);
            kernel.push(timed_socket_flow(&source, bytes, true).await);
            kernel.push(timed_socket_flow(&source, bytes, true).await);
            buffered.push(timed_socket_flow(&source, bytes, false).await);
        }
        for (name, values) in [("buffered64k", buffered), ("sendfile", kernel)] {
            let mut elapsed: Vec<_> = values.iter().map(|value| value.0).collect();
            let mut cpu: Vec<_> = values.iter().map(|value| value.1).collect();
            elapsed.sort_by(f64::total_cmp);
            cpu.sort_by(f64::total_cmp);
            println!("{name}: bytes={bytes} median_elapsed_ms={:.3} median_process_cpu_ms={:.3} samples={} includes_receiver=1", elapsed[elapsed.len()/2], cpu[cpu.len()/2], values.len());
        }
    });
}
