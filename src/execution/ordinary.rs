//! Statement-local publication without MVCC enrollment or retained undo history.
//!
//! The interpreter selects this owner only before managed history or background
//! work exists. Each statement captures its source and prepares only its selected
//! targets; unrelated cwd trees never enter a resource store. All selector
//! members are validated and staged before publication, and their rename receipts
//! remain reversible until the complete statement succeeds. Earlier completed
//! statements are not part of that compensation cut.
//!
//! Endpoint kinds are execution-time guards. Discovering a physical stream
//! returns `Unsupported` without opening it, so the interpreter can resume the
//! remaining suffix through its flow scheduler instead of replaying prior IO.

use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsString;
use std::fs;
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::time::SystemTime;

use same_file::Handle;

use crate::compiler::CopyOp;
use crate::endpoint::{Backend, Location};
use crate::hir::{Input, Instruction, InstructionId, InstructionKind, Output, Program};
use crate::language::DestinationMode;
use crate::plan::FileClass;
use crate::syntax::Span;
use crate::transaction::filesystem::{
    Entry, FsError, InvocationRoot, NodeMetadata, Publication, Snapshot, SnapshotStore,
};

use super::assignment::{self, Target};
use super::error::{ExecutionError, Result};
use super::progress::{Event, Phase, Progress};
use super::transfer::{Selection, relation};
use super::{CleanupIssue, ExecutionReport, Options};

pub(super) enum Attempt {
    Completed(ExecutionReport),
    Unsupported,
}

pub(super) fn supports(program: &Program) -> bool {
    program.body.instructions.iter().all(|node| {
        matches!(&node.kind,
            InstructionKind::Transfer {
                source: Input::Literal(_),
                destination: Output::File { location, .. },
            } if matches!(location.backend, Backend::Local | Backend::Env))
            || matches!(&node.kind,
                InstructionKind::Transfer {
                    source: Input::File { location: source, .. },
                    destination: Output::File { location, .. },
                } if !matches!(source.backend, Backend::Tcp { .. })
                    && matches!(location.backend, Backend::Local | Backend::Env))
    })
}

pub(super) struct Ordinary {
    options: Options,
    storage: Option<Storage>,
    progress: Option<Progress>,
    origin: Option<crate::source::InstructionOrigin>,
}

struct Storage {
    assets: SnapshotStore,
    imports: PathBuf,
    invocation: InvocationRoot,
    sequence: u64,
}

struct Ready {
    instruction: InstructionId,
    source: Source,
    target: PathBuf,
    mode: DestinationMode,
    pattern: Option<String>,
    span: Span,
}

enum Source {
    Local(PathBuf),
    Literal(std::sync::Arc<str>),
    Remote(Location),
}

struct Assignment {
    target: PathBuf,
    snapshot: Snapshot,
    existed: bool,
}

impl Ordinary {
    pub fn new(options: &Options) -> Self {
        Self {
            options: options.clone(),
            storage: None,
            progress: None,
            origin: None,
        }
    }

    pub fn set_progress(&mut self, progress: Option<Progress>) {
        self.progress = progress;
    }

    pub fn set_origin(&mut self, origin: Option<crate::source::InstructionOrigin>) {
        self.origin = origin;
    }

    /// Refuse physical endpoints before opening a journal or reading payloads.
    pub fn execute(&mut self, node: &Instruction) -> Result<Attempt> {
        self.options.check(node.span)?;
        if let Some(progress) = &self.progress {
            let mut event = Event::new(Phase::Inspect);
            event.instruction = Some(node.id);
            if let InstructionKind::Transfer {
                source,
                destination,
            } = &node.kind
            {
                event.source = Some(match source {
                    Input::File { location, .. } => {
                        format!("{}::{}", location.backend, location.path)
                    }
                    Input::Literal(_) => "<literal>".into(),
                    Input::Port { .. } => "<program port>".into(),
                });
                if let Output::File { location, .. } = destination {
                    event.destination = Some(format!("{}::{}", location.backend, location.path));
                }
            }
            progress.update(event);
        }
        let Some(ready) = Ready::inspect(node, self.origin.as_ref())? else {
            return Ok(Attempt::Unsupported);
        };
        let mut trace = crate::telemetry::span("copy");
        trace.expect_completion();
        trace.attribute("copy.kind", "ordinary-assignment");
        trace.attribute("publication.policy", "statement-journal");
        trace.metric("instruction.id", node.id.0 as u64);
        trace.attribute("destination.backend", "local");
        trace.attribute_lazy("destination.path", || ready.target.display().to_string());
        match &ready.source {
            Source::Local(path) => {
                trace.attribute("source.backend", "local");
                trace.attribute_lazy("source.path", || path.display().to_string());
            }
            Source::Literal(_) => {
                trace.attribute("source.backend", "literal");
                trace.attribute("source.path", "<literal>");
            }
            Source::Remote(location) => {
                trace.attribute(
                    "source.backend",
                    match location.backend {
                        Backend::Wsl => "wsl",
                        Backend::Ssh { .. } => "ssh",
                        Backend::Http { .. } => "http",
                        _ => "remote",
                    },
                );
                trace.attribute("source.path", &location.path);
            }
        }
        let result = (|| {
            if self.storage.is_none() {
                self.storage = Some(Storage::new(&self.options, node.span)?);
            }
            self.storage
                .as_mut()
                .expect("ordinary storage initialized")
                .execute(
                    &ready,
                    &self.options,
                    self.progress.as_ref(),
                    self.origin.as_ref(),
                )
        })();
        let mut result = result.map_err(|error| error.with_operand(self.origin.as_ref(), true));
        if let Ok(Attempt::Completed(report)) = &mut result {
            report.cleanup.extend(self.close());
        }
        trace.finish_result(&result);
        match &result {
            Ok(Attempt::Completed(report)) => {
                trace.attribute("phase", "committed");
                trace.metric("files.assigned", report.assigned_files as u64);
                trace.metric("bytes.assigned", report.assigned_bytes);
            }
            Ok(Attempt::Unsupported) => trace.attribute("phase", "guard-fallback"),
            Err(error) => {
                trace.attribute("phase", "failed");
                trace.record_error("copy.statement", error);
                if error.is_cancelled() {
                    trace.cancelled();
                }
            }
        }
        result
    }

    pub fn close(&mut self) -> Vec<CleanupIssue> {
        let Some(mut storage) = self.storage.take() else {
            return Vec::new();
        };
        let root = storage.invocation.path().to_owned();
        if let Err(cause) = storage.assets.cleanup() {
            return vec![cleanup(cause, root)];
        }
        drop(storage.assets);
        match storage.invocation.cleanup() {
            Ok(()) => Vec::new(),
            Err(cause) => vec![cleanup(cause, root)],
        }
    }
}

impl Ready {
    fn inspect(
        node: &Instruction,
        origin: Option<&crate::source::InstructionOrigin>,
    ) -> Result<Option<Self>> {
        let InstructionKind::Transfer {
            source,
            destination,
        } = &node.kind
        else {
            return Ok(None);
        };
        let Output::File { location, mode } = destination else {
            return Ok(None);
        };
        if !matches!(location.backend, Backend::Local | Backend::Env) {
            return Ok(None);
        }
        let target =
            local(location, false, node.span).map_err(|error| error.with_operand(origin, true))?;
        if metadata(&target, node.span)
            .map_err(|error| error.with_operand(origin, true))?
            .is_some_and(|meta| !meta.is_file() && !meta.is_dir())
        {
            return Ok(None);
        }
        let (source, pattern) = match source {
            Input::Literal(text) => (Source::Literal(text.clone()), None),
            Input::Port { .. } => return Ok(None),
            Input::File { location, pattern } => {
                let Some(source) = Source::inspect(location, node.span)
                    .map_err(|error| error.with_operand(origin, false))?
                else {
                    return Ok(None);
                };
                (source, pattern.clone())
            }
        };
        if pattern.is_some() && *mode != DestinationMode::Container {
            return Err(ExecutionError::new(
                "a selector needs a destination container ending in '/'",
                node.span,
            ));
        }
        Ok(Some(Self {
            instruction: node.id,
            source,
            target,
            mode: *mode,
            pattern,
            span: node.span,
        }))
    }
}

impl Source {
    fn inspect(location: &Location, span: Span) -> Result<Option<Self>> {
        match crate::runtime::local_path(location, true)
            .map_err(|message| ExecutionError::new(message, span))?
        {
            Some(path) => {
                let path = super::domain::normalize(&path, span)?;
                if metadata(&path, span)?.is_some_and(|meta| !meta.is_file() && !meta.is_dir()) {
                    return Ok(None);
                }
                Ok(Some(Self::Local(path)))
            }
            None if matches!(location.backend, Backend::Tcp { .. }) => Ok(None),
            None if matches!(location.backend, Backend::Wsl) => {
                Ok(Some(Self::Remote(location.clone())))
            }
            None => {
                if matches!(remote_kind(location, span)?, FileClass::Stream) {
                    return Ok(None);
                }
                Ok(Some(Self::Remote(location.clone())))
            }
        }
    }
}

impl Storage {
    fn new(options: &Options, span: Span) -> Result<Self> {
        let invocation = InvocationRoot::create(&options.scratch_root)
            .map_err(|cause| failure("cannot create ordinary statement storage", span, cause))?;
        let imports = invocation.path().join("imports");
        let initialized = (|| {
            let assets = SnapshotStore::new_in(&invocation.path().join("assets"), &invocation)
                .map_err(|cause| failure("cannot create ordinary source store", span, cause))?;
            fs::create_dir(&imports).map_err(|cause| {
                ExecutionError::caused("cannot create ordinary imports", span, cause)
            })?;
            Ok(assets)
        })();
        let assets = initialized.map_err(|mut error: ExecutionError| {
            if let Err(cause) = invocation.cleanup() {
                error.retained_paths.push(invocation.path().to_owned());
                error.report.help.push(format!(
                    "cannot clean failed ordinary initialization: {cause}"
                ));
            }
            error
        })?;
        Ok(Self {
            assets,
            imports,
            invocation,
            sequence: 0,
        })
    }

    fn execute(
        &mut self,
        ready: &Ready,
        options: &Options,
        progress: Option<&Progress>,
        origin: Option<&crate::source::InstructionOrigin>,
    ) -> Result<Attempt> {
        observe(progress, ready, Phase::Acquire, &ExecutionReport::default());
        let mut acquire = crate::telemetry::span("ordinary.acquire");
        let selection = self.source(ready, options);
        acquire.finish_result(&selection);
        drop(acquire);
        let Some(selection) = selection.map_err(|error| error.with_operand(origin, false))? else {
            return Ok(Attempt::Unsupported);
        };
        observe(progress, ready, Phase::Stage, &ExecutionReport::default());
        let selections = self
            .select(selection, ready.pattern.as_deref(), ready.span, options)
            .map_err(|error| error.with_operand(origin, false))?;
        let mut assignments = Vec::new();
        let mut report = ExecutionReport::default();
        for selection in selections {
            options.check(ready.span)?;
            let target = target(ready, &selection)?;
            relation(&selection, &target, ready.span)?;
            let assigned = self.assignment(&selection.snapshot, &target, ready)?;
            for entry in selection.snapshot.entries().values() {
                if let Entry::File { artifact, .. } = entry {
                    report.assigned_files += 1;
                    report.assigned_bytes = report.assigned_bytes.saturating_add(artifact.len());
                }
            }
            assignments.push(Assignment {
                existed: metadata(&target, ready.span)?.is_some(),
                target,
                snapshot: assigned,
            });
        }
        observe(progress, ready, Phase::Stage, &report);
        self.publish(&assignments, ready, options, progress, &mut report)?;
        report.assignments = 1;
        Ok(Attempt::Completed(report))
    }

    fn source(&mut self, ready: &Ready, options: &Options) -> Result<Option<Selection>> {
        let (snapshot, basename, source) = match &ready.source {
            Source::Local(path) => (
                self.assets.capture_in(path, &self.invocation),
                path.file_name().map(OsString::from),
                Some(path.clone()),
            ),
            Source::Literal(text) => {
                let path = self.fresh("literal");
                let mut file = fs::OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&path)
                    .map_err(|cause| {
                        ExecutionError::caused("cannot create literal source", ready.span, cause)
                    })?;
                file.write_all(text.as_bytes()).map_err(|cause| {
                    ExecutionError::caused("cannot write UTF-8 literal source", ready.span, cause)
                })?;
                drop(file);
                (self.assets.capture(&path), None, None)
            }
            Source::Remote(location) => {
                let path = self.fresh("remote");
                if matches!(location.backend, Backend::Wsl) {
                    use super::wsl_source::{Acquisition, Request};
                    match super::wsl_source::acquire(
                        Request {
                            source: location,
                            stage: &path,
                            logical_target: &ready.target,
                            mode: ready.mode,
                            selector: ready.pattern.is_some(),
                            invocation: &self.invocation,
                        },
                        options,
                        ready.span,
                    )? {
                        Acquisition::Captured { basename } => {
                            let snapshot = self.assets.capture(&path).map_err(|cause| {
                                failure("cannot capture WSL source", ready.span, cause)
                            })?;
                            return Ok(Some(Selection {
                                snapshot,
                                basename: Some(basename),
                                source: None,
                            }));
                        }
                        Acquisition::Stream => return Ok(None),
                        Acquisition::Fallback => return Ok(None),
                    }
                }
                let op = CopyOp {
                    source: location.clone(),
                    destination: Location {
                        backend: Backend::Local,
                        path: path.to_string_lossy().into_owned(),
                    },
                    pattern: None,
                    span: ready.span,
                };
                crate::runtime::execute_copy_cancellable(
                    &op,
                    options.limits.buffer_bytes,
                    options.cancel.clone(),
                )
                .map_err(|cause| {
                    ExecutionError::caused(
                        "cannot import ordinary remote source",
                        ready.span,
                        cause,
                    )
                })?;
                (self.assets.capture(&path), remote_basename(location), None)
            }
        };
        let snapshot = snapshot
            .map_err(|cause| failure("cannot capture ordinary source", ready.span, cause))?;
        if snapshot.is_missing() {
            return Err(ExecutionError::new(
                format!(
                    "source does not exist: {}",
                    source
                        .as_ref()
                        .map_or_else(|| "remote source".into(), |path| path.display().to_string())
                ),
                ready.span,
            )
            .help("Correct the source path or produce it in an earlier statement."));
        }
        Ok(Some(Selection {
            snapshot,
            basename,
            source,
        }))
    }

    fn fresh(&mut self, kind: &str) -> PathBuf {
        self.sequence += 1;
        self.imports.join(format!("{kind}-{}", self.sequence))
    }

    fn select(
        &self,
        source: Selection,
        pattern: Option<&str>,
        span: Span,
        options: &Options,
    ) -> Result<Vec<Selection>> {
        super::transfer::select_snapshot(
            source,
            pattern,
            span,
            || options.check(span),
            |entries| self.snapshot(entries, span),
        )
    }
    fn assignment(&self, source: &Snapshot, target: &Path, ready: &Ready) -> Result<Snapshot> {
        let old = metadata(target, ready.span)?;
        let old_metadata = old
            .as_ref()
            .map(NodeMetadata::capture)
            .transpose()
            .map_err(|cause| {
                ExecutionError::caused("cannot inspect destination metadata", ready.span, cause)
            })?;
        let merge = if old.as_ref().is_some_and(fs::Metadata::is_dir)
            && ready.mode == DestinationMode::Container
            && source.is_directory()
        {
            Some(
                self.assets
                    .capture_in(target, &self.invocation)
                    .map_err(|cause| {
                        failure("cannot capture container merge target", ready.span, cause)
                    })?,
            )
        } else {
            None
        };
        let target = match old.as_ref() {
            None => Target::Missing,
            Some(meta) if meta.is_dir() => Target::Directory(merge.as_ref()),
            Some(_) => Target::File(old_metadata.as_ref().expect("existing metadata")),
        };
        assignment::assign(
            source,
            target,
            ready.mode,
            matches!(ready.source, Source::Literal(_)),
            ready.span,
            |entries| self.snapshot(entries, ready.span),
        )
    }

    fn snapshot(&self, entries: BTreeMap<PathBuf, Entry>, span: Span) -> Result<Snapshot> {
        self.assets
            .from_entries(entries)
            .map_err(|cause| failure("cannot assemble ordinary assignment", span, cause))
    }

    fn publish(
        &self,
        assignments: &[Assignment],
        ready: &Ready,
        options: &Options,
        progress: Option<&Progress>,
        report: &mut ExecutionReport,
    ) -> Result<()> {
        if assignments.is_empty() {
            return Ok(());
        }
        let mut parents = Parents::default();
        let mut publications = Vec::new();
        let span = ready.span;
        let mut stage = crate::telemetry::span("ordinary.stage");
        let result = self.prepare(assignments, span, options, &mut parents, &mut publications);
        stage.finish_result(&result);
        drop(stage);
        let result = result.and_then(|()| {
            observe(progress, ready, Phase::Publish, report);
            let mut publish = crate::telemetry::span("ordinary.publish");
            let result = publish_all(&mut publications, options, span);
            publish.finish_result(&result);
            result
        });
        if let Err(mut error) = result {
            compensate(&mut publications, &mut error);
            parents.rollback(span, &mut error);
            return Err(error);
        }
        report.published_roots = publications.len();
        for publication in &mut publications {
            if let Err(cause) = publication.finalize() {
                report
                    .cleanup
                    .push(cleanup(cause, publication.journal().to_owned()));
            }
        }
        parents.finish(span, report);
        Ok(())
    }

    fn prepare(
        &self,
        assignments: &[Assignment],
        span: Span,
        options: &Options,
        parents: &mut Parents,
        publications: &mut Vec<Publication>,
    ) -> Result<()> {
        for assignment in assignments {
            options.check(span)?;
            parents.ensure(&assignment.target, assignment.existed, span)?;
            publications.push(
                self.assets
                    .prepare(&assignment.snapshot, &assignment.target)
                    .map_err(|cause| failure("cannot prepare ordinary publication", span, cause))?,
            );
        }
        Ok(())
    }
}

fn observe(progress: Option<&Progress>, ready: &Ready, phase: Phase, report: &ExecutionReport) {
    let Some(progress) = progress else {
        return;
    };
    progress.update(Event {
        phase,
        instruction: Some(ready.instruction),
        source: Some(match &ready.source {
            Source::Local(path) => path.display().to_string(),
            Source::Literal(_) => "<literal>".into(),
            Source::Remote(location) => format!("{}::{}", location.backend, location.path),
        }),
        destination: Some(ready.target.display().to_string()),
        files: report.assigned_files,
        bytes: report.assigned_bytes,
    });
}

#[derive(Default)]
struct Parents {
    observed: BTreeMap<PathBuf, (Handle, NodeMetadata)>,
    changed: BTreeSet<PathBuf>,
    created: Vec<(PathBuf, Handle)>,
}

impl Parents {
    fn ensure(&mut self, target: &Path, existed: bool, span: Span) -> Result<()> {
        let parent = target
            .parent()
            .ok_or_else(|| ExecutionError::new("destination has no parent", span))?;
        self.directory(parent, span)?;
        if !existed {
            self.changed.insert(parent.to_owned());
        }
        Ok(())
    }

    fn directory(&mut self, path: &Path, span: Span) -> Result<()> {
        if self.observed.contains_key(path)
            || self.created.iter().any(|(created, _)| created == path)
        {
            return Ok(());
        }
        match metadata(path, span)? {
            Some(meta) if meta.is_dir() => {
                #[cfg(unix)]
                if meta.permissions().readonly() {
                    return Err(ExecutionError::caused(
                        "destination parent is read-only",
                        span,
                        io::Error::new(io::ErrorKind::PermissionDenied, path.display().to_string()),
                    ));
                }
                let stamp = NodeMetadata::capture(&meta).map_err(|cause| {
                    ExecutionError::caused("cannot retain parent metadata", span, cause)
                })?;
                let identity = Handle::from_path(path).map_err(|cause| {
                    ExecutionError::caused("cannot retain parent identity", span, cause)
                })?;
                self.observed.insert(path.to_owned(), (identity, stamp));
                Ok(())
            }
            Some(_) => Err(ExecutionError::new(
                format!("destination parent is not a directory: {}", path.display()),
                span,
            )),
            None => self.create(path, span),
        }
    }

    fn create(&mut self, path: &Path, span: Span) -> Result<()> {
        let parent = path
            .parent()
            .ok_or_else(|| ExecutionError::new("destination has no existing ancestor", span))?;
        self.directory(parent, span)?;
        fs::create_dir(path).map_err(|cause| {
            ExecutionError::caused("cannot create destination parent", span, cause)
        })?;
        let identity = Handle::from_path(path).map_err(|cause| {
            ExecutionError::caused("cannot retain created parent identity", span, cause)
        })?;
        self.created.push((path.to_owned(), identity));
        self.changed.insert(parent.to_owned());
        Ok(())
    }

    fn rollback(&mut self, span: Span, error: &mut ExecutionError) {
        for (path, identity) in self.created.iter().rev() {
            let result = ensure_identity(path, identity).and_then(|()| fs::remove_dir(path));
            if let Err(cause) = result {
                error.retained_paths.push(path.clone());
                error.report.help.push(format!(
                    "cannot remove created parent {}: {cause}",
                    path.display()
                ));
            }
        }
        self.restore(false, span, &mut |cause, path| {
            error.retained_paths.push(path);
            error.report.help.push(cause.to_string());
        });
    }

    fn finish(&self, span: Span, report: &mut ExecutionReport) {
        self.restore(true, span, &mut |cause, path| {
            report.cleanup.push(CleanupIssue {
                cause: Box::new(cause),
                retained_paths: vec![path],
                resolved: false,
            });
        });
    }

    fn restore(&self, success: bool, span: Span, issue: &mut impl FnMut(ExecutionError, PathBuf)) {
        for (path, (identity, metadata)) in &self.observed {
            let mut metadata = metadata.clone();
            if success && self.changed.contains(path) {
                metadata.modified = SystemTime::now();
            }
            if let Err(cause) = ensure_identity(path, identity).and_then(|()| metadata.apply(path))
            {
                issue(
                    ExecutionError::caused(
                        "cannot settle destination parent metadata",
                        span,
                        cause,
                    ),
                    path.clone(),
                );
            }
        }
    }
}

fn publish_all(publications: &mut [Publication], options: &Options, span: Span) -> Result<()> {
    for publication in publications {
        options.check(span)?;
        publication
            .publish_with(|_| {
                if options.cancel.is_cancelled() {
                    Err(io::Error::new(
                        io::ErrorKind::Interrupted,
                        CancelPublication,
                    ))
                } else {
                    Ok(())
                }
            })
            .map_err(|cause| publication_failure(cause, span))?;
    }
    Ok(())
}

#[derive(Debug)]
struct CancelPublication;

impl std::fmt::Display for CancelPublication {
    fn fmt(&self, output: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        output.write_str("ordinary assignment cancelled")
    }
}

impl std::error::Error for CancelPublication {}

fn publication_failure(cause: FsError, span: Span) -> ExecutionError {
    let cancelled = cause
        .cause
        .get_ref()
        .is_some_and(|cause| cause.is::<CancelPublication>());
    if cancelled && cause.recovery == crate::transaction::filesystem::Recovery::Restored {
        let mut error =
            ExecutionError::cancelled("ordinary assignment cancelled during publication", span);
        error.retained_paths.extend(cause.journal);
        return error;
    }
    failure("ordinary publication failed", span, cause)
}

fn compensate(publications: &mut [Publication], error: &mut ExecutionError) {
    for publication in publications.iter_mut().rev() {
        let journal = publication.journal().to_owned();
        if let Err(cause) = publication.restore_before_image() {
            error.retained_paths.push(publication.journal().to_owned());
            error.compensation.push(cause);
            continue;
        }
        if let Err(cause) = publication.finalize() {
            error.retained_paths.push(publication.journal().to_owned());
            error.compensation.push(cause);
        } else {
            error.retained_paths.retain(|path| path != &journal);
            if let Some(cause) = error
                .cause
                .as_deref_mut()
                .and_then(|cause| cause.downcast_mut::<FsError>())
                && cause.journal.as_ref() == Some(&journal)
            {
                cause.journal = None;
                cause.recovery = crate::transaction::filesystem::Recovery::Restored;
                cause.recovery_error = None;
            }
        }
    }
}

fn target(ready: &Ready, selection: &Selection) -> Result<PathBuf> {
    if ready.mode != DestinationMode::Container {
        return Ok(ready.target.clone());
    }
    let name = selection.basename.as_ref().ok_or_else(|| {
        ExecutionError::new(
            "a literal stream has no basename for container insertion",
            ready.span,
        )
        .help("Write the literal to an exact file path instead of a trailing '/' container.")
    })?;
    Ok(ready.target.join(name))
}

fn metadata(path: &Path, span: Span) -> Result<Option<fs::Metadata>> {
    match fs::symlink_metadata(path) {
        Ok(meta) => {
            let forbidden = meta.file_type().is_symlink();
            #[cfg(windows)]
            let forbidden = {
                use std::os::windows::fs::MetadataExt;
                forbidden || meta.file_attributes() & 0x400 != 0
            };
            if forbidden {
                return Err(ExecutionError::new(
                    format!(
                        "symbolic links and reparse points are not supported: {}",
                        path.display()
                    ),
                    span,
                ));
            }
            Ok(Some(meta))
        }
        Err(cause) if cause.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(cause) => Err(ExecutionError::caused(
            format!("cannot inspect {}", path.display()),
            span,
            cause,
        )),
    }
}

fn local(location: &Location, source: bool, span: Span) -> Result<PathBuf> {
    let path = crate::runtime::local_path(location, source)
        .map_err(|message| ExecutionError::new(message, span))?
        .ok_or_else(|| ExecutionError::new("a local resource was expected", span))?;
    super::domain::normalize(&path, span)
}

fn remote_kind(location: &Location, span: Span) -> Result<FileClass> {
    if matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }) {
        return crate::runtime::inspect_locations_live(
            &location.backend,
            std::slice::from_ref(&location.path),
        )
        .map_err(|message| ExecutionError::new(message, span))?
        .into_iter()
        .next()
        .ok_or_else(|| ExecutionError::new("remote metadata receipt is missing", span))?
        .map_err(|message| ExecutionError::new(message, span));
    }
    crate::runtime::inspect_location(location, true)
        .map_err(|message| ExecutionError::new(message, span))
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

fn ensure_identity(path: &Path, expected: &Handle) -> io::Result<()> {
    let actual = Handle::from_path(path)?;
    if actual != *expected {
        return Err(io::Error::other("destination parent identity changed"));
    }
    Ok(())
}

fn failure(action: &'static str, span: Span, cause: FsError) -> ExecutionError {
    let retained = cause.journal.clone();
    let mut error = ExecutionError::caused(action, span, cause);
    error.retained_paths.extend(retained);
    error
}

fn cleanup(cause: FsError, root: PathBuf) -> CleanupIssue {
    CleanupIssue {
        cause: Box::new(cause),
        retained_paths: vec![root],
        resolved: false,
    }
}

#[cfg(test)]
#[path = "ordinary_tests.rs"]
mod tests;
