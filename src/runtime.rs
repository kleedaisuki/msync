//! Execution of compiled copy plans. Local trees are preflighted before mutation.
use crate::compiler::{Backend, CopyOp, Location, Program};
use crate::plan::FileClass;
use crate::syntax::Span;
use regex::Regex;
use std::env;
use std::ffi::OsStr;
use std::fmt;
use std::fs::{self, OpenOptions};
use std::io;
use std::path::{Component, Path, PathBuf};
use std::process::{Command, Output};
use std::sync::atomic::{AtomicU64, Ordering};

#[derive(Clone, Debug, Default)]
pub struct ExecutionOptions {
    pub dry_run: bool,
}

#[derive(Clone, Debug, Default)]
pub struct ExecutionReport {
    pub copied_files: usize,
    pub created_directories: usize,
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

pub(crate) fn execute_copy(op: &CopyOp) -> std::result::Result<ExecutionReport, RuntimeError> {
    let mut trace = crate::telemetry::span("copy");
    let mut report = ExecutionReport::default();
    let result = execute_operation(op, &mut report).map_err(|message| RuntimeError {
        message,
        span: op.span,
    });
    trace.finish_result(&result);
    trace.metric("files", report.copied_files as u64);
    trace.metric("directories", report.created_directories as u64);
    result.map(|()| report)
}

/// Classifies local metadata without opening a stream or contacting a remote endpoint.
pub(crate) fn inspect_location(location: &Location, source: bool) -> Result<FileClass> {
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
    check_ancestors(&path)?;
    match metadata(&path)? {
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
pub(crate) fn inspect_location_live(location: &Location, source: bool) -> Result<FileClass> {
    if !matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }) {
        return inspect_location(location, source);
    }
    let output = remote_command(&location.backend, REMOTE_INSPECT, &[&location.path])?;
    match output.stdout.as_slice() {
        b"missing\0" => Ok(FileClass::Missing),
        b"regular\0" => Ok(FileClass::Regular),
        b"directory\0" => Ok(FileClass::Directory),
        b"stream\0" => Ok(FileClass::Stream),
        _ => Err("remote metadata probe returned an invalid class".into()),
    }
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

fn execute_operation(op: &CopyOp, report: &mut ExecutionReport) -> Result<()> {
    if matches!(op.destination.backend, Backend::Http { .. } | Backend::Path) {
        return Err("HTTP and PATH endpoints are read-only".into());
    }
    report.actions.push(describe_operation(op));
    let source = local_path(&op.source, true)?;
    let destination = local_path(&op.destination, false)?;
    if let Some(destination) = &destination {
        validate_destination(destination, op.destination.path.ends_with('/'))?;
    }
    match (source, destination) {
        (Some(source), Some(destination)) => copy_selected(
            &source,
            &destination,
            op.destination.path.ends_with('/'),
            op.pattern.as_deref(),
            report,
        ),
        (source, destination) => {
            transport_copy(op, source.as_deref(), destination.as_deref(), report)
        }
    }
}

pub(crate) fn local_path(location: &Location, source: bool) -> Result<Option<PathBuf>> {
    match &location.backend {
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
                let candidate = directory.join(name);
                if candidate.is_file() {
                    return Ok(Some(candidate));
                }
            }
            Err(format!("file {:?} was not found in PATH", location.path))
        }
        _ => Ok(None),
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
) -> Result<()> {
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
        let target = if container {
            let name = source
                .file_name()
                .ok_or("cannot preserve the name of a filesystem root")?;
            destination.join(name)
        } else {
            destination.to_owned()
        };
        preflight(&source, &target, container)?;
        plans.push((source, target));
    }
    for (source, target) in plans {
        copy_tree(&source, &target, container, report)?;
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
    for ancestor in raw.ancestors() {
        metadata(ancestor)?;
    }
    Ok(())
}

fn preflight(source: &Path, target: &Path, no_clobber: bool) -> Result<()> {
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
            preflight(&entry.path(), &target.join(entry.file_name()), no_clobber)?;
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

fn validate_source_tree(source: &Path) -> Result<()> {
    check_ancestors(source)?;
    let meta =
        metadata(source)?.ok_or_else(|| format!("source does not exist: {}", source.display()))?;
    if meta.is_dir() {
        for entry in fs::read_dir(source).map_err(|error| at(source, error))? {
            validate_source_tree(&entry.map_err(|error| at(source, error))?.path())?;
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
) -> Result<()> {
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
            )?;
        }
        return Ok(());
    }
    if let Some(parent) = target.parent() {
        ensure_directory(parent, report)?;
    }
    // create_new is the atomic no-clobber contract; never emulate it with exists().
    if no_clobber {
        let mut input = fs::File::open(source).map_err(|error| at(source, error))?;
        let mut output = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(target)
            .map_err(|error| at(target, error))?;
        if let Err(error) = io::copy(&mut input, &mut output) {
            drop(output);
            let _ = fs::remove_file(target);
            return Err(at(target, error));
        }
        fs::set_permissions(target, source_meta.permissions())
            .map_err(|error| at(target, error))?;
    } else {
        metadata(target)?;
        fs::copy(source, target).map_err(|error| at(target, error))?;
    }
    report.copied_files += 1;
    Ok(())
}

static SCRATCH_ID: AtomicU64 = AtomicU64::new(0);
struct Scratch(PathBuf);
impl Scratch {
    fn new() -> Result<Self> {
        let root = env::current_dir()
            .map_err(|error| error.to_string())?
            .join(".temp");
        check_ancestors(&root)?;
        fs::create_dir_all(&root).map_err(|error| at(&root, error))?;
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

fn run(command: &mut Command) -> Result<Output> {
    let name = command.get_program().to_string_lossy().into_owned();
    let output = command
        .output()
        .map_err(|error| format!("cannot start {name}: {error}"))?;
    if !output.status.success() {
        return Err(format!(
            "{name} failed ({}): {}",
            output.status,
            String::from_utf8_lossy(&output.stderr).trim()
        ));
    }
    Ok(output)
}

fn quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "'\\''"))
}

pub(crate) fn remote_command(backend: &Backend, script: &str, args: &[&str]) -> Result<Output> {
    run(&mut remote_command_builder(backend, script, args)?)
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
) -> Result<()> {
    let uses_wsl =
        matches!(op.source.backend, Backend::Wsl) || matches!(op.destination.backend, Backend::Wsl);
    let wsl_source = source.is_some() || matches!(op.source.backend, Backend::Wsl);
    let wsl_destination = destination.is_some() || matches!(op.destination.backend, Backend::Wsl);
    if uses_wsl && wsl_source && wsl_destination {
        let source_path = wsl_path(source, &op.source.path)?;
        let target_path = wsl_path(destination, &op.destination.path)?;
        remote_copy(
            &Backend::Wsl,
            &source_path,
            &target_path,
            op.destination.path.ends_with('/'),
            op.pattern.as_deref(),
            report,
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
        );
    }
    if let Some(source) = source {
        return send(
            source,
            &op.destination,
            op.destination.path.ends_with('/'),
            op.pattern.as_deref(),
            report,
        );
    }
    let scratch = Scratch::new()?;
    let staged = receive(&op.source, &scratch, op.pattern.as_deref())?;
    let source = &staged;
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
        );
    }
    send(
        source,
        &op.destination,
        op.destination.path.ends_with('/'),
        op.pattern.as_deref(),
        report,
    )
}

fn wsl_path(local: Option<&Path>, remote: &str) -> Result<String> {
    let Some(path) = local else {
        return Ok(remote.to_owned());
    };
    let path = absolute(path)?;
    let output = run(Command::new("wsl").args([
        OsStr::new("--exec"),
        OsStr::new("wslpath"),
        OsStr::new("-a"),
        OsStr::new("-u"),
        path.as_os_str(),
    ]))?;
    Ok(String::from_utf8(output.stdout)
        .map_err(|_| "wslpath returned non-UTF-8 output")?
        .trim_end()
        .to_owned())
}

fn receive(location: &Location, scratch: &Scratch, pattern: Option<&str>) -> Result<PathBuf> {
    let listing = if matches!(location.backend, Backend::Ssh { .. }) {
        let output = remote_command(&location.backend, REMOTE_VALIDATE, &[&location.path])?;
        validate_remote_names(&output.stdout)?;
        Some(output.stdout)
    } else {
        None
    };
    let fallback_name = location
        .path
        .trim_end_matches('/')
        .rsplit('/')
        .next()
        .filter(|name| !name.is_empty() && *name != "." && *name != "..")
        .unwrap_or("download");
    let name = match &listing {
        Some(listing) => std::str::from_utf8(
            listing
                .split(|byte| *byte == 0)
                .nth(1)
                .ok_or("remote root basename is missing")?,
        )
        .map_err(|_| "remote root basename is not UTF-8")?,
        None => fallback_name,
    };
    validate_staging_basename(name)?;
    let target = scratch.0.join(name);
    match &location.backend {
        Backend::Http { .. } => {
            if pattern.is_some() {
                return Err("HTTP sources cannot enumerate a regex selection".into());
            }
            run(http_command_builder(location)?.arg("--output").arg(&target))?;
        }
        Backend::Ssh { .. } => {
            scp(location, &target, false)?;
        }
        Backend::Wsl => {
            let path = wsl_path(Some(&target), "")?;
            let mut ignored = ExecutionReport::default();
            remote_copy(
                &Backend::Wsl,
                &location.path,
                &path,
                false,
                None,
                &mut ignored,
            )?;
        }
        _ => return Err("unsupported source endpoint".into()),
    }
    Ok(target)
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

fn validate_remote_names(listing: &[u8]) -> Result<()> {
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
) -> Result<()> {
    let source = absolute(source)?;
    let source = source.as_path();
    match &location.backend {
        Backend::Ssh { .. } => {
            validate_source_tree(source)?;
            let name = source
                .file_name()
                .and_then(OsStr::to_str)
                .ok_or("source has no UTF-8 basename")?;
            let output = remote_command(
                &location.backend,
                "umask 077; mktemp -d /tmp/msync.XXXXXXXXXX",
                &[],
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
            let result = scp(&staged, source, true).and_then(|()| {
                remote_copy(
                    &location.backend,
                    &staged.path,
                    &location.path,
                    container,
                    pattern,
                    report,
                )
            });
            let cleanup = remote_command(&location.backend, "rm -rf -- \"$1\"", &[&remote_root]);
            result?;
            cleanup?;
            Ok(())
        }
        Backend::Wsl => {
            let path = wsl_path(Some(source), "")?;
            remote_copy(
                &Backend::Wsl,
                &path,
                &location.path,
                container,
                pattern,
                report,
            )
        }
        _ => Err("unsupported destination endpoint".into()),
    }
}

fn scp(location: &Location, local: &Path, upload: bool) -> Result<()> {
    let Backend::Ssh { host, user, port } = &location.backend else {
        return Err("SCP requires SSH endpoint".into());
    };
    let mut command = ssh_client("scp");
    command.arg("-r");
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
    run(&mut command)?;
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
) -> Result<()> {
    if let Some(pattern) = pattern {
        if !container {
            return Err("a regex selection requires a destination ending in /".into());
        }
        let regex =
            Regex::new(&format!("\\A(?:{pattern})\\z")).map_err(|error| error.to_string())?;
        let listing = remote_command(backend, REMOTE_LIST, &[source])?;
        let mut names: Vec<&str> = std::str::from_utf8(&listing.stdout)
            .map_err(|_| "remote directory contains non-UTF-8 names")?
            .split('\0')
            .filter(|name| !name.is_empty() && regex.is_match(name))
            .collect();
        names.sort_unstable();
        for name in &names {
            let source = format!("{}/{name}", source.trim_end_matches('/'));
            remote_command(backend, REMOTE_COPY, &[&source, destination, "1", "1"])?;
        }
        for name in names {
            let source = format!("{}/{name}", source.trim_end_matches('/'));
            remote_copy(backend, &source, destination, true, None, report)?;
        }
        return Ok(());
    }
    let output = remote_command(
        backend,
        REMOTE_COPY,
        &[source, destination, if container { "1" } else { "0" }],
    )?;
    let counts = String::from_utf8_lossy(&output.stdout);
    for line in counts.lines() {
        match line {
            "file" => report.copied_files += 1,
            "directory" => report.created_directories += 1,
            _ => {}
        }
    }
    Ok(())
}

const REMOTE_INSPECT: &str = r#"
set -eu
p=$1
case $p in '~') p=$HOME;; '~/'*) p=$HOME/${p#\~/};; esac
case $p in /*) ;; *) p=$PWD/$p;; esac
a=$p
while [ "$a" != / ]; do
    [ ! -L "$a" ] || { echo 'refusing symbolic link in metadata probe' >&2; exit 1; }
    a=$(dirname -- "$a")
done
if [ -f "$p" ]; then printf 'regular\0'
elif [ -d "$p" ]; then printf 'directory\0'
elif [ -p "$p" ] || [ -S "$p" ]; then printf 'stream\0'
elif [ ! -e "$p" ]; then printf 'missing\0'
else echo 'unsupported remote filesystem entry' >&2; exit 1
fi
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
find "$s" -printf '%P\0%f\0'
"#;

const REMOTE_COPY: &str = r#"
set -eu
fail() { echo "$*" >&2; exit 1; }
expand() { case $1 in '~') printf '%s' "$HOME";; '~/'*) printf '%s/%s' "$HOME" "${1#\~/}";; *) printf '%s' "$1";; esac; }
s=$(expand "$1"); d=$(expand "$2"); mode=$3
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
copy() (
    a=$1; b=$2
    if [ -d "$a" ]; then
        if [ ! -d "$b" ]; then mkdir -p -- "$b"; echo directory; fi
        for x in "$a"/* "$a"/.[!.]* "$a"/..?*; do
            [ -e "$x" ] || continue
            copy "$x" "$b/$(basename -- "$x")"
        done
    else
        mkdir -p -- "$(dirname -- "$b")"
        if [ "$mode" = 1 ]; then
            (set -C; cat -- "$a" > "$b") || fail "cannot create destination: $b"
            chmod --reference="$a" -- "$b"
        else cp -p -- "$a" "$b"; fi
        echo file
    fi
)
check "$s" "$d"
[ "${4:-0}" = 0 ] || exit 0
copy "$s" "$d"
"#;

#[cfg(test)]
mod transport_tests {
    use super::{escape_sftp_pattern, validate_remote_names, validate_staging_basename};

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
    }
}
