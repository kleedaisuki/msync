//! A root process's exit is observable before inherited output reaches EOF.

use std::fs;
use std::io::{Read, Write};
use std::net::{TcpListener, TcpStream};
use std::path::Path;
use std::process::{Child, Command, Output, Stdio};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

const DESCENDANT_MARKER: &[u8] = b"DESCENDANT_RELEASED_AND_SELF_EXITED";

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("process-exit-observation-")
        .tempdir_in(root)
        .unwrap()
}

struct Invocation {
    child: Child,
    stdout: Option<JoinHandle<Vec<u8>>>,
    stderr: Option<JoinHandle<Vec<u8>>>,
}

impl Invocation {
    fn start(root: &Path) -> Self {
        let executable = std::env::var_os("MSYNC_TEST_EXE")
            .unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
        let mut child = Command::new(executable)
            .args(["run", "exit-observation.msync", "--color", "never"])
            .current_dir(root)
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let mut stdout = child.stdout.take().unwrap();
        let mut stderr = child.stderr.take().unwrap();
        Self {
            child,
            stdout: Some(thread::spawn(move || {
                let mut bytes = Vec::new();
                stdout.read_to_end(&mut bytes).unwrap();
                bytes
            })),
            stderr: Some(thread::spawn(move || {
                let mut bytes = Vec::new();
                stderr.read_to_end(&mut bytes).unwrap();
                bytes
            })),
        }
    }

    fn finish(&mut self) -> Output {
        let deadline = Instant::now() + Duration::from_secs(15);
        let status = loop {
            if let Some(status) = self.child.try_wait().unwrap() {
                break status;
            }
            assert!(
                Instant::now() < deadline,
                "msync did not settle after descendant output EOF"
            );
            thread::sleep(Duration::from_millis(5));
        };
        Output {
            status,
            stdout: self.stdout.take().unwrap().join().unwrap(),
            stderr: self.stderr.take().unwrap().join().unwrap(),
        }
    }
}

impl Drop for Invocation {
    fn drop(&mut self) {
        // Failure cleanup closes the TCP gate first, allowing the descendant to
        // exit itself. The fallback kill is only a bounded failed-test watchdog.
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            if self.child.try_wait().ok().flatten().is_some() {
                return;
            }
            if Instant::now() >= deadline {
                let _ = self.child.kill();
                let _ = self.child.wait();
                return;
            }
            thread::sleep(Duration::from_millis(5));
        }
    }
}

#[test]
fn root_exit_code_is_available_while_a_descendant_keeps_stdout_open() {
    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let address = listener.local_addr().unwrap();
    let executable = std::env::current_exe()
        .unwrap()
        .to_string_lossy()
        .replace('\\', "\\\\")
        .replace('"', "\\\"");
    fs::write(
        root.path().join("exit-observation.msync"),
        format!(
            "async Root => $local::\"{executable}\"(\"--exact\", \"process_exit_child_fixture\", \"--ignored\", \"--nocapture\", \"--skip\", \"mode:root:{address}\")\nRoot::stdout -> captured\nRoot::exit_code -> status\nawait Root\n"
        ),
    )
    .unwrap();
    let mut invocation = Invocation::start(root.path());
    let deadline = Instant::now() + Duration::from_secs(10);
    let mut gate = loop {
        match listener.accept() {
            Ok((stream, _)) => break stream,
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                assert!(
                    Instant::now() < deadline,
                    "the actual descendant never connected its gate"
                );
                assert!(
                    invocation.child.try_wait().unwrap().is_none(),
                    "msync exited before descendant readiness"
                );
                thread::sleep(Duration::from_millis(5));
            }
            Err(error) => panic!("cannot accept descendant readiness: {error}"),
        }
    };
    gate.set_read_timeout(Some(Duration::from_secs(5))).unwrap();
    let mut ready = [0u8; 1];
    gate.read_exact(&mut ready).unwrap();
    assert_eq!(ready, [b'R']);

    let deadline = Instant::now() + Duration::from_secs(5);
    let status_before_release = loop {
        if fs::read_to_string(root.path().join("status")).is_ok_and(|text| text.trim() == "7") {
            break true;
        }
        if Instant::now() >= deadline || invocation.child.try_wait().unwrap().is_some() {
            break false;
        }
        thread::sleep(Duration::from_millis(5));
    };
    let cli_still_running = invocation.child.try_wait().unwrap().is_none();
    let descendant_output_already_visible =
        fs::read(root.path().join("captured")).is_ok_and(|bytes| {
            bytes
                .windows(DESCENDANT_MARKER.len())
                .any(|part| part == DESCENDANT_MARKER)
        });
    // Release even on a failed observation. EOF also releases the descendant,
    // so assertion unwinding never strands it behind a test-owned socket.
    let _ = gate.write_all(b"finish");
    drop(gate);
    let output = invocation.finish();
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );
    assert!(
        status_before_release,
        "root exit_code remained coupled to descendant output EOF"
    );
    assert!(
        cli_still_running,
        "msync abandoned an output stream that was still owned and open"
    );
    assert!(
        !descendant_output_already_visible,
        "descendant bypassed the explicit test gate"
    );
    let captured = fs::read(root.path().join("captured")).unwrap();
    assert!(
        captured
            .windows(DESCENDANT_MARKER.len())
            .any(|part| part == DESCENDANT_MARKER)
    );
    assert_eq!(
        fs::read_to_string(root.path().join("status"))
            .unwrap()
            .trim(),
        "7"
    );
}

#[test]
#[ignore = "root/descendant subprocess fixture for independent exit observation"]
#[allow(clippy::zombie_processes)]
fn process_exit_child_fixture() {
    let mode = std::env::args()
        .find_map(|argument| argument.strip_prefix("mode:").map(str::to_owned))
        .unwrap();
    let (role, address) = mode.split_once(':').unwrap();
    match role {
        "root" => {
            // The root must exit before its descendant: waiting here would erase
            // the exact inherited-output lifetime this regression exercises.
            Command::new(std::env::current_exe().unwrap())
                .args([
                    "--exact",
                    "process_exit_child_fixture",
                    "--ignored",
                    "--nocapture",
                    "--skip",
                    &format!("mode:descendant:{address}"),
                ])
                .stdin(Stdio::null())
                .stdout(Stdio::inherit())
                .stderr(Stdio::inherit())
                .spawn()
                .unwrap();
            println!("ROOT_IS_EXITING_SEVEN");
            std::io::stdout().flush().unwrap();
            std::process::exit(7);
        }
        "descendant" => {
            let address = address.parse().unwrap();
            let mut gate = TcpStream::connect_timeout(&address, Duration::from_secs(5)).unwrap();
            gate.set_read_timeout(Some(Duration::from_secs(15)))
                .unwrap();
            gate.write_all(b"R").unwrap();
            let _ = gate.read(&mut [0u8; 1]);
            std::io::stdout().write_all(DESCENDANT_MARKER).unwrap();
            std::io::stdout().flush().unwrap();
            std::process::exit(0);
        }
        _ => panic!("unknown fixture role {role}"),
    }
}
