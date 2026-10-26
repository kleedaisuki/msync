use std::fs;
use std::path::Path;
use std::sync::Arc;
use std::time::Duration;

use msync::compiler::{Compiler, Program};
use msync::scheduler::{ResourceLimits, execute_plan_async, prepare};
use msync::syntax;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;
use tokio::sync::{Semaphore, mpsc, oneshot};
use tokio::time::timeout;
use tokio_util::sync::CancellationToken;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("scheduler-")
        .tempdir_in(root)
        .unwrap()
}

fn operand(path: &Path) -> String {
    format!("{:?}", path.to_string_lossy().replace('\\', "/"))
}

fn program(source: &str) -> Program {
    Compiler::new()
        .compile(&syntax::parse(source).unwrap())
        .unwrap()
}

fn limits(jobs: usize, streams: usize) -> ResourceLimits {
    ResourceLimits {
        jobs,
        max_streams: streams,
        buffer_bytes: 1024,
        memory_bytes: streams * 1024,
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn fast_flow_and_finite_copy_finish_while_slow_flow_remains_open() {
    let directory = workspace();
    let input = directory.path().join("input.bin");
    let output = directory.path().join("output.bin");
    fs::write(&input, b"finite copy is not blocked by stream EOF").unwrap();
    let slow_source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let fast_source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let slow_sink = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let fast_sink = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let source = format!(
        "slow => tcp://{}\nfast => tcp://{}\nslow_out => tcp://{}\nfast_out => tcp://{}\nslow::/ -> slow_out::/\nfast::/ -> fast_out::/\n{} -> {}\n",
        slow_source.local_addr().unwrap(),
        fast_source.local_addr().unwrap(),
        slow_sink.local_addr().unwrap(),
        fast_sink.local_addr().unwrap(),
        operand(&input),
        operand(&output),
    );
    let plan = prepare(&program(&source), true).unwrap();
    let (release_slow, released) = oneshot::channel::<()>();
    let (slow_started, started) = oneshot::channel::<()>();
    let slow_sender = tokio::spawn(async move {
        let (mut stream, _) = slow_source.accept().await.unwrap();
        stream.write_all(b"slow prefix").await.unwrap();
        slow_started.send(()).unwrap();
        released.await.unwrap();
        stream.write_all(b" and suffix").await.unwrap();
        stream.shutdown().await.unwrap();
    });
    let fast_sender = tokio::spawn(async move {
        let (mut stream, _) = fast_source.accept().await.unwrap();
        stream.write_all(b"fast complete payload").await.unwrap();
        stream.shutdown().await.unwrap();
    });
    let slow_receiver = tokio::spawn(async move {
        let (mut stream, _) = slow_sink.accept().await.unwrap();
        let mut bytes = Vec::new();
        stream.read_to_end(&mut bytes).await.unwrap();
        bytes
    });
    let fast_receiver = tokio::spawn(async move {
        let (mut stream, _) = fast_sink.accept().await.unwrap();
        let mut bytes = Vec::new();
        stream.read_to_end(&mut bytes).await.unwrap();
        bytes
    });
    let task = tokio::spawn(async move {
        execute_plan_async(&plan, &limits(1, 2), CancellationToken::new()).await
    });
    timeout(Duration::from_secs(5), started)
        .await
        .expect("slow flow must start")
        .unwrap();
    let fast_bytes = timeout(Duration::from_secs(5), fast_receiver)
        .await
        .expect("fast flow EOF must be independent from slow flow")
        .unwrap();
    assert_eq!(fast_bytes, b"fast complete payload");
    timeout(Duration::from_secs(5), async {
        while !output.exists() {
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
    })
    .await
    .expect("finite copies have their own lane while streams remain open");
    assert_eq!(
        fs::read(&output).unwrap(),
        b"finite copy is not blocked by stream EOF"
    );
    assert!(
        !task.is_finished(),
        "the slow flow must remain alive independently"
    );
    release_slow.send(()).unwrap();
    let summary = timeout(Duration::from_secs(5), task)
        .await
        .unwrap()
        .unwrap()
        .unwrap();
    assert!(summary.max_active_copies <= 1);
    assert_eq!(summary.max_active_streams, 2);
    assert_eq!(
        summary.stream_bytes,
        (b"fast complete payload".len() + b"slow prefix and suffix".len()) as u64
    );
    assert_eq!(
        timeout(Duration::from_secs(5), slow_receiver)
            .await
            .unwrap()
            .unwrap(),
        b"slow prefix and suffix"
    );
    slow_sender.await.unwrap();
    fast_sender.await.unwrap();
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn finite_jobs_are_bounded_and_generated_source_dependencies_preserve_order() {
    let directory = workspace();
    let mut script = String::new();
    for index in 0..24_u8 {
        let input = directory.path().join(format!("source-{index}.bin"));
        let output = directory.path().join(format!("target-{index}.bin"));
        fs::write(&input, vec![index; 8192]).unwrap();
        script.push_str(&format!("{} -> {}\n", operand(&input), operand(&output)));
    }
    let intermediate = directory.path().join("generated.bin");
    let final_path = directory.path().join("final.bin");
    script.push_str(&format!(
        "{} -> {}\n{} -> {}\n",
        operand(&directory.path().join("target-7.bin")),
        operand(&intermediate),
        operand(&intermediate),
        operand(&final_path)
    ));
    let plan = prepare(&program(&script), true).unwrap();
    let summary = timeout(
        Duration::from_secs(10),
        execute_plan_async(&plan, &limits(3, 2), CancellationToken::new()),
    )
    .await
    .unwrap()
    .unwrap();
    assert!(summary.max_active_copies > 0 && summary.max_active_copies <= 3);
    assert_eq!(summary.max_active_streams, 0);
    for index in 0..24_u8 {
        assert_eq!(
            fs::read(directory.path().join(format!("target-{index}.bin"))).unwrap(),
            vec![index; 8192]
        );
    }
    assert_eq!(fs::read(final_path).unwrap(), vec![7_u8; 8192]);
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn cancellation_stops_a_blocked_flow_without_waiting_for_source_eof() {
    let directory = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let output = directory.path().join("partial.bin");
    let source = format!(
        "wire => tcp://{}\nwire::/ -> {}\n",
        listener.local_addr().unwrap(),
        operand(&output)
    );
    let plan = prepare(&program(&source), true).unwrap();
    let (connected, connection) = oneshot::channel::<()>();
    let sender = tokio::spawn(async move {
        let (mut stream, _) = listener.accept().await.unwrap();
        stream.write_all(b"prefix").await.unwrap();
        connected.send(()).unwrap();
        std::future::pending::<()>().await;
    });
    let cancel = CancellationToken::new();
    let worker_cancel = cancel.clone();
    let task =
        tokio::spawn(async move { execute_plan_async(&plan, &limits(1, 1), worker_cancel).await });
    timeout(Duration::from_secs(5), connection)
        .await
        .unwrap()
        .unwrap();
    cancel.cancel();
    timeout(Duration::from_secs(3), task)
        .await
        .expect("cancel must interrupt a flow waiting for bytes")
        .unwrap()
        .expect_err("canceled execution must not report successful completion");
    sender.abort();
}

#[tokio::test(flavor = "multi_thread", worker_threads = 4)]
async fn aggregate_buffer_budget_caps_live_flows_below_requested_stream_count() {
    let directory = workspace();
    let release = Arc::new(Semaphore::new(0));
    let (connected, mut connections) = mpsc::channel(4);
    let mut senders = Vec::new();
    let mut script = String::new();
    for index in 0..4_u8 {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        script.push_str(&format!(
            "wire{index} => tcp://{}\nwire{index}::/ -> {}\n",
            listener.local_addr().unwrap(),
            operand(&directory.path().join(format!("out-{index}.bin")))
        ));
        let release = Arc::clone(&release);
        let connected = connected.clone();
        senders.push(tokio::spawn(async move {
            let (mut stream, _) = listener.accept().await.unwrap();
            stream.write_all(&[index]).await.unwrap();
            connected.send(index).await.unwrap();
            release.acquire().await.unwrap().forget();
            stream.shutdown().await.unwrap();
        }));
    }
    drop(connected);
    let plan = prepare(&program(&script), true).unwrap();
    let resource_limits = ResourceLimits {
        jobs: 1,
        max_streams: 4,
        buffer_bytes: 1024,
        memory_bytes: 2048,
    };
    let worker = tokio::spawn(async move {
        execute_plan_async(&plan, &resource_limits, CancellationToken::new()).await
    });
    for _ in 0..2 {
        assert!(
            timeout(Duration::from_secs(5), connections.recv())
                .await
                .unwrap()
                .is_some()
        );
    }
    assert!(
        timeout(Duration::from_millis(150), connections.recv())
            .await
            .is_err(),
        "third flow must wait: only two buffers fit the aggregate budget"
    );
    release.add_permits(2);
    for _ in 0..2 {
        assert!(
            timeout(Duration::from_secs(5), connections.recv())
                .await
                .unwrap()
                .is_some()
        );
    }
    release.add_permits(2);
    let summary = timeout(Duration::from_secs(5), worker)
        .await
        .unwrap()
        .unwrap()
        .unwrap();
    assert_eq!(summary.max_active_streams, 2);
    assert_eq!(summary.stream_bytes, 4);
    for sender in senders {
        sender.await.unwrap();
    }
    for index in 0..4_u8 {
        assert_eq!(
            fs::read(directory.path().join(format!("out-{index}.bin"))).unwrap(),
            [index]
        );
    }
}

#[tokio::test]
async fn invalid_resource_budgets_fail_before_work_starts() {
    let plan = prepare(&Program::default(), true).unwrap();
    for invalid in [
        ResourceLimits {
            jobs: 0,
            ..limits(1, 1)
        },
        ResourceLimits {
            max_streams: 0,
            ..limits(1, 1)
        },
        ResourceLimits {
            buffer_bytes: 0,
            ..limits(1, 1)
        },
        ResourceLimits {
            memory_bytes: 1023,
            ..limits(1, 1)
        },
    ] {
        assert!(
            execute_plan_async(&plan, &invalid, CancellationToken::new())
                .await
                .is_err()
        );
    }
}
