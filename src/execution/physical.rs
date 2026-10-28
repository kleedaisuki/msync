//! Physical endpoint classification and bounded byte-flow adapters.
//!
//! A file sink is always redirected to an invocation-owned capture. Only the
//! runtime's version manager may publish that capture at successful source EOF.
//! Physical sockets/pipes never become immutable snapshot entries themselves.

use std::collections::BTreeMap;
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

const FIFO_PROVIDER: &str = "native-unix-fifo";

#[derive(Default)]
pub(super) struct Rendezvous {
    pub proofs: Vec<crate::hir::effects::FifoRendezvousProof>,
    pub observations:
        BTreeMap<crate::hir::effects::EndpointKey, crate::hir::effects::FifoEndpointProof>,
    pub pins: BTreeMap<crate::hir::effects::EndpointKey, crate::streams::FifoIdentity>,
}

pub(super) fn rendezvous(
    program: &crate::hir::Program,
    context: &crate::hir::graph::PlanningContext,
) -> Result<Rendezvous> {
    use crate::hir::effects::{EndpointKey, EndpointRole, FifoEndpointProof, FifoRendezvousProof};
    #[derive(Default)]
    struct Channel {
        writers: Vec<FifoEndpointProof>,
        readers: Vec<FifoEndpointProof>,
        span: Span,
    }
    fn endpoint(
        channels: &mut BTreeMap<String, Channel>,
        instruction: crate::hir::InstructionId,
        location: &Location,
        role: EndpointRole,
        span: Span,
        context: &crate::hir::graph::PlanningContext,
        pins: &mut BTreeMap<EndpointKey, crate::streams::FifoIdentity>,
    ) -> Result<()> {
        let key = EndpointKey { instruction, role };
        if location.backend != Backend::Local || context.host_unstable.contains(&key) {
            return Ok(());
        }
        let Some(identity) =
            crate::streams::fifo_identity(Path::new(&location.path)).map_err(|cause| {
                ExecutionError::caused(
                    format!("cannot identify FIFO {}", location.path),
                    span,
                    cause,
                )
            })?
        else {
            return Ok(());
        };
        identity.require_generation_pin().map_err(|cause| {
            ExecutionError::caused("cannot certify this structured FIFO endpoint's native generation", span, cause)
                .help("Use a backend with a verified metadata-only generation lease; the legacy external-peer byte-flow adapter remains separate from this scheduling certificate.")
        })?;
        let native_id = identity.native_id();
        let proof =
            FifoEndpointProof::verified_fifo(key, location.clone(), FIFO_PROVIDER, &native_id)
                .map_err(|message| ExecutionError::new(message, span))?;
        pins.insert(key, identity);
        let channel = channels.entry(native_id).or_default();
        channel.span = span;
        match role {
            EndpointRole::Source => channel.readers.push(proof),
            EndpointRole::Destination => channel.writers.push(proof),
            EndpointRole::Executable => unreachable!(),
        }
        Ok(())
    }
    fn visit(
        region: &crate::hir::Region,
        context: &crate::hir::graph::PlanningContext,
        result: &mut Rendezvous,
    ) -> Result<()> {
        let mut channels = BTreeMap::<String, Channel>::new();
        for node in &region.instructions {
            if let crate::hir::InstructionKind::Transaction { body, .. } = &node.kind {
                visit(body, context, result)?;
            }
            let crate::hir::InstructionKind::Transfer {
                source,
                destination,
            } = &node.kind
            else {
                continue;
            };
            let facts = context
                .operation_facts
                .get(&node.id)
                .copied()
                .unwrap_or_default();
            if let Input::File {
                location,
                pattern: None,
            } = source
                && facts.source == FileClass::Stream
            {
                endpoint(
                    &mut channels,
                    node.id,
                    location,
                    EndpointRole::Source,
                    node.span,
                    context,
                    &mut result.pins,
                )?;
            }
            if let Output::File {
                location,
                mode: DestinationMode::Exact,
            } = destination
                && facts.destination == FileClass::Stream
            {
                endpoint(
                    &mut channels,
                    node.id,
                    location,
                    EndpointRole::Destination,
                    node.span,
                    context,
                    &mut result.pins,
                )?;
            }
        }
        for channel in channels.into_values() {
            for endpoint in channel.writers.iter().chain(&channel.readers) {
                result.observations.insert(endpoint.key(), endpoint.clone());
            }
            if channel.writers.is_empty() || channel.readers.is_empty() {
                continue;
            }
            if channel.writers.len() != channel.readers.len() {
                return Err(ExecutionError::new("a FIFO rendezvous has unequal writer and reader counts", channel.span)
                    .help("Use one writer and one reader per FIFO wave, or give independent flows distinct FIFOs."));
            }
            for (writer, reader) in channel.writers.into_iter().zip(channel.readers) {
                result.proofs.push(
                    FifoRendezvousProof::connect(writer, reader)
                        .map_err(|message| ExecutionError::new(message, channel.span))?,
                );
            }
        }
        Ok(())
    }
    let mut result = Rendezvous::default();
    visit(&program.body, context, &mut result)?;
    Ok(result)
}

async fn transfer(
    operation: CopyOp,
    kinds: OperationKinds,
    buffer: usize,
    cancel: CancellationToken,
    fifo_pins: crate::streams::FifoPins,
) -> std::result::Result<FlowReport, crate::runtime::RuntimeError> {
    if fifo_pins.any() {
        return crate::streams::transfer_with_fifo_pins(
            &operation,
            buffer,
            cancel,
            crate::telemetry::context(),
            fifo_pins,
        )
        .await;
    }
    crate::io::transfer_known(
        &operation,
        kinds,
        buffer,
        cancel,
        crate::telemetry::context(),
    )
    .await
}

#[derive(Clone, Debug)]
pub(super) struct Spec {
    pub source: Option<Location>,
    pub destination: Location,
    pub kinds: OperationKinds,
    pub managed: Option<(PathBuf, DestinationMode)>,
    pub basename: Option<OsString>,
    pub fifo_pins: crate::streams::FifoPins,
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
    let fifo = |location: &Location, kind| {
        if kind != FileClass::Stream || location.backend != Backend::Local {
            return Ok(None);
        }
        crate::streams::fifo_identity(Path::new(&location.path)).map_err(|cause| {
            ExecutionError::caused("cannot identify the physical FIFO endpoint", span, cause)
        })
    };
    let fifo_pins = crate::streams::FifoPins {
        source: source_location
            .as_ref()
            .map(|location| fifo(location, source_kind))
            .transpose()?
            .flatten(),
        destination: fifo(&destination, destination_kind)?,
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
        fifo_pins,
        span,
    }))
}

pub(super) fn source_stream(location: &Location, span: Span) -> Result<Option<Location>> {
    let (location, kind) = location_kind(location, true, span)?;
    Ok((kind == FileClass::Stream).then_some(location))
}

pub(super) fn verify_fifo_inputs(
    instruction: crate::hir::InstructionId,
    source: &Input,
    destination: &Output,
    pins: &BTreeMap<crate::hir::effects::EndpointKey, crate::streams::FifoIdentity>,
    span: Span,
) -> Result<()> {
    use crate::hir::effects::{EndpointKey, EndpointRole};
    let source = match source {
        Input::File { location, .. } => Some((EndpointRole::Source, location)),
        _ => None,
    };
    let destination = match destination {
        Output::File { location, .. } => Some((EndpointRole::Destination, location)),
        _ => None,
    };
    for (role, location) in source.into_iter().chain(destination) {
        let Some(expected) = pins.get(&EndpointKey { instruction, role }) else {
            continue;
        };
        let actual = crate::streams::fifo_identity(Path::new(&location.path)).map_err(|cause| {
            ExecutionError::caused(
                format!("cannot validate FIFO {}", location.path),
                span,
                cause,
            )
        })?;
        if actual.as_ref() != Some(expected) {
            return Err(ExecutionError::new(format!("prepared FIFO {} changed before admission", location.path), span)
                .help("Restore the original FIFO or submit a fresh plan. No bytes were transferred by this operation."));
        }
    }
    Ok(())
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
        let entry = item.map_err(|cause| {
            ExecutionError::caused("cannot inspect namespace entry", Span::default(), cause)
        })?;
        let kind = entry.file_type().map_err(|cause| {
            ExecutionError::caused("cannot inspect namespace entry", Span::default(), cause)
        })?;
        if kind.is_dir() && has_live_children(&entry.path())? {
            return Ok(true);
        }
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
    pub fn bind_rendezvous(
        &mut self,
        instruction: crate::hir::InstructionId,
        proofs: &[crate::hir::effects::FifoRendezvousProof],
        pins: &BTreeMap<crate::hir::effects::EndpointKey, crate::streams::FifoIdentity>,
    ) -> Result<()> {
        use crate::hir::effects::{EndpointKey, EndpointRole};
        let bind = |role, observed: Option<&crate::streams::FifoIdentity>| {
            let Some(retained) = pins.get(&EndpointKey { instruction, role }) else {
                return Ok(observed.cloned());
            };
            if observed != Some(retained) {
                return Err(ExecutionError::new("the prepared FIFO endpoint identity changed before admission", self.span)
                    .help("No bytes were transferred by this operation. Restore the original FIFO or submit a fresh plan for its replacement."));
            }
            Ok(Some(retained.clone()))
        };
        self.fifo_pins = crate::streams::FifoPins {
            source: bind(EndpointRole::Source, self.fifo_pins.source.as_ref())?,
            destination: bind(
                EndpointRole::Destination,
                self.fifo_pins.destination.as_ref(),
            )?,
        };
        for endpoint in proofs
            .iter()
            .flat_map(|proof| [proof.writer(), proof.reader()])
        {
            if endpoint.key().instruction != instruction {
                continue;
            }
            let pin = match endpoint.key().role {
                EndpointRole::Source => self.fifo_pins.source.as_ref(),
                EndpointRole::Destination => self.fifo_pins.destination.as_ref(),
                EndpointRole::Executable => None,
            };
            if endpoint.provider() != FIFO_PROVIDER
                || pin.is_none_or(|pin| pin.native_id() != endpoint.identity())
            {
                return Err(ExecutionError::new("the FIFO rendezvous identity changed after preparation", self.span)
                    .help("No bytes were transferred by this operation. Restore the original FIFO or submit a fresh plan for its replacement."));
            }
        }
        Ok(())
    }

    pub async fn run(
        self,
        source: Source,
        capture: Option<PathBuf>,
        buffer: usize,
        cancel: CancellationToken,
    ) -> Result<FlowReport> {
        let fifo_pins = self.fifo_pins;
        let destination = match capture {
            Some(path) => Location {
                backend: Backend::Local,
                path: path.to_string_lossy().into_owned(),
            },
            None => self.destination,
        };
        let result = match source {
            Source::Endpoint(source) => {
                transfer(
                    CopyOp {
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
                    fifo_pins,
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
                let result = transfer(operation, self.kinds, buffer, cancel, fifo_pins).await;
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
                    fifo_pins.destination,
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
                    fifo_pins.destination,
                )
                .await
            }
        }
        .map_err(|cause| ExecutionError::caused("physical byte flow failed", self.span, cause))?;
        if result.end == FlowEnd::Cancelled {
            return Err(ExecutionError::cancelled(
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
    fifo: Option<crate::streams::FifoIdentity>,
) -> Result<FlowReport> {
    let result = crate::streams::transfer_to_writer(
        &source,
        writer,
        buffer,
        cancel,
        span,
        consumer_closed,
        fifo,
    )
    .await
    .map_err(|cause| ExecutionError::caused("physical input flow failed", span, cause))?;
    if result.end == FlowEnd::Cancelled {
        return Err(ExecutionError::cancelled(
            "physical input flow cancelled",
            span,
        ));
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
