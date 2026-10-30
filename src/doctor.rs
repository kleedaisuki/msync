//! Read-only capability inventory. A located executable is not a runtime proof.

use std::env;
use std::fs;
use std::path::{Path, PathBuf};

use super::{Color, print_text};
use crate::presentation::{self, CliResult};

pub(super) fn run(color: Color) -> CliResult {
    let mut output = String::from("msync doctor — read-only capability inventory\n");
    for name in [
        "static compilation / IR preview",
        "script formatting",
        "ordinary file assignments",
        "asynchronous external programs",
    ] {
        item(
            &mut output,
            name,
            "available (built in)",
            "No isolation provider or host driver is required.",
            color,
        );
    }
    for (name, executable, condition) in [
        (
            "WSL file endpoints",
            "wsl",
            "Distribution, guest helpers and endpoint access are not checked; WSL is not started.",
        ),
        (
            "HTTP sources",
            "curl",
            "Server reachability, authentication and client version are not checked.",
        ),
        (
            "SSH client",
            "ssh",
            "Authentication and remote filesystem capabilities are not checked; the SSH client is not executed.",
        ),
        (
            "SCP client",
            "scp",
            "SFTP-default behavior and server compatibility are not checked.",
        ),
    ] {
        let located = executable_path(executable);
        let (status, detail) = match located {
            Some(path) => (
                "located; runtime unverified",
                format!("{}; {condition}", path.display()),
            ),
            None => (
                "not located; runtime unverified",
                format!("Cannot locate {executable} in the executable search paths. {condition}"),
            ),
        };
        item(&mut output, name, status, &detail, color);
    }
    output.push_str(
        "\nOnly requested features are evaluated during execution. This inventory does not launch\n",
    );
    output.push_str(
        "transport clients or user programs, install a driver, or change host / SSH configuration.\n",
    );
    output.push_str(
        "Missing optional transport clients do not disable ordinary copies, formatting,\n",
    );
    output.push_str("static compilation, or native external programs.\n");
    print_text(&output)
}

fn item(output: &mut String, name: &str, status: &str, detail: &str, color: Color) {
    output.push_str(&format!(
        "  {}: {}\n    {}\n",
        presentation::paint(name, "1;36", color.stdout()),
        presentation::escape(status),
        presentation::escape(detail),
    ));
}

fn executable_path(name: &str) -> Option<PathBuf> {
    let mut directories = Vec::new();
    #[cfg(windows)]
    {
        if let Ok(executable) = env::current_exe()
            && let Some(parent) = executable.parent()
        {
            directories.push(parent.to_owned());
        }
        if let Ok(cwd) = env::current_dir() {
            directories.push(cwd);
        }
        if let Some(root) = env::var_os("SystemRoot") {
            directories.push(PathBuf::from(&root).join("System32"));
            directories.push(PathBuf::from(root));
        }
    }
    if let Some(paths) = env::var_os("PATH") {
        directories.extend(env::split_paths(&paths));
    }
    let filename = if cfg!(windows) {
        format!("{name}.exe")
    } else {
        name.to_owned()
    };
    directories
        .into_iter()
        .map(|directory| directory.join(&filename))
        .find(|path| local_candidate(path) && ordinary_file(path))
}

#[cfg(windows)]
fn local_candidate(path: &Path) -> bool {
    use std::path::{Component, Prefix};
    use windows_sys::Win32::Storage::FileSystem::GetDriveTypeW;
    use windows_sys::Win32::System::WindowsProgramming::DRIVE_REMOTE;

    let Ok(absolute) = std::path::absolute(path) else {
        return false;
    };
    let Some(Component::Prefix(prefix)) = absolute.components().next() else {
        return false;
    };
    let drive = match prefix.kind() {
        Prefix::Disk(drive) | Prefix::VerbatimDisk(drive) => drive,
        _ => return false,
    };
    let root = [u16::from(drive), u16::from(b':'), u16::from(b'\\'), 0];
    // SAFETY: The query receives a live, NUL-terminated local drive-root buffer;
    // it is an inventory lookup and never opens a remote executable or DLL.
    unsafe { GetDriveTypeW(root.as_ptr()) != DRIVE_REMOTE }
}

#[cfg(not(windows))]
fn local_candidate(_: &Path) -> bool {
    true
}

fn ordinary_file(path: &Path) -> bool {
    fs::metadata(path).is_ok_and(|metadata| metadata.is_file())
}
