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

use windows_sys::Win32::Foundation::{
    CloseHandle, ERROR_ACCESS_DENIED, ERROR_INVALID_PARAMETER, FreeLibrary, HMODULE, WAIT_OBJECT_0,
    WAIT_TIMEOUT,
};
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
type HandlePath = unsafe extern "system" fn(*mut c_void, *mut u16, *mut u8) -> u32;
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
    handle_path: HandlePath,
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
                handle_path: symbol(module, b"SbieDll_GetHandlePath\0")?,
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
        let actual = String::from_utf16_lossy(&actual[..end]);
        if actual != name {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                format!(
                    "Sandboxie membership mismatch for pid={pid}: expected box {name:?}, actual box {actual:?}"
                ),
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

    pub fn wait_hive_unmounted(&self, name: &str) -> io::Result<()> {
        let mut name = wide(OsStr::new(name))?;
        name.resize(40, 0);
        let mut root = vec![0u16; 32767];
        let mut bytes = (root.len() * 2) as u32;
        status(unsafe {
            (self.box_path)(
                name.as_ptr(),
                null_mut(),
                root.as_mut_ptr(),
                null_mut(),
                null_mut(),
                &mut bytes,
                null_mut(),
            )
        })?;
        let end = root
            .iter()
            .position(|unit| *unit == 0)
            .ok_or_else(|| io::Error::other("Sandboxie returned an unterminated registry root"))?;
        let root = String::from_utf16(&root[..end])
            .map_err(|cause| io::Error::new(io::ErrorKind::InvalidData, cause))?;
        super::hive::wait_unmounted(&root, std::time::Duration::from_secs(10))
    }

    /// Inspect the actual open handle, not the DLL's virtualized DOS spelling.
    pub fn require_boxed_directory(
        &self,
        name: &str,
        path: &Path,
    ) -> io::Result<same_file::Handle> {
        use std::os::windows::ffi::OsStringExt;
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::FILE_FLAG_BACKUP_SEMANTICS;

        self.belongs(std::process::id(), name)?;
        let directory = fs::OpenOptions::new()
            .read(true)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
            .open(path)?;
        // Pinned sbiedll.h requires SBIE_DLL_HANDLE_PATH_BUFFER_BYTES (8192).
        let mut output = vec![0u16; 4096];
        let mut boxed = 0;
        status(unsafe {
            (self.handle_path)(directory.as_raw_handle(), output.as_mut_ptr(), &mut boxed) as i32
        })?;
        let end = output
            .iter()
            .position(|unit| *unit == 0)
            .ok_or_else(|| io::Error::other("Sandboxie returned an unterminated handle path"))?;
        let actual = std::ffi::OsString::from_wide(&output[..end]);
        let actual = actual
            .to_str()
            .ok_or_else(|| io::Error::other("Sandboxie handle path is not UTF-8"))?;
        let actual = path_from_native(actual)?;
        let root = self.box_path(name)?;
        require_private_copy(boxed, &actual, &root).map_err(|cause| {
            io::Error::new(
                cause.kind(),
                format!("worker scratch {} in box {name}: {cause}", path.display()),
            )
        })?;
        same_file::Handle::from_file(directory)
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
        let operations = NativeQuiescence { api: self, name };
        quiesce_with(&operations, name, keep)
    }
}

fn require_private_copy(boxed: u8, actual: &Path, root: &Path) -> io::Result<()> {
    if boxed != 1
        || !actual.starts_with(root)
        || actual == root
        || actual
            .components()
            .any(|part| matches!(part, Component::ParentDir))
    {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            format!(
                "unverified private copy: boxed={boxed}, actual={}, box_root={}",
                actual.display(),
                root.display()
            ),
        ));
    }
    Ok(())
}

trait Quiescence {
    type Handle;
    fn open(&self, pid: u32) -> io::Result<Self::Handle>;
    fn wait(&self, process: &Self::Handle, milliseconds: u32) -> io::Result<bool>;
    fn member(&self, pid: u32) -> io::Result<()>;
    fn terminate(&self, process: &Self::Handle) -> io::Result<()>;
    fn pids(&self) -> io::Result<Vec<u32>>;
    fn listed(&self, pid: u32) -> io::Result<bool> {
        Ok(self.pids()?.contains(&pid))
    }
}

struct NativeQuiescence<'a> {
    api: &'a Api,
    name: &'a str,
}

impl Quiescence for NativeQuiescence<'_> {
    type Handle = OwnedHandle;

    fn open(&self, pid: u32) -> io::Result<Self::Handle> {
        let access = PROCESS_SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_TERMINATE;
        let mut raw = unsafe { OpenProcess(access, 0, pid) };
        if raw.is_null() {
            let original = io::Error::last_os_error();
            // A wait-only handle can prove an already-completed process without
            // incorrectly turning a denied termination right into disappearance.
            raw = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, 0, pid) };
            if raw.is_null() {
                return Err(original);
            }
        }
        // SAFETY: OpenProcess returned a fresh non-null process handle. Retaining
        // it pins this process generation while membership and exit are checked.
        Ok(unsafe { OwnedHandle::from_raw_handle(raw) })
    }

    fn wait(&self, process: &Self::Handle, milliseconds: u32) -> io::Result<bool> {
        match unsafe { WaitForSingleObject(process.as_raw_handle(), milliseconds) } {
            WAIT_OBJECT_0 => Ok(true),
            WAIT_TIMEOUT => Ok(false),
            _ => Err(io::Error::last_os_error()),
        }
    }

    fn member(&self, pid: u32) -> io::Result<()> {
        self.api.belongs(pid, self.name)
    }

    fn terminate(&self, process: &Self::Handle) -> io::Result<()> {
        if unsafe { TerminateProcess(process.as_raw_handle(), 1) } == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }

    fn pids(&self) -> io::Result<Vec<u32>> {
        self.api.pids(self.name)
    }
}

fn quiesce_with(ops: &impl Quiescence, name: &str, keep: &[u32]) -> io::Result<()> {
    let mut joined = std::collections::BTreeMap::new();
    for _ in 0..32 {
        let pids = ops
            .pids()?
            .into_iter()
            .filter(|pid| !keep.contains(pid) && !joined.contains_key(pid))
            .collect::<Vec<_>>();
        if pids.is_empty() {
            return Ok(());
        }
        for pid in pids {
            if let Some(handle) = stop_enumerated(ops, name, pid)? {
                // A retained process handle pins this exact generation. Driver
                // records may lag completion; never cache an unpinned PID alone.
                joined.insert(pid, handle);
            }
        }
    }
    Err(io::Error::other(
        "owned box did not become quiescent; retain its files and configuration",
    ))
}

fn stop_enumerated<Q: Quiescence>(ops: &Q, name: &str, pid: u32) -> io::Result<Option<Q::Handle>> {
    let failure = |action, cause| quiescence_error(name, pid, action, cause);
    let process = match ops.open(pid) {
        Ok(process) => process,
        Err(cause) => {
            let absent = !ops
                .listed(pid)
                .map_err(|error| failure("re-enumerate after OpenProcess failure", error))?;
            // ERROR_INVALID_PARAMETER is the OS lookup's no-process result.
            // Access denied alone (even after a list change) proves no exit.
            if absent && cause.raw_os_error() == Some(ERROR_INVALID_PARAMETER as i32) {
                return Ok(None);
            }
            return Err(failure(
                "OpenProcess without a waitable process handle",
                cause,
            ));
        }
    };
    if ops
        .wait(&process, 0)
        .map_err(|cause| failure("poll process before membership", cause))?
    {
        return Ok(Some(process));
    }
    if let Err(cause) = ops.member(pid) {
        if ops
            .wait(&process, 0)
            .map_err(|error| failure("poll process after membership failure", error))?
        {
            return Ok(Some(process));
        }
        if is_invalid_cid(&cause)
            && ops
                .wait(&process, 10000)
                .map_err(|error| failure("join process marked terminated by Sandboxie", error))?
        {
            return Ok(Some(process));
        }
        return Err(failure(
            "SbieApi_QueryProcess on an unsignaled process",
            cause,
        ));
    }
    if let Err(cause) = ops.terminate(&process) {
        // Windows documents ACCESS_DENIED for an exited or exiting process.
        // The error is accepted only after this same owned handle signals.
        // https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-terminateprocess
        let timeout = if cause.raw_os_error() == Some(ERROR_ACCESS_DENIED as i32) {
            10000
        } else {
            0
        };
        if ops
            .wait(&process, timeout)
            .map_err(|error| failure("join after TerminateProcess failure", error))?
        {
            return Ok(Some(process));
        }
        return Err(failure(
            "TerminateProcess denied while process remains unsignaled",
            cause,
        ));
    }
    if ops
        .wait(&process, 10000)
        .map_err(|cause| failure("join terminated process", cause))?
    {
        return Ok(Some(process));
    }
    Err(failure(
        "WaitForSingleObject after termination",
        io::Error::new(
            io::ErrorKind::TimedOut,
            "owned process did not signal completion",
        ),
    ))
}

#[derive(Debug)]
struct QuiescenceFailure {
    name: String,
    pid: u32,
    action: &'static str,
    cause: io::Error,
}

impl std::fmt::Display for QuiescenceFailure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "owned box {} pid={} {}: {}",
            self.name, self.pid, self.action, self.cause
        )
    }
}

impl std::error::Error for QuiescenceFailure {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.cause)
    }
}

fn quiescence_error(name: &str, pid: u32, action: &'static str, cause: io::Error) -> io::Error {
    io::Error::new(
        cause.kind(),
        QuiescenceFailure {
            name: name.to_owned(),
            pid,
            action,
            cause,
        },
    )
}

fn is_invalid_cid(cause: &io::Error) -> bool {
    cause
        .get_ref()
        .and_then(|error| error.downcast_ref::<NativeStatus>())
        .is_some_and(|status| status.0 as u32 == 0xc000_000b)
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
        return Err(io::Error::other(NativeStatus(code)));
    }
    Ok(())
}

#[derive(Debug)]
struct NativeStatus(i32);

impl std::fmt::Display for NativeStatus {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Sandboxie NTSTATUS 0x{:08X}", self.0 as u32)
    }
}

impl std::error::Error for NativeStatus {}

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

#[cfg(test)]
mod quiescence_tests {
    #[test]
    fn scratch_copy_proof_rejects_unboxed_foreign_and_traversal_paths() {
        use super::require_private_copy;
        use std::path::Path;
        let root = Path::new(r"\\?\GLOBALROOT\Device\HarddiskVolume1\owned");
        let copy = root.join(r"drive\D\msync-worker-123");
        assert!(require_private_copy(1, &copy, root).is_ok());
        assert!(require_private_copy(0, &copy, root).is_err());
        assert!(require_private_copy(2, &copy, root).is_err());
        assert!(require_private_copy(1, root, root).is_err());
        assert!(require_private_copy(1, &root.join(r"..\escape"), root).is_err());
        assert!(
            require_private_copy(
                1,
                Path::new(r"\\?\GLOBALROOT\Device\HarddiskVolume1\owned-foreign\copy"),
                root
            )
            .is_err()
        );
    }

    use super::*;
    use std::cell::{Cell, RefCell};
    use std::collections::VecDeque;

    enum Member {
        Owned,
        InvalidCid,
        Foreign,
    }

    struct Fake {
        open_error: Option<i32>,
        polls: RefCell<VecDeque<bool>>,
        member: Member,
        terminate_error: Option<i32>,
        listed: bool,
        member_calls: Cell<usize>,
        terminate_calls: Cell<usize>,
        enumerations: Cell<usize>,
    }

    impl Fake {
        fn new(polls: &[bool]) -> Self {
            Self {
                open_error: None,
                polls: RefCell::new(polls.iter().copied().collect()),
                member: Member::Owned,
                terminate_error: None,
                listed: true,
                member_calls: Cell::new(0),
                terminate_calls: Cell::new(0),
                enumerations: Cell::new(0),
            }
        }
    }

    impl Quiescence for Fake {
        type Handle = ();
        fn open(&self, _: u32) -> io::Result<()> {
            self.open_error
                .map_or(Ok(()), |code| Err(io::Error::from_raw_os_error(code)))
        }
        fn wait(&self, _: &(), _: u32) -> io::Result<bool> {
            Ok(self
                .polls
                .borrow_mut()
                .pop_front()
                .expect("unexpected extra lifecycle poll"))
        }
        fn member(&self, _: u32) -> io::Result<()> {
            self.member_calls.set(self.member_calls.get() + 1);
            match self.member {
                Member::Owned => Ok(()),
                Member::InvalidCid => status(0xc000_000bu32 as i32),
                Member::Foreign => Err(io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    "PID belongs to a foreign box",
                )),
            }
        }
        fn terminate(&self, _: &()) -> io::Result<()> {
            self.terminate_calls.set(self.terminate_calls.get() + 1);
            self.terminate_error
                .map_or(Ok(()), |code| Err(io::Error::from_raw_os_error(code)))
        }
        fn listed(&self, _: u32) -> io::Result<bool> {
            Ok(self.listed)
        }
        fn pids(&self) -> io::Result<Vec<u32>> {
            self.enumerations.set(self.enumerations.get() + 1);
            Ok(if self.listed { vec![99] } else { Vec::new() })
        }
    }

    #[test]
    fn signaled_process_does_not_require_a_stale_membership_query() {
        let fake = Fake::new(&[true]);
        let joined = stop_enumerated(&fake, "msync_test", 99).unwrap();
        assert!(joined.is_some());
        assert_eq!(fake.member_calls.get(), 0);
        assert_eq!(fake.terminate_calls.get(), 0);
    }

    #[test]
    fn stale_driver_enumeration_cannot_busy_fail_a_pinned_joined_generation() {
        let fake = Fake::new(&[true]);
        quiesce_with(&fake, "msync_test", &[]).unwrap();
        assert_eq!(fake.enumerations.get(), 2);
        assert_eq!(fake.member_calls.get(), 0);
        assert_eq!(fake.terminate_calls.get(), 0);
        assert!(fake.polls.borrow().is_empty());
    }

    #[test]
    fn invalid_cid_requires_a_signaled_owned_handle_not_just_a_driver_flag() {
        for (signals, succeeds) in [(true, true), (false, false)] {
            let mut fake = Fake::new(&[false, false, signals]);
            fake.member = Member::InvalidCid;
            let result = stop_enumerated(&fake, "msync_test", 99);
            assert_eq!(result.is_ok(), succeeds);
            assert_eq!(fake.terminate_calls.get(), 0);
            if let Err(failure) = result {
                assert!(failure.to_string().contains("pid=99"));
                assert!(failure.to_string().contains("0xC000000B"));
            }
        }
    }

    #[test]
    fn terminate_access_denied_is_accepted_only_after_physical_completion() {
        for completed in [false, true] {
            let mut fake = Fake::new(&[false, completed]);
            fake.terminate_error = Some(ERROR_ACCESS_DENIED as i32);
            let result = stop_enumerated(&fake, "msync_test", 99);
            assert_eq!(result.is_ok(), completed);
            assert_eq!(fake.terminate_calls.get(), 1);
            if let Err(failure) = result {
                assert_eq!(failure.kind(), io::ErrorKind::PermissionDenied);
                let context = failure
                    .get_ref()
                    .unwrap()
                    .downcast_ref::<QuiescenceFailure>()
                    .unwrap();
                assert_eq!(
                    context.cause.raw_os_error(),
                    Some(ERROR_ACCESS_DENIED as i32)
                );
                assert!(failure.to_string().contains("TerminateProcess"));
            }
        }
    }

    #[test]
    fn a_foreign_reused_pid_is_never_terminated() {
        let mut fake = Fake::new(&[false, false]);
        fake.member = Member::Foreign;
        let failure = stop_enumerated(&fake, "msync_test", 99).unwrap_err();
        assert_eq!(failure.kind(), io::ErrorKind::PermissionDenied);
        assert_eq!(fake.terminate_calls.get(), 0);
    }

    #[test]
    fn unopenable_pid_requires_native_nonexistence_and_driver_absence() {
        for (code, listed, succeeds) in [
            (ERROR_INVALID_PARAMETER, false, true),
            (ERROR_INVALID_PARAMETER, true, false),
            (ERROR_ACCESS_DENIED, false, false),
            (ERROR_ACCESS_DENIED, true, false),
        ] {
            let mut fake = Fake::new(&[]);
            fake.open_error = Some(code as i32);
            fake.listed = listed;
            assert_eq!(stop_enumerated(&fake, "msync_test", 99).is_ok(), succeeds);
            assert_eq!(fake.member_calls.get(), 0);
            assert_eq!(fake.terminate_calls.get(), 0);
        }
    }

    struct ExitDuringTerminate {
        child: RefCell<Option<std::process::Child>>,
    }

    impl Quiescence for ExitDuringTerminate {
        type Handle = RefCell<std::process::Child>;
        fn open(&self, _: u32) -> io::Result<Self::Handle> {
            Ok(RefCell::new(self.child.borrow_mut().take().unwrap()))
        }
        fn wait(&self, child: &Self::Handle, timeout: u32) -> io::Result<bool> {
            match unsafe { WaitForSingleObject(child.borrow().as_raw_handle(), timeout) } {
                WAIT_OBJECT_0 => Ok(true),
                WAIT_TIMEOUT => Ok(false),
                _ => Err(io::Error::last_os_error()),
            }
        }
        fn member(&self, _: u32) -> io::Result<()> {
            Ok(())
        }
        fn terminate(&self, child: &Self::Handle) -> io::Result<()> {
            use std::io::Write;
            let mut child = child.borrow_mut();
            child.stdin.take().unwrap().write_all(b"completed\r\n")?;
            assert!(child.wait()?.success());
            assert_eq!(unsafe { TerminateProcess(child.as_raw_handle(), 1) }, 0);
            let failure = io::Error::last_os_error();
            assert_eq!(failure.raw_os_error(), Some(ERROR_ACCESS_DENIED as i32));
            Err(failure)
        }
        fn listed(&self, _: u32) -> io::Result<bool> {
            panic!("a held handle cannot require a PID-only proof")
        }
        fn pids(&self) -> io::Result<Vec<u32>> {
            panic!("this fixture tests one owned generation")
        }
    }

    #[test]
    fn a_real_process_exit_between_validation_and_termination_joins_successfully() {
        use std::os::windows::process::CommandExt;
        use std::process::{Command, Stdio};
        // The child waits on stdin until the controlled termination boundary.
        // Only fixed trusted shell arguments are used, with no filesystem work.
        let child = Command::new("cmd.exe")
            .args(["/D", "/C", "set /p _msync_wait="])
            .creation_flags(0x0800_0000)
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        let pid = child.id();
        let joined = stop_enumerated(
            &ExitDuringTerminate {
                child: RefCell::new(Some(child)),
            },
            "msync_test",
            pid,
        )
        .unwrap();
        assert!(joined.is_some());
    }
}
