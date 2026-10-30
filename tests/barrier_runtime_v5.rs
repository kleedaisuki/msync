//! Real process gates establish ordering independently of scheduler timing.

use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::time::Duration;

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{Options, RuntimeSession};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};

const DEADLINE: Duration = Duration::from_secs(15);

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("barrier-runtime-v5-")
        .tempdir_in(root)
        .unwrap()
}

fn quoted(value: &str) -> String {
    format!("\"{}\"", value.replace('\\', "\\\\").replace('"', "\\\""))
}

fn gate_command(address: std::net::SocketAddr, complete: &Path, before: Option<&Path>) -> String {
    let executable = quoted(&std::env::current_exe().unwrap().to_string_lossy());
    let mut arguments = vec![
        "--exact".to_owned(),
        "barrier_runtime_child".to_owned(),
        "--ignored".to_owned(),
        "--nocapture".to_owned(),
        "--skip".to_owned(),
        format!("barrier-gate:{address}"),
        "--skip".to_owned(),
        format!("barrier-complete:{}", complete.display()),
    ];
    if let Some(before) = before {
        arguments.extend([
            "--skip".to_owned(),
            format!("barrier-before:{}", before.display()),
        ]);
    }
    format!(
        "$local::{executable}({})",
        arguments
            .iter()
            .map(|argument| quoted(argument))
            .collect::<Vec<_>>()
            .join(", ")
    )
}

fn compile(work: &Path, text: &str) -> msync::hir::Program {
    Session::new()
        .compile(
            &msync::language::parse(text).unwrap(),
            &CompileContext {
                pwd: work.to_owned(),
                source_file: None,
            },
        )
        .unwrap()
}

struct Invocation(Option<Child>);

impl Invocation {
    fn start(work: &Path, script: &str) -> Self {
        fs::write(work.join("journey.msync"), script).unwrap();
        let executable = std::env::var_os("MSYNC_TEST_EXE")
            .unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
        Self(Some(
            Command::new(executable)
                .args(["run", "journey.msync"])
                .current_dir(work)
                .env("NO_COLOR", "1")
                .stdin(Stdio::null())
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
                .unwrap(),
        ))
    }

    async fn finish(mut self) -> Output {
        tokio::time::timeout(DEADLINE, async {
            while self.0.as_mut().unwrap().try_wait().unwrap().is_none() {
                tokio::time::sleep(Duration::from_millis(5)).await;
            }
        })
        .await
        .expect("msync did not finish after releasing its child");
        self.0.take().unwrap().wait_with_output().unwrap()
    }
}

impl Drop for Invocation {
    fn drop(&mut self) {
        if let Some(mut child) = self.0.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

async fn accept_gate(listener: &TcpListener) -> TcpStream {
    let (mut peer, _) = tokio::time::timeout(DEADLINE, listener.accept())
        .await
        .expect("external program did not reach its gate")
        .unwrap();
    let mut ready = [0; 5];
    tokio::time::timeout(DEADLINE, peer.read_exact(&mut ready))
        .await
        .expect("external program did not announce readiness")
        .unwrap();
    assert_eq!(&ready, b"READY");
    peer
}

async fn published(path: &Path, expected: &[u8]) {
    tokio::time::timeout(DEADLINE, async {
        loop {
            if fs::read(path).is_ok_and(|bytes| bytes == expected) {
                break;
            }
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap_or_else(|_| panic!("publication never became visible: {}", path.display()));
}

fn success(output: &Output) {
    assert!(
        output.status.success(),
        "status={}\nstdout={}\nstderr={}",
        output.status,
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn async_launch_preserves_before_fence_and_await_preserves_after_fence() {
    let root = workspace();
    let before = root.path().join("before");
    let middle = root.path().join("middle");
    let after = root.path().join("after");
    let complete = root.path().join("child-complete");
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let command = gate_command(listener.local_addr().unwrap(), &complete, Some(&before));
    let script = format!(
        "\"before\" -> before\nasync Program => {command}\n\"middle\" -> middle\nawait Program\n\"after\" -> after\n"
    );
    let invocation = Invocation::start(root.path(), &script);
    let mut peer = accept_gate(&listener).await;
    published(&middle, b"middle").await;
    assert!(!complete.exists(), "the child gate was bypassed");
    assert!(!after.exists(), "a statement crossed the await boundary");
    peer.write_all(b"G").await.unwrap();
    success(&invocation.finish().await);
    assert_eq!(fs::read(before).unwrap(), b"before");
    assert_eq!(fs::read(after).unwrap(), b"after");
    assert_eq!(fs::read(complete).unwrap(), b"natural-exit");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn macro_exit_joins_its_unawaited_external_program() {
    let root = workspace();
    let inside = root.path().join("inside");
    let outside = root.path().join("outside");
    let complete = root.path().join("child-complete");
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let command = gate_command(listener.local_addr().unwrap(), &complete, None);
    let script = format!(
        "macro launch():\n    async Program => {command}\n    \"inside\" -> inside\nlaunch()\n\"outside\" -> outside\n"
    );
    let invocation = Invocation::start(root.path(), &script);
    let mut peer = accept_gate(&listener).await;
    published(&inside, b"inside").await;
    assert!(
        !outside.exists(),
        "macro-local async work escaped its lexical join"
    );
    peer.write_all(b"G").await.unwrap();
    success(&invocation.finish().await);
    assert_eq!(fs::read(outside).unwrap(), b"outside");
    assert_eq!(fs::read(complete).unwrap(), b"natural-exit");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn explicit_macro_await_allows_a_single_join_and_following_work() {
    let root = workspace();
    let joined = root.path().join("joined");
    let complete = root.path().join("child-complete");
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let command = gate_command(listener.local_addr().unwrap(), &complete, None);
    let script = format!(
        "macro launch():\n    async Program => {command}\n    await Program\n    \"joined\" -> joined\nlaunch()\n\"outside\" -> outside\n"
    );
    let invocation = Invocation::start(root.path(), &script);
    let mut peer = accept_gate(&listener).await;
    assert!(
        !joined.exists(),
        "macro statement executed before explicit await settled"
    );
    peer.write_all(b"G").await.unwrap();
    success(&invocation.finish().await);
    assert_eq!(fs::read(joined).unwrap(), b"joined");
    assert_eq!(fs::read(root.path().join("outside")).unwrap(), b"outside");
    assert_eq!(fs::read(complete).unwrap(), b"natural-exit");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn completion_scalar_does_not_block_independent_file_work_in_the_same_block() {
    let root = workspace();
    let marker = root.path().join("marker");
    let complete = root.path().join("child-complete");
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let command = gate_command(listener.local_addr().unwrap(), &complete, None);
    let script = format!(
        "async Program => {command}\nProgram::exit_code -> status\n\"go\" -> marker\nawait Program\n"
    );
    let invocation = Invocation::start(root.path(), &script);
    let mut peer = accept_gate(&listener).await;
    // The child cannot finish until this independent statement has published.
    // Waiting for its scalar inline during admission would deadlock this DAG.
    published(&marker, b"go").await;
    assert!(!complete.exists());
    peer.write_all(b"G").await.unwrap();
    success(&invocation.finish().await);
    assert_eq!(fs::read(root.path().join("status")).unwrap(), b"0\n");
    assert_eq!(fs::read(complete).unwrap(), b"natural-exit");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn a_second_async_program_launch_does_not_join_an_open_first_program_flow() {
    let root = workspace();
    let first_complete = root.path().join("first-complete");
    let second_complete = root.path().join("second-complete");
    let first_gate = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let second_gate = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let first = gate_command(first_gate.local_addr().unwrap(), &first_complete, None);
    let second = gate_command(second_gate.local_addr().unwrap(), &second_complete, None);
    let script = format!(
        "async A => {first}\nA::stdout -> first-log\nasync B => {second}\nawait A\nawait B\n"
    );
    let invocation = Invocation::start(root.path(), &script);
    let mut first_peer = accept_gate(&first_gate).await;
    let second_peer =
        tokio::time::timeout(Duration::from_secs(10), accept_gate(&second_gate)).await;
    let independent = second_peer.is_ok();
    // Release A even on regression so a serialized B can start and be joined.
    first_peer.write_all(b"G").await.unwrap();
    let second_peer = match second_peer {
        Ok(peer) => Some(peer),
        Err(_) => tokio::time::timeout(Duration::from_secs(5), accept_gate(&second_gate))
            .await
            .ok(),
    };
    if let Some(mut peer) = second_peer {
        peer.write_all(b"G").await.unwrap();
    }
    let output = invocation.finish().await;
    assert!(
        independent,
        "the second async launch waited for the first program's stdout EOF; stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );
    success(&output);
    assert_eq!(fs::read(first_complete).unwrap(), b"natural-exit");
    assert_eq!(fs::read(second_complete).unwrap(), b"natural-exit");
    assert!(
        fs::read_to_string(root.path().join("first-log"))
            .unwrap()
            .contains("COMPLETED")
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn normal_cancellation_drains_an_external_program_to_its_own_exit() {
    let root = workspace();
    let complete = root.path().join("child-complete");
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let command = gate_command(listener.local_addr().unwrap(), &complete, None);
    let program = compile(root.path(), &format!("async Program => {command}\n"));
    let mut runtime = RuntimeSession::new(&Options::new(root.path().join("scratch"))).unwrap();
    runtime.submit(&program).await.unwrap();
    let mut peer = accept_gate(&listener).await;
    let mut cancellation = Box::pin(runtime.cancel_active());
    assert!(
        tokio::time::timeout(Duration::from_millis(100), &mut cancellation)
            .await
            .is_err(),
        "cancellation returned before the gated external program exited naturally"
    );
    assert!(!complete.exists());
    peer.write_all(b"G").await.unwrap();
    tokio::time::timeout(DEADLINE, cancellation)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(complete).unwrap(), b"natural-exit");
    runtime.shutdown().await.unwrap();
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn one_network_flow_eof_does_not_cancel_an_unrelated_program() {
    let root = workspace();
    let complete = root.path().join("child-complete");
    let child_gate = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let flow_gate = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let command = gate_command(child_gate.local_addr().unwrap(), &complete, None);
    let program = compile(
        root.path(),
        &format!(
            "async Program => {command}\nwire => tcp://{}\nasync wire::/ -> flow\n",
            flow_gate.local_addr().unwrap()
        ),
    );
    let mut runtime = RuntimeSession::new(&Options::new(root.path().join("scratch"))).unwrap();
    runtime.submit(&program).await.unwrap();
    let mut child = accept_gate(&child_gate).await;
    let (mut flow, _) = tokio::time::timeout(DEADLINE, flow_gate.accept())
        .await
        .unwrap()
        .unwrap();
    flow.write_all(b"stream-complete").await.unwrap();
    flow.shutdown().await.unwrap();
    drop(flow);
    let mut waiting = Box::pin(runtime.wait());
    assert!(
        tokio::time::timeout(Duration::from_millis(100), &mut waiting)
            .await
            .is_err(),
        "flow EOF ended the session while an unrelated child was still gated"
    );
    assert!(!complete.exists());
    child.write_all(b"G").await.unwrap();
    tokio::time::timeout(DEADLINE, waiting)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(complete).unwrap(), b"natural-exit");
    assert_eq!(
        fs::read(root.path().join("flow")).unwrap(),
        b"stream-complete"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn a_program_stdin_flow_retains_its_original_file_before_a_later_overwrite() {
    for _ in 0..3 {
        let root = workspace();
        let complete = root.path().join("child-complete");
        let received = root.path().join("received");
        let original = vec![b'O'; 2 * 1024 * 1024];
        fs::write(root.path().join("source"), &original).unwrap();
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let mut command = gate_command(listener.local_addr().unwrap(), &complete, None);
        command.pop();
        command.push_str(&format!(
            ", \"--skip\", {})",
            quoted(&format!("barrier-echo:{}", received.display()))
        ));
        let program = compile(
            root.path(),
            &format!(
                "async Program => {command}\nsource -> Program::stdin\n\"new\" -> source\nawait Program\n"
            ),
        );
        let scratch = root.path().join("scratch");
        let execution = tokio::spawn(async move {
            let mut runtime = RuntimeSession::new(&Options::new(scratch)).unwrap();
            runtime.execute(&program).await.unwrap();
            runtime.shutdown().await.unwrap();
        });
        let mut peer = accept_gate(&listener).await;
        peer.write_all(b"G").await.unwrap();
        tokio::time::timeout(DEADLINE, execution)
            .await
            .unwrap()
            .unwrap();
        assert_eq!(fs::read(received).unwrap(), original);
        assert_eq!(fs::read(root.path().join("source")).unwrap(), b"new");
        assert_eq!(fs::read(complete).unwrap(), b"natural-exit");
    }
}

#[test]
#[ignore = "subprocess fixture, invoked by the real external-program runtime"]
fn barrier_runtime_child() {
    let arguments = std::env::args().collect::<Vec<_>>();
    let value = |prefix: &str| {
        arguments
            .iter()
            .find_map(|argument| argument.strip_prefix(prefix))
    };
    let Some(gate) = value("barrier-gate:") else {
        return;
    };
    if let Some(before) = value("barrier-before:") {
        assert_eq!(
            fs::read(before).unwrap(),
            b"before",
            "launch crossed its before fence"
        );
    }
    let complete = PathBuf::from(value("barrier-complete:").unwrap());
    let mut peer = std::net::TcpStream::connect(gate).unwrap();
    peer.set_read_timeout(Some(DEADLINE)).unwrap();
    peer.write_all(b"READY").unwrap();
    let mut release = [0; 1];
    peer.read_exact(&mut release).unwrap();
    assert_eq!(&release, b"G");
    if let Some(received) = value("barrier-echo:") {
        let mut bytes = Vec::new();
        std::io::stdin().read_to_end(&mut bytes).unwrap();
        fs::write(received, bytes).unwrap();
    }
    fs::write(complete, b"natural-exit").unwrap();
    println!("COMPLETED");
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn standalone_await_joins_physical_flows_before_later_file_statements() {
    use msync::execution::progress::{Phase, Progress};

    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let program = compile(
        root.path(),
        &format!(
            "wire => tcp://{}\nwire::/ -> output\nawait\n\"after\" -> marker\n",
            listener.local_addr().unwrap()
        ),
    );
    let progress = Progress::new();
    let observer = progress.clone();
    let scratch = root.path().join("scratch");
    let execution = tokio::spawn(async move {
        let mut runtime = RuntimeSession::new(&Options::new(scratch)).unwrap();
        runtime.set_progress(Some(observer));
        let result = runtime.execute(&program).await;
        let cleanup = runtime.shutdown().await;
        (result, cleanup)
    });
    let (mut peer, _) = tokio::time::timeout(DEADLINE, listener.accept())
        .await
        .unwrap()
        .unwrap();
    peer.write_all(b"complete physical flow").await.unwrap();
    // A real join phase is the acknowledgement. If await were skipped, this
    // phase would occur only in final session draining, after marker publication.
    let joining = tokio::time::timeout(DEADLINE, async {
        loop {
            if progress.snapshot().event.phase == Phase::Wait {
                break true;
            }
            if execution.is_finished() {
                break false;
            }
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .unwrap_or(false);
    let crossed_before_eof = root.path().join("marker").exists();
    peer.shutdown().await.unwrap();
    drop(peer);
    let (result, cleanup) = tokio::time::timeout(DEADLINE, execution)
        .await
        .unwrap()
        .unwrap();
    result.unwrap();
    cleanup.unwrap();
    assert!(joining, "the physical flow was never joined");
    assert!(
        !crossed_before_eof,
        "a later file statement crossed standalone await before flow EOF"
    );
    assert_eq!(
        fs::read(root.path().join("output")).unwrap(),
        b"complete physical flow"
    );
    assert_eq!(fs::read(root.path().join("marker")).unwrap(), b"after");
}

#[cfg(unix)]
#[tokio::test]
async fn readonly_workspace_uses_only_the_explicit_fallback_without_weakening_errors() {
    use std::os::unix::fs::PermissionsExt;
    // This assertion must exercise DAC, not root's permission bypass.
    assert_ne!(unsafe { libc::geteuid() }, 0);
    let root = workspace();
    let cwd = root.path().join("readonly");
    fs::create_dir(&cwd).unwrap();
    struct Restore(PathBuf);
    impl Drop for Restore {
        fn drop(&mut self) {
            let _ = fs::set_permissions(&self.0, fs::Permissions::from_mode(0o700));
        }
    }
    let _restore = Restore(cwd.clone());
    fs::set_permissions(&cwd, fs::Permissions::from_mode(0o500)).unwrap();
    let output = root.path().join("output");
    let source = format!(
        "\"fallback payload\" -> $local::{}\n",
        quoted(&output.to_string_lossy())
    );
    let program = Session::new()
        .compile(
            &msync::language::parse(&source).unwrap(),
            &CompileContext {
                pwd: cwd.clone(),
                source_file: None,
            },
        )
        .unwrap();
    let mut options = Options::new(cwd.join(".temp"));
    options.fallback_scratch_root = Some(root.path().join("state"));
    let mut session = RuntimeSession::new(&options).unwrap();
    session.execute(&program).await.unwrap();
    session.shutdown().await.unwrap();
    assert_eq!(fs::read(&output).unwrap(), b"fallback payload");
    assert!(!cwd.join(".temp").exists());
    assert!(root.path().join("state/.msync-recovery-v1").is_dir());
    // An occupied non-directory primary path is not a permission failure and
    // must remain an error, even when a writable fallback exists.
    let occupied = root.path().join("occupied");
    fs::write(&occupied, b"user data").unwrap();
    options.scratch_root = occupied;
    let mut session = RuntimeSession::new(&options).unwrap();
    assert!(session.execute(&program).await.is_err());
    assert_eq!(fs::read(&options.scratch_root).unwrap(), b"user data");
}
