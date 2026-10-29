//! Runtime-only physical path normalization preserving traversal obligations.
use super::error::{ExecutionError, Result};
use crate::syntax::Span;
use std::fs;
use std::path::{Component, Path, PathBuf};
pub(super) fn normalize(path: &Path, span: Span) -> Result<PathBuf> {
    let native = crate::runtime::native_path(path.to_owned());
    let path = native.as_path();
    super::path_policy::validate_case_policy(path, span)?;
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
