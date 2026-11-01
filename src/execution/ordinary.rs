//! Ordinary finite assignments use same-directory staging and replacement.
//!
//! Simple files copy directly into their final stage. Composite views retain
//! captured versions when composition needs them, then publish roots in order.
//! Completed roots are not rolled back when a later operation needs retry.

use std::collections::BTreeMap;
use std::ffi::OsString;
use std::fs;
use std::io::{self, Write};
use std::path::{Path, PathBuf};

use crate::endpoint::{Backend, Location};
use crate::filesystem::{
    Entry, FsError, InvocationRoot, NodeMetadata, Publication, Snapshot, SnapshotStore,
};
#[cfg(test)]
use crate::hir::Program;
use crate::hir::{Input, Instruction, InstructionId, InstructionKind, Output};
use crate::language::DestinationMode;
use crate::syntax::Span;

use super::assignment::{self, Target};
use super::error::{ExecutionError, Result};
use super::progress::{Event, Phase, Progress};
use super::transfer::Selection;
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

#[derive(Default)]
struct Staged {
    assignments: Vec<Assignment>,
    targets: Option<BTreeMap<PathBuf, usize>>,
    report: ExecutionReport,
}

struct NativeNode {
    relative: PathBuf,
    metadata: NodeMetadata,
    kind: NativeKind,
}

enum NativeKind {
    File { bytes: u64 },
    Directory,
    Symlink { target: PathBuf, directory: bool },
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
        if matches!(ready.source, Source::Remote(_)) {
            return self.execute_remote(&ready);
        }
        if let Some(report) = self.execute_file(&ready)? {
            return Ok(Attempt::Completed(report));
        }
        if let Some(report) = self.execute_directory(&ready)? {
            return Ok(Attempt::Completed(report));
        }
        self.execute_ready(&ready, None)
    }

    fn execute_file(&self, ready: &Ready) -> Result<Option<ExecutionReport>> {
        if ready.pattern.is_some() || ready.mode == DestinationMode::ReplaceTree {
            return Ok(None);
        }
        let source_metadata = match &ready.source {
            Source::Local(path) => {
                if fs::symlink_metadata(path)
                    .is_ok_and(|metadata| metadata.file_type().is_symlink())
                {
                    return Ok(None);
                }
                let Some(metadata) = metadata(path, ready.span)? else {
                    if fs::symlink_metadata(path)
                        .is_ok_and(|metadata| metadata.file_type().is_symlink())
                    {
                        return Ok(None);
                    }
                    return Err(ExecutionError::new(
                        format!("source does not exist: {}", path.display()),
                        ready.span,
                    ));
                };
                if !metadata.is_file() {
                    return Ok(None);
                }
                Some(NodeMetadata::capture(&metadata).map_err(|cause| {
                    ExecutionError::caused("cannot read source metadata", ready.span, cause)
                })?)
            }
            Source::Literal(_) => None,
            Source::Remote(_) => return Ok(None),
        };
        let target = if ready.mode == DestinationMode::Container {
            let Source::Local(source) = &ready.source else {
                return Ok(None);
            };
            ready.target.join(source.file_name().ok_or_else(|| {
                ExecutionError::new(
                    "source needs a basename for container insertion",
                    ready.span,
                )
            })?)
        } else {
            ready.target.clone()
        };
        if metadata(&target, ready.span)?.is_some_and(|metadata| metadata.is_dir()) {
            return Err(ExecutionError::new(
                "the exact destination is an existing directory",
                ready.span,
            )
            .help("Append '/' for container insertion, or '/.' to replace the directory."));
        }
        let mut trace = copy_trace(ready, "ordinary-assignment");
        let result = self.copy_file(ready, &target, source_metadata.as_ref());
        trace.finish_result(&result);
        if let Ok(report) = &result {
            trace.metric("files.assigned", report.assigned_files as u64);
            trace.metric("bytes.assigned", report.assigned_bytes);
        }
        result.map(Some)
    }

    fn copy_file(
        &self,
        ready: &Ready,
        target: &Path,
        source_metadata: Option<&NodeMetadata>,
    ) -> Result<ExecutionReport> {
        self.copy_file_after_acquire(ready, target, source_metadata, || {})
    }

    fn copy_file_after_acquire(
        &self,
        ready: &Ready,
        target: &Path,
        source_metadata: Option<&NodeMetadata>,
        acquired: impl FnOnce(),
    ) -> Result<ExecutionReport> {
        let span = ready.span;
        let no_clobber = false;
        let stage = crate::io::StagedFile::new(target, no_clobber)
            .map_err(|cause| ExecutionError::caused("cannot create file stage", span, cause))?;
        observe(
            self.progress.as_ref(),
            ready,
            Phase::Acquire,
            &ExecutionReport::default(),
        );
        let mut acquire = crate::telemetry::span("ordinary.acquire");
        let copied = match &ready.source {
            Source::Local(source) => crate::io::copy_file(
                source,
                stage.path(),
                self.options.limits.buffer_bytes,
                &self.options.cancel,
            ),
            Source::Literal(text) => {
                fs::write(stage.path(), text.as_bytes()).map(|()| text.len() as u64)
            }
            Source::Remote(_) => unreachable!("remote sources use acquisition"),
        };
        acquire.finish_result(&copied);
        let bytes = copied.map_err(|cause| {
            if self.options.cancel.is_cancelled() {
                ExecutionError::cancelled("file copy cancelled", span)
            } else {
                ExecutionError::caused("cannot copy source", span, cause)
            }
        })?;
        drop(acquire);
        let report = ExecutionReport {
            assignments: 1,
            assigned_files: 1,
            assigned_bytes: bytes,
            published_roots: 1,
            ..ExecutionReport::default()
        };
        observe(self.progress.as_ref(), ready, Phase::Stage, &report);
        let mut prepared = crate::telemetry::span("ordinary.stage");
        let preparation = match source_metadata {
            Some(metadata) => metadata.apply(stage.path()),
            None => stage.preserve_target_permissions(),
        };
        prepared.finish_result(&preparation);
        preparation
            .map_err(|cause| ExecutionError::caused("cannot apply file metadata", span, cause))?;
        acquired();
        self.options.check(span)?;
        crate::filesystem::sync_file(stage.path())
            .map_err(|cause| failure("cannot flush staged file", span, cause))?;
        drop(prepared);
        observe(self.progress.as_ref(), ready, Phase::Publish, &report);
        let mut publish = crate::telemetry::span("ordinary.publish");
        let publication = stage.commit();
        publish.finish_result(&publication);
        publication
            .map_err(|cause| ExecutionError::caused("cannot replace destination", span, cause))?;
        Ok(report)
    }

    fn execute_directory(&self, ready: &Ready) -> Result<Option<ExecutionReport>> {
        let Source::Local(source) = &ready.source else {
            return Ok(None);
        };
        let source_metadata = fs::symlink_metadata(source).map_err(|cause| {
            ExecutionError::caused(
                format!("cannot inspect source {}", source.display()),
                ready.span,
                cause,
            )
        })?;
        if !source_metadata.is_dir() && !source_metadata.file_type().is_symlink() {
            if ready.pattern.is_some() {
                return Err(ExecutionError::new(
                    "a selector source must be a directory",
                    ready.span,
                ));
            }
            return Ok(None);
        }
        if ready.mode == DestinationMode::ReplaceTree && !source_metadata.is_dir() {
            return Err(ExecutionError::new(
                "directory replacement needs a directory source",
                ready.span,
            ));
        }
        let mut roots = Vec::new();
        if let Some(pattern) = &ready.pattern {
            let regex = regex::Regex::new(&format!("\\A(?:{pattern})\\z")).map_err(|cause| {
                ExecutionError::caused("invalid selector regex", ready.span, cause)
            })?;
            let mut children = fs::read_dir(source)
                .map_err(|cause| {
                    ExecutionError::caused("cannot enumerate source", ready.span, cause)
                })?
                .collect::<io::Result<Vec<_>>>()
                .map_err(|cause| {
                    ExecutionError::caused("cannot read source entry", ready.span, cause)
                })?;
            children.sort_by_key(fs::DirEntry::file_name);
            for child in children {
                if child
                    .file_name()
                    .to_str()
                    .is_some_and(|name| regex.is_match(name))
                {
                    roots.push((child.path(), ready.target.join(child.file_name())));
                }
            }
        } else {
            let target = if ready.mode == DestinationMode::Container {
                ready.target.join(source.file_name().ok_or_else(|| {
                    ExecutionError::new(
                        "source needs a basename for container insertion",
                        ready.span,
                    )
                })?)
            } else {
                ready.target.clone()
            };
            if source_metadata.is_dir()
                && ready.mode == DestinationMode::Exact
                && metadata(&target, ready.span)?.is_some()
            {
                return Err(ExecutionError::new(
                    "a directory source needs an absent exact target or '/.'",
                    ready.span,
                ));
            }
            roots.push((source.clone(), target));
        }
        // The source version is genuinely needed when a destination can change
        // an input that has not yet been read. Ordinary disjoint copies stream.
        if roots.iter().any(|(source, target)| {
            let source = fs::canonicalize(source).unwrap_or_else(|_| source.clone());
            source.starts_with(target) || target.starts_with(source)
        }) {
            return Ok(None);
        }
        let mut planned = Vec::with_capacity(roots.len());
        for (source, target) in roots {
            let mut nodes = Vec::new();
            collect_native(
                &source,
                Path::new(""),
                &mut nodes,
                &self.options,
                ready.span,
            )?;
            planned.push((source, target, nodes));
        }
        let mut trace = copy_trace(ready, "ordinary-assignment");
        let result = (|| {
            let mut report = ExecutionReport {
                assignments: 1,
                ..ExecutionReport::default()
            };
            for (source, target, nodes) in planned {
                self.options.check(ready.span)?;
                if ready.mode == DestinationMode::ReplaceTree {
                    if metadata(&target, ready.span)?.is_some_and(|metadata| !metadata.is_dir()) {
                        return Err(ExecutionError::new(
                            "directory replacement needs a directory target",
                            ready.span,
                        ));
                    }
                    crate::filesystem::remove_tree(&target).map_err(|cause| {
                        failure("cannot remove replaced tree", ready.span, cause)
                    })?;
                }
                self.copy_tree(ready, &source, &target, &nodes, &mut report)?;
                report.published_roots += 1;
            }
            Ok(report)
        })();
        trace.finish_result(&result);
        result.map(Some)
    }

    fn copy_tree(
        &self,
        ready: &Ready,
        source: &Path,
        target: &Path,
        nodes: &[NativeNode],
        report: &mut ExecutionReport,
    ) -> Result<()> {
        let mut directories = Vec::new();
        for node in nodes {
            self.options.check(ready.span)?;
            let destination = crate::filesystem::entry_path(target, &node.relative);
            match &node.kind {
                NativeKind::Directory => {
                    let existed = fs::metadata(&destination).is_ok();
                    fs::create_dir_all(&destination).map_err(|cause| {
                        ExecutionError::caused(
                            format!("cannot create {}", destination.display()),
                            ready.span,
                            cause,
                        )
                    })?;
                    if !existed {
                        directories.push((&node.metadata, destination));
                    }
                }
                NativeKind::File { .. } => {
                    let item = Ready {
                        instruction: ready.instruction,
                        source: Source::Local(crate::filesystem::entry_path(
                            source,
                            &node.relative,
                        )),
                        target: destination.clone(),
                        mode: DestinationMode::Exact,
                        pattern: None,
                        span: ready.span,
                    };
                    let copied = self.copy_file(&item, &destination, Some(&node.metadata))?;
                    report.assigned_files += copied.assigned_files;
                    report.assigned_bytes =
                        report.assigned_bytes.saturating_add(copied.assigned_bytes);
                }
                NativeKind::Symlink {
                    target: link,
                    directory,
                } => {
                    let stage =
                        crate::io::StagedFile::new(&destination, false).map_err(|cause| {
                            ExecutionError::caused(
                                "cannot create symbolic link stage",
                                ready.span,
                                cause,
                            )
                        })?;
                    fs::remove_file(stage.path()).map_err(|cause| {
                        ExecutionError::caused("cannot prepare symbolic link", ready.span, cause)
                    })?;
                    crate::filesystem::create_symlink(link, stage.path(), *directory).map_err(
                        |cause| {
                            ExecutionError::caused("cannot create symbolic link", ready.span, cause)
                        },
                    )?;
                    node.metadata.apply_link(stage.path()).map_err(|cause| {
                        ExecutionError::caused(
                            "cannot apply symbolic link metadata",
                            ready.span,
                            cause,
                        )
                    })?;
                    stage.commit().map_err(|cause| {
                        ExecutionError::caused("cannot replace symbolic link", ready.span, cause)
                    })?;
                    report.assigned_files += 1;
                }
            }
        }
        for (metadata, path) in directories.into_iter().rev() {
            metadata.apply(&path).map_err(|cause| {
                ExecutionError::caused("cannot apply directory metadata", ready.span, cause)
            })?;
        }
        Ok(())
    }

    fn execute_ready(&mut self, ready: &Ready, cohort: Option<&[Ready]>) -> Result<Attempt> {
        let mut trace = copy_trace(
            ready,
            if cohort.is_some() {
                "ordinary-cohort"
            } else {
                "ordinary-assignment"
            },
        );
        let result = (|| {
            if self.storage.is_none() {
                self.storage = Some(Storage::new(&self.options, ready.span)?);
            }
            let storage = self.storage.as_mut().expect("ordinary storage initialized");
            match cohort {
                Some(items) => storage.execute_cohort(
                    items,
                    &self.options,
                    self.progress.as_ref(),
                    self.origin.as_ref(),
                ),
                None => storage.execute(
                    ready,
                    &self.options,
                    self.progress.as_ref(),
                    self.origin.as_ref(),
                ),
            }
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
        let Some((source, pattern)) =
            Source::input(source, node.span).map_err(|error| error.with_operand(origin, false))?
        else {
            return Ok(None);
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
    fn input(input: &Input, span: Span) -> Result<Option<(Self, Option<String>)>> {
        match input {
            Input::Literal(text) => Ok(Some((Self::Literal(text.clone()), None))),
            Input::Port { .. } => Ok(None),
            Input::File { location, pattern } => {
                Ok(Self::inspect(location, span)?.map(|source| (source, pattern.clone())))
            }
        }
    }

    fn inspect(location: &Location, span: Span) -> Result<Option<Self>> {
        match crate::runtime::local_path(location, true)
            .map_err(|message| ExecutionError::new(message, span))?
        {
            Some(path) => {
                if named_pipe(&path) {
                    return Ok(None);
                }
                let path = crate::filesystem::source_path(&path)
                    .map_err(|cause| failure("cannot resolve source", span, cause))?;
                if fs::symlink_metadata(&path).is_ok_and(|metadata| {
                    !metadata.file_type().is_symlink() && !metadata.is_file() && !metadata.is_dir()
                }) {
                    return Ok(None);
                }
                Ok(Some(Self::Local(path)))
            }
            None if matches!(location.backend, Backend::Tcp { .. }) => Ok(None),
            None => Ok(Some(Self::Remote(location.clone()))),
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
            if let Some(context) = crate::runtime::ssh::current() {
                context.exclude_from(&mut assets).map_err(|cause| {
                    ExecutionError::caused(
                        "cannot exclude owned SSH control storage from source capture",
                        span,
                        cause,
                    )
                })?;
            }
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
        let mut staged = Staged::default();
        if !self.stage(ready, options, progress, origin, &mut staged)? {
            return Ok(Attempt::Unsupported);
        }
        self.publish(
            &staged.assignments,
            ready,
            options,
            progress,
            &mut staged.report,
        )?;
        staged.report.assignments = 1;
        Ok(Attempt::Completed(staged.report))
    }

    fn stage(
        &mut self,
        ready: &Ready,
        options: &Options,
        progress: Option<&Progress>,
        origin: Option<&crate::source::InstructionOrigin>,
        staged: &mut Staged,
    ) -> Result<bool> {
        observe(progress, ready, Phase::Acquire, &ExecutionReport::default());
        let mut acquire = crate::telemetry::span("ordinary.acquire");
        let selection = self.source(ready, options);
        acquire.finish_result(&selection);
        drop(acquire);
        let Some(selection) = selection.map_err(|error| error.with_operand(origin, false))? else {
            return Ok(false);
        };
        observe(progress, ready, Phase::Stage, &ExecutionReport::default());
        let selections = self
            .select(selection, ready.pattern.as_deref(), ready.span, options)
            .map_err(|error| error.with_operand(origin, false))?;
        for selection in selections {
            options.check(ready.span)?;
            let target = target(ready, &selection)?;
            let previous = staged
                .targets
                .as_ref()
                .and_then(|targets| targets.get(&target))
                .copied();
            let assigned = match previous {
                Some(index) => self.assignment_over(
                    &selection.snapshot,
                    &staged.assignments[index].snapshot,
                    ready,
                )?,
                None => self.assignment(&selection.snapshot, &target, ready)?,
            };
            for entry in selection.snapshot.entries().values() {
                if let Entry::File { artifact, .. } = entry {
                    staged.report.assigned_files += 1;
                    staged.report.assigned_bytes =
                        staged.report.assigned_bytes.saturating_add(artifact.len());
                }
            }
            if let Some(index) = previous {
                staged.assignments[index].snapshot = assigned;
            } else {
                if let Some(targets) = &mut staged.targets {
                    targets.insert(target.clone(), staged.assignments.len());
                }
                staged.assignments.push(Assignment {
                    target,
                    snapshot: assigned,
                });
            }
        }
        observe(progress, ready, Phase::Stage, &staged.report);
        Ok(true)
    }

    fn assignment_over(
        &self,
        source: &Snapshot,
        previous: &Snapshot,
        ready: &Ready,
    ) -> Result<Snapshot> {
        let target = match previous.entries().get(Path::new("")) {
            Some(Entry::Directory { .. }) => Target::Directory(Some(previous)),
            Some(Entry::File { metadata, .. } | Entry::Symlink { metadata, .. }) => {
                Target::File(metadata)
            }
            None => Target::Missing,
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
                            mode: ready.mode,
                            pattern: ready.pattern.as_deref(),
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
        if assignments.is_empty() {
            return Ok(());
        }
        let span = ready.span;
        let assignments = self.publication_roots(assignments, span)?;
        let mut publications = Vec::with_capacity(assignments.len());
        let mut stage = crate::telemetry::span("ordinary.stage");
        let result = self.prepare(&assignments, span, options, &mut publications);
        stage.finish_result(&result);
        result?;
        drop(stage);
        observe(progress, ready, Phase::Publish, report);
        let mut publish = crate::telemetry::span("ordinary.publish");
        let result = (|| {
            for publication in &mut publications {
                options.check(span)?;
                publication
                    .publish()
                    .map_err(|cause| publication_failure(cause, span))?;
                report.published_roots += 1;
                if let Err(cause) = publication.finalize() {
                    report
                        .cleanup
                        .push(cleanup(cause, publication.journal().to_owned()));
                }
            }
            Ok(())
        })();
        publish.finish_result(&result);
        result
    }

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
                    .map_err(|cause| failure("cannot stage replacement", span, cause))?,
            );
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

fn collect_native(
    source: &Path,
    relative: &Path,
    nodes: &mut Vec<NativeNode>,
    options: &Options,
    span: Span,
) -> Result<()> {
    options.check(span)?;
    let metadata = fs::symlink_metadata(source).map_err(|cause| {
        ExecutionError::caused(format!("cannot inspect {}", source.display()), span, cause)
    })?;
    if metadata.is_dir() {
        if let Some(invocation) = &options.shared_invocation
            && invocation
                .excludes(source)
                .map_err(|cause| failure("cannot identify execution scratch", span, cause))?
        {
            return Ok(());
        }
        if let Some(context) = crate::runtime::ssh::current()
            && context.excludes_directory(source).map_err(|cause| {
                ExecutionError::caused("cannot identify SSH control storage", span, cause)
            })?
        {
            return Ok(());
        }
    }
    let kind = if metadata.file_type().is_symlink() {
        #[cfg(windows)]
        let directory = {
            use std::os::windows::fs::FileTypeExt;
            metadata.file_type().is_symlink_dir()
        };
        #[cfg(not(windows))]
        let directory = false;
        NativeKind::Symlink {
            target: fs::read_link(source).map_err(|cause| {
                ExecutionError::caused("cannot read symbolic link", span, cause)
            })?,
            directory,
        }
    } else if metadata.is_dir() {
        NativeKind::Directory
    } else if metadata.is_file() {
        NativeKind::File {
            bytes: metadata.len(),
        }
    } else {
        return Err(ExecutionError::new(
            "a physical stream needs the stream transfer owner",
            span,
        ));
    };
    let directory = matches!(kind, NativeKind::Directory);
    nodes.push(NativeNode {
        relative: relative.to_owned(),
        metadata: NodeMetadata::capture(&metadata)
            .map_err(|cause| ExecutionError::caused("cannot read source metadata", span, cause))?,
        kind,
    });
    if directory {
        let mut children = fs::read_dir(source)
            .map_err(|cause| {
                ExecutionError::caused("cannot enumerate source directory", span, cause)
            })?
            .collect::<io::Result<Vec<_>>>()
            .map_err(|cause| {
                ExecutionError::caused("cannot read source directory entry", span, cause)
            })?;
        children.sort_by_key(fs::DirEntry::file_name);
        for child in children {
            collect_native(
                &child.path(),
                &relative.join(child.file_name()),
                nodes,
                options,
                span,
            )?;
        }
    }
    Ok(())
}

fn copy_trace(ready: &Ready, kind: &'static str) -> crate::telemetry::SpanGuard {
    let mut trace = crate::telemetry::span("copy");
    trace.expect_completion();
    trace.attribute("copy.kind", kind);
    trace.attribute("publication.policy", "atomic-replace");
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

fn publication_failure(cause: FsError, span: Span) -> ExecutionError {
    if cause.cause.kind() == io::ErrorKind::Interrupted {
        return ExecutionError::cancelled("ordinary assignment cancelled", span);
    }
    failure("ordinary replacement failed", span, cause)
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
    match fs::metadata(path) {
        Ok(meta) => Ok(Some(meta)),
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

#[path = "ordinary_cohort.rs"]
mod cohort;

#[path = "ordinary_receive.rs"]
mod receive;

#[path = "ordinary_fold.rs"]
mod fold;
pub(super) use fold::FoldAttempt;

#[cfg(test)]
#[path = "ordinary_tests.rs"]
mod tests;

#[cfg(test)]
#[path = "ordinary_cohort_tests.rs"]
mod cohort_tests;
