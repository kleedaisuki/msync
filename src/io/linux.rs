//! Kernel-assisted Linux transfers with bounded cancellation/readiness points.

use std::fs::{File, OpenOptions};
use std::io;
use std::os::fd::{AsFd, AsRawFd, OwnedFd, RawFd};
use std::os::unix::fs::{MetadataExt, OpenOptionsExt};
use std::path::Path;
use std::sync::Arc;

use tokio::io::Interest;
use tokio::io::unix::AsyncFd;
use tokio::net::TcpStream;
use tokio_util::sync::CancellationToken;

use crate::io::KernelProgress;
use crate::streams::StreamEnd;

const KERNEL_CHUNK: usize = 1024 * 1024;

/// Advances an explicit kernel offset, leaving the borrowed source cursor alone.
/// Unsupported operations return progress so the caller can resume on these same
/// handles rather than reconnecting a socket or repeating already delivered bytes.
pub async fn file_to_tcp(
    source: &File,
    destination: &TcpStream,
    buffer_bytes: usize,
    cancel: &CancellationToken,
) -> io::Result<KernelProgress> {
    file_to_tcp_with_context(
        source,
        destination,
        buffer_bytes,
        cancel,
        crate::telemetry::context(),
    )
    .await
}

pub(crate) async fn file_to_tcp_with_context(
    source: &File,
    destination: &TcpStream,
    buffer_bytes: usize,
    cancel: &CancellationToken,
    context: Option<crate::telemetry::TraceContext>,
) -> io::Result<KernelProgress> {
    let mut trace = crate::telemetry::linked_span("io.sendfile", context.as_ref());
    trace.expect_completion();
    let result =
        sendfile_inner::<true>(source, destination, buffer_bytes, cancel, &mut trace).await;
    trace.finish_result(&result);
    match &result {
        Ok(progress) => {
            trace.metric("bytes", progress.bytes);
            if progress.end == Some(StreamEnd::Cancelled) {
                trace.cancelled();
            }
        }
        Err(error) => trace.io_error("sendfile", error),
    }
    result
}

async fn sendfile_inner<const OFFLOAD: bool>(
    source: &File,
    destination: &TcpStream,
    buffer_bytes: usize,
    cancel: &CancellationToken,
    trace: &mut crate::telemetry::LinkedSpan,
) -> io::Result<KernelProgress> {
    if buffer_bytes == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "copy buffer is empty",
        ));
    }
    if cancel.is_cancelled() {
        return Ok(progress(0, Some(StreamEnd::Cancelled)));
    }
    let metadata = source.metadata()?;
    if !metadata.is_file() || metadata.len() == 0 {
        trace.event(
            "io.fallback",
            &[
                ("mechanism", "sendfile"),
                ("reason", "non-regular-or-zero-length-source"),
            ],
            &[],
        );
        return Ok(progress(0, None));
    }
    trace.event("io.kernel", &[("mechanism", "sendfile")], &[]);
    let mut offset = current_offset(source)?;
    let worker = if OFFLOAD {
        match KernelWorker::new(source, destination) {
            Ok(worker) => Some(worker),
            Err(error) => {
                trace.io_error("sendfile.registration", &error);
                return Ok(progress(0, None));
            }
        }
    } else {
        None
    };
    let mut bytes = 0;
    loop {
        let mut ready = if let Some(worker) = &worker {
            Some(tokio::select! {
                biased;
                () = cancel.cancelled() => return Ok(progress(bytes, Some(StreamEnd::Cancelled))),
                closed = crate::io::socket_closed(destination) => {
                    closed.map_err(|error| crate::io::copy_failure(bytes, error))?;
                    return Ok(progress(bytes, Some(StreamEnd::DestinationClosed)));
                }
                ready = worker.output.writable() => ready.map_err(|error| crate::io::copy_failure(bytes, error))?,
            })
        } else {
            tokio::select! {
                biased;
                () = cancel.cancelled() => return Ok(progress(bytes, Some(StreamEnd::Cancelled))),
                closed = crate::io::socket_closed(destination) => {
                    closed.map_err(|error| crate::io::copy_failure(bytes, error))?;
                    return Ok(progress(bytes, Some(StreamEnd::DestinationClosed)));
                }
                ready = destination.writable() => ready.map_err(|error| crate::io::copy_failure(bytes, error))?,
            }
            None
        };
        let sent = if let Some(worker) = &worker {
            worker.send(offset).await.map(|(count, next)| {
                offset = next;
                count
            })
        } else {
            destination.try_io(Interest::WRITABLE, || {
                sendfile_once(destination.as_raw_fd(), source.as_raw_fd(), &mut offset)
            })
        };
        if sent
            .as_ref()
            .is_err_and(|error| error.kind() == io::ErrorKind::WouldBlock)
            && let Some(ready) = &mut ready
        {
            ready.clear_ready();
        }
        match sent {
            Ok(0) => return finish_socket(destination, bytes),
            Ok(count) => bytes += count as u64,
            Err(error) if error.kind() == io::ErrorKind::WouldBlock => continue,
            Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
            Err(error) if unsupported(&error) => {
                trace.io_error("sendfile.fallback", &error);
                trace.event(
                    "io.fallback",
                    &[
                        ("mechanism", "sendfile"),
                        ("reason", "unsupported-operation"),
                    ],
                    &[("bytes", bytes)],
                );
                return Ok(progress(bytes, None));
            }
            Err(error) if disconnected(&error) => {
                return Ok(progress(bytes, Some(StreamEnd::DestinationClosed)));
            }
            Err(error) => return Err(crate::io::copy_failure(bytes, error)),
        }
        trace.metric("bytes", bytes);
        // A hot file and writable socket need not otherwise yield to peer flows.
        tokio::task::yield_now().await;
    }
}

struct WorkerSocket(Arc<OwnedFd>);

impl AsRawFd for WorkerSocket {
    fn as_raw_fd(&self) -> RawFd {
        self.0.as_raw_fd()
    }
}

struct KernelWorker {
    source: Arc<File>,
    output: AsyncFd<WorkerSocket>,
}

impl KernelWorker {
    fn new(source: &File, destination: &TcpStream) -> io::Result<Self> {
        let source = Arc::new(source.try_clone()?);
        let socket = WorkerSocket(Arc::new(destination.as_fd().try_clone_to_owned()?));
        // SAFETY: The private owned descriptor is stable while registered. Worker
        // clones extend its lifetime but never replace or close this descriptor.
        let output = unsafe { AsyncFd::register(socket) }.map_err(io::Error::from)?;
        Ok(Self { source, output })
    }

    async fn send(&self, mut offset: libc::off_t) -> io::Result<(usize, libc::off_t)> {
        let source = Arc::clone(&self.source);
        let socket = Arc::clone(&self.output.get_ref().0);
        // Regular-file page faults are not made asynchronous by O_NONBLOCK on
        // the socket. Drain one bounded syscall before honoring cancellation;
        // owned clones also keep an aborted future's descriptors valid.
        tokio::task::spawn_blocking(move || {
            sendfile_once(socket.as_raw_fd(), source.as_raw_fd(), &mut offset)
                .map(|count| (count, offset))
        })
        .await
        .map_err(io::Error::other)?
    }
}

fn sendfile_once(output: RawFd, input: RawFd, offset: &mut libc::off_t) -> io::Result<usize> {
    // SAFETY: The caller retains both descriptors and the live offset for this
    // synchronous syscall. The output socket is nonblocking and has one writer.
    syscall_count(unsafe { libc::sendfile(output, input, offset, KERNEL_CHUNK) })
}

pub(crate) fn copy_file(
    source: &Path,
    target: &Path,
    buffer_bytes: usize,
    cancel: &CancellationToken,
) -> io::Result<u64> {
    let mut trace = crate::telemetry::span("io.copy_file_range");
    trace.expect_completion();
    let result = copy_file_inner(source, target, buffer_bytes, cancel);
    trace.finish_result(&result);
    match &result {
        Ok(bytes) => trace.metric("bytes", *bytes),
        Err(error) => {
            trace.metric("bytes", crate::io::transferred_bytes(error));
            trace.io_error("copy_file_range", error);
            if error.kind() == io::ErrorKind::Interrupted {
                trace.cancelled();
            }
        }
    }
    result
}

fn copy_file_inner(
    source: &Path,
    target: &Path,
    buffer_bytes: usize,
    cancel: &CancellationToken,
) -> io::Result<u64> {
    if buffer_bytes == 0 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "copy buffer is empty",
        ));
    }
    if cancel.is_cancelled() {
        return Err(crate::io::cancelled_copy(0));
    }
    let mut input = OpenOptions::new()
        .read(true)
        .custom_flags(libc::O_NONBLOCK | libc::O_NOFOLLOW)
        .open(source)?;
    let mut output = OpenOptions::new()
        .write(true)
        .custom_flags(libc::O_NONBLOCK | libc::O_NOFOLLOW)
        .open(target)?;
    let source_meta = input.metadata()?;
    let target_meta = output.metadata()?;
    if !source_meta.is_file() || !target_meta.is_file() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "kernel copy requires two regular files",
        ));
    }
    if source_meta.dev() == target_meta.dev() && source_meta.ino() == target_meta.ino() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "source and destination are the same file",
        ));
    }
    output.set_len(0)?;
    if source_meta.len() == 0 {
        return buffered_remainder(&mut input, &mut output, buffer_bytes, cancel, 0);
    }
    copy_kernel_remainder(
        &mut input,
        &mut output,
        source_meta.len(),
        buffer_bytes,
        cancel,
        0,
    )
}

fn copy_kernel_remainder(
    input: &mut File,
    output: &mut File,
    source_len: u64,
    buffer_bytes: usize,
    cancel: &CancellationToken,
    mut bytes: u64,
) -> io::Result<u64> {
    loop {
        if cancel.is_cancelled() {
            return Err(crate::io::cancelled_copy(bytes));
        }
        // SAFETY: Owned regular-file descriptors remain alive for this bounded
        // synchronous operation. Null offsets advance their actual cursors so a
        // filesystem fallback resumes rather than repeating the prefix.
        let count = unsafe {
            libc::copy_file_range(
                input.as_raw_fd(),
                std::ptr::null_mut(),
                output.as_raw_fd(),
                std::ptr::null_mut(),
                KERNEL_CHUNK,
                0,
            )
        };
        match syscall_count(count) {
            Ok(0) if bytes < source_len => {
                return buffered_remainder(input, output, buffer_bytes, cancel, bytes);
            }
            Ok(0) => return Ok(bytes),
            Ok(count) => bytes += count as u64,
            Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
            Err(error) if unsupported(&error) => {
                crate::telemetry::io_error("copy_file_range.fallback", &error);
                crate::telemetry::event(
                    "io.fallback",
                    &[
                        ("mechanism", "copy_file_range"),
                        ("reason", "unsupported-operation"),
                    ],
                    &[("bytes", bytes)],
                );
                return buffered_remainder(input, output, buffer_bytes, cancel, bytes);
            }
            Err(error) => return Err(crate::io::copy_failure(bytes, error)),
        }
    }
}

fn buffered_remainder(
    input: &mut File,
    output: &mut File,
    buffer_bytes: usize,
    cancel: &CancellationToken,
    completed: u64,
) -> io::Result<u64> {
    crate::io::buffered_copy(input, output, buffer_bytes.min(KERNEL_CHUNK), cancel)
        .map(|additional| completed + additional)
        .map_err(|error| {
            crate::io::copy_failure(completed + crate::io::transferred_bytes(&error), error)
        })
}

fn current_offset(file: &File) -> io::Result<libc::off_t> {
    loop {
        // SAFETY: lseek only queries the offset of a live borrowed regular file.
        let offset = unsafe { libc::lseek(file.as_raw_fd(), 0, libc::SEEK_CUR) };
        if offset >= 0 {
            return Ok(offset);
        }
        let error = io::Error::last_os_error();
        if error.kind() != io::ErrorKind::Interrupted {
            return Err(error);
        }
    }
}

fn finish_socket(socket: &TcpStream, bytes: u64) -> io::Result<KernelProgress> {
    // SAFETY: The borrowed socket remains live. FIN follows queued payload; no
    // userspace write buffer needs flushing after sendfile.
    let result = unsafe { libc::shutdown(socket.as_raw_fd(), libc::SHUT_WR) };
    if result == 0 {
        return Ok(progress(bytes, Some(StreamEnd::SourceClosed)));
    }
    let error = io::Error::last_os_error();
    if disconnected(&error) {
        Ok(progress(bytes, Some(StreamEnd::DestinationClosed)))
    } else {
        Err(crate::io::copy_failure(bytes, error))
    }
}

fn syscall_count(count: libc::ssize_t) -> io::Result<usize> {
    if count < 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(count as usize)
    }
}

fn unsupported(error: &io::Error) -> bool {
    matches!(
        error.raw_os_error(),
        Some(libc::EINVAL | libc::ENOSYS | libc::EOPNOTSUPP | libc::EXDEV | libc::EPERM)
    )
}

fn disconnected(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::BrokenPipe
            | io::ErrorKind::ConnectionReset
            | io::ErrorKind::ConnectionAborted
            | io::ErrorKind::NotConnected
    )
}

fn progress(bytes: u64, end: Option<StreamEnd>) -> KernelProgress {
    KernelProgress { bytes, end }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Seek, SeekFrom, Write};
    use std::time::Duration;
    use tokio::io::AsyncReadExt;

    fn workspace() -> tempfile::TempDir {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        std::fs::create_dir_all(&root).unwrap();
        tempfile::tempdir_in(root).unwrap()
    }

    #[tokio::test]
    async fn sendfile_preserves_the_input_cursor_and_binary_payload() {
        let root = workspace();
        let path = root.path().join("payload");
        let bytes: Vec<u8> = (0..2 * 1024 * 1024)
            .map(|index| (index % 251) as u8)
            .collect();
        std::fs::write(&path, &bytes).unwrap();
        let mut source = File::open(path).unwrap();
        source.seek(SeekFrom::Start(31)).unwrap();
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let client = TcpStream::connect(listener.local_addr().unwrap())
            .await
            .unwrap();
        let receiver = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut bytes = Vec::new();
            socket.read_to_end(&mut bytes).await.unwrap();
            bytes
        });
        let report = file_to_tcp(&source, &client, 65536, &CancellationToken::new())
            .await
            .unwrap();
        assert_eq!(report.end, Some(StreamEnd::SourceClosed));
        assert_eq!(report.bytes, bytes.len() as u64 - 31);
        assert_eq!(source.stream_position().unwrap(), 31);
        assert_eq!(receiver.await.unwrap(), bytes[31..]);
    }

    #[tokio::test]
    async fn sendfile_cancel_during_backpressure_does_not_wait_for_receiver() {
        let root = workspace();
        let path = root.path().join("large");
        let mut source = File::create(&path).unwrap();
        source.write_all(&vec![7_u8; 16 * 1024 * 1024]).unwrap();
        drop(source);
        let source = File::open(path).unwrap();
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let client = TcpStream::connect(listener.local_addr().unwrap())
            .await
            .unwrap();
        let (_idle, _) = listener.accept().await.unwrap();
        let cancel = CancellationToken::new();
        let trigger = cancel.clone();
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(20)).await;
            trigger.cancel();
        });
        let result = tokio::time::timeout(
            Duration::from_secs(1),
            file_to_tcp(&source, &client, 65536, &cancel),
        )
        .await
        .unwrap()
        .unwrap();
        assert_eq!(result.end, Some(StreamEnd::Cancelled));
        assert!(result.bytes < 16 * 1024 * 1024);
    }

    #[test]
    fn range_copy_reports_bytes_and_rejects_same_inode_before_truncation() {
        let root = workspace();
        let source = root.path().join("source");
        let target = root.path().join("target");
        std::fs::write(&source, b"binary\0bytes").unwrap();
        File::create(&target).unwrap();
        assert_eq!(
            copy_file(&source, &target, 65536, &CancellationToken::new()).unwrap(),
            12
        );
        assert_eq!(std::fs::read(&target).unwrap(), b"binary\0bytes");
        assert!(copy_file(&source, &source, 65536, &CancellationToken::new()).is_err());
        assert_eq!(std::fs::read(&source).unwrap(), b"binary\0bytes");
        let cancel = CancellationToken::new();
        cancel.cancel();
        let error = copy_file(&source, &target, 65536, &cancel).unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::Interrupted);
        assert_eq!(crate::io::transferred_bytes(&error), 0);
        let mut bytes = Vec::new();
        File::open(target).unwrap().read_to_end(&mut bytes).unwrap();
        assert_eq!(bytes, b"binary\0bytes");
    }

    #[test]
    fn finite_virtual_file_copy_does_not_treat_metadata_size_as_eof() {
        let root = workspace();
        let target = root.path().join("virtual-target");
        File::create(&target).unwrap();
        let expected = std::fs::read("/proc/self/cmdline").unwrap();
        assert!(!expected.is_empty());
        assert_eq!(
            copy_file(
                Path::new("/proc/self/cmdline"),
                &target,
                4096,
                &CancellationToken::new()
            )
            .unwrap(),
            expected.len() as u64
        );
        assert_eq!(std::fs::read(target).unwrap(), expected);
    }

    #[test]
    fn partial_range_fallback_reuses_both_cursors_without_repeating_the_prefix() {
        const CHILD: &str = "MSYNC_TEST_RANGE_SECCOMP_CHILD";
        if std::env::var_os(CHILD).is_none() {
            let output = std::process::Command::new(std::env::current_exe().unwrap())
                .args(["--exact", "io::linux::tests::partial_range_fallback_reuses_both_cursors_without_repeating_the_prefix", "--nocapture"])
                .env(CHILD, "1").output().unwrap();
            assert!(
                output.status.success(),
                "{}\n{}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            );
            return;
        }
        let root = workspace();
        let source = root.path().join("source");
        let target = root.path().join("target");
        let bytes: Vec<u8> = (0..16384).map(|index| (index % 251) as u8).collect();
        std::fs::write(&source, &bytes).unwrap();
        let mut input = File::open(source).unwrap();
        let mut output = OpenOptions::new()
            .create_new(true)
            .write(true)
            .read(true)
            .open(&target)
            .unwrap();
        output.write_all(&bytes[..4096]).unwrap();
        input.seek(SeekFrom::Start(4096)).unwrap();
        deny_copy_file_range();
        assert_eq!(
            copy_kernel_remainder(
                &mut input,
                &mut output,
                bytes.len() as u64,
                4096,
                &CancellationToken::new(),
                4096
            )
            .unwrap(),
            bytes.len() as u64
        );
        assert_eq!(std::fs::read(target).unwrap(), bytes);
    }

    fn deny_copy_file_range() {
        let instructions = [
            libc::sock_filter {
                code: (libc::BPF_LD | libc::BPF_W | libc::BPF_ABS) as u16,
                jt: 0,
                jf: 0,
                k: 0,
            },
            libc::sock_filter {
                code: (libc::BPF_JMP | libc::BPF_JEQ | libc::BPF_K) as u16,
                jt: 0,
                jf: 1,
                k: libc::SYS_copy_file_range as u32,
            },
            libc::sock_filter {
                code: (libc::BPF_RET | libc::BPF_K) as u16,
                jt: 0,
                jf: 0,
                k: libc::SECCOMP_RET_ERRNO | libc::EPERM as u32,
            },
            libc::sock_filter {
                code: (libc::BPF_RET | libc::BPF_K) as u16,
                jt: 0,
                jf: 0,
                k: libc::SECCOMP_RET_ALLOW,
            },
        ];
        let program = libc::sock_fprog {
            len: instructions.len() as u16,
            filter: instructions.as_ptr().cast_mut(),
        };
        // SAFETY: This isolated subprocess permanently adds a four-instruction
        // filter to its own test thread; the kernel copies the live BPF array.
        assert_eq!(
            unsafe { libc::prctl(libc::PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) },
            0
        );
        assert_eq!(
            unsafe {
                libc::prctl(
                    libc::PR_SET_SECCOMP,
                    libc::SECCOMP_MODE_FILTER,
                    &program as *const libc::sock_fprog,
                )
            },
            0
        );
    }

    async fn timed_mode<const OFFLOAD: bool>(path: &Path, expected: usize) -> (f64, f64) {
        timed_mode_checked::<OFFLOAD>(path, expected, false).await
    }

    async fn timed_mode_checked<const OFFLOAD: bool>(
        path: &Path,
        expected: usize,
        verify_payload: bool,
    ) -> (f64, f64) {
        let source = File::open(path).unwrap();
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let destination = TcpStream::connect(listener.local_addr().unwrap())
            .await
            .unwrap();
        let receiver = tokio::spawn(async move {
            let (mut socket, _) = listener.accept().await.unwrap();
            let mut buffer = vec![0_u8; 65536];
            let mut bytes = 0;
            loop {
                let count = socket.read(&mut buffer).await.unwrap();
                if count == 0 {
                    break;
                }
                if verify_payload {
                    assert!(buffer[..count].iter().all(|byte| *byte == 0x5a));
                }
                bytes += count;
            }
            bytes
        });
        let started = std::time::Instant::now();
        let cpu = cpu_ms();
        let mut trace = crate::telemetry::linked_span("benchmark", None);
        let report = sendfile_inner::<OFFLOAD>(
            &source,
            &destination,
            65536,
            &CancellationToken::new(),
            &mut trace,
        )
        .await
        .unwrap();
        assert_eq!(report.end, Some(StreamEnd::SourceClosed));
        assert_eq!(report.bytes, expected as u64);
        assert_eq!(receiver.await.unwrap(), expected);
        (started.elapsed().as_secs_f64() * 1000.0, cpu_ms() - cpu)
    }

    fn cpu_ms() -> f64 {
        let mut usage = std::mem::MaybeUninit::<libc::rusage>::uninit();
        // SAFETY: A successful getrusage initializes its complete output.
        assert_eq!(
            unsafe { libc::getrusage(libc::RUSAGE_SELF, usage.as_mut_ptr()) },
            0
        );
        let usage = unsafe { usage.assume_init() };
        (usage.ru_utime.tv_sec + usage.ru_stime.tv_sec) as f64 * 1000.0
            + (usage.ru_utime.tv_usec + usage.ru_stime.tv_usec) as f64 / 1000.0
    }

    #[test]
    #[ignore = "release-only controlled performance experiment; run alone"]
    fn benchmark_sendfile_execution_modes() {
        if cfg!(debug_assertions) {
            eprintln!("timings skipped; use --release");
            return;
        }
        let root = workspace();
        let source = root.path().join("source");
        let bytes = 32 * 1024 * 1024;
        std::fs::write(&source, vec![0x5a_u8; bytes]).unwrap();
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        runtime.block_on(async {
            timed_mode::<false>(&source, bytes).await;
            timed_mode::<true>(&source, bytes).await;
            let mut inline = Vec::new();
            let mut worker = Vec::new();
            for _ in 0..4 {
                inline.push(timed_mode::<false>(&source, bytes).await);
                worker.push(timed_mode::<true>(&source, bytes).await);
                worker.push(timed_mode::<true>(&source, bytes).await);
                inline.push(timed_mode::<false>(&source, bytes).await);
            }
            for (name, values) in [("inline-sendfile", inline), ("worker-sendfile", worker)] {
                let mut elapsed: Vec<_> = values.iter().map(|value| value.0).collect();
                let mut cpu: Vec<_> = values.iter().map(|value| value.1).collect();
                elapsed.sort_by(f64::total_cmp);
                cpu.sort_by(f64::total_cmp);
                println!("{name}: bytes={bytes} median_elapsed_ms={:.3} median_process_cpu_ms={:.3} samples={} includes_receiver=1", elapsed[elapsed.len()/2], cpu[cpu.len()/2], values.len());
            }
        });
    }

    fn guest_cold_pages(path: &Path, bytes: usize) -> (usize, u64) {
        let file = File::open(path).unwrap();
        file.sync_all().unwrap();
        let mut filesystem = std::mem::MaybeUninit::<libc::statfs>::uninit();
        // SAFETY: The descriptor is live and successful fstatfs initializes its output.
        assert_eq!(
            unsafe { libc::fstatfs(file.as_raw_fd(), filesystem.as_mut_ptr()) },
            0
        );
        let filesystem = unsafe { filesystem.assume_init() };
        assert_eq!(
            filesystem.f_type,
            libc::EXT2_SUPER_MAGIC,
            "run on native ext4, not DrvFs/tmpfs"
        );
        // SAFETY: sysconf takes no borrowed memory.
        let page = unsafe { libc::sysconf(libc::_SC_PAGESIZE) } as usize;
        let mut residency = vec![0_u8; bytes.div_ceil(page)];
        // SAFETY: This non-accessed mapping covers the known file extent; mincore's
        // output has one byte per page. No mapped pointer escapes this function.
        let mapping = unsafe {
            libc::mmap(
                std::ptr::null_mut(),
                bytes,
                libc::PROT_NONE,
                libc::MAP_SHARED,
                file.as_raw_fd(),
                0,
            )
        };
        assert_ne!(mapping, libc::MAP_FAILED);
        let deadline = std::time::Instant::now() + Duration::from_secs(2);
        let mut retries = 0;
        let resident = loop {
            // SAFETY: The descriptor, mapping and full residency vector remain live.
            assert_eq!(
                unsafe {
                    libc::posix_fadvise(file.as_raw_fd(), 0, bytes as _, libc::POSIX_FADV_DONTNEED)
                },
                0
            );
            assert_eq!(
                unsafe { libc::mincore(mapping, bytes, residency.as_mut_ptr()) },
                0
            );
            let count = residency.iter().filter(|state| **state & 1 != 0).count();
            if count == 0 || std::time::Instant::now() >= deadline {
                break count;
            }
            // Late readahead or retained sendfile pages can outlive the preceding
            // flow. Wait for observed eviction, not a fixed claimed-cold delay.
            retries += 1;
            std::thread::sleep(Duration::from_millis(2));
        };
        let unmapped = unsafe { libc::munmap(mapping, bytes) };
        assert_eq!(unmapped, 0);
        (resident, retries)
    }

    #[derive(Default)]
    struct Jitter {
        max_lateness: Duration,
        skipped: u64,
        ticks: u64,
    }

    async fn heartbeat(stop: CancellationToken, ready: tokio::sync::oneshot::Sender<()>) -> Jitter {
        let period = Duration::from_millis(1);
        let mut next = tokio::time::Instant::now() + period;
        let mut result = Jitter::default();
        ready.send(()).unwrap();
        loop {
            let stopped = tokio::select! {
                biased;
                () = stop.cancelled() => true,
                () = tokio::time::sleep_until(next) => false,
            };
            let late = tokio::time::Instant::now().saturating_duration_since(next);
            result.max_lateness = result.max_lateness.max(late);
            let missed = late.as_millis() as u64;
            result.skipped += missed;
            if stopped {
                return result;
            }
            result.ticks += 1;
            // Preserve the absolute timer phase without replaying overdue ticks.
            next += period * (missed as u32 + 1);
        }
    }

    async fn cold_mode<const OFFLOAD: bool>(path: &Path, bytes: usize) -> (f64, f64, Jitter) {
        let stop = CancellationToken::new();
        let (ready, started) = tokio::sync::oneshot::channel();
        let ticker = tokio::spawn(heartbeat(stop.clone(), ready));
        started.await.unwrap();
        let (elapsed, cpu) = timed_mode_checked::<OFFLOAD>(path, bytes, true).await;
        stop.cancel();
        (elapsed, cpu, ticker.await.unwrap())
    }

    #[test]
    #[ignore = "release-only native-ext4 cold reactor experiment; run alone"]
    fn benchmark_guest_cold_sendfile_reactor() {
        if cfg!(debug_assertions) {
            eprintln!("timings skipped; use --release on native ext4");
            return;
        }
        let root = workspace();
        let bytes = 128 * 1024 * 1024;
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        for pair in 0..5 {
            let order = if pair % 2 == 0 {
                [false, true]
            } else {
                [true, false]
            };
            for offload in order {
                // Each trial owns a new inode: an earlier zero-copy send must not
                // influence whether this trial's source can actually be evicted.
                let source = root.path().join(format!("cold-{pair}-{offload}"));
                std::fs::write(&source, vec![0x5a_u8; bytes]).unwrap();
                let (resident, retries) = guest_cold_pages(&source, bytes);
                assert_eq!(
                    resident, 0,
                    "eviction advice did not produce a guest-cold source"
                );
                let (elapsed, cpu, jitter) = runtime.block_on(async {
                    if offload {
                        cold_mode::<true>(&source, bytes).await
                    } else {
                        cold_mode::<false>(&source, bytes).await
                    }
                });
                println!(
                    "cold-reactor mode={} pair={pair} bytes={bytes} source_fstatfs=0xef53 resident_pages_before={resident} eviction_retries={retries} elapsed_ms={elapsed:.3} process_cpu_ms={cpu:.3} heartbeat_max_lateness_ms={:.3} heartbeat_skipped={} heartbeat_ticks={} verified=1 includes_receiver=1",
                    if offload { "worker" } else { "inline" },
                    jitter.max_lateness.as_secs_f64() * 1000.0,
                    jitter.skipped,
                    jitter.ticks
                );
                let open_sources = std::fs::read_dir("/proc/self/fd")
                    .unwrap()
                    .filter_map(Result::ok)
                    .filter_map(|entry| std::fs::read_link(entry.path()).ok())
                    .filter(|path| *path == source)
                    .count();
                assert_eq!(
                    open_sources, 0,
                    "completed flow retained a source descriptor"
                );
                std::fs::remove_file(&source).unwrap();
            }
        }
    }
}
