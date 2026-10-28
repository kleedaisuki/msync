//! Assignment policy shared by statement journals and retained transactions.

use std::collections::BTreeMap;
use std::io;
use std::path::{Path, PathBuf};

use crate::language::DestinationMode;
use crate::syntax::Span;
use crate::transaction::filesystem::{Entry, NodeMetadata, Snapshot};

use super::error::{ExecutionError, Result};

pub(super) enum Target<'a> {
    Missing,
    File(&'a NodeMetadata),
    Directory(Option<&'a Snapshot>),
}

impl<'a> Target<'a> {
    pub fn snapshot(snapshot: &'a Snapshot) -> Self {
        match snapshot.entries().get(Path::new("")) {
            None => Self::Missing,
            Some(Entry::File { metadata, .. }) => Self::File(metadata),
            Some(Entry::Directory { .. }) => Self::Directory(Some(snapshot)),
        }
    }
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
                return Err(ExecutionError::new("destination entry already exists in the container", span)
                    .help("Container insertion never overwrites files; choose an exact target for replacement or '/.' for a complete directory replacement."));
            };
            if !source.is_directory() {
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
            if let Target::File(metadata) = destination {
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
                    return build(entries);
                }
            }
            Ok(source.clone())
        }
    }
}
