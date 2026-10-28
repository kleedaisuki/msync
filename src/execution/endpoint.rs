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

#[derive(Clone, Debug)]
pub(super) struct Spec {
    pub destination: Location,
    pub mode: DestinationMode,
    pub span: Span,
    pattern: Option<String>,
    origin: Option<Location>,
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
    ) -> Result<Report> {
        let span = self.span;
        let cancel = cancel.child_token();
        let _cancel_on_drop = cancel.clone().drop_guard();
        let trace = crate::telemetry::context();
        // Receipt settlement must outlive a caller dropping its transfer future.
        // Dropping the caller cancels this worker, but does not abort its join.
        tokio::spawn(async move {
            let result = self.run_inner(source, basename, buffer, cancel).await;
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
            } else {
                "single"
            },
        ];
        arguments.extend(names.iter().map(String::as_str));
        let publication = command(backend, PUBLISH, &arguments, &cancel, span).await;
        let (committed, cleanup) = self.settle(journal, &cancel).await?;
        if committed {
            return Ok(Report {
                transfer: report,
                cleanup,
            });
        }
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
            let regex = regex::Regex::new(&format!("^(?:{pattern})$")).map_err(|cause| {
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
    let command = crate::runtime::remote_command_builder(backend, script, args)
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

async fn drain(reader: impl tokio::io::AsyncRead + Unpin) -> std::io::Result<Vec<u8>> {
    // Control responses are bounded independently of source payload buffers.
    let mut limited = reader.take(1024 * 1024);
    let mut bytes = Vec::new();
    limited.read_to_end(&mut bytes).await?;
    Ok(bytes)
}

const ALLOCATE: &str = r#"
set -eu
command -v flock >/dev/null || { echo 'remote publication requires flock' >&2; exit 1; }
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
umask 077
j=$(mktemp -d "$p/.msync-v4.XXXXXXXXXX")
printf '%s\n%s\n%s\n' "$j" "$d" "$r"
"#;

const PUBLISH: &str = r#"
set -eu
j=$1; d=$2; r=$3; mode=$4; selection=$5; shift 5
exec 9>"$j/lock"
flock -x 9
rollback() {
    if [ ! -e "$j/COMMITTED" ]; then
        if [ -e "$j/published" ]; then rm -rf -- "$r"; fi
        if [ -e "$j/before" ]; then mv -T -- "$j/before" "$r"; fi
    fi
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
for tree in "$j/payload" "$r"; do
    [ ! -e "$tree" ] || [ -z "$(find "$tree" ! -type f ! -type d -print -quit)" ] || fail "unsupported entry in tree: $tree"
done
[ "$mode" != exact ] || [ ! -d "$d" ] || fail 'destination is an existing directory; append / or /.'
[ "$mode" != replace ] || [ -d "$j/payload" ] || fail 'directory replacement requires a directory source'
[ "$mode" != replace ] || [ ! -e "$d" ] || [ -d "$d" ] || fail 'directory replacement target is not a directory'
if [ "$mode" = container ]; then
    [ ! -e "$d" ] || [ -d "$d" ] || fail 'container destination is not a directory'
    if [ -e "$r" ]; then cp -a --reflink=auto -- "$r" "$j/next"; else mkdir "$j/next"; fi
    case $d in "$r") next=$j/next;; *) next=$j/next/${d#"$r"/};; esac
    mkdir -p -- "$next"
    check() (
        a=$1; b=$2
        if [ -d "$a" ]; then
            [ ! -e "$b" ] || [ -d "$b" ] || fail "directory collides with file: $b"
            for x in "$a"/* "$a"/.[!.]* "$a"/..?*; do
                [ -e "$x" ] || continue
                check "$x" "$b/$(basename -- "$x")"
            done
        else [ ! -e "$b" ] || fail "container file already exists: $b"; fi
    )
    for name do
        a=$j/payload; [ "$selection" != selected ] || a=$a/$name
        check "$a" "$next/$name"
    done
    for name do
        a=$j/payload; [ "$selection" != selected ] || a=$a/$name
        if [ -d "$a" ] && [ -d "$next/$name" ]; then cp -a --reflink=auto -- "$a/." "$next/$name/"; else mv -T -- "$a" "$next/$name"; fi
    done
else
    if [ "$r" = "$d" ]; then mv -T -- "$j/payload" "$j/next"
    else
        mkdir "$j/next"
        next=$j/next/${d#"$r"/}
        mkdir -p -- "$(dirname -- "$next")"
        mv -T -- "$j/payload" "$next"
    fi
fi
[ ! -e "$j/ABORT" ] || fail 'publication cancelled'
if [ -e "$r" ]; then mv -T -- "$r" "$j/before"; fi
# Keep the before-image until the independent receipt query joins this lock.
: > "$j/published"
mv -T -- "$j/next" "$r"
: > "$j/COMMITTED"
printf 'COMMITTED\n'
"#;

const SETTLE: &str = r#"
set -eu
j=$1
[ -d "$j" ] || { echo 'remote journal disappeared' >&2; exit 1; }
: > "$j/ABORT"
exec 9>"$j/lock"
flock -x -w 30 9 || { echo 'remote publication still active; journal retained' >&2; exit 1; }
if [ -e "$j/COMMITTED" ]; then printf 'COMMITTED\n'
else
    # The publishing helper's EXIT trap restores the before-image under lock.
    [ ! -e "$j/before" ] || { echo 'remote before-image remains; journal retained' >&2; exit 1; }
    printf 'ABORTED\n'
fi
rm -rf -- "$j"
"#;
