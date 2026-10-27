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
        if path
            .file_name()
            .is_some_and(|name| name.to_string_lossy().starts_with("program-output-"))
            && fs::read(&path)
                .is_ok_and(|bytes| bytes.windows(marker.len()).any(|part| part == marker))
        {
            return true;
        }
    }
    false
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
