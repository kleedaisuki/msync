//! Physical endpoint classification and bounded byte-flow adapters.
//!
//! A file sink is always redirected to an invocation-owned capture. Only the
//! runtime's version manager may publish that capture at successful source EOF.
//! Physical sockets/pipes never become immutable snapshot entries themselves.

use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use tokio_util::sync::CancellationToken;

use crate::compiler::CopyOp;
use crate::endpoint::{Backend, Location};
use crate::hir::{Input, Output};
use crate::io::{FlowEnd, FlowReport};
use crate::language::DestinationMode;
use crate::plan::{FileClass, OperationKinds};
use crate::syntax::Span;
use crate::transaction::filesystem::{Entry, Snapshot};

use super::error::{ExecutionError, Result};
use super::program::{Reader, Writer};

#[derive(Clone, Debug)]
pub(super) struct Spec {
    pub source: Option<Location>,
    pub destination: Location,
    pub kinds: OperationKinds,
    pub managed: Option<(PathBuf, DestinationMode)>,
    pub basename: Option<OsString>,
    pub span: Span,
}

/// Resolves ambient local namespaces once. Remote probing is stat-only and does
/// not connect to FIFO/socket payloads or consume an accepted peer connection.
pub(super) fn inspect(source: &Input, output: &Output, span: Span) -> Result<Option<Spec>> {
    let Output::File { location, mode } = output else {
        return Ok(None);
    };
    let (destination, destination_kind) = location_kind(location, false, span)?;
    let mode = if matches!(destination.backend, Backend::Tcp { .. }) {
        DestinationMode::Exact
    } else {
        *mode
    };
    let (source_location, source_kind) = match source {
        Input::File { location, .. } => {
            let (location, kind) = location_kind(location, true, span)?;
            (Some(location), kind)
        }
        Input::Literal(_) => (None, FileClass::Regular),
        Input::Port { .. } => (None, FileClass::Stream),
    };
    let source_stream = source_location.is_some() && source_kind == FileClass::Stream;
    if !source_stream && destination_kind != FileClass::Stream {
        return Ok(None);
    }
    if matches!(
        source,
        Input::File {
            pattern: Some(_),
            ..
        }
    ) {
        return Err(ExecutionError::new(
            "a physical byte flow cannot expand a directory selector",
            span,
        ));
    }
    if source_kind == FileClass::Directory
        || destination_kind == FileClass::Directory && mode != DestinationMode::Container
    {
        return Err(ExecutionError::new(
            "physical byte flows require non-directory endpoints",
            span,
        ));
    }
    if mode == DestinationMode::ReplaceTree {
        return Err(ExecutionError::new(
            "directory replacement cannot target a physical byte flow",
            span,
        ));
    }
    let basename = source_location.as_ref().and_then(|location| {
        if matches!(location.backend, Backend::Tcp { .. }) {
            return None;
        }
        Path::new(&location.path).file_name().map(OsString::from)
    });
    if mode == DestinationMode::Container && basename.is_none() {
        return Err(ExecutionError::new(
            "an unnamed byte stream needs an exact file destination",
            span,
        )
        .help("Name the output file explicitly rather than appending '/'."));
    }
    let managed = if destination_kind != FileClass::Stream && destination.backend == Backend::Local
    {
        Some((
            super::domain::normalize(Path::new(&destination.path), span)?,
            mode,
        ))
    } else {
        None
    };
    Ok(Some(Spec {
        source: source_location,
        destination,
        kinds: OperationKinds {
            source: source_kind,
            destination: destination_kind,
        },
        managed,
        basename,
        span,
    }))
}

pub(super) fn source_stream(location: &Location, span: Span) -> Result<Option<Location>> {
    let (location, kind) = location_kind(location, true, span)?;
    Ok((kind == FileClass::Stream).then_some(location))
}

pub(super) fn has_live_children(path: &Path) -> Result<bool> {
    if !path.is_dir() {
        return Ok(false);
    }
    for item in std::fs::read_dir(path).map_err(|cause| {
        ExecutionError::caused(
            "cannot inspect physical output namespace",
            Span::default(),
            cause,
        )
    })? {
        let kind = item.and_then(|entry| entry.file_type()).map_err(|cause| {
            ExecutionError::caused("cannot inspect namespace entry", Span::default(), cause)
        })?;
        if !kind.is_file() && !kind.is_dir() && !kind.is_symlink() {
            return Ok(true);
        }
    }
    Ok(false)
}

fn location_kind(location: &Location, source: bool, span: Span) -> Result<(Location, FileClass)> {
    let resolved = match crate::runtime::local_path(location, source)
        .map_err(|message| ExecutionError::new(message, span))?
    {
        Some(path) => Location {
            backend: Backend::Local,
            path: std::path::absolute(path)
                .map_err(|cause| {
                    ExecutionError::caused("cannot resolve physical endpoint", span, cause)
                })?
                .to_string_lossy()
                .into_owned(),
        },
        None => location.clone(),
    };
    let kind = if matches!(resolved.backend, Backend::Wsl | Backend::Ssh { .. }) {
        crate::runtime::inspect_locations_live(
            &resolved.backend,
            std::slice::from_ref(&resolved.path),
        )
        .map_err(|message| ExecutionError::new(message, span))?
        .pop()
        .ok_or_else(|| ExecutionError::new("physical endpoint metadata is missing", span))?
        .map_err(|message| ExecutionError::new(message, span))?
    } else {
        crate::runtime::inspect_location(&resolved, source)
            .map_err(|message| ExecutionError::new(message, span))?
    };
    Ok((resolved, kind))
}

pub(super) enum Source {
    Endpoint(Location),
    Snapshot(Snapshot),
    Literal(Arc<str>),
    Reader(Reader),
}

impl Spec {
    pub async fn run(
        self,
        source: Source,
        capture: Option<PathBuf>,
        buffer: usize,
        cancel: CancellationToken,
    ) -> Result<FlowReport> {
        let destination = match capture {
            Some(path) => Location {
                backend: Backend::Local,
                path: path.to_string_lossy().into_owned(),
            },
            None => self.destination,
        };
        let result = match source {
            Source::Endpoint(source) => {
                crate::io::transfer_known(
                    &CopyOp {
                        source,
                        destination,
                        pattern: None,
                        span: self.span,
                    },
                    OperationKinds {
                        source: self.kinds.source,
                        destination: if self.managed.is_some() {
                            FileClass::Missing
                        } else {
                            self.kinds.destination
                        },
                    },
                    buffer,
                    cancel,
                    crate::telemetry::context(),
                )
                .await
            }
            Source::Snapshot(snapshot) => {
                let Some(Entry::File { artifact, .. }) = snapshot.entries().get(Path::new(""))
                else {
                    return Err(ExecutionError::new(
                        "a physical output needs one ordinary file source",
                        self.span,
                    ));
                };
                // Keep the immutable artifact pinned while retaining the native
                // file-to-socket fast path rather than erasing it into a reader.
                let operation = CopyOp {
                    source: Location {
                        backend: Backend::Local,
                        path: artifact.path().to_string_lossy().into_owned(),
                    },
                    destination,
                    pattern: None,
                    span: self.span,
                };
                let result = crate::io::transfer_known(
                    &operation,
                    self.kinds,
                    buffer,
                    cancel,
                    crate::telemetry::context(),
                )
                .await;
                drop(snapshot);
                result
            }
            Source::Literal(text) => {
                crate::streams::transfer_from_reader(
                    LiteralReader { text, position: 0 },
                    &destination,
                    buffer,
                    cancel,
                    self.span,
                )
                .await
            }
            Source::Reader(reader) => {
                crate::streams::transfer_from_reader(
                    reader,
                    &destination,
                    buffer,
                    cancel,
                    self.span,
                )
                .await
            }
        }
        .map_err(|cause| ExecutionError::caused("physical byte flow failed", self.span, cause))?;
        if result.end == FlowEnd::Cancelled {
            return Err(ExecutionError::new(
                "physical byte flow cancelled; unpublished file data was discarded",
                self.span,
            ));
        }
        Ok(result)
    }
}

pub(super) async fn to_writer(
    source: Location,
    writer: Writer,
    buffer: usize,
    cancel: CancellationToken,
    span: Span,
    consumer_closed: CancellationToken,
) -> Result<FlowReport> {
    let result =
        crate::streams::transfer_to_writer(&source, writer, buffer, cancel, span, consumer_closed)
            .await
            .map_err(|cause| ExecutionError::caused("physical input flow failed", span, cause))?;
    if result.end == FlowEnd::Cancelled {
        return Err(ExecutionError::new("physical input flow cancelled", span));
    }
    Ok(result)
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
