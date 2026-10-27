use std::fs::File;
use std::io;
use std::os::fd::{AsRawFd, FromRawFd, RawFd};
use std::os::unix::process::CommandExt;
use std::path::Path;
use std::process::{Child, Command, Stdio};

pub struct Process {
    child: Child,
    input: File,
    master: RawFd,
}

impl Process {
    pub fn spawn(executable: &Path, directory: &Path, color: &str) -> io::Result<(Self, File)> {
        let mut master = -1;
        let mut slave = -1;
        let size = libc::winsize {
            ws_row: 30,
            ws_col: 100,
            ws_xpixel: 0,
            ws_ypixel: 0,
        };
        // SAFETY: openpty initializes both descriptors and borrows size only during this call.
        if unsafe {
            libc::openpty(
                &mut master,
                &mut slave,
                std::ptr::null_mut(),
                std::ptr::null(),
                &size,
            )
        } != 0
        {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: The successfully returned descriptors are uniquely owned here.
        let input = unsafe { File::from_raw_fd(master) };
        let slave = unsafe { File::from_raw_fd(slave) };
        for fd in [input.as_raw_fd(), slave.as_raw_fd()] {
            // SAFETY: Both owned descriptors are valid. Only dup2's stdio copies
            // should survive exec, not the parent's PTY master or spare slave.
            if unsafe { libc::fcntl(fd, libc::F_SETFD, libc::FD_CLOEXEC) } < 0 {
                return Err(io::Error::last_os_error());
            }
        }
        let reader = input.try_clone()?;
        let mut command = Command::new(executable);
        command
            .args(["repl", "--color", color])
            .current_dir(directory)
            .env("TERM", "xterm-256color")
            .env("HOME", directory)
            .env("HISTFILE", directory.join(".test_bash_history"))
            .env_remove("NO_COLOR")
            .stdin(Stdio::from(slave.try_clone()?))
            .stdout(Stdio::from(slave.try_clone()?))
            .stderr(Stdio::from(slave));
        // SAFETY: Only async-signal-safe process/session syscalls run after fork;
        // fd 0 is already the privately owned slave terminal in the child.
        unsafe {
            command.pre_exec(|| {
                if libc::setsid() < 0 || libc::ioctl(0, libc::TIOCSCTTY, 0) < 0 {
                    return Err(io::Error::last_os_error());
                }
                Ok(())
            });
        }
        let child = command.spawn()?;
        Ok((
            Self {
                child,
                input,
                master,
            },
            reader,
        ))
    }

    pub fn input(&mut self) -> &mut File {
        &mut self.input
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
        // SAFETY: The test child owns a new session; never signal the test runner's
        // group. Kill an interactive descendant's foreground group as well.
        unsafe {
            let foreground = libc::tcgetpgrp(self.master);
            if foreground > 0 && foreground != libc::getpgrp() {
                libc::kill(-foreground, libc::SIGKILL);
            }
            libc::kill(-(self.child.id() as i32), libc::SIGKILL);
        }
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}
