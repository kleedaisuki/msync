//! Execution of compiled copy plans. Local trees are preflighted before mutation.
use crate::compiler::{Backend, CopyOp, Location, Program};
use crate::plan::FileClass;
use crate::syntax::Span;
use regex::Regex;
use std::cell::Cell;
use std::env;
use std::ffi::{OsStr, OsString};
use std::fmt;
use std::fs;
use std::io::{self, Read};
use std::path::{Component, Path, PathBuf};
use std::process::{Command, Output, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tokio_util::sync::CancellationToken;

mod namespace;
pub(crate) use namespace::{native_windows_filesystem, wsl_native_file};

#[derive(Clone, Debug, Default)]
pub struct ExecutionOptions {
    pub dry_run: bool,
}

#[derive(Clone, Debug, Default)]
pub struct ExecutionReport {
    pub copied_files: usize,
    pub created_directories: usize,
    pub transferred_bytes: u64,
    pub actions: Vec<String>,
}

#[derive(Clone, Debug)]
pub struct RuntimeError {
    pub message: String,
    pub span: Span,
}
impl fmt::Display for RuntimeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}
impl std::error::Error for RuntimeError {}

type Result<T> = std::result::Result<T, String>;

pub(crate) struct CopyOutcome {
    pub report: ExecutionReport,
    pub discarded_bytes: u64,
    pub cancelled: bool,
    pub interruption: Option<String>,
}

pub(crate) fn execute_copy_cancellable(
    op: &CopyOp,
    buffer_bytes: usize,
    cancel: CancellationToken,
) -> std::result::Result<ExecutionReport, RuntimeError> {
    let outcome = execute_copy_flow(op, buffer_bytes, cancel)?;
    if outcome.cancelled {
        return Err(RuntimeError {
            message: format!(
                "copy cancelled: {}; discarded {} byte(s); earlier committed {} byte(s) in {} file(s)",
                outcome
                    .interruption
                    .as_deref()
                    .unwrap_or("copy cancelled; current target unchanged"),
                outcome.discarded_bytes,
                outcome.report.transferred_bytes,
                outcome.report.copied_files
            ),
            span: op.span,
        });
    }
    Ok(outcome.report)
}

pub(crate) fn execute_copy_flow(
    op: &CopyOp,
    buffer_bytes: usize,
    cancel: CancellationToken,
) -> std::result::Result<CopyOutcome, RuntimeError> {
    let mut trace = crate::telemetry::span("copy");
    trace.expect_completion();
    trace.attribute_lazy("source.backend", || {
        backend_kind(&op.source.backend).to_owned()
    });
    trace.attribute_lazy("destination.backend", || {
        backend_kind(&op.destination.backend).to_owned()
    });
    trace.attribute_lazy("source.path", || op.source.path.clone());
    trace.attribute_lazy("destination.path", || op.destination.path.clone());
    let mut report = ExecutionReport::default();
    let context = CopyContext::new(buffer_bytes, &cancel);
    let result = context
        .check()
        .and_then(|()| execute_operation(op, &mut report, &context));
    trace.finish_result(&result);
    let cancelled = context.interrupted.get() || (cancel.is_cancelled() && result.is_err());
    if cancelled {
        trace.cancelled();
    }
    trace.metric("files", report.copied_files as u64);
    trace.metric("directories", report.created_directories as u64);
    trace.metric("bytes.committed", report.transferred_bytes);
    trace.metric("bytes.discarded", context.discarded_bytes.get());
    if cancelled {
        return Ok(CopyOutcome {
            report,
            discarded_bytes: context.discarded_bytes.get(),
            cancelled: true,
            interruption: result.err(),
        });
    }
    result
        .map_err(|message| RuntimeError {
            message,
            span: op.span,
        })
        .map(|()| CopyOutcome {
            report,
            discarded_bytes: 0,
            cancelled: false,
            interruption: None,
        })
}

fn backend_kind(backend: &Backend) -> &'static str {
    match backend {
        Backend::Local | Backend::Path | Backend::Env => "local",
        Backend::Wsl => "wsl",
        Backend::Ssh { .. } => "ssh",
        Backend::Http { .. } => "http",
        Backend::Tcp { .. } => "tcp",
    }
}

struct CopyContext<'a> {
    buffer_bytes: usize,
    cancel: &'a CancellationToken,
    discarded_bytes: Cell<u64>,
    interrupted: Cell<bool>,
}

impl<'a> CopyContext<'a> {
    fn new(buffer_bytes: usize, cancel: &'a CancellationToken) -> Self {
        Self {
            buffer_bytes,
            cancel,
            discarded_bytes: Cell::new(0),
            interrupted: Cell::new(false),
        }
    }
    fn check(&self) -> Result<()> {
        if self.buffer_bytes == 0 {
            return Err("copy buffer size must be greater than zero".into());
        }
        if self.cancel.is_cancelled() {
            self.interrupted.set(true);
            return Err("copy cancelled; uncommitted file data was discarded".into());
        }
        Ok(())
    }
}

/// Classifies local metadata without opening a stream or contacting a remote endpoint.
pub(crate) fn inspect_location(location: &Location, source: bool) -> Result<FileClass> {
    Inspection::default().inspect(location, source)
}

/// Native metadata snapshot owned by one preparation, never by execution.
#[derive(Default)]
pub(crate) struct Inspection {
    ancestors: std::collections::HashSet<PathBuf>,
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

fn parse_remote_inspections(output: &[u8], expected: usize) -> Result<Vec<Result<FileClass>>> {
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

/// Synchronous compatibility entry point using the same typed plan and scheduler as the CLI.
/// Async callers should use `scheduler::execute_plan_async` instead of nesting runtimes.
pub fn execute(
    program: &Program,
    options: &ExecutionOptions,
) -> std::result::Result<ExecutionReport, RuntimeError> {
    let mut trace = crate::telemetry::span("execute");
    trace.metric("operations", program.operations.len() as u64);
    let result = (|| {
        if let Some(op) = program
            .operations
            .iter()
            .find(|op| matches!(op.destination.backend, Backend::Http { .. } | Backend::Path))
        {
            return Err(RuntimeError {
                message: "HTTP and PATH endpoints are read-only".into(),
                span: op.span,
            });
        }
        if !options.dry_run && tokio::runtime::Handle::try_current().is_ok() {
            return Err(RuntimeError {
                message: "synchronous execute cannot nest inside Tokio; use scheduler::execute_plan_async".into(),
                span: program.operations.first().map(|op| op.span).unwrap_or(Span { start: 0, end: 0 }),
            });
        }
        let plan = crate::scheduler::prepare(program, !options.dry_run)?;
        trace.metric("planned_operations", plan.nodes.len() as u64);
        if options.dry_run {
            return Ok(ExecutionReport {
                actions: plan
                    .nodes
                    .iter()
                    .map(|node| describe_operation(&node.operation))
                    .collect(),
                ..ExecutionReport::default()
            });
        }
        crate::scheduler::execute_plan(&plan, &crate::scheduler::ResourceLimits::default())
            .map(|summary| summary.report)
    })();
    trace.finish_result(&result);
    if let Ok(report) = &result {
        trace.metric("files", report.copied_files as u64);
        trace.metric("directories", report.created_directories as u64);
    }
    result
}

fn describe_operation(op: &CopyOp) -> String {
    let selection = op
        .pattern
        .as_ref()
        .map(|pattern| format!(" match {pattern:?}"))
        .unwrap_or_default();
    format!("{}{selection} -> {}", op.source, op.destination)
}

fn execute_operation(
    op: &CopyOp,
    report: &mut ExecutionReport,
    context: &CopyContext<'_>,
) -> Result<()> {
    context.check()?;
    if matches!(op.destination.backend, Backend::Http { .. } | Backend::Path) {
        return Err("HTTP and PATH endpoints are read-only".into());
    }
    report.actions.push(describe_operation(op));
    let source = local_path(&op.source, true)?;
    let destination = local_path(&op.destination, false)?;
    if let Some(destination) = &destination {
        validate_destination(destination, op.destination.path.ends_with('/'))?;
        if matches!(op.source.backend, Backend::Http { .. }) {
            return http_to_local(op, destination, report, context);
        }
    }
    match (source, destination) {
        (Some(source), Some(destination)) => copy_selected(
            &source,
            &destination,
            op.destination.path.ends_with('/'),
            op.pattern.as_deref(),
            report,
            context,
        ),
        (source, destination) => transport_copy(
            op,
            source.as_deref(),
            destination.as_deref(),
            report,
            context,
        ),
    }
}

fn http_to_local(
    op: &CopyOp,
    destination: &Path,
    report: &mut ExecutionReport,
    context: &CopyContext<'_>,
) -> Result<()> {
    if op.pattern.is_some() {
        return Err("HTTP sources cannot enumerate a regex selection".into());
    }
    context.check()?;
    let mut command = http_command_builder(&op.source)?;
    let name = source_basename(&op.source.path);
    validate_staging_basename(name)?;
    let container = op.destination.path.ends_with('/');
    let target = if container {
        destination.join(name)
    } else {
        destination.to_owned()
    };
    check_ancestors(&target)?;
    if let Some(parent) = target.parent() {
        ensure_directory(parent, report)?;
    }
    let mut trace = crate::telemetry::span("file_flow");
    trace.expect_completion();
    trace.attribute("source.backend", "http");
    trace.attribute("destination.backend", "local");
    trace.attribute_lazy("source.path", || op.source.path.clone());
    trace.attribute_lazy("destination.path", || target.display().to_string());
    let result = (|| {
        context.check()?;
        trace.event(
            "file.open",
            &[("mode", if container { "create" } else { "overwrite" })],
            &[],
        );
        let stage = crate::io::StagedFile::new(&target, container).map_err(|error| {
            trace.io_error("open_stage", &error);
            at(&target, error)
        })?;
        trace.attribute_lazy("stage.path", || stage.path().display().to_string());
        trace.event("file.transfer", &[("strategy", "http_direct_stage")], &[]);
        let transfer = run_cancellable(command.arg("--output").arg(stage.path()), context);
        let bytes = match fs::metadata(stage.path()) {
            Ok(metadata) => metadata.len(),
            Err(_) if transfer.is_err() => 0,
            Err(error) => {
                trace.io_error("stage_metadata", &error);
                return Err(at(stage.path(), error));
            }
        };
        if let Err(error) = transfer {
            context.discarded_bytes.set(bytes);
            trace.metric("bytes.discarded", bytes);
            trace.event(
                "file.rollback",
                &[("target_state", "unchanged")],
                &[("bytes.discarded", bytes)],
            );
            return Err(format!(
                "HTTP flow interrupted; target unchanged; discarded {bytes} uncommitted byte(s): {error}"
            ));
        }
        if context.cancel.is_cancelled() {
            context.interrupted.set(true);
            context.discarded_bytes.set(bytes);
            return Err(format!(
                "HTTP flow cancelled; target unchanged; discarded {bytes} uncommitted byte(s)"
            ));
        }
        metadata(&target)?;
        stage.commit().map_err(|error| {
            trace.io_error("commit", &error);
            at(&target, error)
        })?;
        trace.metric("bytes.committed", bytes);
        trace.event(
            "file.commit",
            &[("target_state", "published")],
            &[("bytes.committed", bytes)],
        );
        Ok(bytes)
    })();
    trace.finish_result(&result);
    if context.interrupted.get() || context.cancel.is_cancelled() {
        trace.cancelled();
    }
    let bytes = result?;
    report.copied_files += 1;
    report.transferred_bytes = report.transferred_bytes.saturating_add(bytes);
    Ok(())
}

fn source_basename(path: &str) -> &str {
    path.trim_end_matches('/')
        .rsplit('/')
        .next()
        .filter(|name| !matches!(*name, "" | "." | ".." | "~"))
        .unwrap_or("download")
}

pub(crate) fn local_path(location: &Location, source: bool) -> Result<Option<PathBuf>> {
    let path = match &location.backend {
        Backend::Local => Ok(Some(expand_home(&location.path)?)),
        Backend::Env => {
            let (name, suffix) = location
                .path
                .split_once('/')
                .unwrap_or((&location.path, ""));
            if name.is_empty() {
                return Err("missing environment variable name".into());
            }
            let value = env::var_os(name)
                .ok_or_else(|| format!("environment variable {name:?} is not set"))?;
            if value.is_empty() {
                return Err(format!("environment variable {name:?} is empty"));
            }
            Ok(Some(PathBuf::from(value).join(suffix)))
        }
        Backend::Path if source => {
            let name = Path::new(&location.path);
            if name.components().count() != 1 {
                return Err("PATH source must be a single file name".into());
            }
            let paths = env::var_os("PATH").unwrap_or_default();
            for directory in env::split_paths(&paths) {
                let candidate = native_path(directory.join(name));
                if candidate.is_file() {
                    return Ok(Some(candidate));
                }
            }
            Err(format!("file {:?} was not found in PATH", location.path))
        }
        _ => Ok(None),
    }?;
    Ok(path.map(native_path))
}

pub(crate) fn native_path(path: PathBuf) -> PathBuf {
    #[cfg(windows)]
    {
        use std::os::windows::ffi::{OsStrExt, OsStringExt};
        if !path.as_os_str().as_encoded_bytes().contains(&b'/') {
            return path;
        }
        let native: Vec<u16> = path
            .as_os_str()
            .encode_wide()
            .map(|unit| {
                if unit == u16::from(b'/') {
                    u16::from(b'\\')
                } else {
                    unit
                }
            })
            .collect();
        PathBuf::from(std::ffi::OsString::from_wide(&native))
    }
    #[cfg(not(windows))]
    {
        path
    }
}

fn expand_home(path: &str) -> Result<PathBuf> {
    if path == "~" || path.starts_with("~/") || path.starts_with("~\\") {
        let home = env::var_os("HOME")
            .or_else(|| env::var_os("USERPROFILE"))
            .ok_or("home directory is not configured")?;
        return Ok(PathBuf::from(home).join(path.get(2..).unwrap_or("")));
    }
    if path.starts_with('~') {
        return Err("~user paths are not supported; use ~/ or an absolute path".into());
    }
    Ok(PathBuf::from(path))
}

fn copy_selected(
    source: &Path,
    destination: &Path,
    container: bool,
    pattern: Option<&str>,
    report: &mut ExecutionReport,
    context: &CopyContext<'_>,
) -> Result<()> {
    context.check()?;
    validate_destination(destination, container)?;
    check_ancestors(source)?;
    check_ancestors(destination)?;
    metadata(source)?.ok_or_else(|| format!("source does not exist: {}", source.display()))?;
    let sources = match pattern {
        Some(pattern) => {
            let regex =
                Regex::new(&format!("\\A(?:{pattern})\\z")).map_err(|error| error.to_string())?;
            let mut selected = Vec::new();
            for entry in fs::read_dir(source).map_err(|error| at(source, error))? {
                context.check()?;
                let entry = entry.map_err(|error| at(source, error))?;
                if regex.is_match(&entry.file_name().to_string_lossy()) {
                    selected.push(entry.path());
                }
            }
            selected.sort();
            selected
        }
        None => vec![source.to_owned()],
    };
    if pattern.is_some() && !container {
        return Err("a regex selection requires a destination ending in /".into());
    }
    let mut plans = Vec::with_capacity(sources.len());
    for source in sources {
        context.check()?;
        let target = if container {
            let name = source
                .file_name()
                .ok_or("cannot preserve the name of a filesystem root")?;
            destination.join(name)
        } else {
            destination.to_owned()
        };
        preflight_cancellable(&source, &target, container, context)?;
        plans.push((source, target));
    }
    for (source, target) in plans {
        copy_tree(&source, &target, container, report, context)?;
    }
    Ok(())
}

fn validate_destination(destination: &Path, container: bool) -> Result<()> {
    if !container && metadata(destination)?.is_some_and(|meta| meta.is_dir()) {
        return Err(
            "destination is an existing directory; append / to use it as a container".into(),
        );
    }
    Ok(())
}

fn at(path: &Path, error: io::Error) -> String {
    crate::telemetry::event(
        "filesystem.error",
        &[("path", &path.to_string_lossy())],
        &[],
    );
    crate::telemetry::io_error("filesystem", &error);
    format!("{}: {error}", path.display())
}

fn metadata(path: &Path) -> Result<Option<fs::Metadata>> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => {
            if metadata.file_type().is_symlink() {
                return Err(format!("refusing symbolic link: {}", path.display()));
            }
            #[cfg(windows)]
            {
                use std::os::windows::fs::MetadataExt;
                if metadata.file_attributes() & 0x400 != 0 {
                    return Err(format!("refusing reparse point: {}", path.display()));
                }
            }
            Ok(Some(metadata))
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(at(path, error)),
    }
}

fn absolute(path: &Path) -> Result<PathBuf> {
    let path = if path.is_absolute() {
        path.to_owned()
    } else {
        env::current_dir()
            .map_err(|error| error.to_string())?
            .join(path)
    };
    let mut normalized = PathBuf::new();
    for part in path.components() {
        match part {
            Component::CurDir => {}
            Component::ParentDir => {
                normalized.pop();
            }
            component => normalized.push(component.as_os_str()),
        }
    }
    Ok(normalized)
}

pub(crate) fn check_ancestors(path: &Path) -> Result<()> {
    let raw = if path.is_absolute() {
        path.to_owned()
    } else {
        env::current_dir()
            .map_err(|error| error.to_string())?
            .join(path)
    };
    metadata(&raw)?;
    for ancestor in ancestor_guards(&raw) {
        metadata(ancestor)?;
    }
    Ok(())
}

fn ancestor_guards(raw: &Path) -> impl Iterator<Item = &Path> {
    // lstat("link/") and lstat("link/.") follow a directory link, while parent()
    // skips that final component. Trim ignored suffixes without rebuilding the
    // path: collecting/pushing Windows verbatim components could collapse '..'.
    let trimmed = raw.components().as_path();
    let checked_leaf = usize::from(trimmed.as_os_str() == raw.as_os_str());
    trimmed.ancestors().skip(checked_leaf)
}

#[cfg(all(test, windows))]
fn preflight(source: &Path, target: &Path, no_clobber: bool) -> Result<()> {
    preflight_cancellable(
        source,
        target,
        no_clobber,
        &CopyContext::new(64 * 1024, &CancellationToken::new()),
    )
}

fn preflight_cancellable(
    source: &Path,
    target: &Path,
    no_clobber: bool,
    context: &CopyContext<'_>,
) -> Result<()> {
    context.check()?;
    check_ancestors(source)?;
    check_ancestors(target)?;
    let source_meta =
        metadata(source)?.ok_or_else(|| format!("source does not exist: {}", source.display()))?;
    let target_meta = metadata(target)?;
    if target_meta.is_some()
        && same_file::is_same_file(source, target).map_err(|error| at(target, error))?
    {
        return Err(format!(
            "source and destination refer to the same file: {}",
            target.display()
        ));
    }
    if source_meta.is_dir() {
        if trees_overlap(source, target, target_meta.is_some())? {
            return Err("source and destination directory trees overlap".into());
        }
        if target_meta.as_ref().is_some_and(|meta| !meta.is_dir()) {
            return Err(format!(
                "directory destination is occupied by a file: {}",
                target.display()
            ));
        }
        for entry in fs::read_dir(source).map_err(|error| at(source, error))? {
            let entry = entry.map_err(|error| at(source, error))?;
            preflight_cancellable(
                &entry.path(),
                &target.join(entry.file_name()),
                no_clobber,
                context,
            )?;
        }
    } else if source_meta.is_file() {
        if target_meta.as_ref().is_some_and(|meta| meta.is_dir()) {
            return Err(format!(
                "file destination is a directory: {} (append / to preserve the source name)",
                target.display()
            ));
        }
        if no_clobber && target_meta.is_some() {
            return Err(format!(
                "destination file already exists: {}",
                target.display()
            ));
        }
    } else {
        return Err(format!("unsupported special file: {}", source.display()));
    }
    Ok(())
}

fn trees_overlap(source: &Path, target: &Path, target_exists: bool) -> Result<bool> {
    let source_root = same_file::Handle::from_path(source).map_err(|error| at(source, error))?;
    if ancestor_has_identity(&source_root, target)? {
        return Ok(true);
    }
    if !target_exists {
        return Ok(false);
    }
    let target_root = same_file::Handle::from_path(target).map_err(|error| at(target, error))?;
    ancestor_has_identity(&target_root, source)
}

fn ancestor_has_identity(root: &same_file::Handle, path: &Path) -> Result<bool> {
    // File identity also covers Windows verbatim prefixes, short names and case aliases.
    for ancestor in absolute(path)?.ancestors() {
        match same_file::Handle::from_path(ancestor) {
            Ok(handle) if handle == *root => return Ok(true),
            Ok(_) => {}
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(at(ancestor, error)),
        }
    }
    Ok(false)
}

fn validate_source_tree(source: &Path, context: &CopyContext<'_>) -> Result<()> {
    context.check()?;
    check_ancestors(source)?;
    let meta =
        metadata(source)?.ok_or_else(|| format!("source does not exist: {}", source.display()))?;
    if meta.is_dir() {
        for entry in fs::read_dir(source).map_err(|error| at(source, error))? {
            validate_source_tree(&entry.map_err(|error| at(source, error))?.path(), context)?;
        }
    } else if !meta.is_file() {
        return Err(format!("unsupported special file: {}", source.display()));
    }
    Ok(())
}

fn ensure_directory(path: &Path, report: &mut ExecutionReport) -> Result<()> {
    if path.as_os_str().is_empty() {
        return Ok(());
    }
    match metadata(path)? {
        Some(meta) if meta.is_dir() => Ok(()),
        Some(_) => Err(format!("not a directory: {}", path.display())),
        None => {
            if let Some(parent) = path.parent() {
                ensure_directory(parent, report)?;
            }
            match fs::create_dir(path) {
                Ok(()) => {
                    report.created_directories += 1;
                    Ok(())
                }
                Err(error) if error.kind() == io::ErrorKind::AlreadyExists => {
                    match metadata(path)? {
                        Some(meta) if meta.is_dir() => Ok(()),
                        _ => Err(format!(
                            "directory creation raced with a non-directory: {}",
                            path.display()
                        )),
                    }
                }
                Err(error) => Err(at(path, error)),
            }
        }
    }
}

fn copy_tree(
    source: &Path,
    target: &Path,
    no_clobber: bool,
    report: &mut ExecutionReport,
    context: &CopyContext<'_>,
) -> Result<()> {
    context.check()?;
    let source_meta =
        metadata(source)?.ok_or_else(|| format!("source disappeared: {}", source.display()))?;
    if source_meta.is_dir() {
        ensure_directory(target, report)?;
        for entry in fs::read_dir(source).map_err(|error| at(source, error))? {
            let entry = entry.map_err(|error| at(source, error))?;
            copy_tree(
                &entry.path(),
                &target.join(entry.file_name()),
                no_clobber,
                report,
                context,
            )?;
        }
        return Ok(());
    }
    if let Some(parent) = target.parent() {
        ensure_directory(parent, report)?;
    }
    metadata(target)?;
    let mut trace = crate::telemetry::span("file_flow");
    trace.expect_completion();
    trace.attribute_lazy("source.path", || source.display().to_string());
    trace.attribute_lazy("destination.path", || target.display().to_string());
    trace.attribute("source.backend", "local");
    trace.attribute("destination.backend", "local");
    trace.metric("buffer_bytes", context.buffer_bytes as u64);
    let result = transfer_regular_file(
        source,
        target,
        no_clobber,
        source_meta.permissions(),
        context,
        &mut trace,
    );
    trace.finish_result(&result);
    if context.cancel.is_cancelled() {
        trace.cancelled();
    }
    let bytes = result?;
    report.copied_files += 1;
    report.transferred_bytes = report.transferred_bytes.saturating_add(bytes);
    Ok(())
}

fn transfer_regular_file(
    source: &Path,
    target: &Path,
    no_clobber: bool,
    permissions: fs::Permissions,
    context: &CopyContext<'_>,
    trace: &mut crate::telemetry::SpanGuard,
) -> Result<u64> {
    trace.event(
        "file.open",
        &[("mode", if no_clobber { "create" } else { "overwrite" })],
        &[],
    );
    let stage = crate::io::StagedFile::new(target, no_clobber).map_err(|error| {
        trace.io_error("open_stage", &error);
        at(target, error)
    })?;
    trace.attribute_lazy("stage.path", || stage.path().display().to_string());
    trace.event("file.transfer", &[("strategy", "kernel_assisted")], &[]);
    let bytes = crate::io::copy_file(source, stage.path(), context.buffer_bytes, context.cancel)
        .map_err(|error| {
            let bytes = crate::io::transferred_bytes(&error);
            context.discarded_bytes.set(bytes);
            if error.kind() == io::ErrorKind::Interrupted { context.interrupted.set(true); }
            trace.io_error("transfer", &error);
            trace.metric("bytes.discarded", bytes);
            trace.event("file.rollback", &[("target_state", "unchanged")], &[("bytes.discarded", bytes)]);
            let outcome = if error.kind() == io::ErrorKind::Interrupted || context.cancel.is_cancelled() { "cancelled" } else { "failed" };
            format!("file flow {outcome}; target unchanged; discarded {bytes} uncommitted byte(s): {error}")
        })?;
    if context.cancel.is_cancelled() {
        context.discarded_bytes.set(bytes);
        context.interrupted.set(true);
        trace.metric("bytes.discarded", bytes);
        return Err(format!(
            "file flow cancelled; target unchanged; discarded {bytes} uncommitted byte(s)"
        ));
    }
    fs::set_permissions(stage.path(), permissions).map_err(|error| {
        trace.io_error("permissions", &error);
        at(stage.path(), error)
    })?;
    metadata(target)?;
    if context.cancel.is_cancelled() {
        context.discarded_bytes.set(bytes);
        context.interrupted.set(true);
        trace.metric("bytes.discarded", bytes);
        return Err(format!(
            "file flow cancelled; target unchanged; discarded {bytes} uncommitted byte(s)"
        ));
    }
    stage.commit().map_err(|error| {
        trace.io_error("commit", &error);
        at(target, error)
    })?;
    trace.metric("bytes.committed", bytes);
    trace.event(
        "file.commit",
        &[("target_state", "published")],
        &[("bytes.committed", bytes)],
    );
    Ok(bytes)
}

static SCRATCH_ID: AtomicU64 = AtomicU64::new(0);
struct Scratch(PathBuf);
impl Scratch {
    fn new() -> Result<Self> {
        let root = env::current_dir()
            .map_err(|error| error.to_string())?
            .join(".temp");
        Self::new_in(&root)
    }

    fn new_in(root: &Path) -> Result<Self> {
        check_ancestors(root)?;
        fs::create_dir_all(root).map_err(|error| at(root, error))?;
        for _ in 0..100 {
            let path = root.join(format!(
                "msync-{}-{}",
                std::process::id(),
                SCRATCH_ID.fetch_add(1, Ordering::Relaxed)
            ));
            match fs::create_dir(&path) {
                Ok(()) => return Ok(Self(path)),
                Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
                Err(error) => return Err(at(&path, error)),
            }
        }
        Err("could not allocate transport scratch directory".into())
    }
}
impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}

#[derive(Debug)]
pub(crate) struct ImportReceipt {
    pub basename: Option<OsString>,
}

struct Received {
    path: PathBuf,
    basename: Option<OsString>,
}

const SOURCE_RECEIPT: &[u8] = b"MSYNC-SOURCE-1\0";

struct SourceReceipt {
    basename: String,
    times: Vec<(PathBuf, SystemTime)>,
}

impl SourceReceipt {
    fn parse(bytes: &[u8]) -> Result<Self> {
        let bytes = bytes
            .strip_prefix(SOURCE_RECEIPT)
            .ok_or("unsupported source metadata receipt")?;
        let text =
            std::str::from_utf8(bytes).map_err(|_| "remote source contains non-UTF-8 metadata")?;
        let fields: Vec<_> = text.split_terminator('\0').collect();
        if !text.ends_with('\0') || fields.is_empty() || !fields.len().is_multiple_of(3) {
            return Err("remote source metadata receipt is incomplete".into());
        }
        let mut times = Vec::with_capacity(fields.len() / 3);
        let mut names = Vec::new();
        let mut seen = std::collections::HashSet::new();
        for (index, entry) in fields.as_chunks::<3>().0.iter().enumerate() {
            let relative = source_relative(entry[0])?;
            if relative.as_os_str().is_empty() != (index == 0) {
                return Err("remote source metadata root must appear exactly once, first".into());
            }
            if !seen.insert(relative.clone()) {
                return Err("remote source metadata contains duplicate paths".into());
            }
            if index != 0 && relative.file_name() != Some(OsStr::new(entry[1])) {
                return Err("remote source metadata basename disagrees with its path".into());
            }
            names.extend_from_slice(entry[0].as_bytes());
            names.push(0);
            names.extend_from_slice(entry[1].as_bytes());
            names.push(0);
            times.push((relative, source_time(entry[2])?));
        }
        validate_remote_names(&names)?;
        Ok(Self {
            basename: fields[1].into(),
            times,
        })
    }

    fn apply_times(&self, root: &Path, context: &CopyContext<'_>) -> Result<()> {
        for (relative, modified) in self.times.iter().rev() {
            context.check()?;
            let path = crate::filesystem::entry_path(root, relative);
            check_ancestors(&path)?;
            let metadata = metadata(&path)?
                .ok_or_else(|| format!("captured source entry is missing: {}", path.display()))?;
            if !metadata.is_file() && !metadata.is_dir() {
                return Err(format!(
                    "captured source entry is not a finite file or directory: {}",
                    path.display()
                ));
            }
            let mut metadata = crate::filesystem::NodeMetadata::capture(&metadata)
                .map_err(|cause| at(&path, cause))?;
            metadata.modified = *modified;
            metadata.apply(&path).map_err(|cause| at(&path, cause))?;
        }
        Ok(())
    }
}

fn source_relative(text: &str) -> Result<PathBuf> {
    let mut path = PathBuf::new();
    if text.is_empty() {
        return Ok(path);
    }
    for name in text.split('/') {
        if matches!(name, "" | "." | "..") {
            return Err("remote source metadata path escapes its capture root".into());
        }
        validate_staging_basename(name)?;
        path.push(name);
    }
    Ok(path)
}

fn source_time(text: &str) -> Result<SystemTime> {
    let seconds: i64 = text
        .parse()
        .map_err(|_| "remote source timestamp is not a supported whole-second value")?;
    let duration = Duration::from_secs(seconds.unsigned_abs());
    if seconds < 0 {
        UNIX_EPOCH.checked_sub(duration)
    } else {
        UNIX_EPOCH.checked_add(duration)
    }
    .ok_or_else(|| "remote source timestamp is outside the host time range".into())
}

#[derive(Clone, Copy)]
enum SourcePermissions {
    Filesystem,
    Portable,
}

impl SourcePermissions {
    fn argument(self) -> &'static str {
        match self {
            Self::Filesystem => "filesystem",
            Self::Portable => "portable",
        }
    }
}

#[derive(Clone, Copy)]
struct RemoteSource<'a> {
    path: &'a str,
    permissions: SourcePermissions,
}

impl<'a> RemoteSource<'a> {
    fn filesystem(path: &'a str) -> Self {
        Self {
            path,
            permissions: SourcePermissions::Filesystem,
        }
    }

    fn uploaded(path: &'a str) -> Self {
        Self {
            path,
            permissions: if cfg!(windows) {
                SourcePermissions::Portable
            } else {
                SourcePermissions::Filesystem
            },
        }
    }
}

pub(crate) fn import_finite_source(
    source: &Location,
    target: &Path,
    pattern: Option<&str>,
    buffer_bytes: usize,
    cancel: CancellationToken,
    span: Span,
) -> std::result::Result<ImportReceipt, RuntimeError> {
    let context = CopyContext::new(buffer_bytes, &cancel);
    let result = (|| {
        context.check()?;
        let parent = target
            .parent()
            .ok_or("private import target has no parent")?;
        let scratch = Scratch::new_in(parent)?;
        // Killing the Windows WSL launcher does not join its guest writer.
        // Drain an admitted finite acquisition before retiring its owned paths.
        let acquisition_cancel = if matches!(source.backend, Backend::Wsl) {
            CancellationToken::new()
        } else {
            cancel.clone()
        };
        let acquisition = CopyContext::new(buffer_bytes, &acquisition_cancel);
        let received = receive(source, &scratch, pattern, &acquisition)?;
        adopt_received(received, target, &context)
    })();
    result.map_err(|message| RuntimeError { message, span })
}

fn adopt_received(
    received: Received,
    target: &Path,
    context: &CopyContext<'_>,
) -> Result<ImportReceipt> {
    context.check()?;
    check_ancestors(target)?;
    if metadata(target)?.is_some() {
        return Err(format!(
            "private import target already exists: {}",
            target.display()
        ));
    }
    crate::filesystem::move_new(&received.path, target).map_err(|cause| at(target, cause))?;
    Ok(ImportReceipt {
        basename: received.basename,
    })
}

fn run(command: &mut Command) -> Result<Output> {
    run_cancellable(
        command,
        &CopyContext::new(64 * 1024, &CancellationToken::new()),
    )
}

fn run_cancellable(command: &mut Command, context: &CopyContext<'_>) -> Result<Output> {
    let mut trace = crate::telemetry::span("transport_process");
    trace.expect_completion();
    trace.attribute_lazy("program", || {
        command.get_program().to_string_lossy().into_owned()
    });
    let result = run_process(command, context, &mut trace);
    trace.finish_result(&result);
    if context.cancel.is_cancelled() {
        trace.cancelled();
    }
    result
}

fn run_process(
    command: &mut Command,
    context: &CopyContext<'_>,
    trace: &mut crate::telemetry::SpanGuard,
) -> Result<Output> {
    context.check()?;
    let name = command.get_program().to_string_lossy().into_owned();
    let mut child = command
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|error| {
            trace.io_error("spawn", &error);
            format!("cannot start {name}: {error}")
        })?;
    trace.metric("pid", child.id() as u64);
    trace.event("process.open", &[], &[]);
    let stdout = child
        .stdout
        .take()
        .ok_or("subprocess stdout is unavailable")?;
    let stderr = child
        .stderr
        .take()
        .ok_or("subprocess stderr is unavailable")?;
    let output = std::thread::spawn(move || drain_bounded(stdout, 8 * 1024 * 1024));
    let errors = std::thread::spawn(move || drain_bounded(stderr, 64 * 1024));
    let mut cancelled = false;
    let status = loop {
        if context.cancel.is_cancelled() {
            cancelled = true;
            let _ = child.kill();
            break child.wait();
        }
        match child.try_wait() {
            Ok(Some(status)) => break Ok(status),
            Ok(None) => std::thread::sleep(std::time::Duration::from_millis(10)),
            Err(error) => {
                let _ = child.kill();
                let _ = child.wait();
                break Err(error);
            }
        }
    };
    let stdout = output
        .join()
        .map_err(|_| "subprocess output reader failed")?
        .map_err(|error| {
            trace.io_error("stdout_read", &error);
            error.to_string()
        })?;
    let stderr = errors
        .join()
        .map_err(|_| "subprocess error reader failed")?
        .map_err(|error| {
            trace.io_error("stderr_read", &error);
            error.to_string()
        })?;
    trace.metric("stdout_bytes", stdout.0.len() as u64);
    trace.metric("stderr_bytes", stderr.0.len() as u64);
    if cancelled {
        context.interrupted.set(true);
        return Err(format!("{name} cancelled; transport process closed"));
    }
    let status = status.map_err(|error| {
        trace.io_error("wait", &error);
        format!("cannot wait for {name}: {error}")
    })?;
    trace.event("process.exit", &[("status", &status.to_string())], &[]);
    if stdout.1 {
        return Err(format!(
            "{name} metadata output exceeded the 8MiB bound; split the source selection"
        ));
    }
    if !status.success() {
        trace.event(
            "process.error",
            &[
                ("status", &status.to_string()),
                ("stderr", &String::from_utf8_lossy(&stderr.0)),
            ],
            &[],
        );
        return Err(format!(
            "{name} failed ({status}): {}{}",
            String::from_utf8_lossy(&stderr.0).trim(),
            if stderr.1 { " [stderr truncated]" } else { "" }
        ));
    }
    Ok(Output {
        status,
        stdout: stdout.0,
        stderr: stderr.0,
    })
}

fn drain_bounded(mut reader: impl Read, capacity: usize) -> io::Result<(Vec<u8>, bool)> {
    let mut output = Vec::new();
    let mut buffer = [0_u8; 8192];
    let mut truncated = false;
    loop {
        let amount = match reader.read(&mut buffer) {
            Ok(amount) => amount,
            Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
            Err(error) => return Err(error),
        };
        if amount == 0 {
            return Ok((output, truncated));
        }
        let keep = amount.min(capacity.saturating_sub(output.len()));
        output.extend_from_slice(&buffer[..keep]);
        truncated |= keep < amount;
    }
}

fn quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "'\\''"))
}

pub(crate) fn remote_command(backend: &Backend, script: &str, args: &[&str]) -> Result<Output> {
    run(&mut remote_command_builder(backend, script, args)?)
}

pub(crate) fn remote_command_with_cancel(
    backend: &Backend,
    script: &str,
    args: &[&str],
    buffer: usize,
    cancel: &CancellationToken,
) -> Result<Output> {
    remote_command_cancellable(backend, script, args, &CopyContext::new(buffer, cancel))
}

fn remote_command_cancellable(
    backend: &Backend,
    script: &str,
    args: &[&str],
    context: &CopyContext<'_>,
) -> Result<Output> {
    run_cancellable(&mut remote_command_builder(backend, script, args)?, context)
}

pub(crate) fn remote_command_builder(
    backend: &Backend,
    script: &str,
    args: &[&str],
) -> Result<Command> {
    let invocation = format!(
        "sh -c {} sh {}",
        quote(script),
        args.iter()
            .map(|arg| quote(arg))
            .collect::<Vec<_>>()
            .join(" ")
    );
    match backend {
        Backend::Wsl => {
            let mut command = Command::new("wsl");
            // WSL launcher diagnostics otherwise mix UTF-16 into our UTF-8 text.
            // This is per child, never a change to the user's WSL configuration.
            command.env("WSL_UTF8", "1");
            command.args(["--exec", "sh", "-c", &invocation]);
            Ok(command)
        }
        Backend::Ssh { host, user, port } => {
            let mut command = ssh_client("ssh");
            if let Some(port) = port {
                command.args(["-p", &port.to_string()]);
            }
            command
                .arg("--")
                .arg(ssh_host(host, user.as_deref()))
                .arg(invocation);
            Ok(command)
        }
        _ => Err("not a remote filesystem endpoint".into()),
    }
}

fn ssh_host(host: &str, user: Option<&str>) -> String {
    let host = if host.contains(':') && !host.starts_with('[') {
        format!("[{host}]")
    } else {
        host.to_owned()
    };
    match user {
        Some(user) => format!("{user}@{host}"),
        None => host,
    }
}

fn transport_copy(
    op: &CopyOp,
    source: Option<&Path>,
    destination: Option<&Path>,
    report: &mut ExecutionReport,
    context: &CopyContext<'_>,
) -> Result<()> {
    context.check()?;
    let uses_wsl =
        matches!(op.source.backend, Backend::Wsl) || matches!(op.destination.backend, Backend::Wsl);
    let wsl_source = source.is_some() || matches!(op.source.backend, Backend::Wsl);
    let wsl_destination = destination.is_some() || matches!(op.destination.backend, Backend::Wsl);
    if uses_wsl && wsl_source && wsl_destination {
        let source_path = wsl_path_cancellable(source, &op.source.path, context)?;
        let target_path = wsl_path_cancellable(destination, &op.destination.path, context)?;
        remote_copy(
            &Backend::Wsl,
            &source_path,
            &target_path,
            op.destination.path.ends_with('/'),
            op.pattern.as_deref(),
            report,
            context,
        )?;
        return Ok(());
    }
    if op.source.backend == op.destination.backend
        && matches!(op.source.backend, Backend::Ssh { .. })
    {
        return remote_copy(
            &op.source.backend,
            &op.source.path,
            &op.destination.path,
            op.destination.path.ends_with('/'),
            op.pattern.as_deref(),
            report,
            context,
        );
    }
    if let Some(source) = source {
        return send(
            source,
            &op.destination,
            op.destination.path.ends_with('/'),
            op.pattern.as_deref(),
            report,
            context,
        );
    }
    let scratch = Scratch::new()?;
    let staged = receive(&op.source, &scratch, op.pattern.as_deref(), context)?;
    let source = &staged.path;
    if let Some(destination) = destination {
        let pattern = if matches!(op.source.backend, Backend::Http { .. }) {
            None
        } else {
            op.pattern.as_deref()
        };
        return copy_selected(
            source,
            destination,
            op.destination.path.ends_with('/'),
            pattern,
            report,
            context,
        );
    }
    send(
        source,
        &op.destination,
        op.destination.path.ends_with('/'),
        op.pattern.as_deref(),
        report,
        context,
    )
}

#[cfg(all(test, windows))]
fn wsl_path(local: Option<&Path>, remote: &str) -> Result<String> {
    wsl_path_cancellable(
        local,
        remote,
        &CopyContext::new(64 * 1024, &CancellationToken::new()),
    )
}

fn wsl_path_cancellable(
    local: Option<&Path>,
    remote: &str,
    context: &CopyContext<'_>,
) -> Result<String> {
    let Some(path) = local else {
        return Ok(remote.to_owned());
    };
    let path = absolute(path)?;
    let output = run_cancellable(
        Command::new("wsl").env("WSL_UTF8", "1").args([
            OsStr::new("--exec"),
            OsStr::new("wslpath"),
            OsStr::new("-a"),
            OsStr::new("-u"),
            path.as_os_str(),
        ]),
        context,
    )?;
    Ok(String::from_utf8(output.stdout)
        .map_err(|_| "wslpath returned non-UTF-8 output")?
        .trim_end()
        .to_owned())
}

fn receive(
    location: &Location,
    scratch: &Scratch,
    pattern: Option<&str>,
    context: &CopyContext<'_>,
) -> Result<Received> {
    if let Some(pattern) = pattern {
        return receive_selected(location, scratch, pattern, context);
    }
    let receipt = if matches!(location.backend, Backend::Ssh { .. }) {
        let output = remote_command_cancellable(
            &location.backend,
            REMOTE_VALIDATE,
            &[&location.path],
            context,
        )?;
        Some(SourceReceipt::parse(&output.stdout)?)
    } else {
        None
    };
    let fallback_name = source_basename(&location.path);
    let name = match &receipt {
        Some(receipt) => receipt.basename.as_str(),
        None => fallback_name,
    };
    validate_staging_basename(name)?;
    let target = scratch.0.join(name);
    let mut basename = Some(OsString::from(name));
    match &location.backend {
        Backend::Http { .. } => {
            if pattern.is_some() {
                return Err("HTTP sources cannot enumerate a regex selection".into());
            }
            run_cancellable(
                http_command_builder(location)?.arg("--output").arg(&target),
                context,
            )?;
        }
        Backend::Ssh { .. } => {
            scp(location, &target, false, context)?;
        }
        Backend::Wsl => {
            let path = wsl_path_cancellable(Some(&target), "", context)?;
            let mut ignored = ExecutionReport::default();
            basename = remote_copy_receipt(
                &Backend::Wsl,
                RemoteSource::filesystem(&location.path),
                &path,
                false,
                None,
                &mut ignored,
                context,
            )?;
        }
        _ => return Err("unsupported source endpoint".into()),
    }
    if let Some(receipt) = receipt {
        receipt.apply_times(&target, context)?;
    }
    Ok(Received {
        path: target,
        basename,
    })
}

fn receive_selected(
    location: &Location,
    scratch: &Scratch,
    pattern: &str,
    context: &CopyContext<'_>,
) -> Result<Received> {
    if !matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }) {
        return Err("this source endpoint cannot enumerate a regex selection".into());
    }
    let regex = Regex::new(&format!("\\A(?:{pattern})\\z")).map_err(|error| error.to_string())?;
    let listing =
        remote_command_cancellable(&location.backend, REMOTE_LIST, &[&location.path], context)?;
    let mut names: Vec<_> = listing
        .stdout
        .split(|byte| *byte == 0)
        .filter_map(|name| std::str::from_utf8(name).ok())
        .filter(|name| !name.is_empty() && regex.is_match(name))
        .collect();
    names.sort_unstable();
    let mut selected_listing = b"\0selection\0".to_vec();
    for name in &names {
        selected_listing.extend_from_slice(name.as_bytes());
        selected_listing.push(0);
        selected_listing.extend_from_slice(name.as_bytes());
        selected_listing.push(0);
    }
    validate_remote_names(&selected_listing)?;
    let target = scratch.0.join("selection");
    fs::create_dir(&target).map_err(|error| at(&target, error))?;
    for name in names {
        context.check()?;
        let child = Location {
            backend: location.backend.clone(),
            path: format!("{}/{name}", location.path.trim_end_matches('/')),
        };
        // Only a selected child enters validation and transport. A separate
        // scratch root keeps every valid basename distinct from our envelope.
        let child_scratch = Scratch::new_in(&scratch.0)?;
        let captured = receive(&child, &child_scratch, None, context)?;
        fs::rename(&captured.path, target.join(name)).map_err(|error| at(&captured.path, error))?;
    }
    Ok(Received {
        path: target,
        basename: None,
    })
}

pub(crate) fn http_command_builder(location: &Location) -> Result<Command> {
    let Backend::Http { base } = &location.backend else {
        return Err("HTTP command requires an HTTP endpoint".into());
    };
    let base = url::Url::parse(base).map_err(|error| error.to_string())?;
    let path = location
        .path
        .strip_prefix("~/")
        .unwrap_or(&location.path)
        .trim_start_matches('/');
    let address = base.join(path).map_err(|error| error.to_string())?;
    if !matches!(address.scheme(), "http" | "https") {
        return Err("HTTP endpoint resolved to a non-HTTP URL".into());
    }
    if address.origin() != base.origin() {
        return Err("HTTP source path cannot change its endpoint origin".into());
    }
    let mut command = Command::new("curl");
    command
        .args([
            "--fail",
            "--globoff",
            "--no-buffer",
            "--location",
            "--silent",
            "--show-error",
            "--proto",
            "=http,https",
            "--proto-redir",
            "=http,https",
        ])
        .arg(address.as_str());
    Ok(command)
}

fn validate_staging_basename(name: &str) -> Result<()> {
    let mut components = Path::new(name).components();
    if !matches!(components.next(), Some(Component::Normal(_))) || components.next().is_some() {
        return Err("source basename cannot be represented safely on the local filesystem".into());
    }
    #[cfg(windows)]
    {
        if name.contains(['/', '\\', ':', '*', '?', '"', '<', '>', '|'])
            || name.ends_with(['.', ' '])
            || name.chars().any(|ch| ch.is_control())
        {
            return Err("source basename cannot be represented literally on Windows".into());
        }
        let stem = name
            .split('.')
            .next()
            .unwrap_or(name)
            .trim_end()
            .to_uppercase();
        let numbered_device = stem
            .strip_prefix("COM")
            .or_else(|| stem.strip_prefix("LPT"))
            .is_some_and(|suffix| {
                matches!(
                    suffix,
                    "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³"
                )
            });
        if matches!(
            stem.as_str(),
            "CON" | "PRN" | "AUX" | "NUL" | "CLOCK$" | "CONIN$" | "CONOUT$"
        ) || numbered_device
        {
            return Err("source basename is a reserved Windows device name".into());
        }
    }
    Ok(())
}

pub(crate) fn validate_remote_names(listing: &[u8]) -> Result<()> {
    let listing =
        std::str::from_utf8(listing).map_err(|_| "remote source contains non-UTF-8 names")?;
    let mut fields = listing.split_terminator('\0');
    #[cfg(windows)]
    let mut seen = std::collections::HashSet::new();
    while let Some(relative) = fields.next() {
        let name = fields.next().ok_or("remote source listing is incomplete")?;
        validate_staging_basename(name)?;
        #[cfg(windows)]
        if !seen.insert(relative.to_uppercase()) {
            return Err("remote source contains case-colliding Windows paths".into());
        }
        #[cfg(not(windows))]
        let _ = relative;
    }
    Ok(())
}

fn send(
    source: &Path,
    location: &Location,
    container: bool,
    pattern: Option<&str>,
    report: &mut ExecutionReport,
    context: &CopyContext<'_>,
) -> Result<()> {
    let source = absolute(source)?;
    let source = source.as_path();
    match &location.backend {
        Backend::Ssh { .. } => {
            validate_source_tree(source, context)?;
            let name = source
                .file_name()
                .and_then(OsStr::to_str)
                .ok_or("source has no UTF-8 basename")?;
            let output = remote_command_cancellable(
                &location.backend,
                "umask 077; mktemp -d /tmp/msync.XXXXXXXXXX",
                &[],
                context,
            )?;
            let remote_root = String::from_utf8(output.stdout)
                .map_err(|_| "remote temporary directory is not UTF-8")?
                .trim_end()
                .to_owned();
            if !remote_root.starts_with("/tmp/msync.") || remote_root.contains('\n') {
                return Err("remote returned an invalid temporary directory".into());
            }
            let staged = Location {
                backend: location.backend.clone(),
                path: format!("{remote_root}/{name}"),
            };
            let result = scp(&staged, source, true, context).and_then(|()| {
                remote_copy_receipt(
                    &location.backend,
                    RemoteSource::uploaded(&staged.path),
                    &location.path,
                    container,
                    pattern,
                    report,
                    context,
                )
                .map(|_| ())
            });
            let cleanup = remote_command(&location.backend, "rm -rf -- \"$1\"", &[&remote_root]);
            result?;
            cleanup?;
            Ok(())
        }
        Backend::Wsl => {
            let path = wsl_path_cancellable(Some(source), "", context)?;
            remote_copy(
                &Backend::Wsl,
                &path,
                &location.path,
                container,
                pattern,
                report,
                context,
            )
        }
        _ => Err("unsupported destination endpoint".into()),
    }
}

fn scp(location: &Location, local: &Path, upload: bool, context: &CopyContext<'_>) -> Result<()> {
    let Backend::Ssh { host, user, port } = &location.backend else {
        return Err("SCP requires SSH endpoint".into());
    };
    let mut command = ssh_client("scp");
    command.args(["-r", "-p"]);
    if let Some(port) = port {
        command.args(["-P", &port.to_string()]);
    }
    // SFTP downloads still apply glob(3), even though no remote shell is used.
    #[cfg(windows)]
    if location.path.contains('\\') {
        return Err("native Windows SCP cannot preserve backslashes in remote paths".into());
    }
    let path = if upload {
        location.path.clone()
    } else {
        escape_sftp_pattern(&location.path)
    };
    let remote = format!("{}:{}", ssh_host(host, user.as_deref()), path);
    command.arg("--");
    if upload {
        let source = absolute(local)?;
        #[cfg(windows)]
        let source = escape_sftp_pattern(&source.to_string_lossy().replace('\\', "/"));
        command.arg(source).arg(remote);
    } else {
        command.arg(remote).arg(absolute(local)?);
    }
    run_cancellable(&mut command, context)?;
    Ok(())
}

fn escape_sftp_pattern(path: &str) -> String {
    // Bracket classes survive Win32 scp's unconditional slash conversion, unlike
    // backslash escapes. They are also literal single-character POSIX globs.
    let mut escaped = String::with_capacity(path.len());
    for character in path.chars() {
        match character {
            '*' => escaped.push_str("[*]"),
            '?' => escaped.push_str("[?]"),
            '[' => escaped.push_str("[[]"),
            ']' => escaped.push_str("[]]"),
            '\\' => escaped.push_str("\\\\"),
            _ => escaped.push(character),
        }
    }
    escaped
}

fn ssh_client(program: &str) -> Command {
    let mut command = Command::new(program);
    if let Some(config) = env::var_os("MSYNC_SSH_CONFIG") {
        command.arg("-F").arg(config);
    }
    command
}

fn remote_copy(
    backend: &Backend,
    source: &str,
    destination: &str,
    container: bool,
    pattern: Option<&str>,
    report: &mut ExecutionReport,
    context: &CopyContext<'_>,
) -> Result<()> {
    remote_copy_receipt(
        backend,
        RemoteSource::filesystem(source),
        destination,
        container,
        pattern,
        report,
        context,
    )
    .map(|_| ())
}

fn remote_copy_receipt(
    backend: &Backend,
    source: RemoteSource<'_>,
    destination: &str,
    container: bool,
    pattern: Option<&str>,
    report: &mut ExecutionReport,
    context: &CopyContext<'_>,
) -> Result<Option<OsString>> {
    context.check()?;
    if let Some(pattern) = pattern {
        if !container {
            return Err("a regex selection requires a destination ending in /".into());
        }
        let regex =
            Regex::new(&format!("\\A(?:{pattern})\\z")).map_err(|error| error.to_string())?;
        let listing = remote_command_cancellable(backend, REMOTE_LIST, &[source.path], context)?;
        let mut names: Vec<&str> = std::str::from_utf8(&listing.stdout)
            .map_err(|_| "remote directory contains non-UTF-8 names")?
            .split('\0')
            .filter(|name| !name.is_empty() && regex.is_match(name))
            .collect();
        names.sort_unstable();
        for name in &names {
            let child = format!("{}/{name}", source.path.trim_end_matches('/'));
            remote_command_cancellable(
                backend,
                REMOTE_COPY,
                &[&child, destination, "1", "1", source.permissions.argument()],
                context,
            )?;
        }
        for name in names {
            let child = format!("{}/{name}", source.path.trim_end_matches('/'));
            remote_copy_receipt(
                backend,
                RemoteSource {
                    path: &child,
                    permissions: source.permissions,
                },
                destination,
                true,
                None,
                report,
                context,
            )?;
        }
        return Ok(None);
    }
    let output = remote_command_cancellable(
        backend,
        REMOTE_COPY,
        &[
            source.path,
            destination,
            if container { "1" } else { "0" },
            "0",
            source.permissions.argument(),
        ],
        context,
    )?;
    let (basename, counts) = remote_copy_output(&output.stdout)?;
    let counts = String::from_utf8_lossy(counts);
    for line in counts.lines() {
        match line {
            _ if line.starts_with("file ") => {
                let bytes = line[5..]
                    .parse::<u64>()
                    .map_err(|_| "remote copy returned an invalid byte count")?;
                report.copied_files += 1;
                report.transferred_bytes = report.transferred_bytes.saturating_add(bytes);
            }
            "directory" => report.created_directories += 1,
            _ => {}
        }
    }
    Ok(Some(basename))
}

fn remote_copy_output(output: &[u8]) -> Result<(OsString, &[u8])> {
    let mut fields = output.splitn(3, |byte| *byte == 0);
    if fields.next() != Some(b"source".as_slice()) {
        return Err("remote copy returned no source identity receipt".into());
    }
    let name = fields.next().ok_or("remote source receipt is incomplete")?;
    let counts = fields.next().ok_or("remote source receipt is incomplete")?;
    let name = std::str::from_utf8(name).map_err(|_| "remote source basename is not UTF-8")?;
    validate_staging_basename(name)?;
    Ok((OsString::from(name), counts))
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

const REMOTE_LIST: &str = r#"
set -eu
s=$1
case $s in '~') s=$HOME;; '~/'*) s=$HOME/${s#\~/};; esac
p=$s
while [ "$p" != / ] && [ "$p" != . ]; do
    [ ! -L "$p" ] || { echo 'refusing source symbolic link' >&2; exit 1; }
    p=$(dirname -- "$p")
done
[ -d "$s" ] || { echo 'a regex source must be a directory' >&2; exit 1; }
find "$s" -mindepth 1 -maxdepth 1 -printf '%f\0'
"#;

const REMOTE_VALIDATE: &str = r#"
set -eu
s=$1
case $s in '~') s=$HOME;; '~/'*) s=$HOME/${s#\~/};; esac
p=$s
while [ "$p" != / ] && [ "$p" != . ]; do
    [ ! -L "$p" ] || { echo 'refusing source symbolic link' >&2; exit 1; }
    p=$(dirname -- "$p")
done
[ -e "$s" ] || { echo 'remote source does not exist' >&2; exit 1; }
s=$(realpath -e -- "$s")
[ -z "$(find "$s" -type l -print -quit)" ] || { echo 'remote source tree contains symbolic links' >&2; exit 1; }
[ -z "$(find "$s" ! -type f ! -type d -print -quit)" ] || { echo 'remote source tree contains special files' >&2; exit 1; }
printf 'MSYNC-SOURCE-1\0'
find "$s" -printf '%P\0%f\0%Ts\0'
"#;

const REMOTE_COPY: &str = r#"
set -eu
fail() { echo "$*" >&2; exit 1; }
expand() { case $1 in '~') printf '%s' "$HOME";; '~/'*) printf '%s/%s' "$HOME" "${1#\~/}";; *) printf '%s' "$1";; esac; }
s=$(expand "$1"); d=$(expand "$2"); mode=$3; permission_policy=${5:-filesystem}
case $permission_policy in filesystem|portable) ;; *) fail 'invalid source permission policy';; esac
creation_mask=$(umask)
case $creation_mask in ''|*[!0-7]*) fail 'unsupported destination umask';; esac
portable_file_mode=$(printf '%o' "$((0666 & ~0$creation_mask))")
case $s in /*) ;; *) s=$PWD/$s;; esac
case $d in /*) ;; *) d=$PWD/$d;; esac
check_parents() {
    p=$1
    while [ "$p" != / ]; do
        [ ! -L "$p" ] || fail "refusing symbolic link: $p"
        p=$(dirname -- "$p")
    done
}
check_parents "$s"; check_parents "$d"
s=$(realpath -m -- "$s"); d=$(realpath -m -- "$d")
[ "$mode" != 0 ] || [ ! -d "$d" ] || fail 'destination is an existing directory; append / to use it as a container'
[ "$mode" = 0 ] || d=$d/$(basename -- "$s")
check_parents "$d"
[ "$s" != "$d" ] || fail 'source and destination are identical'
ancestor_identity() (
    root=$1; p=$2
    while :; do
        if [ "$root" -ef "$p" ]; then return 0; fi
        [ "$p" != / ] || return 1
        p=$(dirname -- "$p")
    done
)
if [ -d "$s" ]; then
    if ancestor_identity "$s" "$d" || ancestor_identity "$d" "$s"; then
        fail 'source and destination directory trees overlap'
    fi
fi
[ -e "$s" ] || fail "source does not exist: $s"
[ -z "$(find "$s" -type l -print -quit)" ] || fail 'source tree contains symbolic links'
if [ -d "$d" ]; then
    [ -z "$(find "$d" -type l -print -quit)" ] || fail 'destination tree contains symbolic links'
fi
check() (
    a=$1; b=$2
    [ ! -e "$b" ] || [ ! "$a" -ef "$b" ] || fail 'source and destination are the same file'
    if [ -d "$a" ]; then
        [ ! -e "$b" ] || [ -d "$b" ] || fail "directory destination is a file: $b"
        for x in "$a"/* "$a"/.[!.]* "$a"/..?*; do
            [ -e "$x" ] || continue
            check "$x" "$b/$(basename -- "$x")"
        done
    elif [ -f "$a" ]; then
        [ ! -d "$b" ] || fail "file destination is a directory: $b"
        [ "$mode" = 0 ] || [ ! -e "$b" ] || fail "destination file already exists: $b"
    else fail "unsupported special file: $a"; fi
)
posix_mode() {
    [ "$permission_policy" = filesystem ] || return 1
    case $(stat -f -c %t -- "$1") in
        ef53|1021994|58465342|9123683e|794c7630) return 0;;
        *) return 1;;
    esac
}
attenuate_readonly() {
    bits=$(stat -c %a -- "$1")
    if [ "$((0$bits & 0222))" = 0 ]; then chmod a-w -- "$2"; fi
}
copy() (
    a=$1; b=$2
    if [ -d "$a" ]; then
        fresh=0
        if [ ! -d "$b" ]; then mkdir -p -- "$b"; fresh=1; echo directory; fi
        for x in "$a"/* "$a"/.[!.]* "$a"/..?*; do
            [ -e "$x" ] || continue
            copy "$x" "$b/$(basename -- "$x")"
        done
        if [ "$fresh" = 1 ]; then
            touch -r "$a" -- "$b"
            if posix_mode "$a"; then chmod --reference="$a" -- "$b"
            else attenuate_readonly "$a" "$b"; fi
        fi
    else
        mkdir -p -- "$(dirname -- "$b")"
        part=$(mktemp "$(dirname -- "$b")/.msync.XXXXXXXXXX")
        trap 'rm -f -- "$part"' EXIT
        trap 'exit 130' HUP INT TERM
        bytes=$(stat -c %s -- "$a")
        if posix_mode "$a"; then cp -p -- "$a" "$part"
        else
            cp --preserve=timestamps -- "$a" "$part"
            chmod "$portable_file_mode" -- "$part"
            attenuate_readonly "$a" "$part"
        fi
        if [ "$mode" = 1 ]; then
            ln -- "$part" "$b" || fail "cannot create destination: $b"
        else mv -fT -- "$part" "$b"; fi
        rm -f -- "$part"
        trap - EXIT HUP INT TERM
        echo "file $bytes"
    fi
)
check "$s" "$d"
[ "${4:-0}" = 0 ] || exit 0
printf 'source\0%s\0' "$(basename -- "$s")"
copy "$s" "$d"
"#;

#[cfg(test)]
mod transport_tests {
    use super::{
        escape_sftp_pattern, remote_copy_output, validate_remote_names, validate_staging_basename,
    };

    fn metadata_receipt(entries: &[(&str, &str, &str)]) -> Vec<u8> {
        let mut bytes = super::SOURCE_RECEIPT.to_vec();
        for entry in entries {
            for field in [entry.0, entry.1, entry.2] {
                bytes.extend_from_slice(field.as_bytes());
                bytes.push(0);
            }
        }
        bytes
    }

    #[test]
    fn source_metadata_receipt_retains_root_name_paths_and_whole_second_times() {
        let receipt = super::SourceReceipt::parse(&metadata_receipt(&[
            ("", "source 可莉", "0"),
            ("nested", "nested", "1"),
            ("nested/file", "file", "-1"),
        ]))
        .unwrap();
        assert_eq!(receipt.basename, "source 可莉");
        assert_eq!(
            receipt.times[0],
            (std::path::PathBuf::new(), std::time::UNIX_EPOCH)
        );
        assert_eq!(
            receipt.times[1].1,
            std::time::UNIX_EPOCH + std::time::Duration::from_secs(1)
        );
        assert_eq!(
            receipt.times[2].1,
            std::time::UNIX_EPOCH - std::time::Duration::from_secs(1)
        );
        assert_eq!(
            receipt.times[2].0,
            std::path::Path::new("nested").join("file")
        );
    }

    #[test]
    fn source_metadata_receipt_rejects_escapes_duplicates_and_ambiguous_paths() {
        for path in [
            "/outside",
            "../outside",
            "nested/../outside",
            ".",
            "nested//file",
            "nested/./file",
            "nested/file/",
        ] {
            let bytes = metadata_receipt(&[("", "source", "0"), (path, "file", "0")]);
            assert!(super::SourceReceipt::parse(&bytes).is_err(), "{path:?}");
        }
        for entries in [
            vec![("file", "file", "0")],
            vec![("", "source", "0"), ("", "source", "0")],
            vec![
                ("", "source", "0"),
                ("file", "file", "0"),
                ("file", "file", "1"),
            ],
            vec![("", "source", "0"), ("file", "other", "0")],
        ] {
            assert!(super::SourceReceipt::parse(&metadata_receipt(&entries)).is_err());
        }
    }

    #[test]
    fn source_metadata_receipt_rejects_bad_versions_framing_and_time_bounds() {
        for time in [
            "",
            "nan",
            "1.5",
            "9223372036854775808",
            "-9223372036854775809",
            "18446744073709551616",
        ] {
            assert!(
                super::SourceReceipt::parse(&metadata_receipt(&[("", "source", time)])).is_err(),
                "{time:?}"
            );
        }
        let valid = metadata_receipt(&[("", "source", "0")]);
        assert!(super::SourceReceipt::parse(&valid[1..]).is_err());
        assert!(super::SourceReceipt::parse(&valid[..valid.len() - 1]).is_err());
        assert!(super::SourceReceipt::parse(super::SOURCE_RECEIPT).is_err());
        let mut invalid_utf8 = valid;
        invalid_utf8[super::SOURCE_RECEIPT.len() + 1] = 0xff;
        assert!(super::SourceReceipt::parse(&invalid_utf8).is_err());
    }

    #[cfg(windows)]
    #[test]
    fn source_metadata_receipt_rejects_windows_aliases_and_case_collisions() {
        for path in [r"C:\outside", r"\\server\outside", "file:stream", "NUL"] {
            assert!(
                super::SourceReceipt::parse(&metadata_receipt(&[
                    ("", "source", "0"),
                    (path, path, "0")
                ]))
                .is_err()
            );
        }
        assert!(
            super::SourceReceipt::parse(&metadata_receipt(&[
                ("", "source", "0"),
                ("Name", "Name", "0"),
                ("name", "name", "0")
            ]))
            .is_err()
        );
    }

    #[test]
    fn source_metadata_repairs_a_readonly_root_file_without_changing_bytes_or_flags() {
        let root = inspection_workspace();
        let file = root.path().join("private-file");
        std::fs::write(&file, b"captured readonly body").unwrap();
        let original = std::fs::metadata(&file).unwrap().permissions();
        let mut readonly = original.clone();
        readonly.set_readonly(true);
        std::fs::set_permissions(&file, readonly).unwrap();
        let receipt =
            super::SourceReceipt::parse(&metadata_receipt(&[("", "original-name", "978307207")]))
                .unwrap();
        let cancel = tokio_util::sync::CancellationToken::new();
        receipt
            .apply_times(&file, &super::CopyContext::new(4096, &cancel))
            .unwrap();
        let metadata = std::fs::metadata(&file).unwrap();
        assert_eq!(
            metadata.modified().unwrap(),
            std::time::UNIX_EPOCH + std::time::Duration::from_secs(978_307_207)
        );
        assert!(metadata.permissions().readonly());
        assert_eq!(std::fs::read(&file).unwrap(), b"captured readonly body");
        std::fs::set_permissions(&file, original).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn source_metadata_never_follows_a_captured_symlink_to_an_outside_entry() {
        let root = inspection_workspace();
        let outside = root.path().join("outside");
        let captured = root.path().join("captured");
        std::fs::create_dir(&outside).unwrap();
        std::fs::create_dir(&captured).unwrap();
        let file = outside.join("file");
        std::fs::write(&file, b"must remain untouched").unwrap();
        let before = std::fs::metadata(&file).unwrap().modified().unwrap();
        std::os::unix::fs::symlink(&outside, captured.join("link")).unwrap();
        let receipt = super::SourceReceipt::parse(&metadata_receipt(&[
            ("", "source", "0"),
            ("link/file", "file", "0"),
        ]))
        .unwrap();
        let cancel = tokio_util::sync::CancellationToken::new();
        assert!(
            receipt
                .apply_times(&captured, &super::CopyContext::new(4096, &cancel))
                .is_err()
        );
        assert_eq!(
            std::fs::metadata(&file).unwrap().modified().unwrap(),
            before
        );
        assert_eq!(std::fs::read(file).unwrap(), b"must remain untouched");
    }

    #[test]
    fn remote_copy_receipt_retains_acquired_name_separately_from_counts() {
        let (name, counts) =
            remote_copy_output("source\0root ' 可莉\0directory\nfile 7\n".as_bytes()).unwrap();
        assert_eq!(name, std::ffi::OsStr::new("root ' 可莉"));
        assert_eq!(counts, b"directory\nfile 7\n");
    }

    #[test]
    fn uploaded_source_permissions_retain_origin_provenance_not_staging_filesystem() {
        let uploaded = super::RemoteSource::uploaded("/tmp/native-ext4-staging/payload");
        assert_eq!(uploaded.path, "/tmp/native-ext4-staging/payload");
        assert_eq!(
            uploaded.permissions.argument(),
            if cfg!(windows) {
                "portable"
            } else {
                "filesystem"
            },
        );
        let source = super::RemoteSource::filesystem("~/source/../actual");
        assert_eq!(source.path, "~/source/../actual");
        assert_eq!(source.permissions.argument(), "filesystem");
    }

    #[test]
    fn malformed_remote_copy_receipts_cannot_supply_a_private_or_escaping_name() {
        for output in [
            b"file 7\n".as_slice(),
            b"source\0root".as_slice(),
            b"source\0\0file 7\n".as_slice(),
            b"source\0../root\0file 7\n".as_slice(),
            b"source\0\xff\0file 7\n".as_slice(),
        ] {
            assert!(remote_copy_output(output).is_err(), "{output:?}");
        }
    }

    #[test]
    fn finite_import_scratch_and_recursive_children_stay_in_the_registered_owner() {
        let root = inspection_workspace();
        let invocation = crate::filesystem::InvocationRoot::create(root.path()).unwrap();
        let mut store = crate::filesystem::SnapshotStore::new_in(
            &invocation.path().join("assets"),
            &invocation,
        )
        .unwrap();
        std::fs::write(root.path().join("user-file"), b"user").unwrap();
        let acquisition = super::Scratch::new_in(invocation.path()).unwrap();
        let child = super::Scratch::new_in(&acquisition.0).unwrap();
        std::fs::write(child.0.join("private-body"), b"private").unwrap();
        assert!(child.0.starts_with(invocation.path()));
        let snapshot = store.capture_in(root.path(), &invocation).unwrap();
        assert!(
            snapshot
                .entries()
                .contains_key(std::path::Path::new("user-file"))
        );
        assert_eq!(snapshot.entries().len(), 2);
        let child_path = child.0.clone();
        drop(child);
        assert!(!child_path.exists());
        let acquisition_path = acquisition.0.clone();
        drop(acquisition);
        assert!(!acquisition_path.exists());
        drop(snapshot);
        store.cleanup().unwrap();
        drop(store);
        invocation.cleanup().unwrap();
    }

    #[test]
    fn private_import_adoption_preserves_file_tree_metadata_and_identity() {
        for directory in [false, true] {
            let root = inspection_workspace();
            let source = root.path().join("received");
            let target = root.path().join("owned-target");
            let file = if directory {
                std::fs::create_dir_all(source.join("nested/empty")).unwrap();
                source.join("nested/body")
            } else {
                source.clone()
            };
            std::fs::write(&file, b"immutable body").unwrap();
            let modified = std::time::UNIX_EPOCH + std::time::Duration::from_secs(978_307_206);
            let mut expected =
                crate::filesystem::NodeMetadata::capture(&std::fs::metadata(&source).unwrap())
                    .unwrap();
            expected.modified = modified;
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt;
                expected
                    .permissions
                    .set_mode(if directory { 0o550 } else { 0o440 });
            }
            #[cfg(windows)]
            expected.permissions.set_readonly(true);
            expected.apply(&source).unwrap();
            let identity = same_file::Handle::from_path(&source).unwrap();
            let cancel = tokio_util::sync::CancellationToken::new();
            let receipt = super::adopt_received(
                super::Received {
                    path: source.clone(),
                    basename: Some("actual-source-name".into()),
                },
                &target,
                &super::CopyContext::new(4096, &cancel),
            )
            .unwrap();
            assert!(!source.exists());
            assert_eq!(same_file::Handle::from_path(&target).unwrap(), identity);
            assert_eq!(
                receipt.basename.as_deref(),
                Some(std::ffi::OsStr::new("actual-source-name"))
            );
            let actual = std::fs::metadata(&target).unwrap();
            assert_eq!(actual.modified().unwrap(), modified);
            assert_eq!(
                actual.permissions().readonly(),
                expected.permissions.readonly()
            );
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt;
                assert_eq!(
                    actual.permissions().mode() & 0o777,
                    expected.permissions.mode() & 0o777
                );
            }
            let target_file = if directory {
                target.join("nested/body")
            } else {
                target.clone()
            };
            assert_eq!(std::fs::read(target_file).unwrap(), b"immutable body");
            let original = std::fs::metadata(root.path()).unwrap().permissions();
            std::fs::set_permissions(&target, original).unwrap();
        }
    }

    #[test]
    fn private_import_adoption_never_clobbers_an_existing_target_or_moves_after_cancel() {
        let root = inspection_workspace();
        let source = root.path().join("received");
        let target = root.path().join("occupied");
        std::fs::write(&source, b"received").unwrap();
        std::fs::write(&target, b"keep target").unwrap();
        let cancel = tokio_util::sync::CancellationToken::new();
        assert!(
            super::adopt_received(
                super::Received {
                    path: source.clone(),
                    basename: None
                },
                &target,
                &super::CopyContext::new(4096, &cancel),
            )
            .is_err()
        );
        assert_eq!(std::fs::read(&target).unwrap(), b"keep target");
        assert_eq!(std::fs::read(&source).unwrap(), b"received");
        let absent = root.path().join("absent");
        cancel.cancel();
        assert!(
            super::adopt_received(
                super::Received {
                    path: source.clone(),
                    basename: None
                },
                &absent,
                &super::CopyContext::new(4096, &cancel),
            )
            .is_err()
        );
        assert!(!absent.exists());
        assert_eq!(std::fs::read(&source).unwrap(), b"received");
    }

    #[test]
    fn private_sparse_import_adopts_children_without_publishing_its_envelope_name() {
        let root = inspection_workspace();
        let source = root.path().join("selection");
        std::fs::create_dir(&source).unwrap();
        std::fs::write(source.join("picked"), b"selected").unwrap();
        let target = root.path().join("owned-target");
        let cancel = tokio_util::sync::CancellationToken::new();
        let receipt = super::adopt_received(
            super::Received {
                path: source,
                basename: None,
            },
            &target,
            &super::CopyContext::new(4096, &cancel),
        )
        .unwrap();
        assert!(receipt.basename.is_none());
        assert_eq!(std::fs::read_dir(&target).unwrap().count(), 1);
        assert_eq!(std::fs::read(target.join("picked")).unwrap(), b"selected");
    }

    fn inspection_workspace() -> tempfile::TempDir {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        std::fs::create_dir_all(&root).unwrap();
        tempfile::tempdir_in(root).unwrap()
    }

    fn inspection_local(path: &std::path::Path) -> crate::compiler::Location {
        crate::compiler::Location {
            backend: crate::compiler::Backend::Local,
            path: path.to_string_lossy().into_owned(),
        }
    }

    #[test]
    fn inspection_shares_verified_ancestors_but_reprobes_each_leaf() {
        let root = inspection_workspace();
        let directory = root.path().join("nested");
        std::fs::create_dir(&directory).unwrap();
        let first = directory.join("first");
        let second = directory.join("second");
        std::fs::write(&first, "first").unwrap();
        std::fs::write(&second, "second").unwrap();
        let mut inspection = super::Inspection::default();
        assert_eq!(
            inspection.inspect(&inspection_local(&first), true).unwrap(),
            super::FileClass::Regular
        );
        let probes = inspection.ancestor_probes;
        assert!(probes > 0);
        assert_eq!(
            inspection
                .inspect(&inspection_local(&second), true)
                .unwrap(),
            super::FileClass::Regular
        );
        assert_eq!(inspection.ancestor_probes, probes);
        assert_eq!(inspection.ancestor_hits, 1);
        assert_eq!(inspection.leaf_probes, 2);
        std::fs::remove_file(&second).unwrap();
        assert_eq!(
            inspection
                .inspect(&inspection_local(&second), true)
                .unwrap(),
            super::FileClass::Missing
        );
        assert_eq!(inspection.leaf_probes, 3);
        assert_eq!(inspection.ancestor_probes, probes);
    }

    #[test]
    fn inspection_keeps_parent_file_errors_and_never_opens_stream_endpoints() {
        let root = inspection_workspace();
        let parent = root.path().join("not-a-directory");
        std::fs::write(&parent, "ordinary file").unwrap();
        let mut inspection = super::Inspection::default();
        for child in ["first", "second"] {
            let location = inspection_local(&parent.join(child));
            assert_eq!(
                inspection.inspect(&location, true),
                super::inspect_location(&location, true)
            );
        }
        let before = inspection.leaf_probes;
        let endpoint = crate::compiler::Location {
            backend: crate::compiler::Backend::Tcp {
                host: "127.0.0.1".into(),
                port: 1,
            },
            path: "/".into(),
        };
        assert_eq!(
            inspection.inspect(&endpoint, true).unwrap(),
            super::FileClass::Stream
        );
        assert_eq!(inspection.leaf_probes, before);
    }

    #[cfg(windows)]
    #[test]
    fn inspection_guards_preserve_verbatim_parent_steps_and_trim_directory_markers() {
        let path = std::path::Path::new(r"\\?\C:\parent\link\..\file");
        let guards: Vec<_> = super::ancestor_guards(path).collect();
        assert_eq!(guards[0], std::path::Path::new(r"\\?\C:\parent\link\.."));
        assert_eq!(guards[1], std::path::Path::new(r"\\?\C:\parent\link"));
        for path in [
            r"C:\parent\link\",
            r"C:\parent\link\.",
            r"\\?\C:\parent\link\",
        ] {
            let path = std::path::Path::new(path);
            assert_eq!(
                super::ancestor_guards(path)
                    .next()
                    .unwrap()
                    .file_name()
                    .unwrap(),
                "link"
            );
        }
    }

    #[cfg(unix)]
    #[test]
    fn inspection_failed_symlink_chain_does_not_cache_inner_prefixes() {
        let root = inspection_workspace();
        let target = root.path().join("target");
        std::fs::create_dir_all(target.join("nested")).unwrap();
        for name in ["first", "second"] {
            std::fs::write(target.join("nested").join(name), name).unwrap();
        }
        let link = root.path().join("link");
        std::os::unix::fs::symlink(&target, &link).unwrap();
        let mut inspection = super::Inspection::default();
        for name in ["first", "second"] {
            let location = inspection_local(&link.join("nested").join(name));
            let failure = inspection.inspect(&location, true).unwrap_err();
            assert!(failure.contains("refusing symbolic link"), "{failure}");
            assert!(inspection.ancestors.is_empty());
        }
    }

    #[cfg(unix)]
    #[test]
    fn inspection_and_execution_reject_directory_links_with_ignored_suffixes() {
        use crate::compiler::{CopyOp, Program};
        let root = inspection_workspace();
        let target = root.path().join("target");
        std::fs::create_dir(&target).unwrap();
        std::fs::write(target.join("child"), "protected").unwrap();
        let link = root.path().join("link");
        std::os::unix::fs::symlink(&target, &link).unwrap();
        for suffix in ["/", "/.", "/./"] {
            let path = std::path::PathBuf::from(format!("{}{suffix}", link.display()));
            let mut inspection = super::Inspection::default();
            assert!(
                inspection
                    .inspect(&inspection_local(&path), true)
                    .unwrap_err()
                    .contains("refusing symbolic link")
            );
            assert!(inspection.ancestors.is_empty());
            assert!(
                super::check_ancestors(&path)
                    .unwrap_err()
                    .contains("refusing symbolic link")
            );
            let output = root.path().join("output");
            let result = super::execute(
                &Program {
                    operations: vec![CopyOp {
                        source: inspection_local(&path),
                        destination: inspection_local(&output),
                        pattern: None,
                        span: crate::syntax::Span::default(),
                    }],
                },
                &super::ExecutionOptions::default(),
            );
            assert!(
                result
                    .unwrap_err()
                    .message
                    .contains("refusing symbolic link")
            );
            assert!(!output.exists());
            assert_eq!(
                std::fs::read_to_string(target.join("child")).unwrap(),
                "protected"
            );
        }
    }

    #[cfg(unix)]
    #[test]
    fn inspection_does_not_normalize_away_symlinks_or_outlive_preparation() {
        let root = inspection_workspace();
        let target = root.path().join("target");
        std::fs::create_dir(&target).unwrap();
        let safe = root.path().join("safe");
        std::fs::write(&safe, "safe").unwrap();
        let link = root.path().join("link");
        std::os::unix::fs::symlink(&target, &link).unwrap();
        let mut inspection = super::Inspection::default();
        inspection.inspect(&inspection_local(&safe), true).unwrap();
        let failure = inspection
            .inspect(&inspection_local(&link.join("..").join("safe")), true)
            .unwrap_err();
        assert!(failure.contains("refusing symbolic link"), "{failure}");

        let parent = root.path().join("parent");
        std::fs::create_dir(&parent).unwrap();
        std::fs::write(parent.join("child"), "before").unwrap();
        inspection
            .inspect(&inspection_local(&parent.join("child")), true)
            .unwrap();
        std::fs::remove_file(parent.join("child")).unwrap();
        std::fs::remove_dir(&parent).unwrap();
        std::fs::write(target.join("child"), "after").unwrap();
        std::os::unix::fs::symlink(&target, &parent).unwrap();
        assert!(
            super::inspect_location(&inspection_local(&parent.join("child")), true)
                .unwrap_err()
                .contains("refusing symbolic link")
        );
        assert!(
            super::check_ancestors(&parent.join("child"))
                .unwrap_err()
                .contains("refusing symbolic link")
        );
    }

    #[test]
    fn sftp_downloads_escape_globs_without_changing_home_expansion() {
        assert_eq!(escape_sftp_pattern("~/a[1].txt"), "~/a[[]1[]].txt");
        assert_eq!(
            escape_sftp_pattern(r"dir/a*?[]\file"),
            r"dir/a[*][?][[][]]\\file"
        );
        assert_eq!(escape_sftp_pattern("~/plain 文件.txt"), "~/plain 文件.txt");
    }

    #[test]
    fn metadata_batch_frames_preserve_per_item_errors_and_order() {
        use super::{FileClass, parse_remote_inspections};
        let rows = parse_remote_inspections(
            b"ok\0regular\0error\0unsafe path\0ok\0missing\0ok\0regular\0",
            4,
        )
        .unwrap();
        assert!(matches!(rows[0], Ok(FileClass::Regular)));
        assert_eq!(rows[1].as_ref().unwrap_err(), "unsafe path");
        assert!(matches!(rows[2], Ok(FileClass::Missing)));
        assert!(matches!(rows[3], Ok(FileClass::Regular)));
        for output in [
            b"ok\0regular".as_slice(),
            b"ok\0regular\0".as_slice(),
            b"ok\0bogus\0ok\0regular\0".as_slice(),
        ] {
            assert!(parse_remote_inspections(output, 2).is_err());
        }
    }

    #[test]
    fn cancelled_native_copy_does_not_create_or_modify_a_target() {
        use super::execute_copy_cancellable;
        use crate::{
            compiler::{Backend, CopyOp, Location},
            syntax::Span,
        };
        use std::{fs, path::Path};
        use tokio_util::sync::CancellationToken;
        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&temporary).unwrap();
        let directory = tempfile::tempdir_in(temporary).unwrap();
        let source = directory.path().join("source");
        let target = directory.path().join("target");
        fs::write(&source, "new payload").unwrap();
        for existing in [false, true] {
            if existing {
                fs::write(&target, "previous payload").unwrap();
            }
            let operation = CopyOp {
                source: Location {
                    backend: Backend::Local,
                    path: source.to_str().unwrap().into(),
                },
                destination: Location {
                    backend: Backend::Local,
                    path: target.to_str().unwrap().into(),
                },
                pattern: None,
                span: Span { start: 0, end: 1 },
            };
            let cancel = CancellationToken::new();
            cancel.cancel();
            assert!(
                execute_copy_cancellable(&operation, 64 * 1024, cancel)
                    .unwrap_err()
                    .message
                    .contains("cancelled")
            );
            if existing {
                assert_eq!(fs::read_to_string(&target).unwrap(), "previous payload");
            } else {
                assert!(!target.exists());
            }
        }
        assert_eq!(fs::read_dir(directory.path()).unwrap().count(), 2);
    }

    #[test]
    fn staging_accepts_only_one_native_filename_component() {
        for name in ["", ".", "..", "/root", "folder/file"] {
            assert!(validate_staging_basename(name).is_err(), "{name:?}");
        }
        for name in ["a[1]{x} file.html", "配置.toml"] {
            assert!(validate_staging_basename(name).is_ok(), "{name:?}");
        }
        #[cfg(windows)]
        for name in [
            r"D:\victim.txt",
            r"D:victim.txt",
            r"..\victim",
            r"\victim",
            "victim:stream",
        ] {
            assert!(validate_staging_basename(name).is_err(), "{name:?}");
        }
    }

    #[test]
    fn remote_tree_names_are_validated_before_recursive_download() {
        assert!(validate_remote_names(b"\0tree\0nested\0nested\0nested/a.txt\0a.txt\0").is_ok());
        assert!(validate_remote_names(b"\0tree\0missing-name\0").is_err());
        #[cfg(windows)]
        for listing in [
            b"\0tree\0escape\0..\\..\\victim\0".as_slice(),
            b"\0tree\0a\0a\0A\0A\0".as_slice(),
            b"\0tree\0CON\0CON\0".as_slice(),
            b"\0tree\0a.\0a.\0".as_slice(),
        ] {
            assert!(validate_remote_names(listing).is_err());
        }
    }

    #[cfg(windows)]
    #[test]
    fn directory_overlap_uses_identity_across_windows_namespace_prefixes() {
        use super::preflight;
        use std::{fs, path::Path};
        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&temporary).unwrap();
        let directory = tempfile::tempdir_in(temporary).unwrap();
        let source = directory.path().join("source");
        fs::create_dir(&source).unwrap();
        fs::write(source.join("identity.txt"), "unchanged").unwrap();
        let verbatim_source = source.canonicalize().unwrap();
        let target = verbatim_source.join("not-created");
        assert!(preflight(&source, &target, false).is_err());
        assert!(!target.exists());
        assert!(preflight(&verbatim_source, directory.path(), false).is_err());
        assert_eq!(
            fs::read_to_string(source.join("identity.txt")).unwrap(),
            "unchanged"
        );
    }

    #[test]
    fn metadata_classification_never_needs_file_data() {
        use super::{FileClass, inspect_location};
        use crate::compiler::{Backend, Location};
        use std::{fs, path::Path};
        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&temporary).unwrap();
        let directory = tempfile::tempdir_in(temporary).unwrap();
        let file = directory.path().join("bytes");
        fs::write(&file, "bytes").unwrap();
        let local = |path: &Path| Location {
            backend: Backend::Local,
            path: path.to_str().unwrap().into(),
        };
        assert!(matches!(
            inspect_location(&local(&file), true).unwrap(),
            FileClass::Regular
        ));
        assert!(matches!(
            inspect_location(&local(directory.path()), false).unwrap(),
            FileClass::Directory
        ));
        assert!(matches!(
            inspect_location(&local(&directory.path().join("missing")), true).unwrap(),
            FileClass::Missing
        ));
        let tcp = Location {
            backend: Backend::Tcp {
                host: "127.0.0.1".into(),
                port: 9,
            },
            path: String::new(),
        };
        assert!(matches!(
            inspect_location(&tcp, true).unwrap(),
            FileClass::Stream
        ));
        #[cfg(windows)]
        for path in [r"\\.\pipe\not-listening", "//./pipe/not-listening"] {
            let pipe = Location {
                backend: Backend::Local,
                path: path.into(),
            };
            assert!(matches!(
                inspect_location(&pipe, true).unwrap(),
                FileClass::Stream
            ));
        }
    }

    #[test]
    fn independent_copies_can_race_to_create_a_shared_parent() {
        use super::{ExecutionReport, ensure_directory};
        use std::{
            fs,
            path::Path,
            sync::{Arc, Barrier},
            thread,
        };
        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&temporary).unwrap();
        let directory = tempfile::tempdir_in(temporary).unwrap();
        let target = directory.path().join("shared/parent");
        let barrier = Arc::new(Barrier::new(8));
        let tasks: Vec<_> = (0..8)
            .map(|_| {
                let target = target.clone();
                let barrier = Arc::clone(&barrier);
                thread::spawn(move || {
                    barrier.wait();
                    let mut report = ExecutionReport::default();
                    ensure_directory(&target, &mut report).unwrap();
                    report.created_directories
                })
            })
            .collect();
        assert_eq!(
            tasks
                .into_iter()
                .map(|task| task.join().unwrap())
                .sum::<usize>(),
            2
        );
        assert!(target.is_dir());
    }

    #[cfg(windows)]
    #[test]
    #[ignore = "requires an installed WSL distribution; run explicitly with --ignored"]
    fn live_remote_metadata_probe_never_changes_regular_resources() {
        use super::{FileClass, inspect_location_live, wsl_path};
        use crate::compiler::{Backend, Location};
        use std::{fs, path::Path};
        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&temporary).unwrap();
        let directory = tempfile::tempdir_in(temporary).unwrap();
        let source = directory.path().join("source.bin");
        fs::write(&source, "unchanged").unwrap();
        let remote = |path: &Path| Location {
            backend: Backend::Wsl,
            path: wsl_path(Some(path), "").unwrap(),
        };
        assert!(matches!(
            inspect_location_live(&remote(&source), true).unwrap(),
            FileClass::Regular
        ));
        assert!(matches!(
            inspect_location_live(&remote(directory.path()), false).unwrap(),
            FileClass::Directory
        ));
        assert!(matches!(
            inspect_location_live(&remote(&directory.path().join("missing")), true).unwrap(),
            FileClass::Missing
        ));
        assert_eq!(fs::read_to_string(source).unwrap(), "unchanged");
        assert_eq!(fs::read_dir(directory.path()).unwrap().count(), 1);
        let remote_file = remote(&directory.path().join("source.bin"));
        let paths = vec![
            remote_file.path.clone(),
            "/dev/null".into(),
            remote(directory.path()).path,
            remote(&directory.path().join("missing")).path,
            remote_file.path.clone(),
        ];
        let batch = super::inspect_locations_live(&Backend::Wsl, &paths).unwrap();
        assert!(matches!(batch[0], Ok(FileClass::Regular)));
        assert!(batch[1].is_err());
        assert!(matches!(batch[2], Ok(FileClass::Directory)));
        assert!(matches!(batch[3], Ok(FileClass::Missing)));
        assert!(matches!(batch[4], Ok(FileClass::Regular)));
        let repeated = vec![remote_file.path; 160];
        assert!(
            super::inspect_locations_live(&Backend::Wsl, &repeated)
                .unwrap()
                .into_iter()
                .all(|row| matches!(row, Ok(FileClass::Regular)))
        );
    }

    #[cfg(windows)]
    #[test]
    #[ignore = "measures actual WSL client startup for 16 real metadata paths"]
    fn live_remote_metadata_batch_amortizes_native_client_startup() {
        use super::{FileClass, inspect_locations_live, wsl_path};
        use crate::compiler::Backend;
        use std::{fs, path::Path, time::Instant};
        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&temporary).unwrap();
        let directory = tempfile::tempdir_in(temporary).unwrap();
        let root = wsl_path(Some(directory.path()), "").unwrap();
        let paths: Vec<String> = (0..16)
            .map(|index| {
                fs::write(
                    directory.path().join(format!("source-{index}.bin")),
                    [index],
                )
                .unwrap();
                format!("{root}/source-{index}.bin")
            })
            .collect();
        inspect_locations_live(&Backend::Wsl, &paths[..1]).unwrap();
        let started = Instant::now();
        let serial: Vec<_> = paths
            .iter()
            .map(|path| {
                inspect_locations_live(&Backend::Wsl, std::slice::from_ref(path))
                    .unwrap()
                    .pop()
                    .unwrap()
            })
            .collect();
        let serial_time = started.elapsed();
        let started = Instant::now();
        let batched = inspect_locations_live(&Backend::Wsl, &paths).unwrap();
        let batch_time = started.elapsed();
        assert!(
            serial
                .iter()
                .chain(batched.iter())
                .all(|row| matches!(row, Ok(FileClass::Regular)))
        );
        eprintln!(
            "WSL metadata 16 real paths: 16 client processes {:?}, batched one process {:?}",
            serial_time, batch_time
        );
    }
}

#[cfg(all(test, windows))]
mod native_path_tests {
    use super::*;
    use std::os::windows::ffi::{OsStrExt, OsStringExt};

    #[test]
    fn verbatim_paths_keep_their_prefix_and_raw_traversal_obligations() {
        let location = Location {
            backend: Backend::Local,
            path: r"\\?\D:\root/step/../future-file".into(),
        };
        let native = local_path(&location, false).unwrap().unwrap();
        assert_eq!(
            native.as_os_str(),
            OsStr::new(r"\\?\D:\root\step\..\future-file")
        );
    }

    #[test]
    fn native_separator_conversion_preserves_non_unicode_windows_units() {
        let units = [
            u16::from(b'D'),
            u16::from(b':'),
            u16::from(b'/'),
            0xd800,
            u16::from(b'/'),
            u16::from(b'x'),
        ];
        let original = PathBuf::from(std::ffi::OsString::from_wide(&units));
        let native = native_path(original);
        let actual: Vec<u16> = native.as_os_str().encode_wide().collect();
        assert_eq!(
            actual,
            [
                u16::from(b'D'),
                u16::from(b':'),
                u16::from(b'\\'),
                0xd800,
                u16::from(b'\\'),
                u16::from(b'x')
            ]
        );
    }
}
