//! Statement-local publication without MVCC enrollment or retained undo history.
//!
//! The interpreter selects this owner for finite assignments. Each statement
//! captures its source and prepares only its selected
//! targets; unrelated cwd trees never enter a resource store. All selector
//! members are validated and staged before publication, and their rename receipts
//! remain reversible until the complete statement succeeds. Earlier completed
//! statements are not part of that compensation cut.
//!
//! Endpoint kinds are execution-time guards. Discovering a physical stream
//! returns `Unsupported` without opening it, so the interpreter can resume the
//! remaining suffix through its flow scheduler instead of replaying prior IO.

use std::collections::BTreeMap;
use std::ffi::OsString;
use std::fs;
use std::io::{self, Write};
use std::path::{Path, PathBuf};

use same_file::Handle;

use crate::endpoint::{Backend, Location};
use crate::filesystem::{
    Entry, FsError, InvocationRoot, NodeMetadata, Publication, Snapshot, SnapshotStore,
};
#[cfg(test)]
use crate::hir::Program;
use crate::hir::{Input, Instruction, InstructionId, InstructionKind, Output};
use crate::language::DestinationMode;
use crate::plan::FileClass;
use crate::syntax::Span;

use super::assignment::{self, Target};
use super::error::{ExecutionError, Result};
use super::progress::{Event, Phase, Progress};
use super::transfer::{Selection, relation};
use super::{CleanupIssue, ExecutionReport, Options};

pub(super) enum Attempt {
    Completed(ExecutionReport),
    Unsupported,
}

pub(super) struct Ordinary {
    options: Options,
    storage: Option<Storage>,
    progress: Option<Progress>,
    origin: Option<crate::source::InstructionOrigin>,
}

struct Storage {
    assets: SnapshotStore,
    registry: crate::filesystem::recovery::Registry,
    imports: PathBuf,
    invocation: InvocationRoot,
    capture_root: Option<InvocationRoot>,
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

#[derive(Clone)]
struct Assignment {
    target: PathBuf,
    snapshot: Snapshot,
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

    fn inspect_progress(&self, node: &Instruction) {
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
    }

    /// Refuse physical endpoints before opening a journal or reading payloads.
    pub fn execute(&mut self, node: &Instruction) -> Result<Attempt> {
        self.options.check(node.span)?;
        self.inspect_progress(node);
        let Some(ready) = Ready::inspect(node, self.origin.as_ref())? else {
            return Ok(Attempt::Unsupported);
        };
        let mut trace = copy_trace(&ready, "ordinary-assignment");
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
        let target = crate::runtime::local_path(location, false)
            .map_err(|message| ExecutionError::new(message, node.span).with_operand(origin, true))?
            .ok_or_else(|| ExecutionError::new("a local resource was expected", node.span))?;
        if named_pipe(&target) {
            return Ok(None);
        }
        let target = super::domain::normalize(&target, node.span)
            .map_err(|error| error.with_operand(origin, true))?;
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
                if named_pipe(&path) {
                    return Ok(None);
                }
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
        let scratch = options
            .shared_invocation
            .as_ref()
            .map_or(options.scratch_root.as_path(), InvocationRoot::path);
        let invocation = InvocationRoot::create(scratch)
            .map_err(|cause| failure("cannot create ordinary statement storage", span, cause))?;
        let imports = invocation.path().join("imports");
        let initialized = (|| {
            let capture_root = options.shared_invocation.as_ref().unwrap_or(&invocation);
            let mut assets = SnapshotStore::new_in(&invocation.path().join("assets"), capture_root)
                .map_err(|cause| failure("cannot create ordinary source store", span, cause))?;
            let registry = crate::filesystem::recovery::Registry::new(&options.scratch_root)
                .map_err(|cause| {
                    ExecutionError::caused("cannot open statement recovery registry", span, cause)
                })?;
            assets.exclude_registry(&registry).map_err(|cause| {
                failure("cannot retain recovery registry identity", span, cause)
            })?;
            if let Some(directory) = super::endpoint::owned_registry(&options.scratch_root)
                .map_err(|cause| {
                    ExecutionError::caused("cannot validate remote recovery registry", span, cause)
                })?
            {
                assets
                    .exclude_owned_directory(&directory)
                    .map_err(|cause| {
                        failure("cannot retain remote registry identity", span, cause)
                    })?;
            }
            fs::create_dir(&imports).map_err(|cause| {
                ExecutionError::caused("cannot create ordinary imports", span, cause)
            })?;
            Ok((assets, registry))
        })();
        let (assets, registry) = initialized.map_err(|mut error: ExecutionError| {
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
            registry,
            imports,
            invocation,
            capture_root: options.shared_invocation.clone(),
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
        let selector = ready
            .pattern
            .as_deref()
            .map(|pattern| regex::Regex::new(&format!("\\A(?:{pattern})\\z")))
            .transpose()
            .map_err(|cause| ExecutionError::caused("invalid selector regex", ready.span, cause))?;
        let (snapshot, basename, source) = match &ready.source {
            Source::Local(path) => (
                self.capture_source(path, selector.as_ref()),
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
                            pattern: ready.pattern.as_deref(),
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
                        Acquisition::Fallback => {
                            let source =
                                super::wsl_source::projected_source(location, options, ready.span)?;
                            let snapshot = self
                                .capture_source(&source, selector.as_ref())
                                .map_err(|cause| {
                                    failure(
                                        "cannot capture projected WSL source",
                                        ready.span,
                                        cause,
                                    )
                                })?;
                            return Ok(Some(Selection {
                                snapshot,
                                basename: source.file_name().map(OsString::from),
                                source: Some(source),
                            }));
                        }
                    }
                }
                let receipt = crate::runtime::import_finite_source(
                    location,
                    &path,
                    ready.pattern.as_deref(),
                    options.limits.buffer_bytes,
                    options.cancel.clone(),
                    ready.span,
                )
                .map_err(|cause| {
                    ExecutionError::caused(
                        "cannot import ordinary remote source",
                        ready.span,
                        cause,
                    )
                })?;
                (self.assets.capture(&path), receipt.basename, None)
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

    fn capture_source(
        &self,
        source: &Path,
        selector: Option<&regex::Regex>,
    ) -> std::result::Result<Snapshot, FsError> {
        let invocation = self.capture_root.as_ref().unwrap_or(&self.invocation);
        match selector {
            Some(selector) => self
                .assets
                .capture_selected_in(source, invocation, selector),
            None => self.assets.capture_in(source, invocation),
        }
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
                    .capture_in(
                        target,
                        self.capture_root.as_ref().unwrap_or(&self.invocation),
                    )
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
        self.publish_checked(
            assignments,
            ready,
            options,
            progress,
            report,
            None::<fn() -> bool>,
        )
        .map(|published| debug_assert!(published))
    }

    fn publish_checked(
        &self,
        assignments: &[Assignment],
        ready: &Ready,
        options: &Options,
        progress: Option<&Progress>,
        report: &mut ExecutionReport,
        guard: Option<impl FnOnce() -> bool>,
    ) -> Result<bool> {
        if assignments.is_empty() {
            return Ok(true);
        }
        let span = ready.span;
        let assignments = self.publication_roots(assignments, span)?;
        for assignment in &assignments {
            self.check_private_destination(&assignment.target, span)?;
        }
        let lease = self.registry.enter().map_err(|cause| {
            ExecutionError::caused("cannot enter statement recovery registry", span, cause)
        })?;
        let mut publications = Vec::new();
        let mut stage_registrations = Vec::new();
        let mut stage = crate::telemetry::span("ordinary.stage");
        let result = self.prepare_registered(
            &assignments,
            span,
            options,
            &lease,
            &mut publications,
            &mut stage_registrations,
        );
        stage.finish_result(&result);
        drop(stage);
        if let Err(mut error) = result {
            compensate(&mut publications, &mut error);
            retire_stages(stage_registrations, &mut error);
            if guard.is_some() && error.retained_paths.is_empty() && error.compensation.is_empty() {
                return Ok(false);
            }
            return Err(error);
        }
        // Release speculative handles after staged receipts own identity checks.
        if guard.is_some_and(|guard| !guard()) {
            let mut error = ExecutionError::new("runtime optimization guard was invalidated", span);
            compensate(&mut publications, &mut error);
            retire_stages(stage_registrations, &mut error);
            if error.retained_paths.is_empty() && error.compensation.is_empty() {
                return Ok(false);
            }
            return Err(error);
        }
        let count = publications.len();
        let (mut batch, coordinator_stage) =
            crate::filesystem::durable::Batch::prepare_registered(publications, &lease)
                .map_err(|cause| failure("cannot prepare durable statement", span, cause))?;
        stage_registrations.push(coordinator_stage);
        let registration = match lease.register(batch.parent(), batch.journal_path()) {
            Ok(registration) => registration,
            Err(cause) => {
                let mut error =
                    ExecutionError::caused("cannot register durable statement", span, cause);
                if let Err(cause) = batch.abort() {
                    error.retained_paths.push(batch.journal_path().to_owned());
                    error.compensation.push(cause);
                }
                return Err(error);
            }
        };
        for stage in stage_registrations {
            if let Err(cause) = stage.finish() {
                let mut error = ExecutionError::caused(
                    "cannot retire promoted stage registration",
                    span,
                    cause,
                );
                if let Err(cause) = batch.abort() {
                    error.retained_paths.push(batch.journal_path().to_owned());
                    error.compensation.push(cause);
                }
                return Err(error);
            }
        }
        observe(progress, ready, Phase::Publish, report);
        let mut publish = crate::telemetry::span("ordinary.publish");
        let result = batch.publish_with(|event| {
            if event != crate::filesystem::durable::Event::Committed
                && options.cancel.is_cancelled()
            {
                return Err(io::Error::new(
                    io::ErrorKind::Interrupted,
                    CancelPublication,
                ));
            }
            Ok(())
        });
        publish.finish_result(&result);
        if let Err(cause) = result {
            let mut error = publication_failure(cause, span);
            if !batch.is_committed() {
                if let Err(cause) = batch.abort() {
                    error.retained_paths.push(batch.journal_path().to_owned());
                    error.compensation.push(cause);
                    return Err(error);
                }
                registration.finish().map_err(|cause| {
                    ExecutionError::caused(
                        "cannot retire recovered statement registration",
                        span,
                        cause,
                    )
                })?;
                return Err(error);
            }
            // A visible decision cannot be revoked, but failed durability is
            // still an execution error. Keep recovery rather than claiming a
            // successful durable commit or reverting installed new bytes.
            error.retained_paths.push(batch.journal_path().to_owned());
            error.report.help.push("The complete new statement is installed, but its durability could not be confirmed. Keep its journal and registry for startup recovery.".into());
            return Err(error);
        }
        report.published_roots = count;
        if let Err(cause) = batch.finalize() {
            report
                .cleanup
                .push(cleanup(cause, batch.journal_path().to_owned()));
        } else if let Err(cause) = registration.finish() {
            report.cleanup.push(CleanupIssue {
                cause: Box::new(cause),
                retained_paths: Vec::new(),
                resolved: false,
            });
        }
        Ok(true)
    }

    #[cfg(test)]
    fn prepare(
        &self,
        assignments: &[Assignment],
        span: Span,
        options: &Options,
        publications: &mut Vec<Publication>,
    ) -> Result<()> {
        for assignment in assignments {
            options.check(span)?;
            publications.push(
                self.assets
                    .prepare(&assignment.snapshot, &assignment.target)
                    .map_err(|cause| failure("cannot prepare ordinary publication", span, cause))?,
            );
        }
        Ok(())
    }

    fn prepare_registered(
        &self,
        assignments: &[Assignment],
        span: Span,
        options: &Options,
        lease: &crate::filesystem::recovery::RegistryLease,
        publications: &mut Vec<Publication>,
        registrations: &mut Vec<crate::filesystem::recovery::Registration>,
    ) -> Result<()> {
        for assignment in assignments {
            options.check(span)?;
            let (publication, registration) = self
                .assets
                .prepare_registered(&assignment.snapshot, &assignment.target, lease)
                .map_err(|cause| failure("cannot prepare registered publication", span, cause))?;
            publications.push(publication);
            registrations.push(registration);
        }
        Ok(())
    }

    fn check_private_destination(&self, target: &Path, span: Span) -> Result<()> {
        let backing = self.capture_root.as_ref().unwrap_or(&self.invocation);
        let overlaps = backing
            .contains_private(target)
            .map_err(|cause| failure("cannot verify private destination boundary", span, cause))?
            || contains_directory(target, backing.path(), span)?
            || self.registry.contains_private(target).map_err(|cause| {
                ExecutionError::caused("cannot verify recovery destination boundary", span, cause)
            })?
            || contains_directory(target, self.registry.directory(), span)?;
        if overlaps {
            return Err(ExecutionError::new("destination would replace active execution or recovery storage", span)
                .help("Choose --scratch outside the destination tree. Active journals and program spools must not move while a statement is executing."));
        }
        Ok(())
    }

    /// Missing ancestors are part of the one staged replacement, not public setup.
    fn publication_roots(&self, assignments: &[Assignment], span: Span) -> Result<Vec<Assignment>> {
        let parent = assignments[0]
            .target
            .parent()
            .ok_or_else(|| ExecutionError::new("destination has no parent", span))?;
        let mut first_missing = None;
        let mut cursor = parent;
        loop {
            match metadata(cursor, span)? {
                Some(meta) if meta.is_dir() => break,
                Some(_) => {
                    return Err(ExecutionError::new(
                        "destination parent is not a directory",
                        span,
                    ));
                }
                None => {
                    first_missing = Some(cursor.to_owned());
                    cursor = cursor.parent().ok_or_else(|| {
                        ExecutionError::new("destination has no existing ancestor", span)
                    })?;
                }
            }
        }
        let Some(root) = first_missing else {
            return Ok(assignments.to_vec());
        };
        let directory = NodeMetadata::capture(&fs::metadata(&self.imports).map_err(|cause| {
            ExecutionError::caused("cannot inspect staged directory permissions", span, cause)
        })?)
        .map_err(|cause| {
            ExecutionError::caused("cannot inspect staged directory metadata", span, cause)
        })?;
        let mut entries = BTreeMap::new();
        entries.insert(
            PathBuf::new(),
            Entry::Directory {
                metadata: directory.clone(),
            },
        );
        for assignment in assignments {
            let prefix = assignment.target.strip_prefix(&root).map_err(|_| {
                ExecutionError::new(
                    "statement publication targets do not share one parent",
                    span,
                )
            })?;
            for ancestor in prefix.ancestors().skip(1) {
                entries
                    .entry(ancestor.to_owned())
                    .or_insert_with(|| Entry::Directory {
                        metadata: directory.clone(),
                    });
            }
            for (path, entry) in assignment.snapshot.entries() {
                entries.insert(prefix.join(path), entry.clone());
            }
        }
        Ok(vec![Assignment {
            target: root,
            snapshot: self.snapshot(entries, span)?,
        }])
    }
}

fn contains_directory(target: &Path, protected: &Path, span: Span) -> Result<bool> {
    if metadata(target, span)?.is_none() {
        return Ok(false);
    }
    let expected = Handle::from_path(target).map_err(|cause| {
        ExecutionError::caused("cannot identify destination boundary", span, cause)
    })?;
    for ancestor in protected.ancestors() {
        let actual = Handle::from_path(ancestor).map_err(|cause| {
            ExecutionError::caused("cannot identify private storage boundary", span, cause)
        })?;
        if actual == expected {
            return Ok(true);
        }
    }
    Ok(false)
}

fn copy_trace(ready: &Ready, kind: &'static str) -> crate::telemetry::SpanGuard {
    let mut trace = crate::telemetry::span("copy");
    trace.expect_completion();
    trace.attribute("copy.kind", kind);
    trace.attribute("publication.policy", "statement-journal");
    trace.metric("instruction.id", ready.instruction.0 as u64);
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
    trace
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
    if cancelled && cause.recovery == crate::filesystem::Recovery::Restored {
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
                cause.recovery = crate::filesystem::Recovery::Restored;
                cause.recovery_error = None;
            }
        }
    }
}

fn retire_stages(
    stages: Vec<crate::filesystem::recovery::Registration>,
    error: &mut ExecutionError,
) {
    if !error.compensation.is_empty() {
        return;
    }
    for stage in stages {
        let path = stage.path().to_owned();
        if let Err(cause) = stage.finish() {
            error.retained_paths.push(path);
            error.report.help.push(format!(
                "cannot retire prepared statement registration: {cause}"
            ));
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
    if named_pipe(&path) {
        return Err(ExecutionError::new(
            "a named pipe requires the asynchronous stream owner",
            span,
        ));
    }
    super::domain::normalize(&path, span)
}

fn named_pipe(path: &Path) -> bool {
    #[cfg(windows)]
    {
        crate::streams::is_named_pipe_path(&path.to_string_lossy())
    }
    #[cfg(not(windows))]
    {
        let _ = path;
        false
    }
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

#[path = "ordinary_fold.rs"]
mod fold;
pub(super) use fold::FoldAttempt;

#[cfg(test)]
#[path = "ordinary_tests.rs"]
mod tests;
