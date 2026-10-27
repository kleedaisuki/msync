use super::{CliError, CliResult, Issue};
use std::io;
use std::process::{Command, ExitStatus, Stdio};

pub(super) fn launch(name: &str, argument: &str, interactive: bool) -> CliResult<ExitStatus> {
    if argument.trim().is_empty() && !interactive {
        return Err(Issue::new(format!("/{name} needs an interactive terminal"))
            .help(format!(
                "Use '/{name} COMMAND' to run one command from piped input."
            ))
            .into());
    }
    let mut command = command(name, argument);
    // A one-shot command must not steal the remaining DSL input from a pipe.
    command.stdin(if interactive {
        Stdio::inherit()
    } else {
        Stdio::null()
    });
    command.stdout(Stdio::inherit()).stderr(Stdio::inherit());
    let _interrupts = if interactive {
        Some(InterruptGuard::new().map_err(|error| {
            Issue::new(format!("cannot prepare /{name}"))
                .field("cause", error.to_string())
                .help("Check that the terminal is attached and retry.")
        })?)
    } else {
        None
    };
    reset_child_signals(&mut command);
    command.status().map_err(|error| launch_error(name, &error))
}

fn command(name: &str, argument: &str) -> Command {
    let mut command = if name == "bash" {
        bash()
    } else {
        Command::new("pwsh")
    };
    match (name, argument.trim().is_empty()) {
        ("bash", true) => {
            command.arg("-i");
        }
        ("bash", false) => {
            command.args(["-c", argument]);
        }
        (_, true) => {
            command.arg("-NoLogo");
        }
        (_, false) => {
            command.args([
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                argument,
            ]);
        }
    }
    command
}

fn bash() -> Command {
    #[cfg(windows)]
    {
        let mut command = Command::new("wsl.exe");
        command.env("WSL_UTF8", "1");
        command.args(["--exec", "bash"]);
        command
    }
    #[cfg(not(windows))]
    {
        Command::new("bash")
    }
}

fn launch_error(name: &str, error: &io::Error) -> CliError {
    let program = match name {
        "bash" if cfg!(windows) => "wsl.exe --exec bash",
        "bash" => "bash",
        _ => "pwsh",
    };
    if error.kind() == io::ErrorKind::NotFound {
        let help = match name {
            "bash" if cfg!(windows) => "Install WSL and a Linux distribution, or use /pwsh.",
            "bash" => "Install Bash or add it to PATH, then retry /bash.",
            _ => "Install PowerShell 7 (pwsh) or add it to PATH, then retry /pwsh.",
        };
        return Issue::new(format!(
            "cannot start /{name}: shell executable was not found"
        ))
        .field("program", program)
        .field("cause", error.to_string())
        .help(help)
        .into();
    }
    Issue::new(format!("cannot start /{name}"))
        .field("program", program)
        .field("cause", error.to_string())
        .help("Check executable permissions and PATH; the REPL remains available.")
        .into()
}

#[cfg(unix)]
struct InterruptGuard {
    previous: libc::sigaction,
}

#[cfg(unix)]
impl InterruptGuard {
    fn new() -> io::Result<Self> {
        // The child shares the terminal's foreground group. Ignore SIGINT only
        // in this waiting parent; pre_exec restores the child's default action.
        let mut action: libc::sigaction = unsafe { std::mem::zeroed() };
        action.sa_sigaction = libc::SIG_IGN;
        let mut previous = std::mem::MaybeUninit::uninit();
        // SAFETY: Both action records are valid for this synchronous call.
        if unsafe { libc::sigaction(libc::SIGINT, &action, previous.as_mut_ptr()) } != 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(Self {
            previous: unsafe { previous.assume_init() },
        })
    }
}

#[cfg(unix)]
impl Drop for InterruptGuard {
    fn drop(&mut self) {
        // SAFETY: Restore the exact process-local disposition captured above.
        unsafe {
            libc::sigaction(libc::SIGINT, &self.previous, std::ptr::null_mut());
        }
    }
}

#[cfg(unix)]
fn reset_child_signals(command: &mut Command) {
    use std::os::unix::process::CommandExt;
    // SAFETY: This child-only hook uses async-signal-safe libc operations and
    // does not allocate or acquire a lock between fork and exec.
    unsafe {
        command.pre_exec(|| {
            if libc::signal(libc::SIGINT, libc::SIG_DFL) == libc::SIG_ERR {
                return Err(io::Error::last_os_error());
            }
            Ok(())
        });
    }
}

#[cfg(windows)]
struct InterruptGuard;

#[cfg(windows)]
unsafe extern "system" fn ignore_parent_interrupt(event: u32) -> i32 {
    use windows_sys::Win32::System::Console::{CTRL_BREAK_EVENT, CTRL_C_EVENT};
    i32::from(matches!(event, CTRL_C_EVENT | CTRL_BREAK_EVENT))
}

#[cfg(windows)]
impl InterruptGuard {
    fn new() -> io::Result<Self> {
        use windows_sys::Win32::System::Console::SetConsoleCtrlHandler;
        // A callback is not the inheritable "ignore Ctrl-C" process attribute:
        // the child receives normal console Ctrl-C while its parent survives.
        if unsafe { SetConsoleCtrlHandler(Some(ignore_parent_interrupt), 1) } == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(Self)
    }
}

#[cfg(windows)]
impl Drop for InterruptGuard {
    fn drop(&mut self) {
        use windows_sys::Win32::System::Console::SetConsoleCtrlHandler;
        unsafe {
            SetConsoleCtrlHandler(Some(ignore_parent_interrupt), 0);
        }
    }
}

#[cfg(windows)]
fn reset_child_signals(_: &mut Command) {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shell_arguments_are_preserved_as_one_command_not_reparsed_as_dsl() {
        let source = "printf '%s' 'a -> b := c'";
        let bash_command = command("bash", source);
        let args: Vec<_> = bash_command.get_args().collect();
        assert_eq!(args[args.len() - 2], "-c");
        assert_eq!(args[args.len() - 1], source);
        let pwsh_command = command("pwsh", "Write-Output 'a -> b'");
        assert_eq!(
            pwsh_command.get_args().last().unwrap(),
            "Write-Output 'a -> b'"
        );
    }
}
