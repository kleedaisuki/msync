//! Headless protocol checks and opt-in owned-window native CredUI acceptance.
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

// These opt-in tests interact only with a newly spawned fixture helper's windows.
// A missing desktop or externally brokered UI is an explicit acceptance failure.
mod desktop {
    use super::*;
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Foundation::{HWND, LPARAM, WAIT_OBJECT_0, WAIT_TIMEOUT};
    use windows_sys::Win32::System::Threading::WaitForSingleObject;
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        ES_PASSWORD, EnumChildWindows, EnumWindows, GWL_STYLE, GetClassNameW, GetDlgItem,
        GetWindowLongPtrW, GetWindowThreadProcessId, IDCANCEL, IDOK, IsWindowVisible,
        SMTO_ABORTIFHUNG, SendMessageTimeoutW, WM_COMMAND, WM_GETTEXT, WM_SETTEXT,
    };

    const SECRET: &str = "msync-fixture-密钥-🗝";

    struct Helper(Option<Child>);

    impl Drop for Helper {
        fn drop(&mut self) {
            if let Some(child) = &mut self.0 {
                let _ = child.kill();
                let _ = child.wait();
            }
        }
    }

    unsafe extern "system" fn collect(window: HWND, output: LPARAM) -> i32 {
        // SAFETY: Every enumeration below borrows this Vec for its synchronous call.
        unsafe { &mut *(output as *mut Vec<HWND>) }.push(window);
        1
    }

    fn windows(parent: Option<HWND>) -> Vec<HWND> {
        let mut output = Vec::new();
        let pointer = &mut output as *mut Vec<HWND> as LPARAM;
        // SAFETY: collect borrows output only until synchronous enumeration returns.
        unsafe {
            match parent {
                Some(parent) => {
                    EnumChildWindows(parent, Some(collect), pointer);
                }
                None => {
                    EnumWindows(Some(collect), pointer);
                }
            }
        }
        output
    }

    fn owned(window: HWND, pid: u32) -> bool {
        let mut owner = 0;
        // SAFETY: window is an enumerated HWND, and owner is valid writable storage.
        unsafe { GetWindowThreadProcessId(window, &mut owner) };
        owner == pid
    }

    fn text(window: HWND, class: bool) -> String {
        let mut buffer = [0_u16; 4096];
        // SAFETY: Both APIs receive the actual writable buffer capacity.
        let length = unsafe {
            if class {
                GetClassNameW(window, buffer.as_mut_ptr(), buffer.len() as i32)
            } else {
                let mut copied = 0;
                SendMessageTimeoutW(
                    window,
                    WM_GETTEXT,
                    buffer.len(),
                    buffer.as_mut_ptr() as isize,
                    SMTO_ABORTIFHUNG,
                    1000,
                    &mut copied,
                );
                copied as i32
            }
        };
        String::from_utf16_lossy(&buffer[..length.max(0) as usize])
    }

    fn message(window: HWND, kind: u32, command: usize, value: isize) {
        let mut result = 0;
        // SAFETY: window was verified to belong to our helper. WM_SETTEXT borrows
        // a live NUL-terminated buffer; WM_COMMAND identifies our owned button.
        assert_ne!(
            unsafe {
                SendMessageTimeoutW(
                    window,
                    kind,
                    command,
                    value,
                    SMTO_ABORTIFHUNG,
                    2000,
                    &mut result,
                )
            },
            0,
            "owned dialog did not accept message: {}",
            std::io::Error::last_os_error()
        );
    }

    fn dialog(helper: &mut Helper) -> HWND {
        let pid = helper.0.as_ref().unwrap().id();
        let deadline = Instant::now() + Duration::from_secs(15);
        loop {
            let candidates: Vec<_> = windows(None)
                .into_iter()
                .filter(|window| owned(*window, pid) && unsafe { IsWindowVisible(*window) } != 0)
                .collect();
            if let Some(window) = candidates.iter().find(|window| {
                text(**window, false).contains("msync")
                    && !unsafe { GetDlgItem(**window, IDCANCEL) }.is_null()
            }) {
                return *window;
            }
            if let Some(status) = helper.0.as_mut().unwrap().try_wait().unwrap() {
                let output = helper.0.take().unwrap().wait_with_output().unwrap();
                panic!(
                    "native CredUI exited before an owned dialog appeared ({status}); stderr={}",
                    String::from_utf8_lossy(&output.stderr)
                );
            }
            assert!(
                Instant::now() < deadline,
                "no owned msync CredUI dialog; desktop may be unavailable or UI brokered externally; owned visible windows={:?}",
                candidates
                    .iter()
                    .map(|window| (text(*window, true), text(*window, false)))
                    .collect::<Vec<_>>()
            );
            thread::sleep(Duration::from_millis(20));
        }
    }

    fn journey(accept: bool) {
        let name = format!("Local\\msync-desktop-{}-{accept}", std::process::id());
        let wide: Vec<_> = OsStr::new(&name).encode_wide().chain(Some(0)).collect();
        // SAFETY: The private event stays owned throughout the helper lifetime.
        let raw = unsafe { CreateEventW(std::ptr::null(), 1, 0, wide.as_ptr()) };
        assert!(!raw.is_null(), "{}", std::io::Error::last_os_error());
        let event = unsafe { OwnedHandle::from_raw_handle(raw) };
        let mut helper = Helper(Some(
            Command::new(env!("CARGO_BIN_EXE_msync"))
                .arg("fixture@localhost's password: ")
                .env("MSYNC_ASKPASS_INTERNAL", "msync-native-askpass-v1")
                .env("MSYNC_SSH_CANCEL_EVENT", name)
                .env("MSYNC_SSH_ALIAS", "Acceptance_开发机")
                .env("MSYNC_SSH_USER", "fixture")
                .env("MSYNC_SSH_PORT", "2222")
                .env("MSYNC_SSH_TASK", "native-window-acceptance.msync")
                .env_remove("SSH_ASKPASS_PROMPT")
                .stdin(Stdio::null())
                .stdout(Stdio::piped())
                .stderr(Stdio::piped())
                .spawn()
                .unwrap(),
        ));
        let window = dialog(&mut helper);
        let pid = helper.0.as_ref().unwrap().id();
        let controls: Vec<_> = windows(Some(window))
            .into_iter()
            .filter(|item| owned(*item, pid))
            .collect();
        // Inspect labels before entering the fixture response, never read a secret.
        let labels = controls
            .iter()
            .map(|item| text(*item, false))
            .collect::<Vec<_>>()
            .join("\n");
        for required in [
            "Acceptance_开发机",
            "native-window-acceptance.msync",
            "fixture@localhost",
        ] {
            assert!(
                labels.contains(required),
                "native dialog omitted {required}: {labels:?}"
            );
        }
        assert!(text(window, false).contains("SSH authentication"));
        let password = controls
            .iter()
            .find(|item| {
                text(**item, true).eq_ignore_ascii_case("Edit")
                    && unsafe { GetWindowLongPtrW(**item, GWL_STYLE) } & ES_PASSWORD as isize != 0
            })
            .copied()
            .expect("owned credential dialog has no masked password field");
        let secret: Vec<_> = SECRET.encode_utf16().chain(Some(0)).collect();
        message(password, WM_SETTEXT, 0, secret.as_ptr() as isize);
        let button = unsafe { GetDlgItem(window, if accept { IDOK } else { IDCANCEL }) };
        assert!(
            !button.is_null() && owned(button, pid),
            "dialog action is not owned by helper"
        );
        // WM_COMMAND does not depend on the runner granting foreground focus,
        // unlike BM_CLICK. It invokes the actual owned native dialog handler.
        message(
            window,
            WM_COMMAND,
            if accept { IDOK } else { IDCANCEL } as usize,
            button as isize,
        );
        let deadline = Instant::now() + Duration::from_secs(5);
        while helper.0.as_mut().unwrap().try_wait().unwrap().is_none() {
            assert!(
                Instant::now() < deadline,
                "native dialog action did not complete the owned helper"
            );
            thread::sleep(Duration::from_millis(10));
        }
        let output = helper.0.take().unwrap().wait_with_output().unwrap();
        assert!(!String::from_utf8_lossy(&output.stderr).contains(SECRET));
        if accept {
            assert!(
                output.status.success(),
                "{}",
                String::from_utf8_lossy(&output.stderr)
            );
            assert_eq!(output.stdout, format!("{SECRET}\n").as_bytes());
            assert_eq!(
                unsafe { WaitForSingleObject(event.as_raw_handle(), 0) },
                WAIT_TIMEOUT
            );
        } else {
            assert!(!output.status.success());
            assert!(output.stdout.is_empty());
            assert_eq!(
                unsafe { WaitForSingleObject(event.as_raw_handle(), 0) },
                WAIT_OBJECT_0
            );
        }
    }

    #[test]
    #[ignore = "requires a native Windows desktop; interacts only with owned fixture CredUI"]
    fn native_credential_window_has_context_masking_and_exact_accept_response() {
        journey(true);
    }

    #[test]
    #[ignore = "requires a native Windows desktop; interacts only with owned fixture CredUI"]
    fn native_credential_window_cancel_signals_submission_without_response() {
        journey(false);
    }
}
