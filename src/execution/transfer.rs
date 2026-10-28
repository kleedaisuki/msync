use std::collections::BTreeMap;
use std::ffi::OsString;
#[cfg(unix)]
use std::fs;
use std::fs::OpenOptions;
#[cfg(unix)]
use std::io;
use std::io::Write;
use std::path::{Path, PathBuf};

use regex::Regex;

use crate::endpoint::Backend;
use crate::hir::{InstructionId, TransactionId};
use crate::language::DestinationMode;
use crate::syntax::Span;
use crate::telemetry::SpanGuard;
use crate::transaction::filesystem::{Entry, Snapshot};

use super::Engine;
use super::domain::{self, Source, Transfer};
use super::error::{ExecutionError, Result};

pub(super) struct Selection {
    pub snapshot: Snapshot,
    pub basename: Option<OsString>,
    pub source: Option<PathBuf>,
}

impl Engine {
    pub(super) fn transfer(
        &mut self,
        instruction: InstructionId,
        transfer: &Transfer,
        transaction: TransactionId,
    ) -> Result<()> {
        let mut trace = self.assignment_trace(
            instruction,
            &transfer.source,
            &transfer.destination,
            transfer.mode,
            transaction,
        );
        if let Some(pattern) = &transfer.pattern {
            trace.attribute_lazy("selector.pattern", || pattern.clone());
        }
        let files = self.report.assigned_files;
        let bytes = self.report.assigned_bytes;
        let result = self.transfer_staged(instruction, transfer, transaction, &mut trace);
        trace.metric(
            "files.staged",
            self.report.assigned_files.saturating_sub(files) as u64,
        );
        trace.metric(
            "bytes.staged",
            self.report.assigned_bytes.saturating_sub(bytes),
        );
        trace.finish_result(&result);
        match &result {
            Ok(()) => trace.attribute("phase", "staged"),
            Err(failure) => {
                trace.record_error("copy.stage", failure);
                if self.options.cancel.is_cancelled() {
                    trace.attribute("phase", "cancelled");
                    trace.cancelled();
                } else {
                    trace.attribute("phase", "failed");
                }
            }
        }
        result
    }

    pub(super) fn assignment_trace(
        &self,
        instruction: InstructionId,
        source: &Source,
        destination: &Path,
        mode: DestinationMode,
        transaction: TransactionId,
    ) -> SpanGuard {
        let mut trace = crate::telemetry::span("copy");
        trace.expect_completion();
        trace.attribute("copy.kind", "immutable-assignment");
        trace.attribute("phase", "snapshot");
        // This phase creates a version; only the transaction publisher can
        // report committed bytes after all receipts have settled.
        trace.attribute("publication.policy", "transaction-checkpoint");
        trace.attribute(
            "destination.mode",
            match mode {
                DestinationMode::Exact => "exact",
                DestinationMode::Container => "container",
                DestinationMode::ReplaceTree => "replace-tree",
            },
        );
        trace.metric("instruction.id", instruction.0 as u64);
        trace.metric("transaction.id", transaction.0 as u64);
        trace.attribute_lazy("manager.root", || {
            self.manager.artifact_root().display().to_string()
        });
        trace.attribute("destination.backend", "local");
        trace.attribute_lazy("destination.path", || destination.display().to_string());
        source_metadata(&mut trace, source);
        trace
    }

    fn transfer_staged(
        &mut self,
        instruction: InstructionId,
        transfer: &Transfer,
        transaction: TransactionId,
        trace: &mut SpanGuard,
    ) -> Result<()> {
        self.options.check(transfer.span)?;
        trace.event("copy.source", &[("phase", "snapshot")], &[]);
        let literal = matches!(
            transfer.source,
            Source::Literal(_) | Source::MaterializedLiteral(_)
        );
        let source = self.source(instruction, transfer, transaction)?;
        let selections = self.select(source, transfer.pattern.as_deref(), transfer.span)?;
        if transfer.pattern.is_some() && transfer.mode != DestinationMode::Container {
            return Err(ExecutionError::new(
                "a selector needs a destination container ending in '/'",
                transfer.span,
            ));
        }
        for selection in selections {
            self.options.check(transfer.span)?;
            let target = match transfer.mode {
                DestinationMode::Container => {
                    let basename = selection.basename.as_ref().ok_or_else(|| ExecutionError::new("a literal stream has no basename for container insertion", transfer.span)
                        .help("Write the literal to an exact file path instead of a trailing '/' container."))?;
                    self.ensure_directory(transaction, &transfer.destination, transfer.span)?;
                    transfer.destination.join(basename)
                }
                _ => transfer.destination.clone(),
            };
            relation(&selection, &target, transfer.span)?;
            let old = self.read(transaction, &target, transfer.span)?;
            let assigned = self.assignment(
                &selection.snapshot,
                &old,
                transfer.mode,
                literal,
                transfer.span,
            )?;
            self.ensure_parent(transaction, &target, transfer.span)?;
            self.manager
                .replace_snapshot(transaction, &target, &assigned)
                .map_err(|cause| {
                    self.manager_error("cannot stage immutable assignment", transfer.span, cause)
                })?;
            staged_event(trace, &selection, &target);
            self.report.assigned_files += selection
                .snapshot
                .entries()
                .values()
                .filter(|entry| matches!(entry, Entry::File { .. }))
                .count();
            self.report.assigned_bytes = selection.snapshot.entries().values().fold(
                self.report.assigned_bytes,
                |total, entry| match entry {
                    Entry::File { artifact, .. } => total.saturating_add(artifact.len()),
                    _ => total,
                },
            );
        }
        self.report.assignments += 1;
        Ok(())
    }

    fn source(
        &mut self,
        instruction: InstructionId,
        transfer: &Transfer,
        transaction: TransactionId,
    ) -> Result<Selection> {
        let (snapshot, basename, source) = match &transfer.source {
            Source::MaterializedLiteral(snapshot) => (snapshot.clone(), None, None),
            Source::Captured { snapshot, basename } => (snapshot.clone(), basename.clone(), None),
            Source::Local(path) => (
                self.read(transaction, path, transfer.span)?,
                path.file_name().map(OsString::from),
                Some(path.clone()),
            ),
            Source::Remote { location, basename } => (
                domain::imported(
                    location,
                    transfer.span,
                    &self.imports,
                    &self.assets,
                    &self.options,
                    instruction,
                )?,
                basename.clone(),
                None,
            ),
            Source::Literal(text) => {
                let path = self.imports.join(format!("literal-{}", instruction.0));
                let mut file = OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&path)
                    .map_err(|cause| {
                        crate::telemetry::io_error("literal.open", &cause);
                        ExecutionError::caused("cannot create literal source", transfer.span, cause)
                    })?;
                file.write_all(text.as_bytes()).map_err(|cause| {
                    crate::telemetry::io_error("literal.write", &cause);
                    ExecutionError::caused(
                        "cannot write UTF-8 literal source",
                        transfer.span,
                        cause,
                    )
                })?;
                drop(file);
                let snapshot = self.assets.capture(&path).map_err(|cause| {
                    ExecutionError::caused("cannot capture literal source", transfer.span, cause)
                })?;
                (snapshot, None, None)
            }
        };
        if snapshot.is_missing() {
            let endpoint = source
                .as_ref()
                .map_or_else(|| "source".into(), |path| path.display().to_string());
            return Err(ExecutionError::new(format!("source does not exist in this transaction view: {endpoint}"), transfer.span)
                .help("Correct the source path or create it in an earlier statement; a later write cannot produce an earlier read."));
        }
        Ok(Selection {
            snapshot,
            basename,
            source,
        })
    }

    fn select(
        &self,
        source: Selection,
        pattern: Option<&str>,
        span: Span,
    ) -> Result<Vec<Selection>> {
        select_snapshot(
            source,
            pattern,
            span,
            || self.options.check(span),
            |entries| self.snapshot(entries, span),
        )
    }
    fn assignment(
        &self,
        source: &Snapshot,
        destination: &Snapshot,
        mode: DestinationMode,
        literal: bool,
        span: Span,
    ) -> Result<Snapshot> {
        super::assignment::assign(
            source,
            super::assignment::Target::snapshot(destination),
            mode,
            literal,
            span,
            |entries| self.snapshot(entries, span),
        )
    }
    fn read(&mut self, transaction: TransactionId, path: &Path, span: Span) -> Result<Snapshot> {
        self.manager
            .read_snapshot(transaction, path)
            .map_err(|cause| self.manager_error("cannot read immutable resource view", span, cause))
    }

    fn ensure_parent(
        &mut self,
        transaction: TransactionId,
        target: &Path,
        span: Span,
    ) -> Result<()> {
        let root = self
            .roots
            .iter()
            .find(|root| domain::relative_to(target, root).is_some())
            .ok_or_else(|| {
                ExecutionError::new("target is outside the enrolled resource domain", span)
            })?
            .clone();
        let parent = target
            .parent()
            .ok_or_else(|| ExecutionError::new("destination has no parent", span))?;
        if domain::relative_to(target, &root)
            .is_some_and(|relative| relative.as_os_str().is_empty())
        {
            #[cfg(unix)]
            if fs::metadata(parent)
                .map_err(|cause| {
                    ExecutionError::caused("cannot inspect destination parent", span, cause)
                })?
                .permissions()
                .readonly()
            {
                return Err(ExecutionError::caused(
                    "destination parent is read-only",
                    span,
                    io::Error::new(
                        io::ErrorKind::PermissionDenied,
                        parent.display().to_string(),
                    ),
                ));
            }
            return Ok(());
        }
        let suffix = domain::relative_to(parent, &root)
            .ok_or_else(|| ExecutionError::new("invalid destination ancestry", span))?;
        let mut directory = root;
        self.ensure_directory(transaction, &directory, span)?;
        for part in suffix.components() {
            directory.push(part);
            self.ensure_directory(transaction, &directory, span)?;
        }
        Ok(())
    }

    fn ensure_directory(
        &mut self,
        transaction: TransactionId,
        path: &Path,
        span: Span,
    ) -> Result<()> {
        let old = self
            .manager
            .read_entry(transaction, path)
            .map_err(|cause| {
                self.manager_error("cannot inspect destination directory", span, cause)
            })?;
        if matches!(old, Some(Entry::Directory { .. })) {
            #[cfg(unix)]
            if matches!(&old, Some(Entry::Directory { metadata }) if metadata.permissions.readonly())
            {
                return Err(ExecutionError::caused(
                    "destination directory is read-only",
                    span,
                    io::Error::new(io::ErrorKind::PermissionDenied, path.display().to_string()),
                ));
            }
            return Ok(());
        }
        if old.is_some() {
            return Err(ExecutionError::new(
                format!("destination parent is not a directory: {}", path.display()),
                span,
            ));
        }
        self.ensure_parent(transaction, path, span)?;
        let entries = BTreeMap::from([(
            PathBuf::new(),
            Entry::Directory {
                metadata: crate::transaction::filesystem::NodeMetadata::new(
                    self.directory.clone(),
                    std::time::SystemTime::now(),
                ),
            },
        )]);
        let directory = self.snapshot(entries, span)?;
        self.manager
            .replace_snapshot(transaction, path, &directory)
            .map_err(|cause| self.manager_error("cannot stage destination directory", span, cause))
    }
}

fn source_metadata(trace: &mut SpanGuard, source: &Source) {
    match source {
        Source::Local(path) => {
            trace.attribute("source.backend", "local");
            trace.attribute("source.kind", "file-view");
            trace.attribute_lazy("source.path", || path.display().to_string());
        }
        Source::Remote { location, .. } => {
            let backend = match &location.backend {
                Backend::Local | Backend::Path | Backend::Env => "local",
                Backend::Wsl => "wsl",
                Backend::Ssh { .. } => "ssh",
                Backend::Http { .. } => "http",
                Backend::Tcp { .. } => "tcp",
            };
            trace.attribute("source.backend", backend);
            trace.attribute("source.kind", "remote-import");
            trace.attribute_lazy("source.path", || location.path.clone());
            trace.attribute_lazy("source.endpoint", || location.backend.to_string());
        }
        Source::Literal(_) | Source::MaterializedLiteral(_) => {
            trace.attribute("source.backend", "literal");
            trace.attribute("source.kind", "utf8-literal");
            trace.attribute("source.path", "<literal>");
        }
        Source::Captured { basename, .. } => {
            trace.attribute("source.backend", "snapshot");
            trace.attribute("source.kind", "captured-snapshot");
            trace.attribute("source.path", "<captured snapshot>");
            if let Some(name) = basename {
                trace.attribute_lazy("source.name", || name.to_string_lossy().into_owned());
            }
        }
    }
}

fn staged_event(trace: &SpanGuard, selection: &Selection, target: &Path) {
    if !trace.is_recording() {
        return;
    }
    let target = target.display().to_string();
    let source = selection
        .source
        .as_ref()
        .map(|path| path.display().to_string());
    let mut attributes = vec![
        ("phase", "stage"),
        ("stage.kind", "immutable-version"),
        ("destination.path", target.as_str()),
    ];
    if let Some(source) = &source {
        attributes.push(("source.path", source.as_str()));
    }
    let artifact = match selection.snapshot.entries().get(Path::new("")) {
        Some(Entry::File { artifact, .. }) => Some(artifact.path().display().to_string()),
        _ => None,
    };
    if let Some(artifact) = &artifact {
        attributes.push(("stage.path", artifact.as_str()));
    }
    let (files, bytes) = selection.snapshot.entries().values().fold(
        (0u64, 0u64),
        |(files, bytes), entry| match entry {
            Entry::File { artifact, .. } => (files + 1, bytes.saturating_add(artifact.len())),
            _ => (files, bytes),
        },
    );
    trace.event(
        "copy.stage",
        &attributes,
        &[("files.staged", files), ("bytes.staged", bytes)],
    );
}

pub(super) fn relation(source: &Selection, destination: &Path, span: Span) -> Result<()> {
    let Some(path) = &source.source else {
        return Ok(());
    };
    if path == destination {
        return Err(ExecutionError::new(
            "source and destination are the same path",
            span,
        ));
    }
    if source.snapshot.is_directory()
        && (path.starts_with(destination) || destination.starts_with(path))
    {
        return Err(ExecutionError::new(
            "source and destination directory trees must be disjoint",
            span,
        ));
    }
    if path.exists()
        && destination.exists()
        && same_file::is_same_file(path, destination).map_err(|cause| {
            ExecutionError::caused(
                "cannot compare source and destination identities",
                span,
                cause,
            )
        })?
    {
        return Err(ExecutionError::new(
            "source and destination refer to the same file identity",
            span,
        ));
    }
    Ok(())
}

pub(super) fn select_snapshot(
    source: Selection,
    pattern: Option<&str>,
    span: Span,
    check: impl Fn() -> Result<()>,
    build: impl Fn(BTreeMap<PathBuf, Entry>) -> Result<Snapshot>,
) -> Result<Vec<Selection>> {
    let Some(pattern) = pattern else {
        return Ok(vec![source]);
    };
    if !source.snapshot.is_directory() {
        return Err(ExecutionError::new(
            "a selector source must be a directory",
            span,
        ));
    }
    let regex = Regex::new(&format!("\\A(?:{pattern})\\z"))
        .map_err(|cause| ExecutionError::caused("invalid selector regex", span, cause))?;
    let mut selections = Vec::new();
    for name in source
        .snapshot
        .entries()
        .keys()
        .filter(|path| path.components().count() == 1)
    {
        check()?;
        let text = name
            .to_str()
            .ok_or_else(|| ExecutionError::new("selector entry is not UTF-8", span))?;
        if !regex.is_match(text) {
            continue;
        }
        let entries = source
            .snapshot
            .entries()
            .iter()
            .filter_map(|(path, entry)| {
                path.strip_prefix(name)
                    .ok()
                    .map(|relative| (relative.to_owned(), entry.clone()))
            })
            .collect();
        selections.push(Selection {
            snapshot: build(entries)?,
            basename: name.file_name().map(OsString::from),
            source: source.source.as_ref().map(|path| path.join(name)),
        });
    }
    Ok(selections)
}
