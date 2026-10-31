//! Owned-child completion events without taking over SIGCHLD or child reaping.

use std::process::Child;
use std::time::Duration;

#[cfg(target_os = "linux")]
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};

pub(super) struct ChildWait {
    #[cfg(target_os = "linux")]
    descriptor: Option<OwnedFd>,
    #[cfg(target_os = "linux")]
    fallback: Option<i32>,
}

impl ChildWait {
    pub(super) fn new(_child: &Child) -> Self {
        #[cfg(target_os = "linux")]
        {
            // The unreaped Child owns this PID; pidfd_open returns a new CLOEXEC
            // descriptor even when that child has already become a zombie.
            let raw = unsafe { libc::syscall(libc::SYS_pidfd_open, _child.id(), 0_u32) };
            if raw >= 0 {
                return Self {
                    // SAFETY: this successful syscall transferred a new descriptor.
                    descriptor: Some(unsafe { OwnedFd::from_raw_fd(raw as i32) }),
                    fallback: None,
                };
            }
            Self {
                descriptor: None,
                fallback: std::io::Error::last_os_error().raw_os_error(),
            }
        }
        #[cfg(not(target_os = "linux"))]
        Self {}
    }

    pub(super) fn strategy(&self) -> &'static str {
        #[cfg(target_os = "linux")]
        if self.descriptor.is_some() {
            return "pidfd";
        }
        #[cfg(windows)]
        return "process_handle";
        #[cfg(not(windows))]
        "poll_interval"
    }

    pub(super) fn fallback_errno(&self) -> Option<i32> {
        #[cfg(target_os = "linux")]
        return self.fallback;
        #[cfg(not(target_os = "linux"))]
        None
    }

    pub(super) fn pause(&self, _child: &Child) {
        #[cfg(target_os = "linux")]
        if let Some(descriptor) = &self.descriptor {
            let mut event = libc::pollfd {
                fd: descriptor.as_raw_fd(),
                events: libc::POLLIN,
                revents: 0,
            };
            // SAFETY: the owned descriptor and stack pollfd remain live throughout.
            let result = unsafe { libc::poll(&mut event, 1, 10) };
            if result >= 0 && event.revents & (libc::POLLERR | libc::POLLNVAL) == 0 {
                return;
            }
            if result < 0
                && std::io::Error::last_os_error().kind() == std::io::ErrorKind::Interrupted
            {
                return;
            }
        }
        #[cfg(windows)]
        {
            use std::os::windows::io::AsRawHandle;
            use windows_sys::Win32::Foundation::WAIT_FAILED;
            use windows_sys::Win32::System::Threading::WaitForSingleObject;
            // SAFETY: the borrowed Child keeps its process handle open during wait.
            if unsafe { WaitForSingleObject(_child.as_raw_handle(), 10) } != WAIT_FAILED {
                return;
            }
        }
        std::thread::sleep(Duration::from_millis(10));
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::process::Command;
    use std::time::Instant;

    #[test]
    fn completion_retains_child_exit_status_and_exclusive_reaping() {
        for code in [0, 7] {
            #[cfg(unix)]
            let mut command = {
                let mut command = Command::new("sh");
                command.args(["-c", &format!("exit {code}")]);
                command
            };
            #[cfg(windows)]
            let mut command = {
                let mut command = Command::new("cmd");
                command.args(["/C", &format!("exit {code}")]);
                command
            };
            let mut child = command.spawn().unwrap();
            let ready = ChildWait::new(&child);
            let deadline = Instant::now() + Duration::from_secs(5);
            let status = loop {
                if let Some(status) = child.try_wait().unwrap() {
                    break status;
                }
                if Instant::now() >= deadline {
                    child.kill().unwrap();
                    child.wait().unwrap();
                    panic!("owned child failed to become ready");
                }
                ready.pause(&child);
            };
            assert_eq!(status.code(), Some(code));
            assert_eq!(child.wait().unwrap(), status);
        }
    }
}
