//! Exact, container and whole-tree assignment policies for finite statements.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use crate::filesystem::{Entry, NodeMetadata, Snapshot};
use crate::language::DestinationMode;
use crate::syntax::Span;

use super::error::{ExecutionError, Result};

pub(super) enum Target<'a> {
    Missing,
    File(&'a NodeMetadata),
    Directory(Option<&'a Snapshot>),
}

pub(super) fn assign(
    source: &Snapshot,
    destination: Target<'_>,
    mode: DestinationMode,
    literal: bool,
    span: Span,
    build: impl FnOnce(BTreeMap<PathBuf, Entry>) -> Result<Snapshot>,
) -> Result<Snapshot> {
    match mode {
        DestinationMode::ReplaceTree => {
            if !source.is_directory() {
                return Err(ExecutionError::new(
                    "explicit '/.' replacement requires a directory source",
                    span,
                ));
            }
            if matches!(destination, Target::File(_)) {
                return Err(ExecutionError::new(
                    "explicit '/.' replacement requires a directory destination or an absent path",
                    span,
                ));
            }
            Ok(source.clone())
        }
        DestinationMode::Container => {
            if matches!(destination, Target::Missing) {
                return Ok(source.clone());
            }
            let Target::Directory(Some(destination)) = destination else {
                if !source.is_directory() && matches!(destination, Target::File(_)) {
                    return Ok(source.clone());
                }
                return Err(ExecutionError::new(
                    "source and destination entry kinds differ",
                    span,
                ));
            };
            if !source.is_directory() {
                return Err(ExecutionError::new(
                    "a file cannot replace a directory",
                    span,
                ));
            }
            let mut entries = destination.entries().clone();
            for (path, entry) in source.entries() {
                match entries.get(path) {
                    Some(Entry::Directory { .. }) if matches!(entry, Entry::Directory { .. }) => {}
                    Some(previous)
                        if matches!(previous, Entry::Directory { .. })
                            != matches!(entry, Entry::Directory { .. }) =>
                    {
                        return Err(ExecutionError::new(
                            format!("source and destination kinds differ at {}", path.display()),
                            span,
                        ));
                    }
                    _ => {
                        entries.insert(path.clone(), entry.clone());
                    }
                }
            }

            build(entries)
        }
        DestinationMode::Exact => {
            if matches!(destination, Target::Directory(_)) {
                return Err(ExecutionError::new("the exact destination is an existing directory", span)
                    .help("Append '/' for container insertion, or '/.' to explicitly replace the directory."));
            }
            if source.is_directory() && !matches!(destination, Target::Missing) {
                return Err(ExecutionError::new(
                    "a directory source cannot replace an existing ordinary file",
                    span,
                ));
            }
            if let Target::File(metadata) = destination
                && literal
            {
                let mut entries = source.entries().clone();
                if let Some(Entry::File {
                    metadata: source_metadata,
                    ..
                }) = entries.get_mut(Path::new(""))
                {
                    source_metadata.permissions = metadata.permissions.clone();
                }
                return build(entries);
            }
            Ok(source.clone())
        }
    }
}
