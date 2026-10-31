//! Latency-sensitive journeys validate publication and notices without another command.

#[path = "support/pty.rs"]
mod pty;

use std::fs;
use std::io::{BufRead, BufReader, Write};
use std::net::{Shutdown, TcpListener, TcpStream};
use std::path::Path;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::mpsc::{self, Receiver};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

const DEADLINE: Duration = Duration::from_secs(8);

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("latency-v6-")
        .tempdir_in(root)
        .unwrap()
}

fn command(root: &Path) -> Command {
    let mut command = Command::new(env!("CARGO_BIN_EXE_msync"));
    command
        .current_dir(root)
        .env("HOME", root)
        .env("USERPROFILE", root)
        .env("XDG_CONFIG_HOME", root.join("config"))
        .env("APPDATA", root.join("config"))
        .env_remove("MSYNC_CONFIG")
        .env_remove("MSYNC_TRACE")
        .env_remove("MSYNC_STATE_DIR")
        .env("NO_COLOR", "1");
    command
}

#[test]
fn pure_declarations_do_not_initialize_runtime_storage() {
    let root = workspace();
    fs::write(root.path().join(".temp"), b"not a directory").unwrap();
    fs::write(root.path().join("fallback"), b"also not a directory").unwrap();
    fs::write(
        root.path().join("pure.msync"),
        "ready = true\nlabel = \"pure\"\n",
    )
    .unwrap();
    let output = command(root.path())
        .args(["run", "pure.msync"])
        .env("MSYNC_STATE_DIR", root.path().join("fallback"))
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        fs::read(root.path().join(".temp")).unwrap(),
        b"not a directory"
    );
    assert_eq!(
        fs::read(root.path().join("fallback")).unwrap(),
        b"also not a directory"
    );
    assert!(!root.path().join(".msync").exists());
}

#[test]
fn pure_repl_bindings_remain_available_without_runtime_activation() {
    let root = workspace();
    fs::write(root.path().join(".temp"), b"not a directory").unwrap();
    let mut child = command(root.path())
        .arg("repl")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(b"ready = true\n/bindings\n/reset\n/quit\n")
        .unwrap();
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(String::from_utf8_lossy(&output.stdout).contains("ready = true"));
    assert!(
        output.stderr.is_empty(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
}

struct Repl {
    child: Child,
    input: Option<ChildStdin>,
    lines: Receiver<String>,
    diagnostics: Receiver<String>,
    errors: Option<JoinHandle<Vec<u8>>>,
}

impl Repl {
    fn start(root: &Path) -> Self {
        let mut child = command(root)
            .args(["repl", "--color", "never"])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let input = child.stdin.take();
        let output = child.stdout.take().unwrap();
        let errors = child.stderr.take().unwrap();
        let (sender, lines) = mpsc::channel();
        thread::spawn(move || {
            for line in BufReader::new(output).lines() {
                let Ok(line) = line else { break };
                if sender.send(line).is_err() {
                    break;
                }
            }
        });
        let (diagnostic_sender, diagnostics) = mpsc::channel();
        let errors = Some(thread::spawn(move || {
            let mut bytes = Vec::new();
            for line in BufReader::new(errors).lines() {
                let line = line.unwrap();
                bytes.extend_from_slice(line.as_bytes());
                bytes.push(b'\n');
                let _ = diagnostic_sender.send(line);
            }
            bytes
        }));
        Self {
            child,
            input,
            lines,
            diagnostics,
            errors,
        }
    }

    fn send(&mut self, text: &str) {
        let input = self.input.as_mut().unwrap();
        input.write_all(text.as_bytes()).unwrap();
        input.flush().unwrap();
    }

    fn until(&self, expected: &str) {
        let deadline = Instant::now() + DEADLINE;
        let mut observed = String::new();
        loop {
            let line = self
                .lines
                .recv_timeout(deadline.saturating_duration_since(Instant::now()))
                .unwrap_or_else(|error| {
                    panic!("waiting for {expected}: {error}; output={observed}")
                });
            observed.push_str(&line);
            observed.push('\n');
            if line.contains(expected) {
                return;
            }
        }
    }

    fn finish(&mut self) {
        let errors = self.finish_with_diagnostics();
        assert!(errors.is_empty(), "{}", String::from_utf8_lossy(&errors));
    }

    fn finish_with_diagnostics(&mut self) -> Vec<u8> {
        self.send("/quit\n");
        self.input.take();
        let deadline = Instant::now() + DEADLINE;
        while self.child.try_wait().unwrap().is_none() {
            assert!(Instant::now() < deadline, "REPL did not exit");
            thread::sleep(Duration::from_millis(1));
        }
        self.errors.take().unwrap().join().unwrap()
    }
}

impl Drop for Repl {
    fn drop(&mut self) {
        self.input.take();
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(errors) = self.errors.take() {
            let _ = errors.join();
        }
    }
}

fn connected(listener: &TcpListener) -> TcpStream {
    listener.set_nonblocking(true).unwrap();
    let deadline = Instant::now() + DEADLINE;
    loop {
        match listener.accept() {
            Ok((stream, _)) => return stream,
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {}
            Err(error) => panic!("accept: {error}"),
        }
        assert!(Instant::now() < deadline, "runtime did not open TCP source");
        thread::sleep(Duration::from_millis(1));
    }
}

#[test]
fn background_eof_reports_completion_without_another_input_line() {
    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let mut repl = Repl::start(root.path());
    repl.send(&format!(
        "wire => tcp://{}\nrelease wire::/ -> output\n",
        listener.local_addr().unwrap()
    ));
    repl.until("running in background");
    let mut source = connected(&listener);
    source.write_all(b"actual async payload").unwrap();
    source.shutdown(Shutdown::Write).unwrap();
    repl.until("job 1: completed");
    assert_eq!(
        fs::read(root.path().join("output")).unwrap(),
        b"actual async payload"
    );
    assert!(repl.child.try_wait().unwrap().is_none());
    repl.send("/jobs\n");
    repl.until("no active jobs");
    repl.finish();
}

#[cfg(unix)]
#[test]
fn background_process_completion_is_reported_without_another_input_line() {
    let root = workspace();
    let mut repl = Repl::start(root.path());
    repl.send("release child => $path::sh(\"-c\", \"while [ ! -e go ]; do sleep 0.001; done; printf done\")\n");
    repl.until("running in background");
    fs::write(root.path().join("go"), b"finish").unwrap();
    repl.until("job 1: completed");
    repl.send("child::stdout -> output\n");
    repl.until("job 2: completed");
    assert_eq!(fs::read(root.path().join("output")).unwrap(), b"done");
    repl.send("/jobs\n");
    repl.until("no active jobs");
    repl.finish();
}

#[test]
fn idle_completion_preserves_the_current_unicode_editor_line() {
    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let executable = Path::new(env!("CARGO_BIN_EXE_msync"));
    let mut repl = pty::Terminal::spawn(executable, root.path(), "never");
    repl.prompt();
    repl.submit(&format!("wire => tcp://{}", listener.local_addr().unwrap()));
    repl.submit("release wire::/ -> output");
    let mut source = connected(&listener);
    repl.mark();
    repl.send("typed = \"可莉\"");
    repl.until("typed = \"可莉\"");
    source.write_all(b"prompt-safe completion").unwrap();
    source.shutdown(Shutdown::Write).unwrap();
    let redraw = repl.until_followed_by("job 1: completed", "typed = \"可莉\"");
    assert!(pty::visible(&redraw).contains("job 1: completed"));
    assert_eq!(pty::color_of(&redraw, "job 1: completed"), None);
    repl.accept();
    let bindings = repl.submit("/bindings");
    assert!(
        pty::visible(&bindings).contains("typed := \"可莉\""),
        "{bindings:?}"
    );
    repl.submit("typed -> typed.txt");
    assert_eq!(
        fs::read(root.path().join("typed.txt")).unwrap(),
        "可莉".as_bytes()
    );
    assert_eq!(
        fs::read(root.path().join("output")).unwrap(),
        b"prompt-safe completion"
    );
    repl.exit();
}

#[test]
fn a_background_flow_failure_does_not_wait_for_an_unrelated_held_child() {
    let root = workspace();
    let held = TcpListener::bind("127.0.0.1:0").unwrap();
    fs::write(
        root.path().join("held.msync"),
        format!(
            "wire => tcp://{}\nwire::/ -> held-output\n",
            held.local_addr().unwrap()
        ),
    )
    .unwrap();
    let mut repl = Repl::start(root.path());
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    repl.send(&format!(
        "release child => $local::{executable}(\"run\", \"held.msync\")\n"
    ));
    repl.until("job 1: running in background");
    let mut held_source = connected(&held);
    held_source.write_all(b"independent child output").unwrap();

    fs::create_dir(root.path().join("parent")).unwrap();
    let broken = TcpListener::bind("127.0.0.1:0").unwrap();
    repl.send(&format!(
        "broken => tcp://{}\nrelease broken::/ -> parent/output\n",
        broken.local_addr().unwrap()
    ));
    repl.until("job 2: running in background");
    let mut broken_source = connected(&broken);
    broken_source.write_all(b"cannot publish").unwrap();
    fs::rename(root.path().join("parent"), root.path().join("old-parent")).unwrap();
    fs::write(root.path().join("parent"), b"not a directory").unwrap();
    broken_source.shutdown(Shutdown::Write).unwrap();

    let deadline = Instant::now() + DEADLINE;
    loop {
        let line = repl
            .diagnostics
            .recv_timeout(deadline.saturating_duration_since(Instant::now()))
            .expect("background failure must be reported before the held child exits");
        if line.contains("error:") {
            break;
        }
    }
    repl.send("/jobs\n");
    repl.until("job 1: running");
    assert!(
        !root.path().join("held-output").exists(),
        "the independent child must still await its own EOF"
    );
    held_source.shutdown(Shutdown::Write).unwrap();
    repl.until("job 1: completed");
    assert_eq!(
        fs::read(root.path().join("held-output")).unwrap(),
        b"independent child output"
    );
    let diagnostics = repl.finish_with_diagnostics();
    assert!(String::from_utf8_lossy(&diagnostics).contains("error:"));
}
