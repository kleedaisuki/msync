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
        Self::start_with_options(directory, &[])
    }

    fn start_with_options(directory: &Path, options: &[&str]) -> Self {
        let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
            .args(["repl", "--color", "never", "--trace", "trace.jsonl"])
            .args(options)
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
    assert_eq!(
        fs::read(directory.path().join("extra.txt")).unwrap(),
        b"finite"
    );
    repl.send("/stop\n/jobs\n/quit\n");
    repl.until("job 1: stopped");
    repl.until("no active jobs");
    let (status, errors) = repl.finish();
    source.close();
    assert!(status.success(), "{errors}");
    assert!(errors.contains("use /wait or /stop"), "{errors}");
    assert!(
        errors.contains("shared bindings, working-directory or filesystem state"),
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
    let schedule = phase("schedule");
    let records = trace
        .lines()
        .map(|line| serde_json::from_str::<serde_json::Value>(line).unwrap())
        .collect::<Vec<_>>();
    let spans = records
        .iter()
        .filter(|record| record["kind"] == "span")
        .map(|record| (record["span_id"].as_str().unwrap(), record))
        .collect::<std::collections::BTreeMap<_, _>>();
    let mut current = spans[json_string(schedule, "span_id")];
    let mut ancestry = Vec::new();
    let mut visited = std::collections::BTreeSet::new();
    loop {
        let id = current["span_id"].as_str().unwrap();
        assert!(visited.insert(id), "cyclic trace ancestry: {trace}");
        assert_eq!(current["trace_id"], json_string(schedule, "trace_id"));
        ancestry.push(current);
        let Some(parent) = current["parent_span_id"].as_str() else {
            break;
        };
        current = spans
            .get(parent)
            .expect("every admission ancestor must exist");
    }
    let names = ancestry
        .iter()
        .map(|record| record["name"].as_str().unwrap())
        .collect::<Vec<_>>();
    assert!(
        names.starts_with(&[
            "schedule",
            "interpreter.instruction",
            "interpreter.block",
            "execute",
            "submission"
        ]),
        "the schedule must retain its actual instruction/block/execution/submission ancestry: {trace}"
    );
    assert_eq!(ancestry[1]["attributes"]["instruction.kind"], "transfer");
    assert!(
        ancestry[2]["metrics"]["instructions"]
            .as_u64()
            .is_some_and(|count| count > 0)
    );
    let submission = ancestry[4]["span_id"].as_str().unwrap();
    assert!(
        spans.values().any(|record| record["name"] == "compile"
            && record["parent_span_id"] == submission
            && record["metrics"]["operations"]
                .as_u64()
                .is_some_and(|count| count > 0)),
        "admission must belong to the nonempty compilation, not an earlier empty binding submission: {trace}"
    );
    let task = trace
        .lines()
        .find(|line| {
            line.contains("\"name\":\"task\"")
                && json_string(line, "parent_span_id") == json_string(schedule, "span_id")
        })
        .expect("the scheduled flow must retain its actual task child");
    assert_eq!(
        json_string(task, "parent_span_id"),
        json_string(schedule, "span_id")
    );
    assert_eq!(
        json_string(schedule, "parent_span_id"),
        ancestry[1]["span_id"].as_str().unwrap()
    );
    assert_eq!(ancestry[0]["metrics"]["granted_slots"], 1);
    assert_eq!(ancestry[0]["metrics"]["reserved_buffer_bytes"], 131072);
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
    assert!(repl.output.contains("job 1: closed"));
}

#[test]
fn runtime_assignment_failure_preserves_successfully_compiled_bindings() {
    let directory = workspace();
    fs::write(directory.path().join("input.txt"), "payload").unwrap();
    fs::create_dir(directory.path().join("out")).unwrap();
    fs::write(
        directory.path().join("failed.msync"),
        "retained := \"compiled binding\"\n$pwd::pwd/input.txt -> $pwd::pwd/out\n",
    )
    .unwrap();
    let mut repl = Repl::start(directory.path());
    repl.send("/load failed.msync\n/bindings\nretained -> recovered.txt\n/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success());
    assert!(errors.contains("existing directory"), "{errors}");
    assert!(repl.output.contains("retained"), "{}", repl.output);
    assert_eq!(
        fs::read_to_string(directory.path().join("recovered.txt")).unwrap(),
        "compiled binding"
    );
    assert_eq!(
        fs::read_to_string(directory.path().join("input.txt")).unwrap(),
        "payload"
    );
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

#[test]
fn natural_eof_publishes_while_the_editor_waits_without_another_input() {
    let directory = workspace();
    let mut source = HeldSource::start(b"published without another command");
    let mut repl = Repl::start(directory.path());
    launch(&mut repl, &source);
    assert!(!repl.output.contains("done:"));
    source.close();
    let target = directory.path().join("output.bin");
    let deadline = Instant::now() + DEADLINE;
    while fs::read(&target).ok().as_deref() != Some(b"published without another command") {
        assert!(
            Instant::now() < deadline,
            "EOF must publish without a keystroke"
        );
        thread::sleep(Duration::from_millis(2));
    }
    assert!(repl.child.try_wait().unwrap().is_none());
    repl.send("/jobs\n/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert!(repl.output.contains("job 1: completed"), "{}", repl.output);
    assert!(repl.output.contains("no active jobs"), "{}", repl.output);
    assert!(
        repl.output.contains("session progress: 1 assignment(s)"),
        "{}",
        repl.output
    );
}

#[test]
fn distinct_live_submissions_have_independent_job_ids_and_completion() {
    let directory = workspace();
    let mut slow = HeldSource::start(b"slow source");
    let mut fast = HeldSource::start(b"fast source");
    let mut repl = Repl::start(directory.path());
    launch(&mut repl, &slow);
    repl.send(&format!(
        "fast => tcp://{}\nfast::/ -> $pwd::pwd/fast.bin\n",
        fast.address
    ));
    fast.connected();
    repl.until("job 2: running in background");
    fast.close();
    let deadline = Instant::now() + DEADLINE;
    while fs::read(directory.path().join("fast.bin")).ok().as_deref() != Some(b"fast source") {
        assert!(
            Instant::now() < deadline,
            "ready second job must not wait for first job EOF"
        );
        thread::sleep(Duration::from_millis(2));
    }
    assert!(!directory.path().join("output.bin").exists());
    repl.send("/jobs\n/stop\n/quit\n");
    let (status, errors) = repl.finish();
    slow.close();
    assert!(status.success(), "{errors}");
    assert!(repl.output.contains("job 2: completed"), "{}", repl.output);
    assert!(repl.output.contains("job 1: stopped"), "{}", repl.output);
    assert!(!repl.output.contains("job 2: stopped"), "{}", repl.output);
}

#[test]
fn rejected_obsolete_source_does_not_disturb_an_active_job() {
    let directory = workspace();
    let mut source = HeldSource::start(b"owned by structured session");
    fs::write(
        directory.path().join("stream.msync"),
        format!(
            "# msync: 0.5\nwire => tcp://{}\nwire::/ -> output.bin\n",
            source.address
        ),
    )
    .unwrap();
    fs::write(
        directory.path().join("obsolete.msync"),
        "# msync: 0.3\ninput.txt -> copied.txt\n",
    )
    .unwrap();
    fs::write(directory.path().join("input.txt"), "must remain unassigned").unwrap();
    let mut repl = Repl::start(directory.path());
    repl.send("/load stream.msync\n");
    source.connected();
    repl.until("job 1: running in background");
    repl.send("/jobs\n/load obsolete.msync\n/jobs\n/stop\n/quit\n");
    let (status, errors) = repl.finish();
    source.close();
    assert!(status.success(), "{errors}");
    assert!(repl.output.contains("job 1: stopped"), "{}", repl.output);
    assert!(errors.contains("unsupported syntax version"), "{errors}");
    assert!(errors.contains("obsolete.msync:1:"), "{errors}");
    assert!(!directory.path().join("copied.txt").exists());
}

#[test]
fn wait_does_not_hold_a_ready_second_job_behind_an_idle_first_job() {
    let directory = workspace();
    let mut slow = HeldSource::start(b"still open");
    let mut fast = HeldSource::start(b"ready during wait");
    let mut repl = Repl::start(directory.path());
    launch(&mut repl, &slow);
    repl.send(&format!(
        "fast => tcp://{}\nfast::/ -> fast.bin\n",
        fast.address
    ));
    fast.connected();
    repl.until("job 2: running in background");
    repl.send("/wait\n");
    fast.close();
    let deadline = Instant::now() + DEADLINE;
    while fs::read(directory.path().join("fast.bin")).ok().as_deref() != Some(b"ready during wait")
    {
        assert!(
            Instant::now() < deadline,
            "global wait must service a ready later cohort"
        );
        thread::sleep(Duration::from_millis(2));
    }
    assert!(!directory.path().join("output.bin").exists());
    slow.close();
    // Independent jobs may report completion in either order. Waiting for the
    // first ID can already consume the other job's notice.
    for id in [1, 2] {
        let notice = format!("job {id}: completed");
        if !repl.output.contains(&notice) {
            repl.until(&notice);
        }
    }
    repl.send("/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
}

#[test]
fn waiting_without_work_does_not_invent_a_completed_job() {
    let directory = workspace();
    let mut repl = Repl::start(directory.path());
    repl.send("/wait\n/stop\n/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert!(repl.output.contains("no active jobs"));
    assert!(!repl.output.contains("completed"), "{}", repl.output);
    assert!(!repl.output.contains("stopped"), "{}", repl.output);
}

#[test]
fn source_load_reuses_bindings_without_a_second_compiler() {
    let directory = workspace();
    fs::write(directory.path().join("input.txt"), "shared path binding").unwrap();
    fs::write(
        directory.path().join("modern.msync"),
        "# msync: 0.5\nroot/input.txt -> copied.txt\n",
    )
    .unwrap();
    let mut repl = Repl::start(directory.path());
    repl.send("/syntax 0.3\n/syntax 0.5\nroot := $pwd::pwd\n/load modern.msync\n/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert!(errors.contains("unsupported syntax version"), "{errors}");
    assert!(repl.output.contains("syntax: 0.6"), "{}", repl.output);
    assert_eq!(
        fs::read(directory.path().join("copied.txt")).unwrap(),
        b"shared path binding"
    );
}

#[cfg(unix)]
#[test]
fn a_later_input_failure_keeps_its_source_inside_an_existing_program_job() {
    let directory = workspace();
    let unused = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = unused.local_addr().unwrap();
    drop(unused);
    fs::write(
        directory.path().join("launch.msync"),
        "async child => $path::cat()\n",
    )
    .unwrap();
    let transfer = "wire::/ -> child::stdin";
    fs::write(
        directory.path().join("feed.msync"),
        format!("\n\nwire => tcp://{address}\n{transfer}\n"),
    )
    .unwrap();
    let mut repl = Repl::start(directory.path());
    repl.send("/load launch.msync\n/load feed.msync\n/wait\n/help\n/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert!(errors.contains("feed.msync:4:"), "{errors}");
    assert!(errors.contains(transfer), "{errors}");
    assert!(!errors.contains("launch.msync:"), "{errors}");
    assert!(
        repl.output.contains("job 1: running in background"),
        "{}",
        repl.output
    );
    assert!(
        !repl.output.contains("job 2: running in background"),
        "{}",
        repl.output
    );
    assert!(repl.output.contains("REPL commands"));
}

#[cfg(unix)]
#[test]
fn check_and_plan_borrow_live_program_context_without_consuming_its_ports() {
    let directory = workspace();
    fs::write(
        directory.path().join("inspect.msync"),
        "transient := \"preview only\"\nchild::stdout -> preview.txt\n",
    )
    .unwrap();
    let mut repl = Repl::start(directory.path());
    repl.send("async child => $path::cat()\n/check inspect.msync\n/plan inspect.msync\n/bindings\n\"done\" -> child::stdin\nchild::stdout -> actual.txt\n/wait\n/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert!(errors.is_empty(), "{errors}");
    assert!(!repl.output.contains("transient :="), "{}", repl.output);
    assert!(repl.output.contains("Static IR preview"));
    assert!(!directory.path().join("preview.txt").exists());
    assert_eq!(
        fs::read(directory.path().join("actual.txt")).unwrap(),
        b"done"
    );
}

fn marked_program(delay_millis: u32) -> String {
    #[cfg(windows)]
    let command = {
        let windows = std::env::var_os("WINDIR").expect("Windows directory");
        let executable = Path::new(&windows).join("System32/WindowsPowerShell/v1.0/powershell.exe");
        let executable = serde_json::to_string(&executable.to_string_lossy()).unwrap();
        let script = serde_json::to_string(&format!(
            "[IO.File]::WriteAllText('started.txt','started'); Start-Sleep -Milliseconds {delay_millis}; [IO.File]::WriteAllText('finished.txt','finished')",
        ))
        .unwrap();
        format!(
            "async child => $local::{executable}(\"-NoProfile\", \"-NonInteractive\", \"-Command\", {script})\n"
        )
    };
    #[cfg(unix)]
    let command = format!(
        "async child => $local::/bin/sh(\"-c\", \"printf started > started.txt; sleep {}.{:03}; printf finished > finished.txt\")\n",
        delay_millis / 1_000,
        delay_millis % 1_000
    );
    command
}

fn wait_for_marker(directory: &Path, name: &str) {
    let deadline = Instant::now() + DEADLINE;
    while !directory.join(name).exists() {
        assert!(
            Instant::now() < deadline,
            "external program did not create {name}"
        );
        thread::sleep(Duration::from_millis(5));
    }
}

#[test]
fn stop_drains_a_running_external_program_until_its_own_exit() {
    let directory = workspace();
    let mut repl = Repl::start(directory.path());
    repl.send(&marked_program(500));
    wait_for_marker(directory.path(), "started.txt");
    assert!(!directory.path().join("finished.txt").exists());
    repl.send("/stop\n/jobs\n/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert_eq!(
        fs::read(directory.path().join("finished.txt")).unwrap(),
        b"finished"
    );
    assert!(repl.output.contains("no active jobs"), "{}", repl.output);
    assert!(!errors.contains("Sandboxie"), "{errors}");
    assert!(!errors.contains("bubblewrap"), "{errors}");
}

#[test]
fn a_completed_default_program_retains_ports_for_later_repl_submissions() {
    let directory = workspace();
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    let mut repl = Repl::start(directory.path());
    repl.send(&format!("Program => $local::{executable}(\"--version\")\n"));
    repl.until("done:");
    assert!(!directory.path().join("version.txt").exists());
    repl.send(
        "Program::stdout -> version.txt\nProgram::exit_code -> exit.txt\nProgram::meta -> meta.json\n/jobs\n/quit\n",
    );
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert!(errors.is_empty(), "{errors}");
    assert_eq!(
        fs::read_to_string(directory.path().join("version.txt")).unwrap(),
        format!("msync {}\n", env!("CARGO_PKG_VERSION"))
    );
    assert_eq!(fs::read(directory.path().join("exit.txt")).unwrap(), b"0\n");
    let meta: serde_json::Value =
        serde_json::from_slice(&fs::read(directory.path().join("meta.json")).unwrap()).unwrap();
    assert_eq!(meta["exit_code"], 0);
}

#[test]
fn explicit_kill_is_destructive_and_does_not_masquerade_as_graceful_stop() {
    let directory = workspace();
    let mut repl = Repl::start(directory.path());
    repl.send(&marked_program(20_000));
    wait_for_marker(directory.path(), "started.txt");
    repl.send("/kill\n/jobs\n/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert!(errors.is_empty(), "{errors}");
    assert!(directory.path().join("started.txt").exists());
    assert!(!directory.path().join("finished.txt").exists());
    assert!(
        repl.output.contains("effects may be partial"),
        "{}",
        repl.output
    );
    assert!(repl.output.contains("no active jobs"), "{}", repl.output);
}

#[test]
fn a_partial_submission_retains_the_started_program_job_and_its_bindings() {
    let directory = workspace();
    let mut source = HeldSource::start(b"earlier program completed");
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    fs::write(
        directory.path().join("child.msync"),
        format!(
            "wire => tcp://{}\nwire::/ -> child-completed.txt\n",
            source.address
        ),
    )
    .unwrap();
    fs::write(
        directory.path().join("admission.msync"),
        format!(
            "async child => $local::{executable}(\"run\", \"child.msync\", \"--color\", \"never\")\nasync blocked => $local::{executable}(\"--version\")\n"
        ),
    )
    .unwrap();
    let mut repl = Repl::start_with_options(directory.path(), &["--jobs", "1"]);
    repl.send("/load admission.msync\n/jobs\n");
    source.connected();
    repl.until("job 1: running");
    assert!(!directory.path().join("child-completed.txt").exists());
    source.close();
    repl.send("/stop\nchild::exit_code -> code\n/wait\n/jobs\n/quit\n");
    let (status, errors) = repl.finish();
    assert!(status.success(), "{errors}");
    assert!(
        errors.contains("limit") || errors.contains("capacity"),
        "{errors}"
    );
    assert!(!errors.contains("unknown program"), "{errors}");
    assert_eq!(
        fs::read(directory.path().join("child-completed.txt")).unwrap(),
        b"earlier program completed"
    );
    assert_eq!(fs::read(directory.path().join("code")).unwrap(), b"0\n");
    assert!(
        repl.output.contains("retained after partial submission"),
        "{}",
        repl.output
    );
    assert!(repl.output.contains("no active jobs"), "{}", repl.output);
}
