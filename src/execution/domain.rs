use std::collections::BTreeMap;
use std::ffi::OsString;
use std::fs;
use std::path::{Component, Path, PathBuf};

use crate::compiler::CopyOp;
use crate::endpoint::{Backend, Location};
use crate::hir::{
    Input, InstructionId, InstructionKind, Output, Program, Region, RollbackScope, TransactionId,
};
use crate::plan::FileClass;
use crate::syntax::Span;
use crate::transaction::filesystem::{Snapshot, SnapshotStore};

use super::Options;
use super::error::{ExecutionError, Result};

#[derive(Debug)]
pub(super) enum Source {
    Local(PathBuf),
    Remote {
        location: Location,
        basename: Option<OsString>,
    },
    Literal(std::sync::Arc<str>),
}

#[derive(Debug)]
pub(super) struct Transfer {
    pub source: Source,
    pub destination: PathBuf,
    pub mode: crate::language::DestinationMode,
    pub pattern: Option<String>,
    pub span: Span,
}

#[derive(Debug, Default)]
pub(super) struct Domain {
    pub transfers: BTreeMap<InstructionId, Transfer>,
    pub roots: Vec<PathBuf>,
}

/// Reject unsupported effects before allocating stages, downloading or publishing.
pub(super) fn validate(program: &Program) -> Result<()> {
    fn walk(region: &Region, transactional: bool) -> Result<()> {
        for instruction in &region.instructions {
            match &instruction.kind {
                InstructionKind::Transfer { source, destination } => {
                    if matches!(source, Input::Port { .. }) || matches!(destination, Output::Stdin(_)) {
                        return Err(ExecutionError::new("the file reference executor does not service program ports", instruction.span)
                            .help("Use the process-resource execution adapter; a native fallback would bypass transaction isolation."));
                    }
                    if let Output::File { location, .. } = destination
                        && !matches!(location.backend, Backend::Local | Backend::Env)
                    {
                        return Err(ExecutionError::new(format!("transactional writes to {} need an endpoint publication adapter", location.backend), instruction.span)
                            .help("No program or transfer has started. Remote destination transactions remain an integration obligation; this executor never silently publishes them outside the transaction."));
                    }
                    if let Input::File { location, .. } = source
                        && matches!(location.backend, Backend::Tcp { .. })
                    {
                        return Err(ExecutionError::new("a consuming socket requires the stream execution adapter", instruction.span));
                    }
                    if transactional && let Input::File { location, .. } = source
                        && matches!(location.backend, Backend::Wsl | Backend::Ssh { .. } | Backend::Http { .. })
                    {
                        return Err(ExecutionError::new("remote transaction reads require a versioned endpoint view", instruction.span)
                            .help("Finite remote sources are supported for atomic top-level transfers; named transactions need endpoint identity and version mediation before remote aliases can participate safely."));
                    }
                }
                InstructionKind::Transaction { body, .. } => walk(body, true)?,
                InstructionKind::Spawn { .. } => return Err(ExecutionError::new("program launch requires a verified process-resource view", instruction.span)
                    .help("No effects have started. Native program lifetime management alone is not transaction isolation.")),
                _ => {}
            }
        }
        Ok(())
    }
    walk(&program.body, false)?;
    structure(
        &program.body,
        None,
        &mut std::collections::BTreeSet::new(),
        program,
    )?;
    let graph = crate::hir::graph::build(program);
    if let Some(issue) = graph.issues.first() {
        return Err(ExecutionError::new(&issue.message, Span::default()));
    }
    Ok(())
}

fn structure(
    region: &Region,
    current: Option<TransactionId>,
    named: &mut std::collections::BTreeSet<TransactionId>,
    program: &Program,
) -> Result<()> {
    for node in &region.instructions {
        let valid = match &node.kind {
            InstructionKind::Transaction { transaction, body } => {
                if current.is_some()
                    || !named.insert(*transaction)
                    || program
                        .transactions
                        .iter()
                        .filter(|info| info.id == *transaction)
                        .count()
                        != 1
                {
                    return Err(ExecutionError::new(
                        "named transaction needs one top-level origin and one metadata record",
                        node.span,
                    ));
                }
                structure(body, Some(*transaction), named, program)?;
                true
            }
            InstructionKind::Commit { transaction }
            | InstructionKind::Rollback {
                transaction,
                scope: RollbackScope::Checkpoint,
            } => current == Some(*transaction),
            InstructionKind::Rollback {
                transaction,
                scope: RollbackScope::Begin,
            } => current.is_none() && named.contains(transaction),
            _ => true,
        };
        if !valid {
            return Err(ExecutionError::new(
                "transaction control has no matching preceding context",
                node.span,
            ));
        }
    }
    Ok(())
}

impl Domain {
    pub fn site(&self, root: &Path) -> Span {
        self.transfers
            .values()
            .find(|transfer| {
                transfer.destination.starts_with(root)
                    || matches!(&transfer.source, Source::Local(source) if source.starts_with(root))
            })
            .map_or(Span::default(), |transfer| transfer.span)
    }

    pub fn resolve(program: &Program, options: &Options) -> Result<Self> {
        let mut domain = Self::default();
        domain.region(&program.body, options)?;
        domain.roots.sort_by_key(|root| root.components().count());
        let mut roots = Vec::<PathBuf>::new();
        for path in &domain.roots {
            if roots.iter().any(|root| relative_to(path, root).is_some()) {
                continue;
            }
            roots.push(path.clone());
        }
        domain.roots = roots;
        Ok(domain)
    }

    fn region(&mut self, region: &Region, options: &Options) -> Result<()> {
        for instruction in &region.instructions {
            options.check(instruction.span)?;
            match &instruction.kind {
                InstructionKind::Transfer {
                    source,
                    destination,
                } => {
                    let Output::File { location, mode } = destination else {
                        unreachable!("capabilities validated")
                    };
                    let destination = local(location, false, instruction.span)?;
                    self.roots.push(envelope(&destination));
                    let (source, pattern) = match source {
                        Input::Literal(text) => (Source::Literal(text.clone()), None),
                        Input::File { location, pattern } => {
                            let source = match crate::runtime::local_path(location, true)
                                .map_err(|message| ExecutionError::new(message, instruction.span))?
                            {
                                Some(path) => {
                                    let path = normalize(&path, instruction.span)?;
                                    self.roots.push(envelope(&path));
                                    Source::Local(path)
                                }
                                None => Source::Remote {
                                    location: location.clone(),
                                    basename: remote_basename(location),
                                },
                            };
                            (source, pattern.clone())
                        }
                        Input::Port { .. } => unreachable!("capabilities validated"),
                    };
                    self.transfers.insert(
                        instruction.id,
                        Transfer {
                            source,
                            destination,
                            mode: *mode,
                            pattern,
                            span: instruction.span,
                        },
                    );
                }
                InstructionKind::Transaction { body, .. } => self.region(body, options)?,
                _ => {}
            }
        }
        Ok(())
    }
}

pub(super) fn imported(
    location: &Location,
    span: Span,
    imports: &Path,
    assets: &SnapshotStore,
    options: &Options,
    id: InstructionId,
) -> Result<Snapshot> {
    let kind = if matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }) {
        crate::runtime::inspect_locations_live(
            &location.backend,
            std::slice::from_ref(&location.path),
        )
        .map_err(|message| ExecutionError::new(message, span))?
        .into_iter()
        .next()
        .ok_or_else(|| ExecutionError::new("remote metadata receipt is missing", span))?
        .map_err(|message| ExecutionError::new(message, span))?
    } else {
        crate::runtime::inspect_location(location, true)
            .map_err(|message| ExecutionError::new(message, span))?
    };
    if matches!(kind, FileClass::Stream) {
        return Err(ExecutionError::new(
            "remote pipes and sockets require the stream adapter",
            span,
        ));
    }
    let target = imports.join(format!("remote-{}", id.0));
    let op = CopyOp {
        source: location.clone(),
        destination: Location {
            backend: Backend::Local,
            path: target.to_string_lossy().into_owned(),
        },
        pattern: None,
        span,
    };
    crate::runtime::execute_copy_cancellable(
        &op,
        options.limits.buffer_bytes,
        options.cancel.clone(),
    )
    .map_err(|cause| {
        ExecutionError::caused("cannot acquire immutable remote source", span, cause)
    })?;
    assets
        .capture(&target)
        .map_err(|cause| ExecutionError::caused("cannot capture imported source", span, cause))
}

fn remote_basename(location: &Location) -> Option<OsString> {
    location
        .path
        .trim_end_matches(['/', '\\'])
        .rsplit(['/', '\\'])
        .next()
        .filter(|name| !matches!(*name, "" | "." | ".." | "~"))
        .map(OsString::from)
}

fn local(location: &Location, source: bool, span: Span) -> Result<PathBuf> {
    let path = crate::runtime::local_path(location, source)
        .map_err(|message| ExecutionError::new(message, span))?
        .ok_or_else(|| ExecutionError::new("a local resource was expected", span))?;
    normalize(&path, span)
}

pub(super) fn normalize(path: &Path, span: Span) -> Result<PathBuf> {
    super::native::validate_case_policy(path, span)?;
    crate::runtime::check_ancestors(path).map_err(|message| ExecutionError::new(message, span))?;
    let absolute = std::path::absolute(path).map_err(|cause| {
        ExecutionError::caused(format!("cannot resolve {}", path.display()), span, cause)
    })?;
    let mut clean = PathBuf::new();
    for part in absolute.components() {
        match part {
            Component::CurDir => {}
            Component::ParentDir => {
                clean.pop();
            }
            part => clean.push(part.as_os_str()),
        }
    }
    let mut missing = Vec::new();
    let mut existing = clean.as_path();
    while !existing.try_exists().map_err(|cause| {
        ExecutionError::caused(
            format!("cannot inspect {}", existing.display()),
            span,
            cause,
        )
    })? {
        let name = existing
            .file_name()
            .ok_or_else(|| ExecutionError::new("resource has no existing ancestor", span))?;
        missing.push(name.to_owned());
        existing = existing
            .parent()
            .ok_or_else(|| ExecutionError::new("resource has no parent", span))?;
    }
    let mut root = fs::canonicalize(existing).map_err(|cause| {
        ExecutionError::caused(
            format!("cannot identify {}", existing.display()),
            span,
            cause,
        )
    })?;
    for name in missing.into_iter().rev() {
        root.push(name);
    }
    Ok(root)
}

fn envelope(path: &Path) -> PathBuf {
    let mut root = path;
    while root.parent().is_some_and(|parent| !parent.exists()) {
        root = root.parent().unwrap();
    }
    root.to_owned()
}

pub(super) fn relative_to(path: &Path, root: &Path) -> Option<PathBuf> {
    let mut parts = path.components();
    for base in root.components() {
        if !component_eq(parts.next()?.as_os_str(), base.as_os_str()) {
            return None;
        }
    }
    Some(parts.collect())
}

#[cfg(not(windows))]
fn component_eq(left: &std::ffi::OsStr, right: &std::ffi::OsStr) -> bool {
    left == right
}

#[cfg(windows)]
fn component_eq(left: &std::ffi::OsStr, right: &std::ffi::OsStr) -> bool {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Globalization::{CSTR_EQUAL, CompareStringOrdinal};
    let left = left.encode_wide().collect::<Vec<_>>();
    let right = right.encode_wide().collect::<Vec<_>>();
    let (Ok(left_len), Ok(right_len)) = (i32::try_from(left.len()), i32::try_from(right.len()))
    else {
        return false;
    };
    // SAFETY: Explicit UTF-16 slice lengths are checked and buffers stay live.
    unsafe {
        CompareStringOrdinal(left.as_ptr(), left_len, right.as_ptr(), right_len, 1) == CSTR_EQUAL
    }
}
