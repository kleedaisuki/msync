//! Process groups/jobs support explicit destructive stop, never implicit kill.
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
    use windows_sys::Win32::System::Threading::CREATE_NEW_PROCESS_GROUP;
    // Console Ctrl-C must cancel the interpreter, not interrupt an admitted child.
    // CommandExt provides a setter, not a getter for merging caller flags.
    command.creation_flags(CREATE_NEW_PROCESS_GROUP);
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

#[cfg(windows)]
mod windows {
    use super::*;
    use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
    use std::ptr::null;

    use windows_sys::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, TerminateJobObject,
    };

    pub(in crate::process) struct Tree {
        job: OwnedHandle,
    }

    impl Tree {
        pub(in crate::process) fn attach(child: &Child, _pid: u32) -> io::Result<Self> {
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
            checked(unsafe { AssignProcessToJobObject(tree.job.as_raw_handle(), process) })?;
            Ok(tree)
        }

        pub(in crate::process) fn terminate(&self) {
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
