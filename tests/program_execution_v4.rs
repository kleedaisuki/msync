//! Actual compiler -> session -> mediated process -> transactional file journeys.

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
    let mut session = RuntimeSession::new(&Options::new(workspace.join("scratch"))).unwrap();
    session.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
    session
}

#[tokio::test]
async fn quiescent_submissions_refresh_foreign_bytes_without_forgetting_named_origin() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("target"), "origin").unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .execute(&compile(
            &mut compiler,
            &work,
            "transaction A:\n    \"managed\" -> target\n",
        ))
        .await
        .unwrap();
    fs::write(work.join("target"), "foreign").unwrap();
    let error = runtime
        .execute(&compile(&mut compiler, &work, "rollback A\n"))
        .await
        .unwrap_err();
    assert!(error.to_string().contains("conflict"), "{error}");
    assert_eq!(fs::read_to_string(work.join("target")).unwrap(), "foreign");
    runtime
        .execute(&compile(&mut compiler, &work, "target -> observed\n"))
        .await
        .unwrap();
    assert_eq!(
        fs::read_to_string(work.join("observed")).unwrap(),
        "foreign"
    );
    runtime.shutdown().await.unwrap();
}

fn has_marker(root: &Path, marker: &[u8]) -> bool {
    let Ok(entries) = fs::read_dir(root) else {
        return false;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() && has_marker(&path, marker) {
            return true;
        }
        if path.file_name().is_some_and(|name| {
            let name = name.to_string_lossy();
            name.starts_with("program-output-")
                || name.starts_with("physical-output-")
                || (name.starts_with(".msync-") && name.ends_with(".part"))
        }) && fs::read(&path)
            .is_ok_and(|bytes| bytes.windows(marker.len()).any(|part| part == marker))
        {
            return true;
        }
    }
    false
}

#[tokio::test]
async fn dropping_a_live_session_closes_its_peer_without_publishing_pending_bytes() {
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
    tokio::time::timeout(Duration::from_secs(5), async {
        while !has_marker(root.path(), b"UNCOMMITTED_DROP_PREFIX") {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    drop(runtime);
    let mut byte = [0u8; 1];
    assert_eq!(
        tokio::time::timeout(Duration::from_secs(5), peer.read(&mut byte))
            .await
            .unwrap()
            .unwrap(),
        0
    );
    assert_eq!(fs::read_to_string(work.join("target")).unwrap(), "origin");
}

#[tokio::test]
async fn two_submissions_reusing_node_zero_publish_ready_namespaces_without_waiting_for_slow_peers()
{
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
    assert_eq!(
        first.body.instructions[0].id,
        second.body.instructions[0].id
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
        while !work.join("fast").exists() {
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    assert_eq!(fs::read(work.join("fast")).unwrap(), b"ready second");
    assert!(!work.join("slow").exists());
    let selected_modified = fs::metadata(&work).unwrap().modified().unwrap();
    slow_peer.write_all(b"late first").await.unwrap();
    slow_peer.shutdown().await.unwrap();
    drop(slow_peer);
    let (mut runtime, result) = tokio::time::timeout(Duration::from_secs(5), task)
        .await
        .unwrap()
        .unwrap();
    result.unwrap();
    assert_eq!(fs::read(work.join("slow")).unwrap(), b"late first");
    assert_eq!(
        fs::metadata(&work).unwrap().modified().unwrap(),
        selected_modified
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn stopping_a_reserved_missing_file_flow_releases_its_namespace_capability() {
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
    assert!(!work.join("target").exists());
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
async fn named_file_origins_survive_separate_compiler_and_runtime_submissions() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("target"), "origin").unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let first = compile(
        &mut compiler,
        &work,
        "transaction A:\n    \"checkpoint\" -> target\n    commit\n    \"temporary\" -> newly-created\n",
    );
    runtime.execute(&first).await.unwrap();
    assert_eq!(
        fs::read_to_string(work.join("target")).unwrap(),
        "checkpoint"
    );
    let undo = compile(&mut compiler, &work, "rollback A\n");
    runtime.execute(&undo).await.unwrap();
    assert_eq!(fs::read_to_string(work.join("target")).unwrap(), "origin");
    assert!(!work.join("newly-created").exists());
    runtime.shutdown().await.unwrap();
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
async fn a_program_payload_admission_failure_precedes_an_earlier_literal_write() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut options = Options::new(root.path().join("scratch"));
    options.limits.memory_bytes = options.limits.buffer_bytes * 5;
    let mut runtime = RuntimeSession::new(&options).unwrap();
    runtime.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
    let mut compiler = Session::new();
    let program = compile(
        &mut compiler,
        &work,
        "\"must not publish\" -> earlier\nchild => $path::missing-program()\n",
    );
    let failure = runtime.execute(&program).await.unwrap_err();
    assert!(failure.to_string().contains("cannot admit one program"));
    assert!(!work.join("earlier").exists());
    assert!(!root.path().join("scratch").exists());
}

#[tokio::test]
async fn a_prepared_file_program_runs_with_its_replay_checked_certificate() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut compiler = Session::new();
    let program = compile(
        &mut compiler,
        &work,
        "\"first\" -> target\n\"final\" -> target\n",
    );
    let prepared = msync::preparation::prepare(&program, true).unwrap();
    let mut runtime = runtime(root.path());
    runtime.execute_prepared(&prepared).await.unwrap();
    assert_eq!(fs::read_to_string(work.join("target")).unwrap(), "final");
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn the_requested_checkpoint_and_program_port_lifecycle_runs_end_to_end() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("prompt.txt"), "UTF-8 可莉🌟\n").unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let script = format!(
        "transaction A1(read uncommitted):\n    {}    prompt.txt -> child::stdin\n    child::stdout -> output.txt\n    child::stderr -> errors.txt\n    commit\n    child::exit_code -> exit.txt\n    commit\n    child::exit_code -> pending.txt\n    rollback\nrollback A1\ntransaction B2(serializable):\n    {}    verifier::pid -> pid\n\"success\" -> result.txt\n",
        command("child", "echo"),
        command("verifier", "verify")
    );
    let program = compile(&mut compiler, &work, &script);
    tokio::time::timeout(Duration::from_secs(30), runtime.execute(&program))
        .await
        .unwrap()
        .unwrap();
    for name in [
        "output.txt",
        "errors.txt",
        "exit.txt",
        "pending.txt",
        "side.txt",
    ] {
        assert!(!work.join(name).exists(), "origin undo left {name}");
    }
    assert!(
        fs::read_to_string(work.join("pid"))
            .unwrap()
            .trim()
            .parse::<u32>()
            .unwrap()
            > 0
    );
    assert_eq!(
        fs::read_to_string(work.join("result.txt")).unwrap(),
        "success"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn large_stderr_and_stdin_co_start_and_nonzero_exit_remains_replayable_data() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let input = "可莉\0\n".repeat(16384);
    fs::write(work.join("input"), &input).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let script = format!(
        "{}input -> child::stdin\nchild::stdout -> output\nchild::stderr -> errors\nchild::exit_code -> code\nchild::exit_code -> again\n",
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
        "private direct IO"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn program_and_ports_remain_alive_across_repl_style_submissions() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .submit(&compile(&mut compiler, &work, &command("child", "echo")))
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
    tokio::time::timeout(Duration::from_secs(30), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    assert!(
        fs::read_to_string(work.join("output"))
            .unwrap()
            .contains("across inputs")
    );
    assert!(runtime.live_processes().is_empty());
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn cancellation_joins_before_origin_restore_and_never_exports_partial_program_writes() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("target"), "origin").unwrap();
    let mut compiler = Session::new();
    let script = format!(
        "transaction A:\n    {}    child::stdout -> target\n",
        command("child", "cancel")
    );
    let program = compile(&mut compiler, &work, &script);
    let mut runtime = runtime(root.path());
    let cancellation = runtime.cancellation();
    let task = tokio::spawn(async move {
        let result = runtime.execute(&program).await;
        runtime.shutdown().await.unwrap();
        result
    });
    let deadline = tokio::time::Instant::now() + Duration::from_secs(15);
    while !has_marker(&root.path().join("scratch"), b"PRIVATE_PROGRAM_READY") {
        assert!(
            tokio::time::Instant::now() < deadline,
            "the real child did not produce a private output prefix"
        );
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
    assert_eq!(fs::read_to_string(work.join("target")).unwrap(), "origin");
    assert!(!work.join("partial").exists());
    cancellation.cancel();
    assert!(
        tokio::time::timeout(Duration::from_secs(15), task)
            .await
            .unwrap()
            .unwrap()
            .is_err()
    );
    assert_eq!(fs::read_to_string(work.join("target")).unwrap(), "origin");
    assert!(!work.join("partial").exists());
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn default_cwd_scratch_is_hidden_but_preexisting_user_scratch_survives() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir_all(work.join(".temp")).unwrap();
    fs::write(work.join(".temp/user-data"), "retain user scratch").unwrap();
    let mut compiler = Session::new();
    let mut runtime = RuntimeSession::new(&Options::new(work.join(".temp"))).unwrap();
    runtime.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
    let script = format!(
        "{}\"cwd input\" -> child::stdin\nchild::stdout -> output\n",
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
    assert_eq!(fs::read_dir(work.join(".temp")).unwrap().count(), 1);
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn declared_stdout_publication_follows_the_programs_private_file_delta() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("output"), "origin").unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let script = format!(
        "transaction A:\n    {}    child::stdout -> output\n",
        command("child", "overlap")
    );
    let program = compile(&mut compiler, &work, &script);
    tokio::time::timeout(Duration::from_secs(30), runtime.execute(&program))
        .await
        .unwrap()
        .unwrap();
    let output = fs::read_to_string(work.join("output")).unwrap();
    assert!(output.contains("DECLARED_STREAM_WINS"), "{output}");
    assert!(!output.contains("EARLIER_PRIVATE_WRITE"));
    runtime
        .execute(&compile(&mut compiler, &work, "rollback A\n"))
        .await
        .unwrap();
    assert_eq!(fs::read_to_string(work.join("output")).unwrap(), "origin");
    runtime.shutdown().await.unwrap();
}

#[test]
#[ignore = "subprocess fixture; started only by mediated execution tests"]
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
            fs::write("side.txt", "private direct IO").unwrap();
            std::io::stdout().write_all(b"PAYLOAD:").unwrap();
            std::io::stdout().write_all(&bytes).unwrap();
            std::io::stdout().flush().unwrap();
            std::process::exit(7);
        }
        "verify" => {
            assert!(!PathBuf::from("side.txt").exists());
        }
        "cancel" => {
            fs::write("partial", "must never be exported").unwrap();
            println!("PRIVATE_PROGRAM_READY");
            std::io::stdout().flush().unwrap();
            std::thread::sleep(Duration::from_secs(60));
        }
        "overlap" => {
            fs::write("output", "EARLIER_PRIVATE_WRITE").unwrap();
            println!("DECLARED_STREAM_WINS");
        }
        "producer" => {
            fs::write("shared", "producer private epoch").unwrap();
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
                "producer private epoch"
            );
            fs::write("consumer-side", "same native view").unwrap();
            println!("SHARED_PIPELINE_OK:{}", bytes.len());
        }
        "input-producer" => {
            let mut bytes = Vec::new();
            std::io::stdin().read_to_end(&mut bytes).unwrap();
            std::io::stdout().write_all(&bytes).unwrap();
            std::io::stdout().flush().unwrap();
            std::thread::sleep(Duration::from_millis(400));
        }
        "input-consumer" => {
            let input = fs::read_to_string("input").unwrap();
            assert!(input.contains("new input version"), "{input}");
            println!("FRESH_FILE_INPUT");
            std::io::stdout().flush().unwrap();
            std::thread::sleep(Duration::from_millis(400));
        }
        "exit-value" => {
            fs::write("scalar-shared", "native scalar cohort").unwrap();
            std::process::exit(13);
        }
        "scalar-consumer" => {
            let mut code = String::new();
            std::io::stdin().read_to_string(&mut code).unwrap();
            assert_eq!(code, "13\n");
            assert_eq!(
                fs::read_to_string("scalar-shared").unwrap(),
                "native scalar cohort"
            );
            println!("SCALAR_PIPE_READY");
        }
        _ => panic!("unexpected child mode"),
    }
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn connected_children_share_one_private_epoch_and_rollback_origin() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("shared"), "origin").unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let script = format!(
        "transaction A:\n    {}    {}    p::stdout -> q::stdin\n    q::stdout -> output\n    commit\n",
        command("p", "producer"),
        command("q", "consumer")
    );
    let program = compile(&mut compiler, &work, &script);
    tokio::time::timeout(Duration::from_secs(30), runtime.execute(&program))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(
        fs::read_to_string(work.join("shared")).unwrap(),
        "producer private epoch"
    );
    assert_eq!(
        fs::read_to_string(work.join("consumer-side")).unwrap(),
        "same native view"
    );
    assert!(
        fs::read_to_string(work.join("output"))
            .unwrap()
            .contains("SHARED_PIPELINE_OK")
    );
    runtime
        .execute(&compile(&mut compiler, &work, "rollback A\n"))
        .await
        .unwrap();
    assert_eq!(fs::read_to_string(work.join("shared")).unwrap(), "origin");
    assert!(!work.join("consumer-side").exists());
    assert!(!work.join("output").exists());
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn pipeline_can_be_connected_across_repl_submissions() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    for script in [
        command("p", "producer"),
        command("q", "consumer"),
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
    tokio::time::timeout(Duration::from_secs(30), runtime.wait())
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
        "same native view"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn rejected_prepared_member_never_starts_or_discards_an_existing_shared_peer() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("output"), "origin").unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    runtime
        .submit(&compile(&mut compiler, &work, &command("p", "echo")))
        .await
        .unwrap();
    let original = runtime.live_processes();
    let mut invalid = compile(&mut compiler, &work, &command("q", "overlap"));
    let mut duplicate = invalid.body.instructions[0].clone();
    duplicate.id.0 += 1000;
    invalid.body.instructions.push(duplicate);
    let error = tokio::time::timeout(Duration::from_secs(10), runtime.submit(&invalid))
        .await
        .unwrap()
        .unwrap_err();
    assert!(
        error.to_string().contains("more than one launch"),
        "{error}"
    );
    assert_eq!(runtime.live_processes(), original);
    assert_eq!(fs::read_to_string(work.join("output")).unwrap(), "origin");
    runtime.cancel_active().await.unwrap();
    assert!(runtime.live_processes().is_empty());
    assert_eq!(fs::read_to_string(work.join("output")).unwrap(), "origin");
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn managed_output_seals_an_epoch_before_a_later_program_reads_its_pending_file() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    fs::write(work.join("input"), "old input version").unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let script = format!(
        "transaction A:\n    {}    \"new input version\" -> p::stdin\n    p::stdout -> input\n    {}    q::stdout -> result\n",
        command("p", "input-producer"),
        command("q", "input-consumer")
    );
    let program = compile(&mut compiler, &work, &script);
    let private = root.path().to_owned();
    let task = tokio::spawn(async move {
        let result = runtime.execute(&program).await;
        (runtime, result)
    });
    tokio::time::timeout(Duration::from_secs(30), async {
        while !has_marker(&private, b"FRESH_FILE_INPUT") {
            assert!(
                !task.is_finished(),
                "the pending-view consumer failed before observing the new input"
            );
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap();
    assert_eq!(
        fs::read_to_string(work.join("input")).unwrap(),
        "old input version"
    );
    let (mut runtime, result) = tokio::time::timeout(Duration::from_secs(30), task)
        .await
        .unwrap()
        .unwrap();
    result.unwrap();
    assert!(
        fs::read_to_string(work.join("input"))
            .unwrap()
            .contains("new input version")
    );
    assert!(
        fs::read_to_string(work.join("result"))
            .unwrap()
            .contains("FRESH_FILE_INPUT")
    );
    runtime
        .execute(&compile(&mut compiler, &work, "rollback A\n"))
        .await
        .unwrap();
    assert_eq!(
        fs::read_to_string(work.join("input")).unwrap(),
        "old input version"
    );
    assert!(!work.join("result").exists());
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn exit_data_can_feed_a_live_peer_without_exporting_the_shared_view_first() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let script = format!(
        "{}{}p::exit_code -> q::stdin\nq::stdout -> result\n",
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

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn checkpoint_rollback_cancels_pending_native_io_and_starts_a_fresh_epoch() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let script = format!(
        "transaction A:\n    \"checkpoint\" -> saved\n    commit\n    {}    child::stdout -> discarded\n    child::pid -> pending_pid\n    rollback\n    {}    verifier::pid -> fresh_pid\n    \"after reset\" -> after\n",
        command("child", "cancel"),
        command("verifier", "verify"),
    );
    let program = compile(&mut compiler, &work, &script);
    tokio::time::timeout(Duration::from_secs(30), runtime.execute(&program))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(
        fs::read_to_string(work.join("saved")).unwrap(),
        "checkpoint"
    );
    assert_eq!(
        fs::read_to_string(work.join("after")).unwrap(),
        "after reset"
    );
    assert!(work.join("fresh_pid").is_file());
    for name in ["partial", "discarded", "pending_pid"] {
        assert!(
            !work.join(name).exists(),
            "discarded epoch published {name}"
        );
    }
    let undo = compile(&mut compiler, &work, "rollback A\n");
    runtime.execute(&undo).await.unwrap();
    for name in ["saved", "after", "fresh_pid"] {
        assert!(!work.join(name).exists(), "origin undo retained {name}");
    }
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn stopping_a_native_owner_cancels_old_dependencies_without_poisoning_the_session() {
    let root = workspace();
    let work = root.path().join("work");
    fs::create_dir(&work).unwrap();
    let mut compiler = Session::new();
    let mut runtime = runtime(root.path());
    let program = compile(
        &mut compiler,
        &work,
        &format!("{}child::stdout -> discarded\n", command("child", "cancel"),),
    );
    runtime.submit(&program).await.unwrap();
    tokio::time::timeout(Duration::from_secs(30), runtime.cancel_active())
        .await
        .unwrap()
        .unwrap();
    assert!(!runtime.has_work());
    let old_dependency = compile(&mut compiler, &work, "child::exit_code -> old_exit\n");
    let failure = tokio::time::timeout(Duration::from_secs(3), runtime.execute(&old_dependency))
        .await
        .unwrap()
        .unwrap_err();
    assert!(failure.to_string().contains("discarded"), "{failure}");
    let fresh = compile(&mut compiler, &work, "\"fresh\" -> fresh\n");
    runtime.execute(&fresh).await.unwrap();
    assert_eq!(fs::read_to_string(work.join("fresh")).unwrap(), "fresh");
    assert!(!work.join("partial").exists());
    assert!(!work.join("old_exit").exists());
    runtime.shutdown().await.unwrap();
}
