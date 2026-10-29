//! Bounded one-way transfers between files and asynchronous stream endpoints.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};
#[cfg(unix)]
use std::pin::Pin;
use std::process::Stdio;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
#[cfg(unix)]
use std::task::{Context, Poll};

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};
use tokio_util::sync::CancellationToken;

use crate::compiler::{Backend, CopyOp, Location};
use crate::runtime::RuntimeError;

pub use crate::io::{FlowEnd as StreamEnd, FlowReport as StreamReport};

type Reader = Box<dyn AsyncRead + Unpin + Send>;
type Writer = Box<dyn AsyncWrite + Unpin + Send>;

#[derive(Clone, Debug)]
pub(crate) struct FifoIdentity {
    device: u64,
    inode: u64,
    // A metadata-only native handle pins this inode generation without opening
    // a FIFO reader/writer. Keep it until the actual IO descriptor is verified.
    _lease: Option<Arc<fs::File>>,
}

impl PartialEq for FifoIdentity {
    fn eq(&self, other: &Self) -> bool {
        self.device == other.device && self.inode == other.inode
    }
}

impl Eq for FifoIdentity {}

impl FifoIdentity {
    pub(crate) fn require_generation_pin(&self) -> io::Result<()> {
        if self._lease.is_some() {
            return Ok(());
        }
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "the native backend cannot retain a metadata-only FIFO generation lease",
        ))
    }

    pub(crate) fn native_id(&self) -> String {
        format!("dev={}:ino={}", self.device, self.inode)
    }

    #[cfg(unix)]
    fn verify(&self, file: &fs::File) -> io::Result<()> {
        self.verify_metadata(&file.metadata()?)
    }

    #[cfg(unix)]
    fn verify_metadata(&self, metadata: &fs::Metadata) -> io::Result<()> {
        use std::os::unix::fs::{FileTypeExt, MetadataExt};
        if !metadata.file_type().is_fifo()
            || metadata.dev() != self.device
            || metadata.ino() != self.inode
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                format!(
                    "FIFO identity changed before opening: expected {}; actual dev={} ino={} fifo={}",
                    self.native_id(),
                    metadata.dev(),
                    metadata.ino(),
                    metadata.file_type().is_fifo()
                ),
            ));
        }
        Ok(())
    }
}

pub(crate) fn fifo_identity(path: &Path) -> io::Result<Option<FifoIdentity>> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::{FileTypeExt, MetadataExt};
        let metadata = fs::symlink_metadata(path)?;
        if !metadata.file_type().is_fifo() {
            return Ok(None);
        }
        #[cfg(any(target_os = "linux", target_os = "android", target_os = "freebsd"))]
        let (metadata, lease) = {
            use std::os::unix::fs::OpenOptionsExt;
            let mode = libc::O_PATH;
            let file = fs::OpenOptions::new()
                .read(true)
                .custom_flags(mode | libc::O_NOFOLLOW | libc::O_CLOEXEC)
                .open(path)?;
            (file.metadata()?, Some(Arc::new(file)))
        };
        #[cfg(not(any(target_os = "linux", target_os = "android", target_os = "freebsd")))]
        let lease = None;
        Ok(metadata.file_type().is_fifo().then_some(FifoIdentity {
            device: metadata.dev(),
            inode: metadata.ino(),
            _lease: lease,
        }))
    }
    #[cfg(not(unix))]
    {
        let _ = path;
        Ok(None)
    }
}

#[derive(Clone, Debug, Default)]
pub(crate) struct FifoPins {
    pub source: Option<FifoIdentity>,
    pub destination: Option<FifoIdentity>,
}

impl FifoPins {
    #[cfg(test)]
    pub(crate) fn any(&self) -> bool {
        self.source.is_some() || self.destination.is_some()
    }
}

struct Opened<T> {
    io: T,
    process: Option<RemoteProcess>,
    close: Option<CloseWatch>,
    file: Option<crate::io::FileFinish>,
}

enum CloseWatch {
    Reader(Reader),
    Consumer(CancellationToken),
    #[cfg(windows)]
    NamedPipe(std::os::windows::io::OwnedHandle),
    #[cfg(target_os = "linux")]
    FifoReady(tokio::io::unix::AsyncFd<fs::File>),
    #[cfg(all(unix, not(target_os = "linux")))]
    Fifo(fs::File),
}

impl<T> Opened<T> {
    fn native(io: T) -> Self {
        Self {
            io,
            process: None,
            close: None,
            file: None,
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
    Fifo(PathBuf, FifoIdentity),
    #[cfg(unix)]
    Socket(PathBuf),
}

/// Transfers one bounded-buffer flow. Cancelling drops its endpoint handles.
/// No telemetry guard is held across an await: task context belongs to the scheduler.
pub async fn transfer(
    operation: &CopyOp,
    buffer_bytes: usize,
    cancel: CancellationToken,
) -> Result<StreamReport, RuntimeError> {
    transfer_with_context(operation, buffer_bytes, cancel, crate::telemetry::context()).await
}

pub async fn transfer_with_context(
    operation: &CopyOp,
    buffer_bytes: usize,
    cancel: CancellationToken,
    context: Option<crate::telemetry::TraceContext>,
) -> Result<StreamReport, RuntimeError> {
    transfer_with_fifo_pins(
        operation,
        buffer_bytes,
        cancel,
        context,
        FifoPins::default(),
    )
    .await
}

pub(crate) async fn transfer_with_fifo_pins(
    operation: &CopyOp,
    buffer_bytes: usize,
    cancel: CancellationToken,
    context: Option<crate::telemetry::TraceContext>,
    pins: FifoPins,
) -> Result<StreamReport, RuntimeError> {
    let mut trace = crate::telemetry::linked_span("io.flow", context.as_ref());
    trace.expect_completion();
    trace.attribute_lazy("source", || operation.source.to_string());
    trace.attribute_lazy("destination", || operation.destination.to_string());
    let result = transfer_inner(operation, buffer_bytes, &cancel, trace.context(), pins).await;
    if let Ok(report) = &result {
        trace.metric("bytes", report.bytes);
        if report.end == StreamEnd::Cancelled {
            trace.cancelled();
        } else {
            trace.finish_result(&result);
        }
    } else {
        trace.finish_result(&result);
    }
    result.map_err(|message| RuntimeError {
        message,
        span: operation.span,
    })
}

/// Connect an already-owned source to a physical endpoint without replacing its
/// close watcher or remote commit protocol with a generic `copy` loop.
pub(crate) async fn transfer_from_reader<R>(
    source: R,
    destination: &Location,
    buffer_bytes: usize,
    cancel: CancellationToken,
    span: crate::syntax::Span,
    fifo: Option<FifoIdentity>,
) -> Result<StreamReport, RuntimeError>
where
    R: AsyncRead + Unpin + Send + 'static,
{
    let child = cancel.child_token();
    let _scope = FlowScope(child.clone());
    let result = async {
        if buffer_bytes == 0 {
            return Err("stream buffer size must be greater than zero".into());
        }
        let destination = endpoint_pinned(destination, false, fifo)?;
        let destination = tokio::select! {
            biased;
            () = child.cancelled() => return Ok(report(0, StreamEnd::Cancelled)),
            opened = open_writer(destination, false, buffer_bytes, crate::telemetry::context()) =>
                opened.map_err(|cause| format!("cannot open stream destination: {cause}"))?,
        };
        run_opened(
            Opened::native(Box::new(source) as Reader),
            destination,
            buffer_bytes,
            &child,
            crate::telemetry::context(),
        )
        .await
    }
    .await;
    result.map_err(|message| RuntimeError { message, span })
}

/// Retain the source endpoint/remote-process lifetime while writing into an
/// already-owned destination such as a program's stdin or a private file stage.
pub(crate) async fn transfer_to_writer<W>(
    source: &Location,
    destination: W,
    buffer_bytes: usize,
    cancel: CancellationToken,
    span: crate::syntax::Span,
    consumer_closed: CancellationToken,
    fifo: Option<FifoIdentity>,
) -> Result<StreamReport, RuntimeError>
where
    W: AsyncWrite + Unpin + Send + 'static,
{
    let child = cancel.child_token();
    let _scope = FlowScope(child.clone());
    let result = async {
        if buffer_bytes == 0 {
            return Err("stream buffer size must be greater than zero".into());
        }
        let source = endpoint_pinned(source, true, fifo)?;
        let source = tokio::select! {
            biased;
            () = child.cancelled() => return Ok(report(0, StreamEnd::Cancelled)),
            () = consumer_closed.cancelled() => return Ok(report(0, StreamEnd::DestinationClosed)),
            opened = open_reader(source, buffer_bytes, crate::telemetry::context()) =>
                opened.map_err(|cause| format!("cannot open stream source: {cause}"))?,
        };
        let mut destination = Opened::native(Box::new(destination) as Writer);
        destination.close = Some(CloseWatch::Consumer(consumer_closed));
        run_opened(
            source,
            destination,
            buffer_bytes,
            &child,
            crate::telemetry::context(),
        )
        .await
    }
    .await;
    result.map_err(|message| RuntimeError { message, span })
}

struct FlowScope(CancellationToken);

impl Drop for FlowScope {
    fn drop(&mut self) {
        self.0.cancel();
    }
}

async fn transfer_inner(
    operation: &CopyOp,
    buffer_bytes: usize,
    cancel: &CancellationToken,
    context: Option<crate::telemetry::TraceContext>,
    pins: FifoPins,
) -> Result<StreamReport, String> {
    if buffer_bytes == 0 {
        return Err("stream buffer size must be greater than zero".into());
    }
    if operation.pattern.is_some() {
        return Err("a stream transfer cannot contain a directory selector".into());
    }
    let source = endpoint_pinned(&operation.source, true, pins.source)?;
    let (destination_location, no_clobber) = destination_location(operation)?;
    let destination = endpoint_pinned(&destination_location, false, pins.destination)?;
    if let (Endpoint::File(source), Endpoint::File(destination)) = (&source, &destination)
        && destination.exists()
        && same_file::is_same_file(source, destination).map_err(|error| error.to_string())?
    {
        return Err("source and destination refer to the same file".into());
    }
    if let (Endpoint::File(path), Endpoint::Tcp(host, port)) = (&source, &destination) {
        return transfer_file_to_tcp(path, host, *port, buffer_bytes, cancel, context).await;
    }
    let source = tokio::select! {
        biased;
        _ = cancel.cancelled() => return Ok(report(0, StreamEnd::Cancelled)),
        source = open_reader(source, buffer_bytes, context.clone()) => source.map_err(|error| {
            if let Some(context) = &context { context.io_error("open.source", &error); }
            format!("cannot open stream source: {error}")
        })?,
    };
    let destination = tokio::select! {
        biased;
        _ = cancel.cancelled() => return Ok(report(0, StreamEnd::Cancelled)),
        destination = open_writer(destination, no_clobber, buffer_bytes, context.clone()) => destination.map_err(|error| {
            if let Some(context) = &context { context.io_error("open.destination", &error); }
            format!("cannot open stream destination: {error}")
        })?,
    };
    run_opened(source, destination, buffer_bytes, cancel, context).await
}

async fn transfer_file_to_tcp(
    path: &Path,
    host: &str,
    port: u16,
    buffer_bytes: usize,
    cancel: &CancellationToken,
    context: Option<crate::telemetry::TraceContext>,
) -> Result<StreamReport, String> {
    use std::io::{Seek, SeekFrom};
    let path = path.to_owned();
    let mut file = tokio::select! {
        biased;
        () = cancel.cancelled() => return Ok(report(0, StreamEnd::Cancelled)),
        opened = tokio::task::spawn_blocking(move || fs::File::open(path)) => opened.map_err(|error| error.to_string())?.map_err(|error| error.to_string())?,
    };
    let socket = tokio::select! {
        biased;
        () = cancel.cancelled() => return Ok(report(0, StreamEnd::Cancelled)),
        connected = tokio::net::TcpStream::connect((host, port)) => connected.map_err(|error| error.to_string())?,
    };
    // The source was opened here, so its initial cursor is known without a seek.
    let initial = 0;
    let progress = if crate::io::prefer_kernel_file_to_tcp(&file, initial)
        .map_err(|error| error.to_string())?
    {
        crate::io::file_to_tcp_with_context(&file, &socket, buffer_bytes, cancel, context.clone())
            .await
            .map_err(|error| error.to_string())?
    } else {
        crate::io::KernelProgress {
            bytes: 0,
            end: None,
        }
    };
    if let Some(end) = progress.end {
        return Ok(report(progress.bytes, end));
    }
    file.seek(SeekFrom::Start(initial.saturating_add(progress.bytes)))
        .map_err(|error| error.to_string())?;
    let mut file = tokio::fs::File::from_std(file);
    file.set_max_buf_size(buffer_bytes);
    let (file, completion) = crate::io::managed_file(file, None);
    let source: Opened<Reader> = Opened {
        io: Box::new(file),
        process: None,
        close: None,
        file: Some(completion),
    };
    let (reader, writer) = socket.into_split();
    let destination = Opened {
        io: Box::new(writer) as Writer,
        process: None,
        close: Some(CloseWatch::Reader(Box::new(reader))),
        file: None,
    };
    let mut result = run_opened(source, destination, buffer_bytes, cancel, context).await?;
    result.bytes = result.bytes.saturating_add(progress.bytes);
    Ok(result)
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

fn endpoint_pinned(
    location: &Location,
    source: bool,
    fifo: Option<FifoIdentity>,
) -> Result<Endpoint, String> {
    #[cfg(not(unix))]
    let _ = fifo;
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
            let observed = fifo_identity(&path)
                .map_err(|cause| cause.to_string())?
                .ok_or("FIFO kind changed during endpoint inspection")?;
            if fifo.is_some_and(|expected| expected != observed) {
                return Err(format!(
                    "FIFO identity changed after planning at {}",
                    path.display()
                ));
            }
            return Ok(Endpoint::Fifo(path, observed));
        }
        if fifo.is_some() {
            return Err(format!("planned FIFO was replaced at {}", path.display()));
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

async fn open_reader(
    endpoint: Endpoint,
    buffer_bytes: usize,
    context: Option<crate::telemetry::TraceContext>,
) -> io::Result<Opened<Reader>> {
    if let Endpoint::File(path) = endpoint {
        let mut file = tokio::fs::File::open(path).await?;
        file.set_max_buf_size(buffer_bytes);
        let (file, completion) = crate::io::managed_file(file, None);
        return Ok(Opened {
            io: Box::new(file),
            process: None,
            close: None,
            file: Some(completion),
        });
    }
    if let Endpoint::Http(location) = endpoint {
        let command = crate::runtime::http_command_builder(&location).map_err(io::Error::other)?;
        let mut process = RemoteProcess::capture(command, context)?;
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
            file: None,
        });
    }
    if let Endpoint::Remote(location) = endpoint {
        let mut process = RemoteProcess::open(&location, "r", buffer_bytes, context).await?;
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
            file: None,
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
            Ok(Box::new(stream))
        }
        Endpoint::Remote(_) | Endpoint::Http(_) => {
            unreachable!("child reader is handled before native dispatch")
        }
        #[cfg(windows)]
        Endpoint::NamedPipe(path) => Ok(Box::new(open_named_pipe(&path, true).await?)),
        #[cfg(unix)]
        Endpoint::Fifo(path, identity) => Ok(Box::new(Fifo::reader(&path, &identity)?)),
        #[cfg(unix)]
        Endpoint::Socket(path) => {
            let stream = tokio::net::UnixStream::connect(path).await?;
            Ok(Box::new(stream))
        }
    }
}

async fn open_writer(
    endpoint: Endpoint,
    no_clobber: bool,
    buffer_bytes: usize,
    context: Option<crate::telemetry::TraceContext>,
) -> io::Result<Opened<Writer>> {
    if let Endpoint::File(path) = endpoint {
        if let Some(parent) = path
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty())
        {
            tokio::fs::create_dir_all(parent).await?;
        }
        let stage = tokio::task::spawn_blocking(move || {
            let stage = crate::io::StagedFile::new_with_context(&path, no_clobber, context)?;
            stage.preserve_target_permissions()?;
            Ok::<_, io::Error>(stage)
        })
        .await
        .map_err(io::Error::other)??;
        let mut file = tokio::fs::OpenOptions::new()
            .write(true)
            .open(stage.path())
            .await?;
        file.set_max_buf_size(buffer_bytes);
        let (file, completion) = crate::io::managed_file(file, Some(stage));
        return Ok(Opened {
            io: Box::new(file),
            process: None,
            close: None,
            file: Some(completion),
        });
    }
    if let Endpoint::Tcp(host, port) = endpoint {
        let stream = tokio::net::TcpStream::connect((host.as_str(), port)).await?;
        let (reader, writer) = stream.into_split();
        return Ok(Opened {
            io: Box::new(writer),
            process: None,
            close: Some(CloseWatch::Reader(Box::new(reader))),
            file: None,
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
            file: None,
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
            file: None,
        });
    }
    #[cfg(unix)]
    if let Endpoint::Fifo(path, identity) = endpoint {
        loop {
            match Fifo::writer(&path, &identity) {
                Ok(writer) => {
                    let watch = writer.0.get_ref().try_clone()?;
                    return Ok(Opened {
                        io: Box::new(writer),
                        process: None,
                        close: Some(fifo_watch(watch)?),
                        file: None,
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
        let mut process = RemoteProcess::open(
            &location,
            if no_clobber { "x" } else { "w" },
            buffer_bytes,
            context.clone(),
        )
        .await?;
        let input = process
            .child
            .stdin
            .take()
            .ok_or_else(|| io::Error::other("remote stdin is unavailable"))?;
        let fence = process
            .file_output()
            .then(|| Arc::clone(&process.commit_requested));
        let io: Writer = Box::new(FrameWriter::new(input, buffer_bytes, fence, context));
        return Ok(Opened {
            io,
            process: Some(process),
            close: None,
            file: None,
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
        Endpoint::Fifo(path, identity) => loop {
            match Fifo::writer(&path, &identity) {
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
    source: &mut Opened<Reader>,
    destination: &mut Opened<Writer>,
    buffer_bytes: usize,
    cancel: &CancellationToken,
    context: Option<&crate::telemetry::TraceContext>,
) -> Result<StreamReport, String> {
    let framed = destination
        .process
        .as_ref()
        .is_some_and(|process| process.writer);
    let flow = relay_bytes(
        Relay {
            source: &mut source.io,
            destination: &mut destination.io,
            process: &mut destination.process,
            watcher: &mut destination.close,
            framed,
            context,
        },
        buffer_bytes,
        cancel,
    )
    .await?;
    match flow.end {
        StreamEnd::SourceClosed => {
            if !wait_process(&mut source.process, cancel).await? {
                return Ok(report(flow.bytes, StreamEnd::Cancelled));
            }
            finish(destination, flow.bytes, cancel).await
        }
        StreamEnd::DestinationClosed => {
            if !wait_process(&mut destination.process, cancel).await? {
                return Ok(report(flow.bytes, StreamEnd::Cancelled));
            }
            Ok(flow)
        }
        StreamEnd::Cancelled => Ok(flow),
    }
}

struct Relay<'a> {
    source: &'a mut Reader,
    destination: &'a mut Writer,
    process: &'a mut Option<RemoteProcess>,
    watcher: &'a mut Option<CloseWatch>,
    framed: bool,
    context: Option<&'a crate::telemetry::TraceContext>,
}

async fn relay_bytes(
    relay: Relay<'_>,
    buffer_bytes: usize,
    cancel: &CancellationToken,
) -> Result<StreamReport, String> {
    let Relay {
        source,
        destination,
        process,
        watcher,
        framed,
        context,
    } = relay;
    let mut buffer = vec![0_u8; buffer_bytes];
    let mut bytes = 0_u64;
    let watch = process.is_some() || watcher.is_some();
    // Keep this future alive across chunks. Recreating it would restart the
    // named-pipe timer and query kernel state on every buffered read.
    let closing = destination_exit(process, watcher);
    tokio::pin!(closing);
    loop {
        let read = tokio::select! {
            biased;
            () = cancel.cancelled() => return Ok(report(bytes, StreamEnd::Cancelled)),
            result = &mut closing, if watch => { result?; return Ok(report(bytes, StreamEnd::DestinationClosed)); }
            result = source.read(&mut buffer) => result,
        };
        let count = match read {
            Ok(0) => return Ok(report(bytes, StreamEnd::SourceClosed)),
            Ok(count) => count,
            Err(error) => {
                if let Some(context) = context {
                    context.io_error("read.source", &error);
                    context.event(
                        "flow.failed",
                        &[("stage", "read.source")],
                        &[("bytes.accepted", bytes)],
                    );
                }
                return Err(format!(
                    "cannot read stream source after {bytes} bytes: {error}"
                ));
            }
        };
        let mut offset = 0;
        while offset < count {
            let write = tokio::select! {
                biased;
                () = cancel.cancelled() => return Ok(report(bytes, StreamEnd::Cancelled)),
                result = &mut closing, if watch => { result?; return Ok(report(bytes, StreamEnd::DestinationClosed)); }
                result = destination.write(&buffer[offset..count]) => result,
            };
            match write {
                Ok(0) => return Ok(report(bytes, StreamEnd::DestinationClosed)),
                Ok(written) => {
                    offset += written;
                    bytes += written as u64;
                }
                Err(error) if disconnected(&error) => {
                    return Ok(report(bytes, StreamEnd::DestinationClosed));
                }
                Err(error) => {
                    if let Some(context) = context {
                        context.io_error("write.destination", &error);
                        context.event(
                            "flow.failed",
                            &[("stage", "write.destination")],
                            &[("bytes.accepted", bytes)],
                        );
                    }
                    return Err(format!(
                        "cannot write stream destination after {bytes} bytes: {error}"
                    ));
                }
            }
        }
        if framed {
            let flushed = tokio::select! {
                biased;
                () = cancel.cancelled() => return Ok(report(bytes, StreamEnd::Cancelled)),
                result = &mut closing, if watch => { result?; return Ok(report(bytes, StreamEnd::DestinationClosed)); }
                result = destination.flush() => result,
            };
            if let Err(error) = flushed {
                if disconnected(&error) {
                    return Ok(report(bytes, StreamEnd::DestinationClosed));
                }
                if let Some(context) = context {
                    context.io_error("write.frame", &error);
                }
                return Err(format!(
                    "cannot flush remote DATA frame after {bytes} bytes: {error}"
                ));
            }
        }
    }
}
async fn run_opened(
    mut source: Opened<Reader>,
    mut destination: Opened<Writer>,
    buffer_bytes: usize,
    cancel: &CancellationToken,
    context: Option<crate::telemetry::TraceContext>,
) -> Result<StreamReport, String> {
    let mut result = pump(
        &mut source,
        &mut destination,
        buffer_bytes,
        cancel,
        context.as_ref(),
    )
    .await;
    source.io = Box::new(tokio::io::empty());
    destination.io = Box::new(tokio::io::sink());
    if let Some(completion) = source.file.take()
        && let Err(error) = completion.finish(false, cancel).await
    {
        result = Err(format!("cannot finish source file I/O: {error}"));
    }
    if cancel.is_cancelled()
        && !destination
            .process
            .as_ref()
            .is_some_and(RemoteProcess::commit_requested)
        && let Ok(flow) = &mut result
    {
        flow.end = StreamEnd::Cancelled;
    }
    if let Some(completion) = destination.file.take() {
        let commit = matches!(&result, Ok(flow) if flow.end == StreamEnd::SourceClosed);
        match completion.finish(commit, cancel).await {
            Ok(false) if commit && cancel.is_cancelled() => {
                if let Ok(flow) = &mut result {
                    flow.end = StreamEnd::Cancelled;
                }
            }
            Ok(_) => {}
            Err(error) => result = Err(format!("cannot publish destination file: {error}")),
        }
    }
    result
}

async fn finish(
    destination: &mut Opened<Writer>,
    bytes: u64,
    cancel: &CancellationToken,
) -> Result<StreamReport, String> {
    let deadline = destination
        .process
        .as_ref()
        .is_some_and(RemoteProcess::file_output)
        .then(|| tokio::time::Instant::now() + std::time::Duration::from_secs(30));
    let result = tokio::select! {
        biased;
        _ = cancel.cancelled() => {
            if destination.process.as_ref().is_some_and(RemoteProcess::commit_requested) {
                shutdown_output(&mut destination.io, deadline).await
            } else { return Ok(report(bytes, StreamEnd::Cancelled)); }
        },
        result = shutdown_output(&mut destination.io, deadline) => result,
    };
    match result {
        Ok(()) => {
            destination.io = Box::new(tokio::io::sink());
            let completed = if destination
                .process
                .as_ref()
                .is_some_and(RemoteProcess::commit_requested)
            {
                let process = destination
                    .process
                    .as_mut()
                    .expect("commit fence belongs to a remote process");
                process
                    .wait_commit(deadline.expect("commit fence is only used for a remote file"))
                    .await?;
                true
            } else {
                wait_process(&mut destination.process, cancel).await?
            };
            let end = if completed {
                StreamEnd::SourceClosed
            } else {
                StreamEnd::Cancelled
            };
            Ok(report(bytes, end))
        }
        Err(error)
            if destination
                .process
                .as_ref()
                .is_some_and(RemoteProcess::commit_requested) =>
        {
            let process = destination
                .process
                .as_mut()
                .expect("commit intent owns a process");
            process
                .wait_commit(deadline.expect("commit intent owns a deadline"))
                .await
                .map_err(|outcome| format!("{outcome}; destination shutdown failed: {error}"))?;
            Ok(report(bytes, StreamEnd::SourceClosed))
        }
        Err(error) if disconnected(&error) => {
            wait_process(&mut destination.process, cancel).await?;
            Ok(report(bytes, StreamEnd::DestinationClosed))
        }
        Err(error) => Err(format!("cannot finish stream destination: {error}")),
    }
}

async fn shutdown_output(
    destination: &mut Writer,
    deadline: Option<tokio::time::Instant>,
) -> io::Result<()> {
    let shutdown = async {
        destination.flush().await?;
        destination.shutdown().await
    };
    match deadline {
        Some(deadline) => tokio::time::timeout_at(deadline, shutdown).await.map_err(|_| io::Error::new(io::ErrorKind::TimedOut, "remote commit outcome unknown after finalization deadline; inspect destination before retrying"))?,
        None => shutdown.await,
    }
}

async fn destination_exit(
    process: &mut Option<RemoteProcess>,
    watcher: &mut Option<CloseWatch>,
) -> Result<(), String> {
    match process {
        Some(process) => process.wait().await,
        None => match watcher {
            Some(CloseWatch::Consumer(closed)) => {
                closed.cancelled().await;
                Ok(())
            }
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
            #[cfg(target_os = "linux")]
            Some(CloseWatch::FifoReady(descriptor)) => loop {
                let mut ready = descriptor
                    .ready(tokio::io::Interest::ERROR)
                    .await
                    .map_err(|error| error.to_string())?;
                if ready.ready().is_error() {
                    return Ok(());
                }
                ready.clear_ready();
            },
            #[cfg(all(unix, not(target_os = "linux")))]
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
fn fifo_watch(file: fs::File) -> io::Result<CloseWatch> {
    #[cfg(target_os = "linux")]
    {
        // SAFETY: The owned nonblocking descriptor stays private and stable until
        // this AsyncFd drops. EPOLLERR reports a FIFO's last reader disconnect.
        let descriptor = unsafe {
            tokio::io::unix::AsyncFd::register_with_interest(file, tokio::io::Interest::ERROR)
        }
        .map_err(io::Error::from)?;
        Ok(CloseWatch::FifoReady(descriptor))
    }
    #[cfg(not(target_os = "linux"))]
    {
        Ok(CloseWatch::Fifo(file))
    }
}

#[cfg(all(unix, not(target_os = "linux")))]
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
    kind: RemoteKind,
    writer: bool,
    commit_requested: Arc<AtomicBool>,
    committed: Arc<AtomicBool>,
    context: Option<crate::telemetry::TraceContext>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum RemoteKind {
    File,
    Stream,
}

struct FrameWriter {
    input: tokio::process::ChildStdin,
    frame: Vec<u8>,
    offset: usize,
    capacity: usize,
    finishing: bool,
    fence: Option<Arc<AtomicBool>>,
    context: Option<crate::telemetry::TraceContext>,
    accepted: u64,
}

impl FrameWriter {
    fn new(
        input: tokio::process::ChildStdin,
        capacity: usize,
        fence: Option<Arc<AtomicBool>>,
        context: Option<crate::telemetry::TraceContext>,
    ) -> Self {
        Self {
            input,
            frame: Vec::with_capacity(capacity.saturating_add(8)),
            offset: 0,
            capacity,
            finishing: false,
            fence,
            context,
            accepted: 0,
        }
    }

    fn poll_pending(&mut self, cx: &mut std::task::Context<'_>) -> std::task::Poll<io::Result<()>> {
        while self.offset < self.frame.len() {
            match std::task::ready!(
                std::pin::Pin::new(&mut self.input).poll_write(cx, &self.frame[self.offset..])
            ) {
                Ok(0) => {
                    return std::task::Poll::Ready(Err(io::Error::new(
                        io::ErrorKind::WriteZero,
                        "remote frame transport accepted zero bytes",
                    )));
                }
                Ok(count) => self.offset += count,
                Err(error) => return std::task::Poll::Ready(Err(error)),
            }
        }
        self.frame.clear();
        self.offset = 0;
        std::task::Poll::Ready(Ok(()))
    }
}

impl AsyncWrite for FrameWriter {
    fn poll_write(
        mut self: std::pin::Pin<&mut Self>,
        cx: &mut std::task::Context<'_>,
        bytes: &[u8],
    ) -> std::task::Poll<io::Result<usize>> {
        if self.finishing {
            return std::task::Poll::Ready(Err(io::Error::new(
                io::ErrorKind::BrokenPipe,
                "remote flow is already finishing",
            )));
        }
        std::task::ready!(self.poll_pending(cx))?;
        let count = bytes.len().min(self.capacity);
        if count == 0 {
            return std::task::Poll::Ready(Ok(0));
        }
        self.frame.extend_from_slice(&(count as u64).to_be_bytes());
        self.frame.extend_from_slice(&bytes[..count]);
        self.accepted = self.accepted.saturating_add(count as u64);
        std::task::Poll::Ready(Ok(count))
    }
    fn poll_flush(
        mut self: std::pin::Pin<&mut Self>,
        cx: &mut std::task::Context<'_>,
    ) -> std::task::Poll<io::Result<()>> {
        std::task::ready!(self.poll_pending(cx))?;
        std::pin::Pin::new(&mut self.input).poll_flush(cx)
    }
    fn poll_shutdown(
        mut self: std::pin::Pin<&mut Self>,
        cx: &mut std::task::Context<'_>,
    ) -> std::task::Poll<io::Result<()>> {
        std::task::ready!(self.poll_pending(cx))?;
        if !self.finishing {
            if let Some(fence) = &self.fence {
                fence.store(true, Ordering::Release);
                if let Some(context) = &self.context {
                    context.event(
                        "remote.commit.requested",
                        &[],
                        &[("bytes.accepted", self.accepted)],
                    );
                }
            }
            self.finishing = true;
            self.frame.extend_from_slice(&0_u64.to_be_bytes());
        }
        std::task::ready!(self.poll_pending(cx))?;
        std::pin::Pin::new(&mut self.input).poll_shutdown(cx)
    }
}

impl RemoteProcess {
    fn capture(
        command: std::process::Command,
        context: Option<crate::telemetry::TraceContext>,
    ) -> io::Result<Self> {
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
        let committed = Arc::new(AtomicBool::new(false));
        let drain = tokio::spawn(drain_remote_stderr(
            stderr,
            Arc::clone(&captured),
            None,
            Arc::clone(&committed),
        ));
        Ok(Self {
            child,
            stderr: captured,
            drain,
            kind: RemoteKind::Stream,
            writer: false,
            commit_requested: Arc::new(AtomicBool::new(false)),
            committed,
            context,
        })
    }
    async fn open(
        location: &Location,
        mode: &str,
        buffer_bytes: usize,
        context: Option<crate::telemetry::TraceContext>,
    ) -> io::Result<Self> {
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
        let committed = Arc::new(AtomicBool::new(false));
        let drain = tokio::spawn(drain_remote_stderr(
            stderr,
            Arc::clone(&captured),
            Some(ready),
            Arc::clone(&committed),
        ));
        let mut process = Self {
            child,
            stderr: captured,
            drain,
            kind: RemoteKind::Stream,
            writer: mode != "r",
            commit_requested: Arc::new(AtomicBool::new(false)),
            committed,
            context,
        };
        match receive.await {
            Ok(kind) => {
                process.kind = kind;
                Ok(process)
            }
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

    fn file_output(&self) -> bool {
        self.writer && self.kind == RemoteKind::File
    }
    fn commit_requested(&self) -> bool {
        self.file_output() && self.commit_requested.load(Ordering::Acquire)
    }

    async fn wait_commit(&mut self, deadline: tokio::time::Instant) -> Result<(), String> {
        let result = match tokio::time::timeout_at(deadline, self.wait()).await {
            Ok(result) => result,
            Err(_) => Err("remote acknowledgement deadline expired".into()),
        };
        if let Err(error) = &result {
            let _ = self.child.start_kill();
            // Publication cannot become unknown merely because the client fails
            // to exit or another process keeps its stderr pipe open afterward.
            if self.committed.load(Ordering::Acquire) {
                if let Some(context) = &self.context {
                    context.event("remote.commit.acknowledged", &[], &[]);
                    context.event(
                        "remote.transport.failed_after_commit",
                        &[("reason", error)],
                        &[],
                    );
                }
                return Ok(());
            }
            return Err(format!(
                "remote commit outcome unknown; inspect destination before retrying: {error}"
            ));
        }
        result
    }

    async fn wait(&mut self) -> Result<(), String> {
        let status = self.child.wait().await.map_err(|error| error.to_string())?;
        if self.file_output() || !status.success() {
            let _ = (&mut self.drain).await;
        }
        if self.file_output() && self.committed.load(Ordering::Acquire) {
            if let Some(context) = &self.context {
                context.event(
                    "remote.commit.acknowledged",
                    &[("transport.status", &status.to_string())],
                    &[],
                );
                if !status.success() {
                    context.event(
                        "remote.transport.failed_after_commit",
                        &[("transport.status", &status.to_string())],
                        &[],
                    );
                }
            }
            return Ok(());
        }
        if status.success() && !self.file_output() {
            return Ok(());
        }
        if self.commit_requested() && !self.committed.load(Ordering::Acquire) {
            return Err(format!(
                "remote commit outcome unknown; inspect destination before retrying ({status}): {}",
                self.error_text()
            ));
        }
        if status.success() {
            return Err("remote file helper exited without commit acknowledgement".into());
        }
        Err(format!(
            "remote stream helper failed ({status}): {}",
            self.error_text()
        ))
    }
}

impl Drop for RemoteProcess {
    fn drop(&mut self) {
        if let Some(context) = &self.context {
            if self.commit_requested() && !self.committed.load(Ordering::Acquire) {
                context.event(
                    "remote.commit.outcome_unknown",
                    &[(
                        "reason",
                        "flow dropped after commit intent without acknowledgement",
                    )],
                    &[],
                );
            } else if self.file_output() && !self.committed.load(Ordering::Acquire) {
                context.event(
                    "remote.rollback.requested",
                    &[("reason", "flow closed without FINISH authorization")],
                    &[],
                );
            }
        }
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
import tempfile
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
    stage: str | None

    def __init__(self, path: str, mode: str) -> None:
        self.path = safe_path(path)
        self.mode = mode
        self.fd = None
        self.connection = None
        self.stage = None
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
        if self.kind == 'file' and mode != 'r':
            if metadata is not None:
                probe: int = os.open(self.path, os.O_WRONLY | os.O_CLOEXEC | os.O_NOFOLLOW)
                os.close(probe)
            directory: str = os.path.dirname(self.path)
            os.makedirs(directory, exist_ok=True)
            self.fd, self.stage = tempfile.mkstemp(prefix='.msync-flow-', suffix='.part', dir=directory)
            try:
                mask: int = os.umask(0)
                os.umask(mask)
                permissions: int = stat.S_IMODE(metadata.st_mode) if metadata is not None else 0o666 & ~mask
                os.fchmod(self.fd, permissions)
                os.set_blocking(self.fd, False)
            except BaseException:
                self.close()
                raise

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
        flags: int = os.O_NONBLOCK | os.O_CLOEXEC | os.O_NOFOLLOW | (os.O_RDONLY if self.mode == 'r' else os.O_WRONLY)
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
            self.fd = None
        if self.stage is not None:
            try:
                os.unlink(self.stage)
            except FileNotFoundError:
                pass

    def commit(self) -> None:
        if self.stage is None:
            return
        if self.fd is not None:
            os.close(self.fd)
            self.fd = None
        if self.mode == 'x':
            os.link(self.stage, self.path)
            os.unlink(self.stage)
        else:
            os.replace(self.stage, self.path)
        self.stage = None
        sys.stderr.write('MSYNC_STREAM_COMMITTED\n')
        sys.stderr.flush()

class Frames:
    header: bytearray
    payload: bytearray
    header_bytes: int
    length: int | None
    received: int
    pending: int
    offset: int
    finished: bool

    def __init__(self, capacity: int) -> None:
        self.header = bytearray(8)
        self.payload = bytearray(capacity)
        self.header_bytes = 0
        self.length = None
        self.received = 0
        self.pending = 0
        self.offset = 0
        self.finished = False

    def read(self) -> None:
        if self.finished or self.pending:
            return
        if self.length is None:
            count: int = os.readv(0, [memoryview(self.header)[self.header_bytes:]])
            if count == 0:
                raise EOFError('remote transport ended before an explicit FINISH frame')
            self.header_bytes += count
            if self.header_bytes != 8:
                return
            self.length = int.from_bytes(self.header, 'big')
            self.header_bytes = 0
            if self.length == 0:
                self.finished = True
                return
            if self.length > len(self.payload):
                raise ValueError('remote DATA frame exceeds its configured buffer limit')
        count = os.readv(0, [memoryview(self.payload)[self.received:self.length]])
        if count == 0:
            raise EOFError('remote transport ended inside a DATA frame')
        self.received += count
        if self.received == self.length:
            self.pending = self.length
            self.length = None
            self.received = 0

    def data(self) -> memoryview:
        return memoryview(self.payload)[self.offset:self.pending]

    def consume(self, count: int) -> None:
        self.offset += count
        if self.offset == self.pending:
            self.pending = 0
            self.offset = 0

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

def write_flow(endpoint: Endpoint, capacity: int) -> bool:
    frames: Frames = Frames(capacity)
    while not channel_closed(False):
        if not frames.pending and not frames.finished and ready(0, select.POLLIN, False):
            try:
                frames.read()
            except BlockingIOError:
                pass
        if not endpoint.open():
            time.sleep(0.01)
            continue
        if endpoint.closed_writer():
            return False
        if frames.pending and endpoint.fd is not None and ready(endpoint.fd, select.POLLOUT, False):
            try:
                count: int = os.write(endpoint.fd, frames.data())
                if count == 0:
                    raise OSError(errno.EIO, 'destination accepted zero bytes')
                frames.consume(count)
            except BlockingIOError:
                continue
        if frames.finished and not frames.pending:
            return True
    return False

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
        sys.stderr.write('\nMSYNC_STREAM_READY:' + ('file' if endpoint.kind == 'file' else 'stream') + '\n')
        sys.stderr.flush()
        if source:
            read_flow(endpoint, capacity)
        else:
            completed: bool = write_flow(endpoint, capacity)
            if completed:
                endpoint.commit()
            elif endpoint.kind == 'file':
                raise EOFError('remote file flow ended before commit authorization')
        return 0
    finally:
        endpoint.close()

try:
    raise SystemExit(main())
except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError) as error:
    if len(sys.argv) > 2 and sys.argv[2] == 'r':
        try:
            sys.stderr.write('msync source stream failed: ' + str(error) + '\n')
        except OSError:
            pass
        raise SystemExit(1)
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
    ready: Option<tokio::sync::oneshot::Sender<RemoteKind>>,
    committed: Arc<AtomicBool>,
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
                if line == b"MSYNC_STREAM_READY:file" || line == b"MSYNC_STREAM_READY:stream" {
                    if let Some(ready) = ready.take() {
                        let _ = ready.send(if line == b"MSYNC_STREAM_READY:file" {
                            RemoteKind::File
                        } else {
                            RemoteKind::Stream
                        });
                    }
                } else if line == b"MSYNC_STREAM_COMMITTED" {
                    committed.store(true, Ordering::Release);
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
    fn reader(path: &Path, identity: &FifoIdentity) -> io::Result<Self> {
        use std::os::unix::fs::OpenOptionsExt;
        let file = fs::OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_NONBLOCK | libc::O_CLOEXEC | libc::O_NOFOLLOW)
            .open(path)?;
        identity.verify(&file)?;
        // SAFETY: This module exclusively owns File and never exposes or replaces
        // its descriptor; it stays open and stable until AsyncFd drops it.
        Ok(Self(
            unsafe { tokio::io::unix::AsyncFd::register(file) }.map_err(io::Error::from)?,
        ))
    }

    fn writer(path: &Path, identity: &FifoIdentity) -> io::Result<Self> {
        use std::os::unix::fs::OpenOptionsExt;
        // A missing reader yields ENXIO, so validate the pathname on every
        // rendezvous attempt too: replacing it must not cause an endless retry
        // against another unopened FIFO. The held descriptor is checked again.
        identity.verify_metadata(&fs::symlink_metadata(path)?)?;
        let file = fs::OpenOptions::new()
            .write(true)
            .custom_flags(libc::O_NONBLOCK | libc::O_CLOEXEC | libc::O_NOFOLLOW)
            .open(path)?;
        identity.verify(&file)?;
        // SAFETY: The owned File's stable descriptor never escapes this module.
        Ok(Self(
            unsafe { tokio::io::unix::AsyncFd::register(file) }.map_err(io::Error::from)?,
        ))
    }
}

#[cfg(all(test, unix))]
mod fifo_identity_tests {
    use super::*;
    use std::os::unix::ffi::OsStrExt;

    fn fifo(path: &Path) {
        let path = std::ffi::CString::new(path.as_os_str().as_bytes()).unwrap();
        assert_eq!(unsafe { libc::mkfifo(path.as_ptr(), 0o600) }, 0);
    }

    fn workspace() -> tempfile::TempDir {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        tempfile::tempdir_in(root).unwrap()
    }

    #[test]
    fn replaced_fifo_is_rejected_by_opened_descriptor_before_reading() {
        let root = workspace();
        let path = root.path().join("wire");
        fifo(&path);
        let identity = fifo_identity(&path).unwrap().unwrap();
        fs::rename(&path, root.path().join("old-generation")).unwrap();
        fifo(&path);
        let error = match Fifo::reader(&path, &identity) {
            Ok(_) => panic!("a different FIFO inode cannot satisfy the retained proof"),
            Err(error) => error,
        };
        assert_eq!(error.kind(), io::ErrorKind::InvalidInput);
        assert!(error.to_string().contains("FIFO identity changed"));
    }

    #[cfg(target_os = "linux")]
    #[test]
    fn metadata_lease_pins_an_unlinked_inode_without_connecting_a_fifo_reader() {
        use std::os::unix::fs::MetadataExt;
        let root = workspace();
        let path = root.path().join("wire");
        fifo(&path);
        let identity = fifo_identity(&path).unwrap().unwrap();
        identity.require_generation_pin().unwrap();
        let failure = match Fifo::writer(&path, &identity) {
            Ok(_) => panic!("a metadata lease must not act as a FIFO reader"),
            Err(failure) => failure,
        };
        assert_eq!(failure.raw_os_error(), Some(libc::ENXIO));
        fs::remove_file(&path).unwrap();
        let pinned = identity._lease.as_ref().unwrap().metadata().unwrap();
        assert_eq!(pinned.nlink(), 0);
        assert_eq!(pinned.ino(), identity.inode);
        fifo(&path);
        assert_ne!(fifo_identity(&path).unwrap().unwrap(), identity);
    }

    #[test]
    fn inode_numbers_without_a_retained_lease_are_not_a_generation_capability() {
        let identity = FifoIdentity {
            device: 1,
            inode: 42,
            _lease: None,
        };
        assert_eq!(
            identity.require_generation_pin().unwrap_err().kind(),
            io::ErrorKind::Unsupported
        );
    }

    #[test]
    fn replaced_writer_without_a_reader_is_not_retried_as_the_original_fifo() {
        let root = workspace();
        let path = root.path().join("wire");
        fifo(&path);
        let identity = fifo_identity(&path).unwrap().unwrap();
        fs::rename(&path, root.path().join("old-generation")).unwrap();
        fifo(&path);
        let error = match Fifo::writer(&path, &identity) {
            Ok(_) => panic!("a different FIFO inode cannot satisfy the retained proof"),
            Err(error) => error,
        };
        assert_eq!(error.kind(), io::ErrorKind::InvalidInput);
        fs::remove_file(&path).unwrap();
        fs::write(&path, b"do not change").unwrap();
        assert!(Fifo::writer(&path, &identity).is_err());
        assert_eq!(fs::read(path).unwrap(), b"do not change");
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

#[cfg(test)]
mod commit_tests {
    use super::*;

    struct FailedShutdown;

    impl AsyncWrite for FailedShutdown {
        fn poll_write(
            self: std::pin::Pin<&mut Self>,
            _: &mut std::task::Context<'_>,
            bytes: &[u8],
        ) -> std::task::Poll<io::Result<usize>> {
            std::task::Poll::Ready(Ok(bytes.len()))
        }

        fn poll_flush(
            self: std::pin::Pin<&mut Self>,
            _: &mut std::task::Context<'_>,
        ) -> std::task::Poll<io::Result<()>> {
            std::task::Poll::Ready(Ok(()))
        }

        fn poll_shutdown(
            self: std::pin::Pin<&mut Self>,
            _: &mut std::task::Context<'_>,
        ) -> std::task::Poll<io::Result<()>> {
            std::task::Poll::Ready(Err(io::ErrorKind::BrokenPipe.into()))
        }
    }

    fn committed_child(mode: &str) -> std::process::Command {
        let mut command = std::process::Command::new(std::env::current_exe().unwrap());
        command.args([
            "--exact",
            "streams::commit_tests::committed_child_process",
            "--nocapture",
        ]);
        command.env("MSYNC_TEST_COMMITTED_CHILD", mode);
        command
    }

    #[test]
    fn committed_child_process() {
        if let Ok(mode) = std::env::var("MSYNC_TEST_COMMITTED_CHILD") {
            if mode != "unacked-hold" {
                eprintln!("MSYNC_STREAM_COMMITTED");
            }
            if mode.ends_with("hold") {
                eprintln!("MSYNC_TEST_HOLDING");
                loop {
                    std::thread::park();
                }
            }
            std::process::exit(9);
        }
    }

    #[tokio::test]
    async fn acknowledged_commit_survives_shutdown_and_transport_failure() {
        let mut process = RemoteProcess::capture(committed_child("exit"), None).unwrap();
        process.kind = RemoteKind::File;
        process.writer = true;
        process.commit_requested.store(true, Ordering::Release);
        let mut destination = Opened::native(Box::new(FailedShutdown) as Writer);
        destination.process = Some(process);
        let result = finish(&mut destination, 17, &CancellationToken::new())
            .await
            .unwrap();
        assert_eq!(result.end, StreamEnd::SourceClosed);
        assert_eq!(result.bytes, 17);
    }

    #[tokio::test]
    async fn acknowledged_commit_survives_transport_timeout_and_releases_child() {
        held_commit_outcome("acked-hold", true).await;
    }

    #[tokio::test]
    async fn unacknowledged_commit_timeout_is_unknown_and_releases_child() {
        held_commit_outcome("unacked-hold", false).await;
    }

    async fn held_commit_outcome(mode: &str, acknowledged: bool) {
        let mut process = RemoteProcess::capture(committed_child(mode), None).unwrap();
        process.kind = RemoteKind::File;
        process.writer = true;
        process.commit_requested.store(true, Ordering::Release);
        let mut output = process.child.stdout.take().unwrap();
        tokio::time::timeout(std::time::Duration::from_secs(5), async {
            while !process.error_text().contains("MSYNC_TEST_HOLDING") {
                tokio::task::yield_now().await;
            }
        })
        .await
        .expect("child must actually enter its holding state");
        assert_eq!(process.committed.load(Ordering::Acquire), acknowledged);
        assert!(process.child.try_wait().unwrap().is_none());
        let result = process
            .wait_commit(tokio::time::Instant::now() + std::time::Duration::from_millis(10))
            .await;
        if acknowledged {
            result.unwrap();
        } else {
            assert!(result.unwrap_err().contains("commit outcome unknown"));
        }
        drop(process);
        tokio::time::timeout(
            std::time::Duration::from_secs(5),
            output.read_to_end(&mut Vec::new()),
        )
        .await
        .expect("owned child must terminate and release its stdout")
        .unwrap();
    }
}
