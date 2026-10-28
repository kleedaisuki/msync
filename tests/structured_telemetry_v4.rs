//! Default structured-pipeline telemetry, not a legacy-syntax substitute.

use std::collections::{BTreeMap, BTreeSet};
use std::fs;
use std::io::{Read, Write};
use std::path::Path;
use std::process::{Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("structured-trace-")
        .tempdir_in(root)
        .unwrap()
}

fn invoke(root: &Path, mode: &str, source: &str) -> Output {
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .args([mode, "-", "--color", "never", "--trace", "trace.jsonl"])
        .current_dir(root)
        .env(
            "MSYNC_TEST_TELEMETRY_SECRET",
            "unrelated secret must not be captured",
        )
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let mut stdout = child.stdout.take().unwrap();
    let mut stderr = child.stderr.take().unwrap();
    let out = thread::spawn(move || {
        let mut bytes = Vec::new();
        stdout.read_to_end(&mut bytes).unwrap();
        bytes
    });
    let err = thread::spawn(move || {
        let mut bytes = Vec::new();
        stderr.read_to_end(&mut bytes).unwrap();
        bytes
    });
    child
        .stdin
        .take()
        .unwrap()
        .write_all(source.as_bytes())
        .unwrap();
    let deadline = Instant::now() + Duration::from_secs(20);
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            panic!("structured CLI did not finish within 20 seconds");
        }
        thread::sleep(Duration::from_millis(5));
    };
    Output {
        status,
        stdout: out.join().unwrap(),
        stderr: err.join().unwrap(),
    }
}

fn field<'a>(record: &'a str, key: &str) -> &'a str {
    let prefix = format!("\"{key}\":\"");
    record
        .split_once(&prefix)
        .unwrap_or_else(|| panic!("missing {key}: {record}"))
        .1
        .split('"')
        .next()
        .unwrap()
}

fn number(record: &str, key: &str) -> usize {
    let prefix = format!("\"{key}\":");
    record
        .split_once(&prefix)
        .unwrap_or_else(|| panic!("missing {key}: {record}"))
        .1
        .split([',', '}'])
        .next()
        .unwrap()
        .parse()
        .unwrap()
}

struct Trace {
    text: String,
}

impl Trace {
    fn load(root: &Path) -> Self {
        Self {
            text: fs::read_to_string(root.join("trace.jsonl")).unwrap(),
        }
    }

    fn span(&self, name: &str) -> &str {
        self.text
            .lines()
            .find(|line| {
                field(line, "kind") == "span"
                    && field(line, "name") == name
                    && (name != "copy" || line.contains("\"copy.kind\":\"ordinary-assignment\""))
            })
            .unwrap_or_else(|| panic!("missing actual {name} span: {}", self.text))
    }

    fn status(&self, name: &str, expected: &str) {
        assert_eq!(field(self.span(name), "status"), expected, "{}", self.text);
    }

    fn absent(&self, name: &str) {
        assert!(
            !self.text.lines().any(|line| field(line, "name") == name),
            "unexpected {name} phase: {}",
            self.text
        );
    }

    fn statement_journal(&self) {
        self.absent("prepare");
        self.absent("transaction.publish");
        assert_eq!(
            field(self.span("copy"), "publication.policy"),
            "statement-journal"
        );
    }

    fn rooted(&self) {
        let records: Vec<_> = self.text.lines().collect();
        assert!(!records.is_empty());
        let trace_id = field(records[0], "trace_id");
        let spans: BTreeMap<_, _> = records
            .iter()
            .copied()
            .filter(|line| field(line, "kind") == "span")
            .map(|line| (field(line, "span_id"), line))
            .collect();
        assert_eq!(
            spans.len(),
            records
                .iter()
                .filter(|line| field(line, "kind") == "span")
                .count(),
            "span IDs must be unique"
        );
        assert_eq!(
            spans
                .values()
                .filter(|line| line.contains("\"parent_span_id\":null"))
                .count(),
            1,
            "{}",
            self.text
        );
        assert!(self.span("session").contains("\"parent_span_id\":null"));
        for record in records {
            assert!(record.starts_with('{') && record.ends_with('}'));
            assert_eq!(field(record, "trace_id"), trace_id);
            assert!(
                spans.contains_key(field(record, "span_id")),
                "event owner must exist: {record}"
            );
            if field(record, "kind") == "span" && !record.contains("\"parent_span_id\":null") {
                assert!(
                    spans.contains_key(field(record, "parent_span_id")),
                    "span/event parent must exist: {record}"
                );
            }
        }
        for id in spans.keys() {
            let mut seen = BTreeSet::new();
            let mut current = *id;
            loop {
                assert!(seen.insert(current), "cyclic span ancestry");
                let record = spans[current];
                if record.contains("\"parent_span_id\":null") {
                    break;
                }
                current = field(record, "parent_span_id");
            }
        }
    }

    fn descendant(&self, child: &str, ancestor: &str) {
        let target = field(self.span(ancestor), "span_id");
        let spans: BTreeMap<_, _> = self
            .text
            .lines()
            .filter(|line| field(line, "kind") == "span")
            .map(|line| (field(line, "span_id"), line))
            .collect();
        let mut node = self.span(child);
        while !node.contains("\"parent_span_id\":null") {
            let parent = field(node, "parent_span_id");
            if parent == target {
                return;
            }
            node = spans[parent];
        }
        panic!("{child} is not owned by {ancestor}: {}", self.text);
    }

    fn no_payload(&self, payload: &str) {
        assert!(
            !self.text.contains(payload),
            "file/source payload must not become telemetry: {}",
            self.text
        );
        assert!(!self.text.contains("unrelated secret must not be captured"));
    }
}

#[test]
fn structured_macro_literal_trace_has_real_phase_ownership_without_logging_source_or_payload() {
    let root = workspace();
    let payload = "trace-private-literal-payload-可莉";
    let source =
        format!("macro publish(value):\n    value -> target.txt\npublish(\"{payload}\")\n");
    let output = invoke(root.path(), "run", &source);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        fs::read_to_string(root.path().join("target.txt")).unwrap(),
        payload
    );
    let trace = Trace::load(root.path());
    trace.rooted();
    for phase in ["parse", "compile", "execute"] {
        trace.status(phase, "ok");
        trace.descendant(phase, "submission");
    }
    trace.descendant("submission", "session");
    trace.descendant("copy", "execute");
    trace.statement_journal();
    for phase in ["ordinary.acquire", "ordinary.stage", "ordinary.publish"] {
        trace.status(phase, "ok");
        trace.descendant(phase, "copy");
    }
    assert_eq!(number(trace.span("copy"), "files.assigned"), 1);
    assert_eq!(number(trace.span("copy"), "bytes.assigned"), payload.len());
    trace.no_payload("trace-private-literal-payload-");
}

#[test]
fn structured_frontend_failure_records_raw_diagnostic_offsets_and_error_ancestors() {
    let root = workspace();
    let source = "unknown::source -> target\n";
    let output = invoke(root.path(), "check", source);
    assert!(!output.status.success());
    let trace = Trace::load(root.path());
    trace.rooted();
    trace.status("parse", "ok");
    for phase in ["compile", "submission", "session"] {
        trace.status(phase, "error");
    }
    let diagnostic = trace
        .text
        .lines()
        .find(|line| field(line, "kind") == "event" && field(line, "name") == "diagnostic")
        .unwrap();
    assert_eq!(field(diagnostic, "phase"), "frontend");
    assert_eq!(number(diagnostic, "span.start"), 0);
    assert!(number(diagnostic, "span.end") > 0 && number(diagnostic, "span.end") <= source.len());
    assert!(diagnostic.contains("diagnostic.message") && diagnostic.contains("source.name"));
    assert!(
        !trace
            .text
            .lines()
            .any(|line| field(line, "name") == "execute")
    );
}

#[test]
fn runtime_source_validation_failure_is_located_and_never_looks_like_execution_success() {
    let root = workspace();
    let source = "missing -> target\n";
    let output = invoke(root.path(), "run", source);
    assert!(!output.status.success());
    assert!(!root.path().join("target").exists());
    let trace = Trace::load(root.path());
    trace.rooted();
    for phase in ["parse", "compile"] {
        trace.status(phase, "ok");
    }
    for phase in ["execute", "submission", "session"] {
        trace.status(phase, "error");
    }
    let diagnostic = trace
        .text
        .lines()
        .find(|line| field(line, "name") == "diagnostic" && field(line, "phase") == "execute")
        .unwrap();
    assert_eq!(number(diagnostic, "span.start"), 0);
    assert_eq!(number(diagnostic, "span.end"), "missing".len());
    trace.absent("prepare");
    trace.absent("ordinary.publish");
    trace.absent("transaction.publish");
}

#[test]
fn an_actual_http_reset_fails_execution_preserves_old_target_and_records_causal_context() {
    let root = workspace();
    fs::write(root.path().join("target.txt"), "keep old target").unwrap();
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let address = listener.local_addr().unwrap();
    let payload = "private-http-body-must-not-appear-in-telemetry";
    let server = thread::spawn(move || {
        let deadline = Instant::now() + Duration::from_secs(10);
        let mut socket = loop {
            match listener.accept() {
                Ok((socket, _)) => break socket,
                Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                    assert!(
                        Instant::now() < deadline,
                        "runtime never connected to the HTTP source"
                    );
                    thread::sleep(Duration::from_millis(2));
                }
                Err(error) => panic!("{error}"),
            }
        };
        socket.set_nonblocking(false).unwrap();
        socket
            .set_read_timeout(Some(Duration::from_secs(5)))
            .unwrap();
        let mut request = Vec::new();
        let mut bytes = [0_u8; 1024];
        while !request.windows(4).any(|part| part == b"\r\n\r\n") {
            let count = socket.read(&mut bytes).unwrap();
            assert!(count > 0);
            request.extend_from_slice(&bytes[..count]);
        }
        assert!(
            request.starts_with(b"GET /resource HTTP/"),
            "{:?}",
            String::from_utf8_lossy(&request)
        );
        socket
            .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 65536\r\nConnection: close\r\n\r\n")
            .unwrap();
        socket.write_all(payload.as_bytes()).unwrap();
        socket2::SockRef::from(&socket)
            .set_linger(Some(Duration::ZERO))
            .unwrap();
    });
    let source = format!("web => http://{address}\nweb::resource -> target.txt\n");
    let output = invoke(root.path(), "run", &source);
    server.join().unwrap();
    assert!(
        !output.status.success(),
        "truncated/reset HTTP must not report success"
    );
    assert_eq!(
        fs::read_to_string(root.path().join("target.txt")).unwrap(),
        "keep old target"
    );
    let trace = Trace::load(root.path());
    trace.rooted();
    for phase in ["parse", "compile"] {
        trace.status(phase, "ok");
    }
    for phase in ["execute", "submission", "session"] {
        trace.status(phase, "error");
    }
    trace.descendant("execute", "submission");
    trace.statement_journal();
    trace.status("copy", "error");
    trace.status("ordinary.acquire", "error");
    trace.absent("ordinary.stage");
    trace.absent("ordinary.publish");
    assert!(
        trace
            .text
            .lines()
            .any(|line| field(line, "kind") == "event" && line.contains("\"error_chain\":[")),
        "{}",
        trace.text
    );
    assert!(
        trace
            .text
            .lines()
            .any(|line| field(line, "name") == "diagnostic" && field(line, "phase") == "execute")
    );
    trace.no_payload(payload);
}
