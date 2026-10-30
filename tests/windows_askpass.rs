//! Headless protocol checks; these do not claim a visible Windows GUI journey.
#![cfg(windows)]

use std::ffi::OsStr;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{FromRawHandle, OwnedHandle};
use std::process::{Child, Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant};
use windows_sys::Win32::System::Threading::CreateEventW;

fn finish(mut child: Child) -> Output {
    let deadline = Instant::now() + Duration::from_secs(5);
    while child.try_wait().unwrap().is_none() {
        if Instant::now() >= deadline {
            let _ = child.kill();
            let output = child.wait_with_output().unwrap();
            panic!(
                "headless helper did not exit: {}",
                String::from_utf8_lossy(&output.stderr)
            );
        }
        thread::sleep(Duration::from_millis(10));
    }
    child.wait_with_output().unwrap()
}

#[test]
fn ordinary_version_flag_is_not_a_prompt_even_with_inherited_helper_marker() {
    let child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .arg("--version")
        .env("MSYNC_ASKPASS_INTERNAL", "msync-native-askpass-v1")
        .env("SSH_ASKPASS_PROMPT", "confirm")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let output = finish(child);
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        String::from_utf8(output.stdout).unwrap().trim(),
        concat!("msync ", env!("CARGO_PKG_VERSION"))
    );
}

#[test]
fn pre_cancelled_native_helper_exits_without_stdout_or_opening_a_dialog() {
    let name = format!("Local\\msync-askpass-pre-cancel-{}", std::process::id());
    let wide: Vec<_> = OsStr::new(&name).encode_wide().chain(Some(0)).collect();
    // SAFETY: Private manual-reset event starts signaled. The owned handle
    // remains live until the helper subprocess has observed cancellation.
    let event = unsafe { CreateEventW(std::ptr::null(), 1, 1, wide.as_ptr()) };
    assert!(!event.is_null(), "{}", std::io::Error::last_os_error());
    let _event = unsafe { OwnedHandle::from_raw_handle(event) };
    let child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .arg("fixture@localhost's password: ")
        .env("MSYNC_ASKPASS_INTERNAL", "msync-native-askpass-v1")
        .env("MSYNC_SSH_CANCEL_EVENT", name)
        .env_remove("SSH_ASKPASS_PROMPT")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let output = finish(child);
    assert!(!output.status.success());
    assert!(
        output.stdout.is_empty(),
        "helper wrote data after cancellation"
    );
    assert!(String::from_utf8_lossy(&output.stderr).contains("cancelled"));
}
