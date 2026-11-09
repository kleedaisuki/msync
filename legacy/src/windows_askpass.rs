//! Private OpenSSH askpass protocol; dispatched before normal CLI/trace setup.

use std::ffi::{OsStr, OsString};
use std::io::{self, Write};

const MARKER: &str = "msync-native-askpass-v1";
const CANCELLED: u32 = 1223;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum PromptKind {
    Secret,
    Confirm,
    Notice,
}

fn request(
    marker: Option<&OsStr>,
    arguments: &[OsString],
    mode: Option<&OsStr>,
) -> Option<PromptKind> {
    if marker != Some(OsStr::new(MARKER)) || arguments.len() != 2 {
        return None;
    }
    let prompt = arguments[1].to_str()?.trim();
    if prompt.is_empty() || prompt.contains('\0') || prompt.starts_with('-') {
        return None;
    }
    if mode == Some(OsStr::new("none")) {
        return Some(PromptKind::Notice);
    }
    let host_key = prompt.contains("yes/no")
        && (prompt.contains("Are you sure you want to continue connecting")
            || (prompt.contains("authenticity") && prompt.contains("fingerprint")));
    if mode == Some(OsStr::new("confirm")) || host_key {
        return Some(PromptKind::Confirm);
    }
    Some(PromptKind::Secret)
}

struct Secret<T: Copy + Default>(Vec<T>);

impl<T: Copy + Default> Drop for Secret<T> {
    fn drop(&mut self) {
        for value in &mut self.0 {
            // Volatile writes prevent dead-store elimination when the allocation
            // is released. This erases owned buffers, not all OS/runtime copies.
            unsafe { std::ptr::write_volatile(value, T::default()) };
        }
    }
}

fn answer(password: &[u16], status: u32) -> io::Result<Secret<u8>> {
    match status {
        0 => {}
        CANCELLED => {
            return Err(io::Error::new(
                io::ErrorKind::Interrupted,
                "SSH authentication cancelled",
            ));
        }
        code => return Err(io::Error::from_raw_os_error(code as i32)),
    }
    let end = password.iter().position(|unit| *unit == 0).ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            "credential dialog returned an unterminated password",
        )
    })?;
    let password = &password[..end];
    if char::decode_utf16(password.iter().copied()).any(|value| value.is_err()) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "credential is not valid Unicode",
        ));
    }
    if password.iter().any(|unit| matches!(*unit, 10 | 13)) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "OpenSSH askpass cannot transmit a multiline credential",
        ));
    }
    // OpenSSH's askpass reader accepts at most 1023 response bytes. Allocate
    // once so encoding never frees an unwiped secret through reallocation.
    let mut bytes = Secret(vec![0_u8; 1024]);
    let mut length = 0;
    let mut encoded = Secret(vec![0_u8; 4]);
    for decoded in char::decode_utf16(password.iter().copied()) {
        let text = decoded.unwrap().encode_utf8(&mut encoded.0);
        if length + text.len() > 1023 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "credential exceeds OpenSSH askpass's 1023-byte response limit",
            ));
        }
        bytes.0[length..length + text.len()].copy_from_slice(text.as_bytes());
        length += text.len();
    }
    bytes.0.truncate(length);
    Ok(bytes)
}

fn write_answer(writer: &mut impl Write, answer: &Secret<u8>) -> io::Result<()> {
    writer.write_all(&answer.0)?;
    writer.write_all(b"\n")?;
    writer.flush()
}

fn confirmation_answer(accepted: bool, notice: bool) -> Secret<u8> {
    Secret(if notice {
        Vec::new()
    } else if accepted {
        b"yes".to_vec()
    } else {
        b"no".to_vec()
    })
}

const DIALOG_LIMIT: usize = 1024;
const DIALOG_NOTICE: &str = "Credentials are used only for this authentication, never saved to configuration, history or IR. Cancel stops authentication; earlier committed files are not rolled back.";

#[derive(Default)]
struct DialogMetadata {
    connection: Option<OsString>,
    user: Option<OsString>,
    port: Option<OsString>,
    task: Option<OsString>,
}

fn display_text(text: &OsStr, layout: bool) -> String {
    let mut output = String::new();
    for ch in text.to_string_lossy().chars() {
        if ch.is_control() && !(layout && matches!(ch, '\n' | '\r' | '\t')) {
            output.extend(ch.escape_default());
        } else {
            output.push(ch);
        }
    }
    output
}

fn shorten(text: &str, limit: usize) -> std::borrow::Cow<'_, str> {
    if text.encode_utf16().count() <= limit {
        return std::borrow::Cow::Borrowed(text);
    }
    const ELLIPSIS: &str = " … ";
    let available = limit - ELLIPSIS.encode_utf16().count();
    let mut units = 0;
    let mut head = 0;
    for (offset, ch) in text.char_indices() {
        if units + ch.len_utf16() > available / 2 {
            break;
        }
        units += ch.len_utf16();
        head = offset + ch.len_utf8();
    }
    let mut units = 0;
    let mut tail = text.len();
    for (offset, ch) in text.char_indices().rev() {
        if units + ch.len_utf16() > available - available / 2 {
            break;
        }
        units += ch.len_utf16();
        tail = offset;
    }
    std::borrow::Cow::Owned(format!("{}{}{}", &text[..head], ELLIPSIS, &text[tail..]))
}

fn dialog_message(prompt: &OsStr, metadata: &DialogMetadata, kind: PromptKind) -> String {
    const REQUEST_LABEL: &str = "\nRequest from OpenSSH:\n";
    let fields: Vec<_> = [
        ("Connection", metadata.connection.as_deref(), 96),
        ("User", metadata.user.as_deref(), 64),
        ("Port", metadata.port.as_deref(), 16),
        ("Task", metadata.task.as_deref(), 160),
    ]
    .into_iter()
    .filter_map(|(label, value, cap)| value.map(|value| (label, display_text(value, false), cap)))
    .collect();
    let prompt = display_text(prompt, true);
    let labels: usize = fields.iter().map(|(label, _, _)| label.len() + 3).sum();
    let fixed = labels + REQUEST_LABEL.len() + DIALOG_NOTICE.len() + 2;
    // Keep the request ahead of optional metadata. Very long requests retain
    // their question and final key/path component via explicit middle elision.
    let minimum_fields: usize = fields
        .iter()
        .map(|(_, value, _)| value.encode_utf16().count().min(24))
        .sum();
    let request_budget = DIALOG_LIMIT - fixed - minimum_fields;
    let request_units = if kind == PromptKind::Secret {
        prompt.encode_utf16().count().min(request_budget)
    } else {
        0
    };
    let mut metadata_budget = DIALOG_LIMIT - fixed - request_units;
    let mut message = String::new();
    let mut minimum_left = minimum_fields;
    for (index, (label, value, cap)) in fields.iter().enumerate() {
        let minimum = value.encode_utf16().count().min(24);
        let extra = (metadata_budget - minimum_left) / (fields.len() - index);
        let limit = (*cap).min(minimum + extra);
        minimum_left -= minimum;
        let value = shorten(value, limit);
        metadata_budget -= value.encode_utf16().count();
        message.push_str(&format!("{label}: {value}\n"));
    }
    message.push_str(REQUEST_LABEL);
    if kind == PromptKind::Secret {
        message.push_str(&shorten(&prompt, request_budget));
    } else {
        // MessageBox has no CredUI message limit. Never elide a host-key
        // fingerprint or its trust question to fit a password dialog.
        message.push_str(&prompt);
    }
    message.push_str("\n\n");
    message.push_str(DIALOG_NOTICE);
    message
}

#[cfg(windows)]
pub(super) fn handle() -> Option<std::process::ExitCode> {
    let marker = std::env::var_os("MSYNC_ASKPASS_INTERNAL");
    let arguments: Vec<_> = std::env::args_os().collect();
    let mode = std::env::var_os("SSH_ASKPASS_PROMPT");
    let kind = request(marker.as_deref(), &arguments, mode.as_deref())?;
    let result = native::prompt(&arguments[1], kind).and_then(|answer| {
        if kind == PromptKind::Notice {
            Ok(())
        } else {
            native::write_stdout(&answer)
        }
    });
    Some(match result {
        Ok(()) => std::process::ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("msync SSH prompt: {error}");
            std::process::ExitCode::FAILURE
        }
    })
}

#[cfg(windows)]
mod native {
    use super::{
        CANCELLED, PromptKind, Secret, answer, confirmation_answer, dialog_message, write_answer,
    };
    use std::ffi::{CStr, OsStr, c_void};
    use std::io;
    use std::os::windows::ffi::OsStrExt;
    use std::os::windows::io::{AsRawHandle, BorrowedHandle, FromRawHandle, OwnedHandle};
    use std::ptr;
    use windows_sys::Win32::Foundation::{
        FreeLibrary, HMODULE, INVALID_HANDLE_VALUE, WAIT_OBJECT_0, WAIT_TIMEOUT,
    };
    use windows_sys::Win32::Security::Credentials::{
        CRED_MAX_CREDENTIAL_BLOB_SIZE, CREDUI_FLAGS_ALWAYS_SHOW_UI, CREDUI_FLAGS_DO_NOT_PERSIST,
        CREDUI_FLAGS_GENERIC_CREDENTIALS, CREDUI_FLAGS_KEEP_USERNAME, CREDUI_INFOW,
        CREDUI_MAX_MESSAGE_LENGTH, CREDUI_MAX_USERNAME_LENGTH,
    };
    use windows_sys::Win32::System::Console::{GetStdHandle, STD_OUTPUT_HANDLE};
    use windows_sys::Win32::System::LibraryLoader::{
        GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW,
    };
    use windows_sys::Win32::System::Threading::{
        EVENT_MODIFY_STATE, OpenEventW, SYNCHRONIZATION_SYNCHRONIZE, SetEvent, WaitForSingleObject,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        IDYES, MB_DEFBUTTON2, MB_ICONINFORMATION, MB_ICONWARNING, MB_OK, MB_SETFOREGROUND, MB_YESNO,
    };

    type CredentialPrompt = unsafe extern "system" fn(
        *const CREDUI_INFOW,
        *const u16,
        *const c_void,
        u32,
        *mut u16,
        u32,
        *mut u16,
        u32,
        *mut i32,
        u32,
    ) -> u32;
    type Confirmation = unsafe extern "system" fn(*mut c_void, *const u16, *const u16, u32) -> i32;

    struct Library(HMODULE);

    impl Library {
        fn load(name: &str) -> io::Result<Self> {
            let name = wide(OsStr::new(name));
            // SAFETY: NUL-terminated system DLL name, no borrowed file handle.
            let handle = unsafe {
                LoadLibraryExW(name.as_ptr(), ptr::null_mut(), LOAD_LIBRARY_SEARCH_SYSTEM32)
            };
            if handle.is_null() {
                Err(io::Error::last_os_error())
            } else {
                Ok(Self(handle))
            }
        }

        unsafe fn symbol<T: Copy>(&self, name: &CStr) -> io::Result<T> {
            let address = unsafe { GetProcAddress(self.0, name.as_ptr().cast()) }
                .ok_or_else(io::Error::last_os_error)?;
            assert_eq!(size_of::<T>(), size_of_val(&address));
            // SAFETY: Call sites use the documented system ABI for each symbol.
            // The Library remains owned until after all calls return.
            Ok(unsafe { std::mem::transmute_copy(&address) })
        }
    }

    impl Drop for Library {
        fn drop(&mut self) {
            unsafe { FreeLibrary(self.0) };
        }
    }

    fn wide(text: &OsStr) -> Vec<u16> {
        text.encode_wide().chain(Some(0)).collect()
    }

    pub(super) fn prompt(message: &OsStr, kind: PromptKind) -> io::Result<Secret<u8>> {
        check_cancelled()?;
        let caption = wide(OsStr::new("msync — SSH authentication"));
        let metadata = super::DialogMetadata {
            connection: std::env::var_os("MSYNC_SSH_ALIAS"),
            user: std::env::var_os("MSYNC_SSH_USER"),
            port: std::env::var_os("MSYNC_SSH_PORT"),
            task: Some(
                std::env::var_os("MSYNC_SSH_TASK")
                    .filter(|task| !task.is_empty())
                    .unwrap_or_else(|| "Current msync transfer".into()),
            ),
        };
        let context = dialog_message(message, &metadata, kind);
        let message = wide(OsStr::new(&context));
        match kind {
            PromptKind::Secret => credential(&message, &caption),
            PromptKind::Confirm => confirm(&message, &caption, false),
            PromptKind::Notice => confirm(&message, &caption, true),
        }
    }

    pub(super) fn write_stdout(answer: &Secret<u8>) -> io::Result<()> {
        let handle = unsafe { GetStdHandle(STD_OUTPUT_HANDLE) };
        if handle.is_null() || handle == INVALID_HANDLE_VALUE {
            return Err(io::Error::last_os_error());
        }
        // Avoid Rust's global line buffer retaining a secret after flush. The
        // duplicated pipe handle is owned here; File writes directly to the OS.
        let handle = unsafe { BorrowedHandle::borrow_raw(handle) }.try_clone_to_owned()?;
        write_answer(&mut std::fs::File::from(handle), answer)
    }

    fn credential(message: &[u16], caption: &[u16]) -> io::Result<Secret<u8>> {
        if message.len() > CREDUI_MAX_MESSAGE_LENGTH as usize + 1 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "SSH credential prompt exceeds the native dialog message limit",
            ));
        }
        let library = Library::load("credui.dll")?;
        let prompt: CredentialPrompt = unsafe { library.symbol(c"CredUIPromptForCredentialsW")? };
        let target = wide(OsStr::new("msync SSH"));
        let mut username = Secret(vec![0_u16; CREDUI_MAX_USERNAME_LENGTH as usize + 1]);
        let user =
            std::env::var_os("MSYNC_SSH_USER").unwrap_or_else(|| "(selected by OpenSSH)".into());
        let displayed_user = super::display_text(&user, false);
        let shortened_user = super::shorten(&displayed_user, CREDUI_MAX_USERNAME_LENGTH as usize);
        let label = wide(OsStr::new(shortened_user.as_ref()));
        username.0[..label.len()].copy_from_slice(&label);
        let mut password = Secret(vec![0_u16; CRED_MAX_CREDENTIAL_BLOB_SIZE as usize / 2 + 1]);
        let info = CREDUI_INFOW {
            cbSize: size_of::<CREDUI_INFOW>() as u32,
            hwndParent: ptr::null_mut(),
            pszMessageText: message.as_ptr(),
            pszCaptionText: caption.as_ptr(),
            hbmBanner: ptr::null_mut(),
        };
        let flags = CREDUI_FLAGS_GENERIC_CREDENTIALS
            | CREDUI_FLAGS_ALWAYS_SHOW_UI
            | CREDUI_FLAGS_DO_NOT_PERSIST
            | CREDUI_FLAGS_KEEP_USERNAME;
        // SAFETY: All pointers reference initialized owned buffers that remain
        // live for this synchronous dialog; capacities include terminating NUL.
        let status = unsafe {
            prompt(
                &info,
                target.as_ptr(),
                ptr::null(),
                0,
                username.0.as_mut_ptr(),
                username.0.len() as u32,
                password.0.as_mut_ptr(),
                password.0.len() as u32,
                ptr::null_mut(),
                flags,
            )
        };
        if status == CANCELLED {
            signal_cancel()?;
        }
        answer(&password.0, status)
    }

    fn cancellation_event(access: u32) -> io::Result<Option<OwnedHandle>> {
        let Some(name) = std::env::var_os("MSYNC_SSH_CANCEL_EVENT") else {
            return Ok(None);
        };
        let name = wide(&name);
        // SAFETY: The parent passes a NUL-terminated event name in child-only
        // environment. Access is minimal and the handle is never inherited.
        let handle = unsafe { OpenEventW(access, 0, name.as_ptr()) };
        if handle.is_null() {
            return Err(io::Error::other(format!(
                "cannot open SSH cancellation event: {}",
                io::Error::last_os_error()
            )));
        }
        Ok(Some(unsafe { OwnedHandle::from_raw_handle(handle) }))
    }

    fn signal_cancel() -> io::Result<()> {
        if let Some(handle) = cancellation_event(EVENT_MODIFY_STATE)?
            && unsafe { SetEvent(handle.as_raw_handle()) } == 0
        {
            return Err(io::Error::other(format!(
                "cannot signal SSH cancellation event: {}",
                io::Error::last_os_error()
            )));
        }
        Ok(())
    }

    fn check_cancelled() -> io::Result<()> {
        let Some(handle) = cancellation_event(SYNCHRONIZATION_SYNCHRONIZE)? else {
            return Ok(());
        };
        // An OpenSSH retry may start before the parent polls cancellation. A
        // manual-reset event prevents that retry from flashing another dialog.
        match unsafe { WaitForSingleObject(handle.as_raw_handle(), 0) } {
            WAIT_OBJECT_0 => Err(io::Error::new(
                io::ErrorKind::Interrupted,
                "SSH authentication cancelled",
            )),
            WAIT_TIMEOUT => Ok(()),
            _ => Err(io::Error::other(format!(
                "cannot inspect SSH cancellation event: {}",
                io::Error::last_os_error()
            ))),
        }
    }

    fn confirm(message: &[u16], caption: &[u16], notice: bool) -> io::Result<Secret<u8>> {
        let library = Library::load("user32.dll")?;
        let show: Confirmation = unsafe { library.symbol(c"MessageBoxW")? };
        let flags = if notice {
            MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND
        } else {
            MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2 | MB_SETFOREGROUND
        };
        // SAFETY: Both strings are NUL-terminated; no owner window is borrowed.
        let result = unsafe { show(ptr::null_mut(), message.as_ptr(), caption.as_ptr(), flags) };
        if result == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(confirmation_answer(result == IDYES, notice))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dispatch_requires_private_marker_and_native_prompt_shape() {
        let args = |prompt: &str| vec!["msync".into(), prompt.into()];
        assert_eq!(request(None, &args("user@host's password:"), None), None);
        let marker = Some(OsStr::new(MARKER));
        for ordinary in ["--help", "--version", "repl", "run", "sync.msync"] {
            assert_eq!(request(None, &args(ordinary), None), None);
        }
        assert_eq!(
            request(marker, &args("user@host's password: "), None),
            Some(PromptKind::Secret)
        );
        assert_eq!(
            request(marker, &args("Enter passphrase for key: "), None),
            Some(PromptKind::Secret)
        );
        assert_eq!(
            request(
                marker,
                &args("Host authenticity?"),
                Some(OsStr::new("confirm"))
            ),
            Some(PromptKind::Confirm)
        );
        assert_eq!(request(marker, &["msync".into()], None), None);
    }

    #[test]
    fn keyboard_interactive_and_informational_modes_do_not_require_punctuation() {
        let marker = Some(OsStr::new(MARKER));
        let args = vec!["msync".into(), "OTP from your token".into()];
        assert_eq!(request(marker, &args, None), Some(PromptKind::Secret));
        assert_eq!(
            request(marker, &args, Some(OsStr::new("none"))),
            Some(PromptKind::Notice)
        );
        let args = vec![
            "msync".into(),
            "Are you sure you want to continue connecting (yes/no/[fingerprint])?".into(),
        ];
        assert_eq!(request(marker, &args, None), Some(PromptKind::Confirm));
        assert_eq!(
            request(marker, &["msync".into(), "--help".into()], None),
            None
        );
        assert_eq!(
            request(
                marker,
                &[
                    "msync".into(),
                    "Password (yes/no characters allowed)".into()
                ],
                None
            ),
            Some(PromptKind::Secret)
        );
        assert_eq!(
            request(
                marker,
                &["msync".into(), "--version".into()],
                Some(OsStr::new("confirm"))
            ),
            None
        );
    }

    #[test]
    fn confirmation_wire_output_is_exactly_one_answer_line() {
        for (accepted, expected) in [(true, b"yes\n".as_slice()), (false, b"no\n".as_slice())] {
            let mut output = Vec::new();
            write_answer(&mut output, &confirmation_answer(accepted, false)).unwrap();
            assert_eq!(output, expected);
        }
        assert!(confirmation_answer(false, true).0.is_empty());
    }

    #[test]
    fn message_retains_native_prompt_and_explains_lifetime() {
        let prompt = "Enter passphrase for key 'C:\\Users\\界\\key':";
        let message = dialog_message(
            OsStr::new(prompt),
            &DialogMetadata::default(),
            PromptKind::Secret,
        );
        assert!(message.contains(prompt));
        assert!(message.contains("never saved to configuration, history or IR"));
        assert!(message.contains("earlier committed files are not rolled back"));
    }

    #[test]
    fn long_unicode_paths_and_connection_fields_fit_credential_ui_without_losing_request() {
        let metadata = DialogMetadata {
            connection: Some(format!("Production-{}-Box", "🔑".repeat(1000)).into()),
            user: Some("界".repeat(2000).into()),
            port: Some("1234567890".repeat(100).into()),
            task: Some(format!("C:\\{}\\sync.msync", "界🔑\\".repeat(1000)).into()),
        };
        let prompt = "alice@example.com's password:";
        let message = dialog_message(OsStr::new(prompt), &metadata, PromptKind::Secret);
        assert!(message.encode_utf16().count() <= DIALOG_LIMIT);
        assert!(message.contains(prompt));
        assert!(message.contains("sync.msync"));
        assert!(message.contains("Production-"));
        assert!(message.contains("-Box"));
        assert!(message.contains(" … "));
        assert!(message.ends_with(DIALOG_NOTICE));
        assert!(String::from_utf16(&message.encode_utf16().collect::<Vec<_>>()).is_ok());
    }

    #[test]
    fn oversized_request_is_explicitly_shortened_but_confirmation_fingerprint_is_complete() {
        let prompt = format!(
            "Enter passphrase for key '{}key-name':",
            "界🔑/".repeat(1000)
        );
        let message = dialog_message(
            OsStr::new(&prompt),
            &DialogMetadata::default(),
            PromptKind::Secret,
        );
        assert!(message.encode_utf16().count() <= DIALOG_LIMIT);
        assert!(message.contains("Enter passphrase for key"));
        assert!(message.contains("key-name':"));
        assert!(message.contains(" … "));
        assert!(message.ends_with(DIALOG_NOTICE));
        let fingerprint = format!(
            "{}\nSHA256:fixture-fingerprint\nAre you sure you want to continue connecting (yes/no)?",
            "host界🔑".repeat(1000)
        );
        let confirmation = dialog_message(
            OsStr::new(&fingerprint),
            &DialogMetadata::default(),
            PromptKind::Confirm,
        );
        assert!(confirmation.contains(&fingerprint));
    }

    #[test]
    fn request_has_priority_over_elided_metadata_and_empty_fields_are_safe() {
        let prompt = format!("Password challenge {}:", "界🔑".repeat(150));
        let metadata = DialogMetadata {
            connection: Some("Production_Box".into()),
            task: Some("界🔑/".repeat(3000).into()),
            ..DialogMetadata::default()
        };
        let message = dialog_message(OsStr::new(&prompt), &metadata, PromptKind::Secret);
        assert!(message.contains(&prompt));
        assert!(message.encode_utf16().count() <= DIALOG_LIMIT);
        let metadata = DialogMetadata {
            connection: Some("host".into()),
            user: Some("".into()),
            port: Some("".into()),
            task: Some("".into()),
        };
        let message = dialog_message(
            OsStr::new(&"🔑".repeat(3000)),
            &metadata,
            PromptKind::Secret,
        );
        assert!(message.contains("Connection: host"));
        assert!(message.encode_utf16().count() <= DIALOG_LIMIT);
    }

    #[test]
    fn bounded_display_escapes_controls_before_budgeting_and_never_splits_surrogates() {
        for limit in 3..64 {
            let text = "🔑界".repeat(100);
            let result = shorten(&text, limit);
            assert!(result.encode_utf16().count() <= limit);
            assert!(String::from_utf16(&result.encode_utf16().collect::<Vec<_>>()).is_ok());
        }
        let metadata = DialogMetadata {
            task: Some("path\x1b[2J\nfile.msync".into()),
            ..DialogMetadata::default()
        };
        let message = dialog_message(
            OsStr::new("Password\0token:"),
            &metadata,
            PromptKind::Secret,
        );
        assert!(!message.contains('\x1b'));
        assert!(!message.contains('\0'));
        assert!(message.contains(r"path\u{1b}[2J\nfile.msync"));
        assert!(message.encode_utf16().count() <= DIALOG_LIMIT);
    }

    proptest::proptest! {
        #[test]
        fn arbitrary_unicode_dialog_metadata_respects_utf16_budget(
            prompt in ".{0,1500}", connection in ".{0,500}", user in ".{0,500}",
            port in ".{0,100}", task in ".{0,1500}"
        ) {
            let metadata = DialogMetadata {
                connection: Some(connection.into()), user: Some(user.into()),
                port: Some(port.into()), task: Some(task.into()),
            };
            let message = dialog_message(OsStr::new(&prompt), &metadata, PromptKind::Secret);
            proptest::prop_assert!(message.encode_utf16().count() <= DIALOG_LIMIT);
            proptest::prop_assert!(message.ends_with(DIALOG_NOTICE));
        }
    }

    #[test]
    fn utf8_wire_answer_has_one_newline_and_no_decoration() {
        let password: Vec<_> = "界🔑secret".encode_utf16().chain(Some(0)).collect();
        let secret = answer(&password, 0).unwrap();
        let mut output = Vec::new();
        write_answer(&mut output, &secret).unwrap();
        assert_eq!(output, "界🔑secret\n".as_bytes());
        assert_eq!(answer(&[0], 0).unwrap().0, b"");
    }

    #[test]
    fn cancellation_errors_and_invalid_secrets_produce_no_answer() {
        let cancelled = answer(&[0], CANCELLED).err().unwrap();
        assert_eq!(cancelled.kind(), io::ErrorKind::Interrupted);
        assert!(answer(&[0], 5).is_err());
        assert!(answer(&[0xd800, 0], 0).is_err());
        assert!(answer(&[10, 0], 0).is_err());
        assert!(answer(&[13, 0], 0).is_err());
        assert!(answer(&[65], 0).is_err());
        let mut too_long = vec![b'a' as u16; 1024];
        too_long.push(0);
        assert!(answer(&too_long, 0).is_err());
    }
}
