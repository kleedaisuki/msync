//! One native SSH exec carries discovery, archive bytes and the final result.

use std::ffi::{OsStr, OsString};
use std::fs::{self, File};
use std::io::{self, BufRead, BufReader, BufWriter, Read, Write};
use std::path::{Path, PathBuf};
use std::process::{ChildStdin, ChildStdout, Command, Stdio};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use regex::Regex;
use tokio_util::sync::CancellationToken;

use crate::endpoint::{Backend, Location};
use crate::filesystem::InvocationRoot;
use crate::language::DestinationMode;

use super::{ChildWait, ExecutionReport, ImportReceipt, Result, at, ssh, touch_time};

pub(crate) struct Upload<'a> {
    pub source: &'a Path,
    pub destination: &'a Location,
    pub mode: DestinationMode,
    pub basename: Option<&'a OsStr>,
    pub selected: Option<&'a Regex>,
    pub literal: bool,
    pub exclusion: Option<&'a InvocationRoot>,
}

pub(crate) struct RemoteCopy<'a> {
    pub source: &'a Location,
    pub destination: &'a Location,
    pub mode: DestinationMode,
    pub selected: Option<&'a Regex>,
}

/// Admission has already grouped these sources by the destination backend.
pub(crate) fn copy_many(
    sources: &[Location],
    destination: &Location,
    cancel: &CancellationToken,
) -> Result<ExecutionReport> {
    if sources.is_empty() {
        return Ok(ExecutionReport::default());
    }
    let sources = sources
        .iter()
        .map(|source| source.path.clone())
        .collect::<Vec<_>>();
    let script = format!(
        "{}\n{BEGIN}\n{MOVE}\n{COPY_MANY}\n{INSTALL}",
        relay_definition()
    );
    let args = [
        destination.path.as_str(),
        "container",
        "",
        "selected",
        "filesystem",
        "file",
    ];
    exchange(
        &destination.backend,
        &script,
        &args,
        cancel,
        move |input, output| {
            let mut input = BufWriter::new(input);
            let mut output = BufReader::new(output);
            ready(&mut output)?;
            for source in sources {
                for byte in source.as_bytes() {
                    write!(&mut input, "\\0{byte:03o}").map_err(|cause| cause.to_string())?;
                }
                input.write_all(b"\n").map_err(|cause| cause.to_string())?;
            }
            input
                .write_all(b"MSYNC-END\n")
                .and_then(|()| input.flush())
                .map_err(|cause| cause.to_string())?;
            let result = copy_result(&mut output);
            drop(input);
            result
        },
    )
}

pub(crate) fn copy_remote(
    request: RemoteCopy<'_>,
    cancel: &CancellationToken,
) -> Result<ExecutionReport> {
    let mode = match request.mode {
        DestinationMode::Exact => "exact",
        DestinationMode::Container => "container",
        DestinationMode::ReplaceTree => "replace",
    };
    let selected = request.selected.cloned();
    let args = [
        request.destination.path.as_str(),
        mode,
        "",
        if selected.is_some() {
            "selected"
        } else {
            "single"
        },
        "filesystem",
        "file",
        request.source.path.as_str(),
    ];
    let script = format!("{}\n{BEGIN}\n{COPY}\n{MOVE}\n{INSTALL}", relay_definition());
    exchange(
        &request.destination.backend,
        &script,
        &args,
        cancel,
        move |input, output| {
            let mut input = BufWriter::new(input);
            let mut output = BufReader::new(output);
            let kind = ready(&mut output)?;
            if kind == "names" {
                let pattern = selected
                    .as_ref()
                    .ok_or("remote copy requested an unexpected selector")?;
                let names = matching_names(&mut output, pattern)?;
                for name in names {
                    for byte in name.as_encoded_bytes() {
                        write!(&mut input, "\\0{byte:03o}").map_err(|cause| cause.to_string())?;
                    }
                    input.write_all(b"\n").map_err(|cause| cause.to_string())?;
                }
                input
                    .write_all(b"MSYNC-END\n")
                    .and_then(|()| input.flush())
                    .map_err(|cause| cause.to_string())?;
            }
            let control = input;
            let result = copy_result(&mut output);
            drop(control);
            result
        },
    )
}

fn copy_result(output: &mut impl Read) -> Result<ExecutionReport> {
    let mut response = String::new();
    output
        .read_to_string(&mut response)
        .map_err(|cause| cause.to_string())?;
    let counts = response
        .strip_prefix("MSYNC-DONE\n")
        .ok_or_else(|| format!("remote copy returned no completion: {response}"))?;
    let mut report = ExecutionReport::default();
    let mut receipt = b"source\0payload\0".to_vec();
    receipt.extend_from_slice(counts.as_bytes());
    super::remote_copy_report(&receipt, &mut report)?;
    Ok(report)
}

pub(crate) fn upload(request: Upload<'_>, cancel: &CancellationToken) -> Result<ExecutionReport> {
    let selected = request
        .selected
        .map(|pattern| {
            children(request.source).map(|entries| {
                entries
                    .into_iter()
                    .filter(|path| {
                        pattern.is_match(&path.file_name().unwrap_or_default().to_string_lossy())
                    })
                    .collect::<Vec<_>>()
            })
        })
        .transpose()?;
    if selected.as_ref().is_some_and(Vec::is_empty) {
        return Ok(ExecutionReport::default());
    }
    let exclusion = request.exclusion.cloned();
    let selected_mode = selected.is_some();
    let plan = if let Some(entries) = selected {
        let metadata = fs::metadata(request.source).map_err(|cause| at(request.source, cause))?;
        let roots = entries
            .into_iter()
            .map(|path| {
                let name = Path::new("payload").join(path.file_name().unwrap_or_default());
                (path, name)
            })
            .collect();
        ArchivePlan {
            roots,
            envelope: Some(Envelope {
                mode: archive_mode(&metadata),
                modified: metadata
                    .modified()
                    .map_err(|cause| at(request.source, cause))?,
            }),
        }
    } else {
        ArchivePlan {
            roots: vec![(request.source.to_owned(), PathBuf::from("payload"))],
            envelope: None,
        }
    };
    let mode = match request.mode {
        DestinationMode::Exact => "exact",
        DestinationMode::Container => "container",
        DestinationMode::ReplaceTree => "replace",
    };
    let name = request.basename.unwrap_or_default().to_string_lossy();
    let args = [
        request.destination.path.as_str(),
        mode,
        name.as_ref(),
        if selected_mode { "selected" } else { "single" },
        if cfg!(windows) {
            "portable"
        } else {
            "filesystem"
        },
        if request.literal { "literal" } else { "file" },
    ];
    exchange(
        &request.destination.backend,
        &upload_script(),
        &args,
        cancel,
        move |input, output| send_archive(input, output, plan, exclusion),
    )
}

pub(crate) fn upload_many(
    sources: &[(PathBuf, OsString)],
    destination: &Location,
    exclusion: Option<&InvocationRoot>,
    cancel: &CancellationToken,
) -> Result<ExecutionReport> {
    if sources.is_empty() {
        return Ok(ExecutionReport::default());
    }
    let roots = sources
        .iter()
        .map(|(path, name)| (path.clone(), Path::new("payload").join(name)))
        .collect();
    let plan = ArchivePlan {
        roots,
        envelope: Some(Envelope {
            mode: 0o777,
            modified: SystemTime::now(),
        }),
    };
    let exclusion = exclusion.cloned();
    let args = [
        destination.path.as_str(),
        "container",
        "",
        "selected",
        if cfg!(windows) {
            "portable"
        } else {
            "filesystem"
        },
        "file",
    ];
    exchange(
        &destination.backend,
        &upload_script(),
        &args,
        cancel,
        move |input, output| send_archive(input, output, plan, exclusion),
    )
}

struct ArchivePlan {
    roots: Vec<(PathBuf, PathBuf)>,
    envelope: Option<Envelope>,
}

struct Envelope {
    mode: u32,
    modified: SystemTime,
}

fn send_archive(
    input: ChildStdin,
    output: ChildStdout,
    plan: ArchivePlan,
    exclusion: Option<InvocationRoot>,
) -> Result<ExecutionReport> {
    let mut output = BufReader::new(output);
    ready(&mut output)?;
    let mut archive = tar::Builder::new(BufWriter::new(input));
    let mut report = ExecutionReport::default();
    if let Some(envelope) = plan.envelope {
        let time = touch_time(envelope.modified);
        archive
            .append_pax_extensions([("mtime", time.as_bytes())])
            .map_err(|cause| cause.to_string())?;
        let mut header = tar::Header::new_gnu();
        header.set_mode(envelope.mode);
        header.set_mtime(
            envelope
                .modified
                .duration_since(UNIX_EPOCH)
                .map_or(0, |time| time.as_secs()),
        );
        header.set_entry_type(tar::EntryType::Directory);
        header.set_size(0);
        archive
            .append_data(&mut header, "payload", io::empty())
            .map_err(|cause| cause.to_string())?;
        report.created_directories += 1;
    }
    for (source, name) in plan.roots {
        append_tree(
            &mut archive,
            &source,
            &name,
            exclusion.as_ref(),
            &mut report,
        )?;
    }
    archive.finish().map_err(|cause| cause.to_string())?;
    let mut input = archive.into_inner().map_err(|cause| cause.to_string())?;
    input
        .write_all(b"MSYNC-END\n")
        .and_then(|()| input.flush())
        .map_err(|cause| cause.to_string())?;
    crate::telemetry::event(
        "ssh.archive.sent",
        &[],
        &[
            ("files", report.copied_files as u64),
            ("bytes", report.transferred_bytes),
        ],
    );
    let mut response = String::new();
    output
        .read_to_string(&mut response)
        .map_err(|cause| cause.to_string())?;
    drop(input);
    if !response.starts_with("MSYNC-DONE\n") {
        return Err(format!(
            "remote transfer returned no completion: {response}"
        ));
    }
    Ok(report)
}

pub(crate) fn download(
    source: &Location,
    target: &Path,
    selected: Option<&Regex>,
    cancel: &CancellationToken,
) -> Result<ImportReceipt> {
    let target = target.to_owned();
    let selected = selected.cloned();
    let args = [
        source.path.as_str(),
        if selected.is_some() {
            "selected"
        } else {
            "single"
        },
    ];
    exchange(
        &source.backend,
        &format!("{}\n{DOWNLOAD}", relay_definition()),
        &args,
        cancel,
        move |mut input, output| {
            let mut output = BufReader::new(output);
            let kind = ready(&mut output)?;
            let basename = read_name(&mut output)?;
            if kind == "names" {
                let pattern = selected
                    .as_ref()
                    .ok_or("remote source unexpectedly requested selection")?;
                let names = matching_names(&mut output, pattern)?;
                for name in names {
                    input
                        .write_all(name.as_encoded_bytes())
                        .and_then(|()| input.write_all(&[0]))
                        .map_err(|cause| cause.to_string())?;
                }
            }
            let control = if kind == "names" {
                drop(input);
                None
            } else {
                Some(input)
            };
            if kind == "stream" {
                let mut file = File::create(&target).map_err(|cause| at(&target, cause))?;
                io::copy(&mut output, &mut file).map_err(|cause| at(&target, cause))?;
            } else {
                extract(&mut output, &target)?;
                io::copy(&mut output, &mut io::sink()).map_err(|cause| cause.to_string())?;
            }
            drop(control);
            Ok(ImportReceipt {
                basename: Some(basename),
            })
        },
    )
}

fn children(path: &Path) -> Result<Vec<PathBuf>> {
    let mut entries = fs::read_dir(path)
        .map_err(|cause| at(path, cause))?
        .map(|entry| entry.map(|entry| entry.path()))
        .collect::<io::Result<Vec<_>>>()
        .map_err(|cause| at(path, cause))?;
    entries.sort();
    Ok(entries)
}

fn append_tree<W: Write>(
    archive: &mut tar::Builder<W>,
    source: &Path,
    name: &Path,
    exclusion: Option<&InvocationRoot>,
    report: &mut ExecutionReport,
) -> Result<()> {
    let metadata = fs::symlink_metadata(source).map_err(|cause| at(source, cause))?;
    let directory = metadata.is_dir();
    if directory {
        if exclusion.is_some_and(|exclusion| {
            same_file::is_same_file(source, exclusion.path()).unwrap_or(false)
        }) {
            return Ok(());
        }
        if let Some(context) = ssh::current()
            && context
                .excludes_directory(source)
                .map_err(|cause| at(source, cause))?
        {
            return Ok(());
        }
    }
    append_metadata(archive, source, name, &metadata, report)?;
    if directory {
        for child in children(source)? {
            let child_name = child.file_name().ok_or("source child has no basename")?;
            append_tree(archive, &child, &name.join(child_name), exclusion, report)?;
        }
    }
    Ok(())
}

fn append_metadata<W: Write>(
    archive: &mut tar::Builder<W>,
    source: &Path,
    name: &Path,
    metadata: &fs::Metadata,
    report: &mut ExecutionReport,
) -> Result<()> {
    let mut bytes = metadata.len();
    let modified = metadata.modified().map_err(|cause| at(source, cause))?;
    let time = touch_time(modified);
    archive
        .append_pax_extensions([("mtime", time.as_bytes())])
        .map_err(|cause| at(source, cause))?;
    let mut header = tar::Header::new_gnu();
    header.set_mtime(
        modified
            .duration_since(UNIX_EPOCH)
            .map_or(0, |time| time.as_secs()),
    );
    header.set_uid(0);
    header.set_gid(0);
    header.set_mode(archive_mode(metadata));
    if metadata.is_file() {
        let file = File::open(source).map_err(|cause| at(source, cause))?;
        bytes = file.metadata().map_err(|cause| at(source, cause))?.len();
        header.set_size(bytes);
        header.set_entry_type(tar::EntryType::Regular);
        archive
            .append_data(
                &mut header,
                name,
                ExactReader {
                    reader: file,
                    remaining: bytes,
                },
            )
            .map_err(|cause| at(source, cause))?;
    } else if metadata.is_dir() {
        header.set_size(0);
        header.set_entry_type(tar::EntryType::Directory);
        archive
            .append_data(&mut header, name, io::empty())
            .map_err(|cause| at(source, cause))?;
    } else if metadata.file_type().is_symlink() {
        header.set_size(0);
        header.set_entry_type(tar::EntryType::Symlink);
        archive
            .append_link(
                &mut header,
                name,
                fs::read_link(source).map_err(|cause| at(source, cause))?,
            )
            .map_err(|cause| at(source, cause))?;
    } else {
        archive
            .append_path_with_name(source, name)
            .map_err(|cause| at(source, cause))?;
    }
    if metadata.is_dir() {
        report.created_directories += 1;
    } else {
        report.copied_files += 1;
        report.transferred_bytes += bytes;
    }
    Ok(())
}

struct ExactReader<R> {
    reader: R,
    remaining: u64,
}

impl<R: Read> Read for ExactReader<R> {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        if self.remaining == 0 || buffer.is_empty() {
            return Ok(0);
        }
        let length = self.remaining.min(buffer.len() as u64) as usize;
        let read = self.reader.read(&mut buffer[..length])?;
        if read == 0 {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                format!(
                    "source ended with {} declared archive bytes unread",
                    self.remaining
                ),
            ));
        }
        self.remaining -= read as u64;
        Ok(read)
    }
}

fn archive_mode(metadata: &fs::Metadata) -> u32 {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        metadata.permissions().mode()
    }
    #[cfg(not(unix))]
    {
        let mode = if metadata.is_dir() { 0o777 } else { 0o666 };
        if metadata.permissions().readonly() {
            mode & !0o222
        } else {
            mode
        }
    }
}

fn extract(reader: &mut impl Read, target: &Path) -> Result<()> {
    let parent = target.parent().ok_or("download target has no parent")?;
    let mut archive = tar::Archive::new(reader);
    let mut directories = Vec::new();
    for entry in archive.entries().map_err(|cause| cause.to_string())? {
        let mut entry = entry.map_err(|cause| cause.to_string())?;
        let path = entry
            .path()
            .map_err(|cause| cause.to_string())?
            .into_owned();
        let relative = path
            .strip_prefix("payload")
            .map_err(|_| "remote archive has an unexpected root")?;
        let destination = if relative.as_os_str().is_empty() {
            target.to_owned()
        } else {
            target.join(relative)
        };
        let mode = entry.header().mode().map_err(|cause| cause.to_string())?;
        let mut modified = entry
            .header()
            .mtime()
            .ok()
            .and_then(|seconds| UNIX_EPOCH.checked_add(Duration::from_secs(seconds)))
            .unwrap_or(UNIX_EPOCH);
        if let Some(extensions) = entry.pax_extensions().map_err(|cause| cause.to_string())? {
            for extension in extensions {
                let extension = extension.map_err(|cause| cause.to_string())?;
                if extension.key_bytes() == b"mtime" {
                    modified = parse_time(extension.value().map_err(|cause| cause.to_string())?)?;
                }
            }
        }
        if entry.header().entry_type().is_dir() {
            fs::create_dir_all(&destination).map_err(|cause| at(&destination, cause))?;
            directories.push((destination, mode, modified));
        } else {
            fs::create_dir_all(destination.parent().unwrap_or(parent))
                .map_err(|cause| at(&destination, cause))?;
            entry.set_preserve_permissions(true);
            if entry.header().entry_type().is_hard_link() {
                let link = entry
                    .link_name()
                    .map_err(|cause| cause.to_string())?
                    .ok_or("archive hard link has no target")?
                    .into_owned();
                let relative = link
                    .strip_prefix("payload")
                    .map_err(|_| "archive hard link has an unexpected root")?;
                fs::hard_link(target.join(relative), &destination)
                    .map_err(|cause| at(&destination, cause))?;
            } else {
                entry
                    .unpack(&destination)
                    .map_err(|cause| at(&destination, cause))?;
            }
            apply_metadata(
                &destination,
                mode,
                modified,
                entry.header().entry_type().is_symlink(),
            )?;
        }
    }
    for (directory, mode, modified) in directories.into_iter().rev() {
        apply_metadata(&directory, mode, modified, false)?;
    }
    if fs::symlink_metadata(target).is_err_and(|cause| cause.kind() == io::ErrorKind::NotFound) {
        fs::create_dir(target).map_err(|cause| at(target, cause))?;
    }
    Ok(())
}

fn apply_metadata(path: &Path, mode: u32, modified: SystemTime, symlink: bool) -> Result<()> {
    let time = filetime::FileTime::from_system_time(modified);
    if symlink {
        return filetime::set_symlink_file_times(path, time, time).map_err(|cause| at(path, cause));
    }
    filetime::set_file_mtime(path, time).map_err(|cause| at(path, cause))?;
    #[cfg(unix)]
    let permissions = {
        use std::os::unix::fs::PermissionsExt;
        fs::Permissions::from_mode(mode)
    };
    #[cfg(not(unix))]
    let permissions = {
        let mut permissions = fs::metadata(path)
            .map_err(|cause| at(path, cause))?
            .permissions();
        permissions.set_readonly(mode & 0o222 == 0);
        permissions
    };
    fs::set_permissions(path, permissions).map_err(|cause| at(path, cause))
}

fn parse_time(text: &str) -> Result<SystemTime> {
    let negative = text.starts_with('-');
    let text = text.trim_start_matches('-');
    let (seconds, fraction) = text.split_once('.').unwrap_or((text, ""));
    let seconds = seconds.parse::<u64>().map_err(|cause| cause.to_string())?;
    let fraction = format!("{fraction:0<9}");
    let nanos = fraction
        .chars()
        .take(9)
        .collect::<String>()
        .parse::<u32>()
        .map_err(|cause| cause.to_string())?;
    let duration = Duration::new(seconds, nanos);
    if negative {
        UNIX_EPOCH.checked_sub(duration)
    } else {
        UNIX_EPOCH.checked_add(duration)
    }
    .ok_or_else(|| "remote modification time is outside this platform's range".into())
}

fn ready(reader: &mut impl BufRead) -> Result<String> {
    let mut line = String::new();
    reader
        .read_line(&mut line)
        .map_err(|cause| cause.to_string())?;
    let kind = line
        .trim_end()
        .strip_prefix("MSYNC-READY ")
        .map(str::to_owned)
        .ok_or_else(|| format!("unexpected remote transfer response: {line}"))?;
    crate::telemetry::event("ssh.remote.ready", &[("endpoint.kind", &kind)], &[]);
    Ok(kind)
}

fn matching_names(reader: &mut impl BufRead, pattern: &Regex) -> Result<Vec<OsString>> {
    let mut matches = Vec::new();
    loop {
        let name = read_name(reader)?;
        if name.is_empty() {
            matches.sort();
            return Ok(matches);
        }
        if pattern.is_match(&name.to_string_lossy()) {
            matches.push(name);
        }
    }
}

fn read_name(reader: &mut impl BufRead) -> Result<OsString> {
    let mut bytes = Vec::new();
    if reader
        .read_until(0, &mut bytes)
        .map_err(|cause| cause.to_string())?
        == 0
        || bytes.pop() != Some(0)
    {
        return Err("remote source name response ended early".into());
    }
    #[cfg(unix)]
    {
        use std::os::unix::ffi::OsStringExt;
        Ok(OsString::from_vec(bytes))
    }
    #[cfg(not(unix))]
    {
        String::from_utf8(bytes)
            .map(OsString::from)
            .map_err(|cause| cause.to_string())
    }
}

fn exchange<T: Send + 'static>(
    backend: &Backend,
    script: &str,
    args: &[&str],
    cancel: &CancellationToken,
    action: impl FnOnce(ChildStdin, ChildStdout) -> Result<T> + Send + 'static,
) -> Result<T> {
    if cancel.is_cancelled() {
        return Err("SSH transfer cancelled".into());
    }
    let mut trace = ssh::client_span("ssh.transfer", backend);
    trace.expect_completion();
    trace.attribute("ssh.session.strategy", "native_single_exec");
    trace.metric("client.invocations", 1);
    let mut command = match backend {
        Backend::Ssh { .. } => ssh::single_command(backend, script, args)?,
        Backend::Wsl => super::remote_command_builder(backend, script, args)?,
        _ => return Err("archive transfer requires an SSH or WSL endpoint".into()),
    };
    let result = crate::telemetry::with_context(trace.context().as_ref(), || {
        run_exchange(&mut command, cancel, action)
    });
    trace.finish_result(&result);
    if result.is_ok() {
        trace.event("ssh.remote.done", &[], &[]);
    }
    if cancel.is_cancelled() {
        trace.cancelled();
    }
    result
}

fn run_exchange<T: Send + 'static>(
    command: &mut Command,
    cancel: &CancellationToken,
    action: impl FnOnce(ChildStdin, ChildStdout) -> Result<T> + Send + 'static,
) -> Result<T> {
    let mut trace = crate::telemetry::span("transport_process");
    trace.expect_completion();
    trace.attribute_lazy("program", || {
        command.get_program().to_string_lossy().into_owned()
    });
    trace.attribute("phase", "archive.exchange");
    let result = exchange_process(command, cancel, &mut trace, action);
    trace.finish_result(&result);
    if cancel.is_cancelled() {
        trace.cancelled();
    }
    result
}

fn exchange_process<T: Send + 'static>(
    command: &mut Command,
    cancel: &CancellationToken,
    trace: &mut crate::telemetry::SpanGuard,
    action: impl FnOnce(ChildStdin, ChildStdout) -> Result<T> + Send + 'static,
) -> Result<T> {
    command
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let spawned = trace.is_recording().then(std::time::Instant::now);
    #[cfg(windows)]
    let (mut child, job) =
        super::windows_auth::spawn(command).map_err(|cause| cause.to_string())?;
    #[cfg(not(windows))]
    let mut child = command.spawn().map_err(|cause| cause.to_string())?;
    super::record_elapsed(trace, "spawn_ns", spawned);
    trace.metric("pid", u64::from(child.id()));
    let input = child.stdin.take().ok_or("SSH input is unavailable")?;
    let output = child.stdout.take().ok_or("SSH output is unavailable")?;
    let mut errors = child
        .stderr
        .take()
        .ok_or("SSH diagnostics are unavailable")?;
    let ssh_context = ssh::current();
    let trace_context = crate::telemetry::context();
    let mut worker = Some(std::thread::spawn(move || {
        ssh::with_context(ssh_context.as_ref(), || {
            crate::telemetry::with_context(trace_context.as_ref(), || action(input, output))
        })
    }));
    let mut completed = None;
    let diagnostics = std::thread::spawn(move || {
        let mut bytes = Vec::new();
        errors.read_to_end(&mut bytes).map(|_| bytes)
    });
    let ready = ChildWait::new(&child);
    trace.attribute("wait.strategy", ready.strategy());
    trace.event("process.open", &[], &[]);
    let waiting = trace.is_recording().then(std::time::Instant::now);
    let status = loop {
        if worker
            .as_ref()
            .is_some_and(std::thread::JoinHandle::is_finished)
        {
            completed = Some(
                worker
                    .take()
                    .expect("finished archive worker")
                    .join()
                    .unwrap_or_else(|_| Err("SSH archive worker failed".into())),
            );
            if completed.as_ref().is_some_and(Result::is_err) {
                #[cfg(windows)]
                let _ = job.terminate();
                ssh::stop(&mut child);
                break child.wait();
            }
        }
        if cancel.is_cancelled() {
            #[cfg(windows)]
            let _ = job.terminate();
            ssh::stop(&mut child);
            break child.wait();
        }
        match child.try_wait() {
            Ok(Some(status)) => break Ok(status),
            Ok(None) => ready.pause(&child),
            Err(cause) => {
                ssh::stop(&mut child);
                let _ = child.wait();
                break Err(cause);
            }
        }
    };
    super::record_elapsed(trace, "child_wait_ns", waiting);
    let draining = trace.is_recording().then(std::time::Instant::now);
    #[cfg(windows)]
    let _ = job.terminate();
    let result = match worker {
        Some(worker) => worker
            .join()
            .unwrap_or_else(|_| Err("SSH archive worker failed".into())),
        None => completed.expect("joined archive worker result"),
    };
    let errors = diagnostics
        .join()
        .map_err(|_| "SSH diagnostic reader failed")?
        .map_err(|cause| cause.to_string())?;
    super::record_elapsed(trace, "drain_ns", draining);
    trace.metric("stderr_bytes", errors.len() as u64);
    let status = status.map_err(|cause| cause.to_string())?;
    if trace.is_recording() {
        trace.event("process.exit", &[("status", &status.to_string())], &[]);
    }
    if cancel.is_cancelled() {
        return Err("SSH transfer cancelled".into());
    }
    if let Err(cause) = result {
        let diagnostic = String::from_utf8_lossy(&errors);
        return Err(if diagnostic.trim().is_empty() {
            cause
        } else {
            format!("{cause}; SSH: {}", diagnostic.trim())
        });
    }
    if !status.success() {
        return Err(format!(
            "SSH transfer failed ({status}): {}",
            String::from_utf8_lossy(&errors).trim()
        ));
    }
    result
}

fn upload_script() -> String {
    format!(
        "{}\n{BEGIN}\n{RECEIVE}\n{MOVE}\n{INSTALL}",
        relay_definition()
    )
}

fn relay_definition() -> String {
    format!(
        "{RESOLVE_SOURCE}\nexec 3<&0\nrelay() {{ python3 -c {} \"$@\" <&3; }}",
        super::quote(PHYSICAL)
    )
}

const RESOLVE_SOURCE: &str = r#"
resolve_source() {
    case $s in '.'|'..'|*/|*/.|*/..)
        s=$(realpath -e -- "$s" && printf .)
        s=${s%.}; s=${s%?}
    esac
}
"#;

const PHYSICAL: &str = r#"
from __future__ import annotations
import os
import select
import subprocess
import sys

SOCKET_IO = '''from __future__ import annotations
import shutil, socket, sys
from typing import BinaryIO
direction: str = sys.argv[1]
connection: socket.socket = socket.socket(socket.AF_UNIX)
connection.connect(sys.argv[2])
if direction == "w":
    source: BinaryIO = open(sys.argv[3], "rb")
    connection.sendfile(source)
    connection.shutdown(socket.SHUT_WR)
else:
    incoming: BinaryIO = connection.makefile("rb")
    shutil.copyfileobj(incoming, sys.stdout.buffer)
'''

def main() -> int:
    direction: str = sys.argv[1]
    kind: str = sys.argv[2]
    path: str = sys.argv[3]
    payload: str = sys.argv[4]
    command: list[str]
    if kind == "fifo":
        command = ["cat", "--", path] if direction == "r" else ["sh", "-c", 'exec cat -- "$1" > "$2"', "msync-relay", payload, path]
    else:
        command = [sys.executable, "-c", SOCKET_IO, direction, path, payload]
    child: subprocess.Popen[bytes] = subprocess.Popen(command, stdin=subprocess.DEVNULL)
    watcher: select.poll = select.poll()
    watcher.register(0, select.POLLIN | select.POLLHUP | select.POLLERR)
    try:
        while child.poll() is None:
            events: list[tuple[int, int]] = watcher.poll(100)
            if events and (events[0][1] & (select.POLLHUP | select.POLLERR) or os.read(0, 4096) == b""):
                return 130
        return child.wait()
    finally:
        if child.poll() is None:
            child.terminate()
        child.wait()

sys.exit(main())
"#;

const BEGIN: &str = r#"
set -eu
d=$1; mode=$2; name=$3; selection=$4; policy=$5; origin=$6
case $d in '~') d=$HOME;; '~/'*) d=$HOME/${d#\~/};; /*) ;; *) d=$PWD/$d;; esac
while [ "$d" != / ] && { [ "${d%/}" != "$d" ] || [ "${d%/.}" != "$d" ]; }; do d=${d%/}; d=${d%/.}; done
parent=${d%/*}
if [ "$mode" = container ] && [ -d "$d" ]; then parent=$d; fi
mkdir -p -- "$parent"
stage=$(mktemp -d "$parent/.msync-transfer.XXXXXXXXXX")
cleanup() { find "$stage" -type d -exec chmod u+rwx -- {} + 2>/dev/null || :; rm -rf -- "$stage"; }
trap cleanup EXIT
trap 'exit 130' HUP INT TERM
"#;

const RECEIVE: &str = r#"
if [ -p "$d" ] || [ -S "$d" ]; then printf 'MSYNC-READY stream\n'; else printf 'MSYNC-READY file\n'; fi
tar --extract --file=- --directory="$stage" --same-permissions --no-same-owner --blocking-factor=1
IFS= read -r completed
[ "$completed" = MSYNC-END ] || { echo 'source transfer did not finish' >&2; exit 1; }
"#;

const COPY: &str = r#"
s=$7
case $s in '~') s=$HOME;; '~/'*) s=$HOME/${s#\~/};; /*) ;; *) s=$PWD/$s;; esac
resolve_source
name=${s##*/}
if [ ! -L "$s" ] && [ -e "$d" ] && [ "$s" -ef "$d" ]; then echo 'source and destination are the same file' >&2; exit 1; fi
if [ "$selection" = selected ]; then
    printf 'MSYNC-READY names\n'
    find "$s/" -mindepth 1 -maxdepth 1 -printf '%f\0'; printf '\0'
    mkdir "$stage/payload"
    complete=0
    while IFS= read -r encoded; do
        if [ "$encoded" = MSYNC-END ]; then complete=1; break; fi
        child=$(printf '%b.' "$encoded"); child=${child%.}
        cp -a -- "$s/$child" "$stage/payload/$child"
    done
    [ "$complete" = 1 ] || { echo 'source selection did not finish' >&2; exit 1; }
elif [ -p "$s" ] || [ -S "$s" ]; then
    printf 'MSYNC-READY file\n'
    if [ -p "$s" ]; then relay r fifo "$s" "" > "$stage/payload"
    else relay r socket "$s" "" > "$stage/payload"; fi
else
    printf 'MSYNC-READY file\n'
    cp -a -- "$s" "$stage/payload"
fi
counts=$(find "$stage/payload" -type d -printf 'directory\n' -o -type f -printf 'file %s\n' -o -type l -printf 'file %s\n')
"#;

const MOVE: &str = r#"
move() (
    a=$1; b=$2; saved=
    if [ -d "$a" ] && [ ! -L "$a" ]; then
        saved=$(stat -c %a -- "$a")
        [ -w "$a" ] || chmod u+w -- "$a"
    fi
    mv -fT -- "$a" "$b"
    [ -z "$saved" ] || chmod "$saved" -- "$b"
)
"#;

const COPY_MANY: &str = r#"
printf 'MSYNC-READY file\n'
complete=0
while IFS= read -r encoded; do
    if [ "$encoded" = MSYNC-END ]; then complete=1; break; fi
    printf '%s\n' "$encoded" >> "$stage/sources"
done
[ "$complete" = 1 ] || { echo 'source list did not finish' >&2; exit 1; }
merge_private() (
    a=$1; b=$2
    if [ -d "$a" ] && [ ! -L "$a" ] && [ -d "$b" ] && [ ! -L "$b" ]; then
        saved=$(stat -c %a -- "$a")
        modified=$(stat -c %y -- "$a")
        [ -w "$a" ] || chmod u+w -- "$a"
        [ -w "$b" ] || chmod u+w -- "$b"
        for child in "$a"/* "$a"/.[!.]* "$a"/..?*; do
            [ -e "$child" ] || [ -L "$child" ] || continue
            merge_private "$child" "$b/${child##*/}"
        done
        chmod "$saved" -- "$b"
        touch --date="$modified" -- "$b"
    else
        if [ -d "$a" ] && [ ! -L "$a" ] && [ -L "$b" ]; then rm -f -- "$b"; fi
        move "$a" "$b"
    fi
)
mkdir "$stage/payload"
index=0
while IFS= read -r encoded; do
    s=$(printf '%b.' "$encoded"); s=${s%.}
    case $s in '~') s=$HOME;; '~/'*) s=$HOME/${s#\~/};; /*) ;; *) s=$PWD/$s;; esac
    resolve_source
    name=${s##*/}
    if [ -d "$s" ] && [ ! -L "$s" ] && [ "$s" -ef "$d" ]; then echo 'source directory contains its destination' >&2; exit 1; fi
    item=$stage/item-$index; index=$((index+1))
    if [ -p "$s" ]; then relay r fifo "$s" "" > "$item"
    elif [ -S "$s" ]; then relay r socket "$s" "" > "$item"
    else cp -aT -- "$s" "$item"; fi
    merge_private "$item" "$stage/payload/$name"
done < "$stage/sources"
counts=$(find "$stage/payload" -type d -printf 'directory\n' -o -type f -printf 'file %s\n' -o -type l -printf 'file %s\n')
"#;

const INSTALL: &str = r#"
payload=$stage/payload
normalize() (
    p=$1; bits=$(stat -c %a -- "$p")
    [ ! -L "$p" ] || return 0
    if [ -d "$p" ]; then
        for child in "$p"/* "$p"/.[!.]* "$p"/..?*; do [ -e "$child" ] || [ -L "$child" ] || continue; normalize "$child"; done
        value=$((0777 & ~0$mask))
    else value=$((0666 & ~0$mask)); fi
    [ "$((0$bits & 0222))" -ne 0 ] || value=$((value & ~0222))
    chmod "$(printf '%o' "$value")" -- "$p"
)
if [ "$policy" = portable ]; then mask=$(umask); normalize "$payload"; fi
if [ "$mode" = replace ]; then
    [ -d "$payload" ] && [ ! -L "$payload" ] || { echo 'directory replacement needs a directory source' >&2; exit 1; }
    if [ -e "$d" ] || [ -L "$d" ]; then
        [ -d "$d" ] || { echo 'directory replacement target is not a directory' >&2; exit 1; }
    fi
fi
if [ -p "$d" ]; then relay w fifo "$d" "$payload"
elif [ -S "$d" ]; then
    relay w socket "$d" "$payload"
elif [ "$mode" = container ]; then
    mkdir -p -- "$d"
    merge() (
        a=$1; b=$2
        if [ -d "$a" ] && [ ! -L "$a" ] && [ -d "$b" ] && [ ! -L "$b" ]; then
            for child in "$a"/* "$a"/.[!.]* "$a"/..?*; do [ -e "$child" ] || [ -L "$child" ] || continue; merge "$child" "$b/${child##*/}"; done
        else
            move "$a" "$b"
        fi
    )
    if [ "$selection" = selected ]; then
        for child in "$payload"/* "$payload"/.[!.]* "$payload"/..?*; do [ -e "$child" ] || [ -L "$child" ] || continue; merge "$child" "$d/${child##*/}"; done
    else
        [ -n "$name" ] || { echo 'container source has no basename' >&2; exit 1; }
        merge "$payload" "$d/$name"
    fi
else
    if [ "$mode" = exact ] && [ -d "$d" ] && [ ! -L "$d" ]; then echo 'destination is an existing directory; append / or /.' >&2; exit 1; fi
    if [ "$origin" = literal ] && [ -f "$d" ]; then chmod --reference="$d" -- "$payload"; fi
    if [ "$mode" = replace ] && [ -e "$d" ]; then rm -rf -- "$d"; fi
    move "$payload" "$d"
fi
printf 'MSYNC-DONE\n'
[ -z "${counts:-}" ] || printf '%s\n' "$counts"
"#;

const DOWNLOAD: &str = r#"
set -eu
s=$1; selection=$2
case $s in '~') s=$HOME;; '~/'*) s=$HOME/${s#\~/};; /*) ;; *) s=$PWD/$s;; esac
resolve_source
name=${s##*/}; parent=${s%/*}
if [ "$selection" = selected ]; then
    printf 'MSYNC-READY names\n%s\0' "$name"
    find "$s/" -mindepth 1 -maxdepth 1 -printf '%f\0'; printf '\0'
    names=$(mktemp)
    trap 'rm -f -- "$names"' EXIT
    trap 'exit 130' HUP INT TERM
    # Consume the request before producing archive bytes: otherwise a full
    # input/output pipe pair can deadlock a large selection behind its first file.
    cat > "$names"
    tar --create --format=pax --file=- --directory="$s" --null --verbatim-files-from --files-from="$names" --transform='flags=rh;s|^|payload/|'
elif [ -p "$s" ] || [ -S "$s" ]; then
    printf 'MSYNC-READY stream\n%s\0' "$name"
    if [ -p "$s" ]; then relay r fifo "$s" ""
    else relay r socket "$s" ""; fi
else
    printf 'MSYNC-READY archive\n%s\0' "$name"
    tar --create --format=pax --file=- --directory="$parent" --transform='flags=rh;s|^[^/]*|payload|' -- "$name"
fi
"#;

#[cfg(test)]
mod tests {
    use super::*;

    proptest::proptest! {
        #[test]
        fn pax_times_round_trip_signed_seconds_and_nanoseconds(
            seconds in -2_000_000_000_i64..2_000_000_000_i64,
            nanos in 0_u32..1_000_000_000_u32,
        ) {
            let seconds = if seconds < 0 {
                UNIX_EPOCH - Duration::from_secs(seconds.unsigned_abs())
            } else { UNIX_EPOCH + Duration::from_secs(seconds as u64) };
            let time = seconds + Duration::from_nanos(u64::from(nanos));
            proptest::prop_assert_eq!(parse_time(&touch_time(time)).unwrap(), time);
        }

        #[test]
        fn archive_payload_reader_matches_its_declared_frame_length(
            bytes in proptest::collection::vec(proptest::num::u8::ANY, 0..4096),
            declared in 0_usize..4096,
        ) {
            let mut reader = ExactReader { reader: bytes.as_slice(), remaining: declared as u64 };
            let mut actual = Vec::new();
            let result = reader.read_to_end(&mut actual);
            if declared <= bytes.len() {
                proptest::prop_assert!(result.is_ok());
                proptest::prop_assert_eq!(actual, bytes[..declared].to_vec());
            } else {
                proptest::prop_assert_eq!(result.unwrap_err().kind(), io::ErrorKind::UnexpectedEof);
                proptest::prop_assert_eq!(actual, bytes);
            }
        }
    }

    #[cfg(unix)]
    #[test]
    fn archive_round_trip_preserves_native_names_links_modes_and_fractional_times() {
        use std::os::unix::fs::PermissionsExt;

        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        let workspace = tempfile::tempdir_in(root).unwrap();
        let source = workspace.path().join("source");
        fs::create_dir_all(source.join("readonly-empty")).unwrap();
        let name = "file '[x] \\ 可莉\n";
        fs::write(source.join(name), b"archive payload").unwrap();
        std::os::unix::fs::symlink("missing target", source.join("dangling")).unwrap();
        let modified = UNIX_EPOCH - Duration::from_nanos(500_000_001);
        let time = filetime::FileTime::from_system_time(modified);
        for path in [&source, &source.join(name), &source.join("readonly-empty")] {
            filetime::set_file_mtime(path, time).unwrap();
        }
        filetime::set_symlink_file_times(source.join("dangling"), time, time).unwrap();
        fs::set_permissions(source.join(name), fs::Permissions::from_mode(0o751)).unwrap();
        fs::set_permissions(
            source.join("readonly-empty"),
            fs::Permissions::from_mode(0o500),
        )
        .unwrap();
        let mut archive = tar::Builder::new(Vec::new());
        append_tree(
            &mut archive,
            &source,
            Path::new("payload"),
            None,
            &mut ExecutionReport::default(),
        )
        .unwrap();
        let bytes = archive.into_inner().unwrap();
        let target = workspace.path().join("received");
        extract(&mut io::Cursor::new(bytes), &target).unwrap();
        assert_eq!(fs::read(target.join(name)).unwrap(), b"archive payload");
        assert_eq!(
            fs::metadata(target.join(name))
                .unwrap()
                .permissions()
                .mode()
                & 0o777,
            0o751
        );
        assert_eq!(
            fs::metadata(target.join("readonly-empty"))
                .unwrap()
                .permissions()
                .mode()
                & 0o777,
            0o500
        );
        assert_eq!(
            fs::read_link(target.join("dangling")).unwrap(),
            Path::new("missing target")
        );
        for path in [
            &target,
            &target.join(name),
            &target.join("readonly-empty"),
            &target.join("dangling"),
        ] {
            assert_eq!(
                fs::symlink_metadata(path).unwrap().modified().unwrap(),
                modified,
                "{}",
                path.display()
            );
        }
        for path in [source.join("readonly-empty"), target.join("readonly-empty")] {
            fs::set_permissions(path, fs::Permissions::from_mode(0o700)).unwrap();
        }
    }

    #[cfg(unix)]
    #[test]
    fn failed_archive_worker_stops_and_joins_a_waiting_client() {
        let mut command = Command::new("sleep");
        command.arg("120");
        let error = run_exchange::<()>(&mut command, &CancellationToken::new(), |_, _| {
            Err("fixture destination write failed".into())
        })
        .unwrap_err();
        assert!(error.contains("fixture destination write failed"));
    }
}
