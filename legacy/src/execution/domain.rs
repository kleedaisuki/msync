//! Runtime paths follow the selected native namespace, including directory links.

use super::error::{ExecutionError, Result};
use crate::syntax::Span;
use std::fs;
use std::path::{Component, Path, PathBuf};

pub(super) fn normalize(path: &Path, span: Span) -> Result<PathBuf> {
    let native = crate::runtime::native_path(path.to_owned());
    let absolute = std::path::absolute(&native).map_err(|cause| {
        ExecutionError::caused(format!("cannot resolve {}", path.display()), span, cause)
    })?;
    let mut existing = absolute.as_path();
    let mut missing = Vec::new();
    let mut root = loop {
        match fs::canonicalize(existing) {
            Ok(root) => break root,
            Err(cause) if cause.kind() == std::io::ErrorKind::NotFound => {
                let name = match existing.components().next_back() {
                    Some(Component::Normal(name)) => Some(name),
                    Some(Component::ParentDir) => Some(std::ffi::OsStr::new("..")),
                    _ => None,
                };
                let (Some(parent), Some(name)) = (existing.parent(), name) else {
                    return Err(ExecutionError::caused(
                        format!("cannot resolve {}", path.display()),
                        span,
                        cause,
                    ));
                };
                missing.push(name.to_owned());
                existing = parent;
            }
            Err(cause) => {
                return Err(ExecutionError::caused(
                    format!("cannot resolve {}", path.display()),
                    span,
                    cause,
                ));
            }
        }
    };
    for name in missing.into_iter().rev() {
        root.push(name);
    }
    Ok(root)
}

#[cfg(all(test, unix))]
mod tests {
    use super::*;

    #[test]
    fn selected_directory_links_resolve_existing_and_future_children() {
        let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&base).unwrap();
        let work = tempfile::tempdir_in(base).unwrap();
        let real = work.path().join("real/nested");
        fs::create_dir_all(&real).unwrap();
        fs::write(real.join("input"), b"selected bytes").unwrap();
        let alias = work.path().join("alias");
        std::os::unix::fs::symlink(&real, &alias).unwrap();
        let span = Span::default();
        assert_eq!(
            normalize(&alias.join("input"), span).unwrap(),
            real.join("input")
        );
        assert_eq!(
            normalize(&alias.join("future/file"), span).unwrap(),
            real.join("future/file")
        );
        assert_eq!(
            normalize(&alias.join("../future"), span).unwrap(),
            real.parent().unwrap().join("future")
        );
    }

    #[test]
    fn native_traversal_errors_are_not_lexically_erased() {
        let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&base).unwrap();
        let work = tempfile::tempdir_in(base).unwrap();
        let ordinary = work.path().join("file");
        fs::write(&ordinary, b"not a directory").unwrap();
        assert!(normalize(&ordinary.join("../target"), Span::default()).is_err());
        let missing = work.path().join("missing/../target");
        assert_eq!(normalize(&missing, Span::default()).unwrap(), missing);
        assert!(fs::metadata(missing).is_err());
    }
}
