use std::ffi::c_void;
use std::fs::File;
use std::io;
use std::io::{Read, Write};
use std::mem::{size_of, zeroed};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::FromRawHandle;
use std::path::Path;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::ptr::{null, null_mut};
use std::thread;
use std::time::Duration;

type Handle = *mut c_void;

#[repr(C)]
#[derive(Clone, Copy)]
struct Coord {
    x: i16,
    y: i16,
}

#[repr(C)]
struct StartupInfo {
    size: u32,
    reserved: *mut u16,
    desktop: *mut u16,
    title: *mut u16,
    x: u32,
    y: u32,
    width: u32,
    height: u32,
    x_count: u32,
    y_count: u32,
    fill: u32,
    flags: u32,
    show: u16,
    reserved_size: u16,
    reserved_bytes: *mut u8,
    input: Handle,
    output: Handle,
    error: Handle,
}

#[repr(C)]
struct StartupInfoEx {
    info: StartupInfo,
    attributes: *mut c_void,
}

#[repr(C)]
struct ProcessInfo {
    process: Handle,
    thread: Handle,
    id: u32,
    thread_id: u32,
}

#[repr(C)]
#[derive(Default)]
struct BasicLimits {
    process_time: i64,
    job_time: i64,
    flags: u32,
    minimum_working_set: usize,
    maximum_working_set: usize,
    process_count: u32,
    affinity: usize,
    priority: u32,
    scheduling: u32,
}

#[repr(C)]
#[derive(Default)]
struct ExtendedLimits {
    basic: BasicLimits,
    io: [u64; 6],
    process_memory: usize,
    job_memory: usize,
    peak_process_memory: usize,
    peak_job_memory: usize,
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn CreatePipe(read: *mut Handle, write: *mut Handle, security: *const c_void, size: u32)
    -> i32;
    fn CloseHandle(handle: Handle) -> i32;
    fn CreatePseudoConsole(
        size: Coord,
        input: Handle,
        output: Handle,
        flags: u32,
        console: *mut Handle,
    ) -> i32;
    fn ClosePseudoConsole(console: Handle);
    fn InitializeProcThreadAttributeList(
        list: *mut c_void,
        count: u32,
        flags: u32,
        bytes: *mut usize,
    ) -> i32;
    fn UpdateProcThreadAttribute(
        list: *mut c_void,
        flags: u32,
        attribute: usize,
        value: *mut c_void,
        bytes: usize,
        previous: *mut c_void,
        returned: *mut usize,
    ) -> i32;
    fn DeleteProcThreadAttributeList(list: *mut c_void);
    fn CreateProcessW(
        application: *const u16,
        command: *mut u16,
        process_security: *const c_void,
        thread_security: *const c_void,
        inherit: i32,
        flags: u32,
        environment: *const c_void,
        directory: *const u16,
        startup: *const StartupInfo,
        process: *mut ProcessInfo,
    ) -> i32;
    fn ResumeThread(thread: Handle) -> u32;
    fn GetExitCodeProcess(process: Handle, code: *mut u32) -> i32;
    fn WaitForSingleObject(handle: Handle, milliseconds: u32) -> u32;
    fn TerminateProcess(process: Handle, code: u32) -> i32;
    fn CreateJobObjectW(security: *const c_void, name: *const u16) -> Handle;
    fn SetInformationJobObject(job: Handle, class: i32, data: *const c_void, size: u32) -> i32;
    fn AssignProcessToJobObject(job: Handle, process: Handle) -> i32;
    fn GetStdHandle(kind: u32) -> Handle;
    fn SetStdHandle(kind: u32, handle: Handle) -> i32;
}

struct OwnedHandle(Handle);
impl Drop for OwnedHandle {
    fn drop(&mut self) {
        if !self.0.is_null() {
            // SAFETY: This wrapper owns exactly one ordinary kernel handle.
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
}

struct OwnedConsole(Handle);

impl Drop for OwnedConsole {
    fn drop(&mut self) {
        // SAFETY: This is a successfully created, exclusively owned pseudoconsole.
        unsafe { ClosePseudoConsole(self.0) }
    }
}

struct NativeProcess {
    process: OwnedHandle,
    job: Option<OwnedHandle>,
    _console: OwnedConsole,
    input: File,
}

fn wide(path: &Path) -> Vec<u16> {
    path.as_os_str().encode_wide().chain(Some(0)).collect()
}

fn checked(success: i32) -> io::Result<()> {
    if success == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

fn pipe() -> io::Result<(OwnedHandle, OwnedHandle)> {
    let mut read = null_mut();
    let mut write = null_mut();
    // SAFETY: Both output handles are initialized on successful pipe creation.
    checked(unsafe { CreatePipe(&mut read, &mut write, null(), 0) })?;
    Ok((OwnedHandle(read), OwnedHandle(write)))
}

fn environment(directory: &Path) -> Vec<u16> {
    let mut entries: Vec<_> = std::env::vars_os()
        .filter(|(key, _)| {
            !key.to_string_lossy().eq_ignore_ascii_case("TERM")
                && !key.to_string_lossy().eq_ignore_ascii_case("NO_COLOR")
                && !key.to_string_lossy().eq_ignore_ascii_case("HOME")
                && !key.to_string_lossy().eq_ignore_ascii_case("USERPROFILE")
                && !key.to_string_lossy().eq_ignore_ascii_case("APPDATA")
                && !key.to_string_lossy().eq_ignore_ascii_case("LOCALAPPDATA")
                && !key.to_string_lossy().eq_ignore_ascii_case("HISTFILE")
                && !key.to_string_lossy().eq_ignore_ascii_case("WSLENV")
        })
        .collect();
    entries.push(("TERM".into(), "xterm-256color".into()));
    entries.push(("HOME".into(), directory.as_os_str().to_owned()));
    entries.push(("USERPROFILE".into(), directory.as_os_str().to_owned()));
    entries.push(("APPDATA".into(), directory.as_os_str().to_owned()));
    entries.push(("LOCALAPPDATA".into(), directory.as_os_str().to_owned()));
    entries.push((
        "HISTFILE".into(),
        directory.join(".test_bash_history").into_os_string(),
    ));
    let mut forward: Vec<_> = std::env::var("WSLENV")
        .unwrap_or_default()
        .split(':')
        .filter(|entry| {
            !entry.is_empty() && !matches!(entry.split('/').next(), Some("HOME" | "HISTFILE"))
        })
        .map(str::to_owned)
        .collect();
    forward.extend(["HOME/p".into(), "HISTFILE/p".into()]);
    entries.push(("WSLENV".into(), forward.join(":").into()));
    entries.sort_by_key(|(key, _)| key.to_string_lossy().to_uppercase());
    let mut block = Vec::new();
    for (key, value) in entries {
        block.extend(key.encode_wide());
        block.push(b'=' as u16);
        block.extend(value.encode_wide());
        block.push(0);
    }
    block.push(0);
    block
}

impl NativeProcess {
    pub fn spawn(executable: &Path, directory: &Path, color: &str) -> io::Result<(Self, File)> {
        let (child_input, input) = pipe()?;
        let (output, child_output) = pipe()?;
        let mut console = null_mut();
        // SAFETY: The console borrows live pipe ends during creation and returns its own handle.
        let result = unsafe {
            CreatePseudoConsole(
                Coord { x: 100, y: 30 },
                child_input.0,
                child_output.0,
                0,
                &mut console,
            )
        };
        if result < 0 {
            return Err(io::Error::other(format!(
                "CreatePseudoConsole HRESULT {result:#x}"
            )));
        }
        let console = OwnedConsole(console);
        drop(child_input);
        drop(child_output);
        let mut bytes = 0;
        // SAFETY: A null list requests the required allocation size only.
        unsafe {
            InitializeProcThreadAttributeList(null_mut(), 1, 0, &mut bytes);
        }
        let mut storage = vec![0_usize; bytes.div_ceil(size_of::<usize>())];
        let list = storage.as_mut_ptr().cast::<c_void>();
        // SAFETY: storage is sufficiently sized and naturally aligned for this private list.
        checked(unsafe { InitializeProcThreadAttributeList(list, 1, 0, &mut bytes) })?;
        let attribute_result = checked(unsafe {
            UpdateProcThreadAttribute(
                list,
                0,
                0x0002_0016,
                console.0,
                size_of::<Handle>(),
                null_mut(),
                null_mut(),
            )
        });
        if let Err(error) = attribute_result {
            // SAFETY: No child exists, and the initialized list/console are exclusively owned.
            unsafe {
                DeleteProcThreadAttributeList(list);
            }
            return Err(error);
        }
        let executable_wide = wide(executable);
        let environment = environment(directory);
        let directory = wide(directory);
        let mut command: Vec<u16> = format!("\"{}\" repl --color {color}", executable.display())
            .encode_utf16()
            .chain(Some(0))
            .collect();
        // SAFETY: Zero is the documented default for all omitted startup fields.
        let mut startup: StartupInfoEx = unsafe { zeroed() };
        startup.info.size = size_of::<StartupInfoEx>() as u32;
        startup.attributes = list;
        let mut info: ProcessInfo = unsafe { zeroed() };
        // SAFETY: All terminated strings and the attribute list outlive process creation.
        let spawned = unsafe {
            CreateProcessW(
                executable_wide.as_ptr(),
                command.as_mut_ptr(),
                null(),
                null(),
                1,
                0x0008_0000 | 0x0000_0004 | 0x0000_0400,
                environment.as_ptr().cast(),
                directory.as_ptr(),
                &startup.info,
                &mut info,
            )
        };
        let spawn_error = (spawned == 0).then(io::Error::last_os_error);
        unsafe {
            DeleteProcThreadAttributeList(list);
        }
        if let Some(error) = spawn_error {
            return Err(error);
        }
        let process = OwnedHandle(info.process);
        let thread = OwnedHandle(info.thread);
        // SAFETY: These newly created pipe endpoints are transferred to standard File owners.
        let reader = unsafe { File::from_raw_handle(output.0) };
        std::mem::forget(output);
        let input_file = unsafe { File::from_raw_handle(input.0) };
        std::mem::forget(input);
        let mut child = Self {
            process,
            job: None,
            _console: console,
            input: input_file,
        };
        let job = OwnedHandle(unsafe { CreateJobObjectW(null(), null()) });
        if job.0.is_null() {
            return Err(io::Error::last_os_error());
        }
        let mut limits = ExtendedLimits::default();
        limits.basic.flags = 0x2000;
        // SAFETY: The job only contains this suspended test child and its descendants.
        checked(unsafe {
            SetInformationJobObject(
                job.0,
                9,
                (&limits as *const ExtendedLimits).cast(),
                size_of::<ExtendedLimits>() as u32,
            )
        })?;
        checked(unsafe { AssignProcessToJobObject(job.0, child.process.0) })?;
        child.job = Some(job);
        if unsafe { ResumeThread(thread.0) } == u32::MAX {
            return Err(io::Error::last_os_error());
        }
        Ok((child, reader))
    }

    fn input(&mut self) -> &mut File {
        &mut self.input
    }

    fn status(&mut self) -> io::Result<Option<i32>> {
        let mut code = 0;
        // SAFETY: The process handle remains live and code is initialized by the API.
        checked(unsafe { GetExitCodeProcess(self.process.0, &mut code) })?;
        Ok((code != 259).then_some(code as i32))
    }
}

impl Drop for NativeProcess {
    fn drop(&mut self) {
        self.job.take();
        // SAFETY: Kill/wait target only the owned test process. The output-drain
        // thread stays live while closing ConPTY, avoiding pipe-backpressure deadlock.
        unsafe {
            TerminateProcess(self.process.0, 99);
            WaitForSingleObject(self.process.0, 5000);
        }
    }
}

pub struct Process {
    child: Child,
    input: Option<ChildStdin>,
}

impl Process {
    pub fn spawn(
        executable: &Path,
        directory: &Path,
        color: &str,
    ) -> io::Result<(Self, std::process::ChildStdout)> {
        let module = module_path!().split_once("::").unwrap().1;
        let mut child = Command::new(std::env::current_exe()?)
            .args([
                "--exact",
                &format!("{module}::native_pseudoconsole_host"),
                "--nocapture",
            ])
            .env("MSYNC_TEST_PTY_HOST", "1")
            .env("MSYNC_TEST_PTY_EXECUTABLE", executable)
            .env("MSYNC_TEST_PTY_DIRECTORY", directory)
            .env("MSYNC_TEST_PTY_COLOR", color)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()?;
        let input = child.stdin.take();
        let output = child.stdout.take().unwrap();
        Ok((Self { child, input }, output))
    }

    pub fn input(&mut self) -> &mut ChildStdin {
        self.input.as_mut().unwrap()
    }

    pub fn status(&mut self) -> io::Result<Option<i32>> {
        Ok(self
            .child
            .try_wait()?
            .map(|status| status.code().unwrap_or(-1)))
    }
}

impl Drop for Process {
    fn drop(&mut self) {
        self.input.take();
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

#[test]
fn native_pseudoconsole_host() {
    if std::env::var_os("MSYNC_TEST_PTY_HOST").is_none() {
        return;
    }
    let executable = std::env::var_os("MSYNC_TEST_PTY_EXECUTABLE").unwrap();
    let directory = std::env::var_os("MSYNC_TEST_PTY_DIRECTORY").unwrap();
    let color = std::env::var("MSYNC_TEST_PTY_COLOR").unwrap();
    let kinds = [-10_i32 as u32, -11_i32 as u32, -12_i32 as u32];
    // A redirected Cargo test host otherwise lends its pipe handles to the new
    // application even when a pseudoconsole is attached. Clear them only in this
    // isolated one-test helper process, never in the parallel test runner.
    let previous = kinds.map(|kind| unsafe { GetStdHandle(kind) });
    for kind in kinds {
        unsafe {
            SetStdHandle(kind, null_mut());
        }
    }
    let result = NativeProcess::spawn(Path::new(&executable), Path::new(&directory), &color);
    for (kind, handle) in kinds.into_iter().zip(previous) {
        unsafe {
            SetStdHandle(kind, handle);
        }
    }
    let (mut child, mut output) = result.unwrap();
    let mut input = child.input().try_clone().unwrap();
    thread::spawn(move || {
        let _ = io::copy(&mut io::stdin().lock(), &mut input);
    });
    let drain = thread::spawn(move || {
        let mut stdout = io::stdout().lock();
        let mut buffer = [0_u8; 8192];
        loop {
            match output.read(&mut buffer) {
                Ok(0) | Err(_) => break,
                Ok(count) => {
                    stdout.write_all(&buffer[..count]).unwrap();
                    stdout.flush().unwrap();
                }
            }
        }
    });
    let code = loop {
        if let Some(code) = child.status().unwrap() {
            break code;
        }
        thread::sleep(Duration::from_millis(5));
    };
    drop(child);
    drain.join().unwrap();
    std::process::exit(code);
}
