//! Bounded one-way transfers between files and asynchronous stream endpoints.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};
#[cfg(unix)]
use std::pin::Pin;
use std::process::Stdio;
use std::sync::{Arc, Mutex};
#[cfg(unix)]
use std::task::{Context, Poll};

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio_util::sync::CancellationToken;

use crate::compiler::{Backend, CopyOp, Location};
use crate::runtime::RuntimeError;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StreamEnd {
    SourceClosed,
    DestinationClosed,
    Cancelled,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct StreamReport {
    pub bytes: u64,
    pub end: StreamEnd,
}

type Reader = Box<dyn AsyncRead + Unpin + Send>;
type Writer = Box<dyn AsyncWrite + Unpin + Send>;

struct Opened<T> {
    io: T,
    process: Option<RemoteProcess>,
    close: Option<CloseWatch>,
}

enum CloseWatch {
    Reader(Reader),
    #[cfg(windows)]
    NamedPipe(std::os::windows::io::OwnedHandle),
    #[cfg(unix)]
    Fifo(fs::File),
}

impl<T> Opened<T> {
    fn native(io: T) -> Self {
        Self {
            io,
            process: None,
            close: None,
        }
    }
}

#[derive(Debug)]
enum Endpoint {
    File(PathBuf),
    Tcp(String, u16),
    Remote(Location),
    Http(Location),
    #[cfg(windows)]
    NamedPipe(PathBuf),
    #[cfg(unix)]
    Fifo(PathBuf),
    #[cfg(unix)]
    Socket(PathBuf),
}

impl Endpoint {
    fn is_stream(&self) -> bool {
        !matches!(self, Self::File(_) | Self::Http(_))
    }
}

/// Transfers one bounded-buffer flow. Cancelling drops its endpoint handles.
/// No telemetry guard is held across an await: task context belongs to the scheduler.
pub async fn transfer(
    operation: &CopyOp,
    buffer_bytes: usize,
    cancel: CancellationToken,
) -> Result<StreamReport, RuntimeError> {
    let result = transfer_inner(operation, buffer_bytes, &cancel).await;
    result.map_err(|message| RuntimeError {
        message,
        span: operation.span,
    })
}

async fn transfer_inner(
    operation: &CopyOp,
    buffer_bytes: usize,
    cancel: &CancellationToken,
) -> Result<StreamReport, String> {
    if buffer_bytes == 0 {
        return Err("stream buffer size must be greater than zero".into());
    }
    if operation.pattern.is_some() {
        return Err("a stream transfer cannot contain a directory selector".into());
    }
    let source = endpoint(&operation.source, true)?;
    let (destination_location, no_clobber) = destination_location(operation)?;
    let destination = endpoint(&destination_location, false)?;
    if !source.is_stream() && !destination.is_stream() {
        return Err("stream transfer requires a pipe or socket endpoint".into());
    }
    let source = tokio::select! {
        biased;
        _ = cancel.cancelled() => return Ok(report(0, StreamEnd::Cancelled)),
        source = open_reader(source, buffer_bytes) => source.map_err(|error| format!("cannot open stream source: {error}"))?,
    };
    let destination = tokio::select! {
        biased;
        _ = cancel.cancelled() => return Ok(report(0, StreamEnd::Cancelled)),
        destination = open_writer(destination, no_clobber, buffer_bytes) => destination.map_err(|error| format!("cannot open stream destination: {error}"))?,
    };
    pump(source, destination, buffer_bytes, cancel).await
}

fn report(bytes: u64, end: StreamEnd) -> StreamReport {
    StreamReport { bytes, end }
}

fn destination_location(operation: &CopyOp) -> Result<(Location, bool), String> {
    if !operation.destination.path.ends_with('/')
        || matches!(operation.destination.backend, Backend::Tcp { .. })
    {
        return Ok((operation.destination.clone(), false));
    }
    let source = source_basename(&operation.source)?;
    let Some(directory) = crate::runtime::local_path(&operation.destination, false)? else {
        let mut destination = operation.destination.clone();
        destination.path.push_str(&source);
        return Ok((destination, true));
    };
    let source = Path::new(&source);
    let name = source
        .file_name()
        .ok_or("a stream without a basename needs an exact destination filename")?;
    let target = directory.join(name);
    if target.exists() {
        return Err("destination file already exists; choose a new name or remove the trailing '/' to overwrite".into());
    }
    Ok((
        Location {
            backend: Backend::Local,
            path: target.to_string_lossy().into_owned(),
        },
        true,
    ))
}

fn source_basename(source: &Location) -> Result<String, String> {
    if matches!(source.backend, Backend::Tcp { .. }) {
        return Err("an unnamed network stream needs an exact destination filename, not a directory container".into());
    }
    let path =
        crate::runtime::local_path(source, true)?.unwrap_or_else(|| PathBuf::from(&source.path));
    path.file_name()
        .and_then(|name| name.to_str())
        .map(str::to_owned)
        .ok_or_else(|| "a stream without a basename needs an exact destination filename".into())
}

fn endpoint(location: &Location, source: bool) -> Result<Endpoint, String> {
    if let Backend::Tcp { host, port } = &location.backend {
        return Ok(Endpoint::Tcp(host.clone(), *port));
    }
    if matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }) {
        return Ok(Endpoint::Remote(location.clone()));
    }
    if matches!(location.backend, Backend::Http { .. }) {
        if !source {
            return Err("HTTP(S) endpoints are read-only".into());
        }
        return Ok(Endpoint::Http(location.clone()));
    }
    let path = crate::runtime::local_path(location, source)?
        .ok_or("streaming requires a native local pipe/socket or a TCP endpoint")?;
    #[cfg(windows)]
    if is_named_pipe_path(&path.to_string_lossy()) {
        return Ok(Endpoint::NamedPipe(path));
    }
    crate::runtime::check_ancestors(&path)?;
    let metadata = match fs::symlink_metadata(&path) {
        Ok(metadata) => Some(metadata),
        Err(error) if !source && error.kind() == io::ErrorKind::NotFound => None,
        Err(error) => return Err(format!("{}: {error}", path.display())),
    };
    if metadata.as_ref().is_some_and(|metadata| metadata.is_dir()) {
        return Err(
            "directories cannot be stream sources or destinations; name a file, pipe or socket"
                .into(),
        );
    }
    #[cfg(unix)]
    if let Some(metadata) = &metadata {
        use std::os::unix::fs::FileTypeExt;
        if metadata.file_type().is_fifo() {
            return Ok(Endpoint::Fifo(path));
        }
        if metadata.file_type().is_socket() {
            return Ok(Endpoint::Socket(path));
        }
    }
    if metadata
        .as_ref()
        .is_some_and(|metadata| !metadata.is_file())
    {
        return Err("unsupported native stream endpoint; expected a FIFO or stream socket".into());
    }
    if !source && location.path.ends_with('/') {
        return Err("stream output needs an exact file path, not a container ending in '/'".into());
    }
    Ok(Endpoint::File(path))
}

pub fn is_named_pipe_path(path: &str) -> bool {
    let normalized = path.replace('/', "\\");
    normalized.to_ascii_lowercase().starts_with(r"\\.\pipe\")
}

async fn open_reader(endpoint: Endpoint, buffer_bytes: usize) -> io::Result<Opened<Reader>> {
    if let Endpoint::Http(location) = endpoint {
        let command = crate::runtime::http_command_builder(&location).map_err(io::Error::other)?;
        let mut process = RemoteProcess::capture(command)?;
        let io: Reader = Box::new(
            process
                .child
                .stdout
                .take()
                .ok_or_else(|| io::Error::other("HTTP stdout is unavailable"))?,
        );
        return Ok(Opened {
            io,
            process: Some(process),
            close: None,
        });
    }
    if let Endpoint::Remote(location) = endpoint {
        let mut process = RemoteProcess::open(&location, "r", buffer_bytes).await?;
        let io: Reader = Box::new(
            process
                .child
                .stdout
                .take()
                .ok_or_else(|| io::Error::other("remote stdout is unavailable"))?,
        );
        return Ok(Opened {
            io,
            process: Some(process),
            close: None,
        });
    }
    open_native_reader(endpoint, buffer_bytes)
        .await
        .map(Opened::native)
}

async fn open_native_reader(endpoint: Endpoint, buffer_bytes: usize) -> io::Result<Reader> {
    match endpoint {
        Endpoint::File(path) => {
            let mut file = tokio::fs::File::open(path).await?;
            file.set_max_buf_size(buffer_bytes);
            Ok(Box::new(file))
        }
        Endpoint::Tcp(host, port) => {
            let stream = tokio::net::TcpStream::connect((host.as_str(), port)).await?;
            let (reader, _) = stream.into_split();
            Ok(Box::new(reader))
        }
        Endpoint::Remote(_) | Endpoint::Http(_) => {
            unreachable!("child reader is handled before native dispatch")
        }
        #[cfg(windows)]
        Endpoint::NamedPipe(path) => Ok(Box::new(open_named_pipe(&path, true).await?)),
        #[cfg(unix)]
        Endpoint::Fifo(path) => Ok(Box::new(Fifo::reader(&path)?)),
        #[cfg(unix)]
        Endpoint::Socket(path) => {
            let stream = tokio::net::UnixStream::connect(path).await?;
            let (reader, _) = stream.into_split();
            Ok(Box::new(reader))
        }
    }
}

async fn open_writer(
    endpoint: Endpoint,
    no_clobber: bool,
    buffer_bytes: usize,
) -> io::Result<Opened<Writer>> {
    if let Endpoint::Tcp(host, port) = endpoint {
        let stream = tokio::net::TcpStream::connect((host.as_str(), port)).await?;
        let (reader, writer) = stream.into_split();
        return Ok(Opened {
            io: Box::new(writer),
            process: None,
            close: Some(CloseWatch::Reader(Box::new(reader))),
        });
    }
    #[cfg(unix)]
    if let Endpoint::Socket(path) = endpoint {
        let stream = tokio::net::UnixStream::connect(path).await?;
        let (reader, writer) = stream.into_split();
        return Ok(Opened {
            io: Box::new(writer),
            process: None,
            close: Some(CloseWatch::Reader(Box::new(reader))),
        });
    }
    #[cfg(windows)]
    if let Endpoint::NamedPipe(path) = endpoint {
        use std::os::windows::io::AsHandle;
        let client = open_named_pipe(&path, false).await?;
        let watch = client.as_handle().try_clone_to_owned()?;
        return Ok(Opened {
            io: Box::new(client),
            process: None,
            close: Some(CloseWatch::NamedPipe(watch)),
        });
    }
    #[cfg(unix)]
    if let Endpoint::Fifo(path) = endpoint {
        loop {
            match Fifo::writer(&path) {
                Ok(writer) => {
                    let watch = writer.0.get_ref().try_clone()?;
                    return Ok(Opened {
                        io: Box::new(writer),
                        process: None,
                        close: Some(CloseWatch::Fifo(watch)),
                    });
                }
                Err(error) if error.raw_os_error() == Some(libc::ENXIO) => {
                    tokio::time::sleep(std::time::Duration::from_millis(10)).await
                }
                Err(error) => return Err(error),
            }
        }
    }
    if let Endpoint::Remote(location) = endpoint {
        let mut process =
            RemoteProcess::open(&location, if no_clobber { "x" } else { "w" }, buffer_bytes)
                .await?;
        let io: Writer = Box::new(
            process
                .child
                .stdin
                .take()
                .ok_or_else(|| io::Error::other("remote stdin is unavailable"))?,
        );
        return Ok(Opened {
            io,
            process: Some(process),
            close: None,
        });
    }
    open_native_writer(endpoint, no_clobber, buffer_bytes)
        .await
        .map(Opened::native)
}

async fn open_native_writer(
    endpoint: Endpoint,
    no_clobber: bool,
    buffer_bytes: usize,
) -> io::Result<Writer> {
    match endpoint {
        Endpoint::File(path) => {
            if let Some(parent) = path
                .parent()
                .filter(|parent| !parent.as_os_str().is_empty())
            {
                tokio::fs::create_dir_all(parent).await?;
            }
            let mut file = tokio::fs::OpenOptions::new()
                .write(true)
                .create(!no_clobber)
                .create_new(no_clobber)
                .truncate(!no_clobber)
                .open(path)
                .await?;
            file.set_max_buf_size(buffer_bytes);
            Ok(Box::new(file))
        }
        Endpoint::Tcp(host, port) => {
            let stream = tokio::net::TcpStream::connect((host.as_str(), port)).await?;
            let (_, writer) = stream.into_split();
            Ok(Box::new(writer))
        }
        Endpoint::Remote(_) | Endpoint::Http(_) => {
            unreachable!("remote writer is handled before native dispatch")
        }
        #[cfg(windows)]
        Endpoint::NamedPipe(path) => Ok(Box::new(open_named_pipe(&path, false).await?)),
        #[cfg(unix)]
        Endpoint::Fifo(path) => loop {
            match Fifo::writer(&path) {
                Ok(writer) => return Ok(Box::new(writer)),
                Err(error) if error.raw_os_error() == Some(libc::ENXIO) => {
                    tokio::time::sleep(std::time::Duration::from_millis(10)).await;
                }
                Err(error) => return Err(error),
            }
        },
        #[cfg(unix)]
        Endpoint::Socket(path) => {
            let stream = tokio::net::UnixStream::connect(path).await?;
            let (_, writer) = stream.into_split();
            Ok(Box::new(writer))
        }
    }
}

#[cfg(windows)]
async fn open_named_pipe(
    path: &Path,
    source: bool,
) -> io::Result<tokio::net::windows::named_pipe::NamedPipeClient> {
    let path = path.to_string_lossy().replace('/', "\\");
    loop {
        match tokio::net::windows::named_pipe::ClientOptions::new()
            .read(source)
            .write(!source)
            .open(&path)
        {
            Ok(pipe) => return Ok(pipe),
            Err(error) if error.raw_os_error() == Some(231) => {
                tokio::time::sleep(std::time::Duration::from_millis(10)).await;
            }
            Err(error) => return Err(error),
        }
    }
}

async fn pump(
    mut source: Opened<Reader>,
    mut destination: Opened<Writer>,
    buffer_bytes: usize,
    cancel: &CancellationToken,
) -> Result<StreamReport, String> {
    let mut buffer = vec![0_u8; buffer_bytes];
    let mut bytes = 0_u64;
    loop {
        let read = tokio::select! {
            biased;
            _ = cancel.cancelled() => return Ok(report(bytes, StreamEnd::Cancelled)),
            result = destination_exit(&mut destination.process, &mut destination.close), if destination.process.is_some() || destination.close.is_some() => {
                result?;
                return Ok(report(bytes, StreamEnd::DestinationClosed));
            }
            result = source.io.read(&mut buffer) => result,
        };
        let count = match read {
            Ok(0) => {
                if !wait_process(&mut source.process, cancel).await? {
                    return Ok(report(bytes, StreamEnd::Cancelled));
                }
                return finish(destination, bytes, cancel).await;
            }
            Ok(count) => count,
            Err(error) if disconnected(&error) => {
                if !wait_process(&mut source.process, cancel).await? {
                    return Ok(report(bytes, StreamEnd::Cancelled));
                }
                return finish(destination, bytes, cancel).await;
            }
            Err(error) => return Err(format!("cannot read stream source: {error}")),
        };
        let mut offset = 0;
        while offset < count {
            let write = tokio::select! {
                biased;
                _ = cancel.cancelled() => return Ok(report(bytes, StreamEnd::Cancelled)),
                result = destination.io.write(&buffer[offset..count]) => result,
            };
            match write {
                Ok(0) => {
                    wait_process(&mut destination.process, cancel).await?;
                    return Ok(report(bytes, StreamEnd::DestinationClosed));
                }
                Ok(written) => {
                    offset += written;
                    bytes += written as u64;
                }
                Err(error) if disconnected(&error) => {
                    wait_process(&mut destination.process, cancel).await?;
                    return Ok(report(bytes, StreamEnd::DestinationClosed));
                }
                Err(error) => return Err(format!("cannot write stream destination: {error}")),
            }
        }
    }
}

async fn finish(
    mut destination: Opened<Writer>,
    bytes: u64,
    cancel: &CancellationToken,
) -> Result<StreamReport, String> {
    let result = tokio::select! {
        biased;
        _ = cancel.cancelled() => return Ok(report(bytes, StreamEnd::Cancelled)),
        result = async { destination.io.flush().await?; destination.io.shutdown().await } => result,
    };
    match result {
        Ok(()) => {
            destination.io = Box::new(tokio::io::sink());
            let end = if wait_process(&mut destination.process, cancel).await? {
                StreamEnd::SourceClosed
            } else {
                StreamEnd::Cancelled
            };
            Ok(report(bytes, end))
        }
        Err(error) if disconnected(&error) => {
            wait_process(&mut destination.process, cancel).await?;
            Ok(report(bytes, StreamEnd::DestinationClosed))
        }
        Err(error) => Err(format!("cannot finish stream destination: {error}")),
    }
}

async fn destination_exit(
    process: &mut Option<RemoteProcess>,
    watcher: &mut Option<CloseWatch>,
) -> Result<(), String> {
    match process {
        Some(process) => process.wait().await,
        None => match watcher {
            Some(CloseWatch::Reader(reader)) => {
                let mut byte = [0_u8; 1];
                loop {
                    match reader.read(&mut byte).await {
                        Ok(0) => return Ok(()),
                        Ok(_) => {}
                        Err(error) if disconnected(&error) => return Ok(()),
                        Err(error) => {
                            return Err(format!("cannot monitor destination closure: {error}"));
                        }
                    }
                }
            }
            #[cfg(windows)]
            Some(CloseWatch::NamedPipe(handle)) => loop {
                if named_pipe_closed(handle)
                    .map_err(|error| format!("cannot inspect named pipe closure: {error}"))?
                {
                    return Ok(());
                }
                tokio::time::sleep(std::time::Duration::from_millis(20)).await;
            },
            #[cfg(unix)]
            Some(CloseWatch::Fifo(file)) => loop {
                if fifo_closed(file)
                    .map_err(|error| format!("cannot inspect FIFO closure: {error}"))?
                {
                    return Ok(());
                }
                tokio::time::sleep(std::time::Duration::from_millis(20)).await;
            },
            None => std::future::pending().await,
        },
    }
}

async fn wait_process(
    process: &mut Option<RemoteProcess>,
    cancel: &CancellationToken,
) -> Result<bool, String> {
    let Some(process) = process else {
        return Ok(true);
    };
    tokio::select! {
        biased;
        _ = cancel.cancelled() => Ok(false),
        result = process.wait() => result.map(|()| true),
    }
}

#[cfg(windows)]
fn named_pipe_closed(handle: &std::os::windows::io::OwnedHandle) -> io::Result<bool> {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Wdk::Storage::FileSystem::{
        FILE_PIPE_CONNECTED_STATE, FILE_PIPE_LOCAL_INFORMATION, FilePipeLocalInformation,
        NtQueryInformationFile,
    };
    use windows_sys::Win32::Foundation::{
        RtlNtStatusToDosError, STATUS_PIPE_BROKEN, STATUS_PIPE_CLOSING, STATUS_PIPE_DISCONNECTED,
    };
    use windows_sys::Win32::System::IO::IO_STATUS_BLOCK;
    let mut information = FILE_PIPE_LOCAL_INFORMATION::default();
    let mut completion = IO_STATUS_BLOCK::default();
    // SAFETY: The independently owned handle remains valid throughout this query;
    // initialized, correctly sized outputs stay alive through this synchronous
    // API, including on overlapped handles (IRP_SYNCHRONOUS_API contract):
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ioisoperationsynchronous
    let status = unsafe {
        NtQueryInformationFile(
            handle.as_raw_handle(),
            &mut completion,
            (&mut information as *mut FILE_PIPE_LOCAL_INFORMATION).cast(),
            std::mem::size_of::<FILE_PIPE_LOCAL_INFORMATION>() as u32,
            FilePipeLocalInformation,
        )
    };
    if matches!(
        status,
        STATUS_PIPE_BROKEN | STATUS_PIPE_CLOSING | STATUS_PIPE_DISCONNECTED
    ) {
        return Ok(true);
    }
    if status < 0 {
        // SAFETY: Conversion takes an NTSTATUS value and no pointers.
        return Err(io::Error::from_raw_os_error(
            unsafe { RtlNtStatusToDosError(status) } as i32,
        ));
    }
    Ok(information.NamedPipeState != FILE_PIPE_CONNECTED_STATE)
}

#[cfg(unix)]
fn fifo_closed(file: &fs::File) -> io::Result<bool> {
    use std::os::fd::AsRawFd;
    let mut event = libc::pollfd {
        fd: file.as_raw_fd(),
        events: libc::POLLERR | libc::POLLHUP,
        revents: 0,
    };
    // SAFETY: One initialized pollfd references a live owned File; timeout is zero.
    let count = unsafe { libc::poll(&mut event, 1, 0) };
    if count < 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(event.revents & (libc::POLLERR | libc::POLLHUP) != 0)
}

struct RemoteProcess {
    child: tokio::process::Child,
    stderr: Arc<Mutex<Vec<u8>>>,
    drain: tokio::task::JoinHandle<()>,
}

impl RemoteProcess {
    fn capture(command: std::process::Command) -> io::Result<Self> {
        let mut command = tokio::process::Command::from(command);
        command
            .kill_on_drop(true)
            .stdin(Stdio::null())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        let mut child = command.spawn()?;
        let stderr = child
            .stderr
            .take()
            .ok_or_else(|| io::Error::other("child stderr is unavailable"))?;
        let captured = Arc::new(Mutex::new(Vec::new()));
        let drain = tokio::spawn(drain_remote_stderr(stderr, Arc::clone(&captured), None));
        Ok(Self {
            child,
            stderr: captured,
            drain,
        })
    }
    async fn open(location: &Location, mode: &str, buffer_bytes: usize) -> io::Result<Self> {
        let command = crate::runtime::remote_command_builder(
            &location.backend,
            "exec python3 -u -c \"$1\" \"$2\" \"$3\" \"$4\"",
            &[
                REMOTE_PYTHON,
                &location.path,
                mode,
                &buffer_bytes.to_string(),
            ],
        )
        .map_err(io::Error::other)?;
        let mut command = tokio::process::Command::from(command);
        command
            .kill_on_drop(true)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        let mut child = command.spawn()?;
        let stderr = child
            .stderr
            .take()
            .ok_or_else(|| io::Error::other("remote stderr is unavailable"))?;
        let captured = Arc::new(Mutex::new(Vec::new()));
        let (ready, receive) = tokio::sync::oneshot::channel();
        let drain = tokio::spawn(drain_remote_stderr(
            stderr,
            Arc::clone(&captured),
            Some(ready),
        ));
        let process = Self {
            child,
            stderr: captured,
            drain,
        };
        match receive.await {
            Ok(()) => Ok(process),
            Err(_) => Err(io::Error::other(format!(
                "remote stream did not start; install Python 3 on the endpoint: {}",
                process.error_text()
            ))),
        }
    }

    fn error_text(&self) -> String {
        String::from_utf8_lossy(
            &self
                .stderr
                .lock()
                .unwrap_or_else(|error| error.into_inner()),
        )
        .trim()
        .to_owned()
    }

    async fn wait(&mut self) -> Result<(), String> {
        let status = self.child.wait().await.map_err(|error| error.to_string())?;
        if status.success() {
            return Ok(());
        }
        let _ = (&mut self.drain).await;
        Err(format!(
            "remote stream helper failed ({status}): {}",
            self.error_text()
        ))
    }
}

impl Drop for RemoteProcess {
    fn drop(&mut self) {
        self.drain.abort();
        let _ = self.child.start_kill();
    }
}

const REMOTE_PYTHON: &str = r#"
from __future__ import annotations
import errno
import os
import select
import socket
import stat
import sys
import time

def channel_closed(source: bool) -> bool:
    channel: int = 0 if source else 1
    watcher: select.poll = select.poll()
    watcher.register(channel, select.POLLHUP | select.POLLERR | (select.POLLIN if source else 0))
    events: list[tuple[int, int]] = watcher.poll(0)
    if not events:
        return False
    if not source or events[0][1] & (select.POLLHUP | select.POLLERR):
        return True
    try:
        return os.read(0, 1) == b''
    except BlockingIOError:
        return False

def ready(fd: int, interest: int, source: bool) -> bool:
    watcher: select.poll = select.poll()
    watcher.register(fd, interest | select.POLLHUP | select.POLLERR)
    watcher.register(0 if source else 1, select.POLLHUP | select.POLLERR | (select.POLLIN if source else 0))
    events: list[tuple[int, int]] = watcher.poll(100)
    return any(number == fd for number, _ in events)

def safe_path(raw: str) -> str:
    expanded: str = os.path.expanduser(raw)
    current: str = expanded if os.path.isabs(expanded) else os.path.join(os.getcwd(), expanded)
    while current != os.path.dirname(current):
        if os.path.islink(current):
            raise ValueError('refusing symbolic link in stream path')
        current = os.path.dirname(current)
    return os.path.abspath(expanded)

class Endpoint:
    path: str
    mode: str
    kind: str
    fd: int | None
    connection: socket.socket | None

    def __init__(self, path: str, mode: str) -> None:
        self.path = safe_path(path)
        self.mode = mode
        self.fd = None
        self.connection = None
        metadata: os.stat_result | None = None
        try:
            metadata = os.stat(self.path, follow_symlinks=False)
        except FileNotFoundError:
            if mode == 'r':
                raise
        if metadata is not None and mode == 'x':
            raise FileExistsError('destination file already exists')
        if metadata is None or stat.S_ISREG(metadata.st_mode):
            self.kind = 'file'
        elif stat.S_ISFIFO(metadata.st_mode):
            self.kind = 'fifo'
        elif stat.S_ISSOCK(metadata.st_mode):
            self.kind = 'socket'
        elif stat.S_ISDIR(metadata.st_mode):
            raise ValueError('directories cannot participate in a stream transfer')
        else:
            raise ValueError('expected a regular file, FIFO or Unix stream socket')

    def open(self) -> bool:
        if self.fd is not None:
            return True
        if self.kind == 'socket':
            if self.connection is None:
                self.connection = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                self.connection.setblocking(False)
                result: int = self.connection.connect_ex(self.path)
                if result not in (0, errno.EINPROGRESS, errno.EALREADY, errno.EWOULDBLOCK):
                    raise OSError(result, os.strerror(result))
                if result == 0:
                    self.fd = self.connection.fileno()
                    return True
            watcher: select.poll = select.poll()
            watcher.register(self.connection.fileno(), select.POLLOUT | select.POLLERR | select.POLLHUP)
            if not watcher.poll(0):
                return False
            result = self.connection.getsockopt(socket.SOL_SOCKET, socket.SO_ERROR)
            if result:
                raise OSError(result, os.strerror(result))
            self.fd = self.connection.fileno()
            return True
        flags: int = os.O_NONBLOCK | os.O_CLOEXEC | (os.O_RDONLY if self.mode == 'r' else os.O_WRONLY)
        if self.kind == 'file' and self.mode != 'r':
            os.makedirs(os.path.dirname(self.path), exist_ok=True)
            flags |= os.O_CREAT | (os.O_EXCL if self.mode == 'x' else os.O_TRUNC)
        try:
            self.fd = os.open(self.path, flags, 0o666)
            return True
        except OSError as error:
            if self.kind == 'fifo' and error.errno == errno.ENXIO:
                return False
            raise

    def closed_writer(self) -> bool:
        if self.fd is None or self.kind == 'file':
            return False
        watcher: select.poll = select.poll()
        watcher.register(self.fd, select.POLLERR | select.POLLHUP | (select.POLLIN if self.kind == 'socket' else 0))
        events: list[tuple[int, int]] = watcher.poll(0)
        if not events:
            return False
        if events[0][1] & (select.POLLERR | select.POLLHUP):
            return True
        for _ in range(256):
            try:
                if os.read(self.fd, 1) == b'':
                    return True
            except BlockingIOError:
                return False
        return False

    def close(self) -> None:
        if self.connection is not None:
            self.connection.close()
        elif self.fd is not None:
            os.close(self.fd)

def read_flow(endpoint: Endpoint, capacity: int) -> None:
    pending: bytes = b''
    offset: int = 0
    while not channel_closed(True):
        if pending:
            if not ready(1, select.POLLOUT, True):
                continue
            try:
                offset += os.write(1, memoryview(pending)[offset:])
            except BlockingIOError:
                continue
            if offset == len(pending):
                pending = b''
                offset = 0
        elif endpoint.fd is not None and ready(endpoint.fd, select.POLLIN, True):
            try:
                pending = os.read(endpoint.fd, capacity)
            except BlockingIOError:
                continue
            if not pending:
                return

def write_flow(endpoint: Endpoint, capacity: int) -> None:
    pending: bytes = b''
    offset: int = 0
    eof: bool = False
    while not channel_closed(False):
        if not pending and not eof and ready(0, select.POLLIN, False):
            try:
                pending = os.read(0, capacity)
                eof = not pending
            except BlockingIOError:
                pass
        if not endpoint.open():
            if eof and not pending:
                return
            time.sleep(0.01)
            continue
        if endpoint.closed_writer():
            return
        if pending and endpoint.fd is not None and ready(endpoint.fd, select.POLLOUT, False):
            try:
                offset += os.write(endpoint.fd, memoryview(pending)[offset:])
            except BlockingIOError:
                continue
            if offset == len(pending):
                pending = b''
                offset = 0
        if eof and not pending:
            return

def main() -> int:
    path: str = sys.argv[1]
    mode: str = sys.argv[2]
    capacity: int = int(sys.argv[3])
    if capacity <= 0:
        raise ValueError('stream buffer must be positive')
    endpoint: Endpoint = Endpoint(path, mode)
    source: bool = mode == 'r'
    os.set_blocking(0, False)
    os.set_blocking(1, False)
    try:
        if source:
            while not endpoint.open():
                if channel_closed(True):
                    return 0
                time.sleep(0.01)
        sys.stderr.write('\nMSYNC_STREAM_READY\n')
        sys.stderr.flush()
        if source:
            read_flow(endpoint, capacity)
        else:
            write_flow(endpoint, capacity)
        return 0
    finally:
        endpoint.close()

try:
    raise SystemExit(main())
except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
    raise SystemExit(0)
except Exception as error:
    try:
        sys.stderr.write('msync remote stream: ' + str(error) + '\n')
    except OSError:
        pass
    raise SystemExit(1)
"#;

async fn drain_remote_stderr(
    mut stderr: tokio::process::ChildStderr,
    captured: Arc<Mutex<Vec<u8>>>,
    ready: Option<tokio::sync::oneshot::Sender<()>>,
) {
    let mut ready = ready;
    let mut line = Vec::new();
    let mut bytes = [0_u8; 512];
    loop {
        let Ok(count) = stderr.read(&mut bytes).await else {
            return;
        };
        if count == 0 {
            return;
        }
        for byte in &bytes[..count] {
            if *byte == b'\n' {
                if line == b"MSYNC_STREAM_READY" {
                    if let Some(ready) = ready.take() {
                        let _ = ready.send(());
                    }
                } else {
                    let mut output = captured.lock().unwrap_or_else(|error| error.into_inner());
                    let remaining = 8192_usize.saturating_sub(output.len());
                    output.extend_from_slice(&line[..line.len().min(remaining)]);
                    if output.len() < 8192 {
                        output.push(b'\n');
                    }
                }
                line.clear();
            } else if line.len() < 8192 {
                line.push(*byte);
            }
        }
    }
}

fn disconnected(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::BrokenPipe
            | io::ErrorKind::ConnectionReset
            | io::ErrorKind::ConnectionAborted
            | io::ErrorKind::NotConnected
            | io::ErrorKind::UnexpectedEof
    )
}

#[cfg(unix)]
struct Fifo(tokio::io::unix::AsyncFd<std::fs::File>);

#[cfg(unix)]
impl Fifo {
    fn reader(path: &Path) -> io::Result<Self> {
        use std::os::unix::fs::OpenOptionsExt;
        let file = fs::OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_NONBLOCK | libc::O_CLOEXEC)
            .open(path)?;
        // SAFETY: This module exclusively owns File and never exposes or replaces
        // its descriptor; it stays open and stable until AsyncFd drops it.
        Ok(Self(
            unsafe { tokio::io::unix::AsyncFd::register(file) }.map_err(io::Error::from)?,
        ))
    }

    fn writer(path: &Path) -> io::Result<Self> {
        use std::os::unix::fs::OpenOptionsExt;
        let file = fs::OpenOptions::new()
            .write(true)
            .custom_flags(libc::O_NONBLOCK | libc::O_CLOEXEC)
            .open(path)?;
        // SAFETY: The owned File's stable descriptor never escapes this module.
        Ok(Self(
            unsafe { tokio::io::unix::AsyncFd::register(file) }.map_err(io::Error::from)?,
        ))
    }
}

#[cfg(unix)]
impl AsyncRead for Fifo {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buffer: &mut tokio::io::ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        use std::io::Read;
        loop {
            let mut ready = std::task::ready!(self.0.poll_read_ready(cx))?;
            match ready.try_io(|inner| inner.get_ref().read(buffer.initialize_unfilled())) {
                Ok(Ok(count)) => {
                    buffer.advance(count);
                    return Poll::Ready(Ok(()));
                }
                Ok(Err(error)) => return Poll::Ready(Err(error)),
                Err(_) => continue,
            }
        }
    }
}

#[cfg(unix)]
impl AsyncWrite for Fifo {
    fn poll_write(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buffer: &[u8],
    ) -> Poll<io::Result<usize>> {
        use std::io::Write;
        loop {
            let mut ready = std::task::ready!(self.0.poll_write_ready(cx))?;
            match ready.try_io(|inner| inner.get_ref().write(buffer)) {
                Ok(result) => return Poll::Ready(result),
                Err(_) => continue,
            }
        }
    }
    fn poll_flush(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<io::Result<()>> {
        Poll::Ready(Ok(()))
    }
    fn poll_shutdown(self: Pin<&mut Self>, _: &mut Context<'_>) -> Poll<io::Result<()>> {
        Poll::Ready(Ok(()))
    }
}
