//! Actual live-runtime admission and async poll telemetry, exercised via the CLI.
use std::{
    fs,
    io::Write,
    net::{TcpListener, TcpStream},
    path::Path,
    process::{Child, Command, Output, Stdio},
    thread,
    time::{Duration, Instant},
};

fn accept(listener: &TcpListener) -> TcpStream {
    listener.set_nonblocking(true).unwrap();
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        match listener.accept() {
            Ok((peer, _)) => return peer,
            Err(cause)
                if cause.kind() == std::io::ErrorKind::WouldBlock && Instant::now() < deadline =>
            {
                thread::sleep(Duration::from_millis(5))
            }
            Err(cause) => panic!("physical flow did not connect: {cause}"),
        }
    }
}

fn finish(mut child: Child) -> Output {
    let deadline = Instant::now() + Duration::from_secs(15);
    while child.try_wait().unwrap().is_none() {
        if Instant::now() >= deadline {
            child.kill().unwrap();
            let output = child.wait_with_output().unwrap();
            panic!(
                "runtime task failed to terminate: {}",
                String::from_utf8_lossy(&output.stderr)
            );
        }
        thread::sleep(Duration::from_millis(5));
    }
    child.wait_with_output().unwrap()
}

fn workspace() -> tempfile::TempDir {
    let path = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&path).unwrap();
    tempfile::Builder::new()
        .prefix("runtime-task-trace-")
        .tempdir_in(path)
        .unwrap()
}
fn field<'a>(record: &'a str, key: &str) -> &'a str {
    record
        .split_once(&format!("\"{key}\":\""))
        .unwrap()
        .1
        .split('"')
        .next()
        .unwrap()
}
fn span<'a>(trace: &'a str, name: &str) -> &'a str {
    trace
        .lines()
        .find(|record| {
            record.contains("\"kind\":\"span\"") && record.contains(&format!("\"name\":\"{name}\""))
        })
        .unwrap_or_else(|| panic!("missing {name}: {trace}"))
}
fn hierarchy(trace: &str) {
    let schedule = span(trace, "schedule");
    let task = span(trace, "task");
    assert_eq!(
        field(schedule, "parent_span_id"),
        field(span(trace, "execute"), "span_id")
    );
    assert_eq!(field(task, "parent_span_id"), field(schedule, "span_id"));
    assert!(schedule.contains("\"granted_slots\":1"));
    assert!(schedule.contains("\"reserved_buffer_bytes\":131072"));
    let flow = span(trace, "io.flow");
    assert_eq!(field(flow, "parent_span_id"), field(task, "span_id"));
}
#[test]
fn successful_physical_transfer_retains_task_context_across_polling() {
    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let sender = thread::spawn(move || {
        let mut socket = accept(&listener);
        socket.write_all(b"PRIVATE-PAYLOAD-DO-NOT-TRACE").unwrap();
    });
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .args(["run", "-", "--trace", "trace.jsonl", "--color", "never"])
        .current_dir(root.path())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(format!("wire => tcp://{address}\nwire::/ -> output\n").as_bytes())
        .unwrap();
    let output = finish(child);
    sender.join().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        fs::read(root.path().join("output")).unwrap(),
        b"PRIVATE-PAYLOAD-DO-NOT-TRACE"
    );
    let trace = fs::read_to_string(root.path().join("trace.jsonl")).unwrap();
    hierarchy(&trace);
    assert_eq!(field(span(&trace, "task"), "status"), "ok");
    assert!(!trace.contains("PRIVATE-PAYLOAD-DO-NOT-TRACE"));
}
#[test]
fn stopping_an_idle_flow_reports_task_cancellation_instead_of_success() {
    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .args(["repl", "--trace", "trace.jsonl", "--color", "never"])
        .current_dir(root.path())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let mut input = child.stdin.take().unwrap();
    input
        .write_all(format!("wire => tcp://{address}\nwire::/ -> output\n").as_bytes())
        .unwrap();
    input.flush().unwrap();
    let _peer = accept(&listener);
    input.write_all(b"/stop\n/quit\n").unwrap();
    drop(input);
    let output = finish(child);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(!root.path().join("output").exists());
    let trace = fs::read_to_string(root.path().join("trace.jsonl")).unwrap();
    hierarchy(&trace);
    assert_eq!(field(span(&trace, "task"), "status"), "cancelled");
}

#[test]
fn failed_native_connection_reports_task_failure_and_typed_io_cause() {
    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    drop(listener);
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .args(["run", "-", "--trace", "trace.jsonl", "--color", "never"])
        .current_dir(root.path())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(format!("wire => tcp://{address}\nwire::/ -> output\n").as_bytes())
        .unwrap();
    let output = finish(child);
    assert!(!output.status.success());
    assert!(!root.path().join("output").exists());
    let trace = fs::read_to_string(root.path().join("trace.jsonl")).unwrap();
    hierarchy(&trace);
    assert_eq!(field(span(&trace, "task"), "status"), "error");
    assert!(trace.contains("ConnectionRefused"), "{trace}");
    assert!(trace.contains("\"error.raw_os_error\""), "{trace}");
}
