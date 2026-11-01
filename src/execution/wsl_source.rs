//! Ordinary bulk acquisition from WSL into the current transfer's staging path.

use std::ffi::OsString;
use std::path::Path;

use crate::endpoint::{Backend, Location};
use crate::language::DestinationMode;
use crate::syntax::Span;

use super::Options;
use super::error::{ExecutionError, Result};

pub(super) struct Request<'a> {
    pub source: &'a Location,
    pub stage: &'a Path,
    pub mode: DestinationMode,
    pub pattern: Option<&'a str>,
}

#[derive(Debug)]
pub(super) enum Acquisition {
    Captured { basename: OsString },
    Stream,
}

pub(super) fn acquire(request: Request<'_>, options: &Options, span: Span) -> Result<Acquisition> {
    options.check(span)?;
    let mut trace = crate::telemetry::span("ordinary.wsl.acquire");
    trace.expect_completion();
    trace.attribute("source.path", &request.source.path);
    trace.attribute("destination.path", request.stage.to_string_lossy());
    trace.attribute(
        "destination.mode",
        match (request.pattern.is_some(), request.mode) {
            (true, _) => "selected",
            (_, DestinationMode::Exact) => "exact",
            (_, DestinationMode::Container) => "container",
            (_, DestinationMode::ReplaceTree) => "replace",
        },
    );
    let result = acquire_inner(request, options, span);
    trace.finish_result(&result);
    if options.cancel.is_cancelled() {
        trace.cancelled();
    }
    result
}

fn acquire_inner(request: Request<'_>, options: &Options, span: Span) -> Result<Acquisition> {
    let stage = request.stage.to_string_lossy();
    let output = match request.pattern {
        Some(pattern) => {
            let listing = command(SELECT_LIST, &[&request.source.path], options, span)?;
            let names = selected_names(&listing, pattern, span)?;
            let mut arguments = vec![request.source.path.as_str(), stage.as_ref()];
            arguments.extend(names.iter().map(String::as_str));
            command(SELECT_CAPTURE, &arguments, options, span)?
        }
        None => command(CAPTURE, &[&request.source.path, &stage], options, span)?,
    };
    receipt(&output, span)
}

fn selected_names(bytes: &[u8], pattern: &str, span: Span) -> Result<Vec<String>> {
    let regex = regex::Regex::new(&format!("\\A(?:{pattern})\\z"))
        .map_err(|cause| ExecutionError::caused("invalid selector regex", span, cause))?;
    if !bytes.ends_with(&[0]) {
        return Err(ExecutionError::new("incomplete WSL selector listing", span));
    }
    Ok(bytes[..bytes.len() - 1]
        .split(|byte| *byte == 0)
        .filter_map(|name| std::str::from_utf8(name).ok())
        .filter(|name| !name.is_empty() && regex.is_match(name))
        .map(str::to_owned)
        .collect())
}

fn command(script: &str, args: &[&str], options: &Options, span: Span) -> Result<Vec<u8>> {
    options.check(span)?;
    let output = crate::runtime::remote_command_with_cancel(
        &Backend::Wsl,
        script,
        args,
        options.limits.buffer_bytes,
        &options.cancel,
    )
    .map_err(|message| ExecutionError::new(format!("cannot copy WSL source: {message}"), span))?;
    options.check(span)?;
    Ok(output.stdout)
}

fn receipt(bytes: &[u8], span: Span) -> Result<Acquisition> {
    if bytes == b"stream\0" {
        return Ok(Acquisition::Stream);
    }
    let basename = bytes
        .strip_prefix(b"captured\0")
        .and_then(|name| name.strip_suffix(&[0]))
        .ok_or_else(|| ExecutionError::new("incomplete WSL acquisition receipt", span))?;
    let basename = std::str::from_utf8(basename)
        .map_err(|cause| ExecutionError::caused("WSL basename is not UTF-8", span, cause))?;
    Ok(Acquisition::Captured {
        basename: basename.into(),
    })
}

const SELECT_LIST: &str = r#"
set -eu
s=$1
case $s in '~') s=$HOME;; '~/'*) s=$HOME/${s#\~/};; esac
find -H "$s" -mindepth 1 -maxdepth 1 -printf '%f\0'
printf '\0'
"#;

const CAPTURE: &str = r#"
set -eu
s=$1
case $s in '~') s=$HOME;; '~/'*) s=$HOME/${s#\~/};; esac
if [ -p "$s" ] || [ -S "$s" ]; then printf 'stream\0'; exit 0; fi
d=$(wslpath -a -u "$2")
cp -a -- "$s" "$d"
while [ "$s" != / ] && [ "${s%/}" != "$s" ]; do s=${s%/}; done
printf 'captured\0%s\0' "${s##*/}"
"#;

const SELECT_CAPTURE: &str = r#"
set -eu
s=$1
case $s in '~') s=$HOME;; '~/'*) s=$HOME/${s#\~/};; esac
d=$(wslpath -a -u "$2")
shift 2
mkdir -- "$d"
for n do cp -a -- "$s/$n" "$d/$n"; done
chmod --reference="$s" -- "$d"
touch -r "$s" -- "$d"
while [ "$s" != / ] && [ "${s%/}" != "$s" ]; do s=${s%/}; done
printf 'captured\0%s\0' "${s##*/}"
"#;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn selector_names_are_anchored_without_visiting_unselected_children() {
        let names = selected_names(
            b"keep.txt\0not-keep.txt\0picked\0secret\0\0",
            "keep\\.txt|picked",
            Span::default(),
        )
        .unwrap();
        assert_eq!(names, ["keep.txt", "picked"]);
        assert_eq!(
            selected_names(b"\xff\0keep.txt\0", ".*", Span::default()).unwrap(),
            ["keep.txt"]
        );
        assert!(selected_names(b"keep.txt", ".*", Span::default()).is_err());
        assert!(selected_names(b"\0", "[", Span::default()).is_err());
    }

    #[test]
    fn acquisition_receipt_has_no_manifest_or_quiescence_proof() {
        assert!(matches!(
            receipt("captured\0source 可莉\n\0".as_bytes(), Span::default()).unwrap(),
            Acquisition::Captured { basename } if basename == "source 可莉\n"
        ));
        assert!(matches!(
            receipt(b"stream\0", Span::default()).unwrap(),
            Acquisition::Stream
        ));
        assert!(receipt(b"captured\0source", Span::default()).is_err());
    }

    #[cfg(unix)]
    fn helper(script: &str, arguments: &[&Path]) -> std::process::Output {
        let script = format!("wslpath() {{ printf '%s' \"$3\"; }}\n{script}");
        std::process::Command::new("sh")
            .args(["-c", &script, "wsl-acquisition-test"])
            .args(arguments)
            .output()
            .unwrap()
    }

    #[cfg(unix)]
    fn workspace() -> tempfile::TempDir {
        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        std::fs::create_dir_all(&temporary).unwrap();
        tempfile::Builder::new()
            .prefix("wsl-copy-")
            .tempdir_in(temporary)
            .unwrap()
    }

    #[cfg(unix)]
    #[test]
    fn one_bulk_copy_preserves_readonly_tree_times_and_links_through_root_alias() {
        use std::os::unix::fs::{PermissionsExt, symlink};
        use std::time::{Duration, UNIX_EPOCH};
        let root = workspace();
        let source = root.path().join("source\n");
        std::fs::create_dir(&source).unwrap();
        std::fs::write(source.join("file"), b"copied bytes").unwrap();
        symlink("file", source.join("link")).unwrap();
        let epoch = UNIX_EPOCH + Duration::new(1_700_000_000, 123_456_789);
        let file = std::fs::File::open(source.join("file")).unwrap();
        file.set_modified(epoch).unwrap();
        std::fs::set_permissions(source.join("file"), std::fs::Permissions::from_mode(0o400))
            .unwrap();
        std::fs::File::open(&source)
            .unwrap()
            .set_modified(epoch)
            .unwrap();
        std::fs::set_permissions(&source, std::fs::Permissions::from_mode(0o500)).unwrap();
        symlink(root.path(), root.path().join("alias")).unwrap();
        let alias_source = root.path().join("alias/source\n");
        let destination = root.path().join("copied");
        let output = helper(CAPTURE, &[&alias_source, &destination]);
        assert!(output.status.success(), "{output:?}");
        assert!(
            matches!(receipt(&output.stdout, Span::default()).unwrap(), Acquisition::Captured { basename } if basename == "source\n")
        );
        assert_eq!(
            std::fs::read(destination.join("file")).unwrap(),
            b"copied bytes"
        );
        assert_eq!(
            std::fs::read_link(destination.join("link")).unwrap(),
            Path::new("file")
        );
        assert_eq!(
            std::fs::metadata(destination.join("file"))
                .unwrap()
                .permissions()
                .mode()
                & 0o777,
            0o400
        );
        assert_eq!(
            std::fs::metadata(&destination)
                .unwrap()
                .permissions()
                .mode()
                & 0o777,
            0o500
        );
        assert_eq!(
            std::fs::metadata(destination.join("file"))
                .unwrap()
                .modified()
                .unwrap(),
            epoch
        );
        assert_eq!(
            std::fs::metadata(&destination).unwrap().modified().unwrap(),
            epoch
        );
        std::fs::set_permissions(source, std::fs::Permissions::from_mode(0o700)).unwrap();
        std::fs::set_permissions(destination, std::fs::Permissions::from_mode(0o700)).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn sparse_copy_does_not_acquire_unselected_fifo_or_secret_tree() {
        use std::os::unix::fs::{PermissionsExt, symlink};
        let root = workspace();
        let source = root.path().join("source");
        std::fs::create_dir(&source).unwrap();
        std::fs::write(source.join("chosen"), b"visible").unwrap();
        std::fs::create_dir(source.join("secret")).unwrap();
        std::fs::set_permissions(source.join("secret"), std::fs::Permissions::from_mode(0))
            .unwrap();
        assert!(
            std::process::Command::new("mkfifo")
                .arg(source.join("pipe"))
                .status()
                .unwrap()
                .success()
        );
        let alias = root.path().join("selected-alias");
        symlink(&source, &alias).unwrap();
        let listing = helper(SELECT_LIST, &[&alias]);
        assert!(listing.status.success(), "{listing:?}");
        assert_eq!(
            selected_names(&listing.stdout, "chosen", Span::default()).unwrap(),
            ["chosen"]
        );
        let destination = root.path().join("copy");
        let output = helper(SELECT_CAPTURE, &[&alias, &destination, Path::new("chosen")]);
        assert!(output.status.success(), "{output:?}");
        assert_eq!(
            std::fs::read(destination.join("chosen")).unwrap(),
            b"visible"
        );
        assert_eq!(std::fs::read_dir(destination).unwrap().count(), 1);
        std::fs::set_permissions(
            source.join("secret"),
            std::fs::Permissions::from_mode(0o700),
        )
        .unwrap();
    }
}
