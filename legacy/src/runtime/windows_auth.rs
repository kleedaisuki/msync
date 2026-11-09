//! Transport process-tree ownership; helpers cannot outlive their SSH client.

use std::io;
use std::mem::size_of;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::os::windows::process::CommandExt;
use std::process::{Child, Command};
use std::ptr::null;

use windows_sys::Win32::Foundation::{HANDLE, INVALID_HANDLE_VALUE};
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, TH32CS_SNAPTHREAD, THREADENTRY32, Thread32First, Thread32Next,
};
use windows_sys::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation,
    SetInformationJobObject, TerminateJobObject,
};
use windows_sys::Win32::System::Threading::{
    CREATE_NO_WINDOW, CREATE_SUSPENDED, OpenThread, ResumeThread, THREAD_SUSPEND_RESUME,
};

/// Parent-owned notification channel for cancelling the whole authentication
/// submission, rather than returning an empty password that OpenSSH may retry.
pub(crate) struct CancellationEvent {
    name: String,
    _event: OwnedHandle,
    stop: OwnedHandle,
    watcher: Option<std::thread::JoinHandle<()>>,
}

impl CancellationEvent {
    pub(crate) fn new(cancel: tokio_util::sync::CancellationToken) -> io::Result<Self> {
        use std::sync::atomic::{AtomicU64, Ordering};
        use windows_sys::Win32::Foundation::{ERROR_ALREADY_EXISTS, GetLastError, WAIT_OBJECT_0};
        use windows_sys::Win32::System::Threading::{
            CreateEventW, INFINITE, WaitForMultipleObjects,
        };
        static NEXT: AtomicU64 = AtomicU64::new(0);
        let name = format!(
            "Local\\msync-ssh-cancel-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        );
        let wide: Vec<u16> = name.encode_utf16().chain(Some(0)).collect();
        // SAFETY: the name is NUL-terminated and default security attributes do not
        // inherit the handle; the helper opens the name under the same logon DACL.
        let raw = unsafe { CreateEventW(null(), 1, 0, wide.as_ptr()) };
        if raw.is_null() {
            return Err(io::Error::last_os_error());
        }
        // Read collision status before any other Win32 call changes last error.
        let exists = unsafe { GetLastError() } == ERROR_ALREADY_EXISTS;
        // SAFETY: successful CreateEventW transferred a uniquely owned handle.
        let event = unsafe { OwnedHandle::from_raw_handle(raw) };
        if exists {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                "SSH cancellation event name collision",
            ));
        }
        // SAFETY: null name creates a new anonymous manual-reset event.
        let raw_stop = unsafe { CreateEventW(null(), 1, 0, null()) };
        if raw_stop.is_null() {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: successful CreateEventW transferred a uniquely owned handle.
        let stop = unsafe { OwnedHandle::from_raw_handle(raw_stop) };
        let watched_event = event.try_clone()?;
        let watched_stop = stop.try_clone()?;
        let trace = crate::telemetry::context();
        let watcher = std::thread::Builder::new()
            .name("msync-ssh-gui-cancel".into())
            .spawn(move || {
                let handles = [watched_event.as_raw_handle(), watched_stop.as_raw_handle()];
                // SAFETY: both owned handles remain live until the wait returns.
                let status = unsafe { WaitForMultipleObjects(2, handles.as_ptr(), 0, INFINITE) };
                if status == WAIT_OBJECT_0 {
                    cancel.cancel();
                }
                if status != WAIT_OBJECT_0 && status != WAIT_OBJECT_0 + 1 {
                    let error = io::Error::last_os_error();
                    if let Some(trace) = trace {
                        trace.io_error("ssh_cancel_event_wait", &error);
                    }
                    cancel.cancel();
                }
            })?;
        Ok(Self {
            name,
            _event: event,
            stop,
            watcher: Some(watcher),
        })
    }

    pub(crate) fn name(&self) -> &str {
        &self.name
    }
}

impl Drop for CancellationEvent {
    fn drop(&mut self) {
        use windows_sys::Win32::System::Threading::SetEvent;
        // SAFETY: the stop event remains owned throughout watcher shutdown.
        if unsafe { SetEvent(self.stop.as_raw_handle()) } == 0 {
            crate::telemetry::io_error("ssh_cancel_event_stop", &io::Error::last_os_error());
        }
        if let Some(watcher) = self.watcher.take() {
            let _ = watcher.join();
        }
    }
}

pub(crate) struct ProcessJob(OwnedHandle);

impl ProcessJob {
    fn new() -> io::Result<Self> {
        // SAFETY: no inherited handle or name; the returned handle is uniquely owned.
        let raw = unsafe { CreateJobObjectW(null(), null()) };
        if raw.is_null() {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: CreateJobObjectW returned a valid uniquely owned job handle.
        let job = Self(unsafe { OwnedHandle::from_raw_handle(raw) });
        let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        // SAFETY: the correctly sized limits structure remains live for this call.
        checked(unsafe {
            SetInformationJobObject(
                job.raw(),
                JobObjectExtendedLimitInformation,
                &limits as *const _ as *const _,
                size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            )
        })?;
        Ok(job)
    }

    fn raw(&self) -> HANDLE {
        self.0.as_raw_handle()
    }

    /// Termination is scoped to this transport tree and safe to request repeatedly.
    pub(crate) fn terminate(&self) -> io::Result<()> {
        // SAFETY: this owned job handle stays live for the whole call.
        checked(unsafe { TerminateJobObject(self.raw(), 1) })
    }

    fn enroll_and_resume(&self, process: HANDLE, id: u32) -> io::Result<()> {
        // The primary thread cannot create helpers before job enrollment.
        // SAFETY: the borrowed process handle refers to our CREATE_SUSPENDED child.
        checked(unsafe { AssignProcessToJobObject(self.raw(), process) })?;
        let thread = primary_thread(id)?;
        // SAFETY: the owned thread handle belongs to this still-suspended child.
        let count = unsafe { ResumeThread(thread.as_raw_handle()) };
        if count == u32::MAX {
            return Err(io::Error::last_os_error());
        }
        if count != 1 {
            return Err(io::Error::other(format!(
                "transport primary thread had unexpected suspend count {count}"
            )));
        }
        Ok(())
    }
}

pub(crate) fn spawn(command: &mut Command) -> io::Result<(Child, ProcessJob)> {
    let job = ProcessJob::new()?;
    let flags = creation_flags(command);
    let mut child = command.creation_flags(flags).spawn()?;
    if let Err(error) = job.enroll_and_resume(child.as_raw_handle(), child.id()) {
        let _ = job.terminate();
        let _ = child.kill();
        let _ = child.wait();
        return Err(error);
    }
    Ok((child, job))
}

pub(crate) fn spawn_async(
    command: &mut tokio::process::Command,
) -> io::Result<(tokio::process::Child, ProcessJob)> {
    let job = ProcessJob::new()?;
    let flags = creation_flags(command.as_std());
    let mut child = command.creation_flags(flags).kill_on_drop(true).spawn()?;
    let setup = child
        .raw_handle()
        .zip(child.id())
        .ok_or_else(|| io::Error::other("suspended transport process handle is unavailable"))
        .and_then(|(handle, id)| job.enroll_and_resume(handle, id));
    if let Err(error) = setup {
        let _ = job.terminate();
        let _ = child.start_kill();
        // Tokio's kill-on-drop reaper retains wait ownership for this failed spawn.
        return Err(error);
    }
    Ok((child, job))
}

fn creation_flags(command: &Command) -> u32 {
    let graphical = command.get_envs().any(|(name, value)| {
        name.to_string_lossy()
            .eq_ignore_ascii_case("SSH_ASKPASS_REQUIRE")
            && value.is_some_and(|value| value == "force")
    });
    if graphical {
        CREATE_SUSPENDED | CREATE_NO_WINDOW
    } else {
        CREATE_SUSPENDED
    }
}

fn primary_thread(process: u32) -> io::Result<OwnedHandle> {
    // SAFETY: snapshot creation takes no borrowed data.
    let raw = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0) };
    if raw == INVALID_HANDLE_VALUE {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: snapshot is a uniquely owned valid kernel handle.
    let snapshot = unsafe { OwnedHandle::from_raw_handle(raw) };
    let mut entry = THREADENTRY32 {
        dwSize: size_of::<THREADENTRY32>() as u32,
        ..Default::default()
    };
    // SAFETY: snapshot and correctly sized mutable entry remain live.
    let mut found = unsafe { Thread32First(snapshot.as_raw_handle(), &mut entry) };
    while found != 0 {
        if entry.th32OwnerProcessID == process {
            // SAFETY: only the matching child thread is opened, with resume access.
            let thread = unsafe { OpenThread(THREAD_SUSPEND_RESUME, 0, entry.th32ThreadID) };
            if thread.is_null() {
                return Err(io::Error::last_os_error());
            }
            // SAFETY: OpenThread returns a uniquely owned handle on success.
            return Ok(unsafe { OwnedHandle::from_raw_handle(thread) });
        }
        entry.dwSize = size_of::<THREADENTRY32>() as u32;
        // SAFETY: entry is reinitialized to the expected size before each call.
        found = unsafe { Thread32Next(snapshot.as_raw_handle(), &mut entry) };
    }
    Err(io::Error::other(format!(
        "cannot find primary thread of suspended transport process {process}"
    )))
}

fn checked(success: i32) -> io::Result<()> {
    if success == 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::io::Read;
    use std::path::{Path, PathBuf};
    use std::sync::mpsc;
    use std::thread;
    use std::time::{Duration, Instant};
    use windows_sys::Win32::Foundation::WAIT_OBJECT_0;
    use windows_sys::Win32::System::Threading::{
        OpenProcess, PROCESS_SYNCHRONIZE, WaitForSingleObject,
    };

    const HELPER: &str = "runtime::windows_auth::tests::process_tree_helper";

    fn helper(role: &str, root: &Path) -> Command {
        let mut command = Command::new(std::env::current_exe().unwrap());
        command
            .args(["--exact", HELPER, "--nocapture", "--test-threads=1"])
            .env("MSYNC_JOB_TEST_ROLE", role)
            .env("MSYNC_JOB_TEST_ROOT", root);
        command
    }

    fn workspace() -> tempfile::TempDir {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix("windows-job-")
            .tempdir_in(root)
            .unwrap()
    }

    fn descendant(root: &Path) -> OwnedHandle {
        let deadline = Instant::now() + Duration::from_secs(10);
        let pid = loop {
            if let Ok(text) = fs::read_to_string(root.join("pid"))
                && let Ok(pid) = text.parse::<u32>()
            {
                break pid;
            }
            assert!(Instant::now() < deadline, "descendant did not start");
            thread::sleep(Duration::from_millis(10));
        };
        // SAFETY: requesting only synchronization access to the fixture's PID.
        let raw = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, 0, pid) };
        assert!(!raw.is_null(), "{}", io::Error::last_os_error());
        // SAFETY: OpenProcess returned a uniquely owned valid handle.
        unsafe { OwnedHandle::from_raw_handle(raw) }
    }

    fn terminated(handle: &OwnedHandle) {
        // SAFETY: the owned fixture process handle is live, with synchronize access.
        assert_eq!(
            unsafe { WaitForSingleObject(handle.as_raw_handle(), 5000) },
            WAIT_OBJECT_0
        );
    }

    #[test]
    fn only_explicit_graphical_authentication_suppresses_console_creation() {
        let mut command = Command::new("ssh");
        assert_eq!(creation_flags(&command), CREATE_SUSPENDED);
        command.env("SSH_ASKPASS_REQUIRE", "force");
        assert_eq!(
            creation_flags(&command),
            CREATE_SUSPENDED | CREATE_NO_WINDOW
        );
        command.env("SSH_ASKPASS_REQUIRE", "never");
        assert_eq!(creation_flags(&command), CREATE_SUSPENDED);
        command.env_remove("SSH_ASKPASS_REQUIRE");
        assert_eq!(creation_flags(&command), CREATE_SUSPENDED);
    }

    #[test]
    fn named_gui_cancel_event_cancels_the_submission_token() {
        use windows_sys::Win32::System::Threading::{EVENT_MODIFY_STATE, OpenEventW, SetEvent};
        let cancel = tokio_util::sync::CancellationToken::new();
        let event = CancellationEvent::new(cancel.clone()).unwrap();
        let wide: Vec<u16> = event.name().encode_utf16().chain(Some(0)).collect();
        // SAFETY: helper-compatible event name is NUL-terminated and live.
        let raw = unsafe { OpenEventW(EVENT_MODIFY_STATE, 0, wide.as_ptr()) };
        assert!(!raw.is_null());
        // SAFETY: OpenEventW returned a uniquely owned valid handle.
        let helper = unsafe { OwnedHandle::from_raw_handle(raw) };
        // SAFETY: helper owns event-modification rights to this submission only.
        assert_ne!(unsafe { SetEvent(helper.as_raw_handle()) }, 0);
        let deadline = Instant::now() + Duration::from_secs(5);
        while !cancel.is_cancelled() {
            assert!(
                Instant::now() < deadline,
                "GUI cancel did not reach submission"
            );
            thread::sleep(Duration::from_millis(5));
        }
        drop(event);
    }

    #[test]
    fn unused_gui_event_shutdown_joins_without_cancelling_submission() {
        let cancel = tokio_util::sync::CancellationToken::new();
        let first = CancellationEvent::new(cancel.clone()).unwrap();
        let second = CancellationEvent::new(cancel.clone()).unwrap();
        assert_ne!(first.name(), second.name());
        let started = Instant::now();
        drop(first);
        drop(second);
        assert!(started.elapsed() < Duration::from_secs(5));
        assert!(!cancel.is_cancelled());
    }

    #[test]
    fn dropping_owned_job_terminates_descendants() {
        let root = workspace();
        let (mut child, job) = spawn(&mut helper("parent-wait", root.path())).unwrap();
        let descendant = descendant(root.path());
        drop(job);
        child.wait().unwrap();
        terminated(&descendant);
    }

    #[test]
    fn parent_exit_cannot_leave_descendant_holding_capture_pipe() {
        let root = workspace();
        let mut command = helper("parent-exit", root.path());
        command.stdout(std::process::Stdio::piped());
        let (mut child, job) = spawn(&mut command).unwrap();
        let descendant = descendant(root.path());
        let mut stdout = child.stdout.take().unwrap();
        assert!(child.wait().unwrap().success());
        job.terminate().unwrap();
        terminated(&descendant);
        let (sender, receiver) = mpsc::channel();
        thread::spawn(move || {
            let mut bytes = Vec::new();
            let result = stdout.read_to_end(&mut bytes);
            let _ = sender.send(result);
        });
        receiver
            .recv_timeout(Duration::from_secs(5))
            .expect("capture pipe leaked")
            .unwrap();
    }

    #[tokio::test]
    async fn asynchronous_transport_owns_its_helper_tree() {
        let root = workspace();
        let mut command = tokio::process::Command::from(helper("parent-wait", root.path()));
        let (mut child, job) = spawn_async(&mut command).unwrap();
        let descendant = descendant(root.path());
        job.terminate().unwrap();
        tokio::time::timeout(Duration::from_secs(5), child.wait())
            .await
            .unwrap()
            .unwrap();
        terminated(&descendant);
    }

    #[test]
    fn process_tree_helper() {
        let Ok(role) = std::env::var("MSYNC_JOB_TEST_ROLE") else {
            return;
        };
        let root = PathBuf::from(std::env::var_os("MSYNC_JOB_TEST_ROOT").unwrap());
        if role == "descendant" {
            thread::sleep(Duration::from_secs(300));
            return;
        }
        // The test intentionally delegates descendant reaping to the Job Object.
        #[allow(clippy::zombie_processes)]
        let child = helper("descendant", &root).spawn().unwrap();
        fs::write(root.join("pid"), child.id().to_string()).unwrap();
        if role == "parent-wait" {
            thread::sleep(Duration::from_secs(300));
        }
        // Deliberately leave the fixture child alive; the job owns its lifetime.
    }
}
