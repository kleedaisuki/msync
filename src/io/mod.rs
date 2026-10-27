//! Common finite/stream flow contracts and capability-selected operating-system I/O.

use std::ffi::OsString;
use std::fmt;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::task::{Context, Poll};

use tokio::net::TcpStream;
use tokio_util::sync::CancellationToken;

#[cfg(target_os = "linux")]
pub mod linux;
#[cfg(windows)]
pub mod windows;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FlowEnd {
    SourceClosed,
    DestinationClosed,
    Cancelled,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FlowReport {
    pub bytes: u64,
    pub end: FlowEnd,
}

/// `end == None` requests the buffered fallback on the same open endpoints.
/// `bytes` always counts the prefix already accepted by the destination.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct KernelProgress {
    pub bytes: u64,
    pub end: Option<FlowEnd>,
}

#[cfg(windows)]
pub const MIN_TRANSMIT_FILE_BYTES: u64 = 1024 * 1024;

pub(crate) fn prefer_kernel_file_to_tcp(source: &File, offset: u64) -> io::Result<bool> {
    #[cfg(windows)]
    return Ok(source.metadata()?.len().saturating_sub(offset) >= MIN_TRANSMIT_FILE_BYTES);
    #[cfg(not(windows))]
    {
        let _ = (source, offset);
        Ok(true)
    }
}

#[derive(Debug)]
pub struct CopyFailure {
    pub bytes: u64,
    pub cause: io::Error,
}

impl fmt::Display for CopyFailure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{} (after {} bytes)", self.cause, self.bytes)
    }
}

impl std::error::Error for CopyFailure {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.cause)
    }
}

pub fn copy_failure(bytes: u64, cause: io::Error) -> io::Error {
    io::Error::new(cause.kind(), CopyFailure { bytes, cause })
}

pub fn cancelled_copy(bytes: u64) -> io::Error {
    copy_failure(
        bytes,
        io::Error::new(io::ErrorKind::Interrupted, "copy cancelled"),
    )
}

pub fn transferred_bytes(error: &io::Error) -> u64 {
    error
        .get_ref()
        .and_then(|inner| inner.downcast_ref::<CopyFailure>())
        .map_or(0, |failure| failure.bytes)
}

pub fn interrupted_bytes(error: &io::Error) -> u64 {
    transferred_bytes(error)
}

/// Executes a byte flow; directory expansion is a separate structural operation.
pub async fn transfer(
    operation: &crate::compiler::CopyOp,
    buffer_bytes: usize,
    cancel: CancellationToken,
) -> Result<FlowReport, crate::runtime::RuntimeError> {
    let kinds = crate::plan::OperationKinds {
        source: crate::runtime::inspect_location(&operation.source, true).map_err(|message| {
            crate::runtime::RuntimeError {
                message,
                span: operation.span,
            }
        })?,
        destination: crate::runtime::inspect_location(&operation.destination, false).map_err(
            |message| crate::runtime::RuntimeError {
                message,
                span: operation.span,
            },
        )?,
    };
    transfer_known(
        operation,
        kinds,
        buffer_bytes,
        cancel,
        crate::telemetry::context(),
    )
    .await
}

pub async fn transfer_known(
    operation: &crate::compiler::CopyOp,
    kinds: crate::plan::OperationKinds,
    buffer_bytes: usize,
    cancel: CancellationToken,
    context: Option<crate::telemetry::TraceContext>,
) -> Result<FlowReport, crate::runtime::RuntimeError> {
    use crate::plan::FileClass;
    if kinds.source == FileClass::Directory || operation.pattern.is_some() {
        return Err(crate::runtime::RuntimeError { message: "directory operations must structurally expand into child file flows before byte transfer".into(), span: operation.span });
    }
    let child = cancel.child_token();
    let _scope = CancelOnDrop(child.clone());
    let stream = matches!(kinds.source, FileClass::Stream | FileClass::Unknown)
        || matches!(kinds.destination, FileClass::Stream | FileClass::Unknown);
    if stream {
        return crate::streams::transfer_with_context(operation, buffer_bytes, child, context)
            .await;
    }
    let operation = operation.clone();
    let span = operation.span;
    let outcome = tokio::task::spawn_blocking(move || {
        crate::telemetry::with_context(context.as_ref(), || {
            crate::runtime::execute_copy_flow(&operation, buffer_bytes, child)
        })
    })
    .await
    .map_err(|error| crate::runtime::RuntimeError {
        message: format!("file flow worker failed: {error}"),
        span,
    })??;
    Ok(FlowReport {
        bytes: outcome
            .report
            .transferred_bytes
            .saturating_add(outcome.discarded_bytes),
        end: if outcome.cancelled {
            FlowEnd::Cancelled
        } else {
            FlowEnd::SourceClosed
        },
    })
}

struct CancelOnDrop(CancellationToken);

impl Drop for CancelOnDrop {
    fn drop(&mut self) {
        self.0.cancel();
    }
}

/// Uses an already-open source and connection. Fallback must not reopen either.
pub async fn file_to_tcp(
    source: &File,
    destination: &TcpStream,
    buffer_bytes: usize,
    cancel: &CancellationToken,
) -> io::Result<KernelProgress> {
    if buffer_bytes == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "flow buffer must be positive",
        ));
    }
    #[cfg(target_os = "linux")]
    return linux::file_to_tcp(source, destination, buffer_bytes, cancel).await;
    #[cfg(windows)]
    return windows::file_to_tcp(source, destination, buffer_bytes, cancel).await;
    #[cfg(not(any(target_os = "linux", windows)))]
    {
        let _ = (source, destination, cancel);
        Ok(KernelProgress {
            bytes: 0,
            end: None,
        })
    }
}

pub async fn file_to_tcp_with_context(
    source: &File,
    destination: &TcpStream,
    buffer_bytes: usize,
    cancel: &CancellationToken,
    context: Option<crate::telemetry::TraceContext>,
) -> io::Result<KernelProgress> {
    if buffer_bytes == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "flow buffer must be positive",
        ));
    }
    #[cfg(target_os = "linux")]
    return linux::file_to_tcp_with_context(source, destination, buffer_bytes, cancel, context)
        .await;
    #[cfg(windows)]
    return windows::file_to_tcp_with_context(source, destination, buffer_bytes, cancel, context)
        .await;
    #[cfg(not(any(target_os = "linux", windows)))]
    {
        let _ = (source, destination, cancel, context);
        Ok(KernelProgress {
            bytes: 0,
            end: None,
        })
    }
}

/// Finite file copy into an exclusively owned, unpublished staging path.
pub fn copy_file(
    source: &Path,
    target: &Path,
    buffer_bytes: usize,
    cancel: &CancellationToken,
) -> io::Result<u64> {
    if buffer_bytes == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "copy buffer must be positive",
        ));
    }
    #[cfg(target_os = "linux")]
    return linux::copy_file(source, target, buffer_bytes, cancel);
    #[cfg(windows)]
    return windows::copy_file(source, target, buffer_bytes, cancel);
    #[cfg(not(any(target_os = "linux", windows)))]
    {
        let mut source = File::open(source)?;
        let mut target = OpenOptions::new().write(true).truncate(true).open(target)?;
        buffered_copy(&mut source, &mut target, buffer_bytes, cancel)
    }
}

/// Resumes at the current file cursors and reports only this fallback's progress.
pub fn buffered_copy(
    source: &mut File,
    target: &mut File,
    buffer_bytes: usize,
    cancel: &CancellationToken,
) -> io::Result<u64> {
    let mut buffer = vec![0_u8; buffer_bytes.clamp(1, 1024 * 1024)];
    let mut bytes = 0_u64;
    loop {
        if cancel.is_cancelled() {
            return Err(cancelled_copy(bytes));
        }
        let count = match source.read(&mut buffer) {
            Ok(0) => return Ok(bytes),
            Ok(count) => count,
            Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
            Err(error) => return Err(copy_failure(bytes, error)),
        };
        let mut offset = 0;
        while offset < count {
            if cancel.is_cancelled() {
                return Err(cancelled_copy(bytes));
            }
            match target.write(&buffer[offset..count]) {
                Ok(0) => {
                    return Err(copy_failure(
                        bytes,
                        io::Error::new(io::ErrorKind::WriteZero, "destination accepted zero bytes"),
                    ));
                }
                Ok(written) => {
                    offset += written;
                    bytes += written as u64;
                }
                Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
                Err(error) => return Err(copy_failure(bytes, error)),
            }
        }
    }
}

/// EOF of the peer's sending half is the DSL's explicit one-way close boundary.
pub async fn socket_closed(socket: &TcpStream) -> io::Result<()> {
    let mut byte = [0_u8; 1];
    loop {
        socket.readable().await?;
        match socket.try_read(&mut byte) {
            Ok(0) => return Ok(()),
            Ok(_) => {}
            Err(error) if error.kind() == io::ErrorKind::WouldBlock => {}
            Err(error) if is_disconnect(&error) => return Ok(()),
            Err(error) => return Err(error),
        }
    }
}

pub fn is_disconnect(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::BrokenPipe
            | io::ErrorKind::ConnectionReset
            | io::ErrorKind::ConnectionAborted
            | io::ErrorKind::NotConnected
            | io::ErrorKind::UnexpectedEof
    )
}

/// Atomically publishes a sibling stage without exposing a partial file.
pub fn publish_file(stage: &Path, target: &Path, no_clobber: bool) -> io::Result<()> {
    if !no_clobber {
        return fs::rename(stage, target);
    }
    #[cfg(windows)]
    {
        use windows_sys::Win32::Storage::FileSystem::MoveFileExW;
        let source = windows::wide_path(stage)?;
        let destination = windows::wide_path(target)?;
        // SAFETY: Two live, validated NUL-terminated paths; flags 0 forbids replacement.
        if unsafe { MoveFileExW(source.as_ptr(), destination.as_ptr(), 0) } != 0 {
            Ok(())
        } else {
            Err(io::Error::last_os_error())
        }
    }
    #[cfg(target_os = "linux")]
    {
        use std::os::unix::ffi::OsStrExt;
        let source = std::ffi::CString::new(stage.as_os_str().as_bytes())
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "file path contains NUL"))?;
        let destination = std::ffi::CString::new(target.as_os_str().as_bytes())
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidInput, "file path contains NUL"))?;
        // SAFETY: Both C paths remain live; kernel enforces atomic no-replace semantics.
        if unsafe {
            libc::renameat2(
                libc::AT_FDCWD,
                source.as_ptr(),
                libc::AT_FDCWD,
                destination.as_ptr(),
                libc::RENAME_NOREPLACE,
            )
        } == 0
        {
            return Ok(());
        }
        let error = io::Error::last_os_error();
        if !matches!(
            error.raw_os_error(),
            Some(libc::ENOSYS | libc::EINVAL | libc::EOPNOTSUPP)
        ) {
            return Err(error);
        }
    }
    #[cfg(not(windows))]
    {
        fs::hard_link(stage, target)?;
        fs::remove_file(stage)
    }
}

static STAGE_ID: AtomicU64 = AtomicU64::new(0);

/// Owns exactly one unpublished sibling pathname. Drop never removes the target.
pub struct StagedFile {
    path: PathBuf,
    target: PathBuf,
    no_clobber: bool,
    previous_permissions: Option<fs::Permissions>,
    published: bool,
    context: Option<crate::telemetry::TraceContext>,
}

impl StagedFile {
    pub fn new(target: &Path, no_clobber: bool) -> io::Result<Self> {
        Self::new_with_context(target, no_clobber, crate::telemetry::context())
    }

    pub(crate) fn new_with_context(
        target: &Path,
        no_clobber: bool,
        context: Option<crate::telemetry::TraceContext>,
    ) -> io::Result<Self> {
        let result = Self::create(target, no_clobber, context.clone());
        if let (Some(context), Err(error)) = (context, &result) {
            context.io_error("stage.open", error);
        }
        result
    }

    fn create(
        target: &Path,
        no_clobber: bool,
        context: Option<crate::telemetry::TraceContext>,
    ) -> io::Result<Self> {
        let previous_permissions = match fs::symlink_metadata(target) {
            Ok(metadata) if !metadata.is_file() => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "file destination is not an ordinary file",
                ));
            }
            Ok(_) if no_clobber => {
                return Err(io::Error::new(
                    io::ErrorKind::AlreadyExists,
                    "destination file already exists",
                ));
            }
            Ok(metadata) => {
                let mut options = OpenOptions::new();
                options.write(true);
                #[cfg(unix)]
                {
                    use std::os::unix::fs::OpenOptionsExt;
                    options.custom_flags(libc::O_NOFOLLOW);
                }
                let probe = options.open(target)?;
                if !probe.metadata()?.is_file() {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidInput,
                        "file destination changed type before opening",
                    ));
                }
                drop(probe);
                Some(metadata.permissions())
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => None,
            Err(error) => return Err(error),
        };
        let parent = target
            .parent()
            .filter(|path| !path.as_os_str().is_empty())
            .unwrap_or_else(|| Path::new("."));
        target.file_name().ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "destination needs a file basename",
            )
        })?;
        for _ in 0..128 {
            let temporary = OsString::from(format!(
                ".msync-{}-{}.part",
                std::process::id(),
                STAGE_ID.fetch_add(1, Ordering::Relaxed)
            ));
            let path = parent.join(temporary);
            match OpenOptions::new().write(true).create_new(true).open(&path) {
                Ok(file) => {
                    drop(file);
                    return Ok(Self {
                        path,
                        target: target.to_owned(),
                        no_clobber,
                        previous_permissions,
                        published: false,
                        context: context.clone(),
                    });
                }
                Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
                Err(error) => return Err(error),
            }
        }
        Err(io::Error::new(
            io::ErrorKind::AlreadyExists,
            "cannot allocate a unique file stage",
        ))
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn preserve_target_permissions(&self) -> io::Result<()> {
        if let Some(permissions) = &self.previous_permissions {
            fs::set_permissions(&self.path, permissions.clone())?;
        }
        Ok(())
    }

    pub fn commit(mut self) -> io::Result<()> {
        let result = publish_file(&self.path, &self.target, self.no_clobber);
        match &result {
            Ok(()) => {
                self.published = true;
                if let Some(context) = &self.context {
                    context.event(
                        "file.publish",
                        &[
                            ("stage.path", &self.path.to_string_lossy()),
                            ("destination.path", &self.target.to_string_lossy()),
                        ],
                        &[],
                    );
                }
            }
            Err(error) => {
                if let Some(context) = &self.context {
                    context.io_error("file.publish", error);
                }
            }
        }
        result
    }
}

type SharedStage = Arc<Mutex<Option<StagedFile>>>;

/// The unique finishing side of an opened file; completion means physical I/O
/// has drained and the last owned file handle has closed, not just task cancel.
pub(crate) struct FileFinish {
    closed: tokio::sync::oneshot::Receiver<()>,
    stage: Option<SharedStage>,
}

impl FileFinish {
    pub(crate) async fn finish(self, commit: bool, cancel: &CancellationToken) -> io::Result<bool> {
        self.closed.await.map_err(|_| {
            io::Error::other("file I/O cleanup was interrupted before physical close")
        })?;
        let commit = commit && !cancel.is_cancelled();
        if let Some(stage) = self.stage {
            let stage = stage
                .lock()
                .unwrap_or_else(|error| error.into_inner())
                .take();
            if let Some(stage) = stage
                && commit
            {
                stage.commit()?;
                return Ok(true);
            }
        }
        Ok(false)
    }
}

pub(crate) struct ManagedFile {
    file: Option<tokio::fs::File>,
    closed: Option<tokio::sync::oneshot::Sender<()>>,
    stage: Option<SharedStage>,
}

pub(crate) fn managed_file(
    file: tokio::fs::File,
    stage: Option<StagedFile>,
) -> (ManagedFile, FileFinish) {
    let stage = stage.map(|stage| Arc::new(Mutex::new(Some(stage))));
    let (closed, completion) = tokio::sync::oneshot::channel();
    (
        ManagedFile {
            file: Some(file),
            closed: Some(closed),
            stage: stage.clone(),
        },
        FileFinish {
            closed: completion,
            stage,
        },
    )
}

impl tokio::io::AsyncRead for ManagedFile {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buffer: &mut tokio::io::ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        Pin::new(self.file.as_mut().expect("managed file is live until Drop")).poll_read(cx, buffer)
    }
}

impl tokio::io::AsyncWrite for ManagedFile {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buffer: &[u8],
    ) -> Poll<io::Result<usize>> {
        Pin::new(self.file.as_mut().expect("managed file is live until Drop"))
            .poll_write(cx, buffer)
    }
    fn poll_flush(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(self.file.as_mut().expect("managed file is live until Drop")).poll_flush(cx)
    }
    fn poll_shutdown(mut self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Pin::new(self.file.as_mut().expect("managed file is live until Drop")).poll_shutdown(cx)
    }
}

impl Drop for ManagedFile {
    fn drop(&mut self) {
        let Some(file) = self.file.take() else {
            return;
        };
        let completion = self.closed.take();
        let stage = self.stage.take();
        match file.try_into_std() {
            Ok(file) => {
                drop(file);
                if let Some(completion) = completion {
                    let _ = completion.send(());
                }
                drop(stage);
            }
            Err(file) => {
                if let Ok(runtime) = tokio::runtime::Handle::try_current() {
                    // One cleanup future per pending file, not per chunk. It owns
                    // the stage until Tokio's physical blocking I/O has drained.
                    runtime.spawn(async move {
                        drop(file.into_std().await);
                        if let Some(completion) = completion {
                            let _ = completion.send(());
                        }
                        drop(stage);
                    });
                } else {
                    // A runtime teardown cannot publish. Tokio retains ownership
                    // of any in-flight operation; the stage is discarded only.
                    drop(file);
                    drop(stage);
                }
            }
        }
    }
}

impl Drop for StagedFile {
    fn drop(&mut self) {
        if self.published {
            return;
        }
        #[cfg(windows)]
        if let Ok(metadata) = fs::symlink_metadata(&self.path)
            && metadata.is_file()
            && metadata.permissions().readonly()
        {
            use std::os::windows::fs::MetadataExt;
            use windows_sys::Win32::Storage::FileSystem::{
                FILE_ATTRIBUTE_NORMAL, FILE_ATTRIBUTE_READONLY, SetFileAttributesW,
            };
            let Ok(path) = windows::wide_path(&self.path) else {
                return;
            };
            let attributes = metadata.file_attributes() & !FILE_ATTRIBUTE_READONLY;
            // SAFETY: This guard owns the staged path; the NUL-terminated path
            // stays live and clearing READONLY does not broaden Unix permissions.
            let _ = unsafe {
                SetFileAttributesW(
                    path.as_ptr(),
                    if attributes == 0 {
                        FILE_ATTRIBUTE_NORMAL
                    } else {
                        attributes
                    },
                )
            };
        }
        let result = fs::remove_file(&self.path);
        if let Some(context) = &self.context {
            match &result {
                Ok(()) => context.event(
                    "stage.cleanup",
                    &[
                        ("stage.path", &self.path.to_string_lossy()),
                        ("outcome", "removed"),
                    ],
                    &[],
                ),
                Err(error) if error.kind() == io::ErrorKind::NotFound => context.event(
                    "stage.cleanup",
                    &[
                        ("stage.path", &self.path.to_string_lossy()),
                        ("outcome", "already_absent"),
                    ],
                    &[],
                ),
                Err(error) => context.io_error("stage.cleanup", error),
            }
        }
    }
}
