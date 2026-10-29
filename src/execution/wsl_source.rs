//! Guarded bulk acquisition into an invisible, invocation-owned import path.

use std::ffi::OsString;
use std::path::{Path, PathBuf};

use crate::endpoint::{Backend, Location};
use crate::filesystem::InvocationRoot;
use crate::language::DestinationMode;
use crate::syntax::Span;

use super::Options;
use super::error::{ExecutionError, Result};

pub(super) struct Request<'a> {
    pub source: &'a Location,
    pub stage: &'a Path,
    pub logical_target: &'a Path,
    pub mode: DestinationMode,
    pub pattern: Option<&'a str>,
    pub invocation: &'a InvocationRoot,
}

#[derive(Debug)]
pub(super) enum Acquisition {
    Captured { basename: OsString },
    Stream,
    Fallback,
}

struct Receipt<'a> {
    kind: &'a str,
    source: &'a str,
    fingerprint: &'a str,
    manifest: &'a [u8],
}

pub(super) fn acquire(request: Request<'_>, options: &Options, span: Span) -> Result<Acquisition> {
    options.check(span)?;
    let mut trace = crate::telemetry::span("ordinary.wsl.acquire");
    trace.attribute("source.path", &request.source.path);
    trace.attribute("publication.policy", "private-import");
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
    result
}

/// Resolve an unproven WSL projection through the guest, not a `/mnt/X` guess.
/// The ordinary owner then uses the same local identity and private-view rules
/// as a native source instead of erasing aliases behind an imported copy.
pub(super) fn projected_source(
    source: &Location,
    options: &Options,
    span: Span,
) -> Result<PathBuf> {
    let output = command(PROJECTED_SOURCE, &[&source.path], options, span)?;
    let path = output
        .strip_suffix(&[0])
        .ok_or_else(|| ExecutionError::new("WSL projection receipt is incomplete", span))?;
    let path = std::str::from_utf8(path)
        .map_err(|cause| ExecutionError::caused("WSL projection is not UTF-8", span, cause))?;
    #[cfg(windows)]
    {
        super::domain::normalize(Path::new(path), span)
    }
    #[cfg(not(windows))]
    {
        let _ = path;
        Err(ExecutionError::new(
            "this WSL projection requires a Windows filesystem view",
            span,
        )
        .help("Use the local namespace inside WSL, or run the Windows msync executable."))
    }
}

const PROJECTED_SOURCE: &str = r#"
set -eu
p=$1
case $p in '~') p=$HOME;; '~/'*) p=$HOME/${p#\~/};; esac
case $p in /*) ;; *) p=$PWD/$p;; esac
a=$p
while [ "$a" != / ]; do
    [ ! -L "$a" ] || { echo "source traverses a symbolic link: $a" >&2; exit 1; }
    a=${a%/*}; [ -n "$a" ] || a=/
done
p=$(realpath -e -- "$p")
[ -f "$p" ] || [ -d "$p" ] || { echo 'projected source must be a finite file or directory' >&2; exit 1; }
w=$(wslpath -a -w "$p")
printf '%s\0' "$w"
"#;

fn acquire_inner(request: Request<'_>, options: &Options, span: Span) -> Result<Acquisition> {
    if request.source.backend != Backend::Wsl {
        return Ok(Acquisition::Fallback);
    }
    let manifest = manifest_path(request.stage);
    for path in [request.stage, manifest.as_path()] {
        let owned = request.invocation.contains_private(path).map_err(|cause| {
            ExecutionError::caused("cannot verify private WSL import", span, cause)
        })?;
        if !owned
            || path.try_exists().map_err(|cause| {
                ExecutionError::caused("cannot inspect private WSL import", span, cause)
            })?
        {
            return Err(ExecutionError::new(
                "WSL acquisition needs a fresh invocation-owned path",
                span,
            ));
        }
    }
    let destination = Location {
        backend: Backend::Local,
        path: request.logical_target.to_string_lossy().into_owned(),
    };
    let native_target = crate::runtime::native_windows_filesystem(&destination);
    if !native_target {
        return Ok(Acquisition::Fallback);
    }
    if let Some(pattern) = request.pattern {
        return acquire_selected(&request, pattern, options, span);
    }
    let stage = request.stage.to_string_lossy();
    let preflight = command(PREFLIGHT, &[&request.source.path, &stage], options, span)?;
    if preflight == b"stream\0" {
        return Ok(Acquisition::Stream);
    }
    if preflight == b"fallback\0" {
        return Ok(Acquisition::Fallback);
    }
    let before = receipt(&preflight, "ready", span)?;
    let listing = names(before.manifest, span)?;
    crate::runtime::validate_remote_names(&listing)
        .map_err(|message| ExecutionError::new(message, span))?;
    options.check(span)?;
    let captured = command(
        CAPTURE,
        &[before.source, &stage, before.kind, before.fingerprint],
        options,
        span,
    )?;
    let after = receipt(&captured, "captured", span)?;
    if before.kind != after.kind
        || before.source != after.source
        || before.fingerprint != after.fingerprint
        || before.manifest != after.manifest
    {
        return Err(changed(span));
    }
    options.check(span)?;
    let basename = before
        .source
        .rsplit('/')
        .next()
        .filter(|name| !name.is_empty())
        .ok_or_else(|| ExecutionError::new("WSL source has no basename", span))?;
    Ok(Acquisition::Captured {
        basename: basename.into(),
    })
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

fn acquire_selected(
    request: &Request<'_>,
    pattern: &str,
    options: &Options,
    span: Span,
) -> Result<Acquisition> {
    let listing = command(SELECT_LIST, &[&request.source.path], options, span)?;
    if listing == b"fallback\0" {
        return Ok(Acquisition::Fallback);
    }
    let chosen = selected_names(&listing, pattern, span)?;
    let mut validation = Vec::new();
    for name in &chosen {
        validation.extend_from_slice(name.as_bytes());
        validation.push(0);
        validation.extend_from_slice(name.as_bytes());
        validation.push(0);
    }
    crate::runtime::validate_remote_names(&validation)
        .map_err(|message| ExecutionError::new(message, span))?;
    let stage = request.stage.to_string_lossy();
    let mut args = vec![request.source.path.as_str(), stage.as_ref()];
    args.extend(chosen.iter().map(String::as_str));
    let script = format!("{SELECT_COMMON}\n{SELECT_PREFLIGHT}");
    let preflight = command(&script, &args, options, span)?;
    if preflight == b"fallback\0" {
        return Ok(Acquisition::Fallback);
    }
    let before = receipt(&preflight, "ready", span)?;
    crate::runtime::validate_remote_names(&names(before.manifest, span)?)
        .map_err(|message| ExecutionError::new(message, span))?;
    let mut args = vec![before.source, stage.as_ref(), before.fingerprint];
    args.extend(chosen.iter().map(String::as_str));
    let script = format!("{SELECT_COMMON}\n{SELECT_CAPTURE}");
    let captured = command(&script, &args, options, span)?;
    let after = receipt(&captured, "captured", span)?;
    if before.kind != after.kind
        || before.source != after.source
        || before.fingerprint != after.fingerprint
        || before.manifest != after.manifest
    {
        return Err(changed(span));
    }
    let basename = before
        .source
        .rsplit('/')
        .next()
        .filter(|name| !name.is_empty())
        .ok_or_else(|| ExecutionError::new("WSL source has no basename", span))?;
    Ok(Acquisition::Captured {
        basename: basename.into(),
    })
}

// Name discovery is shallow: unselected entries must never be traversed,
// validated as payload, or transmitted across the WSL filesystem boundary.
const SELECT_LIST: &str = r#"
set -eu
p=$1
case $p in '~') p=$HOME;; '~/'*) p=$HOME/${p#\~/};; esac
case $p in /*) ;; *) p=$PWD/$p;; esac
a=$p
while [ "$a" != / ]; do [ ! -L "$a" ] || { echo 'selector traverses a symbolic link' >&2; exit 1; }; a=${a%/*}; [ -n "$a" ] || a=/; done
[ -d "$p" ] || { echo 'selector source must be a directory' >&2; exit 1; }
[ "$(stat -f -c %t -- "$p")" = ef53 ] || { printf 'fallback\0'; exit 0; }
find "$p" -mindepth 1 -maxdepth 1 -printf '%f\0'
printf '\0'
"#;

const SELECT_COMMON: &str = r#"
set -eu
fail() { echo "$*" >&2; exit 1; }
parents() { p=$1; while [ "$p" != / ]; do [ ! -L "$p" ] || fail "refusing symbolic link: $p"; p=${p%/*}; [ -n "$p" ] || p=/; done; }
s=$1
case $s in '~') s=$HOME;; '~/'*) s=$HOME/${s#\~/};; esac
case $s in /*) ;; *) s=$PWD/$s;; esac
parents "$s"
[ -d "$s" ] || fail 'selector source must be a directory'
s=$(realpath -e -- "$s")
[ "$(stat -f -c %t -- "$s")" = ef53 ] || { printf 'fallback\0'; exit 0; }
d=$(wslpath -a -u "$2"); parents "$d"
m=$d.manifest
manifest() {
    # The selector container basename is never materialized across the boundary.
    find "$s" -maxdepth 0 -printf '%P\0selector-root\0%D:%i:%m:%s:%T@:%C@\0'
    for n do
        f=$(printf '%s' "$n" | sed 's/\\/\\\\/g;s/%/%%/g')
        find "$s/$n" -maxdepth 0 -printf "$f\000%f\000%D:%i:%m:%s:%T@:%C@\000"
        if [ -d "$s/$n" ]; then
            find "$s/$n" -mindepth 1 -printf "$f/%P\000%f\000%D:%i:%m:%s:%T@:%C@\000"
        fi
    done
}
validate() {
    dev=$(stat -c %d -- "$s")
    for n do
        case $n in ''|.|..|*/*) fail 'invalid selected basename';; esac
        [ -e "$s/$n" ] || [ -L "$s/$n" ] || fail 'selected source disappeared'
        [ "$(find "$s/$n" -xdev -printf '%D\n' | sort -u)" = "$dev" ] || { printf 'fallback\0'; exit 0; }
        [ -z "$(find "$s/$n" -type l -print -quit)" ] || fail 'selected source contains symbolic links'
        [ -z "$(find "$s/$n" ! -type f ! -type d -print -quit)" ] || fail 'selected source contains special files'
    done
}
"#;

const SELECT_PREFLIGHT: &str = r#"
shift 2
validate "$@"
[ ! -e "$d" ] || fail 'private import path already exists'
umask 077
set -C
: > "$m"
set +C
manifest "$@" > "$m"
h=$(sha256sum -- "$m"); h=${h%% *}
printf 'ready\0directory\0%s\0%s\0' "$s" "$h"
cat -- "$m"
"#;

const SELECT_CAPTURE: &str = r#"
h=$3
shift 3
validate "$@"
[ ! -e "$d" ] || fail 'private import path already exists'
actual=$(sha256sum -- "$m"); actual=${actual%% *}
[ "$actual" = "$h" ] || fail 'private source manifest changed'
manifest "$@" | cmp -s - "$m" || fail 'selected source changed before private acquisition'
mkdir -- "$d"
for n do cp -a -- "$s/$n" "$d/$n"; done
chmod --reference="$s" -- "$d"
touch -r "$s" -- "$d"
manifest "$@" | cmp -s - "$m" || fail 'selected source changed during private acquisition'
printf 'captured\0directory\0%s\0%s\0' "$s" "$h"
cat -- "$m"
"#;

fn manifest_path(stage: &Path) -> PathBuf {
    let mut path = stage.as_os_str().to_owned();
    path.push(".manifest");
    PathBuf::from(path)
}

fn command(script: &str, args: &[&str], options: &Options, span: Span) -> Result<Vec<u8>> {
    options.check(span)?;
    // Killing the Windows launcher does not prove that its guest copy stopped.
    // Finite imports drain normally; owner cancellation is checked before any
    // later capture or publication, with the private journal still retained.
    let drain = tokio_util::sync::CancellationToken::new();
    let output = crate::runtime::remote_command_with_cancel(
        &Backend::Wsl, script, args, options.limits.buffer_bytes, &drain,
    ).map_err(|message| {
        ExecutionError::new(format!("cannot acquire WSL source: {message}"), span)
            .help("The logical destination is unchanged; only invocation-owned import data may have been created.")
    })?;
    options.check(span)?;
    Ok(output.stdout)
}

fn receipt<'a>(bytes: &'a [u8], tag: &str, span: Span) -> Result<Receipt<'a>> {
    let mut fields = bytes.splitn(5, |byte| *byte == 0);
    let label = fields.next().unwrap_or_default();
    let kind = fields.next().unwrap_or_default();
    let source = fields.next().unwrap_or_default();
    let fingerprint = fields.next().unwrap_or_default();
    let manifest = fields.next().unwrap_or_default();
    let decode = |text| {
        std::str::from_utf8(text)
            .map_err(|_| ExecutionError::new("WSL source receipt is not UTF-8", span))
    };
    let (kind, source, fingerprint) = (decode(kind)?, decode(source)?, decode(fingerprint)?);
    if label != tag.as_bytes()
        || !matches!(kind, "regular" | "directory")
        || !source.starts_with('/')
        || fingerprint.len() != 64
        || !fingerprint.bytes().all(|byte| byte.is_ascii_hexdigit())
        || manifest.is_empty()
    {
        return Err(ExecutionError::new(
            "invalid WSL source acquisition receipt",
            span,
        ));
    }
    Ok(Receipt {
        kind,
        source,
        fingerprint,
        manifest,
    })
}

fn names(manifest: &[u8], span: Span) -> Result<Vec<u8>> {
    let mut fields = manifest.split(|byte| *byte == 0);
    let mut listing = Vec::with_capacity(manifest.len());
    while let Some(relative) = fields.next() {
        let Some(name) = fields.next() else {
            if relative.is_empty() {
                break;
            }
            return Err(ExecutionError::new("incomplete WSL source manifest", span));
        };
        let proof = fields
            .next()
            .ok_or_else(|| ExecutionError::new("incomplete WSL source manifest", span))?;
        if name.is_empty() || proof.is_empty() {
            return Err(ExecutionError::new(
                "invalid WSL source manifest entry",
                span,
            ));
        }
        listing.extend_from_slice(relative);
        listing.push(0);
        listing.extend_from_slice(name);
        listing.push(0);
    }
    Ok(listing)
}

fn changed(span: Span) -> ExecutionError {
    ExecutionError::new(
        "WSL source changed during private acquisition; destination unchanged",
        span,
    )
    .help(
        "Retry the statement after the source stops changing. No completed assignment is replayed.",
    )
}

const PREFLIGHT: &str = r#"
set -eu
fail() { echo "$*" >&2; exit 1; }
parents() { p=$1; while [ "$p" != / ]; do [ ! -L "$p" ] || fail "refusing symbolic link: $p"; p=${p%/*}; [ -n "$p" ] || p=/; done; }
case $1 in '~') s=$HOME;; '~/'*) s=$HOME/${1#\~/};; /*) s=$1;; *) s=$PWD/$1;; esac
parents "$s"
if [ -p "$s" ] || [ -S "$s" ]; then printf 'stream\0'; exit 0; fi
[ -e "$s" ] || fail "source does not exist: $s"
if [ -f "$s" ]; then k=regular; elif [ -d "$s" ]; then k=directory; else fail 'unsupported source special file'; fi
s=$(realpath -e -- "$s")
if [ "$(stat -f -c %t -- "$s")" != ef53 ]; then printf 'fallback\0'; exit 0; fi
dev=$(stat -c %d -- "$s")
if [ "$(find "$s" -xdev -printf '%D\n' | sort -u)" != "$dev" ]; then printf 'fallback\0'; exit 0; fi
[ -z "$(find "$s" -type l -print -quit)" ] || fail 'source tree contains symbolic links'
[ -z "$(find "$s" ! -type f ! -type d -print -quit)" ] || fail 'source tree contains special files'
d=$(wslpath -a -u "$2"); parents "$d"
[ ! -e "$d" ] || fail 'private import path already exists'
m=$d.manifest
umask 077
set -C
: > "$m"
set +C
find "$s" -printf '%P\0%f\0%D:%i:%m:%s:%T@:%C@\0' > "$m"
h=$(sha256sum -- "$m"); h=${h%% *}
printf 'ready\0%s\0%s\0%s\0' "$k" "$s" "$h"
cat -- "$m"
"#;

const CAPTURE: &str = r#"
set -eu
fail() { echo "$*" >&2; exit 1; }
s=$1
p=$s; while [ "$p" != / ]; do [ ! -L "$p" ] || fail "refusing symbolic link: $p"; p=${p%/*}; [ -n "$p" ] || p=/; done
d=$(wslpath -a -u "$2")
m=$d.manifest
[ ! -e "$d" ] || fail 'private import path already exists'
h=$(sha256sum -- "$m"); h=${h%% *}
[ "$h" = "$4" ] || fail 'private source manifest changed'
manifest() { find "$s" -printf '%P\0%f\0%D:%i:%m:%s:%T@:%C@\0'; }
manifest | cmp -s - "$m" || fail 'source changed before private acquisition'
# The target is invisible and exclusively owned. Per-leaf public rename journals
# would only add cross-filesystem metadata work before statement publication.
cp -a -- "$s" "$d"
manifest | cmp -s - "$m" || fail 'source changed during private acquisition'
printf 'captured\0%s\0%s\0%s\0' "$3" "$s" "$4"
cat -- "$m"
"#;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn selector_names_are_anchored_and_do_not_admit_unselected_payload() {
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
    fn proof_triples_preserve_utf8_names_for_the_shared_validator() {
        let manifest = "\0agents\0proof-root\0nested/可莉 file.toml\0可莉 file.toml\0proof-file\0";
        let listing = names(manifest.as_bytes(), Span::default()).unwrap();
        assert_eq!(
            listing,
            "\0agents\0nested/可莉 file.toml\0可莉 file.toml\0".as_bytes()
        );
        crate::runtime::validate_remote_names(&listing).unwrap();
    }

    #[test]
    fn incomplete_or_unfingerprinted_receipts_are_not_proofs() {
        for bytes in [
            b"ready\0regular\0/tmp/a\0short\0\0a\0proof\0".as_slice(),
            b"stream".as_slice(),
        ] {
            assert!(receipt(bytes, "ready", Span::default()).is_err());
        }
        assert!(names(b"name\0", Span::default()).is_err());
        assert!(names(b"name\0file\0", Span::default()).is_err());
        assert!(names(b"name\0file\0\0", Span::default()).is_err());
    }

    #[cfg(windows)]
    #[test]
    fn unsafe_windows_names_and_unicode_case_aliases_fail_before_copy() {
        for manifest in [
            "\0root\0p\0bad\0..\\outside\0p\0",
            "\0root\0p\0CON\0CON\0p\0",
            "\0root\0p\0A\0A\0p\0a\0a\0p\0",
            "\0root\0p\0é\0é\0p\0É\0É\0p\0",
        ] {
            let listing = names(manifest.as_bytes(), Span::default()).unwrap();
            assert!(
                crate::runtime::validate_remote_names(&listing).is_err(),
                "{manifest:?}"
            );
        }
    }
}

#[cfg(all(test, windows))]
mod wsl_tests {
    use std::fs;
    use std::time::{Duration, UNIX_EPOCH};

    use super::*;

    struct Remote(String);

    impl Drop for Remote {
        fn drop(&mut self) {
            run(
                "case \"$1\" in /tmp/msync-acquire-v4.*) chmod -R u+rwX -- \"$1\"; rm -rf -- \"$1\";; *) exit 1;; esac",
                &[&self.0],
            );
        }
    }

    fn run(script: &str, arguments: &[&str]) -> Vec<u8> {
        crate::runtime::remote_command(&Backend::Wsl, script, arguments)
            .unwrap()
            .stdout
    }

    fn capture(
        remote: &Remote,
        suffix: &str,
        name: &str,
        invocation: &InvocationRoot,
        options: &Options,
        target: &Path,
    ) -> Result<Acquisition> {
        acquire(
            Request {
                source: &Location {
                    backend: Backend::Wsl,
                    path: format!("{}/{suffix}", remote.0),
                },
                stage: &invocation.path().join("imports").join(name),
                logical_target: target,
                mode: DestinationMode::Exact,
                pattern: None,
                invocation,
            },
            options,
            Span::default(),
        )
    }

    #[test]
    #[ignore = "explicit real WSL ext4 private-acquisition guard and bulk-copy journey"]
    fn real_wsl_bulk_acquisition_preserves_bytes_and_rejects_unsafe_sources() {
        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&temporary).unwrap();
        let root = tempfile::tempdir_in(temporary).unwrap();
        let options = Options::new(root.path().join("scratch"));
        let invocation = InvocationRoot::create(&options.scratch_root).unwrap();
        fs::create_dir(invocation.path().join("imports")).unwrap();
        let target = root.path().join("logical-target");
        let remote = Remote(
            String::from_utf8(run("mktemp -d /tmp/msync-acquire-v4.XXXXXXXXXX", &[]))
                .unwrap()
                .trim()
                .into(),
        );
        run(
            r#"set -eu
mkdir -p "$1/source/agents" "$1/bad"
printf '可莉-config' > "$1/source/config file 可莉.toml"
touch -m -d @946684800 "$1/source/config file 可莉.toml"
for i in $(seq 0 12); do mkdir -p "$1/source/agents/group-$((i % 3))"; printf '可莉 %s\n' "$i" > "$1/source/agents/group-$((i % 3))/agent-$i.toml"; done
ln -s "$1/source/config file 可莉.toml" "$1/link"
ln -s "$1/source/config file 可莉.toml" "$1/bad/link"
mkfifo "$1/fifo"
printf bad > "$1/CON"
"#,
            &[&remote.0],
        );

        let result = capture(
            &remote,
            "source/config file 可莉.toml",
            "file",
            &invocation,
            &options,
            &target,
        )
        .unwrap();
        assert!(
            matches!(result, Acquisition::Captured { basename } if basename == "config file 可莉.toml")
        );
        let file = invocation.path().join("imports/file");
        assert_eq!(fs::read(&file).unwrap(), "可莉-config".as_bytes());
        assert_eq!(
            fs::metadata(&file).unwrap().modified().unwrap(),
            UNIX_EPOCH + Duration::from_secs(946684800)
        );

        assert!(matches!(
            capture(
                &remote,
                "source/agents",
                "tree",
                &invocation,
                &options,
                &target
            )
            .unwrap(),
            Acquisition::Captured { .. }
        ));
        for i in 0..13 {
            let path = invocation
                .path()
                .join(format!("imports/tree/group-{}/agent-{i}.toml", i % 3));
            assert_eq!(fs::read_to_string(path).unwrap(), format!("可莉 {i}\n"));
        }
        for (source, name) in [
            ("missing", "missing"),
            ("link", "link"),
            ("bad", "bad"),
            ("CON", "device"),
        ] {
            assert!(
                capture(&remote, source, name, &invocation, &options, &target).is_err(),
                "{source}"
            );
            assert!(!invocation.path().join("imports").join(name).exists());
        }
        assert!(matches!(
            capture(&remote, "fifo", "fifo", &invocation, &options, &target).unwrap(),
            Acquisition::Stream
        ));
        assert!(!invocation.path().join("imports/fifo").exists());
        assert!(!target.exists());
        options.cancel.cancel();
        assert!(
            capture(
                &remote,
                "source/agents",
                "cancelled",
                &invocation,
                &options,
                &target
            )
            .unwrap_err()
            .is_cancelled()
        );
        assert!(!invocation.path().join("imports/cancelled").exists());
        invocation.cleanup().unwrap();
    }

    #[test]
    #[ignore = "explicit real WSL ext4 sparse selector acquisition journey"]
    fn real_wsl_selector_never_acquires_unselected_special_or_secret_payload() {
        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&temporary).unwrap();
        let root = tempfile::tempdir_in(temporary).unwrap();
        let options = Options::new(root.path().join("scratch"));
        let invocation = InvocationRoot::create(&options.scratch_root).unwrap();
        fs::create_dir(invocation.path().join("imports")).unwrap();
        let remote = Remote(
            String::from_utf8(run("mktemp -d /tmp/msync-acquire-v4.XXXXXXXXXX", &[]))
                .unwrap()
                .trim()
                .into(),
        );
        run(
            r#"set -eu
mkdir -p "$1/source/picked/nested" "$1/source/secret"
printf chosen > "$1/source/keep.txt"
printf tree > "$1/source/picked/nested/file"
printf hidden > "$1/source/secret/data"
chmod 000 "$1/source/secret"
mkfifo "$1/source/unselected-fifo"
ln -s /missing "$1/source/unselected-link"
"#,
            &[&remote.0],
        );
        let source = Location {
            backend: Backend::Wsl,
            path: format!("{}/source", remote.0),
        };
        let target = root.path().join("target");
        for (name, pattern) in [("chosen", "keep\\.txt|picked"), ("empty", "absent")] {
            let stage = invocation.path().join("imports").join(name);
            let result = acquire(
                Request {
                    source: &source,
                    stage: &stage,
                    logical_target: &target,
                    mode: DestinationMode::Container,
                    pattern: Some(pattern),
                    invocation: &invocation,
                },
                &options,
                Span::default(),
            )
            .unwrap();
            assert!(matches!(result, Acquisition::Captured { .. }));
            if name == "chosen" {
                assert_eq!(fs::read(stage.join("keep.txt")).unwrap(), b"chosen");
                assert_eq!(fs::read(stage.join("picked/nested/file")).unwrap(), b"tree");
                assert_eq!(fs::read_dir(&stage).unwrap().count(), 2);
            } else {
                assert_eq!(fs::read_dir(&stage).unwrap().count(), 0);
            }
        }
        let stage = invocation.path().join("imports/rejected");
        assert!(
            acquire(
                Request {
                    source: &source,
                    stage: &stage,
                    logical_target: &target,
                    mode: DestinationMode::Container,
                    pattern: Some("unselected-fifo"),
                    invocation: &invocation
                },
                &options,
                Span::default()
            )
            .is_err()
        );
        assert!(!stage.exists());
        assert!(!target.exists());
        invocation.cleanup().unwrap();
    }

    #[test]
    #[ignore = "explicit real WSL guest barrier; finite owner cancellation must join the launched helper"]
    fn real_wsl_inflight_cancellation_waits_for_guest_copy_before_owner_cleanup() {
        use std::sync::mpsc;
        use std::thread;
        use std::time::Instant;

        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&temporary).unwrap();
        let root = tempfile::tempdir_in(temporary).unwrap();
        let options = Options::new(root.path().join("scratch"));
        let invocation = InvocationRoot::create(&options.scratch_root).unwrap();
        fs::create_dir(invocation.path().join("imports")).unwrap();
        let stage = invocation.path().join("imports/cancelled");
        let logical = root.path().join("logical-target");
        fs::write(&logical, "preserved").unwrap();
        let remote = Remote(
            String::from_utf8(run("mktemp -d /tmp/msync-acquire-v4.XXXXXXXXXX", &[]))
                .unwrap()
                .trim()
                .into(),
        );
        run(
            r#"set -eu
mkdir "$1/bin"
printf 'actual guest copy' > "$1/source"
cat > "$1/bin/cp" <<'WRAPPER'
#!/bin/sh
set -eu
: > "$MSYNC_TEST_CONTROL/ready"
i=0
while [ ! -e "$MSYNC_TEST_CONTROL/release" ] && [ "$i" -lt 200 ]; do sleep 0.025; i=$((i + 1)); done
/bin/cp "$@"
: > "$MSYNC_TEST_CONTROL/finished"
WRAPPER
chmod +x "$1/bin/cp"
"#,
            &[&remote.0],
        );
        let source = format!("{}/source", remote.0);
        let stage_text = stage.to_string_lossy().into_owned();
        let preflight = command(
            PREFLIGHT,
            &[&source, &stage_text],
            &options,
            Span::default(),
        )
        .unwrap();
        let proof = receipt(&preflight, "ready", Span::default()).unwrap();
        crate::runtime::validate_remote_names(&names(proof.manifest, Span::default()).unwrap())
            .unwrap();
        let script = format!(
            "PATH=$5/bin:$PATH; export PATH; MSYNC_TEST_CONTROL=$5; export MSYNC_TEST_CONTROL\n{CAPTURE}"
        );
        let arguments = [
            proof.source.to_owned(),
            stage_text,
            proof.kind.to_owned(),
            proof.fingerprint.to_owned(),
            remote.0.clone(),
        ];
        let (send, receive) = mpsc::channel();
        let worker_options = options.clone();

        struct Release<'a>(&'a Remote);
        impl Drop for Release<'_> {
            fn drop(&mut self) {
                run(": > \"$1/release\"", &[&self.0.0]);
            }
        }

        thread::scope(|scope| {
            scope.spawn(move || {
                let args = arguments.iter().map(String::as_str).collect::<Vec<_>>();
                send.send(command(&script, &args, &worker_options, Span::default()))
                    .unwrap();
            });
            let release = Release(&remote);
            let deadline = Instant::now() + Duration::from_secs(10);
            loop {
                let ready = run(
                    "if [ -e \"$1/ready\" ]; then printf ready; fi",
                    &[&remote.0],
                );
                if ready == b"ready" {
                    break;
                }
                assert!(
                    Instant::now() < deadline,
                    "guest copy never reached its barrier"
                );
            }
            options.cancel.cancel();
            assert!(
                matches!(
                    receive.recv_timeout(Duration::from_millis(150)),
                    Err(mpsc::RecvTimeoutError::Timeout)
                ),
                "owner returned while its launched guest was still at the copy barrier"
            );
            drop(release);
            let result = receive.recv_timeout(Duration::from_secs(10)).unwrap();
            assert!(result.unwrap_err().is_cancelled());
        });
        assert_eq!(
            run("test -e \"$1/finished\"; printf joined", &[&remote.0]),
            b"joined"
        );
        assert_eq!(fs::read(&stage).unwrap(), b"actual guest copy");
        assert_eq!(fs::read(&logical).unwrap(), b"preserved");
        invocation.cleanup().unwrap();
    }
}
