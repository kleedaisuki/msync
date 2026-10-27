//! Real physical endpoint journeys through the default v4 runtime.
use msync::{
    compiler::{CompileContext, structured::Session},
    execution::{Options, RuntimeSession},
    language,
};
#[cfg(unix)]
use std::os::unix::fs::FileTypeExt;
use std::{fs, path::Path, time::Duration};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::TcpListener,
};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("physical-v4-")
        .tempdir_in(root)
        .unwrap()
}
fn compile(compiler: &mut Session, work: &Path, text: &str) -> msync::hir::Program {
    compiler
        .compile(
            &language::parse(text).unwrap(),
            &CompileContext {
                pwd: work.to_owned(),
                source_file: None,
            },
        )
        .unwrap()
}
fn runtime(root: &Path) -> RuntimeSession {
    RuntimeSession::new(&Options::new(root.join("scratch"))).unwrap()
}
async fn accept(listener: &TcpListener) -> tokio::net::TcpStream {
    tokio::time::timeout(Duration::from_secs(5), listener.accept())
        .await
        .unwrap()
        .unwrap()
        .0
}
#[tokio::test]
async fn tcp_source_publishes_only_at_eof_and_session_reads_committed_version() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("target"), b"old").unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let program = compile(
        &mut compiler,
        &work,
        &format!(
            "wire => tcp://{}\nwire::/ -> target\n",
            listener.local_addr().unwrap()
        ),
    );
    runtime.submit(&program).await.unwrap();
    let mut sender = accept(&listener).await;
    sender.write_all(b"new prefix").await.unwrap();
    tokio::time::sleep(Duration::from_millis(30)).await;
    assert_eq!(fs::read(work.join("target")).unwrap(), b"old");
    sender.shutdown().await.unwrap();
    drop(sender);
    tokio::time::timeout(Duration::from_secs(5), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(work.join("target")).unwrap(), b"new prefix");
    let next = compile(&mut compiler, &work, "target -> copied\n");
    runtime.execute(&next).await.unwrap();
    assert_eq!(fs::read(work.join("copied")).unwrap(), b"new prefix");
    runtime.shutdown().await.unwrap();
}
#[tokio::test]
async fn stop_closes_peer_and_discards_unfinished_capture() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("target"), b"old").unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .submit(&compile(
            &mut compiler,
            &work,
            &format!(
                "wire => tcp://{}\nwire::/ -> target\n",
                listener.local_addr().unwrap()
            ),
        ))
        .await
        .unwrap();
    let mut sender = accept(&listener).await;
    sender.write_all(b"discard").await.unwrap();
    runtime.cancel_active().await.unwrap();
    let mut byte = [0];
    let closed = tokio::time::timeout(Duration::from_secs(5), sender.read(&mut byte))
        .await
        .unwrap();
    assert!(
        matches!(closed, Ok(0))
            || closed.is_err_and(|cause| matches!(
                cause.kind(),
                std::io::ErrorKind::ConnectionReset | std::io::ErrorKind::ConnectionAborted
            ))
    );
    assert_eq!(fs::read(work.join("target")).unwrap(), b"old");
    runtime.shutdown().await.unwrap();
}
#[tokio::test]
async fn file_to_tcp_uses_immutable_source_and_literal_to_tcp_is_data() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("source"), b"original").unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .submit(&compile(
            &mut compiler,
            &work,
            &format!(
                "wire => tcp://{}\nsource -> wire::/\n",
                listener.local_addr().unwrap()
            ),
        ))
        .await
        .unwrap();
    fs::write(work.join("source"), b"changed").unwrap();
    let mut receiver = accept(&listener).await;
    let mut bytes = Vec::new();
    tokio::time::timeout(Duration::from_secs(5), receiver.read_to_end(&mut bytes))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(bytes, b"original");
    runtime.wait().await.unwrap();
    runtime
        .submit(&compile(
            &mut compiler,
            &work,
            "\"literal source\" -> wire::/\n",
        ))
        .await
        .unwrap();
    let mut receiver = accept(&listener).await;
    let mut bytes = Vec::new();
    tokio::time::timeout(Duration::from_secs(5), receiver.read_to_end(&mut bytes))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(bytes, b"literal source");
    runtime.wait().await.unwrap();
    runtime.shutdown().await.unwrap();
}
#[tokio::test]
async fn named_transaction_rejects_physical_consumption_before_first_effect() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let program = compile(
        &mut compiler,
        &work,
        &format!(
            "wire => tcp://{}\ntransaction T:\n    \"early\" -> target\n    wire::/ -> output\n",
            listener.local_addr().unwrap()
        ),
    );
    assert!(
        runtime
            .submit(&program)
            .await
            .unwrap_err()
            .to_string()
            .contains("transactional peer")
    );
    assert!(!work.join("target").exists());
    assert!(
        tokio::time::timeout(Duration::from_millis(50), listener.accept())
            .await
            .is_err()
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn closing_one_flow_does_not_stop_an_independent_flow() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let first = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let second = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .submit(&compile(
            &mut compiler,
            &work,
            &format!(
                "a => tcp://{}\nb => tcp://{}\na::/ -> one\nb::/ -> two\n",
                first.local_addr().unwrap(),
                second.local_addr().unwrap()
            ),
        ))
        .await
        .unwrap();
    let mut a = accept(&first).await;
    let mut b = accept(&second).await;
    a.write_all(b"first").await.unwrap();
    a.shutdown().await.unwrap();
    drop(a);
    b.write_all(b"still ").await.unwrap();
    tokio::time::sleep(Duration::from_millis(40)).await;
    runtime
        .submit(&compile(&mut compiler, &work, ""))
        .await
        .unwrap();
    assert_eq!(fs::read(work.join("one")).unwrap(), b"first");
    assert!(runtime.has_work());
    assert!(!work.join("two").exists());
    b.write_all(b"alive").await.unwrap();
    b.shutdown().await.unwrap();
    drop(b);
    tokio::time::timeout(Duration::from_secs(5), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(work.join("two")).unwrap(), b"still alive");
    runtime.shutdown().await.unwrap();
}

#[cfg(unix)]
#[tokio::test]
async fn fifo_writer_and_reader_co_start_without_waiting_for_each_other() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::create_dir(work.join("pipes")).unwrap();
    fs::create_dir(work.join("outputs")).unwrap();
    fs::write(work.join("input"), b"fifo chain").unwrap();
    assert!(
        std::process::Command::new("mkfifo")
            .arg(work.join("pipes/wire"))
            .status()
            .unwrap()
            .success()
    );
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let program = compile(
        &mut compiler,
        &work,
        "input -> pipes/wire\npipes/wire -> outputs/result\n",
    );
    tokio::time::timeout(Duration::from_secs(5), runtime.execute(&program))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(
        fs::read(work.join("outputs/result")).unwrap(),
        b"fifo chain"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn physical_to_physical_stream_closes_when_idle_destination_disconnects() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let destination = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let program = compile(
        &mut compiler,
        &work,
        &format!(
            "source => tcp://{}\ndestination => tcp://{}\nsource::/ -> destination::/\n",
            source.local_addr().unwrap(),
            destination.local_addr().unwrap()
        ),
    );
    runtime.submit(&program).await.unwrap();
    let mut sender = accept(&source).await;
    let mut receiver = accept(&destination).await;
    sender.write_all(b"bridge").await.unwrap();
    let mut bytes = [0; 6];
    receiver.read_exact(&mut bytes).await.unwrap();
    assert_eq!(&bytes, b"bridge");
    drop(receiver);
    tokio::time::timeout(Duration::from_secs(5), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    let mut byte = [0];
    let closed = tokio::time::timeout(Duration::from_secs(5), sender.read(&mut byte))
        .await
        .unwrap();
    assert!(
        matches!(closed, Ok(0))
            || closed.is_err_and(|cause| matches!(
                cause.kind(),
                std::io::ErrorKind::ConnectionReset | std::io::ErrorKind::ConnectionAborted
            ))
    );
    runtime.shutdown().await.unwrap();
}

#[cfg(unix)]
#[tokio::test]
async fn live_fifo_and_target_in_same_directory_publish_without_snapshotting_fifo() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("target"), b"old").unwrap();
    assert!(
        std::process::Command::new("mkfifo")
            .arg(work.join("wire"))
            .status()
            .unwrap()
            .success()
    );
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .submit(&compile(&mut compiler, &work, "wire -> target\n"))
        .await
        .unwrap();
    let fifo = work.join("wire");
    tokio::task::spawn_blocking(move || {
        use std::io::Write;
        let mut writer = fs::OpenOptions::new().write(true).open(fifo).unwrap();
        writer.write_all(b"same-directory flow").unwrap();
    })
    .await
    .unwrap();
    tokio::time::timeout(Duration::from_secs(5), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(
        fs::read(work.join("target")).unwrap(),
        b"same-directory flow"
    );
    assert!(
        fs::symlink_metadata(work.join("wire"))
            .unwrap()
            .file_type()
            .is_fifo()
    );
    runtime
        .execute(&compile(&mut compiler, &work, "target -> copied\n"))
        .await
        .unwrap();
    assert_eq!(
        fs::read(work.join("copied")).unwrap(),
        b"same-directory flow"
    );
    runtime.shutdown().await.unwrap();
}

#[cfg(target_os = "linux")]
#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn actual_child_exit_closes_an_idle_physical_source() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
    let shell = fs::canonicalize("/bin/sh").unwrap();
    let program = compile(
        &mut compiler,
        &work,
        &format!(
            "wire => tcp://{}\nchild => $local::\"{}\"(\"-c\", \"sleep 0.3; exit 0\")\nwire::/ -> child::stdin\n",
            listener.local_addr().unwrap(),
            shell.display()
        ),
    );
    runtime.submit(&program).await.unwrap();
    let mut peer = accept(&listener).await;
    tokio::time::timeout(Duration::from_secs(10), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    let mut byte = [0];
    let closed = tokio::time::timeout(Duration::from_secs(5), peer.read(&mut byte))
        .await
        .unwrap();
    assert!(
        matches!(closed, Ok(0))
            || closed.is_err_and(|cause| matches!(
                cause.kind(),
                std::io::ErrorKind::ConnectionReset | std::io::ErrorKind::ConnectionAborted
            ))
    );
    runtime.shutdown().await.unwrap();
}
