//! Pinned Sandboxie DLL ABI. Configuration always addresses an owned section.

use std::ffi::{OsStr, c_void};
use std::fs;
use std::io;
use std::mem;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle};
use std::path::{Component, Path, PathBuf, Prefix};
use std::ptr::{null, null_mut};
use std::sync::Arc;

use windows_sys::Win32::Foundation::{CloseHandle, FreeLibrary, HMODULE, WAIT_OBJECT_0};
use windows_sys::Win32::System::LibraryLoader::{
    GetModuleFileNameW, GetModuleHandleW, GetProcAddress, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS,
    LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, LoadLibraryExW,
};
use windows_sys::Win32::System::Threading::{
    OpenProcess, PROCESS_INFORMATION, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
    PROCESS_TERMINATE, STARTUPINFOW, TerminateProcess, WaitForSingleObject,
};

type Run = unsafe extern "system" fn(
    *const u16,
    *const u16,
    *const u16,
    u32,
    *mut STARTUPINFOW,
    *mut PROCESS_INFORMATION,
) -> i32;
type Version = unsafe extern "system" fn(*mut u16, *mut u32) -> i32;
type Update = unsafe extern "system" fn(u16, *const u16, *const u16, *const u16, *const u16) -> u32;
type Query = unsafe extern "system" fn(*const u16, *const u16, u32, *mut u16, u32) -> i32;
type Membership =
    unsafe extern "system" fn(*mut c_void, *mut u16, *mut u16, *mut u16, *mut u32) -> i32;
type Enumerate = unsafe extern "system" fn(*const u16, u8, u32, *mut u32, *mut u32) -> i32;
type Kill = unsafe extern "system" fn(u32, *const u16) -> u8;
type BoxPath = unsafe extern "system" fn(
    *const u16,
    *mut u16,
    *mut u16,
    *mut u16,
    *mut u32,
    *mut u32,
    *mut u32,
) -> i32;

pub(super) fn wide(value: &OsStr) -> io::Result<Vec<u16>> {
    let mut result = value.encode_wide().collect::<Vec<_>>();
    if result.contains(&0) || result.len() > 32767 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "invalid native path or argument",
        ));
    }
    result.push(0);
    Ok(result)
}

pub(super) struct Api {
    module: HMODULE,
    run: Run,
    update: Update,
    query: Query,
    membership: Membership,
    enumerate: Enumerate,
    kill: Kill,
    box_path: BoxPath,
}

// SAFETY: DLL reference is process-wide; the documented service/driver APIs are
// thread safe, and every caller retains an Arc until its synchronous call ends.
unsafe impl Send for Api {}
unsafe impl Sync for Api {}

impl Drop for Api {
    fn drop(&mut self) {
        unsafe { FreeLibrary(self.module) };
    }
}

impl Api {
    pub fn detect() -> io::Result<Arc<Self>> {
        if let Some(root) = std::env::var_os("MSYNC_SANDBOXIE_HOME") {
            return Self::from_installation(Path::new(&root));
        }
        // The service already injected the boxed worker from its real install
        // path; do not assume it lives in the conventional ProgramFiles folder.
        let name = wide(OsStr::new("SbieDll.dll"))?;
        let loaded = unsafe { GetModuleHandleW(name.as_ptr()) };
        if !loaded.is_null() {
            use std::os::windows::ffi::OsStringExt;
            let mut path = vec![0u16; 32768];
            let length = unsafe { GetModuleFileNameW(loaded, path.as_mut_ptr(), path.len() as u32) }
                as usize;
            if length == 0 || length >= path.len() {
                return Err(io::Error::last_os_error());
            }
            return Self::load_file(Path::new(&std::ffi::OsString::from_wide(&path[..length])));
        }
        let locations = std::env::var_os("ProgramFiles")
            .into_iter()
            .flat_map(|base| {
                [
                    PathBuf::from(&base).join("Sandboxie-Plus"),
                    PathBuf::from(base).join("Sandboxie"),
                ]
            });
        let path = locations.map(|root| root.join("SbieDll.dll")).find(|path| path.is_file())
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound,
                "Sandboxie Plus is required for native Windows transactional programs; install its signed driver/service or set MSYNC_SANDBOXIE_HOME to the installed directory"))?;
        Self::load_file(&path)
    }

    pub fn from_installation(root: &Path) -> io::Result<Arc<Self>> {
        Self::load_file(&root.join("SbieDll.dll"))
    }

    fn load_file(path: &Path) -> io::Result<Arc<Self>> {
        let path = fs::canonicalize(path)?;
        let encoded = wide(path.as_os_str())?;
        let module = unsafe {
            LoadLibraryExW(
                encoded.as_ptr(),
                null_mut(),
                LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS,
            )
        };
        if module.is_null() {
            return Err(io::Error::last_os_error());
        }
        // SAFETY: Each exact pinned export signature is from v1.18.8 sbiedll.h
        // and sbieapi.h. A failed lookup drops the DLL before returning.
        let result = (|| unsafe {
            let version: Version = symbol(module, b"SbieApi_GetVersionEx\0")?;
            let mut text = [0u16; 16];
            let mut abi = 0;
            status(version(text.as_mut_ptr(), &mut abi))?;
            if abi == 0 || text[0] == 0 {
                return Err(io::Error::other(
                    "Sandboxie driver did not report a compatible live ABI",
                ));
            }
            Ok(Self {
                module,
                run: symbol(module, b"SbieDll_RunSandboxed\0")?,
                update: symbol(module, b"SbieDll_UpdateConf\0")?,
                query: symbol(module, b"SbieApi_QueryConf\0")?,
                membership: symbol(module, b"SbieApi_QueryProcess\0")?,
                enumerate: symbol(module, b"SbieApi_EnumProcessEx\0")?,
                kill: symbol(module, b"SbieDll_KillAll\0")?,
                box_path: symbol(module, b"SbieApi_QueryBoxPath\0")?,
            })
        })();
        if result.is_err() {
            unsafe { FreeLibrary(module) };
        }
        result.map(Arc::new)
    }

    pub fn configure(
        &self,
        name: &str,
        key: &str,
        value: Option<&OsStr>,
        append: bool,
    ) -> io::Result<()> {
        owned_name(name)?;
        let name = wide(OsStr::new(name))?;
        let key = wide(OsStr::new(key))?;
        let value = value.map(wide).transpose()?;
        let result = unsafe {
            (self.update)(
                if append { b'a' } else { b's' } as u16,
                null(),
                name.as_ptr(),
                key.as_ptr(),
                value.as_ref().map_or(null(), |text| text.as_ptr()),
            )
        };
        status(result as i32)
    }

    pub fn values(&self, name: &str, key: &str) -> io::Result<Vec<String>> {
        self.values_flags(name, key, 0)
    }

    pub fn values_flags(&self, name: &str, key: &str, flags: u32) -> io::Result<Vec<String>> {
        let name_utf16 = wide(OsStr::new(name))?;
        let key_utf16 = wide(OsStr::new(key))?;
        let mut result = Vec::new();
        for index in 0..4096 {
            let mut buffer = vec![0u16; 32767];
            let code = unsafe {
                (self.query)(
                    name_utf16.as_ptr(),
                    key_utf16.as_ptr(),
                    index | flags,
                    buffer.as_mut_ptr(),
                    (buffer.len() * 2) as u32,
                )
            };
            if matches!(code as u32, 0x8000_001A | 0xC000_0034 | 0xC000_008B) {
                return Ok(result);
            }
            status(code).map_err(|cause| {
                io::Error::new(
                    cause.kind(),
                    QueryFailure {
                        section: name.to_owned(),
                        setting: key.to_owned(),
                        index,
                        flags,
                        status: code,
                        cause,
                    },
                )
            })?;
            let end = buffer
                .iter()
                .position(|unit| *unit == 0)
                .unwrap_or(buffer.len());
            result.push(
                String::from_utf16(&buffer[..end])
                    .map_err(|_| io::Error::other("Sandboxie configuration is not UTF-16"))?,
            );
        }
        Err(io::Error::other(
            "Sandboxie effective configuration exceeds the bounded policy limit",
        ))
    }

    pub fn belongs(&self, pid: u32, name: &str) -> io::Result<()> {
        let mut actual = [0u16; 40];
        let code = unsafe {
            (self.membership)(
                pid as usize as *mut c_void,
                actual.as_mut_ptr(),
                null_mut(),
                null_mut(),
                null_mut(),
            )
        };
        status(code)?;
        let end = actual
            .iter()
            .position(|unit| *unit == 0)
            .unwrap_or(actual.len());
        if String::from_utf16_lossy(&actual[..end]) != name {
            return Err(io::Error::other(
                "control peer is not a member of the owned Sandboxie box",
            ));
        }
        Ok(())
    }

    pub fn box_path(&self, name: &str) -> io::Result<PathBuf> {
        use std::os::windows::ffi::OsStringExt;
        let mut name = wide(OsStr::new(name))?;
        name.resize(40, 0);
        let mut output = vec![0u16; 32767];
        let mut bytes = (output.len() * 2) as u32;
        status(unsafe {
            (self.box_path)(
                name.as_ptr(),
                output.as_mut_ptr(),
                null_mut(),
                null_mut(),
                &mut bytes,
                null_mut(),
                null_mut(),
            )
        })?;
        let end = output
            .iter()
            .position(|unit| *unit == 0)
            .ok_or_else(|| io::Error::other("Sandboxie returned an unterminated box root"))?;
        let native = std::ffi::OsString::from_wide(&output[..end]);
        let text = native
            .to_str()
            .ok_or_else(|| io::Error::other("Sandboxie box root is not a UTF-8 path"))?;
        path_from_native(text)
    }

    pub fn launch(
        &self,
        name: &str,
        worker: &Path,
        args: &[String],
        cwd: &Path,
    ) -> io::Result<NativeProcess> {
        owned_name(name)?;
        // std uses the same quoting rules when serializing argv for CreateProcess.
        // Reuse our bounded explicit Windows quoting below, not shell parsing.
        let mut line = quote(worker.as_os_str())?;
        for arg in args {
            line.push(' ');
            line.push_str(&quote(OsStr::new(arg))?);
        }
        let name = wide(OsStr::new(name))?;
        let line = wide(OsStr::new(&line))?;
        let cwd = wide(cwd.as_os_str())?;
        let mut startup = STARTUPINFOW {
            cb: mem::size_of::<STARTUPINFOW>() as u32,
            ..Default::default()
        };
        let mut info = PROCESS_INFORMATION::default();
        if unsafe {
            (self.run)(
                name.as_ptr(),
                line.as_ptr(),
                cwd.as_ptr(),
                0x0800_0000,
                &mut startup,
                &mut info,
            )
        } == 0
        {
            let failure = io::Error::last_os_error();
            if !info.hThread.is_null() {
                unsafe { CloseHandle(info.hThread) };
            }
            if !info.hProcess.is_null() {
                unsafe { CloseHandle(info.hProcess) };
            }
            return Err(failure);
        }
        if !info.hThread.is_null() {
            unsafe { CloseHandle(info.hThread) };
        }
        if info.hProcess.is_null() {
            return Err(io::Error::other(
                "Sandboxie returned no worker process handle",
            ));
        }
        Ok(NativeProcess {
            handle: Arc::new(unsafe { OwnedHandle::from_raw_handle(info.hProcess) }),
            pid: info.dwProcessId,
        })
    }

    pub fn pids(&self, name: &str) -> io::Result<Vec<u32>> {
        let mut name = wide(OsStr::new(name))?;
        name.resize(40, 0);
        let mut capacity = 32usize;
        loop {
            let mut pids = vec![0u32; capacity];
            let mut count = capacity as u32;
            status(unsafe {
                (self.enumerate)(name.as_ptr(), 0, u32::MAX, pids.as_mut_ptr(), &mut count)
            })?;
            if count as usize >= capacity {
                capacity = capacity
                    .checked_mul(2)
                    .filter(|size| *size <= 65536)
                    .ok_or_else(|| {
                        io::Error::other("owned box process count exceeds lifecycle limit")
                    })?;
                continue;
            }
            pids.truncate(count as usize);
            return Ok(pids);
        }
    }

    pub fn kill_all(&self, name: &str) -> io::Result<()> {
        owned_name(name)?;
        let name = wide(OsStr::new(name))?;
        if unsafe { (self.kill)(u32::MAX, name.as_ptr()) } == 0 {
            return Err(io::Error::other(
                "Sandboxie refused termination of the owned box",
            ));
        }
        Ok(())
    }

    pub fn quiesce_except(&self, name: &str, keep: Option<u32>) -> io::Result<()> {
        self.quiesce_except_many(name, &keep.into_iter().collect::<Vec<_>>())
    }

    pub fn quiesce_except_many(&self, name: &str, keep: &[u32]) -> io::Result<()> {
        for _ in 0..32 {
            let pids = self
                .pids(name)?
                .into_iter()
                .filter(|pid| !keep.contains(pid))
                .collect::<Vec<_>>();
            if pids.is_empty() {
                return Ok(());
            }
            for pid in pids {
                let raw = unsafe {
                    OpenProcess(
                        PROCESS_SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE,
                        0,
                        pid,
                    )
                };
                if raw.is_null() {
                    // Re-enumeration, not a stale PID, decides quiescence.
                    continue;
                }
                let handle = unsafe { OwnedHandle::from_raw_handle(raw) };
                self.belongs(pid, name)?;
                if unsafe { TerminateProcess(handle.as_raw_handle(), 1) } == 0 {
                    return Err(io::Error::last_os_error());
                }
                if unsafe { WaitForSingleObject(handle.as_raw_handle(), 10000) } != WAIT_OBJECT_0 {
                    return Err(io::Error::new(
                        io::ErrorKind::TimedOut,
                        "owned box process did not terminate",
                    ));
                }
            }
        }
        Err(io::Error::other(
            "owned box did not become quiescent; retain its files and configuration",
        ))
    }
}

/// Driver-expanded settings use NT device paths, not the caller's DOS spelling.
pub(super) fn path_from_native(text: &str) -> io::Result<PathBuf> {
    let path = if let Some(tail) = text.strip_prefix(r"\??\") {
        PathBuf::from(format!(r"\\?\{tail}"))
    } else if text.starts_with(r"\Device\") {
        PathBuf::from(format!(r"\\?\GLOBALROOT{text}"))
    } else {
        PathBuf::from(text)
    };
    if !path.is_absolute() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Sandboxie returned a nonabsolute native path",
        ));
    }
    Ok(path)
}

/// Sandboxie's DOS-to-NT configuration parser treats `\\?\` as UNC, not as
/// Win32 verbatim syntax. Serialize supported canonical filesystem prefixes in
/// that parser's own namespace; actual effective paths are still checked by ID.
pub(super) fn configuration_path(path: &Path) -> io::Result<std::ffi::OsString> {
    use std::os::windows::ffi::OsStringExt;
    if !path.is_absolute()
        || path
            .components()
            .any(|part| matches!(part, Component::CurDir | Component::ParentDir))
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Sandboxie directory policies require canonical absolute paths",
        ));
    }
    let Some(Component::Prefix(prefix)) = path.components().next() else {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Sandboxie directory policies require DOS drive or UNC prefixes",
        ));
    };
    let verbatim = matches!(
        prefix.kind(),
        Prefix::VerbatimDisk(_) | Prefix::VerbatimUNC(_, _)
    );
    if verbatim
        && path
            .as_os_str()
            .encode_wide()
            .any(|unit| unit == b'/' as u16)
    {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "a verbatim path containing forward slashes is not a canonical directory policy",
        ));
    }
    if verbatim
        && path.components().any(|part| {
            matches!(part, Component::Normal(name)
        if name.encode_wide().last().is_some_and(|unit| matches!(unit, 0x20 | 0x2e)))
        })
    {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "a verbatim directory ending in a space or dot cannot be represented by Sandboxie's DOS configuration parser",
        ));
    }
    let mut encoded = match prefix.kind() {
        Prefix::Disk(letter) | Prefix::VerbatimDisk(letter) => vec![letter as u16, b':' as u16],
        Prefix::UNC(server, share) | Prefix::VerbatimUNC(server, share) => {
            let mut result = vec![b'\\' as u16; 2];
            result.extend(server.encode_wide());
            result.push(b'\\' as u16);
            result.extend(share.encode_wide());
            result
        }
        _ => {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "Sandboxie directory policies do not support this device or verbatim namespace",
            ));
        }
    };
    let whole = path.as_os_str().encode_wide().collect::<Vec<_>>();
    let prefix_length = prefix.as_os_str().encode_wide().count();
    encoded.extend(whole[prefix_length..].iter().map(|unit| {
        if *unit == b'/' as u16 {
            b'\\' as u16
        } else {
            *unit
        }
    }));
    if encoded.contains(&0) || encoded.len() > 1024 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Sandboxie expanded configuration paths must fit its 1024-character limit and cannot contain NUL",
        ));
    }
    Ok(std::ffi::OsString::from_wide(&encoded))
}

#[derive(Debug)]
struct QueryFailure {
    section: String,
    setting: String,
    index: u32,
    flags: u32,
    status: i32,
    cause: io::Error,
}

impl std::fmt::Display for QueryFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "SbieApi_QueryConf [{}] {} index={} flags=0x{:08X}: NTSTATUS 0x{:08X}",
            self.section, self.setting, self.index, self.flags, self.status as u32
        )
    }
}

impl std::error::Error for QueryFailure {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.cause)
    }
}

unsafe fn symbol<T: Copy>(module: HMODULE, name: &[u8]) -> io::Result<T> {
    let address = unsafe { GetProcAddress(module, name.as_ptr()) }.ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::Unsupported,
            format!("Sandboxie is missing {}", String::from_utf8_lossy(name)),
        )
    })?;
    // SAFETY: Call sites specify the exact documented function pointer ABI.
    Ok(unsafe { mem::transmute_copy(&address) })
}

fn status(code: i32) -> io::Result<()> {
    if code < 0 {
        return Err(io::Error::other(format!(
            "Sandboxie NTSTATUS 0x{:08X}",
            code as u32
        )));
    }
    Ok(())
}

pub(super) fn owned_name(name: &str) -> io::Result<()> {
    if !name.starts_with("msync_")
        || name.len() > 38
        || !name
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'_')
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "not an owned msync box name",
        ));
    }
    Ok(())
}

pub(super) fn nonce() -> io::Result<String> {
    #[link(name = "advapi32")]
    unsafe extern "system" {
        fn SystemFunction036(buffer: *mut c_void, length: u32) -> u8;
    }
    let mut bytes = [0u8; 16];
    if unsafe { SystemFunction036(bytes.as_mut_ptr().cast(), bytes.len() as u32) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(bytes.iter().map(|byte| format!("{byte:02x}")).collect())
}

fn quote(text: &OsStr) -> io::Result<String> {
    let text = text.to_str().ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "worker launch path must be UTF-8",
        )
    })?;
    if text.contains('\0') {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "worker argument contains NUL",
        ));
    }
    let mut output = String::from("\"");
    let mut slashes = 0;
    for ch in text.chars() {
        if ch == '\\' {
            slashes += 1;
            continue;
        }
        output.extend(std::iter::repeat_n(
            '\\',
            slashes * if ch == '"' { 2 } else { 1 },
        ));
        if ch == '"' {
            output.push('\\');
        }
        output.push(ch);
        slashes = 0;
    }
    output.extend(std::iter::repeat_n('\\', slashes * 2));
    output.push('"');
    Ok(output)
}

pub(super) struct NativeProcess {
    pub handle: Arc<OwnedHandle>,
    pub pid: u32,
}

impl NativeProcess {
    pub async fn wait(&self) -> io::Result<()> {
        let handle = self.handle.clone();
        tokio::task::spawn_blocking(move || {
            if unsafe { WaitForSingleObject(handle.as_raw_handle(), 30000) } != WAIT_OBJECT_0 {
                return Err(io::Error::new(
                    io::ErrorKind::TimedOut,
                    "Sandboxie worker did not close",
                ));
            }
            Ok(())
        })
        .await
        .map_err(io::Error::other)?
    }
}
