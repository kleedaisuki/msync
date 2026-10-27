#![cfg(windows)]

use std::ffi::CStr;
use std::fs::{self, File};
use std::io;
use std::io::Write;
use std::os::windows::fs::FileExt;
use std::os::windows::io::AsRawHandle;
use std::path::Path;
use std::ptr;
use std::time::{Duration, Instant};

use msync::io::{FlowEnd, StagedFile, copy_file, file_to_tcp, transferred_bytes};
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpStream};
use tokio_util::sync::CancellationToken;

use windows_sys::Win32::Foundation::{FreeLibrary, HMODULE};
use windows_sys::Win32::Storage::FileSystem::{
    HIORING, IORING_BUFFER_REF, IORING_BUFFER_REF_0, IORING_CAPABILITIES, IORING_CQE,
    IORING_CREATE_FLAGS, IORING_HANDLE_REF, IORING_HANDLE_REF_0, IORING_OP_READ, IORING_OP_WRITE,
    IORING_REF_RAW, IORING_VERSION, IOSQE_FLAGS_NONE,
};
use windows_sys::Win32::System::IO::CancelIoEx;
use windows_sys::Win32::System::LibraryLoader::{
    GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW,
};

type Query = unsafe extern "system" fn(*mut IORING_CAPABILITIES) -> i32;
type Create =
    unsafe extern "system" fn(IORING_VERSION, IORING_CREATE_FLAGS, u32, u32, *mut HIORING) -> i32;
type Close = unsafe extern "system" fn(HIORING) -> i32;
type Supported = unsafe extern "system" fn(HIORING, i32) -> i32;
type BuildRead = unsafe extern "system" fn(
    HIORING,
    IORING_HANDLE_REF,
    IORING_BUFFER_REF,
    u32,
    u64,
    usize,
    i32,
) -> i32;
type Submit = unsafe extern "system" fn(HIORING, u32, u32, *mut u32) -> i32;
type Pop = unsafe extern "system" fn(HIORING, *mut IORING_CQE) -> i32;

struct RingApi {
    library: HMODULE,
    query: Query,
    create: Create,
    close: Close,
    supported: Supported,
    read: BuildRead,
    submit: Submit,
    pop: Pop,
}

impl RingApi {
    fn load() -> io::Result<Option<Self>> {
        let name: Vec<u16> = "api-ms-win-core-ioring-l1-1-0.dll\0"
            .encode_utf16()
            .collect();
        // Secure dynamic lookup keeps Win11-only imports out of the executable's
        // mandatory loader table, including on Windows versions without I/O Ring.
        let library =
            unsafe { LoadLibraryExW(name.as_ptr(), ptr::null_mut(), LOAD_LIBRARY_SEARCH_SYSTEM32) };
        if library.is_null() {
            return Ok(None);
        }
        let loaded = (|| -> io::Result<Self> {
            Ok(Self {
                library,
                query: unsafe { symbol(library, c"QueryIoRingCapabilities")? },
                create: unsafe { symbol(library, c"CreateIoRing")? },
                close: unsafe { symbol(library, c"CloseIoRing")? },
                supported: unsafe { symbol(library, c"IsIoRingOpSupported")? },
                read: unsafe { symbol(library, c"BuildIoRingReadFile")? },
                submit: unsafe { symbol(library, c"SubmitIoRing")? },
                pop: unsafe { symbol(library, c"PopIoRingCompletion")? },
            })
        })();
        if loaded.is_err() {
            unsafe { FreeLibrary(library) };
        }
        loaded.map(Some)
    }

    fn capabilities(&self) -> io::Result<IORING_CAPABILITIES> {
        let mut capabilities = IORING_CAPABILITIES::default();
        check(unsafe { (self.query)(&mut capabilities) })?;
        Ok(capabilities)
    }
}

impl Drop for RingApi {
    fn drop(&mut self) {
        unsafe { FreeLibrary(self.library) };
    }
}

unsafe fn symbol<T: Copy>(library: HMODULE, name: &CStr) -> io::Result<T> {
    let address = unsafe { GetProcAddress(library, name.as_ptr().cast()) }
        .ok_or_else(io::Error::last_os_error)?;
    assert_eq!(std::mem::size_of::<T>(), std::mem::size_of_val(&address));
    // SAFETY: Every caller supplies the exact documented system-ABI function
    // pointer type for this symbol; RingApi retains the library until after use.
    Ok(unsafe { std::mem::transmute_copy(&address) })
}

fn check(result: i32) -> io::Result<()> {
    if result >= 0 {
        Ok(())
    } else {
        Err(io::Error::other(format!("I/O Ring HRESULT {result:#010x}")))
    }
}

struct PendingRead {
    buffer: Vec<u8>,
    submitted: bool,
}

struct Ring<'a> {
    api: &'a RingApi,
    handle: HIORING,
    source: Option<(usize, File)>,
    pending: Option<PendingRead>,
}

impl<'a> Ring<'a> {
    fn create(api: &'a RingApi) -> io::Result<Self> {
        let capabilities = api.capabilities()?;
        let mut handle = ptr::null_mut();
        check(unsafe {
            (api.create)(
                capabilities.MaxVersion,
                IORING_CREATE_FLAGS::default(),
                8,
                16,
                &mut handle,
            )
        })?;
        Ok(Self {
            api,
            handle,
            source: None,
            pending: None,
        })
    }

    fn supports(&self, operation: i32) -> bool {
        unsafe { (self.api.supported)(self.handle, operation) != 0 }
    }

    fn read(&mut self, file: &File, offset: u64, size: usize) -> io::Result<Vec<u8>> {
        assert!(self.pending.is_none());
        let identity = file.as_raw_handle() as usize;
        if self
            .source
            .as_ref()
            .is_none_or(|(existing, _)| *existing != identity)
        {
            self.source = Some((identity, file.try_clone()?));
        }
        self.pending = Some(PendingRead {
            buffer: vec![0_u8; size],
            submitted: false,
        });
        let pending = self.pending.as_mut().unwrap();
        let handle = IORING_HANDLE_REF {
            Kind: IORING_REF_RAW,
            Handle: IORING_HANDLE_REF_0 {
                Handle: self.source.as_ref().unwrap().1.as_raw_handle(),
            },
        };
        let buffer = IORING_BUFFER_REF {
            Kind: IORING_REF_RAW,
            Buffer: IORING_BUFFER_REF_0 {
                Address: pending.buffer.as_mut_ptr().cast(),
            },
        };
        check(unsafe {
            (self.api.read)(
                self.handle,
                handle,
                buffer,
                size.try_into().unwrap(),
                offset,
                1,
                IOSQE_FLAGS_NONE,
            )
        })?;
        let mut submitted = 0;
        let result = unsafe { (self.api.submit)(self.handle, 1, u32::MAX, &mut submitted) };
        // Failed submit leaves entries queued according to the API contract;
        // keeping pending ownership until Drop also covers this error path.
        self.pending.as_mut().unwrap().submitted = result >= 0 || submitted != 0;
        check(result)?;
        let completion = self.reap()?;
        let mut pending = self.pending.take().unwrap();
        check(completion.ResultCode)?;
        assert_eq!(completion.UserData, 1);
        assert!(completion.Information <= size);
        pending.buffer.truncate(completion.Information);
        Ok(pending.buffer)
    }

    fn reap(&self) -> io::Result<IORING_CQE> {
        loop {
            let mut completion = IORING_CQE::default();
            let result = unsafe { (self.api.pop)(self.handle, &mut completion) };
            check(result)?;
            if result == 0 {
                return Ok(completion);
            }
            std::thread::sleep(Duration::from_millis(1));
        }
    }
}

impl Drop for Ring<'_> {
    fn drop(&mut self) {
        if self
            .pending
            .as_ref()
            .is_some_and(|pending| pending.submitted)
        {
            // Closing a ring does not cancel in-flight buffer accesses. Retain
            // both the cloned file and backing allocation until its CQE arrives.
            unsafe { CancelIoEx(self.source.as_ref().unwrap().1.as_raw_handle(), ptr::null()) };
            if self.reap().is_err() {
                // A broken completion API cannot justify freeing kernel-visible
                // memory. This is a test-only emergency leak, never a normal path.
                std::mem::forget(self.pending.take());
                std::mem::forget(self.source.take());
            }
        }
        unsafe { (self.api.close)(self.handle) };
    }
}

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("windows-io-")
        .tempdir_in(root)
        .unwrap()
}

#[tokio::test]
async fn transmit_file_preserves_large_payload_and_leaves_no_socket_reference() {
    let root = workspace();
    let path = root.path().join("payload.bin");
    let payload: Vec<u8> = (0..8 * 1024 * 1024)
        .map(|index| (index % 251) as u8)
        .collect();
    fs::write(&path, &payload).unwrap();
    let source = File::open(path).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let destination = TcpStream::connect(listener.local_addr().unwrap())
        .await
        .unwrap();
    let (mut receiver, _) = listener.accept().await.unwrap();
    let receive = tokio::spawn(async move {
        let mut bytes = Vec::new();
        receiver.read_to_end(&mut bytes).await.unwrap();
        bytes
    });
    let result = file_to_tcp(&source, &destination, 65536, &CancellationToken::new())
        .await
        .unwrap();
    assert_eq!(result.end, Some(FlowEnd::SourceClosed));
    assert_eq!(result.bytes, payload.len() as u64);
    drop(destination);
    let bytes = tokio::time::timeout(Duration::from_secs(3), receive)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(bytes, payload);
}

#[tokio::test]
async fn cancel_pending_transmit_drains_the_specific_operation() {
    let root = workspace();
    let path = root.path().join("large.bin");
    let file = File::create(&path).unwrap();
    file.set_len(64 * 1024 * 1024).unwrap();
    drop(file);
    let source = File::open(path).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let destination = TcpStream::connect(listener.local_addr().unwrap())
        .await
        .unwrap();
    let (mut receiver, _) = listener.accept().await.unwrap();
    let cancel = CancellationToken::new();
    let stop = cancel.clone();
    let task =
        tokio::spawn(async move { file_to_tcp(&source, &destination, 65536, &cancel).await });
    tokio::time::sleep(Duration::from_millis(20)).await;
    assert!(
        !task.is_finished(),
        "a non-reading receiver must exert backpressure"
    );
    stop.cancel();
    let report = tokio::time::timeout(Duration::from_secs(3), task)
        .await
        .unwrap()
        .unwrap()
        .unwrap();
    assert_eq!(report.end, Some(FlowEnd::Cancelled));
    assert!(report.bytes < 64 * 1024 * 1024);
    let mut received = Vec::new();
    let closed = tokio::time::timeout(Duration::from_secs(3), receiver.read_to_end(&mut received))
        .await
        .unwrap();
    assert!(
        closed.is_ok()
            || closed.is_err_and(|error| matches!(
                error.kind(),
                io::ErrorKind::ConnectionReset
                    | io::ErrorKind::ConnectionAborted
                    | io::ErrorKind::BrokenPipe
            ))
    );
    assert!(received.len() < 64 * 1024 * 1024);
}

#[tokio::test]
async fn aborting_transmit_future_closes_owned_worker_socket_after_completion() {
    let root = workspace();
    let path = root.path().join("large.bin");
    let file = File::create(&path).unwrap();
    file.set_len(64 * 1024 * 1024).unwrap();
    drop(file);
    let source = File::open(path).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let destination = TcpStream::connect(listener.local_addr().unwrap())
        .await
        .unwrap();
    let (mut receiver, _) = listener.accept().await.unwrap();
    let task = tokio::spawn(async move {
        file_to_tcp(&source, &destination, 65536, &CancellationToken::new()).await
    });
    tokio::time::sleep(Duration::from_millis(20)).await;
    assert!(!task.is_finished());
    task.abort();
    assert!(task.await.unwrap_err().is_cancelled());
    let mut received = Vec::new();
    let closed = tokio::time::timeout(Duration::from_secs(3), receiver.read_to_end(&mut received))
        .await
        .unwrap();
    assert!(
        closed.is_ok()
            || closed.is_err_and(|error| matches!(
                error.kind(),
                io::ErrorKind::ConnectionReset
                    | io::ErrorKind::ConnectionAborted
                    | io::ErrorKind::BrokenPipe
            ))
    );
    assert!(
        received.len() < 64 * 1024 * 1024,
        "abort must stop rather than finish the whole send"
    );
}

#[tokio::test]
async fn peer_fin_interrupts_a_pending_large_transmit() {
    let root = workspace();
    let path = root.path().join("large.bin");
    let file = File::create(&path).unwrap();
    file.set_len(64 * 1024 * 1024).unwrap();
    drop(file);
    let source = File::open(path).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let destination = TcpStream::connect(listener.local_addr().unwrap())
        .await
        .unwrap();
    let (mut receiver, _) = listener.accept().await.unwrap();
    receiver.shutdown().await.unwrap();
    let report = tokio::time::timeout(
        Duration::from_secs(3),
        file_to_tcp(&source, &destination, 65536, &CancellationToken::new()),
    )
    .await
    .unwrap()
    .unwrap();
    assert_eq!(report.end, Some(FlowEnd::DestinationClosed));
    assert!(report.bytes < 64 * 1024 * 1024);
}

#[test]
fn abandoned_readonly_native_copy_is_removed_from_a_long_parent() {
    let root = workspace();
    let parent = root.path().join("a".repeat(120)).join("b".repeat(120));
    fs::create_dir_all(&parent).unwrap();
    let source = parent.join("readonly-source.bin");
    let target = parent.join("unpublished.bin");
    fs::write(&source, b"readonly payload").unwrap();
    let original = fs::metadata(&source).unwrap().permissions();
    let mut readonly = original.clone();
    readonly.set_readonly(true);
    fs::set_permissions(&source, readonly).unwrap();
    {
        let stage = StagedFile::new(&target, true).unwrap();
        copy_file(&source, stage.path(), 65536, &CancellationToken::new()).unwrap();
        assert!(fs::metadata(stage.path()).unwrap().permissions().readonly());
    }
    fs::set_permissions(&source, original).unwrap();
    assert!(!target.exists());
    assert_eq!(fs::read_dir(parent).unwrap().count(), 1);
}

#[test]
fn native_copy_and_publication_preserve_long_paths_for_both_replace_policies() {
    use std::os::windows::ffi::OsStrExt;

    let root = workspace();
    let parent = root.path().join("a".repeat(120)).join("b".repeat(120));
    fs::create_dir_all(&parent).unwrap();
    let source = parent.join(format!("{}.bin", "s".repeat(230)));
    let payload = b"long path payload";
    fs::write(&source, payload).unwrap();
    for no_clobber in [true, false] {
        let target = parent.join(format!("target-{no_clobber}.bin"));
        if !no_clobber {
            fs::write(&target, b"old target").unwrap();
        }
        let stage = StagedFile::new(&target, no_clobber).unwrap();
        assert!(stage.path().as_os_str().encode_wide().count() > 260);
        assert_eq!(
            copy_file(&source, stage.path(), 65536, &CancellationToken::new()).unwrap(),
            payload.len() as u64
        );
        stage.commit().unwrap();
        assert_eq!(fs::read(&target).unwrap(), payload);
    }
    let target = parent.join("racing-target.bin");
    let stage = StagedFile::new(&target, true).unwrap();
    copy_file(&source, stage.path(), 65536, &CancellationToken::new()).unwrap();
    fs::write(&target, b"concurrent target").unwrap();
    assert_eq!(
        stage.commit().unwrap_err().kind(),
        io::ErrorKind::AlreadyExists
    );
    assert_eq!(fs::read(&target).unwrap(), b"concurrent target");
    assert_eq!(fs::read_dir(&parent).unwrap().count(), 4);
}

#[test]
fn native_copy_uses_a_private_stage_and_cancel_never_publishes_it() {
    let root = workspace();
    let source = root.path().join("source.bin");
    let destination = root.path().join("target.bin");
    let bytes: Vec<u8> = (0..1024 * 1024).map(|index| (index % 251) as u8).collect();
    fs::write(&source, &bytes).unwrap();
    fs::write(&destination, b"old published version").unwrap();
    {
        let stage = StagedFile::new(&destination, false).unwrap();
        let cancel = CancellationToken::new();
        cancel.cancel();
        let error = copy_file(&source, stage.path(), 65536, &cancel).unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::Interrupted);
        assert_eq!(transferred_bytes(&error), 0);
    }
    assert_eq!(fs::read(&destination).unwrap(), b"old published version");
    let stage = StagedFile::new(&destination, false).unwrap();
    assert_eq!(
        copy_file(&source, stage.path(), 65536, &CancellationToken::new()).unwrap(),
        bytes.len() as u64
    );
    stage.commit().unwrap();
    assert_eq!(fs::read(&destination).unwrap(), bytes);
    assert_eq!(fs::read_dir(root.path()).unwrap().count(), 2);
}

#[test]
#[ignore = "host-specific I/O Ring experiment; run explicitly with --ignored --nocapture"]
fn actual_io_ring_capabilities_and_read_match_positioned_file_io() {
    let Some(api) = RingApi::load().unwrap() else {
        println!("I/O Ring unavailable: no API DLL on this Windows version");
        return;
    };
    let capabilities = api.capabilities().unwrap();
    let mut ring = Ring::create(&api).unwrap();
    println!(
        "I/O Ring: max_version={} submission={} completion={} flags={} read={} write={}",
        capabilities.MaxVersion,
        capabilities.MaxSubmissionQueueSize,
        capabilities.MaxCompletionQueueSize,
        capabilities.FeatureFlags,
        ring.supports(IORING_OP_READ),
        ring.supports(IORING_OP_WRITE)
    );
    assert!(ring.supports(IORING_OP_READ));
    let root = workspace();
    let path = root.path().join("input.bin");
    let bytes: Vec<u8> = (0..4 * 1024 * 1024)
        .map(|index| (index % 251) as u8)
        .collect();
    fs::write(&path, &bytes).unwrap();
    let file = File::open(path).unwrap();
    assert_eq!(ring.read(&file, 7, 65536).unwrap(), bytes[7..7 + 65536]);
}

#[test]
#[ignore = "timed I/O Ring experiment; serialize with other CPU benchmarks"]
fn io_ring_read_latency_experiment() {
    let Some(api) = RingApi::load().unwrap() else {
        return;
    };
    let mut ring = Ring::create(&api).unwrap();
    let root = workspace();
    let path = root.path().join("input.bin");
    let bytes = vec![0x5a_u8; 4 * 1024 * 1024];
    fs::write(&path, &bytes).unwrap();
    let file = File::open(&path).unwrap();
    for size in [4096, 65536, 4 * 1024 * 1024] {
        let mut ring_times = Vec::new();
        let mut read_times = Vec::new();
        for _ in 0..41 {
            let start = Instant::now();
            let received = ring.read(&file, 0, size).unwrap();
            ring_times.push(start.elapsed().as_nanos());
            assert_eq!(received, bytes[..size]);
            let start = Instant::now();
            let mut received = vec![0_u8; size];
            assert_eq!(file.seek_read(&mut received, 0).unwrap(), size);
            read_times.push(start.elapsed().as_nanos());
            assert_eq!(received, bytes[..size]);
        }
        ring_times.sort_unstable();
        read_times.sort_unstable();
        println!(
            "read size={size}: ring_median_us={:.3}, positioned_read_median_us={:.3}",
            ring_times[20] as f64 / 1000.0,
            read_times[20] as f64 / 1000.0
        );
    }
}

fn process_cpu_ticks() -> u64 {
    use windows_sys::Win32::Foundation::FILETIME;
    use windows_sys::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes};
    let mut created = FILETIME::default();
    let mut exited = FILETIME::default();
    let mut kernel = FILETIME::default();
    let mut user = FILETIME::default();
    // SAFETY: Four initialized outputs and the live process pseudohandle.
    assert_ne!(
        unsafe {
            GetProcessTimes(
                GetCurrentProcess(),
                &mut created,
                &mut exited,
                &mut kernel,
                &mut user,
            )
        },
        0
    );
    let ticks =
        |time: FILETIME| (u64::from(time.dwHighDateTime) << 32) | u64::from(time.dwLowDateTime);
    ticks(kernel) + ticks(user)
}

async fn send_benchmark(path: &Path, native: bool) -> (Duration, u64, bool) {
    let source = File::open(path).unwrap();
    let size = source.metadata().unwrap().len();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut sender = TcpStream::connect(listener.local_addr().unwrap())
        .await
        .unwrap();
    let (mut receiver, _) = listener.accept().await.unwrap();
    let receive = tokio::spawn(async move {
        let mut buffer = [0_u8; 65536];
        let mut bytes = 0_u64;
        loop {
            let count = receiver.read(&mut buffer).await.unwrap();
            if count == 0 {
                return bytes;
            }
            bytes += count as u64;
        }
    });
    let cpu = process_cpu_ticks();
    let start = Instant::now();
    let used_kernel = if native {
        let report = file_to_tcp(&source, &sender, 65536, &CancellationToken::new())
            .await
            .unwrap();
        assert_eq!(
            report.end,
            Some(FlowEnd::SourceClosed),
            "timed native trial unexpectedly fell back: {report:?}"
        );
        assert_eq!(report.bytes, size);
        true
    } else {
        let mut source = tokio::fs::File::from_std(source);
        source.set_max_buf_size(65536);
        let mut buffer = vec![0_u8; 65536];
        loop {
            let count = source.read(&mut buffer).await.unwrap();
            if count == 0 {
                break;
            }
            sender.write_all(&buffer[..count]).await.unwrap();
        }
        false
    };
    sender.shutdown().await.unwrap();
    drop(sender);
    assert_eq!(receive.await.unwrap(), size);
    let elapsed = start.elapsed();
    (elapsed, process_cpu_ticks() - cpu, used_kernel)
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
#[ignore = "timed TransmitFile/64KiB Tokio copy experiment; serialize CPU measurements"]
async fn windows_file_tcp_latency_and_throughput_experiment() {
    let root = workspace();
    let data: Vec<u8> = (0..65536).map(|index| (index % 251) as u8).collect();
    for size in [4096, 65536, 1024 * 1024, 16 * 1024 * 1024, 64 * 1024 * 1024] {
        let path = root.path().join(format!("input-{size}.bin"));
        let mut file = File::create(&path).unwrap();
        let mut left = size;
        while left > 0 {
            let count = left.min(data.len());
            file.write_all(&data[..count]).unwrap();
            left -= count;
        }
        drop(file);
        send_benchmark(&path, true).await;
        send_benchmark(&path, false).await;
        let mut native = Vec::new();
        let mut buffered = Vec::new();
        let mut native_cpu = 0;
        let mut buffered_cpu = 0;
        for iteration in 0..9 {
            for kernel in if iteration % 2 == 0 {
                [true, false]
            } else {
                [false, true]
            } {
                let (elapsed, cpu, _) = send_benchmark(&path, kernel).await;
                if kernel {
                    native.push(elapsed.as_nanos());
                    native_cpu += cpu;
                } else {
                    buffered.push(elapsed.as_nanos());
                    buffered_cpu += cpu;
                }
            }
        }
        native.sort_unstable();
        buffered.sort_unstable();
        println!(
            "tcp bytes={size}: native_p50_ms={:.4} native_p90_ms={:.4} buffered_p50_ms={:.4} buffered_p90_ms={:.4} native_process_cpu_mean_ms={:.4} buffered_process_cpu_mean_ms={:.4}",
            native[4] as f64 / 1e6,
            native[8] as f64 / 1e6,
            buffered[4] as f64 / 1e6,
            buffered[8] as f64 / 1e6,
            native_cpu as f64 / 90000.0,
            buffered_cpu as f64 / 90000.0
        );
    }
}
