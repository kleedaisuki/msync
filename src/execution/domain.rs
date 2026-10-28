use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsString;
use std::fs;
use std::path::{Component, Path, PathBuf};

use crate::compiler::CopyOp;
use crate::endpoint::{Backend, Location};
use crate::hir::effects::{EndpointKey, EndpointRole};
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
    Captured {
        snapshot: Snapshot,
        basename: Option<OsString>,
    },
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
        Self::resolve_deferred(program, options, &BTreeSet::new())
    }

    /// Resolve read enrollment without using a producer-invalidated host path
    /// as a final endpoint identity. Start/publication must still normalize it.
    /// Deferral is operand-specific and independent of its logical file kind.
    pub fn resolve_deferred(
        program: &Program,
        options: &Options,
        unstable: &BTreeSet<EndpointKey>,
    ) -> Result<Self> {
        let mut domain = Self::default();
        domain.region(&program.body, options, unstable)?;
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

    fn region(
        &mut self,
        region: &Region,
        options: &Options,
        unstable: &BTreeSet<EndpointKey>,
    ) -> Result<()> {
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
                    let deferred_destination = unstable.contains(&EndpointKey {
                        instruction: instruction.id,
                        role: EndpointRole::Destination,
                    });
                    let destination = if deferred_destination {
                        deferred_local(location, false, instruction.span)?
                    } else {
                        local(location, false, instruction.span)?
                    };
                    self.roots.push(if deferred_destination {
                        deferred_envelope(&destination, instruction.span)?
                    } else {
                        destination_envelope(&destination, instruction.span)?
                    });
                    let (source, pattern) = match source {
                        Input::Literal(text) => (Source::Literal(text.clone()), None),
                        Input::File { location, pattern } => {
                            let source = match crate::runtime::local_path(location, true)
                                .map_err(|message| ExecutionError::new(message, instruction.span))?
                            {
                                Some(path) => {
                                    let deferred_source = unstable.contains(&EndpointKey {
                                        instruction: instruction.id,
                                        role: EndpointRole::Source,
                                    });
                                    let path = if deferred_source {
                                        let path = raw_absolute(&path, instruction.span)?;
                                        self.roots
                                            .push(deferred_envelope(&path, instruction.span)?);
                                        path
                                    } else {
                                        let path = normalize(&path, instruction.span)?;
                                        self.roots.push(envelope(&path));
                                        path
                                    };
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
                InstructionKind::Transaction { body, .. } => {
                    self.region(body, options, unstable)?
                }
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
    let native = native_path(path);
    let path = native.as_path();
    super::native::validate_case_policy(path, span)?;
    crate::runtime::check_ancestors(path).map_err(|message| ExecutionError::new(message, span))?;
    validate_directory_ancestors(path, span)?;
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

fn validate_directory_ancestors(path: &Path, span: Span) -> Result<()> {
    for ancestor in path.ancestors().skip(1) {
        match fs::symlink_metadata(ancestor) {
            Ok(metadata) if !metadata.is_dir() => {
                return Err(ExecutionError::caused(
                    format!(
                        "resource ancestor is not a directory: {}",
                        ancestor.display()
                    ),
                    span,
                    std::io::Error::from(std::io::ErrorKind::NotADirectory),
                ));
            }
            Ok(_) => {}
            // Windows may report a child below an ordinary file as missing.
            // Continue upward to identify that actual non-directory ancestor.
            Err(cause)
                if matches!(
                    cause.kind(),
                    std::io::ErrorKind::NotFound | std::io::ErrorKind::NotADirectory
                ) => {}
            Err(cause) => {
                return Err(ExecutionError::caused(
                    format!("cannot inspect resource ancestor {}", ancestor.display()),
                    span,
                    cause,
                ));
            }
        }
    }
    Ok(())
}

/// Keep the unresolved spelling until the producer's import gate. In particular,
/// `..` after a currently non-directory ancestor is not yet a lexical rewrite.
pub(super) fn deferred_local(location: &Location, source: bool, span: Span) -> Result<PathBuf> {
    let path = crate::runtime::local_path(location, source)
        .map_err(|message| ExecutionError::new(message, span))?
        .ok_or_else(|| ExecutionError::new("a local resource was expected", span))?;
    raw_absolute(&path, span)
}

fn raw_absolute(path: &Path, span: Span) -> Result<PathBuf> {
    let native = native_path(path);
    let path = native.as_path();
    if path.as_os_str().as_encoded_bytes().contains(&0) {
        return Err(ExecutionError::new(
            "file path contains a NUL character",
            span,
        ));
    }
    if path.is_absolute() {
        return Ok(path.to_owned());
    }
    std::env::current_dir()
        .map(|cwd| cwd.join(path))
        .map_err(|cause| ExecutionError::caused("cannot resolve deferred endpoint", span, cause))
}

fn native_path(path: &Path) -> PathBuf {
    #[cfg(windows)]
    {
        PathBuf::from(path.to_string_lossy().replace('/', "\\"))
    }
    #[cfg(not(windows))]
    {
        path.to_owned()
    }
}

/// Select only an existing, verified directory for read enrollment. This is not
/// endpoint admission: unknown suffixes may change kind, and their final native
/// traversal/case/alias checks belong after the typed producer dependency gate.
pub(super) fn deferred_envelope(path: &Path, span: Span) -> Result<PathBuf> {
    let absolute = raw_absolute(path, span)?;
    let mut directory = None;
    // Borrow original prefixes: rebuilding Windows verbatim paths with push()
    // would collapse `..` before its preceding component becomes traversable.
    for prefix in absolute.ancestors().collect::<Vec<_>>().into_iter().rev() {
        let metadata = match fs::symlink_metadata(prefix) {
            Ok(metadata) => metadata,
            Err(cause)
                if matches!(
                    cause.kind(),
                    std::io::ErrorKind::NotFound | std::io::ErrorKind::NotADirectory
                ) =>
            {
                break;
            }
            Err(cause) => {
                return Err(ExecutionError::caused(
                    format!("cannot inspect enrollment directory {}", prefix.display()),
                    span,
                    cause,
                ));
            }
        };
        crate::runtime::check_ancestors(prefix)
            .map_err(|message| ExecutionError::new(message, span))?;
        if !metadata.is_dir() {
            break;
        }
        directory = Some(prefix);
    }
    let directory = directory
        .filter(|path| path.file_name().is_some())
        .ok_or_else(|| {
            ExecutionError::new(
                "a deferred endpoint needs an existing managed directory below the filesystem root",
                span,
            )
        })?;
    normalize(directory, span)
}

fn envelope(path: &Path) -> PathBuf {
    let mut root = path;
    while root.parent().is_some_and(|parent| !parent.exists()) {
        root = root.parent().unwrap();
    }
    root.to_owned()
}

fn destination_envelope(path: &Path, span: Span) -> Result<PathBuf> {
    if path.exists() {
        return Ok(envelope(path));
    }
    let mut parent = path
        .parent()
        .ok_or_else(|| ExecutionError::new("destination needs a containing directory", span))?;
    while !parent.exists() {
        parent = parent.parent().ok_or_else(|| {
            ExecutionError::new("destination has no existing containing directory", span)
        })?;
    }
    if parent.file_name().is_none() {
        return Err(ExecutionError::new(
            "a new resource at a filesystem root needs an explicit managed directory",
            span,
        ));
    }
    Ok(parent.to_owned())
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

#[cfg(test)]
#[path = "domain_tests.rs"]
mod tests;
