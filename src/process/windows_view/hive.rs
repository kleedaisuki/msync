//! Process exit precedes SbieSvc's asynchronous registry-hive unload.

use std::ffi::OsStr;
use std::fmt;
use std::io;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::ptr::{null, null_mut};
use std::time::{Duration, Instant};

use windows_sys::Win32::Foundation::{
    ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND, WAIT_OBJECT_0, WAIT_TIMEOUT,
};
use windows_sys::Win32::System::Registry::{
    HKEY, HKEY_LOCAL_MACHINE, HKEY_USERS, KEY_NOTIFY, KEY_READ, REG_NOTIFY_CHANGE_NAME,
    RegCloseKey, RegNotifyChangeKeyValue, RegOpenKeyExW,
};
use windows_sys::Win32::System::Threading::{CreateEventW, WaitForSingleObject};

use super::native::wide;

pub(super) fn wait_unmounted(native: &str, timeout: Duration) -> io::Result<()> {
    let outcome = || {
        let (root, relative) = native_root(native)?;
        let (parent, child) = relative.rsplit_once('\\').unwrap_or(("", relative));
        let mut watch = HiveWatch {
            parent: open(root, parent, KEY_NOTIFY)?,
            child: wide(OsStr::new(child))?,
            event: event()?,
        };
        wait_absent(&mut watch, timeout)
    };
    outcome().map_err(|cause| {
        io::Error::new(
            cause.kind(),
            HiveFailure {
                root: native.to_owned(),
                cause,
            },
        )
    })
}

#[derive(Debug)]
struct HiveFailure {
    root: String,
    cause: io::Error,
}

impl fmt::Display for HiveFailure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "owned registry hive {} has not quiesced: {}; retain the private box and check Sandboxie service hive-unload diagnostics",
            self.root, self.cause
        )
    }
}

impl std::error::Error for HiveFailure {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.cause)
    }
}

fn native_root(native: &str) -> io::Result<(HKEY, &str)> {
    for (prefix, key) in [
        (r"\REGISTRY\USER\", HKEY_USERS),
        (r"\REGISTRY\MACHINE\", HKEY_LOCAL_MACHINE),
    ] {
        if native
            .get(..prefix.len())
            .is_some_and(|head| head.eq_ignore_ascii_case(prefix))
        {
            let relative = &native[prefix.len()..];
            if !relative.is_empty()
                && !relative.split('\\').any(str::is_empty)
                && !relative.contains('\0')
            {
                return Ok((key, relative));
            }
        }
    }
    Err(io::Error::new(
        io::ErrorKind::InvalidData,
        format!("unsupported driver registry root {native:?}"),
    ))
}

struct Key(HKEY);

impl Drop for Key {
    fn drop(&mut self) {
        unsafe { RegCloseKey(self.0) };
    }
}

fn open(root: HKEY, path: &str, access: u32) -> io::Result<Key> {
    let path = wide(OsStr::new(path))?;
    let mut key = null_mut();
    result(unsafe { RegOpenKeyExW(root, path.as_ptr(), 0, access, &mut key) })?;
    Ok(Key(key))
}

fn result(code: u32) -> io::Result<()> {
    match code {
        0 => Ok(()),
        code => Err(io::Error::from_raw_os_error(code as i32)),
    }
}

fn event() -> io::Result<OwnedHandle> {
    let raw = unsafe { CreateEventW(null(), 0, 0, null()) };
    if raw.is_null() {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: CreateEventW returned a new, non-null owned event handle.
    Ok(unsafe { OwnedHandle::from_raw_handle(raw) })
}

struct HiveWatch {
    parent: Key,
    child: Vec<u16>,
    event: OwnedHandle,
}

trait UnmountWatch {
    fn arm(&mut self) -> io::Result<()>;
    fn exists(&mut self) -> io::Result<bool>;
    fn wait(&mut self, timeout: Duration) -> io::Result<bool>;
}

impl UnmountWatch for HiveWatch {
    fn arm(&mut self) -> io::Result<()> {
        // Observe the parent, not the hive: a retained hive handle can prevent
        // the very unload whose completion we are waiting to prove.
        result(unsafe {
            RegNotifyChangeKeyValue(
                self.parent.0,
                0,
                REG_NOTIFY_CHANGE_NAME,
                self.event.as_raw_handle(),
                1,
            )
        })
    }

    fn exists(&mut self) -> io::Result<bool> {
        let mut raw = null_mut();
        match unsafe { RegOpenKeyExW(self.parent.0, self.child.as_ptr(), 0, KEY_READ, &mut raw) } {
            0 => {
                drop(Key(raw));
                Ok(true)
            }
            ERROR_FILE_NOT_FOUND | ERROR_PATH_NOT_FOUND => Ok(false),
            code => Err(io::Error::from_raw_os_error(code as i32)),
        }
    }

    fn wait(&mut self, timeout: Duration) -> io::Result<bool> {
        let milliseconds = timeout.as_millis().clamp(1, u32::MAX as u128 - 1) as u32;
        match unsafe { WaitForSingleObject(self.event.as_raw_handle(), milliseconds) } {
            WAIT_OBJECT_0 => Ok(true),
            WAIT_TIMEOUT => Ok(false),
            _ => Err(io::Error::last_os_error()),
        }
    }
}

fn wait_absent(watch: &mut impl UnmountWatch, timeout: Duration) -> io::Result<()> {
    let deadline = Instant::now() + timeout;
    loop {
        // Arm before inspecting absence, including after unrelated siblings
        // change, so deletion cannot be lost between query and registration.
        watch.arm()?;
        if !watch.exists()? {
            return Ok(());
        }
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() || !watch.wait(remaining)? {
            return Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "service did not unload the registry hive before the cleanup deadline",
            ));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::VecDeque;

    struct Fake {
        existence: VecDeque<bool>,
        notifications: VecDeque<bool>,
        armed: bool,
    }

    impl UnmountWatch for Fake {
        fn arm(&mut self) -> io::Result<()> {
            assert!(!self.armed);
            self.armed = true;
            Ok(())
        }

        fn exists(&mut self) -> io::Result<bool> {
            assert!(self.armed, "the absence query must follow registration");
            Ok(self.existence.pop_front().unwrap())
        }

        fn wait(&mut self, _: Duration) -> io::Result<bool> {
            assert!(self.armed);
            self.armed = false;
            Ok(self.notifications.pop_front().unwrap())
        }
    }

    #[test]
    fn deletion_before_query_needs_no_notification() {
        let mut watch = Fake {
            existence: [false].into(),
            notifications: [].into(),
            armed: false,
        };
        wait_absent(&mut watch, Duration::from_secs(1)).unwrap();
    }

    #[test]
    fn sibling_notifications_do_not_prove_hive_absence() {
        let mut watch = Fake {
            existence: [true, true, false].into(),
            notifications: [true, true].into(),
            armed: false,
        };
        wait_absent(&mut watch, Duration::from_secs(1)).unwrap();
        assert!(watch.notifications.is_empty());
    }

    #[test]
    fn unmounted_deadline_is_a_real_error() {
        let mut watch = Fake {
            existence: [true].into(),
            notifications: [false].into(),
            armed: false,
        };
        assert_eq!(
            wait_absent(&mut watch, Duration::from_secs(1))
                .unwrap_err()
                .kind(),
            io::ErrorKind::TimedOut
        );
    }

    #[test]
    fn native_hive_root_is_not_a_filesystem_or_an_unchecked_namespace() {
        let (root, path) = native_root(r"\registry\user\Sandbox_test_msync_001").unwrap();
        assert_eq!(root, HKEY_USERS);
        assert_eq!(path, "Sandbox_test_msync_001");
        for invalid in [
            r"C:\Sandbox_test",
            r"\REGISTRY\USER\",
            r"\REGISTRY\USER\\child",
        ] {
            assert!(native_root(invalid).is_err());
        }
    }

    #[test]
    fn an_os_notification_wakes_for_exact_private_key_deletion() {
        use windows_sys::Win32::System::Registry::{
            HKEY_CURRENT_USER, KEY_ALL_ACCESS, RegCreateKeyExW, RegDeleteKeyW,
        };

        let parent = format!(
            r"Software\msync-hive-watch-{}",
            super::super::native::nonce().unwrap()
        );
        let child = format!(r"{parent}\owned-child");
        let parent_wide = wide(OsStr::new(&parent)).unwrap();
        let child_wide = wide(OsStr::new(&child)).unwrap();
        let mut raw = null_mut();
        result(unsafe {
            RegCreateKeyExW(
                HKEY_CURRENT_USER,
                child_wide.as_ptr(),
                0,
                null(),
                0,
                KEY_ALL_ACCESS,
                null(),
                &mut raw,
                null_mut(),
            )
        })
        .unwrap();
        drop(Key(raw));
        let (armed, registered) = std::sync::mpsc::channel();
        let deletion = std::thread::spawn(move || {
            registered.recv().unwrap();
            result(unsafe { RegDeleteKeyW(HKEY_CURRENT_USER, child_wide.as_ptr()) })
        });
        struct SignaledWatch {
            watch: HiveWatch,
            armed: Option<std::sync::mpsc::Sender<()>>,
        }
        impl UnmountWatch for SignaledWatch {
            fn arm(&mut self) -> io::Result<()> {
                self.watch.arm()
            }

            fn exists(&mut self) -> io::Result<bool> {
                let exists = self.watch.exists()?;
                if let Some(armed) = self.armed.take() {
                    assert!(exists);
                    armed.send(()).unwrap();
                }
                Ok(exists)
            }

            fn wait(&mut self, timeout: Duration) -> io::Result<bool> {
                self.watch.wait(timeout)
            }
        }
        let mut watch = SignaledWatch {
            watch: HiveWatch {
                parent: open(HKEY_CURRENT_USER, &parent, KEY_NOTIFY).unwrap(),
                child: wide(OsStr::new("owned-child")).unwrap(),
                event: event().unwrap(),
            },
            armed: Some(armed),
        };
        let observed = wait_absent(&mut watch, Duration::from_secs(5));
        drop(watch);
        deletion.join().unwrap().unwrap();
        result(unsafe { RegDeleteKeyW(HKEY_CURRENT_USER, parent_wide.as_ptr()) }).unwrap();
        observed.unwrap();
    }
}
