//! Process-tree ownership, separate from transaction resource mediation.
//!
//! Unix process groups cover descendants that remain in their inherited group;
//! this is lifecycle management, not a hostile-child sandbox against setsid.

use std::io;
use std::process::Command;

use tokio::process::Child;

#[cfg(unix)]
pub(super) fn configure(command: &mut Command) {
    use std::os::unix::process::CommandExt;
    command.process_group(0);
}

#[cfg(windows)]
pub(super) fn configure(command: &mut Command) {
    use std::os::windows::process::CommandExt;
    use windows_sys::Win32::System::Threading::CREATE_SUSPENDED;
    command.creation_flags(CREATE_SUSPENDED);
}

#[cfg(not(any(unix, windows)))]
pub(super) fn configure(_: &mut Command) {}

#[cfg(unix)]
pub(super) struct Tree {
    group: i32,
    terminated: std::cell::Cell<bool>,
}

#[cfg(unix)]
impl Tree {
    pub(super) fn attach(_: &Child, pid: u32) -> io::Result<Self> {
        Ok(Self {
            group: pid as i32,
            terminated: std::cell::Cell::new(false),
        })
    }

    pub(super) fn terminate(&self) {
        if self.terminated.replace(true) {
            return;
        }
        // SAFETY: configure creates a fresh group whose ID is this owned PID.
        // ESRCH is normal after the complete group has already exited.
        unsafe { libc::kill(-self.group, libc::SIGKILL) };
    }
}

#[cfg(unix)]
impl Drop for Tree {
    fn drop(&mut self) {
        self.terminate();
    }
}

#[cfg(windows)]
mod windows {
    use super::*;
    use std::mem::size_of;
    use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
    use std::ptr::null;

    use windows_sys::Win32::Foundation::INVALID_HANDLE_VALUE;
    use windows_sys::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, TH32CS_SNAPTHREAD, THREADENTRY32, Thread32First, Thread32Next,
    };
    use windows_sys::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation,
        SetInformationJobObject, TerminateJobObject,
    };
    use windows_sys::Win32::System::Threading::{OpenThread, ResumeThread, THREAD_SUSPEND_RESUME};

    pub(in crate::process) struct Tree {
        job: OwnedHandle,
    }

    impl Tree {
        pub(in crate::process) fn attach(child: &Child, pid: u32) -> io::Result<Self> {
            let process = child
                .raw_handle()
                .ok_or_else(|| io::Error::other("child has no process handle"))?;
            let job = unsafe { CreateJobObjectW(null(), null()) };
            if job.is_null() {
                return Err(io::Error::last_os_error());
            }
            // SAFETY: CreateJobObjectW returned a fresh non-null owned handle.
            let tree = Self {
                job: unsafe { OwnedHandle::from_raw_handle(job) },
            };
            let mut limits = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            checked(unsafe {
                SetInformationJobObject(
                    tree.job.as_raw_handle(),
                    JobObjectExtendedLimitInformation,
                    (&limits as *const JOBOBJECT_EXTENDED_LIMIT_INFORMATION).cast(),
                    size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
                )
            })?;
            checked(unsafe { AssignProcessToJobObject(tree.job.as_raw_handle(), process) })?;
            // CREATE_SUSPENDED closes the child-to-grandchild ownership race.
            resume_initial_thread(pid)?;
            Ok(tree)
        }

        pub(in crate::process) fn terminate(&self) {
            // Closing the job is the final guard even if this request races exit.
            unsafe { TerminateJobObject(self.job.as_raw_handle(), 1) };
        }
    }

    fn checked(value: i32) -> io::Result<()> {
        if value == 0 {
            Err(io::Error::last_os_error())
        } else {
            Ok(())
        }
    }

    fn resume_initial_thread(pid: u32) -> io::Result<()> {
        // Standard Child does not expose hThread. A CREATE_SUSPENDED process has
        // exactly its initial thread; use documented Toolhelp/ResumeThread APIs.
        // https://learn.microsoft.com/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject
        let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0) };
        if snapshot == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: The snapshot is a new handle, checked against its failure value.
        let snapshot = unsafe { OwnedHandle::from_raw_handle(snapshot) };
        let mut entry = THREADENTRY32 {
            dwSize: size_of::<THREADENTRY32>() as u32,
            ..Default::default()
        };
        let mut more = unsafe { Thread32First(snapshot.as_raw_handle(), &mut entry) };
        while more != 0 {
            if entry.th32OwnerProcessID == pid {
                let thread = unsafe { OpenThread(THREAD_SUSPEND_RESUME, 0, entry.th32ThreadID) };
                if thread.is_null() {
                    return Err(io::Error::last_os_error());
                }
                // SAFETY: OpenThread returned a new, non-null owned handle.
                let thread = unsafe { OwnedHandle::from_raw_handle(thread) };
                if unsafe { ResumeThread(thread.as_raw_handle()) } == u32::MAX {
                    return Err(io::Error::last_os_error());
                }
                return Ok(());
            }
            more = unsafe { Thread32Next(snapshot.as_raw_handle(), &mut entry) };
        }
        Err(io::Error::new(
            io::ErrorKind::NotFound,
            "suspended child has no initial thread",
        ))
    }
}

#[cfg(windows)]
pub(super) use windows::Tree;

#[cfg(not(any(unix, windows)))]
pub(super) struct Tree;

#[cfg(not(any(unix, windows)))]
impl Tree {
    pub(super) fn attach(_: &Child, _: u32) -> io::Result<Self> {
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "process-tree ownership is unavailable on this platform",
        ))
    }

    pub(super) fn terminate(&self) {}
}
