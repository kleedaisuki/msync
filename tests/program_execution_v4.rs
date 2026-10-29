//! Direct asynchronous processes, replayable ports, and long-lived stream owners.

use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::Duration;

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{Options, RuntimeSession};
use msync::language;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("program-execution-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn context(work: &Path) -> CompileContext {
    CompileContext {
        pwd: work.to_owned(),
        source_file: None,
    }
}

fn compile(compiler: &mut Session, work: &Path, text: &str) -> msync::hir::Program {
    compiler
        .compile(&language::parse(text).unwrap(), &context(work))
        .unwrap()
}

fn executable() -> String {
    std::env::current_exe()
        .unwrap()
        .to_string_lossy()
        .replace('\\', "\\\\")
        .replace('"', "\\\"")
}

fn command(name: &str, mode: &str) -> String {
    format!(
        "{name} => $local::\"{}\"(\"--exact\", \"program_child_fixture\", \"--ignored\", \"--nocapture\", \"--skip\", \"mode:{mode}\")\n",
        executable()
    )
}

fn runtime(workspace: &Path) -> RuntimeSession {
    RuntimeSession::new(&Options::new(workspace.join("scratch"))).unwrap()
}

#[tokio::test]
async fn later_submissions_read_current_foreign_file_bytes() {
    let root = workspace();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .execute(&compile(
            &mut compiler,
            root.path(),
            "\"managed\" -> target\n",
        ))
        .await
        .unwrap();
    fs::write(root.path().join("target"), b"foreign replacement").unwrap();
    runtime
        .execute(&compile(&mut compiler, root.path(), "target -> observed\n"))
        .await
        .unwrap();
    assert_eq!(
        fs::read(root.path().join("observed")).unwrap(),
        b"foreign replacement"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn a_later_launch_failure_does_not_undo_a_completed_file_statement() {
    let root = workspace();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let program = compile(
        &mut compiler,
        root.path(),
        "\"completed\" -> earlier\nchild => $path::msync_nonexistent_program_for_barrier_test()\n",
    );
    assert!(runtime.execute(&program).await.is_err());
    assert_eq!(fs::read(root.path().join("earlier")).unwrap(), b"completed");
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn dropping_a_live_stream_owner_closes_its_peer() {
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("target"), "origin").unwrap();
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let script = format!(
        "wire => tcp://{}\nwire::/ -> target\n",
        listener.local_addr().unwrap()
    );
    runtime
        .submit(&compile(&mut compiler, &work, &script))
        .await
        .unwrap();
    let (mut peer, _) = tokio::time::timeout(Duration::from_secs(5), listener.accept())
        .await
        .unwrap()
        .unwrap();
    peer.write_all(b"UNCOMMITTED_DROP_PREFIX").await.unwrap();
    let private = runtime.private_root().unwrap().unwrap().to_owned();
    let record = runtime.private_stage_record().unwrap().unwrap().to_owned();
    drop(runtime);
    let mut byte = [0u8; 1];
    let closed = tokio::time::timeout(Duration::from_secs(5), peer.read(&mut byte))
        .await
        .unwrap();
    assert!(
        matches!(closed, Ok(0))
            || matches!(closed,Err(ref error) if matches!(error.kind(),std::io::ErrorKind::ConnectionReset|std::io::ErrorKind::ConnectionAborted)),
        "{closed:?}"
    );
    assert_eq!(fs::read(work.join("target")).unwrap(), b"origin");
    tokio::time::timeout(Duration::from_secs(5), async {
        while private.exists() || record.exists() {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .expect("retirement must keep its owner until all backing is cleaned");
}

#[tokio::test]
async fn independent_stream_submissions_publish_without_waiting_for_a_slow_peer() {
    use tokio::io::AsyncWriteExt;
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let slow = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let fast = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let first = compile(
        &mut compiler,
        &work,
        &format!("a => tcp://{}\na::/ -> slow\n", slow.local_addr().unwrap()),
    );
    runtime.submit(&first).await.unwrap();
    let (mut slow_peer, _) = slow.accept().await.unwrap();
    let second = compile(
        &mut compiler,
        &work,
        &format!("b => tcp://{}\nb::/ -> fast\n", fast.local_addr().unwrap()),
    );
    runtime.submit(&second).await.unwrap();
    let (mut fast_peer, _) = fast.accept().await.unwrap();
    fast_peer.write_all(b"ready second").await.unwrap();
    fast_peer.shutdown().await.unwrap();
    drop(fast_peer);
    let task = tokio::spawn(async move {
        let result = runtime.wait().await;
        (runtime, result)
    });
    tokio::time::timeout(Duration::from_secs(5), async {
        while !fs::read(work.join("fast")).is_ok_and(|bytes| bytes == b"ready second") {
            assert!(
                !task.is_finished(),
                "the stream owner failed before the ready peer completed"
            );
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    assert_eq!(fs::read(work.join("fast")).unwrap(), b"ready second");
    slow_peer.write_all(b"late first").await.unwrap();
    slow_peer.shutdown().await.unwrap();
    drop(slow_peer);
    let (mut runtime, result) = tokio::time::timeout(Duration::from_secs(5), task)
        .await
        .unwrap()
        .unwrap();
    result.unwrap();
    assert_eq!(fs::read(work.join("slow")).unwrap(), b"late first");
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn stopping_a_stream_releases_its_peer_and_allows_a_fresh_statement() {
    use tokio::io::AsyncReadExt;
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
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
    let (mut peer, _) = listener.accept().await.unwrap();
    runtime.cancel_active().await.unwrap();
    assert_eq!(
        tokio::time::timeout(Duration::from_secs(5), peer.read(&mut [0u8; 1]))
            .await
            .unwrap()
            .unwrap(),
        0
    );
    runtime
        .execute(&compile(&mut compiler, &work, "\"after stop\" -> target\n"))
        .await
        .unwrap();
    assert_eq!(
        fs::read_to_string(work.join("target")).unwrap(),
        "after stop"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn an_empty_runtime_submission_does_not_create_an_execution_workspace() {
    let root = workspace();
    let mut runtime = runtime(root.path());
    assert_eq!(
        runtime
            .execute(&msync::hir::Program::default())
            .await
            .unwrap()
            .assignments,
        0
    );
    runtime.shutdown().await.unwrap();
    assert!(!root.path().join("scratch").exists());
}

#[tokio::test]
async fn invalid_resource_configuration_precedes_any_data_capture() {
    let root = workspace();
    let mut options = Options::new(root.path().join("scratch"));
    options.limits.jobs = 0;
    assert!(RuntimeSession::new(&options).is_err());
    options.limits.jobs = usize::MAX;
    assert!(RuntimeSession::new(&options).is_err());
    assert!(!root.path().join("scratch").exists());
}

#[tokio::test]
async fn replayed_scalars_finish_each_implicit_statement_before_staging_its_sibling() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .execute(&compile(&mut compiler, &work, &command("child", "verify")))
        .await
        .unwrap();
    let copies =
        "child::exit_code -> first\nchild::exit_code -> second\nchild::exit_code -> third\n";
    tokio::time::timeout(
        Duration::from_secs(15),
        runtime.execute(&compile(&mut compiler, &work, copies)),
    )
    .await
    .unwrap()
    .unwrap();
    for name in ["first", "second", "third"] {
        assert_eq!(fs::read(work.join(name)).unwrap(), b"0\n");
    }
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn large_stderr_and_stdin_co_start_and_nonzero_exit_remains_replayable_data() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let input = "可莉\0\n".repeat(16384);
    fs::write(work.join("input"), &input).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let script = format!(
        "async {}input -> child::stdin\nchild::stdout -> output\nchild::stderr -> errors\nawait child\nchild::exit_code -> code\nchild::exit_code -> again\n",
        command("child", "echo")
    );
    let program = compile(&mut compiler, &work, &script);
    tokio::time::timeout(Duration::from_secs(30), runtime.execute(&program))
        .await
        .unwrap()
        .unwrap();
    let output = fs::read(work.join("output")).unwrap();
    assert!(
        output
            .windows(input.len())
            .any(|part| part == input.as_bytes())
    );
    assert!(fs::metadata(work.join("errors")).unwrap().len() >= 768 * 1024);
    assert_eq!(fs::read_to_string(work.join("code")).unwrap(), "7\n");
    assert_eq!(
        fs::read(work.join("code")).unwrap(),
        fs::read(work.join("again")).unwrap()
    );
    assert_eq!(
        fs::read_to_string(work.join("side.txt")).unwrap(),
        "direct external IO"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn program_and_ports_remain_alive_across_repl_style_submissions() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .submit(&compile(
            &mut compiler,
            &work,
            &format!("async {}", command("child", "echo")),
        ))
        .await
        .unwrap();
    assert_eq!(runtime.live_processes().len(), 1);
    runtime
        .submit(&compile(
            &mut compiler,
            &work,
            "\"across inputs\" -> child::stdin\n",
        ))
        .await
        .unwrap();
    runtime
        .submit(&compile(&mut compiler, &work, "child::stdout -> output\n"))
        .await
        .unwrap();
    tokio::time::timeout(
        Duration::from_secs(30),
        runtime.execute(&compile(&mut compiler, &work, "await child\n")),
    )
    .await
    .unwrap()
    .unwrap();
    assert!(
        fs::read_to_string(work.join("output"))
            .unwrap()
            .contains("across inputs")
    );
    assert!(!runtime.has_work());
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn preexisting_user_scratch_survives_direct_process_execution() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir_all(work.join(".temp")).unwrap();
    fs::write(work.join(".temp/user-data"), "retain user scratch").unwrap();
    let mut compiler = Session::new();
    let mut runtime = RuntimeSession::new(&Options::new(work.join(".temp"))).unwrap();
    let script = format!(
        "async {}\"cwd input\" -> child::stdin\nchild::stdout -> output\nawait child\n",
        command("child", "echo")
    );
    let program = compile(&mut compiler, &work, &script);
    tokio::time::timeout(Duration::from_secs(30), runtime.execute(&program))
        .await
        .unwrap()
        .unwrap();
    assert!(
        fs::read_to_string(work.join("output"))
            .unwrap()
            .contains("cwd input")
    );
    assert_eq!(
        fs::read_to_string(work.join(".temp/user-data")).unwrap(),
        "retain user scratch"
    );
    runtime.shutdown().await.unwrap();
}

#[test]
#[ignore = "subprocess fixture; started only by direct process tests"]
fn program_child_fixture() {
    let mode = std::env::args()
        .find_map(|argument| argument.strip_prefix("mode:").map(str::to_owned))
        .unwrap();
    match mode.as_str() {
        "echo" => {
            std::io::stderr()
                .write_all(&vec![b'E'; 768 * 1024])
                .unwrap();
            let mut bytes = Vec::new();
            std::io::stdin().read_to_end(&mut bytes).unwrap();
            fs::write("side.txt", "direct external IO").unwrap();
            std::io::stdout().write_all(b"PAYLOAD:").unwrap();
            std::io::stdout().write_all(&bytes).unwrap();
            std::io::stdout().flush().unwrap();
            std::process::exit(7);
        }
        "verify" => {
            assert!(!PathBuf::from("side.txt").exists());
        }
        "overlap" => {
            fs::write("output", "EARLIER_PRIVATE_WRITE").unwrap();
            println!("DECLARED_STREAM_WINS");
        }
        "producer" => {
            fs::write("shared", "producer direct write").unwrap();
            std::io::stdout()
                .write_all(&vec![b'P'; 512 * 1024])
                .unwrap();
            std::io::stdout().write_all(b"PIPELINE_END").unwrap();
            std::io::stdout().flush().unwrap();
        }
        "consumer" => {
            let mut bytes = Vec::new();
            std::io::stdin().read_to_end(&mut bytes).unwrap();
            assert!(bytes.windows(12).any(|part| part == b"PIPELINE_END"));
            assert_eq!(
                fs::read_to_string("shared").unwrap(),
                "producer direct write"
            );
            fs::write("consumer-side", "same real filesystem").unwrap();
            println!("SHARED_PIPELINE_OK:{}", bytes.len());
        }
        "exit-value" => {
            fs::write("scalar-shared", "direct scalar producer").unwrap();
            std::process::exit(13);
        }
        "scalar-consumer" => {
            let mut code = String::new();
            std::io::stdin().read_to_string(&mut code).unwrap();
            assert_eq!(code, "13\n");
            assert_eq!(
                fs::read_to_string("scalar-shared").unwrap(),
                "direct scalar producer"
            );
            println!("SCALAR_PIPE_READY");
        }
        _ => panic!("unexpected child mode"),
    }
}

#[tokio::test]
async fn pipeline_can_be_connected_across_repl_submissions() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    for script in [
        format!("async {}", command("p", "producer")),
        format!("async {}", command("q", "consumer")),
        "p::stdout -> q::stdin\nq::stdout -> output\n".into(),
    ] {
        tokio::time::timeout(
            Duration::from_secs(5),
            runtime.submit(&compile(&mut compiler, &work, &script)),
        )
        .await
        .unwrap()
        .unwrap();
    }
    tokio::time::timeout(
        Duration::from_secs(30),
        runtime.execute(&compile(&mut compiler, &work, "await q\nawait p\n")),
    )
    .await
    .unwrap()
    .unwrap();
    assert!(
        fs::read_to_string(work.join("output"))
            .unwrap()
            .contains("SHARED_PIPELINE_OK")
    );
    assert_eq!(
        fs::read_to_string(work.join("consumer-side")).unwrap(),
        "same real filesystem"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn exit_data_can_feed_an_async_child_stdin() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let script = format!(
        "{}async {}p::exit_code -> q::stdin\nq::stdout -> result\nawait q\n",
        command("p", "exit-value"),
        command("q", "scalar-consumer")
    );
    let program = compile(&mut compiler, &work, &script);
    tokio::time::timeout(Duration::from_secs(30), runtime.execute(&program))
        .await
        .unwrap()
        .unwrap();
    assert!(
        fs::read_to_string(work.join("result"))
            .unwrap()
            .contains("SCALAR_PIPE_READY")
    );
    runtime.shutdown().await.unwrap();
}
