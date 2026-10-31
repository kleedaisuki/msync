//! Opt-in, local-only JSONL and SQLite spans shared by the language pipeline.
//!
//! Names and attribute keys use a fixed vocabulary; values describe actual paths,
//! backends and decisions, never file payloads or credentials. Thread-scoped guards
//! cannot cross task boundaries; use explicit
//! contexts and linked spans for asynchronous or concurrent work. All guards and
//! context snapshots retain their sink if the process reconfigures tracing.

use std::cell::RefCell;
use std::collections::hash_map::RandomState;
use std::fs::OpenOptions;
use std::hash::BuildHasher;
use std::io::{self, BufWriter, Write};
use std::marker::PhantomData;
use std::path::Path;
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, OnceLock, RwLock};
use std::time::{Instant, SystemTime, UNIX_EPOCH};

pub mod query;
mod sqlite;

pub use sqlite::Options as SqliteOptions;

static ENABLED: AtomicBool = AtomicBool::new(false);
static SESSION: OnceLock<RwLock<Option<Arc<Session>>>> = OnceLock::new();
static ACTIVE_CONTEXTS: AtomicUsize = AtomicUsize::new(0);
static NEXT_ATTACHMENT: AtomicU64 = AtomicU64::new(1);

thread_local! {
    static STACK: RefCell<Vec<Attachment>> = const { RefCell::new(Vec::new()) };
}

/// An owned execution-context snapshot safe to move between threads and tasks.
/// It identifies a parent span and preserves its original telemetry session.
#[derive(Clone)]
pub struct TraceContext {
    session: Arc<Session>,
    span_id: u64,
}

impl TraceContext {
    /// Flushes this context's sink, even after global tracing is reconfigured.
    pub fn flush(&self) -> Result<(), String> {
        self.session.flush()
    }

    /// Emits a point-in-time event attributed to this span without entering TLS.
    pub fn event(
        &self,
        name: &'static str,
        attributes: &[(&'static str, &str)],
        metrics: &[(&'static str, u64)],
    ) {
        self.session.write(&event_record(
            self,
            name,
            &[],
            attributes,
            &[],
            metrics,
            None,
        ));
    }

    /// Records typed I/O details before the caller flattens its diagnostic.
    /// Recoverable kernel fallbacks must not automatically fail their parent span.
    pub fn io_error(&self, stage: &'static str, error: &io::Error) {
        self.error_event("io.error", stage, error);
    }

    pub fn record_error(&self, stage: &'static str, error: &(dyn std::error::Error + 'static)) {
        self.error_event("error", stage, error);
    }

    fn error_event(
        &self,
        name: &'static str,
        stage: &'static str,
        error: &(dyn std::error::Error + 'static),
    ) {
        let details = error_details(error);
        let mut attributes = vec![("phase", stage)];
        if let Some(kind) = details.chain.iter().find_map(|cause| cause.kind.as_deref()) {
            attributes.push(("error.kind", kind));
        }
        let raw_os_error = details
            .chain
            .iter()
            .find_map(|cause| cause.raw_os_error)
            .map(|code| code.to_string());
        if let Some(code) = raw_os_error.as_deref() {
            attributes.push(("error.raw_os_error", code));
        }
        if let Some(first) = details.chain.first() {
            attributes.push(("error.message", &first.message));
        }
        self.session.write(&event_record(
            self,
            name,
            &[],
            &attributes,
            &[],
            &[],
            Some(&details),
        ));
    }
}

struct Attachment {
    context: TraceContext,
    token: u64,
}

struct AttachmentGuard {
    token: u64,
    thread_bound: PhantomData<Rc<()>>,
}

impl Drop for AttachmentGuard {
    fn drop(&mut self) {
        detach(self.token);
    }
}

fn attach(context: TraceContext) -> u64 {
    let token = NEXT_ATTACHMENT.fetch_add(1, Ordering::Relaxed);
    STACK.with(|stack| stack.borrow_mut().push(Attachment { context, token }));
    ACTIVE_CONTEXTS.fetch_add(1, Ordering::Relaxed);
    token
}

fn detach(token: u64) {
    STACK.with(|stack| {
        let mut stack = stack.borrow_mut();
        if let Some(index) = stack.iter().rposition(|entry| entry.token == token) {
            stack.remove(index);
            ACTIVE_CONTEXTS.fetch_sub(1, Ordering::Relaxed);
        }
    });
}

/// Captures the current synchronous scope's parent context without creating a span.
/// A thread without an attached span returns `None`, not an unrelated global root.
pub fn context() -> Option<TraceContext> {
    if ACTIVE_CONTEXTS.load(Ordering::Relaxed) == 0 {
        return None;
    }
    STACK.with(|stack| stack.borrow().last().map(|entry| entry.context.clone()))
}

/// Runs synchronous work under an explicitly supplied parent, restoring the
/// previous thread context even if `f` unwinds. `None` leaves the context unchanged.
/// Do not return a future from `f` and expect the attachment to cover its polling.
pub fn with_context<T>(parent: Option<&TraceContext>, f: impl FnOnce() -> T) -> T {
    let _attachment = parent.map(|parent| AttachmentGuard {
        token: attach(parent.clone()),
        thread_bound: PhantomData,
    });
    f()
}

/// Emits an event on the current synchronous span. No attached span means no event.
pub fn event(
    name: &'static str,
    attributes: &[(&'static str, &str)],
    metrics: &[(&'static str, u64)],
) {
    if let Some(context) = context() {
        context.event(name, attributes, metrics);
    }
}

/// Captures typed error kind, OS code and causal chain without parsing prose.
pub fn io_error(stage: &'static str, error: &io::Error) {
    if let Some(context) = context() {
        context.io_error(stage, error);
    }
}

pub fn record_error(stage: &'static str, error: &(dyn std::error::Error + 'static)) {
    if let Some(context) = context() {
        context.record_error(stage, error);
    }
}

struct Sink {
    writer: Writer,
    failure: Option<String>,
}

enum Writer {
    Jsonl(Box<dyn Write + Send>),
    Sqlite(sqlite::Writer),
}

impl Writer {
    fn write(&mut self, line: &str) -> Result<(), String> {
        match self {
            Self::Jsonl(writer) => writer
                .write_all(line.as_bytes())
                .map_err(|error| format!("could not write telemetry: {error}")),
            Self::Sqlite(writer) => writer.write(line),
        }
    }

    fn flush(&mut self) -> Result<(), String> {
        match self {
            Self::Jsonl(writer) => writer
                .flush()
                .map_err(|error| format!("could not flush telemetry: {error}")),
            Self::Sqlite(writer) => writer.flush(),
        }
    }

    fn close(&mut self) -> Result<(), String> {
        match self {
            Self::Jsonl(_) => self.flush(),
            Self::Sqlite(writer) => writer.close(),
        }
    }

    fn failure(&self) -> Option<String> {
        match self {
            Self::Jsonl(_) => None,
            Self::Sqlite(writer) => writer.failure(),
        }
    }
}

fn trace_id() -> u128 {
    let random = RandomState::new();
    let now = unix_ns();
    let high = random.hash_one((now, std::process::id(), 0_u8));
    let low = random.hash_one((now, std::process::id(), 1_u8));
    ((u128::from(high) << 64) | u128::from(low)).max(1)
}

struct Session {
    trace_id: u128,
    next_span: AtomicU64,
    sink: Mutex<Sink>,
}

impl Session {
    fn new(writer: Box<dyn Write + Send>) -> Self {
        Self::with_writer(trace_id(), Writer::Jsonl(writer))
    }

    fn with_writer(trace_id: u128, writer: Writer) -> Self {
        Self {
            trace_id,
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
        if let Err(error) = sink.writer.write(line) {
            sink.failure = Some(error);
        }
    }

    fn flush(&self) -> Result<(), String> {
        let mut sink = self.sink.lock().unwrap_or_else(|error| error.into_inner());
        if let Some(error) = &sink.failure {
            return Err(error.clone());
        }
        sink.writer.flush().inspect_err(|message| {
            sink.failure = Some(message.clone());
        })
    }

    fn close(&self) -> Result<(), String> {
        let mut sink = self.sink.lock().unwrap_or_else(|error| error.into_inner());
        let result = sink.writer.close();
        if sink.failure.is_none() {
            sink.failure = result.as_ref().err().cloned();
        }
        sink.failure.clone().map_or(result, Err)
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
    install(Arc::new(Session::new(writer)))
}

/// Enables a normalized, batched SQLite trace. Sampling selects a whole session.
/// Zero sampling disables the current sink without creating a database or thread.
pub fn init_sqlite(path: &Path, options: SqliteOptions) -> Result<(), String> {
    options.validate()?;
    let trace_id = trace_id();
    if (trace_id % 1_000_000) as u32 >= options.sample_per_million {
        return disable();
    }
    let writer = sqlite::Writer::open(path, trace_id, &options)?;
    install(Arc::new(Session::with_writer(
        trace_id,
        Writer::Sqlite(writer),
    )))
}

fn install(session: Arc<Session>) -> Result<(), String> {
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
    sink.failure.clone().or_else(|| sink.writer.failure())
}

/// Finishes the current exporter after all application work and spans settle.
/// Unlike `disable`, this is terminal for contexts retaining the current sink.
/// The CLI uses this boundary so database close failures are observable.
pub fn shutdown() -> Result<(), String> {
    let Some(slot) = SESSION.get() else {
        return Ok(());
    };
    let mut active = slot.write().unwrap_or_else(|error| error.into_inner());
    let result = active.as_ref().map_or(Ok(()), |session| session.close());
    *active = None;
    ENABLED.store(false, Ordering::Release);
    result
}

/// Flushes and disables unparented future spans. Retained contexts and their
/// child spans remain valid, so already-running work keeps its original trace.
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
/// Disabled spans do not allocate; without active contexts they avoid mutexes
/// and the thread-local stack as well.
pub fn span(name: &'static str) -> SpanGuard {
    let session = context()
        .map(|parent| parent.session)
        .or_else(current_session);
    SpanGuard {
        active: session.map(|session| ActiveSpan::new(session, name)),
        thread_bound: PhantomData,
    }
}

/// Starts an explicit child span without attaching it to thread-local state.
///
/// The guard is `Send + Sync` and can live across `.await`, move between tasks,
/// or pass to a blocking worker. `None` disables this span even if a global
/// session is enabled; callers must capture and pass their intended context.
pub fn linked_span(name: &'static str, parent: Option<&TraceContext>) -> LinkedSpan {
    LinkedSpan {
        active: parent.map(|parent| {
            Box::new(ActiveSpan::detached(
                parent.session.clone(),
                name,
                Some(parent.span_id),
            ))
        }),
    }
}

#[must_use = "the guard must remain alive for the duration of the operation"]
pub struct LinkedSpan {
    // Inactive spans must not inflate every containing async future with recording state.
    active: Option<Box<ActiveSpan>>,
}

impl LinkedSpan {
    /// Captures this span as the explicit parent of its child work.
    pub fn context(&self) -> Option<TraceContext> {
        self.active.as_ref().map(|active| TraceContext {
            session: active.session.clone(),
            span_id: active.span_id,
        })
    }

    pub fn error(&mut self) {
        if let Some(active) = &mut self.active {
            active.error();
        }
    }

    pub fn finish_result<T, E>(&mut self, result: &Result<T, E>) {
        if let Some(active) = &mut self.active {
            active.finish_result(result);
        }
    }

    /// Requires a terminal outcome; dropping an unfinished scope is cancellation.
    pub fn expect_completion(&mut self) {
        if let Some(active) = &mut self.active {
            active.completion = Completion::Pending;
        }
    }

    pub fn cancelled(&mut self) {
        if let Some(active) = &mut self.active {
            active.completion = Completion::Cancelled;
        }
    }

    pub fn is_recording(&self) -> bool {
        self.active.is_some()
    }

    pub fn attribute(&mut self, key: &'static str, value: impl Into<String>) {
        if let Some(active) = &mut self.active {
            active.attribute(key, value.into());
        }
    }

    /// Avoids formatting expensive context when telemetry is disabled.
    pub fn attribute_lazy(&mut self, key: &'static str, value: impl FnOnce() -> String) {
        if let Some(active) = &mut self.active {
            active.attribute(key, value());
        }
    }

    pub fn event(
        &self,
        name: &'static str,
        attributes: &[(&'static str, &str)],
        metrics: &[(&'static str, u64)],
    ) {
        if let Some(active) = &self.active {
            active.event(name, attributes, metrics);
        }
    }

    pub fn io_error(&self, stage: &'static str, error: &io::Error) {
        if let Some(context) = self.context() {
            context.io_error(stage, error);
        }
    }

    pub fn record_error(&self, stage: &'static str, error: &(dyn std::error::Error + 'static)) {
        if let Some(context) = self.context() {
            context.record_error(stage, error);
        }
    }

    pub fn metric(&mut self, name: &'static str, value: u64) {
        if let Some(active) = &mut self.active {
            active.metric(name, value);
        }
    }
}

#[must_use = "the guard must remain alive for the duration of the operation"]
pub struct SpanGuard {
    active: Option<ActiveSpan>,
    thread_bound: PhantomData<Rc<()>>,
}

impl SpanGuard {
    pub fn context(&self) -> Option<TraceContext> {
        self.active.as_ref().map(ActiveSpan::context)
    }

    /// Marks a failed operation without guessing its cause from a display string.
    pub fn error(&mut self) {
        if let Some(active) = &mut self.active {
            active.error();
        }
    }

    pub fn finish_result<T, E>(&mut self, result: &Result<T, E>) {
        if let Some(active) = &mut self.active {
            active.finish_result(result);
        }
    }

    pub fn expect_completion(&mut self) {
        if let Some(active) = &mut self.active {
            active.completion = Completion::Pending;
        }
    }

    pub fn cancelled(&mut self) {
        if let Some(active) = &mut self.active {
            active.completion = Completion::Cancelled;
        }
    }

    pub fn is_recording(&self) -> bool {
        self.active.is_some()
    }

    pub fn attribute(&mut self, key: &'static str, value: impl Into<String>) {
        if let Some(active) = &mut self.active {
            active.attribute(key, value.into());
        }
    }

    pub fn attribute_lazy(&mut self, key: &'static str, value: impl FnOnce() -> String) {
        if let Some(active) = &mut self.active {
            active.attribute(key, value());
        }
    }

    pub fn event(
        &self,
        name: &'static str,
        attributes: &[(&'static str, &str)],
        metrics: &[(&'static str, u64)],
    ) {
        if let Some(active) = &self.active {
            active.event(name, attributes, metrics);
        }
    }

    pub fn io_error(&self, stage: &'static str, error: &io::Error) {
        if let Some(active) = &self.active {
            active.context().io_error(stage, error);
        }
    }

    pub fn record_error(&self, stage: &'static str, error: &(dyn std::error::Error + 'static)) {
        if let Some(active) = &self.active {
            active.context().record_error(stage, error);
        }
    }

    /// Adds a numeric measurement such as operations, bytes, or file counts.
    pub fn metric(&mut self, name: &'static str, value: u64) {
        if let Some(active) = &mut self.active {
            active.metric(name, value);
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Completion {
    Automatic,
    Pending,
    Complete,
    Cancelled,
}

struct ActiveSpan {
    session: Arc<Session>,
    span_id: u64,
    parent_span_id: Option<u64>,
    name: &'static str,
    started: Instant,
    start_unix_ns: u128,
    failed: bool,
    completion: Completion,
    metrics: Vec<(&'static str, u64)>,
    attributes: Vec<(&'static str, String)>,
    attachment: Option<u64>,
}

impl ActiveSpan {
    fn new(session: Arc<Session>, name: &'static str) -> Self {
        let parent_span_id = STACK.with(|stack| {
            stack
                .borrow()
                .iter()
                .rev()
                .find(|entry| entry.context.session.trace_id == session.trace_id)
                .map(|entry| entry.context.span_id)
        });
        let mut active = Self::detached(session, name, parent_span_id);
        active.attachment = Some(attach(TraceContext {
            session: active.session.clone(),
            span_id: active.span_id,
        }));
        active
    }

    fn detached(session: Arc<Session>, name: &'static str, parent_span_id: Option<u64>) -> Self {
        let span_id = session.next_span.fetch_add(1, Ordering::Relaxed);
        Self {
            session,
            span_id,
            parent_span_id,
            name,
            started: Instant::now(),
            start_unix_ns: unix_ns(),
            failed: false,
            completion: Completion::Automatic,
            metrics: Vec::new(),
            attributes: Vec::new(),
            attachment: None,
        }
    }

    fn context(&self) -> TraceContext {
        TraceContext {
            session: self.session.clone(),
            span_id: self.span_id,
        }
    }

    fn error(&mut self) {
        self.failed = true;
        self.completion = Completion::Complete;
    }

    fn finish_result<T, E>(&mut self, result: &Result<T, E>) {
        if result.is_err() {
            self.error();
        } else if self.completion != Completion::Cancelled {
            self.completion = Completion::Complete;
        }
    }

    fn attribute(&mut self, key: &'static str, value: String) {
        match self.attributes.iter_mut().find(|(name, _)| *name == key) {
            Some((_, previous)) => *previous = value,
            None => self.attributes.push((key, value)),
        }
    }

    fn event(
        &self,
        name: &'static str,
        attributes: &[(&'static str, &str)],
        metrics: &[(&'static str, u64)],
    ) {
        self.session.write(&event_record(
            &self.context(),
            name,
            &self.attributes,
            attributes,
            &self.metrics,
            metrics,
            None,
        ));
    }

    fn metric(&mut self, name: &'static str, value: u64) {
        match self.metrics.iter_mut().find(|(key, _)| *key == name) {
            Some((_, previous)) => *previous = value,
            None => self.metrics.push((name, value)),
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
        } else if matches!(self.completion, Completion::Pending | Completion::Cancelled) {
            "cancelled"
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
        let completion = match self.completion {
            Completion::Pending => "incomplete",
            Completion::Cancelled => "cancelled",
            Completion::Automatic | Completion::Complete => "complete",
        };
        output.push_str("},\"attributes\":");
        json_attributes(&mut output, &self.attributes, &[]);
        let _ = writeln!(output, ",\"completion\":\"{completion}\"}}");
        output
    }
}

impl Drop for ActiveSpan {
    fn drop(&mut self) {
        if let Some(token) = self.attachment {
            detach(token);
        }
        self.session.write(&self.record());
    }
}

fn unix_ns() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_nanos())
}

struct ErrorDetail {
    message: String,
    kind: Option<String>,
    raw_os_error: Option<i32>,
}

struct ErrorDetails {
    chain: Vec<ErrorDetail>,
    truncated: bool,
}

fn error_details(error: &(dyn std::error::Error + 'static)) -> ErrorDetails {
    let mut chain = Vec::new();
    let mut current = Some(error);
    // Error implementations can contain cycles; recording an error must stay bounded.
    for _ in 0..32 {
        let Some(error) = current else {
            break;
        };
        let io_error = error.downcast_ref::<io::Error>();
        chain.push(ErrorDetail {
            message: error.to_string(),
            kind: io_error.map(|error| format!("{:?}", error.kind())),
            raw_os_error: io_error.and_then(io::Error::raw_os_error),
        });
        // io::Error::source can skip its custom wrapper. Preserve that wrapper
        // explicitly so CopyFailure progress and its underlying OS cause survive.
        current = match io_error.and_then(io::Error::get_ref) {
            Some(inner) => Some(inner as &(dyn std::error::Error + 'static)),
            None => error.source(),
        };
    }
    ErrorDetails {
        chain,
        truncated: current.is_some(),
    }
}

fn event_record(
    context: &TraceContext,
    name: &'static str,
    inherited_attributes: &[(&'static str, String)],
    attributes: &[(&'static str, &str)],
    inherited_metrics: &[(&'static str, u64)],
    metrics: &[(&'static str, u64)],
    errors: Option<&ErrorDetails>,
) -> String {
    use std::fmt::Write as _;
    let mut output = String::with_capacity(384);
    let _ = write!(
        output,
        "{{\"schema_version\":1,\"kind\":\"event\",\"trace_id\":\"{:032x}\",\"span_id\":\"{:016x}\",\"name\":",
        context.session.trace_id, context.span_id
    );
    json_string(&mut output, name);
    let _ = write!(
        output,
        ",\"timestamp_unix_ns\":{},\"attributes\":",
        unix_ns()
    );
    json_attributes(&mut output, inherited_attributes, attributes);
    output.push_str(",\"metrics\":");
    json_metrics(&mut output, inherited_metrics, metrics);
    if let Some(errors) = errors {
        output.push_str(",\"error_chain\":[");
        for (index, cause) in errors.chain.iter().enumerate() {
            if index != 0 {
                output.push(',');
            }
            output.push_str("{\"message\":");
            json_string(&mut output, &cause.message);
            output.push_str(",\"kind\":");
            match &cause.kind {
                Some(kind) => json_string(&mut output, kind),
                None => output.push_str("null"),
            }
            output.push_str(",\"raw_os_error\":");
            match cause.raw_os_error {
                Some(code) => {
                    let _ = write!(output, "{code}");
                }
                None => output.push_str("null"),
            }
            output.push('}');
        }
        let _ = write!(output, "],\"error_chain_truncated\":{}", errors.truncated);
    }
    output.push_str("}\n");
    output
}

fn json_attributes(
    output: &mut String,
    inherited: &[(&'static str, String)],
    attributes: &[(&'static str, &str)],
) {
    output.push('{');
    let mut separator = "";
    for (key, value) in inherited {
        if attributes
            .iter()
            .any(|(override_key, _)| override_key == key)
        {
            continue;
        }
        output.push_str(separator);
        json_string(output, key);
        output.push(':');
        json_string(output, value);
        separator = ",";
    }
    for (index, (key, value)) in attributes.iter().enumerate() {
        if attributes[index + 1..]
            .iter()
            .any(|(later, _)| later == key)
        {
            continue;
        }
        output.push_str(separator);
        json_string(output, key);
        output.push(':');
        json_string(output, value);
        separator = ",";
    }
    output.push('}');
}

fn json_metrics(
    output: &mut String,
    inherited: &[(&'static str, u64)],
    metrics: &[(&'static str, u64)],
) {
    use std::fmt::Write as _;
    output.push('{');
    let mut separator = "";
    for (index, (key, value)) in inherited.iter().chain(metrics).enumerate() {
        let overridden = if index < inherited.len() {
            metrics.iter().any(|(override_key, _)| override_key == key)
        } else {
            metrics[index - inherited.len() + 1..]
                .iter()
                .any(|(later, _)| later == key)
        };
        if overridden {
            continue;
        }
        output.push_str(separator);
        json_string(output, key);
        let _ = write!(output, ":{value}");
        separator = ",";
    }
    output.push('}');
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

    #[test]
    fn linked_spans_and_context_are_send_sync_and_leave_tls_untouched() {
        fn assert_send_sync<T: Send + Sync>() {}
        assert_send_sync::<TraceContext>();
        assert_send_sync::<LinkedSpan>();

        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory.clone())));
        let root = ActiveSpan::new(session, "run");
        let parent = context().unwrap();
        let linked = linked_span("task", Some(&parent));
        assert_eq!(context().unwrap().span_id, root.span_id);
        let child = linked.context().unwrap();
        std::thread::spawn(move || {
            assert!(context().is_none());
            with_context(Some(&child), || {
                let copy = span("copy");
                assert_eq!(
                    copy.active.as_ref().unwrap().parent_span_id,
                    Some(child.span_id)
                );
            });
            assert!(context().is_none());
            drop(linked);
        })
        .join()
        .unwrap();
        assert_eq!(context().unwrap().span_id, root.span_id);
        drop(root);
        assert!(context().is_none());
        assert_eq!(memory.output().lines().count(), 3);
    }

    #[test]
    fn context_attachment_restores_outer_scope_after_panic() {
        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory)));
        let root = ActiveSpan::new(session, "run");
        let root_context = context().unwrap();
        let linked = linked_span("task", Some(&root_context));
        let task_context = linked.context().unwrap();
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            with_context(Some(&task_context), || {
                assert_eq!(context().unwrap().span_id, task_context.span_id);
                let _copy = span("copy");
                panic!("attachment must unwind");
            });
        }));
        assert!(result.is_err());
        assert_eq!(context().unwrap().span_id, root.span_id);
        drop(linked);
        drop(root);
        assert!(context().is_none());
    }

    #[test]
    fn repeated_attachment_of_same_context_is_removed_by_scope_not_span_id() {
        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory)));
        let root = ActiveSpan::new(session, "run");
        let root_context = context().unwrap();
        with_context(Some(&root_context), || {
            // Dropping the original scope must not remove the explicit attachment.
            drop(root);
            assert_eq!(context().unwrap().span_id, root_context.span_id);
        });
        assert!(context().is_none());
    }

    #[test]
    fn explicit_missing_parent_does_not_make_an_unrelated_root() {
        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory.clone())));
        let _root = ActiveSpan::new(session, "run");
        let disabled = linked_span("task", None);
        assert!(disabled.context().is_none());
        drop(disabled);
        assert!(memory.output().is_empty());
    }

    #[test]
    fn dynamic_attributes_and_event_overrides_preserve_useful_context() {
        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory.clone())));
        let mut trace = SpanGuard {
            active: Some(ActiveSpan::new(session, "file_flow")),
            thread_bound: PhantomData,
        };
        assert!(trace.is_recording());
        trace.attribute("source.path", "D:\\資料\\a\".txt");
        trace.attribute("destination.path", "old");
        trace.attribute_lazy("destination.path", || "D:\\copy\\a.txt".to_string());
        trace.attribute("phase", "transfer");
        trace.metric("bytes.progress", 17);
        trace.event(
            "commit",
            &[("phase", "wrong"), ("phase", "commit")],
            &[("bytes.progress", 18), ("bytes.progress", 19)],
        );
        drop(trace);
        let output = memory.output();
        let lines: Vec<&str> = output.lines().collect();
        assert_eq!(lines.len(), 2);
        assert!(lines[0].contains("\"kind\":\"event\""));
        assert!(lines[0].contains("\"source.path\":\"D:\\\\資料\\\\a\\\".txt\""));
        assert_eq!(lines[0].matches("\"phase\":").count(), 1);
        assert!(lines[0].contains("\"phase\":\"commit\""));
        assert_eq!(lines[0].matches("\"bytes.progress\":").count(), 1);
        assert!(lines[0].contains("\"bytes.progress\":19"));
        assert!(lines[1].contains("\"phase\":\"transfer\""));
        assert!(lines[1].contains("\"bytes.progress\":17"));
        assert!(!output.contains("wrong"));
    }

    #[test]
    fn typed_error_event_preserves_wrapper_and_underlying_os_cause() {
        #[derive(Debug)]
        struct ProgressFailure {
            bytes: u64,
            cause: io::Error,
        }
        impl std::fmt::Display for ProgressFailure {
            fn fmt(&self, output: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                write!(
                    output,
                    "transfer stopped after {} bytes: {}",
                    self.bytes, self.cause
                )
            }
        }
        impl std::error::Error for ProgressFailure {
            fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
                Some(&self.cause)
            }
        }
        let error = io::Error::new(
            io::ErrorKind::Interrupted,
            ProgressFailure {
                bytes: 17,
                cause: io::Error::from_raw_os_error(5),
            },
        );
        let details = error_details(&error);
        assert_eq!(details.chain.len(), 3);
        assert_eq!(details.chain[0].kind.as_deref(), Some("Interrupted"));
        assert!(details.chain[1].kind.is_none());
        assert_eq!(details.chain[2].raw_os_error, Some(5));
        assert!(!details.truncated);

        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory.clone())));
        let trace = SpanGuard {
            active: Some(ActiveSpan::new(session, "file_flow")),
            thread_bound: PhantomData,
        };
        trace.io_error("transfer", &error);
        drop(trace);
        let output = memory.output();
        let event = output.lines().next().unwrap();
        assert!(event.contains("\"name\":\"io.error\""));
        assert!(event.contains("\"phase\":\"transfer\""));
        assert!(event.contains("\"error.raw_os_error\":\"5\""));
        assert!(event.contains("\"raw_os_error\":5"));
        assert!(event.contains("\"error_chain\":["));
        assert!(event.contains("transfer stopped after 17 bytes"));
        // Recording a failed kernel candidate is not a terminal copy failure.
        assert!(output.lines().last().unwrap().contains("\"status\":\"ok\""));
    }

    #[test]
    fn cyclic_error_chains_are_bounded() {
        #[derive(Debug)]
        struct Cycle;
        impl std::fmt::Display for Cycle {
            fn fmt(&self, output: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                output.write_str("cyclic source")
            }
        }
        impl std::error::Error for Cycle {
            fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
                Some(self)
            }
        }
        let details = error_details(&Cycle);
        assert_eq!(details.chain.len(), 32);
        assert!(details.truncated);
    }

    #[test]
    fn pending_scopes_report_incomplete_cancellation_without_breaking_defaults() {
        let memory = MemoryWriter::default();
        let session = Arc::new(Session::new(Box::new(memory.clone())));
        let root = ActiveSpan::new(session, "run");
        let parent = context().unwrap();
        drop(linked_span("legacy", Some(&parent)));
        let mut pending = linked_span("pending", Some(&parent));
        pending.expect_completion();
        drop(pending);
        let mut complete = linked_span("complete", Some(&parent));
        complete.expect_completion();
        complete.finish_result(&Ok::<(), ()>(()));
        drop(complete);
        let mut failed = linked_span("failed", Some(&parent));
        failed.expect_completion();
        failed.error();
        drop(failed);
        let mut cancelled = linked_span("cancelled", Some(&parent));
        cancelled.expect_completion();
        cancelled.cancelled();
        cancelled.finish_result(&Ok::<(), ()>(()));
        drop(cancelled);
        drop(root);
        let output = memory.output();
        let lines: Vec<&str> = output.lines().collect();
        assert!(lines[0].contains("\"status\":\"ok\""));
        assert!(lines[1].contains("\"status\":\"cancelled\""));
        assert!(lines[1].contains("\"completion\":\"incomplete\""));
        assert!(lines[2].contains("\"status\":\"ok\""));
        assert!(lines[2].contains("\"completion\":\"complete\""));
        assert!(lines[3].contains("\"status\":\"error\""));
        assert!(lines[3].contains("\"completion\":\"complete\""));
        assert!(lines[4].contains("\"status\":\"cancelled\""));
        assert!(lines[4].contains("\"completion\":\"cancelled\""));
    }

    #[test]
    fn disabled_attributes_do_not_convert_or_evaluate_lazy_values() {
        struct CannotConvert;
        impl From<CannotConvert> for String {
            fn from(_: CannotConvert) -> Self {
                panic!("disabled attribute must not convert its input")
            }
        }
        let mut thread = SpanGuard {
            active: None,
            thread_bound: PhantomData,
        };
        let mut linked = linked_span("disabled", None);
        assert!(!thread.is_recording());
        assert!(!linked.is_recording());
        thread.attribute("path", CannotConvert);
        linked.attribute("path", CannotConvert);
        thread.attribute_lazy("path", || panic!("must remain lazy"));
        linked.attribute_lazy("path", || panic!("must remain lazy"));
        thread.expect_completion();
        linked.expect_completion();
        thread.io_error("open", &io::Error::other("unused"));
        linked.io_error("open", &io::Error::other("unused"));
    }
}

#[cfg(test)]
mod linked_span_layout_tests {
    #[test]
    fn inactive_linked_span_is_pointer_sized_and_remains_send_sync() {
        fn send_sync<T: Send + Sync>() {}
        send_sync::<super::LinkedSpan>();
        assert_eq!(
            std::mem::size_of::<super::LinkedSpan>(),
            std::mem::size_of::<usize>()
        );
        assert!(!super::linked_span("disabled", None).is_recording());
    }
}
