//! Owned byte flows. Closing one endpoint settles only its transfer.

use std::fs;
use std::future::{Future, poll_fn};
use std::path::Path;
use std::sync::Arc;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::task::JoinHandle;
use tokio_util::sync::CancellationToken;

use crate::compiler::CopyOp;
use crate::endpoint::{Backend, Location};
use crate::hir::{Input, Instruction, InstructionKind, Output};
use crate::io::{FlowEnd, FlowReport};
use crate::language::DestinationMode;
use crate::syntax::Span;

use super::error::{ExecutionError, Result};
use super::program::{Reader, Writer};

pub(super) fn validate_native_transfer(node: &Instruction) -> Result<()> {
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        destination,
    } = &node.kind
    else {
        return Ok(());
    };
    let Some(path) = crate::runtime::local_path(location, true)
        .map_err(|message| ExecutionError::new(message, node.span))?
    else {
        return Ok(());
    };
    #[cfg(windows)]
    if crate::streams::is_named_pipe_path(&path.to_string_lossy()) {
        return Ok(());
    }
    if !fs::metadata(path).is_ok_and(|metadata| metadata.is_dir()) {
        return Ok(());
    }
    let physical = match destination {
        Output::Stdin(_) => true,
        Output::File { location, .. } => native_stream(location, false),
    };
    if !physical {
        return Ok(());
    }
    Err(ExecutionError::new(
        "a directory cannot be written to a pipe or socket",
        node.span,
    )
    .help("Select an ordinary file, or explicitly serialize the directory with a program before connecting its stdout."))
}

pub(super) fn native_stream(location: &Location, source: bool) -> bool {
    if matches!(location.backend, Backend::Tcp { .. }) {
        return true;
    }
    let Ok(Some(path)) = crate::runtime::local_path(location, source) else {
        return false;
    };
    #[cfg(windows)]
    if crate::streams::is_named_pipe_path(&path.to_string_lossy()) {
        return true;
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::FileTypeExt;
        fs::metadata(path).is_ok_and(|metadata| {
            metadata.file_type().is_fifo() || metadata.file_type().is_socket()
        })
    }
    #[cfg(not(unix))]
    false
}

pub(super) enum Source {
    Endpoint(Location),
    File { file: fs::File, location: Location },
    Reader(Reader),
    Literal(Arc<str>),
}

impl Source {
    pub fn capture(location: Location, scratch: &Path, span: Span) -> Result<Self> {
        capture_source(location, scratch, span)
    }

    /// Finish finite native acquisition before dispatching a background flow.
    /// Stream metadata inspection never opens a FIFO/socket or waits for a peer.
    #[cfg(test)]
    pub async fn acquire(location: &Location, scratch: &Path, span: Span) -> Result<Self> {
        let location = location.clone();
        let scratch = scratch.to_owned();
        let context = crate::telemetry::context();
        crate::runtime::ssh::spawn_blocking(move || {
            crate::telemetry::with_context(context.as_ref(), || {
                capture_source(location, &scratch, span)
            })
        })
        .await
        .map_err(|cause| {
            ExecutionError::caused("flow-source acquisition worker failed", span, cause)
        })?
    }
}

fn capture_source(location: Location, _scratch: &Path, span: Span) -> Result<Source> {
    #[cfg(windows)]
    if crate::streams::is_named_pipe_path(&location.path) {
        return Ok(Source::Endpoint(location));
    }
    let Some(path) = crate::runtime::local_path(&location, true)
        .map_err(|message| ExecutionError::new(message, span))?
    else {
        return Ok(Source::Endpoint(location));
    };
    #[cfg(windows)]
    if crate::streams::is_named_pipe_path(&path.to_string_lossy()) {
        return Ok(Source::Endpoint(location));
    }
    let metadata = fs::metadata(&path).map_err(|cause| {
        ExecutionError::caused(
            "cannot inspect the flow source before admission",
            span,
            cause,
        )
    })?;
    if !metadata.is_file() {
        return Ok(Source::Endpoint(location));
    }
    let file = fs::File::open(&path).map_err(|cause| {
        ExecutionError::caused("cannot open the flow source before admission", span, cause)
    })?;
    Ok(Source::File { file, location })
}

pub(super) enum Destination {
    Endpoint {
        location: Location,
        mode: DestinationMode,
    },
    Writer {
        writer: Writer,
        closed: CancellationToken,
    },
}

pub(super) struct Flow {
    completion: CancellationToken,
    handle: Option<JoinHandle<Result<FlowReport>>>,
    span: Span,
}

impl Flow {
    #[cfg(test)]
    pub fn start(
        source: Source,
        destination: Destination,
        buffer: usize,
        cancel: CancellationToken,
        span: Span,
    ) -> Self {
        Self::prepare_or_start(source, destination, buffer, cancel, span)
            .expect("valid test flow admission")
    }

    #[cfg(test)]
    pub fn prepare_or_start(
        source: Source,
        destination: Destination,
        buffer: usize,
        cancel: CancellationToken,
        span: Span,
    ) -> Result<Self> {
        let pins = prepare(&source, &destination, buffer, span)?;
        Self::start_with_pins(source, destination, buffer, cancel, span, pins)
    }

    pub fn start_with_pins(
        source: Source,
        destination: Destination,
        buffer: usize,
        cancel: CancellationToken,
        span: Span,
        pins: crate::streams::FifoPins,
    ) -> Result<Self> {
        Self::start_task(source, destination, buffer, cancel, span, pins, None)
    }

    pub fn start_queued(
        source: Source,
        destination: Destination,
        buffer: usize,
        cancel: CancellationToken,
        span: Span,
        pins: crate::streams::FifoPins,
        admission: super::scheduler::Admission,
    ) -> Result<Self> {
        admission.feasible(super::scheduler::Demand::flows(1), span)?;
        Self::start_task(
            source,
            destination,
            buffer,
            cancel,
            span,
            pins,
            Some(admission),
        )
    }

    fn start_task(
        source: Source,
        destination: Destination,
        buffer: usize,
        cancel: CancellationToken,
        span: Span,
        pins: crate::streams::FifoPins,
        admission: Option<super::scheduler::Admission>,
    ) -> Result<Self> {
        if buffer == 0 {
            return Err(ExecutionError::new(
                "the byte-flow buffer size must be positive",
                span,
            ));
        }
        let context = crate::telemetry::context();
        let completion = CancellationToken::new();
        let completed = completion.clone().drop_guard();
        let handle = crate::runtime::ssh::spawn(async move {
            let _completed = completed;
            let mut task = crate::telemetry::linked_span("task", context.as_ref());
            task.expect_completion();
            task.attribute("operation", "byte-flow");
            let context = task.context();
            let mut future = Box::pin(async move {
                let _lease = match admission {
                    Some(admission) => tokio::select! {
                        biased;
                        () = cancel.cancelled() => return Ok(report(0, FlowEnd::Cancelled)),
                        lease = admission.wait_flow(span) => Some(lease?),
                    },
                    None => None,
                };
                run_prepared(source, destination, buffer, cancel, span, pins).await
            });
            let result = poll_fn(|cx| {
                crate::telemetry::with_context(context.as_ref(), || future.as_mut().poll(cx))
            })
            .await;
            super::scheduler::finish_task(
                &mut task,
                &result,
                result
                    .as_ref()
                    .is_ok_and(|report| report.end == FlowEnd::Cancelled),
            );
            if let Ok(report) = &result {
                task.metric("bytes", report.bytes);
            }
            result
        });
        Ok(Self {
            completion,
            handle: Some(handle),
            span,
        })
    }

    pub fn is_finished(&self) -> bool {
        self.completion.is_cancelled() || self.handle.as_ref().is_none_or(JoinHandle::is_finished)
    }

    pub fn completion_signal(&self) -> CancellationToken {
        self.completion.clone()
    }

    pub async fn join(&mut self) -> Result<FlowReport> {
        let Some(handle) = self.handle.as_mut() else {
            return Err(ExecutionError::new(
                "the byte flow has already been joined",
                self.span,
            ));
        };
        let result = handle.await;
        self.handle.take();
        result
            .map_err(|cause| ExecutionError::caused("byte-flow worker failed", self.span, cause))?
    }
}

#[cfg(test)]
async fn run(
    source: Source,
    destination: Destination,
    buffer: usize,
    cancel: CancellationToken,
    span: Span,
) -> Result<FlowReport> {
    let pins = prepare(&source, &destination, buffer, span)?;
    run_prepared(source, destination, buffer, cancel, span, pins).await
}

#[cfg(test)]
fn prepare(
    source: &Source,
    destination: &Destination,
    buffer: usize,
    span: Span,
) -> Result<crate::streams::FifoPins> {
    if buffer == 0 {
        return Err(ExecutionError::new(
            "the byte-flow buffer size must be positive",
            span,
        ));
    }
    let source = match source {
        Source::Endpoint(location) => pin_fifo(location, true, span)?,
        _ => None,
    };
    let destination = match destination {
        Destination::Endpoint {
            location,
            mode: DestinationMode::Exact,
        } => pin_fifo(location, false, span)?,
        _ => None,
    };
    Ok(crate::streams::FifoPins {
        source,
        destination,
    })
}

#[cfg(test)]
fn pin_fifo(
    location: &Location,
    source: bool,
    span: Span,
) -> Result<Option<crate::streams::FifoIdentity>> {
    let Some(path) = crate::runtime::local_path(location, source)
        .map_err(|message| ExecutionError::new(message, span))?
    else {
        return Ok(None);
    };
    let pin = match crate::streams::fifo_identity(&path) {
        Ok(pin) => pin,
        Err(cause) if !source && cause.kind() == std::io::ErrorKind::NotFound => None,
        Err(cause) => {
            return Err(ExecutionError::caused(
                "cannot retain the byte-flow FIFO generation",
                span,
                cause,
            ));
        }
    };
    if let Some(pin) = &pin
        && let Some(context) = crate::telemetry::context()
    {
        context.event(
            "flow.fifo.pin",
            &[
                ("identity", &pin.native_id()),
                ("path", &path.to_string_lossy()),
            ],
            &[],
        );
    }
    Ok(pin)
}

async fn run_prepared(
    source: Source,
    destination: Destination,
    buffer: usize,
    cancel: CancellationToken,
    span: Span,
    pins: crate::streams::FifoPins,
) -> Result<FlowReport> {
    match destination {
        Destination::Endpoint { location, mode } => {
            to_endpoint(source, location, mode, buffer, cancel, span, pins).await
        }
        Destination::Writer { writer, closed } => {
            to_writer(source, writer, closed, buffer, cancel, span, pins.source).await
        }
    }
}

async fn to_endpoint(
    source: Source,
    mut destination: Location,
    mode: DestinationMode,
    buffer: usize,
    cancel: CancellationToken,
    span: Span,
    pins: crate::streams::FifoPins,
) -> Result<FlowReport> {
    let retained_fifo = pins.source.is_some() || pins.destination.is_some();
    if mode == DestinationMode::ReplaceTree {
        return Err(ExecutionError::new(
            "a byte flow cannot replace a directory tree",
            span,
        ));
    }
    if mode == DestinationMode::Container {
        let named = match &source {
            Source::File { .. } => true,
            Source::Endpoint(location) => !matches!(location.backend, Backend::Tcp { .. }),
            _ => false,
        };
        if !named {
            return Err(ExecutionError::new(
                "an unnamed program or literal flow needs an exact destination filename",
                span,
            )
            .help("Replace the trailing '/' with the output filename."));
        }
        if !destination.path.ends_with('/') {
            destination.path.push('/');
        }
    }
    let result = match source {
        Source::Endpoint(source) => {
            crate::streams::transfer_with_fifo_pins(
                &CopyOp {
                    source,
                    destination,
                    pattern: None,
                    span,
                },
                buffer,
                cancel,
                crate::telemetry::context(),
                pins,
            )
            .await
        }
        Source::Reader(source) => {
            crate::streams::transfer_from_reader(
                source,
                &destination,
                buffer,
                cancel,
                span,
                pins.destination,
            )
            .await
        }
        Source::Literal(text) => {
            crate::streams::transfer_from_reader(
                LiteralReader { text, position: 0 },
                &destination,
                buffer,
                cancel,
                span,
                pins.destination,
            )
            .await
        }
        Source::File { file, location } => {
            crate::streams::transfer_from_file(
                file,
                &CopyOp {
                    source: location,
                    destination,
                    pattern: None,
                    span,
                },
                buffer,
                cancel,
                pins.destination,
            )
            .await
        }
    };
    result.map_err(|cause| flow_failure("byte-flow output failed", span, cause, retained_fifo))
}

fn flow_failure(
    message: &'static str,
    span: Span,
    cause: crate::runtime::RuntimeError,
    retained_fifo: bool,
) -> ExecutionError {
    let error = ExecutionError::caused(message, span, cause);
    if retained_fifo {
        return error.help("This flow retains a specific FIFO generation. Keep that FIFO in place until its flows settle; if it was replaced, use /stop to release existing flows, then re-evaluate the statements against the current endpoints.");
    }
    error
}

async fn to_writer(
    source: Source,
    writer: Writer,
    closed: CancellationToken,
    buffer: usize,
    cancel: CancellationToken,
    span: Span,
    fifo: Option<crate::streams::FifoIdentity>,
) -> Result<FlowReport> {
    let reader = match source {
        Source::Endpoint(source) => {
            let retained_fifo = fifo.is_some();
            return crate::streams::transfer_to_writer(
                &source, writer, buffer, cancel, span, closed, fifo,
            )
            .await
            .map_err(|cause| flow_failure("byte-flow input failed", span, cause, retained_fifo));
        }
        Source::Reader(reader) => reader,
        Source::Literal(text) => Box::pin(LiteralReader { text, position: 0 }) as Reader,
        Source::File { file, .. } => {
            let mut file = tokio::fs::File::from_std(file);
            file.set_max_buf_size(buffer);
            Box::pin(file) as Reader
        }
    };
    pump(reader, writer, closed, buffer, cancel, span).await
}

async fn pump(
    mut source: Reader,
    mut destination: Writer,
    closed: CancellationToken,
    buffer: usize,
    cancel: CancellationToken,
    span: Span,
) -> Result<FlowReport> {
    let mut data = vec![0_u8; buffer];
    let mut bytes = 0_u64;
    loop {
        let count = tokio::select! {
            biased;
            () = cancel.cancelled() => return Ok(report(bytes, FlowEnd::Cancelled)),
            () = closed.cancelled() => return Ok(report(bytes, FlowEnd::DestinationClosed)),
            read = source.read(&mut data) => read.map_err(|cause| {
                ExecutionError::caused(format!("cannot read program flow after {bytes} bytes"), span, cause)
            })?,
        };
        if count == 0 {
            let shutdown = tokio::select! {
                biased;
                () = cancel.cancelled() => return Ok(report(bytes, FlowEnd::Cancelled)),
                () = closed.cancelled() => return Ok(report(bytes, FlowEnd::DestinationClosed)),
                result = destination.shutdown() => result,
            };
            return match shutdown {
                Ok(()) => Ok(report(bytes, FlowEnd::SourceClosed)),
                Err(cause) if crate::io::is_disconnect(&cause) => {
                    Ok(report(bytes, FlowEnd::DestinationClosed))
                }
                Err(cause) => Err(ExecutionError::caused(
                    "cannot close program flow destination",
                    span,
                    cause,
                )),
            };
        }
        let mut offset = 0;
        while offset < count {
            let result = tokio::select! {
                biased;
                () = cancel.cancelled() => return Ok(report(bytes, FlowEnd::Cancelled)),
                () = closed.cancelled() => return Ok(report(bytes, FlowEnd::DestinationClosed)),
                result = destination.write(&data[offset..count]) => result,
            };
            match result {
                Ok(0) => return Ok(report(bytes, FlowEnd::DestinationClosed)),
                Ok(count) => {
                    offset += count;
                    bytes += count as u64;
                }
                Err(cause) if crate::io::is_disconnect(&cause) => {
                    return Ok(report(bytes, FlowEnd::DestinationClosed));
                }
                Err(cause) => {
                    return Err(ExecutionError::caused(
                        format!("cannot write program flow after {bytes} bytes"),
                        span,
                        cause,
                    ));
                }
            }
        }
    }
}

fn report(bytes: u64, end: FlowEnd) -> FlowReport {
    FlowReport { bytes, end }
}

struct LiteralReader {
    text: Arc<str>,
    position: usize,
}

impl tokio::io::AsyncRead for LiteralReader {
    fn poll_read(
        mut self: std::pin::Pin<&mut Self>,
        _: &mut std::task::Context<'_>,
        buffer: &mut tokio::io::ReadBuf<'_>,
    ) -> std::task::Poll<std::io::Result<()>> {
        let count = buffer.remaining().min(self.text.len() - self.position);
        buffer.put_slice(&self.text.as_bytes()[self.position..self.position + count]);
        self.position += count;
        std::task::Poll::Ready(Ok(()))
    }
}

#[cfg(test)]
mod tests {
    use std::fs;
    use std::task::{Context, Poll};

    use tokio::io::{AsyncRead, ReadBuf};

    use super::*;

    #[test]
    fn directory_stream_rejection_is_metadata_only_and_actionable() {
        let root = fixture();
        let source = root.path().join("directory");
        fs::create_dir(&source).unwrap();
        fs::write(source.join("keep"), b"unchanged").unwrap();
        let node = Instruction {
            id: crate::hir::InstructionId(0),
            span: Span::default(),
            expansion: vec![],
            kind: InstructionKind::Transfer {
                source: Input::File {
                    location: local(&source),
                    pattern: None,
                },
                destination: Output::File {
                    location: Location {
                        backend: Backend::Tcp {
                            host: "127.0.0.1".into(),
                            port: 1,
                        },
                        path: "/".into(),
                    },
                    mode: DestinationMode::Exact,
                },
            },
        };
        let error = validate_native_transfer(&node).unwrap_err();
        assert!(error.report.diagnostic.message.contains("directory cannot"));
        assert!(
            error
                .report
                .help
                .iter()
                .any(|help| { help.contains("serialize") && help.contains("stdout") })
        );
        assert_eq!(fs::read(source.join("keep")).unwrap(), b"unchanged");
        assert_eq!(fs::read_dir(root.path()).unwrap().count(), 1);
    }

    #[test]
    fn retained_fifo_failure_explains_recovery_without_interpreting_error_text() {
        let error = flow_failure(
            "byte-flow output failed",
            Span::default(),
            crate::runtime::RuntimeError {
                message: "native endpoint failure".into(),
                span: Span::default(),
            },
            true,
        );
        assert!(error.report.help.iter().any(|help| {
            help.contains("FIFO generation")
                && help.contains("/stop")
                && help.contains("re-evaluate")
        }));
        assert_eq!(
            error.native_cause().unwrap().to_string(),
            "native endpoint failure"
        );
    }

    #[cfg(unix)]
    #[test]
    fn native_socket_classification_never_connects_to_its_listener() {
        let root = fixture();
        let path = root.path().join("socket");
        let listener = std::os::unix::net::UnixListener::bind(&path).unwrap();
        listener.set_nonblocking(true).unwrap();
        assert!(native_stream(&local(&path), true));
        assert!(native_stream(&local(&path), false));
        assert_eq!(
            listener.accept().unwrap_err().kind(),
            std::io::ErrorKind::WouldBlock
        );
    }

    #[tokio::test]
    async fn a_literal_is_byte_exact_with_a_small_bounded_buffer() {
        let text: Arc<str> = Arc::from("Klee 可莉\n".repeat(1024));
        let (writer, mut consumer) = tokio::io::duplex(7);
        let expected = text.as_bytes().to_vec();
        let mut flow = Flow::start(
            Source::Literal(text),
            Destination::Writer {
                writer: Box::pin(writer),
                closed: CancellationToken::new(),
            },
            3,
            CancellationToken::new(),
            Span::default(),
        );
        let consumer = crate::runtime::ssh::spawn(async move {
            let mut data = Vec::new();
            consumer.read_to_end(&mut data).await.unwrap();
            data
        });
        let report = flow.join().await.unwrap();
        assert_eq!(report.end, FlowEnd::SourceClosed);
        assert_eq!(report.bytes, expected.len() as u64);
        assert_eq!(consumer.await.unwrap(), expected);
    }

    #[tokio::test]
    async fn a_closed_consumer_does_not_cancel_other_flows() {
        let parent = CancellationToken::new();
        let closed = CancellationToken::new();
        let (_producer, reader) = tokio::io::duplex(8);
        let (writer, _consumer) = tokio::io::duplex(8);
        let mut flow = Flow::start(
            Source::Reader(Box::pin(reader)),
            Destination::Writer {
                writer: Box::pin(writer),
                closed: closed.clone(),
            },
            8,
            parent.child_token(),
            Span::default(),
        );
        closed.cancel();
        let report = flow.join().await.unwrap();
        assert_eq!(report.end, FlowEnd::DestinationClosed);
        assert_eq!(report.bytes, 0);
        assert!(!parent.is_cancelled());
        let report = run(
            Source::Literal(Arc::from("still running")),
            Destination::Writer {
                writer: Box::pin(tokio::io::sink()),
                closed: CancellationToken::new(),
            },
            4,
            parent.child_token(),
            Span::default(),
        )
        .await
        .unwrap();
        assert_eq!(report.end, FlowEnd::SourceClosed);
        assert_eq!(report.bytes, 13);
    }

    #[tokio::test]
    async fn busy_independent_flow_waits_in_its_task_and_releases_capacity_on_completion() {
        let limits = crate::scheduler::ResourceLimits {
            jobs: 1,
            max_streams: 1,
            buffer_bytes: 4,
            memory_bytes: 8,
        };
        let admission = super::super::scheduler::Admission::new(&limits).unwrap();
        let held = admission.flow(Span::default()).unwrap();
        let (writer, mut reader) = tokio::io::duplex(4);
        let mut flow = Flow::start_queued(
            Source::Literal(Arc::from("queued bytes")),
            Destination::Writer {
                writer: Box::pin(writer),
                closed: CancellationToken::new(),
            },
            4,
            CancellationToken::new(),
            Span::default(),
            crate::streams::FifoPins::default(),
            admission.clone(),
        )
        .unwrap();
        tokio::task::yield_now().await;
        assert!(!flow.is_finished());
        let received = tokio::spawn(async move {
            let mut bytes = Vec::new();
            reader.read_to_end(&mut bytes).await.unwrap();
            bytes
        });
        drop(held);
        let result = tokio::time::timeout(std::time::Duration::from_secs(2), flow.join())
            .await
            .unwrap()
            .unwrap();
        assert_eq!(result.end, FlowEnd::SourceClosed);
        assert_eq!(received.await.unwrap(), b"queued bytes");
        assert_eq!(
            admission.availability(super::super::scheduler::Demand::flows(1)),
            super::super::scheduler::Availability::Ready
        );
    }

    #[tokio::test]
    async fn cancelling_queued_flow_does_not_wait_for_another_transfers_permit() {
        let limits = crate::scheduler::ResourceLimits {
            jobs: 1,
            max_streams: 1,
            buffer_bytes: 4,
            memory_bytes: 8,
        };
        let admission = super::super::scheduler::Admission::new(&limits).unwrap();
        let _held = admission.flow(Span::default()).unwrap();
        let cancel = CancellationToken::new();
        let mut flow = Flow::start_queued(
            Source::Literal(Arc::from("not admitted")),
            Destination::Writer {
                writer: Box::pin(tokio::io::sink()),
                closed: CancellationToken::new(),
            },
            4,
            cancel.clone(),
            Span::default(),
            crate::streams::FifoPins::default(),
            admission,
        )
        .unwrap();
        cancel.cancel();
        let result = tokio::time::timeout(std::time::Duration::from_secs(2), flow.join())
            .await
            .unwrap()
            .unwrap();
        assert_eq!(result.end, FlowEnd::Cancelled);
        assert_eq!(result.bytes, 0);
    }

    #[tokio::test]
    async fn cancellation_preserves_the_previous_finite_file() {
        let scratch = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&scratch).unwrap();
        let root = tempfile::tempdir_in(scratch).unwrap();
        let target = root.path().join("target");
        fs::write(&target, b"previous complete data").unwrap();
        let (waiting, reached) = tokio::sync::oneshot::channel();
        let cancel = CancellationToken::new();
        let mut flow = Flow::start(
            Source::Reader(Box::pin(PausedReader {
                sent: false,
                waiting: Some(waiting),
            })),
            Destination::Endpoint {
                location: Location {
                    backend: Backend::Local,
                    path: target.to_string_lossy().into_owned(),
                },
                mode: DestinationMode::Exact,
            },
            64,
            cancel.clone(),
            Span::default(),
        );
        reached.await.unwrap();
        assert_eq!(fs::read(&target).unwrap(), b"previous complete data");
        cancel.cancel();
        assert_eq!(flow.join().await.unwrap().end, FlowEnd::Cancelled);
        assert_eq!(fs::read(&target).unwrap(), b"previous complete data");
        assert_eq!(fs::read_dir(root.path()).unwrap().count(), 1);
    }

    #[tokio::test]
    async fn an_unnamed_port_cannot_implicitly_choose_a_container_name() {
        let error = run(
            Source::Literal(Arc::from("data")),
            Destination::Endpoint {
                location: Location {
                    backend: Backend::Local,
                    path: "unused-container/".into(),
                },
                mode: DestinationMode::Container,
            },
            4,
            CancellationToken::new(),
            Span::default(),
        )
        .await
        .unwrap_err();
        assert!(
            error
                .report
                .diagnostic
                .message
                .contains("exact destination")
        );
    }

    #[tokio::test]
    async fn opened_input_retains_the_version_replaced_by_a_later_statement() {
        let root = fixture();
        let input = root.path().join("input.txt");
        let scratch = root.path().join("scratch");
        fs::create_dir(&scratch).unwrap();
        fs::write(&input, b"bytes admitted before submit returned").unwrap();
        let source = Source::acquire(&local(&input), &scratch, Span::default())
            .await
            .unwrap();
        assert!(matches!(&source, Source::File { .. }));
        let replacement = root.path().join("replacement");
        fs::write(&replacement, b"new bytes written by the next statement").unwrap();
        crate::io::publish_file(&replacement, &input, false).unwrap();
        let (writer, mut consumer) = tokio::io::duplex(5);
        let consumer = crate::runtime::ssh::spawn(async move {
            let mut bytes = Vec::new();
            consumer.read_to_end(&mut bytes).await.unwrap();
            bytes
        });
        let report = run(
            source,
            Destination::Writer {
                writer: Box::pin(writer),
                closed: CancellationToken::new(),
            },
            7,
            CancellationToken::new(),
            Span::default(),
        )
        .await
        .unwrap();
        assert_eq!(report.end, FlowEnd::SourceClosed);
        assert_eq!(
            consumer.await.unwrap(),
            b"bytes admitted before submit returned"
        );
        assert_eq!(fs::read_dir(&scratch).unwrap().count(), 0);
    }

    #[tokio::test]
    async fn opened_finite_input_retains_its_container_basename() {
        let root = fixture();
        let input = root.path().join("named-input.txt");
        let scratch = root.path().join("scratch");
        let output = root.path().join("output");
        fs::create_dir(&scratch).unwrap();
        fs::create_dir(&output).unwrap();
        fs::write(&input, b"admitted").unwrap();
        let source = Source::acquire(&local(&input), &scratch, Span::default())
            .await
            .unwrap();
        fs::remove_file(&input).unwrap();
        run(
            source,
            Destination::Endpoint {
                location: local(&output),
                mode: DestinationMode::Container,
            },
            4,
            CancellationToken::new(),
            Span::default(),
        )
        .await
        .unwrap();
        assert_eq!(
            fs::read(output.join("named-input.txt")).unwrap(),
            b"admitted"
        );
        assert_eq!(fs::read_dir(output).unwrap().count(), 1);
        assert_eq!(fs::read_dir(scratch).unwrap().count(), 0);
    }

    #[cfg(unix)]
    #[tokio::test]
    async fn acquisition_does_not_open_a_fifo_waiting_for_a_peer() {
        use std::os::unix::ffi::OsStrExt;

        let root = fixture();
        let pipe = root.path().join("unpaired-fifo");
        let name = std::ffi::CString::new(pipe.as_os_str().as_bytes()).unwrap();
        // SAFETY: The exclusively owned fixture path is live and NUL-terminated.
        assert_eq!(unsafe { libc::mkfifo(name.as_ptr(), 0o600) }, 0);
        let scratch = root.path().join("not-created");
        let source = tokio::time::timeout(
            std::time::Duration::from_secs(2),
            Source::acquire(&local(&pipe), &scratch, Span::default()),
        )
        .await
        .expect("FIFO classification must not wait for its peer")
        .unwrap();
        assert!(matches!(source, Source::Endpoint(_)));
        assert!(!scratch.exists());
    }

    #[cfg(any(target_os = "linux", target_os = "android", target_os = "freebsd"))]
    #[tokio::test]
    async fn a_fifo_replaced_after_dispatch_cannot_receive_or_supply_bytes() {
        for source_fifo in [true, false] {
            let root = fixture();
            let path = root.path().join("fifo");
            fifo(&path);
            let (source, destination) = fifo_flow(&path, source_fifo);
            let cancel = CancellationToken::new();
            let mut flow =
                Flow::prepare_or_start(source, destination, 64, cancel.clone(), Span::default())
                    .unwrap();
            // No await has yielded to the spawned task yet. The O_PATH lease
            // keeps the unlinked generation alive while the pathname is reused.
            fs::remove_file(&path).unwrap();
            fifo(&path);
            let result = tokio::time::timeout(std::time::Duration::from_secs(2), flow.join()).await;
            if result.is_err() {
                cancel.cancel();
                let _ = flow.join().await;
            }
            let error = result
                .expect("a replacement FIFO must be rejected before opening it")
                .unwrap_err();
            assert!(error.report.help.iter().any(|help| help.contains("/stop")));
            assert!(
                error
                    .cause
                    .unwrap()
                    .to_string()
                    .contains("FIFO identity changed")
            );
        }
    }

    #[cfg(any(target_os = "linux", target_os = "android", target_os = "freebsd"))]
    #[tokio::test]
    async fn an_old_block_fifo_pin_is_checked_by_the_actual_stream_open() {
        let root = fixture();
        let path = root.path().join("fifo");
        fifo(&path);
        let (source, destination) = fifo_flow(&path, true);
        let pins = prepare(&source, &destination, 64, Span::default()).unwrap();
        fs::remove_file(&path).unwrap();
        fifo(&path);
        let mut flow = Flow::start_with_pins(
            source,
            destination,
            64,
            CancellationToken::new(),
            Span::default(),
            pins,
        )
        .unwrap();
        let error = flow
            .join()
            .await
            .expect_err("a stale endpoint cannot open a different FIFO");
        assert!(
            error
                .cause
                .unwrap()
                .to_string()
                .contains("FIFO identity changed")
        );
    }

    #[cfg(any(target_os = "linux", target_os = "android", target_os = "freebsd"))]
    fn fifo_flow(path: &Path, source_fifo: bool) -> (Source, Destination) {
        if source_fifo {
            return (
                Source::Endpoint(local(path)),
                Destination::Writer {
                    writer: Box::pin(tokio::io::sink()),
                    closed: CancellationToken::new(),
                },
            );
        }
        (
            Source::Literal(Arc::from("must not reach the new FIFO")),
            Destination::Endpoint {
                location: local(path),
                mode: DestinationMode::Exact,
            },
        )
    }

    #[cfg(any(target_os = "linux", target_os = "android", target_os = "freebsd"))]
    fn fifo(path: &Path) {
        use std::os::unix::ffi::OsStrExt;

        let name = std::ffi::CString::new(path.as_os_str().as_bytes()).unwrap();
        // SAFETY: The exclusively owned fixture path is live and NUL-terminated.
        assert_eq!(unsafe { libc::mkfifo(name.as_ptr(), 0o600) }, 0);
    }

    #[cfg(windows)]
    #[tokio::test]
    async fn acquisition_does_not_open_a_named_pipe_waiting_for_a_peer() {
        let root = fixture();
        let scratch = root.path().join("not-created");
        let location = Location {
            backend: Backend::Local,
            path: r"\\.\pipe\msync-unpaired-acquisition-test".into(),
        };
        let source = Source::acquire(&location, &scratch, Span::default())
            .await
            .unwrap();
        assert!(matches!(source, Source::Endpoint(_)));
        assert!(!scratch.exists());
    }

    fn fixture() -> tempfile::TempDir {
        let scratch = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&scratch).unwrap();
        tempfile::tempdir_in(scratch).unwrap()
    }

    fn local(path: &Path) -> Location {
        Location {
            backend: Backend::Local,
            path: path.to_string_lossy().into_owned(),
        }
    }

    struct PausedReader {
        sent: bool,
        waiting: Option<tokio::sync::oneshot::Sender<()>>,
    }

    impl AsyncRead for PausedReader {
        fn poll_read(
            mut self: std::pin::Pin<&mut Self>,
            _: &mut Context<'_>,
            buffer: &mut ReadBuf<'_>,
        ) -> Poll<std::io::Result<()>> {
            if !self.sent {
                buffer.put_slice(b"uncommitted prefix");
                self.sent = true;
                return Poll::Ready(Ok(()));
            }
            if let Some(waiting) = self.waiting.take() {
                let _ = waiting.send(());
            }
            Poll::Pending
        }
    }
}
