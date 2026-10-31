//! Filesystem effects for typed control expressions and lazy range admission.

use std::io;
use std::path::{Path, PathBuf};

use regex::Regex;
use tokio_util::sync::CancellationToken;

use crate::endpoint::{Backend, Location};
use crate::syntax::Span;

use super::error::{ExecutionError, Result};

pub(super) async fn exists(
    location: &Location,
    cancel: &CancellationToken,
    span: Span,
) -> Result<bool> {
    check(cancel, span)?;
    if location.backend == Backend::Path {
        return path_exists(location, cancel, span).await;
    }
    if let Some(path) = local(location, true, span)? {
        return match tokio::fs::metadata(&path).await {
            Ok(_) => Ok(true),
            Err(cause) if cause.kind() == io::ErrorKind::NotFound => Ok(false),
            Err(cause) => Err(io_error("inspect existence of", location, span, cause)),
        };
    }
    let output = remote(location, "exists", cancel, span).await?;
    match output.as_slice() {
        b"true" => Ok(true),
        b"false" => Ok(false),
        _ => Err(ExecutionError::new(
            format!("invalid existence response from {location}"),
            span,
        )),
    }
}

pub(super) async fn delete(
    location: &Location,
    cancel: &CancellationToken,
    span: Span,
) -> Result<()> {
    check(cancel, span)?;
    if location.backend == Backend::Path {
        return Err(ExecutionError::new(
            format!("cannot delete from the readonly PATH namespace: {location}"),
            span,
        ));
    }
    if let Some(path) = local(location, false, span)? {
        let metadata = tokio::fs::symlink_metadata(&path)
            .await
            .map_err(|cause| io_error("inspect deletion target", location, span, cause))?;
        if !metadata.is_file() && !metadata.file_type().is_symlink() {
            return Err(ExecutionError::new(
                format!(
                    "delete requires a file or symbolic link, not a directory or special entry: {location}"
                ),
                span,
            ));
        }
        check(cancel, span)?;
        return tokio::fs::remove_file(&path)
            .await
            .map_err(|cause| io_error("delete", location, span, cause));
    }
    remote(location, "delete", cancel, span).await?;
    Ok(())
}

pub(super) async fn enumerate_regex(
    location: &Location,
    regex: &Regex,
    cancel: &CancellationToken,
    span: Span,
) -> Result<Vec<Location>> {
    check(cancel, span)?;
    if location.backend == Backend::Path {
        return Err(ExecutionError::new(
            format!("a PATH item is not an enumerable directory: {location}"),
            span,
        ));
    }
    let names = if let Some(path) = local(location, true, span)? {
        local_names(&path, location, cancel, span).await?
    } else {
        let output = remote(location, "enumerate", cancel, span).await?;
        remote_names(output, location, span)?
    };
    let mut names: Vec<String> = names
        .into_iter()
        .filter(|name| regex.is_match(name))
        .collect();
    names.sort_unstable();
    Ok(names
        .into_iter()
        .map(|name| Location {
            backend: location.backend.clone(),
            path: join_name(&location.path, &name),
        })
        .collect())
}

#[cfg(test)]
async fn enumerate(
    location: &Location,
    pattern: &str,
    cancel: &CancellationToken,
    span: Span,
) -> Result<Vec<Location>> {
    let regex = Regex::new(&format!(r"\A(?:{pattern})\z"))
        .map_err(|cause| ExecutionError::caused("invalid range selector", span, cause))?;
    enumerate_regex(location, &regex, cancel, span).await
}

fn check(cancel: &CancellationToken, span: Span) -> Result<()> {
    if cancel.is_cancelled() {
        return Err(ExecutionError::cancelled(
            "filesystem control operation cancelled before admission",
            span,
        ));
    }
    Ok(())
}

fn local(location: &Location, source: bool, span: Span) -> Result<Option<PathBuf>> {
    crate::runtime::local_path(location, source).map_err(|message| {
        ExecutionError::new(
            format!("cannot resolve control path {location}: {message}"),
            span,
        )
    })
}

fn io_error(action: &str, location: &Location, span: Span, cause: io::Error) -> ExecutionError {
    ExecutionError::caused(format!("cannot {action} {location}"), span, cause)
}

async fn path_exists(location: &Location, cancel: &CancellationToken, span: Span) -> Result<bool> {
    let name = Path::new(&location.path);
    if name.components().count() != 1 {
        return Err(ExecutionError::new(
            "PATH existence requires a single file name",
            span,
        ));
    }
    let search = std::env::var_os("PATH").unwrap_or_default();
    for directory in std::env::split_paths(&search) {
        check(cancel, span)?;
        match tokio::fs::metadata(directory.join(name)).await {
            Ok(metadata) if metadata.is_file() => return Ok(true),
            Ok(_) => {}
            Err(cause) if cause.kind() == io::ErrorKind::NotFound => {}
            Err(cause) => return Err(io_error("inspect PATH entry for", location, span, cause)),
        }
    }
    Ok(false)
}

async fn local_names(
    path: &Path,
    location: &Location,
    cancel: &CancellationToken,
    span: Span,
) -> Result<Vec<String>> {
    let mut directory = tokio::fs::read_dir(path)
        .await
        .map_err(|cause| io_error("enumerate", location, span, cause))?;
    let mut names = Vec::new();
    while let Some(entry) = directory
        .next_entry()
        .await
        .map_err(|cause| io_error("read directory entry in", location, span, cause))?
    {
        check(cancel, span)?;
        let name = entry.file_name().into_string().map_err(|name| {
            ExecutionError::new(
                format!("range entry name is not UTF-8 in {location}: {name:?}"),
                span,
            )
        })?;
        names.push(name);
    }
    Ok(names)
}

fn remote_names(output: Vec<u8>, location: &Location, span: Span) -> Result<Vec<String>> {
    if output.is_empty() {
        return Ok(Vec::new());
    }
    if output.last() != Some(&0) {
        return Err(ExecutionError::new(
            format!("invalid directory response framing from {location}"),
            span,
        ));
    }
    output[..output.len() - 1]
        .split(|byte| *byte == 0)
        .map(|name| {
            let name = std::str::from_utf8(name).map_err(|cause| {
                ExecutionError::caused(
                    format!("range entry name is not UTF-8 in {location}"),
                    span,
                    cause,
                )
            })?;
            if name.is_empty() || matches!(name, "." | "..") || name.contains('/') {
                return Err(ExecutionError::new(
                    format!("invalid directory entry name from {location}: {name:?}"),
                    span,
                ));
            }
            Ok(name.to_owned())
        })
        .collect()
}

fn join_name(root: &str, name: &str) -> String {
    if root.is_empty() {
        return name.to_owned();
    }
    if root.ends_with('/') {
        return format!("{root}{name}");
    }
    format!("{root}/{name}")
}

async fn remote(
    location: &Location,
    action: &'static str,
    cancel: &CancellationToken,
    span: Span,
) -> Result<Vec<u8>> {
    if !matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }) {
        return Err(ExecutionError::new(
            format!("{action} requires a filesystem endpoint, found {location}"),
            span,
        ));
    }
    if location.path.contains('\0') {
        return Err(ExecutionError::new(
            "remote control path contains NUL",
            span,
        ));
    }
    let location = location.clone();
    let cancel = cancel.clone();
    crate::runtime::ssh::spawn_blocking(move || {
        check(&cancel, span)?;
        crate::runtime::remote_command_with_cancel(
            &location.backend,
            REMOTE_CONTROL,
            &[action, &location.path],
            64 * 1024,
            &cancel,
        )
        .map(|output| output.stdout)
        .map_err(|message| {
            if cancel.is_cancelled() {
                return ExecutionError::cancelled(
                    format!("remote {action} cancelled for {location}: {message}"),
                    span,
                );
            }
            ExecutionError::new(format!("cannot {action} {location}: {message}"), span)
        })
    })
    .await
    .map_err(|cause| ExecutionError::caused("filesystem control worker failed", span, cause))?
}

// Arguments are passed through the existing transport argv quoting owner. The
// probe checks searchable ancestors so access failures are not reported as false.
const REMOTE_CONTROL: &str = r#"
set -eu
action=$1; p=$2
case $p in '~') p=$HOME;; '~/'*) p=$HOME/${p#\~/};; esac
case $p in /*) ;; *) p=$PWD/$p;; esac
fail() { printf '%s\n' "$*" >&2; exit 1; }
check_parent() {
    q=${p%/*}; [ -n "$q" ] || q=/
    while :; do
        if [ -d "$q" ]; then
            [ -x "$q" ] || fail "directory is not searchable: $q"
        elif [ -e "$q" ]; then
            fail "ancestor is not a directory: $q"
        fi
        [ "$q" != / ] || break
        q=${q%/*}; [ -n "$q" ] || q=/
    done
}
check_parent
case $action in
exists)
    # stat follows links and preserves errors such as ELOOP and EACCES. A failed
    # shell -e test alone cannot distinguish those from a missing target.
    LC_ALL=C; export LC_ALL
    if error=$(stat -L -c '' -- "$p" 2>&1); then printf true
    else
        case $error in *': No such file or directory') printf false;; *) fail "$error";; esac
    fi
    ;;
delete)
    [ -L "$p" ] || [ -f "$p" ] || fail "delete requires a file or symbolic link: $p"
    rm -- "$p"
    ;;
enumerate)
    [ -d "$p" ] || fail "range root is not a directory: $p"
    [ -r "$p" ] && [ -x "$p" ] || fail "range root is not readable and searchable: $p"
    for entry in "$p"/* "$p"/.[!.]* "$p"/..?*; do
        [ -e "$entry" ] || [ -L "$entry" ] || continue
        printf '%s\0' "${entry##*/}"
    done
    ;;
*) fail 'unknown filesystem control operation';;
esac
"#;

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture() -> tempfile::TempDir {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/control-fs-tests");
        std::fs::create_dir_all(&root).unwrap();
        tempfile::tempdir_in(root).unwrap()
    }

    fn location(path: &Path) -> Location {
        Location {
            backend: Backend::Local,
            path: path.to_str().expect("UTF-8 fixture").to_owned(),
        }
    }

    #[tokio::test]
    async fn existence_and_delete_observe_each_effect() {
        let directory = fixture();
        let path = directory.path().join("message.txt");
        let location = location(&path);
        let cancel = CancellationToken::new();
        assert!(!exists(&location, &cancel, Span::default()).await.unwrap());
        tokio::fs::write(&path, b"hello").await.unwrap();
        assert!(exists(&location, &cancel, Span::default()).await.unwrap());
        delete(&location, &cancel, Span::default()).await.unwrap();
        assert!(!exists(&location, &cancel, Span::default()).await.unwrap());
        assert!(delete(&location, &cancel, Span::default()).await.is_err());
    }

    #[tokio::test]
    async fn delete_rejects_directories_and_cancelled_effects() {
        let directory = fixture();
        let root = location(directory.path());
        let cancel = CancellationToken::new();
        assert!(delete(&root, &cancel, Span::default()).await.is_err());
        let path = directory.path().join("retained");
        tokio::fs::write(&path, b"retained").await.unwrap();
        cancel.cancel();
        assert!(
            delete(&location(&path), &cancel, Span::default())
                .await
                .is_err()
        );
        assert!(path.is_file());
    }

    #[tokio::test]
    async fn ranges_match_whole_names_and_sort_unicode() {
        let directory = fixture();
        for name in ["z.txt", "α.txt", "a.txt", "a.txt.bak", ".hidden.txt"] {
            tokio::fs::write(directory.path().join(name), b"")
                .await
                .unwrap();
        }
        let paths = enumerate(
            &location(directory.path()),
            r".*\.txt",
            &CancellationToken::new(),
            Span::default(),
        )
        .await
        .unwrap();
        let names: Vec<_> = paths
            .iter()
            .map(|item| Path::new(&item.path).file_name().unwrap().to_str().unwrap())
            .collect();
        assert_eq!(names, [".hidden.txt", "a.txt", "z.txt", "α.txt"]);
    }

    #[cfg(unix)]
    #[tokio::test]
    async fn deleting_a_link_does_not_follow_it() {
        let directory = fixture();
        let target = directory.path().join("target");
        let link = directory.path().join("link");
        tokio::fs::write(&target, b"safe").await.unwrap();
        std::os::unix::fs::symlink(&target, &link).unwrap();
        delete(&location(&link), &CancellationToken::new(), Span::default())
            .await
            .unwrap();
        assert_eq!(tokio::fs::read(&target).await.unwrap(), b"safe");
        std::os::unix::fs::symlink(directory.path().join("missing"), &link).unwrap();
        assert!(
            !exists(&location(&link), &CancellationToken::new(), Span::default())
                .await
                .unwrap()
        );
        tokio::fs::remove_file(&link).await.unwrap();
        std::os::unix::fs::symlink(&link, &link).unwrap();
        assert!(
            exists(&location(&link), &CancellationToken::new(), Span::default())
                .await
                .is_err()
        );
    }

    #[cfg(unix)]
    #[test]
    fn remote_shell_distinguishes_missing_cycles_and_directory_deletion() {
        let directory = fixture();
        let target = directory.path().join("literal '$(false)' target");
        let cycle = directory.path().join("cycle");
        let dangling = directory.path().join("dangling");
        std::fs::write(&target, b"retained").unwrap();
        std::os::unix::fs::symlink(&cycle, &cycle).unwrap();
        std::os::unix::fs::symlink(directory.path().join("missing"), &dangling).unwrap();
        let run = |action: &str, path: &Path| {
            std::process::Command::new("sh")
                .args(["-c", REMOTE_CONTROL, "sh", action])
                .arg(path)
                .output()
                .unwrap()
        };
        assert_eq!(run("exists", &target).stdout, b"true");
        assert_eq!(run("exists", &dangling).stdout, b"false");
        assert!(!run("exists", &cycle).status.success());
        assert!(!run("delete", directory.path()).status.success());
        assert!(run("delete", &dangling).status.success());
        assert_eq!(std::fs::read(&target).unwrap(), b"retained");
    }

    #[test]
    fn remote_framing_rejects_invalid_entry_names() {
        let root = Location {
            backend: Backend::Wsl,
            path: "/tmp".into(),
        };
        assert!(remote_names(b"one\0two\0".to_vec(), &root, Span::default()).is_ok());
        for output in [b"incomplete".as_slice(), b"../escape\0", b"\0"] {
            assert!(remote_names(output.to_vec(), &root, Span::default()).is_err());
        }
    }
}
