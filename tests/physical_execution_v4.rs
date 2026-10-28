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

#[tokio::test]
async fn independent_tcp_inputs_queue_at_the_concurrent_capacity_limit() {
    use std::sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    };
    for low_memory in [false, true] {
        let root = workspace();
        let work = root.path().join("work");
        fs::create_dir(&work).unwrap();
        fs::create_dir(work.join("out1")).unwrap();
        fs::create_dir(work.join("out2")).unwrap();
        let first = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let second = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let mut compiler = Session::new();
        let program = compile(
            &mut compiler,
            &work,
            &format!(
                "left => tcp://{}\nright => tcp://{}\nleft::/ -> out1/result\nright::/ -> out2/result\n",
                first.local_addr().unwrap(),
                second.local_addr().unwrap(),
            ),
        );
        let first_closed = Arc::new(AtomicBool::new(false));
        let observed = first_closed.clone();
        let first_server = tokio::spawn(async move {
            let mut peer = accept(&first).await;
            peer.write_all(b"one").await.unwrap();
            tokio::time::sleep(Duration::from_millis(150)).await;
            first_closed.store(true, Ordering::Release);
            peer.shutdown().await.unwrap();
        });
        let second_server = tokio::spawn(async move {
            let mut peer = accept(&second).await;
            assert!(
                observed.load(Ordering::Acquire),
                "a second flow opened before occupied capacity was released"
            );
            peer.write_all(b"two").await.unwrap();
            peer.shutdown().await.unwrap();
        });
        let mut options = Options::new(root.path().join("scratch"));
        options.limits.max_streams = if low_memory { 2 } else { 1 };
        if low_memory {
            options.limits.memory_bytes = options.limits.buffer_bytes * 2;
        }
        let mut runtime = RuntimeSession::new(&options).unwrap();
        let result = tokio::time::timeout(Duration::from_secs(5), runtime.execute(&program)).await;
        if !matches!(result, Ok(Ok(_))) {
            first_server.abort();
            second_server.abort();
            let cleanup = runtime.shutdown().await;
            panic!(
                "queued batch failed (low_memory={low_memory}): {result:?}; cleanup={cleanup:?}"
            );
        }
        first_server.await.unwrap();
        second_server.await.unwrap();
        assert_eq!(fs::read(work.join("out1/result")).unwrap(), b"one");
        assert_eq!(fs::read(work.join("out2/result")).unwrap(), b"two");
        assert!(!runtime.has_work());
        runtime.shutdown().await.unwrap();
    }
}

#[tokio::test]
async fn interactive_capacity_refusal_preserves_the_old_flow_and_can_be_retried() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let first = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let second = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut compiler = Session::new();
    let initial = compile(
        &mut compiler,
        &work,
        &format!(
            "left => tcp://{}\nright => tcp://{}\nleft::/ -> output\n",
            first.local_addr().unwrap(),
            second.local_addr().unwrap(),
        ),
    );
    let mut options = Options::new(root.path().join("scratch"));
    options.limits.max_streams = 1;
    let mut runtime = RuntimeSession::new(&options).unwrap();
    runtime.submit(&initial).await.unwrap();
    let mut sender = accept(&first).await;
    let next = compile(&mut compiler, &work, "\"two\" -> right::/\n");
    let error = tokio::time::timeout(Duration::from_secs(2), runtime.submit(&next))
        .await
        .unwrap()
        .unwrap_err();
    assert!(
        error
            .report
            .diagnostic
            .message
            .contains("capacity is occupied"),
        "{error}"
    );
    assert!(
        error
            .report
            .help
            .iter()
            .any(|help| help.contains("/wait") && help.contains("/stop")),
        "{error:?}"
    );
    assert!(runtime.has_work());
    assert!(
        tokio::time::timeout(Duration::from_millis(50), second.accept())
            .await
            .is_err()
    );
    sender.write_all(b"one").await.unwrap();
    sender.shutdown().await.unwrap();
    drop(sender);
    tokio::time::timeout(Duration::from_secs(5), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(work.join("output")).unwrap(), b"one");
    runtime.submit(&next).await.unwrap();
    let mut receiver = accept(&second).await;
    let mut bytes = Vec::new();
    receiver.read_to_end(&mut bytes).await.unwrap();
    assert_eq!(bytes, b"two");
    runtime.wait().await.unwrap();
    runtime.shutdown().await.unwrap();
}

#[cfg(unix)]
#[tokio::test]
async fn fifo_writer_and_reader_co_start_without_waiting_for_each_other() {
    use std::os::unix::fs::MetadataExt;
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
    let fifo_before = fs::symlink_metadata(work.join("pipes/wire")).unwrap();
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
    let fifo_after = fs::symlink_metadata(work.join("pipes/wire")).unwrap();
    assert!(fifo_after.file_type().is_fifo());
    assert_eq!(fifo_before.ino(), fifo_after.ino());
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
async fn fifo_waves_preserve_order_and_reject_half_cohort_resource_budgets() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::create_dir(work.join("pipes")).unwrap();
    fs::create_dir(work.join("outputs")).unwrap();
    fs::write(work.join("one"), b"first wave").unwrap();
    fs::write(work.join("two"), b"second wave").unwrap();
    assert!(
        std::process::Command::new("mkfifo")
            .arg(work.join("pipes/wire"))
            .status()
            .unwrap()
            .success()
    );
    let mut compiler = Session::new();
    let first = compile(
        &mut compiler,
        &work,
        "one -> pipes/wire\npipes/wire -> outputs/first\n",
    );
    for low_memory in [false, true] {
        let scratch = root.path().join(if low_memory {
            "memory-scratch"
        } else {
            "stream-scratch"
        });
        let mut options = Options::new(&scratch);
        if low_memory {
            options.limits.memory_bytes = options.limits.buffer_bytes * 3;
        } else {
            options.limits.max_streams = 1;
        }
        let mut limited = RuntimeSession::new(&options).unwrap();
        let failure = limited.execute(&first).await.unwrap_err();
        assert!(
            failure.to_string().contains("must co-start 2 flows"),
            "{failure}"
        );
        assert!(
            !scratch.exists(),
            "insufficient cohort capacity must precede payload capture"
        );
        assert!(!work.join("outputs/first").exists());
        limited.shutdown().await.unwrap();
    }
    let waves = compile(
        &mut compiler,
        &work,
        "one -> pipes/wire\npipes/wire -> outputs/first\ntwo -> pipes/wire\npipes/wire -> outputs/second\n",
    );
    let mut session = runtime(root.path());
    tokio::time::timeout(Duration::from_secs(5), session.execute(&waves))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(work.join("outputs/first")).unwrap(), b"first wave");
    assert_eq!(
        fs::read(work.join("outputs/second")).unwrap(),
        b"second wave"
    );
    session.shutdown().await.unwrap();
}

#[cfg(unix)]
#[tokio::test]
async fn a_three_flow_fifo_chain_reserves_the_whole_connected_cohort() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::create_dir(work.join("pipes")).unwrap();
    fs::create_dir(work.join("outputs")).unwrap();
    let bytes = b"bounded three-flow FIFO chain\n".repeat(32 * 1024);
    fs::write(work.join("input"), &bytes).unwrap();
    for name in ["a", "b"] {
        assert!(
            std::process::Command::new("mkfifo")
                .arg(work.join("pipes").join(name))
                .status()
                .unwrap()
                .success()
        );
    }
    let program = compile(
        &mut Session::new(),
        &work,
        "input -> pipes/a\npipes/a -> pipes/b\npipes/b -> outputs/result\n",
    );
    let mut options = Options::new(root.path().join("limited-scratch"));
    options.limits.max_streams = 2;
    let mut limited = RuntimeSession::new(&options).unwrap();
    let failure = limited.execute(&program).await.unwrap_err();
    assert!(
        failure.to_string().contains("must co-start 3 flows"),
        "{failure}"
    );
    assert!(!options.scratch_root.exists());
    limited.shutdown().await.unwrap();
    let mut session = runtime(root.path());
    tokio::time::timeout(Duration::from_secs(5), session.execute(&program))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(work.join("outputs/result")).unwrap(), bytes);
    session.shutdown().await.unwrap();
}

#[cfg(unix)]
#[tokio::test]
async fn hard_linked_fifo_aliases_preserve_wave_boundaries() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::create_dir(work.join("pipes")).unwrap();
    fs::create_dir(work.join("outputs")).unwrap();
    let one = b"first FIFO wave\n".repeat(32 * 1024);
    let two = b"second FIFO wave\n".repeat(32 * 1024);
    fs::write(work.join("one"), &one).unwrap();
    fs::write(work.join("two"), &two).unwrap();
    assert!(
        std::process::Command::new("mkfifo")
            .arg(work.join("pipes/a"))
            .status()
            .unwrap()
            .success()
    );
    for alias in ["b", "c", "d"] {
        fs::hard_link(work.join("pipes/a"), work.join("pipes").join(alias)).unwrap();
    }
    let program = compile(
        &mut Session::new(),
        &work,
        "one -> pipes/a\npipes/b -> outputs/first\ntwo -> pipes/c\npipes/d -> outputs/second\n",
    );
    let mut session = runtime(root.path());
    tokio::time::timeout(Duration::from_secs(5), session.execute(&program))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(work.join("outputs/first")).unwrap(), one);
    assert_eq!(fs::read(work.join("outputs/second")).unwrap(), two);
    session.shutdown().await.unwrap();
}

#[cfg(unix)]
#[tokio::test]
async fn a_later_fifo_peer_gets_a_constructive_single_submission_hint_and_can_recover() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("input"), b"complete FIFO bytes").unwrap();
    assert!(
        std::process::Command::new("mkfifo")
            .arg(work.join("wire"))
            .status()
            .unwrap()
            .success()
    );
    let mut compiler = Session::new();
    let mut session = runtime(root.path());
    session
        .submit(&compile(&mut compiler, &work, "input -> wire\n"))
        .await
        .unwrap();
    let rejected = tokio::time::timeout(
        Duration::from_secs(2),
        session.submit(&compile(&mut compiler, &work, "wire -> output\n")),
    )
    .await
    .unwrap()
    .unwrap_err();
    assert!(
        rejected
            .report
            .help
            .iter()
            .any(|help| help.contains("/load")
                && help.contains("macro")
                && help.contains("/wait alone")),
        "{rejected:?}"
    );
    assert!(!work.join("output").exists());
    session.cancel_active().await.unwrap();
    tokio::time::timeout(
        Duration::from_secs(5),
        session.execute(&compile(
            &mut compiler,
            &work,
            "input -> wire\nwire -> output\n",
        )),
    )
    .await
    .unwrap()
    .unwrap();
    assert_eq!(
        fs::read(work.join("output")).unwrap(),
        b"complete FIFO bytes"
    );
    session.shutdown().await.unwrap();
}

#[cfg(unix)]
#[tokio::test]
async fn fifo_cohort_waits_for_all_external_prerequisites_before_reserving_slots() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::create_dir(work.join("pipes")).unwrap();
    fs::create_dir(work.join("outputs")).unwrap();
    fs::write(work.join("outputs/result"), b"original target").unwrap();
    assert!(
        std::process::Command::new("mkfifo")
            .arg(work.join("pipes/wire"))
            .status()
            .unwrap()
            .success()
    );
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let program = compile(
        &mut Session::new(),
        &work,
        &format!(
            "incoming => tcp://{}\n\"later FIFO value\" -> pipes/wire\nincoming::/ -> outputs/result\npipes/wire -> outputs/result\n",
            listener.local_addr().unwrap(),
        ),
    );
    let mut options = Options::new(root.path().join("scratch"));
    options.limits.max_streams = 2;
    let mut session = RuntimeSession::new(&options).unwrap();
    let running = tokio::spawn(async move {
        let result = session.execute(&program).await;
        (session, result)
    });
    let mut sender = accept(&listener).await;
    sender.write_all(b"earlier TCP value").await.unwrap();
    sender.shutdown().await.unwrap();
    drop(sender);
    let (mut session, result) = tokio::time::timeout(Duration::from_secs(5), running)
        .await
        .unwrap()
        .unwrap();
    result.unwrap();
    assert_eq!(
        fs::read(work.join("outputs/result")).unwrap(),
        b"later FIFO value"
    );
    session.shutdown().await.unwrap();
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
async fn a_reader_first_fifo_pipeline_can_feed_a_native_program_stdin() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let fifo = root.path().join("wire");
    assert!(
        std::process::Command::new("mkfifo")
            .arg(&fifo)
            .status()
            .unwrap()
            .success()
    );
    let mut limited_options = Options::new(root.path().join("limited-scratch"));
    limited_options.limits.max_streams = 2;
    limited_options.limits.memory_bytes = limited_options.limits.buffer_bytes * 8;
    let mut limited = RuntimeSession::new(&limited_options).unwrap();
    limited.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
    let mut limited_compiler = Session::new();
    limited
        .submit(&compile(
            &mut limited_compiler,
            &work,
            "child => $path::cat()\n",
        ))
        .await
        .unwrap();
    let limited_program = compile(
        &mut limited_compiler,
        &work,
        &format!(
            "\"must not publish\" -> earlier\n$local::\"{}\" -> child::stdin\n\"reader-first bytes\" -> $local::\"{}\"\n",
            fifo.display(),
            fifo.display(),
        ),
    );
    let error = limited.execute(&limited_program).await.unwrap_err();
    assert!(
        error
            .report
            .diagnostic
            .message
            .contains("retain 1 native program spaces"),
        "{error}"
    );
    assert!(!work.join("earlier").exists());
    assert_eq!(
        limited.live_processes().len(),
        1,
        "the refused input must not discard its earlier live program"
    );
    limited.shutdown().await.unwrap();
    let mut compiler = Session::new();
    let mut session = runtime(root.path());
    session.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
    session
        .submit(&compile(&mut compiler, &work, "child => $path::cat()\n"))
        .await
        .unwrap();
    let program = compile(
        &mut compiler,
        &work,
        &format!(
            "$local::\"{}\" -> child::stdin\n\"reader-first bytes\" -> $local::\"{}\"\nchild::stdout -> output\n",
            fifo.display(),
            fifo.display(),
        ),
    );
    tokio::time::timeout(Duration::from_secs(10), session.execute(&program))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(
        fs::read(work.join("output")).unwrap(),
        b"reader-first bytes"
    );
    session.shutdown().await.unwrap();
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

#[cfg(unix)]
async fn accept_unix(listener: &tokio::net::UnixListener) -> tokio::net::UnixStream {
    tokio::time::timeout(Duration::from_secs(5), listener.accept())
        .await
        .unwrap()
        .unwrap()
        .0
}

#[cfg(unix)]
#[tokio::test]
async fn unix_socket_source_publishes_same_directory_file_at_eof_and_preserves_socket() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("target"), b"old").unwrap();
    let listener = tokio::net::UnixListener::bind(work.join("socket")).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .submit(&compile(&mut compiler, &work, "socket -> target\n"))
        .await
        .unwrap();
    let mut source = accept_unix(&listener).await;
    source.write_all(b"socket prefix").await.unwrap();
    tokio::time::sleep(Duration::from_millis(30)).await;
    assert_eq!(fs::read(work.join("target")).unwrap(), b"old");
    source.shutdown().await.unwrap();
    drop(source);
    tokio::time::timeout(Duration::from_secs(5), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(work.join("target")).unwrap(), b"socket prefix");
    assert!(
        fs::symlink_metadata(work.join("socket"))
            .unwrap()
            .file_type()
            .is_socket()
    );
    runtime
        .execute(&compile(&mut compiler, &work, "target -> copied\n"))
        .await
        .unwrap();
    assert_eq!(fs::read(work.join("copied")).unwrap(), b"socket prefix");
    runtime.shutdown().await.unwrap();
    assert!(
        fs::symlink_metadata(work.join("socket"))
            .unwrap()
            .file_type()
            .is_socket()
    );
}

#[cfg(unix)]
#[tokio::test]
async fn unix_socket_sink_receives_immutable_file_and_literal_bytes_with_eof() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("source"), b"immutable\0socket bytes").unwrap();
    let listener = tokio::net::UnixListener::bind(work.join("socket")).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .submit(&compile(&mut compiler, &work, "source -> socket\n"))
        .await
        .unwrap();
    fs::write(work.join("source"), b"mutated host bytes").unwrap();
    let mut peer = accept_unix(&listener).await;
    let mut received = Vec::new();
    tokio::time::timeout(Duration::from_secs(5), peer.read_to_end(&mut received))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(received, b"immutable\0socket bytes");
    runtime.wait().await.unwrap();
    runtime
        .submit(&compile(
            &mut compiler,
            &work,
            "\"literal socket data\" -> socket\n",
        ))
        .await
        .unwrap();
    let mut peer = accept_unix(&listener).await;
    let mut received = Vec::new();
    tokio::time::timeout(Duration::from_secs(5), peer.read_to_end(&mut received))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(received, b"literal socket data");
    runtime.wait().await.unwrap();
    runtime.shutdown().await.unwrap();
    assert!(
        fs::symlink_metadata(work.join("socket"))
            .unwrap()
            .file_type()
            .is_socket()
    );
}

#[cfg(unix)]
#[tokio::test]
async fn unix_socket_sink_rejects_directory_source_before_connecting() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::create_dir(work.join("directory")).unwrap();
    fs::write(work.join("directory/entry"), b"preserve").unwrap();
    let listener = tokio::net::UnixListener::bind(work.join("socket")).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let error = runtime
        .submit(&compile(&mut compiler, &work, "directory -> socket\n"))
        .await
        .unwrap_err();
    assert!(
        error
            .report
            .diagnostic
            .message
            .contains("directory cannot be written to a pipe or socket"),
        "{error}"
    );
    assert!(
        error
            .report
            .help
            .iter()
            .any(|help| help.contains("serialize") && help.contains("stdout")),
        "{error:?}"
    );
    assert!(
        !root.path().join("scratch").exists(),
        "directory rejection must precede workspace creation"
    );
    assert!(
        tokio::time::timeout(Duration::from_millis(50), listener.accept())
            .await
            .is_err()
    );
    assert_eq!(fs::read(work.join("directory/entry")).unwrap(), b"preserve");
    runtime.shutdown().await.unwrap();
}

#[cfg(unix)]
#[tokio::test]
async fn unix_socket_peer_eof_ends_only_its_flow_and_stop_preserves_other_target() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("two"), b"old second target").unwrap();
    let first = tokio::net::UnixListener::bind(work.join("a")).unwrap();
    let second = tokio::net::UnixListener::bind(work.join("b")).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .submit(&compile(&mut compiler, &work, "a -> one\nb -> two\n"))
        .await
        .unwrap();
    let mut a = accept_unix(&first).await;
    let mut b = accept_unix(&second).await;
    a.write_all(b"completed first").await.unwrap();
    a.shutdown().await.unwrap();
    drop(a);
    b.write_all(b"unpublished second").await.unwrap();
    let deadline = tokio::time::Instant::now() + Duration::from_secs(5);
    while !work.join("one").exists() {
        assert!(
            tokio::time::Instant::now() < deadline,
            "first socket flow did not complete independently"
        );
        runtime
            .submit(&msync::hir::Program::default())
            .await
            .unwrap();
        tokio::time::sleep(Duration::from_millis(5)).await;
    }
    assert_eq!(fs::read(work.join("one")).unwrap(), b"completed first");
    assert!(runtime.has_work());
    assert_eq!(fs::read(work.join("two")).unwrap(), b"old second target");
    runtime.cancel_active().await.unwrap();
    let mut byte = [0];
    let closed = tokio::time::timeout(Duration::from_secs(5), b.read(&mut byte))
        .await
        .unwrap();
    assert!(
        matches!(closed, Ok(0))
            || closed.is_err_and(|cause| matches!(
                cause.kind(),
                std::io::ErrorKind::ConnectionReset | std::io::ErrorKind::ConnectionAborted
            ))
    );
    assert_eq!(fs::read(work.join("two")).unwrap(), b"old second target");
    runtime.shutdown().await.unwrap();
    for name in ["a", "b"] {
        assert!(
            fs::symlink_metadata(work.join(name))
                .unwrap()
                .file_type()
                .is_socket()
        );
    }
}
