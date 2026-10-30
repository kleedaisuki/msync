//! Metadata-only preparation and preparation-scoped native inspection caching.

use std::env;
use std::fs;
use std::path::{Path, PathBuf};

use super::{Result, ancestor_guards, backend_kind, local_path, metadata, quote, remote_command};
use crate::compiler::{Backend, Location};
use crate::plan::FileClass;

/// Classifies local metadata without opening a stream or contacting a remote endpoint.
pub(crate) fn inspect_location(location: &Location, source: bool) -> Result<FileClass> {
    Inspection::default().inspect(location, source)
}

/// Native metadata snapshot owned by one preparation, never by execution.
#[derive(Default)]
pub(crate) struct Inspection {
    pub(super) ancestors: std::collections::HashSet<PathBuf>,
    pub(crate) leaf_probes: u64,
    pub(crate) ancestor_probes: u64,
    pub(crate) ancestor_hits: u64,
}

impl Inspection {
    pub(crate) fn inspect(&mut self, location: &Location, source: bool) -> Result<FileClass> {
        if matches!(location.backend, Backend::Tcp { .. }) {
            return Ok(FileClass::Stream);
        }
        if matches!(location.backend, Backend::Http { .. }) && source {
            return Ok(FileClass::Regular);
        }
        let Some(path) = local_path(location, source)? else {
            return Ok(FileClass::Unknown);
        };
        #[cfg(windows)]
        if path
            .to_string_lossy()
            .replace('/', "\\")
            .to_ascii_lowercase()
            .starts_with(r"\\.\pipe\")
        {
            return Ok(FileClass::Stream);
        }
        self.leaf_probes += 1;
        let leaf = metadata(&path)?;
        self.check_parents(&path)?;
        classify_metadata(leaf)
    }

    fn check_parents(&mut self, path: &Path) -> Result<()> {
        let raw = if path.is_absolute() {
            path.to_owned()
        } else {
            env::current_dir()
                .map_err(|error| error.to_string())?
                .join(path)
        };
        let mut pending = Vec::new();
        for ancestor in ancestor_guards(&raw) {
            if self.ancestors.contains(ancestor) {
                self.ancestor_hits += 1;
                break;
            }
            self.ancestor_probes += 1;
            metadata(ancestor)?;
            pending.push(ancestor.to_owned());
        }
        // A failed outer prefix must not bless inner prefixes that traversed it.
        self.ancestors.extend(pending);
        Ok(())
    }
}

fn classify_metadata(leaf: Option<fs::Metadata>) -> Result<FileClass> {
    match leaf {
        None => Ok(FileClass::Missing),
        Some(meta) if meta.is_file() => Ok(FileClass::Regular),
        Some(meta) if meta.is_dir() => Ok(FileClass::Directory),
        Some(meta) => {
            #[cfg(unix)]
            {
                use std::os::unix::fs::FileTypeExt;
                if meta.file_type().is_fifo() || meta.file_type().is_socket() {
                    return Ok(FileClass::Stream);
                }
            }
            #[cfg(not(unix))]
            let _ = meta;
            Err(
                "unsupported filesystem entry: expected a regular file, directory, pipe or socket"
                    .into(),
            )
        }
    }
}

/// Remote classification uses stat-like shell tests only; it never opens file data.
#[cfg(all(test, windows))]
pub(crate) fn inspect_location_live(location: &Location, source: bool) -> Result<FileClass> {
    if !matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }) {
        return inspect_location(location, source);
    }
    inspect_locations_live(&location.backend, std::slice::from_ref(&location.path))?
        .pop()
        .ok_or_else(|| "remote metadata result is missing".to_owned())?
}

pub(crate) fn inspect_locations_live(
    backend: &Backend,
    paths: &[String],
) -> Result<Vec<Result<FileClass>>> {
    let mut trace = crate::telemetry::span("inspect_remote_batch");
    trace.expect_completion();
    trace.attribute("backend", backend_kind(backend));
    if let Backend::Ssh { host, user, port } = backend {
        trace.attribute_lazy("endpoint.host", || host.clone());
        if let Some(user) = user {
            trace.attribute_lazy("endpoint.user", || user.clone());
        }
        if let Some(port) = port {
            trace.metric("endpoint.port", *port as u64);
        }
    }
    trace.metric("paths", paths.len() as u64);
    let result = inspect_remote_batch(backend, paths, &mut trace);
    if let Ok(rows) = &result {
        trace.metric(
            "item_errors",
            rows.iter().filter(|row| row.is_err()).count() as u64,
        );
        for (path, row) in paths.iter().zip(rows) {
            if let Err(error) = row {
                trace.event(
                    "metadata.item_error",
                    &[("path", path), ("reason", error)],
                    &[],
                );
            }
        }
    }
    trace.finish_result(&result);
    result
}

fn inspect_remote_batch(
    backend: &Backend,
    paths: &[String],
    trace: &mut crate::telemetry::SpanGuard,
) -> Result<Vec<Result<FileClass>>> {
    if !matches!(backend, Backend::Wsl | Backend::Ssh { .. }) {
        return Err("remote metadata batch requires a WSL or SSH endpoint".into());
    }
    let mut results = Vec::with_capacity(paths.len());
    let fixed = quote(REMOTE_INSPECT).len() + 32;
    let mut start = 0;
    let mut invocations = 0;
    while start < paths.len() {
        let mut end = start;
        let mut budget = fixed;
        while end < paths.len() {
            let size = quote(&paths[end]).len() + 1;
            if budget.saturating_add(size) > 8 * 1024 {
                break;
            }
            budget += size;
            end += 1;
        }
        if end == start {
            results.push(Err(
                "remote metadata path exceeds the bounded 8KiB command budget".into(),
            ));
            start += 1;
            continue;
        }
        let args: Vec<&str> = paths[start..end].iter().map(String::as_str).collect();
        invocations += 1;
        trace.metric("client.invocations", invocations);
        trace.event(
            "metadata.batch",
            &[],
            &[("paths", args.len() as u64), ("argv.bytes", budget as u64)],
        );
        let output = remote_command(backend, REMOTE_INSPECT, &args)?;
        results.extend(parse_remote_inspections(&output.stdout, args.len())?);
        start = end;
    }
    Ok(results)
}

pub(super) fn parse_remote_inspections(
    output: &[u8],
    expected: usize,
) -> Result<Vec<Result<FileClass>>> {
    let fields: Vec<&[u8]> = output.split(|byte| *byte == 0).collect();
    if fields.len() != expected.saturating_mul(2).saturating_add(1)
        || fields.last() != Some(&b"".as_slice())
    {
        return Err("remote metadata probe returned an invalid record count or framing".into());
    }
    fields[..fields.len() - 1]
        .as_chunks::<2>()
        .0
        .iter()
        .map(|record| {
            Ok(match record[0] {
                b"error" => Err(String::from_utf8_lossy(record[1]).into_owned()),
                b"ok" => Ok(match record[1] {
                    b"missing" => FileClass::Missing,
                    b"regular" => FileClass::Regular,
                    b"directory" => FileClass::Directory,
                    b"stream" => FileClass::Stream,
                    _ => return Err("remote metadata probe returned an invalid class".into()),
                }),
                _ => return Err("remote metadata probe returned an invalid record tag".into()),
            })
        })
        .collect()
}

const REMOTE_INSPECT: &str = r#"
set -eu
inspect_one() (
p=$1
case $p in '~') p=$HOME;; '~/'*) p=$HOME/${p#\~/};; esac
case $p in /*) ;; *) p=$PWD/$p;; esac
a=$p
while [ "$a" != / ]; do
    [ ! -L "$a" ] || { echo 'refusing symbolic link in metadata probe' >&2; exit 1; }
    a=${a%/*}
    [ -n "$a" ] || a=/
done
if [ -f "$p" ]; then printf regular
elif [ -d "$p" ]; then printf directory
elif [ -p "$p" ] || [ -S "$p" ]; then printf stream
elif [ ! -e "$p" ]; then printf missing
else echo 'unsupported remote filesystem entry' >&2; exit 1
fi
)
for item in "$@"; do
    if value=$(inspect_one "$item" 2>&1); then
        printf 'ok\0%s\0' "$value"
    else
        printf 'error\0%s\0' "$value"
    fi
done
"#;
