//! Windows cache-manager transfers with owned cancellation/completion state.

use std::ffi::c_void;
use std::fs::{self, File};
use std::io::{self, Seek};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{AsRawHandle, AsRawSocket, FromRawHandle, OwnedHandle};
use std::path::{Component, Path, Prefix};
use std::ptr;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use socket2::{SockRef, Socket};
use tokio::net::TcpStream;
use tokio_util::sync::CancellationToken;
use windows_sys::Win32::Foundation::{GENERIC_READ, HANDLE, INVALID_HANDLE_VALUE, WAIT_OBJECT_0};
use windows_sys::Win32::Networking::WinSock::{
    LPFN_TRANSMITFILE, SIO_GET_EXTENSION_FUNCTION_POINTER, SOCKET, WSA_IO_PENDING,
    WSA_OPERATION_ABORTED, WSAEINVAL, WSAEOPNOTSUPP, WSAGetLastError, WSAGetOverlappedResult,
    WSAID_TRANSMITFILE, WSAIoctl,
};
use windows_sys::Win32::Storage::FileSystem::{
    CopyFileExW, FILE_FLAG_OVERLAPPED, FILE_FLAG_SEQUENTIAL_SCAN, FILE_SHARE_DELETE,
    FILE_SHARE_READ, FILE_SHARE_WRITE, PROGRESS_CANCEL, PROGRESS_CONTINUE, ReOpenFile,
};
use windows_sys::Win32::System::IO::{CancelIoEx, OVERLAPPED, OVERLAPPED_0, OVERLAPPED_0_0};
use windows_sys::Win32::System::Threading::{
    CreateEventW, INFINITE, ResetEvent, SetEvent, WaitForMultipleObjects, WaitForSingleObject,
};

use super::KernelProgress;
use crate::streams::StreamEnd;

const TRANSMIT_CHUNK: u64 = 16 * 1024 * 1024;

struct StopOnDrop(Arc<OwnedHandle>);

impl Drop for StopOnDrop {
    fn drop(&mut self) {
        // The worker retains the handle and drains any operation before releasing it.
        unsafe { SetEvent(self.0.as_raw_handle()) };
    }
}

fn event() -> io::Result<OwnedHandle> {
    // SAFETY: No security descriptor/name is borrowed. The new manual-reset event
    // is adopted exactly once, and OwnedHandle closes it after the worker exits.
    let handle = unsafe { CreateEventW(ptr::null(), 1, 0, ptr::null()) };
    if handle.is_null() {
        return Err(io::Error::last_os_error());
    }
    Ok(unsafe { OwnedHandle::from_raw_handle(handle) })
}

/// No operation retains a borrowed file/socket after cancellation or future drop.
/// The bounded executor admits the worker; only kernel completion waits block it.
pub(crate) async fn file_to_tcp(
    source: &File,
    destination: &TcpStream,
    _buffer_bytes: usize,
    cancel: &CancellationToken,
) -> io::Result<KernelProgress> {
    file_to_tcp_with_context(
        source,
        destination,
        _buffer_bytes,
        cancel,
        crate::telemetry::context(),
    )
    .await
}

pub(crate) async fn file_to_tcp_with_context(
    source: &File,
    destination: &TcpStream,
    _buffer_bytes: usize,
    cancel: &CancellationToken,
    context: Option<crate::telemetry::TraceContext>,
) -> io::Result<KernelProgress> {
    let mut trace = crate::telemetry::linked_span("io.transmitfile", context.as_ref());
    trace.expect_completion();
    let result = transmit_async(source, destination, cancel, trace.context()).await;
    trace.finish_result(&result);
    if let Ok(progress) = &result {
        trace.metric("bytes", progress.bytes);
        if progress.end == Some(StreamEnd::Cancelled) {
            trace.cancelled();
        }
        let outcome = match progress.end {
            Some(StreamEnd::SourceClosed) => "source_closed",
            Some(StreamEnd::DestinationClosed) => "destination_closed",
            Some(StreamEnd::Cancelled) => "cancelled",
            None => "buffered_fallback",
        };
        trace.event(
            "io.completion",
            &[("outcome", outcome)],
            &[("bytes", progress.bytes)],
        );
    }
    result
}

async fn transmit_async(
    source: &File,
    destination: &TcpStream,
    cancel: &CancellationToken,
    context: Option<crate::telemetry::TraceContext>,
) -> io::Result<KernelProgress> {
    if cancel.is_cancelled() {
        return Ok(progress(0, Some(StreamEnd::Cancelled)));
    }
    let metadata = source.metadata()?;
    if !metadata.is_file() {
        return Ok(progress(0, None));
    }
    let mut cursor = source;
    let start = cursor.stream_position()?;
    let size = metadata.len().saturating_sub(start);
    if size == 0 {
        return Ok(progress(0, Some(StreamEnd::SourceClosed)));
    }
    let source = match independent_source(source) {
        Ok(source) => source,
        Err(error) => {
            if let Some(context) = &context {
                context.io_error("ReOpenFile", &error);
                context.event("io.fallback", &[("stage", "ReOpenFile")], &[]);
            }
            return Ok(progress(0, None));
        }
    };
    let destination_owned = SockRef::from(destination).try_clone()?;
    let stop = Arc::new(event()?);
    let _stop_on_drop = StopOnDrop(Arc::clone(&stop));
    let worker_stop = Arc::clone(&stop);
    let mut worker = tokio::task::spawn_blocking(move || {
        crate::telemetry::with_context(context.as_ref(), || {
            transmit(&source, &destination_owned, start, size, &worker_stop)
        })
    });
    let stopped = tokio::select! {
        biased;
        () = cancel.cancelled() => Ok(StreamEnd::Cancelled),
        result = super::socket_closed(destination) => result.map(|()| StreamEnd::DestinationClosed),
        result = &mut worker => return join(result),
    };
    // SAFETY: The shared owned event remains live in this scope and the worker.
    unsafe { SetEvent(stop.as_raw_handle()) };
    let mut completed = join(worker.await)?;
    completed.end = Some(stopped?);
    Ok(completed)
}

fn join(
    result: Result<io::Result<KernelProgress>, tokio::task::JoinError>,
) -> io::Result<KernelProgress> {
    result.map_err(|error| io::Error::other(format!("TransmitFile worker failed: {error}")))?
}

fn progress(bytes: u64, end: Option<StreamEnd>) -> KernelProgress {
    KernelProgress { bytes, end }
}

fn independent_source(source: &File) -> io::Result<File> {
    // A duplicated synchronous File shares its current offset. TransmitFile can
    // advance that offset during read-ahead, even on an unsupported transfer.
    // ReOpenFile creates an independent overlapped file object by handle, so no
    // pathname is re-resolved and both fallback and future-drop preserve cursors.
    let handle = unsafe {
        ReOpenFile(
            source.as_raw_handle(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            FILE_FLAG_OVERLAPPED | FILE_FLAG_SEQUENTIAL_SCAN,
        )
    };
    if handle == INVALID_HANDLE_VALUE {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: ReOpenFile returned a new independently owned handle for the same file.
    Ok(unsafe { File::from_raw_handle(handle) })
}

fn transmit(
    source: &File,
    destination: &Socket,
    start: u64,
    size: u64,
    stop: &OwnedHandle,
) -> io::Result<KernelProgress> {
    let socket = destination.as_raw_socket() as SOCKET;
    let Some(transmit) = transmit_function(socket)? else {
        return Ok(progress(0, None));
    };
    let complete = event()?;
    let mut bytes = 0;
    while bytes < size {
        // SAFETY: A zero-time wait only queries our independently owned event.
        if unsafe { WaitForSingleObject(stop.as_raw_handle(), 0) } == WAIT_OBJECT_0 {
            return Ok(progress(bytes, Some(StreamEnd::Cancelled)));
        }
        let count = (size - bytes).min(TRANSMIT_CHUNK) as u32;
        // The low event bit suppresses IOCP delivery. A raw OVERLAPPED must never
        // enter Tokio/Mio's completion port as though it were its private record.
        // https://learn.microsoft.com/windows/win32/api/ioapiset/nf-ioapiset-getqueuedcompletionstatus
        let mut operation = OVERLAPPED {
            Anonymous: OVERLAPPED_0 {
                Anonymous: OVERLAPPED_0_0 {
                    Offset: (start + bytes) as u32,
                    OffsetHigh: ((start + bytes) >> 32) as u32,
                },
            },
            hEvent: ((complete.as_raw_handle() as usize) | 1) as HANDLE,
            ..OVERLAPPED::default()
        };
        // SAFETY: Both handles, the stable stack record and its event stay live
        // until complete_operation has reaped the specific operation below.
        unsafe { ResetEvent(complete.as_raw_handle()) };
        let started = unsafe {
            transmit(
                socket,
                source.as_raw_handle(),
                count,
                0,
                &mut operation,
                ptr::null(),
                0,
            )
        };
        if started == 0 {
            let code = unsafe { WSAGetLastError() };
            if code != WSA_IO_PENDING {
                if unsupported(code) {
                    crate::telemetry::event(
                        "io.fallback",
                        &[("stage", "TransmitFile")],
                        &[("os_error", code as u64), ("bytes", bytes)],
                    );
                    return Ok(progress(bytes, None));
                }
                if disconnected(code) {
                    return Ok(progress(bytes, Some(StreamEnd::DestinationClosed)));
                }
                let error = io::Error::from_raw_os_error(code);
                crate::telemetry::io_error("TransmitFile", &error);
                return Err(super::copy_failure(bytes, error));
            }
            wait_operation(socket, &mut operation, &complete, stop)
                .map_err(|error| super::copy_failure(bytes, error))?;
        }
        match complete_operation(socket, &operation) {
            Ok(0) => return Ok(progress(bytes, Some(StreamEnd::SourceClosed))),
            Ok(count) => bytes += u64::from(count),
            Err(error) if error.raw_os_error() == Some(WSA_OPERATION_ABORTED) => {
                return Ok(progress(bytes, Some(StreamEnd::Cancelled)));
            }
            Err(error) if error.raw_os_error().is_some_and(disconnected) => {
                return Ok(progress(bytes, Some(StreamEnd::DestinationClosed)));
            }
            // Once a request was submitted, a failing completion can include an
            // unreported partial send. Do not replay that range through fallback.
            Err(error) => {
                crate::telemetry::io_error("TransmitFile completion", &error);
                return Err(super::copy_failure(bytes, error));
            }
        }
    }
    Ok(progress(bytes, Some(StreamEnd::SourceClosed)))
}

fn transmit_function(socket: SOCKET) -> io::Result<LPFN_TRANSMITFILE> {
    let mut function: LPFN_TRANSMITFILE = None;
    let mut returned = 0;
    // SAFETY: Winsock fills one correctly sized function pointer for this socket's
    // provider. No asynchronous operation is submitted by this capability query.
    let result = unsafe {
        WSAIoctl(
            socket,
            SIO_GET_EXTENSION_FUNCTION_POINTER,
            (&WSAID_TRANSMITFILE as *const windows_sys::core::GUID).cast(),
            std::mem::size_of_val(&WSAID_TRANSMITFILE) as u32,
            (&mut function as *mut LPFN_TRANSMITFILE).cast(),
            std::mem::size_of_val(&function) as u32,
            &mut returned,
            ptr::null_mut(),
            None,
        )
    };
    if result == 0 {
        return Ok(function);
    }
    let code = unsafe { WSAGetLastError() };
    if unsupported(code) {
        crate::telemetry::event(
            "io.fallback",
            &[("stage", "WSAIoctl")],
            &[("os_error", code as u64)],
        );
        Ok(None)
    } else {
        Err(io::Error::from_raw_os_error(code))
    }
}

fn wait_operation(
    socket: SOCKET,
    operation: &mut OVERLAPPED,
    complete: &OwnedHandle,
    stop: &OwnedHandle,
) -> io::Result<()> {
    let handles = [complete.as_raw_handle(), stop.as_raw_handle()];
    // This executes only on the bounded blocking worker, never the async reactor.
    let waited = unsafe { WaitForMultipleObjects(2, handles.as_ptr(), 0, INFINITE) };
    if waited == WAIT_OBJECT_0 {
        return Ok(());
    }
    let failure = (waited != WAIT_OBJECT_0 + 1).then(io::Error::last_os_error);
    // Cancellation is a request, not completion. The specific record/event must
    // remain alive even when the initiating async future has already been dropped.
    unsafe { CancelIoEx(socket as HANDLE, operation) };
    let result = unsafe { WaitForSingleObject(complete.as_raw_handle(), INFINITE) };
    if result != WAIT_OBJECT_0 {
        // This private ALL_ACCESS event has no concurrent closer and this wait
        // has no timeout/APC path. A failed wait violates the handle invariant.
        // WSAGetOverlappedResult(FALSE), or a failed TRUE call, cannot prove that
        // an operation stopped accessing its record. Never unwind live memory.
        // https://learn.microsoft.com/windows/win32/api/winsock2/nf-winsock2-wsagetoverlappedresult
        let failure = io::Error::last_os_error();
        crate::telemetry::io_error("private TransmitFile completion wait invariant", &failure);
        eprintln!("fatal: private TransmitFile completion wait failed: {failure}");
        std::process::abort();
    }
    match failure {
        Some(error) => Err(error),
        None => Ok(()),
    }
}

fn complete_operation(socket: SOCKET, operation: &OVERLAPPED) -> io::Result<u32> {
    let mut transferred = 0;
    let mut flags = 0;
    // SAFETY: An immediate success or the operation's private event established
    // completion. FALSE never waits on the flag-tagged hEvent value.
    let result =
        unsafe { WSAGetOverlappedResult(socket, operation, &mut transferred, 0, &mut flags) };
    if result != 0 {
        Ok(transferred)
    } else {
        Err(io::Error::from_raw_os_error(unsafe { WSAGetLastError() }))
    }
}

fn unsupported(code: i32) -> bool {
    matches!(code, WSAEOPNOTSUPP | WSAEINVAL | 1 | 50)
}
fn disconnected(code: i32) -> bool {
    matches!(code, 64 | 109 | 232 | 233 | 10053 | 10054 | 10057 | 10058)
}

struct CopyProgress<'a> {
    cancel: &'a CancellationToken,
    bytes: AtomicU64,
    #[cfg(test)]
    cancel_after: Option<u64>,
}

unsafe extern "system" fn copy_progress(
    _total: i64,
    copied: i64,
    _stream_size: i64,
    _stream_copied: i64,
    _stream: u32,
    _reason: u32,
    _source: HANDLE,
    _target: HANDLE,
    data: *const c_void,
) -> u32 {
    // SAFETY: CopyFileExW invokes this only during the synchronous call below;
    // data points to its live context, shared callback counters use atomics.
    let progress = unsafe { &*data.cast::<CopyProgress<'_>>() };
    progress
        .bytes
        .store(copied.max(0) as u64, Ordering::Relaxed);
    #[cfg(test)]
    if progress
        .cancel_after
        .is_some_and(|after| copied.max(0) as u64 >= after)
    {
        progress.cancel.cancel();
    }
    if progress.cancel.is_cancelled() {
        PROGRESS_CANCEL
    } else {
        PROGRESS_CONTINUE
    }
}

pub(crate) fn copy_file(
    source: &Path,
    target: &Path,
    _buffer_bytes: usize,
    cancel: &CancellationToken,
) -> io::Result<u64> {
    let mut trace = crate::telemetry::span("io.copyfile");
    trace.expect_completion();
    let result = copy_file_inner(source, target, cancel);
    trace.finish_result(&result);
    match &result {
        Ok(bytes) => trace.metric("bytes", *bytes),
        Err(error) if cancel.is_cancelled() => {
            trace.cancelled();
            trace.metric("bytes", super::transferred_bytes(error));
        }
        Err(error) => trace.io_error("CopyFileExW", error),
    }
    result
}

fn copy_file_inner(source: &Path, target: &Path, cancel: &CancellationToken) -> io::Result<u64> {
    if cancel.is_cancelled() {
        return Err(super::cancelled_copy(0));
    }
    let progress = CopyProgress {
        cancel,
        bytes: AtomicU64::new(0),
        #[cfg(test)]
        cancel_after: None,
    };
    copy_file_with_progress(source, target, &progress)
}

fn copy_file_with_progress(
    source: &Path,
    target: &Path,
    progress: &CopyProgress<'_>,
) -> io::Result<u64> {
    let source = wide_path(source)?;
    let destination = wide_path(target)?;
    // SAFETY: Both terminated paths and callback context outlive this synchronous
    // native copy. The caller exclusively owns the new staging target pathname.
    let result = unsafe {
        CopyFileExW(
            source.as_ptr(),
            destination.as_ptr(),
            Some(copy_progress),
            (progress as *const CopyProgress<'_>).cast(),
            ptr::null_mut(),
            0,
        )
    };
    let failure = (result == 0).then(io::Error::last_os_error);
    if result != 0 {
        return fs::metadata(target).map(|metadata| metadata.len());
    }
    let copied = progress.bytes.load(Ordering::Relaxed);
    if progress.cancel.is_cancelled() {
        Err(super::cancelled_copy(copied))
    } else {
        Err(super::copy_failure(copied, failure.unwrap()))
    }
}

/// Preserve std filesystem long-path support without requiring longPathAware.
pub(crate) fn wide_path(path: &Path) -> io::Result<Vec<u16>> {
    let mut value: Vec<u16> = path.as_os_str().encode_wide().collect();
    if value.contains(&0) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "path contains NUL",
        ));
    }
    let namespace_path = matches!(
        path.components().next(),
        Some(Component::Prefix(prefix))
            if matches!(prefix.kind(), Prefix::Verbatim(_) | Prefix::VerbatimDisk(_)
                | Prefix::VerbatimUNC(_, _) | Prefix::DeviceNS(_))
    );
    if namespace_path {
        value.push(0);
        return Ok(value);
    }
    // Resolve drive-relative/current-drive paths and normalize slash/dot syntax
    // before verbatim conversion; verbatim Win32 paths do not perform that work.
    // Unlike canonicalize, absolute also accepts our not-yet-published target.
    // https://learn.microsoft.com/windows/win32/fileio/maximum-file-path-limitation
    let absolute = std::path::absolute(path)?;
    value = absolute.as_os_str().encode_wide().collect();
    let mut extended: Vec<u16> = r"\\?\".encode_utf16().collect();
    if value.starts_with(&[b'\\' as u16, b'\\' as u16]) {
        extended.extend("UNC\\".encode_utf16());
        extended.extend_from_slice(&value[2..]);
    } else {
        extended.extend(value);
    }
    extended.push(0);
    Ok(extended)
}

/// Establish the ordinal-insensitive identity policy used by the new local
/// transaction manager. Legacy copy execution does not opt into this policy.
pub(crate) fn require_case_insensitive_ancestors(path: &Path) -> io::Result<()> {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Foundation::{
        ERROR_INVALID_FUNCTION, ERROR_INVALID_PARAMETER, ERROR_NOT_SUPPORTED,
    };
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_CASE_SENSITIVE_INFO, FILE_FLAG_BACKUP_SEMANTICS, FILE_READ_ATTRIBUTES,
        FileCaseSensitiveInfo, GetFileInformationByHandleEx,
    };

    for directory in path.ancestors() {
        let metadata = match fs::symlink_metadata(directory) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == io::ErrorKind::NotFound => continue,
            Err(error) => return Err(case_policy_error(directory, error, false)),
        };
        if !metadata.is_dir() {
            continue;
        }
        let file = fs::OpenOptions::new()
            .access_mode(FILE_READ_ATTRIBUTES)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
            .open(directory)
            .map_err(|cause| case_policy_error(directory, cause, false))?;
        let mut info = FILE_CASE_SENSITIVE_INFO::default();
        // SAFETY: A live directory handle and correctly sized output buffer.
        let result = unsafe {
            GetFileInformationByHandleEx(
                file.as_raw_handle(),
                FileCaseSensitiveInfo,
                (&raw mut info).cast(),
                std::mem::size_of::<FILE_CASE_SENSITIVE_INFO>() as u32,
            )
        };
        if result == 0 {
            let cause = io::Error::last_os_error();
            let unsupported = cause.raw_os_error().is_some_and(|code| {
                [
                    ERROR_INVALID_FUNCTION,
                    ERROR_INVALID_PARAMETER,
                    ERROR_NOT_SUPPORTED,
                ]
                .contains(&(code as u32))
            });
            return Err(case_policy_error(directory, cause, unsupported));
        }
        // FILE_CS_FLAG_CASE_SENSITIVE_DIR. Do not merge names the filesystem
        // treats as distinct, including directories created through WSL.
        // https://devblogs.microsoft.com/commandline/per-directory-case-sensitivity-and-wsl/
        if info.Flags & 1 != 0 {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                format!(
                    "case-sensitive Windows directory {} requires a case-sensitive resource adapter",
                    directory.display()
                ),
            ));
        }
    }
    Ok(())
}

fn case_policy_error(path: &Path, cause: io::Error, unsupported: bool) -> io::Error {
    let kind = if unsupported {
        io::ErrorKind::Unsupported
    } else {
        cause.kind()
    };
    io::Error::new(
        kind,
        CasePolicyError {
            path: path.to_owned(),
            cause,
        },
    )
}

#[derive(Debug)]
struct CasePolicyError {
    path: std::path::PathBuf,
    cause: io::Error,
}

impl std::fmt::Display for CasePolicyError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "cannot establish native case policy for {}: {}; no case-insensitive identity is assumed",
            self.path.display(),
            self.cause
        )
    }
}

impl std::error::Error for CasePolicyError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.cause)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::OsString;
    use std::os::windows::ffi::OsStringExt;

    fn encoded(path: &str) -> Vec<u16> {
        path.encode_utf16().chain(Some(0)).collect()
    }

    #[test]
    fn native_paths_normalize_drive_and_unc_syntax_before_verbatim_conversion() {
        for (input, expected) in [
            ("C:/parent/./child/../leaf", r"\\?\C:\parent\leaf"),
            (
                r"\\server\share\parent\..\leaf",
                r"\\?\UNC\server\share\leaf",
            ),
            (
                "//server/share/parent/../leaf",
                r"\\?\UNC\server\share\leaf",
            ),
        ] {
            assert_eq!(wide_path(Path::new(input)).unwrap(), encoded(expected));
        }
    }

    #[test]
    fn native_paths_preserve_existing_verbatim_and_device_namespaces() {
        for path in [
            r"\\?\C:\parent\..\leaf",
            r"\\?\UNC\server\share\leaf",
            r"\\?\Volume{00000000-0000-0000-0000-000000000000}\leaf",
            r"\\.\pipe\msync-test",
        ] {
            assert_eq!(wide_path(Path::new(path)).unwrap(), encoded(path));
        }
    }

    #[test]
    fn native_paths_resolve_relative_current_drive_and_drive_relative_paths() {
        for path in ["relative/../leaf", r"\msync-leaf", "C:msync-leaf"] {
            let absolute = std::path::absolute(path).unwrap();
            let mut expected: Vec<u16> = r"\\?\".encode_utf16().collect();
            expected.extend(absolute.as_os_str().encode_wide());
            expected.push(0);
            assert_eq!(wide_path(Path::new(path)).unwrap(), expected);
        }
    }

    #[test]
    fn native_paths_reject_nul_without_losing_unpaired_utf16() {
        let path = OsString::from_wide(&[67, 58, 92, 0xd800, 46, 98, 105, 110]);
        let mut expected: Vec<u16> = r"\\?\".encode_utf16().collect();
        expected.extend(path.encode_wide());
        expected.push(0);
        assert_eq!(wide_path(Path::new(&path)).unwrap(), expected);
        let invalid = OsString::from_wide(&[67, 58, 92, 0, 120]);
        assert_eq!(
            wide_path(Path::new(&invalid)).unwrap_err().kind(),
            io::ErrorKind::InvalidInput
        );
    }

    #[test]
    fn native_copy_cancels_after_the_open_file_reports_real_progress() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        let root = tempfile::tempdir_in(root).unwrap();
        let source = root.path().join("source.bin");
        let destination = root.path().join("published.bin");
        fs::write(&source, vec![0x5a_u8; 16 * 1024 * 1024]).unwrap();
        fs::write(&destination, b"keep published bytes").unwrap();
        let cancel = CancellationToken::new();
        let progress = CopyProgress {
            cancel: &cancel,
            bytes: AtomicU64::new(0),
            cancel_after: Some(1),
        };
        {
            let stage = super::super::StagedFile::new(&destination, false).unwrap();
            let error = copy_file_with_progress(&source, stage.path(), &progress).unwrap_err();
            assert_eq!(error.kind(), io::ErrorKind::Interrupted);
            assert!(
                super::super::transferred_bytes(&error) > 0,
                "the callback must observe actual copied bytes, not a pre-open cancellation"
            );
        }
        assert_eq!(fs::read(&destination).unwrap(), b"keep published bytes");
        assert_eq!(fs::metadata(&source).unwrap().len(), 16 * 1024 * 1024);
        assert_eq!(
            fs::read_dir(root.path()).unwrap().count(),
            2,
            "abandoned stage must be cleaned"
        );
    }
}
