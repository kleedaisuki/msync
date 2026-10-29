//! Selection and alias constraints shared by statement-local assignments.

use std::collections::BTreeMap;
use std::ffi::OsString;
use std::path::{Path, PathBuf};

use regex::Regex;

use crate::filesystem::{Entry, Snapshot};
use crate::syntax::Span;

use super::error::{ExecutionError, Result};

pub(super) struct Selection {
    pub snapshot: Snapshot,
    pub basename: Option<OsString>,
    pub source: Option<PathBuf>,
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
