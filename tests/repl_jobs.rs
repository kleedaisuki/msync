use std::fs;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpListener;
use std::path::Path;
use std::process::{Child, ChildStdin, Command, ExitStatus, Stdio};
use std::sync::mpsc::{self, Receiver, Sender};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

const DEADLINE: Duration = Duration::from_secs(8);

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("repl-jobs-")
        .tempdir_in(root)
        .unwrap()
}

struct Repl {
    child: Child,
    input: Option<ChildStdin>,
    lines: Receiver<String>,
    output: String,
    output_reader: Option<JoinHandle<()>>,
    error_reader: Option<JoinHandle<String>>,
}

impl Repl {
    fn start(directory: &Path) -> Self {
        let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
            .args(["repl", "--color", "never", "--trace", "trace.jsonl"])
            .current_dir(directory)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let input = child.stdin.take();
        let output = child.stdout.take().unwrap();
        let mut errors = child.stderr.take().unwrap();
        let (sender, lines) = mpsc::channel();
        let output_reader = thread::spawn(move || {
            for line in BufReader::new(output).lines() {
                let Ok(line) = line else {
                    break;
                };
                if sender.send(line).is_err() {
                    break;
                }
            }
        });
        let error_reader = thread::spawn(move || {
            let mut text = String::new();
            errors.read_to_string(&mut text).unwrap();
            text
        });
        Self {
            child,
            input,
            lines,
            output: String::new(),
            output_reader: Some(output_reader),
            error_reader: Some(error_reader),
        }
    }

    fn send(&mut self, input: &str) {
        let stdin = self.input.as_mut().unwrap();
        stdin.write_all(input.as_bytes()).unwrap();
        stdin.flush().unwrap();
    }

    fn until(&mut self, expected: &str) {
        let deadline = Instant::now() + DEADLINE;
        loop {
            let line = self
                .lines
                .recv_timeout(deadline.saturating_duration_since(Instant::now()))
                .unwrap_or_else(|error| {
                    panic!("waiting for {expected:?}: {error}; output={}", self.output)
                });
            self.output.push_str(&line);
            self.output.push('\n');
            if line.contains(expected) {
                return;
            }
        }
    }

    fn finish(&mut self) -> (ExitStatus, String) {
        self.input.take();
        let deadline = Instant::now() + DEADLINE;
        let status = loop {
            if let Some(status) = self.child.try_wait().unwrap() {
                break status;
            }
            assert!(
                Instant::now() < deadline,
                "REPL did not exit; output={}",
                self.output
            );
            thread::sleep(Duration::from_millis(10));
        };
        if let Some(reader) = self.output_reader.take() {
            reader.join().unwrap();
        }
        for line in self.lines.try_iter() {
            self.output.push_str(&line);
            self.output.push('\n');
        }
        let errors = self.error_reader.take().unwrap().join().unwrap();
        (status, errors)
    }
}

impl Drop for Repl {
    fn drop(&mut self) {
        self.input.take();
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(reader) = self.output_reader.take() {
            let _ = reader.join();
        }
        if let Some(reader) = self.error_reader.take() {
            let _ = reader.join();
        }
    }
}

struct HeldSource {
    address: String,
    accepted: Receiver<()>,
    release: Option<Sender<()>>,
    thread: Option<JoinHandle<()>>,
}

impl HeldSource {
    fn start(payload: &'static [u8]) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        listener.set_nonblocking(true).unwrap();
        let address = listener.local_addr().unwrap().to_string();
        let (accepted_sender, accepted) = mpsc::channel();
        let (release, receiver) = mpsc::channel();
        let thread = thread::spawn(move || {
            let deadline = Instant::now() + DEADLINE;
            let mut socket = loop {
                match listener.accept() {
                    Ok((socket, _)) => break socket,
                    Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                        if Instant::now() >= deadline {
                            return;
                        }
                        thread::sleep(Duration::from_millis(5));
                    }
                    Err(error) => panic!("accept: {error}"),
                }
            };
            socket.set_nonblocking(false).unwrap();
            socket.write_all(payload).unwrap();
            let _ = accepted_sender.send(());
            let _ = receiver.recv_timeout(DEADLINE);
        });
        Self {
            address,
            accepted,
            release: Some(release),
            thread: Some(thread),
        }
    }

    fn connected(&self) {
        self.accepted.recv_timeout(DEADLINE).unwrap();
    }

    fn close(&mut self) {
        if let Some(release) = self.release.take() {
            let _ = release.send(());
        }
        if let Some(thread) = self.thread.take() {
            thread.join().unwrap();
        }
    }
}

impl Drop for HeldSource {
    fn drop(&mut self) {
        self.close();
    }
}

fn launch(repl: &mut Repl, source: &HeldSource) {
    repl.send(&format!(
        "wire => tcp://{}\nwire::/ -> $pwd::pwd/output.bin\n",
        source.address
    ));
    source.connected();
    repl.until("job 1: running in background");
}

fn json_string<'a>(line: &'a str, field: &str) -> &'a str {
    line.split_once(&format!("\"{field}\":\""))
        .unwrap()
        .1
        .split('"')
        .next()
        .unwrap()
}

#[test]
fn live_stream_keeps_repl_responsive_and_stop_joins_resources() {
    let directory = workspace();
    fs::write(directory.path().join("input.txt"), "finite").unwrap();
    fs::write(
        directory.path().join("copy.msync"),
        "input.txt -> extra.txt\n",
    )
    .unwrap();
    let mut source = HeldSource::start(b"stream stays open");
    let mut repl = Repl::start(directory.path());
    launch(&mut repl, &source);
    repl.send("/help\n/jobs\n/cd .\n/fmt copy.msync\n/load copy.msync\ninput.txt -> extra.txt\nroot := $pwd::pwd/configs\n/bindings\n/limits\n");
    repl.until("REPL commands");
    repl.until("job 1: running");
    repl.until("root :=");
    repl.until("memory-limit=");
    assert!(!directory.path().join("extra.txt").exists());
    repl.send("/stop\n/jobs\n/quit\n");
    repl.until("job 1: stopped");
    repl.until("no active jobs");
    let (status, errors) = repl.finish();
    source.close();
    assert!(status.success(), "{errors}");
    assert!(errors.contains("use /wait or /stop"), "{errors}");
    assert!(
        errors.contains("shared working-directory or filesystem state"),
        "{errors}"
    );
    assert!(!repl.output.contains('\u{1b}'));
    let trace = fs::read_to_string(directory.path().join("trace.jsonl")).unwrap();
    for phase in ["execute", "schedule", "task"] {
        assert!(
            trace.contains(&format!("\"name\":\"{phase}\"")),
            "missing {phase}: {trace}"
        );
    }
    let phase = |name: &str| {
        trace
            .lines()
            .find(|line| line.contains(&format!("\"name\":\"{name}\"")))
            .unwrap()
    };
    let execute = phase("execute");
    let schedule = phase("schedule");
    let task = phase("task");
    assert_eq!(
        json_string(schedule, "parent_span_id"),
        json_string(execute, "span_id")
    );
    assert_eq!(
        json_string(task, "parent_span_id"),
        json_string(schedule, "span_id")
    );
    assert!(
        trace
            .lines()
            .any(|line| line.contains("\"name\":\"submission\"")
                && json_string(line, "span_id") == json_string(execute, "parent_span_id"))
    );
}

#[test]
fn wait_observes_stream_eof_then_resumes_commands() {
    let directory = workspace();
    let mut source = HeldSource::start(b"complete payload");
    let mut repl = Repl::start(directory.path());
    launch(&mut repl, &source);
    repl.send("/wait\n/help\n");
    assert!(
        repl.lines.recv_timeout(Duration::from_millis(150)).is_err(),
        "/wait must wait for EOF"
    );
    source.close();
    repl.until("job 1: completed");
    repl.until("REPL commands");
    repl.send("/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert_eq!(
        fs::read(directory.path().join("output.bin")).unwrap(),
        b"complete payload"
    );
}

#[test]
fn piped_eof_waits_for_background_stream_without_cancelling_it() {
    let directory = workspace();
    let mut source = HeldSource::start(b"payload before EOF");
    let mut repl = Repl::start(directory.path());
    launch(&mut repl, &source);
    repl.input.take();
    thread::sleep(Duration::from_millis(150));
    assert!(repl.child.try_wait().unwrap().is_none());
    source.close();
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert!(repl.output.contains("job 1: completed"));
    assert_eq!(
        fs::read(directory.path().join("output.bin")).unwrap(),
        b"payload before EOF"
    );
}

#[test]
fn quit_cancels_open_background_stream_instead_of_waiting_for_eof() {
    let directory = workspace();
    let mut source = HeldSource::start(b"held open");
    let mut repl = Repl::start(directory.path());
    launch(&mut repl, &source);
    repl.send("/quit\n");
    let (status, errors) = repl.finish();
    source.close();
    assert!(status.success(), "{errors}");
    assert!(repl.output.contains("job 1: stopped"));
}

#[test]
fn failed_plan_preparation_does_not_commit_bindings() {
    let directory = workspace();
    fs::write(directory.path().join("input.txt"), "payload").unwrap();
    fs::create_dir(directory.path().join("out")).unwrap();
    fs::write(
        directory.path().join("failed.msync"),
        "leaked := $pwd::pwd/configs\n$pwd::pwd/input.txt -> $pwd::pwd/out\n",
    )
    .unwrap();
    let mut repl = Repl::start(directory.path());
    repl.send("/load failed.msync\n/bindings\n/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success());
    assert!(errors.contains("existing directory"), "{errors}");
    assert!(!repl.output.contains("leaked"), "{}", repl.output);
    assert_eq!(
        fs::read_dir(directory.path().join("out")).unwrap().count(),
        0
    );
}

#[test]
fn background_errors_keep_owned_source_and_repl_recovers() {
    let directory = workspace();
    let unused = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = unused.local_addr().unwrap();
    drop(unused);
    let mut repl = Repl::start(directory.path());
    repl.send(&format!(
        "wire => tcp://{address}\nwire::/ -> $pwd::pwd/output.bin\n/wait\n/help\n/quit\n"
    ));
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert!(
        errors.contains("error:") && errors.contains("<repl>:"),
        "{errors}"
    );
    assert!(
        errors.contains("wire::/ -> $pwd::pwd/output.bin"),
        "{errors}"
    );
    assert!(repl.output.contains("job 1: running in background"));
    assert!(repl.output.contains("REPL commands"));
}
