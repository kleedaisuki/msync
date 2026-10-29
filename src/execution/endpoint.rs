//! Finite remote publication. Transport populates an owned journal, never a user
//! tree. A remote helper stages the complete replacement and retains its before
//! image until a durable commit receipt resolves transport acknowledgement loss.

use std::ffi::OsString;
use std::path::PathBuf;
use std::process::Output;
use std::process::Stdio;
use std::time::Duration;

use tokio::io::AsyncReadExt;
use tokio_util::sync::CancellationToken;

use crate::endpoint::{Backend, Location};
use crate::hir::{Input, Output as Sink};
use crate::language::DestinationMode;
use crate::syntax::Span;

use super::error::{ExecutionError, Result};

#[path = "remote.rs"]
mod remote;
pub(super) use remote::{execute, owned_registry, recover};

#[derive(Clone, Debug)]
pub(super) struct Spec {
    pub destination: Location,
    pub mode: DestinationMode,
    pub span: Span,
    pattern: Option<String>,
    origin: Option<Location>,
    literal: bool,
}

pub(super) struct Report {
    pub transfer: crate::runtime::ExecutionReport,
    pub cleanup: Option<super::CleanupIssue>,
}

pub(super) fn inspect(source: &Input, output: &Sink, span: Span) -> Result<Option<Spec>> {
    let Sink::File { location, mode } = output else {
        return Ok(None);
    };
    if !matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }) {
        return Ok(None);
    }
    if location.path.contains(['\n', '\r', '\0']) {
        return Err(ExecutionError::new(
            "remote publication paths cannot contain line breaks or NUL",
            span,
        ));
    }
    let pattern = match source {
        Input::File { pattern, .. } => pattern.clone(),
        _ => None,
    };
    if pattern.is_some() && *mode != DestinationMode::Container {
        return Err(ExecutionError::new(
            "a selector requires a destination container ending in '/'",
            span,
        ));
    }
    Ok(Some(Spec {
        destination: location.clone(),
        mode: *mode,
        span,
        pattern,
        origin: match source {
            Input::File { location, .. } => Some(location.clone()),
            _ => None,
        },
        literal: matches!(source, Input::Literal(_)),
    }))
}

impl Spec {
    /// `source` is an invocation-owned materialization, not a live user pathname.
    pub(super) async fn run(
        self,
        source: PathBuf,
        basename: Option<OsString>,
        buffer: usize,
        cancel: CancellationToken,
        scratch: PathBuf,
    ) -> Result<Report> {
        let span = self.span;
        let cancel = cancel.child_token();
        let _cancel_on_drop = cancel.clone().drop_guard();
        let trace = crate::telemetry::context();
        // Receipt settlement must outlive a caller dropping its transfer future.
        // Dropping the caller cancels this worker, but does not abort its join.
        tokio::spawn(async move {
            let result = self
                .run_inner(source, basename, buffer, cancel, scratch)
                .await;
            if let (Some(trace), Err(error)) = (trace, &result) {
                trace.record_error("remote_publication", error);
            }
            result
        })
        .await
        .map_err(|cause| ExecutionError::caused("remote publication worker failed", span, cause))?
    }

    async fn run_inner(
        self,
        source: PathBuf,
        basename: Option<OsString>,
        buffer: usize,
        cancel: CancellationToken,
        scratch: PathBuf,
    ) -> Result<Report> {
        let names = self.names(&source, basename)?;
        if self.pattern.is_some() && names.is_empty() {
            return Ok(Report {
                transfer: crate::runtime::ExecutionReport::default(),
                cleanup: None,
            });
        }
        let backend = &self.destination.backend;
        let origin = self.remote_origin(&cancel).await?;
        let allocated = command(
            backend,
            ALLOCATE,
            &[
                &self.destination.path,
                origin.as_deref().unwrap_or(""),
                if self.mode == DestinationMode::Container && self.pattern.is_none() {
                    names.first().map(String::as_str).unwrap_or("")
                } else {
                    ""
                },
            ],
            &cancel,
            self.span,
        )
        .await?;
        let allocation = String::from_utf8(allocated.stdout).map_err(|cause| {
            ExecutionError::caused("remote journal returned invalid UTF-8", self.span, cause)
        })?;
        let fields: Vec<_> = allocation.lines().collect();
        if fields.len() != 3 || !fields[0].starts_with('/') || !fields[0].contains("/.msync-v4.") {
            return Err(ExecutionError::new(
                "remote journal returned an invalid allocation",
                self.span,
            ));
        }
        let (journal, target, root) = (fields[0], fields[1], fields[2]);
        let pending = remote::Pending::create(&scratch, backend, journal, target, self.span)?;
        let op = crate::compiler::CopyOp {
            source: Location {
                backend: Backend::Local,
                path: source.to_string_lossy().into_owned(),
            },
            destination: Location {
                backend: backend.clone(),
                path: format!("{journal}/payload"),
            },
            pattern: None,
            span: self.span,
        };
        let copy_cancel = cancel.clone();
        let span = self.span;
        let uploaded = tokio::task::spawn_blocking(move || {
            crate::runtime::execute_copy_cancellable(&op, buffer, copy_cancel)
        })
        .await
        .map_err(|cause| ExecutionError::caused("remote upload task failed", span, cause))?;
        let report = match uploaded {
            Ok(report) => report,
            Err(cause) => {
                return Err(ExecutionError::caused(
                    format!("cannot stage remote source; destination unchanged, retained remote journal {journal}"),
                    span,
                    cause,
                ).help("The transport client has been joined. A disconnected remote upload may still own private staging data; remove the retained journal only after confirming that transfer has stopped."));
            }
        };
        let mode = match self.mode {
            DestinationMode::Exact => "exact",
            DestinationMode::Container => "container",
            DestinationMode::ReplaceTree => "replace",
        };
        let mut arguments = vec![
            journal,
            target,
            root,
            mode,
            if self.pattern.is_some() {
                "selected"
            } else if self.literal {
                "literal"
            } else {
                "single"
            },
        ];
        arguments.extend(names.iter().map(String::as_str));
        let publication = command(backend, PUBLISH, &arguments, &cancel, span).await;
        let (committed, mut cleanup) = self.settle(journal, &cancel).await?;
        if committed {
            let record = pending.path().to_owned();
            if let Some(issue) = cleanup.as_mut() {
                issue.retained_paths.push(record);
                drop(pending);
            } else if let Err(cause) = pending.finish(self.span) {
                cleanup = Some(super::CleanupIssue {
                    cause: Box::new(cause),
                    retained_paths: vec![record],
                    resolved: false,
                });
            }
            return Ok(Report {
                transfer: report,
                cleanup,
            });
        }
        pending.finish(self.span)?;
        publication?;
        Err(ExecutionError::new(
            "remote publication ended without a commit receipt; before-image restored",
            span,
        ))
    }

    async fn remote_origin(&self, cancel: &CancellationToken) -> Result<Option<String>> {
        let Some(origin) = &self.origin else {
            return Ok(None);
        };
        if origin.backend == self.destination.backend {
            return Ok(Some(origin.path.clone()));
        }
        if self.destination.backend != Backend::Wsl {
            return Ok(None);
        }
        let Some(local) = crate::runtime::local_path(origin, true)
            .map_err(|message| ExecutionError::new(message, self.span))?
        else {
            return Ok(None);
        };
        let local = std::path::absolute(local).map_err(|cause| {
            ExecutionError::caused("cannot resolve remote source alias", self.span, cause)
        })?;
        let output = command(
            &Backend::Wsl,
            "wslpath -a -u \"$1\"",
            &[&local.to_string_lossy()],
            cancel,
            self.span,
        )
        .await?;
        Ok(Some(
            String::from_utf8(output.stdout)
                .map_err(|cause| {
                    ExecutionError::caused("invalid WSL source alias", self.span, cause)
                })?
                .trim_end()
                .to_owned(),
        ))
    }

    fn names(&self, source: &std::path::Path, basename: Option<OsString>) -> Result<Vec<String>> {
        if let Some(pattern) = &self.pattern {
            let regex = regex::Regex::new(&format!("\\A(?:{pattern})\\z")).map_err(|cause| {
                ExecutionError::caused("invalid remote selector", self.span, cause)
            })?;
            let mut names = Vec::new();
            for entry in std::fs::read_dir(source).map_err(|cause| {
                ExecutionError::caused("cannot enumerate immutable selector", self.span, cause)
            })? {
                let entry = entry.map_err(|cause| {
                    ExecutionError::caused("cannot read immutable selector entry", self.span, cause)
                })?;
                let name = entry.file_name().into_string().map_err(|_| {
                    ExecutionError::new("remote selector name is not UTF-8", self.span)
                })?;
                if regex.is_match(&name) {
                    names.push(name);
                }
            }
            names.sort();
            return Ok(names);
        }
        if self.mode != DestinationMode::Container {
            return Ok(Vec::new());
        }
        let name = basename
            .ok_or_else(|| {
                ExecutionError::new(
                    "a literal has no basename for a remote container",
                    self.span,
                )
            })?
            .into_string()
            .map_err(|_| ExecutionError::new("remote basename is not UTF-8", self.span))?;
        if matches!(name.as_str(), "" | "." | "..") || name.contains(['/', '\\', '\0']) {
            return Err(ExecutionError::new(
                "invalid remote source basename",
                self.span,
            ));
        }
        Ok(vec![name])
    }

    async fn settle(
        &self,
        journal: &str,
        _: &CancellationToken,
    ) -> Result<(bool, Option<super::CleanupIssue>)> {
        // A killed SSH client does not prove its server-side shell has stopped.
        // The independent lock joins publication before observing its receipt.
        let recovery = CancellationToken::new();
        let arguments = [journal];
        let operation = command_output(
            &self.destination.backend,
            SETTLE,
            &arguments,
            &recovery,
            self.span,
        );
        tokio::pin!(operation);
        let result = tokio::select! {
            result = &mut operation => result,
            _ = tokio::time::sleep(Duration::from_secs(35)) => {
                recovery.cancel();
                // Cancellation kills and joins the physical client, unlike
                // dropping a timed-out process future and hoping it is reaped.
                operation.await
            }
        };
        match result {
            Ok(output) if output.stdout.starts_with(b"COMMITTED\n") => {
                let cleanup = (!output.status.success()).then(|| super::CleanupIssue {
                    cause: Box::new(ExecutionError::new(format!("remote assignment committed; cleanup failed, retained journal {journal}: {}", String::from_utf8_lossy(&output.stderr).trim()), self.span)),
                    retained_paths: Vec::new(), resolved: false,
                });
                Ok((true, cleanup))
            }
            Ok(output) if output.stdout.starts_with(b"ABORTED\n") => {
                if output.status.success() { return Ok((false, None)); }
                Err(ExecutionError::new(format!("remote publication aborted; cleanup failed, retained journal {journal}: {}", String::from_utf8_lossy(&output.stderr).trim()), self.span))
            }
            failure => Err(ExecutionError::new(format!("remote commit state is unknown; retained journal {journal}: {failure:?}"), self.span)
                .help("Do not retry blindly. Inspect the retained remote receipt and before-image before recovery.")),
        }
    }
}

async fn command(
    backend: &Backend,
    script: &str,
    args: &[&str],
    cancel: &CancellationToken,
    span: Span,
) -> Result<Output> {
    let output = command_output(backend, script, args, cancel, span).await?;
    if !output.status.success() {
        return Err(ExecutionError::new(
            format!(
                "remote publication rejected: {}",
                String::from_utf8_lossy(&output.stderr).trim()
            ),
            span,
        ));
    }
    Ok(output)
}

async fn command_output(
    backend: &Backend,
    script: &str,
    args: &[&str],
    cancel: &CancellationToken,
    span: Span,
) -> Result<Output> {
    let script = format!("{RECOVERY}\n{script}");
    let command = crate::runtime::remote_command_builder(backend, &script, args)
        .map_err(|message| ExecutionError::new(message, span))?;
    let mut command = tokio::process::Command::from(command);
    command.kill_on_drop(true);
    command.stdout(Stdio::piped()).stderr(Stdio::piped());
    let mut child = command.spawn().map_err(|cause| {
        ExecutionError::caused("cannot start remote publication command", span, cause)
    })?;
    let stdout = child.stdout.take().expect("piped remote stdout");
    let stderr = child.stderr.take().expect("piped remote stderr");
    let out = tokio::spawn(drain(stdout));
    let err = tokio::spawn(drain(stderr));
    let status = tokio::select! {
        status = child.wait() => status.map_err(|cause| ExecutionError::caused("cannot wait for remote publication command", span, cause))?,
        _ = cancel.cancelled() => {
            let _ = child.kill().await;
            let _ = child.wait().await;
            out.abort(); err.abort();
            let _ = out.await; let _ = err.await;
            return Err(ExecutionError::cancelled("remote publication cancelled", span));
        },
    };
    let stdout = out
        .await
        .map_err(|cause| ExecutionError::caused("remote stdout reader failed", span, cause))?
        .map_err(|cause| ExecutionError::caused("cannot read remote stdout", span, cause))?;
    let stderr = err
        .await
        .map_err(|cause| ExecutionError::caused("remote stderr reader failed", span, cause))?
        .map_err(|cause| ExecutionError::caused("cannot read remote stderr", span, cause))?;
    let output = Output {
        status,
        stdout,
        stderr,
    };
    Ok(output)
}

async fn drain(mut reader: impl tokio::io::AsyncRead + Unpin) -> std::io::Result<Vec<u8>> {
    // Control responses are bounded independently of source payload buffers.
    let mut bytes = Vec::new();
    let mut buffer = [0; 4096];
    loop {
        let count = reader.read(&mut buffer).await?;
        if count == 0 {
            break;
        }
        let retain = count.min((1024 * 1024usize).saturating_sub(bytes.len()));
        bytes.extend_from_slice(&buffer[..retain]);
    }
    Ok(bytes)
}

const RECOVERY: &str = r#"
restore_mode() (
    record=$1; target=$2
    [ -e "$record" ] || return 0
    [ ! -L "$record" ] && [ ! -L "$target" ] && [ -d "$target" ] || { echo 'invalid directory mode recovery record' >&2; return 1; }
    identity=${record%-mode}-id
    [ -f "$identity" ] && [ "$(stat -c '%d:%i' -- "$target")" = "$(cat "$identity")" ] || { echo 'directory mode target identity changed; journal retained' >&2; return 1; }
    saved=$(cat "$record")
    case $saved in ''|*[!0-7]*|?????*) echo 'invalid recorded directory mode' >&2; return 1;; esac
    [ "$(stat -c %a -- "$target")" != "$saved" ] || return 0
    chmod "$saved" -- "$target"
)
admit_directory() (
    target=$1; identity=${2:-}
    [ ! -L "$target" ] || { echo 'refusing symbolic link during directory admission' >&2; return 1; }
    if [ -d "$target" ]; then
        if [ -n "$identity" ]; then
            [ -f "$identity" ] && [ "$(stat -c '%d:%i' -- "$target")" = "$(cat "$identity")" ] || { echo 'directory admission identity changed; journal retained' >&2; return 1; }
        fi
        [ -w "$target" ] || chmod u+w -- "$target"
    fi
)
move_private() (
    source=$1; target=$2; saved=
    if [ "$(dirname -- "$source")" != "$(dirname -- "$target")" ] && [ -d "$source" ] && [ ! -L "$source" ]; then
        if [ ! -w "$source" ]; then
            saved=$(stat -c %a -- "$source")
            admit_directory "$source" || return 1
        fi
    fi
    if mv -T -- "$source" "$target"; then
        [ -z "$saved" ] || chmod "$saved" -- "$target"
    else
        result=$?
        [ -z "$saved" ] || chmod "$saved" -- "$source" || return 1
        return "$result"
    fi
)
# Call only on owned private paths or an identity-proven installed new root.
# Regular files may retain external hard-link aliases: unlink, never chmod them.
remove_owned_tree() (
    target=$1
    [ ! -L "$target" ] || { echo 'refusing symbolic-link cleanup root' >&2; return 1; }
    if [ -d "$target" ]; then
        find "$target" -type d -exec sh -c '
            [ -r "$1" ] && [ -w "$1" ] && [ -x "$1" ] || exec chmod u+rwx -- "$1"
        ' sh {} \; || return 1
    fi
    rm -rf -- "$target"
)
recover_journal() (
    j=$1
    [ -f "$j/intent" ] || return 0
    [ ! -L "$j" ] && [ ! -L "$j/intent" ] || return 1
    IFS= read -r r < "$j/intent"
    [ "$(dirname -- "$r")" = "$(dirname -- "$j")" ] || { echo 'invalid remote recovery root' >&2; return 1; }
    [ ! -L "$r" ] || { echo 'remote recovery target became symbolic link' >&2; return 1; }
    if [ ! -e "$j/COMMITTED" ]; then
        expected=$(cat "$j/after-id")
        if [ -e "$j/before" ]; then
            [ ! -L "$j/before" ] || { echo 'remote before-image became symbolic link; journal retained' >&2; return 1; }
            if [ -d "$j/before" ] && [ -f "$j/before-id" ]; then
                [ "$(stat -c '%d:%i' -- "$j/before")" = "$(cat "$j/before-id")" ] || { echo 'remote before-image identity changed; journal retained' >&2; return 1; }
            fi
            if [ -e "$r" ]; then
                current=$(stat -c '%d:%i' -- "$r")
                if [ -f "$j/before-id" ] && [ "$current" = "$(cat "$j/before-id")" ]; then
                    restore_mode "$j/before-mode" "$r" || return 1
                    remove_owned_tree "$j/before" || return 1
                    sync -f -- "$(dirname -- "$j")"
                    return 0
                fi
                [ "$current" = "$expected" ] || { echo 'remote recovery target changed; journal retained' >&2; return 1; }
                remove_owned_tree "$r" || return 1
            fi
            admit_directory "$j/before" "$j/before-id" || return 1
            mv -T -- "$j/before" "$r" || return 1
            restore_mode "$j/before-mode" "$r" || return 1
        elif [ -e "$j/had-before" ]; then
            [ -e "$r" ] && [ -f "$j/before-id" ] && [ "$(stat -c '%d:%i' -- "$r")" = "$(cat "$j/before-id")" ] || { echo 'remote before-image is missing; journal retained' >&2; return 1; }
            restore_mode "$j/before-mode" "$r" || return 1
        elif [ ! -e "$j/had-before" ] && [ -e "$r" ]; then
            [ "$(stat -c '%d:%i' -- "$r")" = "$expected" ] || { echo 'remote recovery target changed; journal retained' >&2; return 1; }
            remove_owned_tree "$r" || return 1
        fi
    fi
    sync -f -- "$(dirname -- "$j")"
)
retire_intent() (
    j=$1
    rm -f -- "$j/intent" || return 1
    sync -f -- "$j"
)
"#;

const ALLOCATE: &str = r#"
set -eu
command -v flock >/dev/null || { echo 'remote publication requires flock' >&2; exit 1; }
command -v sync >/dev/null || { echo 'remote publication requires GNU sync -f' >&2; exit 1; }
case $1 in '~') d=$HOME;; '~/'*) d=$HOME/${1#\~/};; /*) d=$1;; *) d=$PWD/$1;; esac
p=$d
while [ "$p" != / ]; do
    [ ! -L "$p" ] || { echo "refusing symbolic link: $p" >&2; exit 1; }
    p=$(dirname -- "$p")
done
d=$(realpath -m -- "$d")
[ "$d" != / ] || { echo 'remote filesystem root is reserved' >&2; exit 1; }
if [ -n "$2" ]; then
    case $2 in '~') s=$HOME;; '~/'*) s=$HOME/${2#\~/};; /*) s=$2;; *) s=$PWD/$2;; esac
    s=$(realpath -m -- "$s")
    q=$d; [ -z "$3" ] || q=$d/$3
    [ "$s" != "$q" ] && { [ ! -e "$q" ] || [ ! "$s" -ef "$q" ]; } || { echo 'source and destination are identical' >&2; exit 1; }
    if [ -d "$s" ]; then
        case $q/ in "$s/"*) echo 'source and destination directory trees overlap' >&2; exit 1;; esac
        case $s/ in "$q/"*) echo 'source and destination directory trees overlap' >&2; exit 1;; esac
    fi
fi
r=$d
while [ ! -e "$(dirname -- "$r")" ]; do r=$(dirname -- "$r"); done
p=$(dirname -- "$r")
[ -d "$p" ] || { echo 'destination ancestor is not a directory' >&2; exit 1; }
exec 7<"$p"
flock -x 7
# Startup settles only journals owned by an unlocked coordinator record. A
# sibling allocation must not delete another client's acknowledged commit
# receipt in the gap between its publisher exit and receipt settlement.
umask 077
j=$(mktemp -d "$p/.msync-v4.XXXXXXXXXX")
printf '%s\n%s\n%s\n' "$j" "$d" "$r"
"#;

const PUBLISH: &str = r#"
set -eu
j=$1; d=$2; r=$3; mode=$4; selection=$5; shift 5
exec 9>"$j/lock"
exec 7<"$(dirname -- "$j")"
flock -x 7
flock -x 9
rollback() {
    recover_journal "$j"
}
trap rollback EXIT
trap 'exit 130' HUP INT TERM
fail() { echo "$*" >&2; exit 1; }
[ ! -e "$j/ABORT" ] || fail 'publication cancelled'
p=$r
while [ "$p" != / ]; do
    [ ! -L "$p" ] || fail "refusing symbolic link: $p"
    p=$(dirname -- "$p")
done
[ -z "$(find "$j/payload" ! -type f ! -type d -print -quit)" ] || fail 'unsupported entry in source payload'
if [ -e "$r" ]; then
    if [ "$mode" = container ]; then
        [ -z "$(find "$r" ! -type f ! -type d ! -type l -print -quit)" ] || fail 'unsupported entry in destination tree'
    else
        [ -z "$(find "$r" ! -type f ! -type d -print -quit)" ] || fail 'unsupported entry in destination tree'
    fi
fi
[ "$mode" != exact ] || [ ! -d "$d" ] || fail 'destination is an existing directory; append / or /.'
[ "$mode" != exact ] || [ ! -d "$j/payload" ] || [ ! -e "$d" ] || fail 'a directory source cannot replace an existing ordinary file'
[ "$mode" != replace ] || [ -d "$j/payload" ] || fail 'directory replacement requires a directory source'
[ "$mode" != replace ] || [ ! -e "$d" ] || [ -d "$d" ] || fail 'directory replacement target is not a directory'
if [ "$mode" = exact ] && [ -f "$d" ]; then
    permissions=$(stat -c '%a' -- "$d")
    [ $((0$permissions & 0222)) -ne 0 ] || fail 'the existing file is read-only'
    if [ "$selection" = literal ]; then chmod --reference="$d" -- "$j/payload"; fi
fi
if [ "$mode" = container ]; then
    [ ! -e "$d" ] || [ -d "$d" ] || fail 'container destination is not a directory'
    if [ -e "$r" ]; then cp -a --reflink=auto -- "$r" "$j/next"; else mkdir "$j/next"; fi
    case $d in "$r") next=$j/next;; *) next=$j/next/${d#"$r"/};; esac
    mkdir -p -- "$next"
    check() (
        a=$1; b=$2
        [ ! -L "$a" ] && [ ! -L "$b" ] || fail "refusing symbolic link in container merge: $b"
        if [ -d "$a" ]; then
            [ ! -e "$b" ] || [ -d "$b" ] || fail "directory collides with file: $b"
            for x in "$a"/* "$a"/.[!.]* "$a"/..?*; do
                [ -e "$x" ] || continue
                check "$x" "$b/$(basename -- "$x")"
            done
        else [ ! -e "$b" ] || fail "container file already exists: $b"; fi
    )
    copy_merge() (
        a=$1; b=$2
        [ ! -L "$a" ] && [ ! -L "$b" ] || fail "refusing symbolic link in container merge: $b"
        if [ -d "$a" ] && [ -d "$b" ]; then
            for x in "$a"/* "$a"/.[!.]* "$a"/..?*; do
                [ -e "$x" ] || continue
                copy_merge "$x" "$b/$(basename -- "$x")"
            done
        else
            [ ! -e "$b" ] || fail "container entry already exists: $b"
            cp -a --reflink=auto -- "$a" "$b"
        fi
    )
    for name do
        a=$j/payload; [ "$selection" != selected ] || a=$a/$name
        check "$a" "$next/$name"
    done
    for name do
        a=$j/payload; [ "$selection" != selected ] || a=$a/$name
        copy_merge "$a" "$next/$name"
    done
else
    if [ "$r" = "$d" ]; then move_private "$j/payload" "$j/next"
    else
        mkdir "$j/next"
        next=$j/next/${d#"$r"/}
        mkdir -p -- "$(dirname -- "$next")"
        move_private "$j/payload" "$next"
    fi
fi
[ ! -e "$j/ABORT" ] || fail 'publication cancelled'
stat -c '%d:%i' -- "$j/next" > "$j/after-id"
[ ! -d "$j/next" ] || stat -c %a -- "$j/next" > "$j/after-mode"
if [ -e "$r" ]; then
    : > "$j/had-before"
    stat -c '%d:%i' -- "$r" > "$j/before-id"
    [ ! -d "$r" ] || stat -c %a -- "$r" > "$j/before-mode"
fi
printf '%s\n' "$r" > "$j/intent"
# The intent, complete staged payload and before-image decision reach the same
# filesystem before any destructive rename. Recovery is idempotent.
sync -f -- "$j"
if [ -f "$r" ] && [ -f "$j/next" ]; then
    cp -a --reflink=auto -- "$r" "$j/before"
    sync -f -- "$j"
elif [ -e "$r" ]; then
    admit_directory "$r" "$j/before-id"
    mv -T -- "$r" "$j/before"
fi
# Keep the before-image until the independent receipt query joins this lock.
: > "$j/published"
admit_directory "$j/next" "$j/after-id"
mv -T -- "$j/next" "$r"
restore_mode "$j/after-mode" "$r"
sync -f -- "$j"
: > "$j/COMMITTED"
sync -f -- "$j"
printf 'COMMITTED\n'
"#;

const SETTLE: &str = r#"
set -eu
j=$1
[ -d "$j" ] || { echo 'remote journal disappeared' >&2; exit 1; }
: > "$j/ABORT"
exec 9>"$j/lock"
exec 7<"$(dirname -- "$j")"
flock -x 7
flock -x -w 30 9 || { echo 'remote publication still active; journal retained' >&2; exit 1; }
if [ -e "$j/COMMITTED" ]; then printf 'COMMITTED\n'
else
    recover_journal "$j"
    printf 'ABORTED\n'
fi
retire_intent "$j"
remove_owned_tree "$j"
"#;

const RECOVER: &str = r#"
set -eu
j=$1
[ -d "$j" ] || { printf 'UNCHANGED\n'; exit 0; }
[ ! -L "$j" ] || { echo 'remote journal became symbolic link' >&2; exit 1; }
exec 7<"$(dirname -- "$j")"
flock -x 7
exec 9>"$j/lock"
flock -x -w 30 9 || { echo 'remote publication remains active' >&2; exit 1; }
if [ ! -f "$j/intent" ]; then
    # The coordinator record is exclusively owned by recovery: no live client
    # can advance this upload to publication. Any surviving uploader can only
    # write this private journal, never the user destination.
    p=$(dirname -- "$j")
    remove_owned_tree "$j"
    sync -f -- "$p"
    printf 'UNCHANGED\n'
    exit 0
fi
recover_journal "$j"
if [ -e "$j/COMMITTED" ]; then printf 'COMMITTED\n'; else printf 'ABORTED\n'; fi
p=$(dirname -- "$j")
retire_intent "$j"
remove_owned_tree "$j"
sync -f -- "$p"
"#;

#[cfg(all(test, target_os = "linux"))]
mod journal_tests {
    use std::fs;
    use std::io::Write;
    use std::path::Path;
    use std::process::{Command, Stdio};

    use super::{ALLOCATE, PUBLISH, RECOVER, RECOVERY, SETTLE};

    const FIXTURE: &str = include_str!(concat!(
        env!("CARGO_MANIFEST_DIR"),
        "/tests/support/remote_journal_fixture.sh"
    ));
    const KILL: &str = r#"printf '%s\n' "$MSYNC_HELPER_PGID" > "$MSYNC_TEST_KILL_MARKER"; /bin/kill -KILL -- "-$MSYNC_HELPER_PGID""#;

    fn replace_once(source: &str, anchor: &str, replacement: &str) -> String {
        assert_eq!(
            source.matches(anchor).count(),
            1,
            "ambiguous fault cut: {anchor}"
        );
        source.replacen(anchor, replacement, 1)
    }

    fn prepare(library: &Path) {
        fs::create_dir(library).unwrap();
        for (name, body) in [
            ("RECOVERY", RECOVERY),
            ("ALLOCATE", ALLOCATE),
            ("PUBLISH", PUBLISH),
            ("SETTLE", SETTLE),
            ("RECOVER", RECOVER),
        ] {
            fs::write(library.join(format!("{name}.sh")), body).unwrap();
        }
        let old = r#"admit_directory "$r" "$j/before-id""#;
        let backup = r#"mv -T -- "$r" "$j/before""#;
        let next = r#"admit_directory "$j/next" "$j/after-id""#;
        let install = r#"mv -T -- "$j/next" "$r""#;
        let restore = r#"restore_mode "$j/after-mode" "$r""#;
        let commit = r#": > "$j/COMMITTED""#;
        let synced = format!("{commit}\nsync -f -- \"$j\"");
        let cases = [
            ("normal", "", false, "new"),
            ("before-old-admit", old, true, "old"),
            ("after-old-admit", old, false, "old"),
            ("after-before-rename", backup, false, "old"),
            ("after-next-admit", next, false, "old"),
            ("after-install", install, false, "old"),
            ("after-restore", restore, false, "old"),
            ("before-commit", commit, true, "old"),
            ("after-commit", commit, false, "new"),
            ("after-commit-sync", synced.as_str(), false, "new"),
        ];
        let mut plan = String::new();
        for (name, anchor, before, state) in cases {
            let body = if anchor.is_empty() {
                PUBLISH.to_owned()
            } else {
                let cut = if before {
                    format!("{KILL}; {anchor}")
                } else {
                    format!("{anchor}; {KILL}")
                };
                replace_once(PUBLISH, anchor, &cut)
            };
            fs::write(library.join(format!("PUBLISH-{name}.sh")), body).unwrap();
            let exit = if anchor.is_empty() { 0 } else { 137 };
            plan.push_str(&format!("{name} {state} {exit}\n"));
        }
        fs::write(library.join("plan.txt"), plan).unwrap();
        let anchor = r#"mv -T -- "$j/before" "$r" || return 1"#;
        fs::write(
            library.join("RECOVERY-restore-crash.sh"),
            replace_once(RECOVERY, anchor, &format!("{anchor}; {KILL}")),
        )
        .unwrap();
        let anchor = r#"    rm -rf -- "$target""#;
        let prefix = r#"    if [ "$target" = "$j" ]; then
        [ ! -e "$j/intent" ] || { echo 'GC began before durable intent retirement' >&2; exit 1; }
        victim=$j/before/nested/data
        [ -f "$victim" ] || victim=$j/next/nested/data
        [ -f "$victim" ] || { echo 'no private payload for partial GC cut' >&2; exit 1; }
        rm -f -- "$j/COMMITTED"
        rm -f -- "$victim"
        printf 'legal partial GC prefix removed COMMITTED and %s\n' "$victim"
"#;
        let cut = format!("{prefix}        {KILL}\n    fi\n{anchor}");
        fs::write(
            library.join("RECOVERY-gc-crash.sh"),
            replace_once(RECOVERY, anchor, &cut),
        )
        .unwrap();
    }

    fn can_drop_capabilities() -> bool {
        Command::new("timeout")
            .args([
                "--kill-after=1s",
                "5s",
                "setpriv",
                "--bounding-set=-all",
                "--inh-caps=-all",
                "--ambient-caps=-all",
                "--no-new-privs",
                "sh",
                "-c",
                r#"[ "$(awk '/^CapEff:/ {print $2}' /proc/self/status)" = 0000000000000000 ]"#,
            ])
            .output()
            .is_ok_and(|output| output.status.success())
    }

    #[test]
    fn readonly_modes_and_partial_gc_remain_crash_atomic() {
        let process_status = fs::read_to_string("/proc/self/status").unwrap();
        let capability = |name: &str| {
            process_status
                .lines()
                .find_map(|line| line.strip_prefix(name))
                .expect("Linux exposes process capability masks")
                .trim()
        };
        // SAFETY: geteuid has no preconditions and does not change process state.
        let root_actor = unsafe { libc::geteuid() } == 0;
        let drop_capabilities = capability("CapEff:") != "0000000000000000"
            || (root_actor && capability("CapBnd:") != "0000000000000000");
        if drop_capabilities && !can_drop_capabilities() {
            let _ = writeln!(
                std::io::stderr(),
                "SKIP remote journal DAC fixture: cannot drop nonzero effective capabilities with setpriv; no mode/recovery claim was tested"
            );
            return;
        }
        let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&base).unwrap();
        let owned = tempfile::Builder::new()
            .prefix("remote-journal-v5-")
            .tempdir_in(base)
            .unwrap();
        let library = owned.path().join("helpers");
        prepare(&library);
        let fixture = owned.path().join("fixture.sh");
        fs::write(&fixture, FIXTURE.replace("\r\n", "\n")).unwrap();
        let stdout = owned.path().join("stdout.log");
        let stderr = owned.path().join("stderr.log");
        let mut command = Command::new("timeout");
        command.args(["--kill-after=2s", "90s"]);
        if drop_capabilities {
            command.args([
                "setpriv",
                "--bounding-set=-all",
                "--inh-caps=-all",
                "--ambient-caps=-all",
                "--no-new-privs",
            ]);
        }
        command
            .arg("sh")
            .arg(&fixture)
            .arg(&library)
            .arg(owned.path().join("data"))
            .arg(owned.path().join("logs"))
            .stdout(Stdio::from(fs::File::create(&stdout).unwrap()))
            .stderr(Stdio::from(fs::File::create(&stderr).unwrap()));
        let status = command
            .status()
            .expect("the Linux journal fixture requires GNU timeout");
        let output = fs::read_to_string(stdout).unwrap();
        let errors = fs::read_to_string(stderr).unwrap();
        if !status.success() {
            let retained = owned.keep();
            panic!(
                "journal fixture failed ({status}); evidence retained at {}\n{output}\n{errors}",
                retained.display()
            );
        }
        let journeys = output
            .lines()
            .filter(|line| line.starts_with("PASS ") && !line.starts_with("PASS all "))
            .count();
        assert_eq!(journeys, 16, "{output}\n{errors}");
        assert!(output.contains("capeff=0000000000000000"), "{output}");
    }
}
