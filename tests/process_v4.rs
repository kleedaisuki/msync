//! Real child-process/pipe journeys; no external scripting runtime is required.

use std::fs;
use std::io::{Read, Write};
use std::path::Path;
use std::process::{Command, Stdio};
use std::time::Duration;

use msync::hir::ReadPort;
use msync::process::{Process, ProcessConfig};
use tokio::io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader};

const FIXTURE: &str = "MSYNC_PROCESS_V4_FIXTURE";

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("process-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn child(mode: &str) -> Command {
    let mut command = Command::new(std::env::current_exe().unwrap());
    command.args([
        "--exact",
        "process_child_fixture",
        "--ignored",
        "--nocapture",
    ]);
    command.env(FIXTURE, mode);
    command
}

fn payload(output: &[u8]) -> &[u8] {
    let marker = b"PAYLOAD:";
    let index = output
        .windows(marker.len())
        .position(|bytes| bytes == marker)
        .unwrap();
    &output[index + marker.len()..]
}

#[tokio::test]
async fn exact_argv_boundaries_are_not_shell_parsed_and_spools_are_removed() {
    let workspace = workspace();
    let values = [
        "two words",
        "",
        "引号\" and \\ slash",
        "$(touch NEVER); a -> b",
    ];
    let mut command = child("argv");
    command.current_dir(workspace.path());
    for value in values {
        command.args(["--skip", value]);
    }
    let mut process = Process::spawn_native(command, ProcessConfig::new(workspace.path())).unwrap();
    let info = tokio::time::timeout(Duration::from_secs(10), process.wait())
        .await
        .unwrap()
        .unwrap();
    assert!(info.success);
    let mut stdout = process.take_stdout().unwrap();
    assert!(
        process.take_stdout().is_none(),
        "stream ownership must not be duplicated"
    );
    let mut bytes = Vec::new();
    stdout.read_to_end(&mut bytes).await.unwrap();
    let mut expected = Vec::new();
    for value in values {
        expected.extend_from_slice(&(value.len() as u32).to_le_bytes());
        expected.extend_from_slice(value.as_bytes());
    }
    assert_eq!(payload(&bytes), expected);
    assert!(!workspace.path().join("NEVER").exists());
    drop(stdout);
    drop(process);
    assert_eq!(fs::read_dir(workspace.path()).unwrap().count(), 0);
}

#[tokio::test]
async fn utf8_stdin_large_stderr_and_late_stdout_never_deadlock_exit_scalars() {
    let workspace = workspace();
    let mut config = ProcessConfig::new(workspace.path());
    config.buffer_bytes = 4096;
    let mut process = Process::spawn_native(child("echo"), config).unwrap();
    let pid = process.pid();
    assert_eq!(
        process.scalar(ReadPort::Pid).await.unwrap(),
        format!("{pid}\n").as_bytes()
    );
    let input = "UTF-8: 可莉🌟\n\0\" -> \\ escaped\r\n"
        .repeat(4096)
        .into_bytes();
    let mut stdin = process.take_stdin().unwrap();
    stdin.write_all(&input).await.unwrap();
    stdin.shutdown().await.unwrap();
    drop(stdin);
    let exit = tokio::time::timeout(Duration::from_secs(10), process.wait())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(exit.exit_code, 37);
    assert!(!exit.success);
    assert!(!exit.cancelled);
    assert_eq!(process.scalar(ReadPort::ExitCode).await.unwrap(), b"37\n");
    assert_eq!(process.scalar(ReadPort::ExitCode).await.unwrap(), b"37\n");
    let metadata = String::from_utf8(process.scalar(ReadPort::Meta).await.unwrap()).unwrap();
    assert!(metadata.contains(&format!("\"pid\":{pid}")));
    assert!(metadata.contains("\"exit_code\":37"));
    let mut stdout = process.take_stdout().unwrap();
    let mut stderr = process.take_stderr().unwrap();
    let mut out = Vec::new();
    let mut err = Vec::new();
    stdout.read_to_end(&mut out).await.unwrap();
    stderr.read_to_end(&mut err).await.unwrap();
    assert_eq!(payload(&out), input);
    assert_eq!(err, vec![b'E'; 768 * 1024]);
    assert_eq!(exit.stdout_bytes, out.len() as u64);
    assert_eq!(exit.stderr_bytes, err.len() as u64);
}

#[tokio::test]
async fn live_output_waits_for_more_data_instead_of_reporting_temporary_file_eof() {
    let workspace = workspace();
    let mut process =
        Process::spawn_native(child("paced"), ProcessConfig::new(workspace.path())).unwrap();
    let mut stdout = BufReader::new(process.take_stdout().unwrap());
    let mut prelude = Vec::new();
    tokio::time::timeout(
        Duration::from_secs(10),
        stdout.read_until(b':', &mut prelude),
    )
    .await
    .unwrap()
    .unwrap();
    let mut prefix = [0; 6];
    stdout.read_exact(&mut prefix).await.unwrap();
    assert_eq!(&prefix, b"before");
    assert!(!process.is_finished());
    let mut next = [0];
    assert!(
        tokio::time::timeout(Duration::from_millis(30), stdout.read_exact(&mut next))
            .await
            .is_err()
    );
    process.close_stdin();
    let info = tokio::time::timeout(Duration::from_secs(10), process.wait())
        .await
        .unwrap()
        .unwrap();
    assert!(info.success);
    let mut suffix = Vec::new();
    stdout.read_to_end(&mut suffix).await.unwrap();
    assert_eq!(suffix, b"after");
}

#[tokio::test]
async fn cancellation_joins_descendant_tree_and_physically_closes_its_socket() {
    let workspace = workspace();
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut command = child("tree");
    command.env(
        "MSYNC_PROCESS_V4_SOCKET",
        listener.local_addr().unwrap().to_string(),
    );
    let mut process = Process::spawn_native(command, ProcessConfig::new(workspace.path())).unwrap();
    let (mut socket, _) = tokio::time::timeout(Duration::from_secs(10), listener.accept())
        .await
        .unwrap()
        .unwrap();
    let exit = tokio::time::timeout(Duration::from_secs(10), process.cancel_and_wait())
        .await
        .unwrap()
        .unwrap();
    assert!(exit.cancelled);
    assert!(!exit.success);
    let mut byte = [0];
    let closed = tokio::time::timeout(Duration::from_secs(3), socket.read(&mut byte))
        .await
        .unwrap();
    assert!(
        matches!(closed, Ok(0))
            || matches!(closed, Err(ref error) if error.kind() == std::io::ErrorKind::ConnectionReset),
        "a descendant still owns the accepted TCP connection: {closed:?}"
    );
}

#[tokio::test]
async fn leader_exit_quiesces_descendants_before_publishing_exit_metadata() {
    let workspace = workspace();
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut command = child("tree-exit");
    command.env(
        "MSYNC_PROCESS_V4_SOCKET",
        listener.local_addr().unwrap().to_string(),
    );
    command.env("MSYNC_PROCESS_V4_READY", workspace.path().join("connected"));
    let mut process = Process::spawn_native(command, ProcessConfig::new(workspace.path())).unwrap();
    let (mut socket, _) = tokio::time::timeout(Duration::from_secs(10), listener.accept())
        .await
        .unwrap()
        .unwrap();
    let exit = tokio::time::timeout(Duration::from_secs(10), process.wait())
        .await
        .unwrap()
        .unwrap();
    assert!(exit.success);
    assert!(!exit.cancelled);
    let mut byte = [0];
    let closed = tokio::time::timeout(Duration::from_secs(3), socket.read(&mut byte))
        .await
        .unwrap();
    assert!(
        matches!(closed, Ok(0))
            || matches!(closed, Err(ref error) if error.kind() == std::io::ErrorKind::ConnectionReset),
        "{closed:?}"
    );
}

#[tokio::test]
async fn combined_output_quota_reports_native_cause_and_joins_the_child() {
    let workspace = workspace();
    let mut config = ProcessConfig::new(workspace.path());
    config.max_output_bytes = Some(8192);
    config.buffer_bytes = 1024;
    let mut process = Process::spawn_native(child("flood"), config).unwrap();
    let error = tokio::time::timeout(Duration::from_secs(10), process.wait())
        .await
        .unwrap()
        .unwrap_err();
    assert_eq!(error.phase, "drain output");
    assert_eq!(error.cause.kind(), std::io::ErrorKind::StorageFull);
    assert_eq!(error.pid, Some(process.pid()));
    assert!(error.path.as_ref().unwrap().starts_with(workspace.path()));
    let mut stdout = process.take_stdout().unwrap();
    let mut bytes = Vec::new();
    let error = stdout.read_to_end(&mut bytes).await.unwrap_err();
    assert_eq!(error.kind(), std::io::ErrorKind::StorageFull);
    assert!(bytes.len() <= 8192);
}

#[tokio::test]
async fn invalid_configuration_and_missing_executable_do_not_leave_spools() {
    let workspace = workspace();
    let mut config = ProcessConfig::new(workspace.path());
    config.buffer_bytes = 0;
    assert!(Process::spawn_native(child("paced"), config).is_err());
    let missing = workspace.path().join("no-such-program");
    let error =
        match Process::spawn_native(Command::new(&missing), ProcessConfig::new(workspace.path())) {
            Ok(_) => panic!("missing executable launched"),
            Err(error) => error,
        };
    assert_eq!(error.cause.kind(), std::io::ErrorKind::NotFound);
    assert_eq!(fs::read_dir(workspace.path()).unwrap().count(), 0);
}

#[test]
#[ignore = "subprocess fixture; invoked explicitly by the process acceptance tests"]
fn process_child_fixture() {
    let Ok(mode) = std::env::var(FIXTURE) else {
        return;
    };
    match mode.as_str() {
        "argv" => {
            let mut out = std::io::stdout().lock();
            out.write_all(b"PAYLOAD:").unwrap();
            let args = std::env::args().collect::<Vec<_>>();
            for pair in args.windows(2).filter(|pair| pair[0] == "--skip") {
                out.write_all(&(pair[1].len() as u32).to_le_bytes())
                    .unwrap();
                out.write_all(pair[1].as_bytes()).unwrap();
            }
            out.flush().unwrap();
            std::process::exit(0);
        }
        "echo" => {
            let mut input = Vec::new();
            std::io::stdin().read_to_end(&mut input).unwrap();
            std::io::stderr()
                .write_all(&vec![b'E'; 768 * 1024])
                .unwrap();
            std::io::stdout().write_all(b"PAYLOAD:").unwrap();
            std::io::stdout().write_all(&input).unwrap();
            std::io::stdout().flush().unwrap();
            std::process::exit(37);
        }
        "paced" => {
            std::io::stdout().write_all(b"PAYLOAD:before").unwrap();
            std::io::stdout().flush().unwrap();
            let mut input = Vec::new();
            std::io::stdin().read_to_end(&mut input).unwrap();
            std::io::stdout().write_all(b"after").unwrap();
            std::io::stdout().flush().unwrap();
            std::process::exit(0);
        }
        "tree" | "tree-exit" => {
            let mut command = child("hold");
            command
                .stdout(Stdio::inherit())
                .stderr(Stdio::inherit())
                .stdin(Stdio::null());
            #[expect(
                clippy::zombie_processes,
                reason = "The owning Process must quiesce this descendant; waiting here would hide leaked tree ownership"
            )]
            let _descendant = command.spawn().unwrap();
            if mode == "tree-exit" {
                let ready = std::env::var_os("MSYNC_PROCESS_V4_READY").unwrap();
                let start = std::time::Instant::now();
                while !Path::new(&ready).exists() {
                    assert!(start.elapsed() < Duration::from_secs(10));
                    std::thread::sleep(Duration::from_millis(1));
                }
                std::process::exit(0);
            }
            std::thread::sleep(Duration::from_secs(120));
        }
        "hold" => {
            let socket =
                std::net::TcpStream::connect(std::env::var("MSYNC_PROCESS_V4_SOCKET").unwrap())
                    .unwrap();
            if let Some(ready) = std::env::var_os("MSYNC_PROCESS_V4_READY") {
                fs::write(ready, "connected").unwrap();
            }
            std::thread::sleep(Duration::from_secs(120));
            drop(socket);
        }
        "flood" => {
            let block = [b'F'; 4096];
            let mut out = std::io::stdout().lock();
            loop {
                out.write_all(&block).unwrap();
            }
        }
        other => panic!("unknown child fixture mode: {other}"),
    }
}
