use std::collections::BTreeMap;
use std::ffi::OsString;
#[cfg(unix)]
use std::fs;
use std::fs::OpenOptions;
use std::io::{self, Write};
use std::path::{Path, PathBuf};

use regex::Regex;

use crate::hir::{InstructionId, TransactionId};
use crate::language::DestinationMode;
use crate::syntax::Span;
use crate::transaction::filesystem::{Entry, Snapshot};

use super::Engine;
use super::domain::{self, Source, Transfer};
use super::error::{ExecutionError, Result};

struct Selection {
    snapshot: Snapshot,
    basename: Option<OsString>,
    source: Option<PathBuf>,
}

impl Engine {
    pub(super) fn transfer(
        &mut self,
        instruction: InstructionId,
        transfer: &Transfer,
        transaction: TransactionId,
    ) -> Result<()> {
        self.options.check(transfer.span)?;
        let literal = matches!(transfer.source, Source::Literal(_));
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
                        ExecutionError::caused("cannot create literal source", transfer.span, cause)
                    })?;
                file.write_all(text.as_bytes()).map_err(|cause| {
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
            self.options.check(span)?;
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
                snapshot: self.snapshot(entries, span)?,
                basename: name.file_name().map(OsString::from),
                source: source.source.as_ref().map(|path| path.join(name)),
            });
        }
        Ok(selections)
    }

    fn assignment(
        &self,
        source: &Snapshot,
        destination: &Snapshot,
        mode: DestinationMode,
        literal: bool,
        span: Span,
    ) -> Result<Snapshot> {
        match mode {
            DestinationMode::ReplaceTree => {
                if !source.is_directory() {
                    return Err(ExecutionError::new(
                        "explicit '/.' replacement requires a directory source",
                        span,
                    ));
                }
                if !destination.is_missing() && !destination.is_directory() {
                    return Err(ExecutionError::new(
                        "explicit '/.' replacement requires a directory destination or an absent path",
                        span,
                    ));
                }
                Ok(source.clone())
            }
            DestinationMode::Container => {
                if destination.is_missing() {
                    return Ok(source.clone());
                }
                if !source.is_directory() || !destination.is_directory() {
                    return Err(ExecutionError::new("destination entry already exists in the container", span)
                        .help("Container insertion never overwrites files; choose an exact target for replacement or '/.' for a complete directory replacement."));
                }
                let mut entries = destination.entries().clone();
                for (path, entry) in source.entries() {
                    match entries.get(path) {
                        Some(Entry::Directory { .. }) if matches!(entry, Entry::Directory { .. }) => {}
                        Some(_) => return Err(ExecutionError::new(format!("container collision at {}", path.display()), span)
                            .help("No selected entry has been published. Rename the source or choose an explicit replacement target.")),
                        None => { entries.insert(path.clone(), entry.clone()); }
                    }
                }
                self.snapshot(entries, span)
            }
            DestinationMode::Exact => {
                if destination.is_directory() {
                    return Err(ExecutionError::new("the exact destination is an existing directory", span)
                        .help("Append '/' for container insertion, or '/.' to explicitly replace the directory."));
                }
                if source.is_directory() && !destination.is_missing() {
                    return Err(ExecutionError::new(
                        "a directory source cannot replace an existing ordinary file",
                        span,
                    ));
                }
                if let Some(Entry::File { metadata, .. }) = destination.entries().get(Path::new(""))
                {
                    if metadata.permissions.readonly() {
                        return Err(ExecutionError::caused(
                            "the existing file is read-only",
                            span,
                            io::Error::new(
                                io::ErrorKind::PermissionDenied,
                                "immutable publication must not bypass the target's write protection",
                            ),
                        ));
                    }
                    if literal {
                        let mut entries = source.entries().clone();
                        if let Some(Entry::File {
                            metadata: source_metadata,
                            ..
                        }) = entries.get_mut(Path::new(""))
                        {
                            source_metadata.permissions = metadata.permissions.clone();
                        }
                        return self.snapshot(entries, span);
                    }
                }
                Ok(source.clone())
            }
        }
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

fn relation(source: &Selection, destination: &Path, span: Span) -> Result<()> {
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
