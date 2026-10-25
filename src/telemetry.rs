//! Opt-in, local-only JSONL spans shared by the entire language pipeline.
//!
//! Span names and metric names should be fixed vocabulary, never user paths or
//! source text. A guard belongs to the thread on which it was created. Existing
//! guards retain their original sink if the process reconfigures tracing.

use std::cell::RefCell;
use std::collections::hash_map::RandomState;
use std::fs::OpenOptions;
use std::hash::BuildHasher;
use std::io::{self, BufWriter, Write};
use std::marker::PhantomData;
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock, RwLock};
use std::time::{Instant, SystemTime, UNIX_EPOCH};

static ENABLED: AtomicBool = AtomicBool::new(false);
static SESSION: OnceLock<RwLock<Option<Arc<Session>>>> = OnceLock::new();

thread_local! {
    static STACK: RefCell<Vec<Context>> = const { RefCell::new(Vec::new()) };
}

#[derive(Clone, Copy)]
struct Context {
    trace_id: u128,
    span_id: u64,
}

struct Sink {
    writer: Box<dyn Write + Send>,
    failure: Option<String>,
}

struct Session {
    trace_id: u128,
    next_span: AtomicU64,
    sink: Mutex<Sink>,
}

impl Session {
    fn new(writer: Box<dyn Write + Send>) -> Self {
        let random = RandomState::new();
        let now = unix_ns();
        let high = random.hash_one((now, std::process::id(), 0_u8));
        let low = random.hash_one((now, std::process::id(), 1_u8));
        Self {
            trace_id: ((u128::from(high) << 64) | u128::from(low)).max(1),
            next_span: AtomicU64::new(1),
            sink: Mutex::new(Sink {
                writer,
                failure: None,
            }),
        }
    }

    fn write(&self, line: &str) {
        let mut sink = self.sink.lock().unwrap_or_else(|error| error.into_inner());
        if sink.failure.is_some() {
            return;
        }
        if let Err(error) = sink.writer.write_all(line.as_bytes()) {
            sink.failure = Some(format!("could not write telemetry: {error}"));
        }
    }

    fn flush(&self) -> Result<(), String> {
        let mut sink = self.sink.lock().unwrap_or_else(|error| error.into_inner());
        if let Some(error) = &sink.failure {
            return Err(error.clone());
        }
        sink.writer.flush().map_err(|error| {
            let message = format!("could not flush telemetry: {error}");
            sink.failure = Some(message.clone());
            message
        })
    }
}

/// Enables tracing to an append-only JSONL file, or to stderr for `"-"`.
///
/// The destination's parent must already exist. An initialization failure keeps
/// the previous configuration unchanged. This function never enables a network
/// exporter or reads telemetry settings from the environment.
pub fn init(path: &str) -> Result<(), String> {
    let writer: Box<dyn Write + Send> = if path == "-" {
        Box::new(io::stderr())
    } else {
        let file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)
            .map_err(|error| format!("cannot open telemetry destination: {error}"))?;
        Box::new(BufWriter::new(file))
    };
    let session = Arc::new(Session::new(writer));
    let slot = SESSION.get_or_init(|| RwLock::new(None));
    let mut active = slot.write().unwrap_or_else(|error| error.into_inner());
    if let Some(previous) = active.as_ref() {
        previous.flush()?;
    }
    *active = Some(session);
    ENABLED.store(true, Ordering::Release);
    Ok(())
}

/// Flushes recorded spans. Call after dropping the CLI's outermost span.
pub fn flush() -> Result<(), String> {
    match current_session() {
        Some(session) => session.flush(),
        None => Ok(()),
    }
}

/// Returns the first exporter failure without panicking or exposing user data.
/// The stored failure is retained so later `flush` calls cannot hide it.
pub fn take_error() -> Option<String> {
    let session = current_session()?;
    let sink = session
        .sink
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    sink.failure.clone()
}

/// Flushes and disables future spans. Already-active guards remain valid.
pub fn disable() -> Result<(), String> {
    let Some(slot) = SESSION.get() else {
        return Ok(());
    };
    let mut active = slot.write().unwrap_or_else(|error| error.into_inner());
    let result = active.as_ref().map_or(Ok(()), |session| session.flush());
    *active = None;
    ENABLED.store(false, Ordering::Release);
    result
}

fn current_session() -> Option<Arc<Session>> {
    if !ENABLED.load(Ordering::Acquire) {
        return None;
    }
    SESSION
        .get()?
        .read()
        .unwrap_or_else(|error| error.into_inner())
        .clone()
}

/// Starts a child of the current thread's active span, when tracing is enabled.
/// Disabled spans do not allocate or access a mutex/thread-local stack.
pub fn span(name: &'static str) -> SpanGuard {
    SpanGuard {
        active: current_session().map(|session| ActiveSpan::new(session, name)),
        thread_bound: PhantomData,
    }
}

#[must_use = "the guard must remain alive for the duration of the operation"]
pub struct SpanGuard {
    active: Option<ActiveSpan>,
    thread_bound: PhantomData<Rc<()>>,
}

impl SpanGuard {
    /// Marks a failed operation. Error messages remain in user diagnostics,
    /// rather than accidentally leaking paths or secrets through telemetry.
    pub fn error(&mut self) {
        if let Some(active) = &mut self.active {
            active.failed = true;
        }
    }

    pub fn finish_result<T, E>(&mut self, result: &Result<T, E>) {
        if result.is_err() {
            self.error();
        }
    }

    /// Adds a numeric measurement such as operations, bytes, or file counts.
    pub fn metric(&mut self, name: &'static str, value: u64) {
        if let Some(active) = &mut self.active {
            match active.metrics.iter_mut().find(|(key, _)| *key == name) {
                Some((_, previous)) => *previous = value,
                None => active.metrics.push((name, value)),
            }
        }
    }
}

struct ActiveSpan {
    session: Arc<Session>,
    span_id: u64,
    parent_span_id: Option<u64>,
    name: &'static str,
    started: Instant,
    start_unix_ns: u128,
    failed: bool,
    metrics: Vec<(&'static str, u64)>,
}

impl ActiveSpan {
    fn new(session: Arc<Session>, name: &'static str) -> Self {
        let span_id = session.next_span.fetch_add(1, Ordering::Relaxed);
        let parent_span_id = STACK.with(|stack| {
            let mut stack = stack.borrow_mut();
            let parent = stack
                .iter()
                .rev()
                .find(|context| context.trace_id == session.trace_id)
                .map(|context| context.span_id);
            stack.push(Context {
                trace_id: session.trace_id,
                span_id,
            });
            parent
        });
        Self {
            session,
            span_id,
            parent_span_id,
            name,
            started: Instant::now(),
            start_unix_ns: unix_ns(),
            failed: false,
            metrics: Vec::new(),
        }
    }

    fn record(&self) -> String {
        use std::fmt::Write as _;
        let mut output = String::with_capacity(320);
        let parent = self
            .parent_span_id
            .map(|id| format!("\"{id:016x}\""))
            .unwrap_or_else(|| "null".into());
        let status = if std::thread::panicking() {
            "panic"
        } else if self.failed {
            "error"
        } else {
            "ok"
        };
        let _ = write!(
            output,
            "{{\"schema_version\":1,\"kind\":\"span\",\"trace_id\":\"{:032x}\",\"span_id\":\"{:016x}\",\"parent_span_id\":{},\"name\":",
            self.session.trace_id, self.span_id, parent
        );
        json_string(&mut output, self.name);
        let _ = write!(
            output,
            ",\"start_unix_ns\":{},\"duration_ns\":{},\"status\":\"{}\",\"metrics\":{{",
            self.start_unix_ns,
            self.started.elapsed().as_nanos(),
            status
        );
        for (index, (name, value)) in self.metrics.iter().enumerate() {
            if index != 0 {
                output.push(',');
            }
            json_string(&mut output, name);
            let _ = write!(output, ":{value}");
        }
        output.push_str("}}\n");
        output
    }
}

impl Drop for ActiveSpan {
    fn drop(&mut self) {
        STACK.with(|stack| {
            let mut stack = stack.borrow_mut();
            if let Some(index) = stack.iter().rposition(|context| {
                context.trace_id == self.session.trace_id && context.span_id == self.span_id
            }) {
                stack.remove(index);
            }
        });
        self.session.write(&self.record());
    }
}

fn unix_ns() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_nanos())
}

fn json_string(output: &mut String, value: &str) {
    use std::fmt::Write as _;
    output.push('"');
    for character in value.chars() {
        match character {
            '"' => output.push_str("\\\""),
            '\\' => output.push_str("\\\\"),
            '\n' => output.push_str("\\n"),
            '\r' => output.push_str("\\r"),
            '\t' => output.push_str("\\t"),
            '\u{08}' => output.push_str("\\b"),
            '\u{0c}' => output.push_str("\\f"),
            character if character <= '\u{1f}' => {
                let _ = write!(output, "\\u{:04x}", character as u32);
            }
            character => output.push(character),
        }
    }
    output.push('"');
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Clone, Default)]
    struct MemoryWriter(Arc<Mutex<Vec<u8>>>);

    impl Write for MemoryWriter {
        fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
            self.0.lock().unwrap().extend_from_slice(bytes);
            Ok(bytes.len())
        }

        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    impl MemoryWriter {
        fn output(&self) -> String {
            String::from_utf8(self.0.lock().unwrap().clone()).unwrap()
        }
    }

    #[test]
    fn nested_spans_share_trace_and_record_parent_status_and_metrics() {
        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory.clone())));
        let root = ActiveSpan::new(session.clone(), "run");
        let mut child = ActiveSpan::new(session.clone(), "compile");
        assert_eq!(child.parent_span_id, Some(root.span_id));
        child.failed = true;
        child.metrics.push(("operations", 3));
        drop(child);
        drop(root);
        session.flush().unwrap();
        let output = memory.output();
        let lines: Vec<&str> = output.lines().collect();
        assert_eq!(lines.len(), 2);
        assert!(lines[0].contains("\"status\":\"error\""));
        assert!(lines[0].contains("\"metrics\":{\"operations\":3}"));
        assert!(lines[0].contains("\"parent_span_id\":\"0000000000000001\""));
        assert!(lines[1].contains("\"parent_span_id\":null"));
        for line in lines {
            assert!(line.contains(&format!("\"trace_id\":\"{:032x}\"", session.trace_id)));
            assert!(line.starts_with('{') && line.ends_with('}'));
        }
    }

    #[test]
    fn strings_escape_json_controls_and_preserve_unicode() {
        let mut output = String::new();
        json_string(&mut output, "測試\"\\\n\r\t\u{0}\u{08}\u{0c}");
        assert_eq!(output, "\"測試\\\"\\\\\\n\\r\\t\\u0000\\b\\f\"");
    }

    #[test]
    fn concurrent_writers_keep_complete_lines_and_unique_ids() {
        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory.clone())));
        std::thread::scope(|scope| {
            for _ in 0..8 {
                let session = session.clone();
                scope.spawn(move || {
                    for _ in 0..32 {
                        drop(ActiveSpan::new(session.clone(), "copy"));
                    }
                });
            }
        });
        let output = memory.output();
        let lines: Vec<&str> = output.lines().collect();
        assert_eq!(lines.len(), 256);
        let mut ids = std::collections::BTreeSet::new();
        for line in lines {
            assert!(line.starts_with('{') && line.ends_with('}'));
            let id = line
                .split("\"span_id\":\"")
                .nth(1)
                .unwrap()
                .split('"')
                .next()
                .unwrap();
            assert!(ids.insert(id.to_string()));
        }
    }

    #[test]
    fn out_of_order_drops_do_not_leave_stale_parents() {
        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory)));
        let root = ActiveSpan::new(session.clone(), "run");
        let child = ActiveSpan::new(session.clone(), "compile");
        drop(root);
        drop(child);
        let next = ActiveSpan::new(session, "parse");
        assert!(next.parent_span_id.is_none());
    }

    #[test]
    fn exporter_failure_is_observable_without_panicking() {
        struct BrokenWriter;
        impl Write for BrokenWriter {
            fn write(&mut self, _: &[u8]) -> io::Result<usize> {
                Err(io::Error::other("simulated disk full"))
            }
            fn flush(&mut self) -> io::Result<()> {
                Ok(())
            }
        }
        let session = Arc::new(Session::new(Box::new(BrokenWriter)));
        drop(ActiveSpan::new(session.clone(), "run"));
        assert!(session.flush().unwrap_err().contains("simulated disk full"));
        drop(ActiveSpan::new(session.clone(), "next"));
        assert!(session.flush().is_err());
    }

    #[test]
    fn guard_can_mark_result_and_replace_numeric_metrics() {
        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory.clone())));
        let mut guard = SpanGuard {
            active: Some(ActiveSpan::new(session, "check")),
            thread_bound: PhantomData,
        };
        guard.metric("statements", 1);
        guard.metric("statements", 2);
        guard.finish_result(&Err::<(), _>("not recorded"));
        drop(guard);
        let output = memory.output();
        assert!(output.contains("\"statements\":2"));
        assert!(output.contains("\"status\":\"error\""));
        assert!(!output.contains("not recorded"));
    }

    #[test]
    fn panic_unwinding_records_failure_and_cleans_parent_stack() {
        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory.clone())));
        let captured = session.clone();
        let result = std::panic::catch_unwind(move || {
            let _guard = ActiveSpan::new(captured, "run");
            panic!("simulated failure");
        });
        assert!(result.is_err());
        assert!(memory.output().contains("\"status\":\"panic\""));
        let next = ActiveSpan::new(session, "next");
        assert!(next.parent_span_id.is_none());
    }
}
