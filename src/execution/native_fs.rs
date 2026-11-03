//! Native filesystem operations share endpoint resolution and the native transport.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use tokio_util::sync::CancellationToken;

use crate::endpoint::{Backend, Location};
use crate::language::DestinationMode;
use crate::syntax::Span;

use super::super::error::{ExecutionError, Result};
use super::control::Value;
use super::native_fs_path::{self, PathValue};

pub(super) enum NativeResult {
    Value(Value),
    Range(Vec<Value>),
    Void,
}

pub(super) async fn call(
    name: &str,
    args: Vec<Value>,
    cancel: &CancellationToken,
    span: Span,
) -> Result<NativeResult> {
    check(cancel, span)?;
    let name = name.strip_prefix("fs.").unwrap_or(name);
    let locations = args
        .into_iter()
        .enumerate()
        .map(|(index, arg)| {
            if name == "set_readonly"
                && index == 1
                && let Value::Bool(value) = arg
            {
                return Ok(Location {
                    backend: Backend::Local,
                    path: value.to_string(),
                });
            }
            location(arg, span)
        })
        .collect::<Result<Vec<_>>>()?;
    if let Some(value) = native_fs_path::call(name, &locations, span)? {
        return Ok(NativeResult::Value(match value {
            PathValue::Path(location) => path_value(location),
            PathValue::Bool(value) => Value::Bool(value),
            PathValue::Paths(locations) => {
                return Ok(NativeResult::Range(
                    locations.into_iter().map(path_value).collect(),
                ));
            }
            PathValue::String(value) => Value::String(value),
        }));
    }
    if name == "exists" {
        let location = one(&locations, name, span)?;
        return Ok(boolean(
            super::super::control_fs::exists(location, cancel, span).await?,
        ));
    }
    if name == "delete" {
        let location = one(&locations, name, span)?;
        super::super::control_fs::delete(location, cancel, span).await?;
        return Ok(NativeResult::Void);
    }
    let name = name.to_owned();
    let cancel = cancel.clone();
    crate::runtime::ssh::spawn_blocking(move || {
        check(&cancel, span)?;
        dispatch(&name, &locations, &cancel, span)
    })
    .await
    .map_err(|cause| ExecutionError::caused("filesystem native worker failed", span, cause))?
}

fn location(value: Value, span: Span) -> Result<Location> {
    match value {
        Value::Path(location, _) => Ok(location),
        Value::String(path) => Ok(Location {
            backend: Backend::Local,
            path,
        }),
        _ => Err(ExecutionError::new(
            "filesystem argument must be a Path or String",
            span,
        )),
    }
}

fn one<'a>(args: &'a [Location], name: &str, span: Span) -> Result<&'a Location> {
    match args {
        [path] => Ok(path),
        _ => Err(ExecutionError::new(
            format!("fs::{name} requires one path argument"),
            span,
        )),
    }
}

fn pair<'a>(args: &'a [Location], name: &str, span: Span) -> Result<(&'a Location, &'a Location)> {
    match args {
        [left, right] => Ok((left, right)),
        _ => Err(ExecutionError::new(
            format!("fs::{name} requires two path arguments"),
            span,
        )),
    }
}

fn path_value(location: Location) -> Value {
    Value::Path(location, DestinationMode::Exact)
}

fn boolean(value: bool) -> NativeResult {
    NativeResult::Value(Value::Bool(value))
}

fn text(value: impl Into<String>) -> NativeResult {
    NativeResult::Value(Value::String(value.into()))
}

fn path_result(location: Location, path: PathBuf, span: Span) -> Result<NativeResult> {
    let path = path.into_os_string().into_string().map_err(|path| {
        ExecutionError::new(
            format!("filesystem returned a non-UTF-8 path: {path:?}"),
            span,
        )
    })?;
    Ok(NativeResult::Value(path_value(Location {
        backend: location.backend,
        path,
    })))
}

fn check(cancel: &CancellationToken, span: Span) -> Result<()> {
    if cancel.is_cancelled() {
        return Err(ExecutionError::cancelled(
            "filesystem native operation cancelled",
            span,
        ));
    }
    Ok(())
}

fn local(location: &Location, source: bool, span: Span) -> Result<Option<PathBuf>> {
    crate::runtime::local_path(location, source).map_err(|cause| {
        ExecutionError::new(
            format!("cannot resolve filesystem path {location}: {cause}"),
            span,
        )
    })
}

fn io_error(action: &str, location: &Location, span: Span, cause: io::Error) -> ExecutionError {
    ExecutionError::caused(format!("cannot {action} {location}"), span, cause)
}

fn metadata(
    path: &Path,
    location: &Location,
    follow: bool,
    span: Span,
) -> Result<Option<fs::Metadata>> {
    match if follow {
        fs::metadata(path)
    } else {
        fs::symlink_metadata(path)
    } {
        Ok(metadata) => Ok(Some(metadata)),
        Err(cause)
            if matches!(
                cause.kind(),
                io::ErrorKind::NotFound | io::ErrorKind::NotADirectory
            ) =>
        {
            Ok(None)
        }
        Err(cause) => Err(io_error("inspect", location, span, cause)),
    }
}

fn dispatch(
    name: &str,
    args: &[Location],
    cancel: &CancellationToken,
    span: Span,
) -> Result<NativeResult> {
    if name == "temp_directory_path" {
        if !args.is_empty() {
            return Err(ExecutionError::new(
                "fs::temp_directory_path takes no arguments",
                span,
            ));
        }
        return path_result(
            Location {
                backend: Backend::Local,
                path: String::new(),
            },
            std::env::temp_dir(),
            span,
        );
    }
    if matches!(
        name,
        "permissions" | "set_permissions" | "is_readonly" | "set_readonly"
    ) {
        return permissions(name, args, cancel, span);
    }
    if name == "copy_file" {
        return copy_file(args, cancel, span);
    }
    if matches!(
        name,
        "equivalent"
            | "set_permissions"
            | "set_readonly"
            | "rename"
            | "create_hard_link"
            | "create_symlink"
            | "create_directory_symlink"
            | "copy_symlink"
    ) {
        return binary(name, args, cancel, span);
    }
    let location = one(args, name, span)?;
    let writable = matches!(
        name,
        "create_directory" | "create_directories" | "remove" | "remove_all"
    );
    let Some(path) = local(location, !writable, span)? else {
        return remote(name, args, cancel, span);
    };
    match name {
        "status" | "symlink_status" => {
            let value = metadata(&path, location, name == "status", span)?;
            Ok(text(file_type(value.as_ref())))
        }
        "is_regular_file" | "is_directory" | "is_symlink" | "is_block_file"
        | "is_character_file" | "is_fifo" | "is_socket" | "is_other" => {
            let value = metadata(&path, location, name != "is_symlink", span)?;
            Ok(boolean(type_matches(name, file_type(value.as_ref()))))
        }
        "is_empty" => {
            let value = fs::metadata(&path)
                .map_err(|cause| io_error("inspect emptiness of", location, span, cause))?;
            if value.is_file() {
                return Ok(boolean(value.len() == 0));
            }
            if value.is_dir() {
                let mut entries = fs::read_dir(&path)
                    .map_err(|cause| io_error("enumerate", location, span, cause))?;
                let empty = entries
                    .next()
                    .transpose()
                    .map_err(|cause| io_error("read entry in", location, span, cause))?
                    .is_none();
                return Ok(boolean(empty));
            }
            Err(ExecutionError::new(
                format!("fs::is_empty requires a regular file or directory: {location}"),
                span,
            ))
        }
        "absolute" => {
            let absolute = std::path::absolute(&path)
                .map_err(|cause| io_error("make absolute", location, span, cause))?;
            path_result(
                Location {
                    backend: Backend::Local,
                    path: String::new(),
                },
                absolute,
                span,
            )
        }
        "canonical" | "weakly_canonical" => {
            let path = if name == "canonical" {
                fs::canonicalize(&path)
            } else {
                weakly_canonical(&path)
            }
            .map_err(|cause| io_error(name, location, span, cause))?;
            path_result(
                Location {
                    backend: Backend::Local,
                    path: String::new(),
                },
                path,
                span,
            )
        }
        "read_symlink" => {
            let target = fs::read_link(&path)
                .map_err(|cause| io_error("read symbolic link", location, span, cause))?;
            path_result(
                Location {
                    backend: Backend::Local,
                    path: String::new(),
                },
                target,
                span,
            )
        }
        "create_directory" => create_directory(&path, location, false, span),
        "create_directories" => create_directory(&path, location, true, span),
        "remove" | "remove_all" => remove(&path, location, name == "remove_all", span),
        "directory_iterator" | "recursive_directory_iterator" => {
            let mut values = Vec::new();
            enumerate(
                &path,
                location,
                name == "recursive_directory_iterator",
                &mut values,
                cancel,
                span,
            )?;
            Ok(NativeResult::Range(values))
        }
        _ => Err(ExecutionError::new(
            format!("unknown filesystem native fs.{name}"),
            span,
        )),
    }
}

fn file_type(metadata: Option<&fs::Metadata>) -> &'static str {
    let Some(metadata) = metadata else {
        return "not_found";
    };
    let kind = metadata.file_type();
    if kind.is_file() {
        return "regular";
    }
    if kind.is_dir() {
        return "directory";
    }
    if kind.is_symlink() {
        return "symlink";
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::FileTypeExt;
        if kind.is_block_device() {
            return "block";
        }
        if kind.is_char_device() {
            return "character";
        }
        if kind.is_fifo() {
            return "fifo";
        }
        if kind.is_socket() {
            return "socket";
        }
    }
    "unknown"
}

fn type_matches(name: &str, kind: &str) -> bool {
    match name {
        "is_regular_file" => kind == "regular",
        "is_directory" => kind == "directory",
        "is_symlink" => kind == "symlink",
        "is_block_file" => kind == "block",
        "is_character_file" => kind == "character",
        "is_fifo" => kind == "fifo",
        "is_socket" => kind == "socket",
        "is_other" => !matches!(kind, "not_found" | "regular" | "directory" | "symlink"),
        _ => false,
    }
}

fn weakly_canonical(path: &Path) -> io::Result<PathBuf> {
    let absolute = std::path::absolute(path)?;
    let mut prefix = absolute.as_path();
    let mut tail = Vec::new();
    loop {
        match fs::canonicalize(prefix) {
            Ok(mut canonical) => {
                for component in tail.into_iter().rev() {
                    canonical.push(component);
                }
                return Ok(normalize_native(&canonical));
            }
            Err(cause) if cause.kind() == io::ErrorKind::NotFound => {
                let Some(component) = prefix.components().next_back() else {
                    return Err(cause);
                };
                tail.push(component.as_os_str().to_owned());
                prefix = prefix.parent().ok_or(cause)?;
            }
            Err(cause) => return Err(cause),
        }
    }
}

fn normalize_native(path: &Path) -> PathBuf {
    use std::path::Component;
    let mut normal = PathBuf::new();
    for component in path.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                normal.pop();
            }
            _ => normal.push(component.as_os_str()),
        }
    }
    normal
}

fn create_directory(
    path: &Path,
    location: &Location,
    recursive: bool,
    span: Span,
) -> Result<NativeResult> {
    if recursive && fs::metadata(path).is_ok_and(|metadata| metadata.is_dir()) {
        return Ok(boolean(false));
    }
    let result = if recursive {
        fs::create_dir_all(path)
    } else {
        fs::create_dir(path)
    };
    match result {
        Ok(()) => Ok(boolean(true)),
        Err(cause)
            if cause.kind() == io::ErrorKind::AlreadyExists
                && fs::metadata(path).is_ok_and(|metadata| metadata.is_dir()) =>
        {
            Ok(boolean(false))
        }
        Err(cause) => Err(io_error("create directory", location, span, cause)),
    }
}

fn remove(path: &Path, location: &Location, recursive: bool, span: Span) -> Result<NativeResult> {
    let Some(metadata) = metadata(path, location, false, span)? else {
        return Ok(boolean(false));
    };
    let result = if metadata.is_dir() {
        if recursive {
            fs::remove_dir_all(path)
        } else {
            fs::remove_dir(path)
        }
    } else {
        #[cfg(windows)]
        if metadata.file_type().is_symlink() {
            use std::os::windows::fs::FileTypeExt;
            if metadata.file_type().is_symlink_dir() {
                return fs::remove_dir(path)
                    .map(|()| boolean(true))
                    .map_err(|cause| io_error("remove symbolic link", location, span, cause));
            }
        }
        fs::remove_file(path)
    };
    result.map(|()| boolean(true)).map_err(|cause| {
        io_error(
            if recursive { "remove tree" } else { "remove" },
            location,
            span,
            cause,
        )
    })
}

fn enumerate(
    path: &Path,
    location: &Location,
    recursive: bool,
    values: &mut Vec<Value>,
    cancel: &CancellationToken,
    span: Span,
) -> Result<()> {
    let mut entries = fs::read_dir(path)
        .map_err(|cause| io_error("enumerate", location, span, cause))?
        .collect::<io::Result<Vec<_>>>()
        .map_err(|cause| io_error("read entry in", location, span, cause))?;
    entries.sort_unstable_by_key(|entry| entry.file_name());
    for entry in entries {
        check(cancel, span)?;
        let name = entry.file_name().into_string().map_err(|name| {
            ExecutionError::new(
                format!("directory entry is not UTF-8 in {location}: {name:?}"),
                span,
            )
        })?;
        let child = Location {
            backend: location.backend.clone(),
            path: Path::new(&location.path)
                .join(name)
                .to_str()
                .expect("UTF-8 components")
                .to_owned(),
        };
        values.push(path_value(child.clone()));
        if recursive
            && entry
                .file_type()
                .map_err(|cause| io_error("inspect directory entry", &child, span, cause))?
                .is_dir()
        {
            enumerate(&entry.path(), &child, true, values, cancel, span)?;
        }
    }
    Ok(())
}

const PERMISSION_FLAGS: &[(&str, u32)] = &[
    ("owner_read", 0o400),
    ("owner_write", 0o200),
    ("owner_exec", 0o100),
    ("group_read", 0o040),
    ("group_write", 0o020),
    ("group_exec", 0o010),
    ("others_read", 0o004),
    ("others_write", 0o002),
    ("others_exec", 0o001),
    ("set_uid", 0o4000),
    ("set_gid", 0o2000),
    ("sticky_bit", 0o1000),
];

fn parse_mode(text: &str, span: Span) -> Result<u32> {
    let octal = text.strip_prefix("0o").unwrap_or(text);
    if !octal.is_empty() && octal.bytes().all(|byte| matches!(byte, b'0'..=b'7')) {
        return u32::from_str_radix(octal, 8)
            .ok()
            .filter(|mode| *mode <= 0o7777)
            .ok_or_else(|| {
                ExecutionError::new(
                    format!("permission mode outside filesystem flag range: {text:?}"),
                    span,
                )
            });
    }
    let mut mode = 0;
    for flag in text.split(['|', ',', ' ']).filter(|flag| !flag.is_empty()) {
        mode |= match flag {
            "none" => 0,
            "owner_all" => 0o700,
            "group_all" => 0o070,
            "others_all" => 0o007,
            "all" => 0o777,
            flag => PERMISSION_FLAGS
                .iter()
                .find_map(|(name, bits)| (*name == flag).then_some(*bits))
                .ok_or_else(|| {
                    ExecutionError::new(
                        format!("unknown filesystem permission flag: {flag:?}"),
                        span,
                    )
                })?,
        };
    }
    Ok(mode)
}

fn permission_values(mode: u32) -> NativeResult {
    NativeResult::Range(
        PERMISSION_FLAGS
            .iter()
            .filter(|(_, bits)| mode & bits != 0)
            .map(|(name, _)| Value::String((*name).to_owned()))
            .collect(),
    )
}

fn permissions(
    name: &str,
    args: &[Location],
    cancel: &CancellationToken,
    span: Span,
) -> Result<NativeResult> {
    let writable = matches!(name, "set_permissions" | "set_readonly");
    let (location, value) = if writable {
        let (location, value) = pair(args, name, span)?;
        (location, Some(value))
    } else {
        (one(args, name, span)?, None)
    };
    let mode = if name == "set_permissions" {
        Some(parse_mode(&value.expect("paired mode").path, span)?)
    } else {
        None
    };
    let Some(path) = local(location, !writable, span)? else {
        if let Some(mode) = mode {
            return remote(
                name,
                &[
                    location.clone(),
                    Location {
                        backend: location.backend.clone(),
                        path: format!("{mode:o}"),
                    },
                ],
                cancel,
                span,
            );
        }
        return remote(name, args, cancel, span);
    };
    let mut permissions = fs::metadata(&path)
        .map_err(|cause| io_error("read permissions of", location, span, cause))?
        .permissions();
    match name {
        "is_readonly" => Ok(boolean(permissions.readonly())),
        "permissions" => {
            #[cfg(unix)]
            let mode = {
                use std::os::unix::fs::PermissionsExt;
                permissions.mode()
            };
            #[cfg(not(unix))]
            let mode = if permissions.readonly() { 0o444 } else { 0o666 };
            Ok(permission_values(mode))
        }
        "set_permissions" => {
            #[cfg(unix)]
            {
                use std::os::unix::fs::PermissionsExt;
                permissions.set_mode(mode.expect("parsed mode"));
            }
            #[cfg(not(unix))]
            permissions.set_readonly(mode.expect("parsed mode") & 0o222 == 0);
            fs::set_permissions(&path, permissions)
                .map(|()| NativeResult::Void)
                .map_err(|cause| io_error("set permissions of", location, span, cause))
        }
        "set_readonly" => {
            permissions.set_readonly(value.expect("paired readonly").path == "true");
            fs::set_permissions(&path, permissions)
                .map(|()| NativeResult::Void)
                .map_err(|cause| io_error("set readonly attribute of", location, span, cause))
        }
        _ => unreachable!(),
    }
}

fn binary(
    name: &str,
    args: &[Location],
    cancel: &CancellationToken,
    span: Span,
) -> Result<NativeResult> {
    let (left, right) = pair(args, name, span)?;
    if matches!(name, "create_symlink" | "create_directory_symlink") {
        let Some(target) = local(right, false, span)? else {
            return remote(name, args, cancel, span);
        };
        return symlink(
            Path::new(&left.path),
            &target,
            name == "create_directory_symlink",
        )
        .map(|()| NativeResult::Void)
        .map_err(|cause| {
            ExecutionError::caused(
                format!("cannot fs::{name} {:?} -> {right}", left.path),
                span,
                cause,
            )
        });
    }
    let source = local(left, name != "rename", span)?;
    let target = local(right, name == "equivalent", span)?;
    let (Some(source), Some(target)) = (source, target) else {
        if left.backend != right.backend {
            if name == "equivalent" {
                return Ok(boolean(false));
            }
            return Err(ExecutionError::new(
                format!("fs::{name} requires paths on one filesystem endpoint: {left}, {right}"),
                span,
            ));
        }
        return remote(name, args, cancel, span);
    };
    let result = match name {
        "equivalent" => {
            return same_file::is_same_file(&source, &target)
                .map(boolean)
                .map_err(|cause| io_error("compare identity of", left, span, cause));
        }
        "rename" => fs::rename(&source, &target),
        "create_hard_link" => fs::hard_link(&source, &target),
        "create_symlink" | "create_directory_symlink" => {
            symlink(&source, &target, name == "create_directory_symlink")
        }
        "copy_symlink" => {
            let link = fs::read_link(&source)
                .map_err(|cause| io_error("read symbolic link", left, span, cause))?;
            #[cfg(windows)]
            let directory = {
                use std::os::windows::fs::FileTypeExt;
                fs::symlink_metadata(&source)
                    .map_err(|cause| io_error("inspect symbolic link", left, span, cause))?
                    .file_type()
                    .is_symlink_dir()
            };
            #[cfg(not(windows))]
            let directory = false;
            symlink(&link, &target, directory)
        }
        _ => unreachable!(),
    };
    result.map(|()| NativeResult::Void).map_err(|cause| {
        ExecutionError::caused(format!("cannot fs::{name} {left} -> {right}"), span, cause)
    })
}

fn symlink(source: &Path, target: &Path, directory: bool) -> io::Result<()> {
    #[cfg(unix)]
    {
        let _ = directory;
        std::os::unix::fs::symlink(source, target)
    }
    #[cfg(windows)]
    {
        if directory {
            std::os::windows::fs::symlink_dir(source, target)
        } else {
            std::os::windows::fs::symlink_file(source, target)
        }
    }
    #[cfg(not(any(unix, windows)))]
    {
        let _ = (source, target, directory);
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "symbolic links are not supported on this platform",
        ))
    }
}

fn copy_file(args: &[Location], cancel: &CancellationToken, span: Span) -> Result<NativeResult> {
    let (source, destination) = pair(args, "copy_file", span)?;
    let operation = crate::compiler::CopyOp {
        source: source.clone(),
        destination: destination.clone(),
        pattern: None,
        span,
    };
    crate::runtime::execute_copy_cancellable(&operation, 64 * 1024, cancel.clone())
        .map(|_| boolean(true))
        .map_err(|cause| {
            ExecutionError::caused(
                format!("cannot copy file {source} -> {destination}"),
                span,
                cause,
            )
        })
}

fn remote(
    name: &str,
    args: &[Location],
    cancel: &CancellationToken,
    span: Span,
) -> Result<NativeResult> {
    let location = if matches!(name, "create_symlink" | "create_directory_symlink") {
        pair(args, name, span)?.1
    } else {
        one_or_first(args, span)?
    };
    if !matches!(location.backend, Backend::Ssh { .. } | Backend::Wsl) {
        return Err(ExecutionError::new(
            format!("fs::{name} requires a filesystem endpoint: {location}"),
            span,
        ));
    }
    let mut arguments = vec![name];
    for arg in args {
        if arg.path.contains('\0') {
            return Err(ExecutionError::new(
                format!("filesystem path contains NUL: {arg}"),
                span,
            ));
        }
        arguments.push(&arg.path);
    }
    let output = crate::runtime::remote_command_with_cancel(
        &location.backend,
        REMOTE_FS,
        &arguments,
        64 * 1024,
        cancel,
    )
    .map_err(|cause| {
        if cancel.is_cancelled() {
            return ExecutionError::cancelled(
                format!("remote fs::{name} cancelled for {location}: {cause}"),
                span,
            );
        }
        ExecutionError::new(format!("cannot fs::{name} {location}: {cause}"), span)
    })?
    .stdout;
    decode_remote(name, location, output, span)
}

fn one_or_first(args: &[Location], span: Span) -> Result<&Location> {
    args.first()
        .ok_or_else(|| ExecutionError::new("filesystem native requires a path", span))
}

fn decode_remote(
    name: &str,
    location: &Location,
    output: Vec<u8>,
    span: Span,
) -> Result<NativeResult> {
    if matches!(
        name,
        "set_permissions"
            | "set_readonly"
            | "rename"
            | "create_hard_link"
            | "create_symlink"
            | "create_directory_symlink"
            | "copy_symlink"
    ) {
        return Ok(NativeResult::Void);
    }
    if name == "permissions" {
        let mode = std::str::from_utf8(&output).map_err(|cause| {
            ExecutionError::caused("invalid permission mode response", span, cause)
        })?;
        return Ok(permission_values(parse_mode(mode.trim(), span)?));
    }
    if matches!(name, "directory_iterator" | "recursive_directory_iterator") {
        let mut values = Vec::new();
        for path in nul_strings(&output, location, span)? {
            values.push(path_value(Location {
                backend: location.backend.clone(),
                path: path.to_owned(),
            }));
        }
        values.sort_unstable_by(|left, right| match (left, right) {
            (Value::Path(left, _), Value::Path(right, _)) => left.path.cmp(&right.path),
            _ => unreachable!(),
        });
        return Ok(NativeResult::Range(values));
    }
    if matches!(
        name,
        "absolute" | "canonical" | "weakly_canonical" | "read_symlink"
    ) {
        let paths = nul_strings(&output, location, span)?;
        if let [path] = paths.as_slice() {
            return Ok(NativeResult::Value(path_value(Location {
                backend: location.backend.clone(),
                path: (*path).to_owned(),
            })));
        }
        return Err(ExecutionError::new(
            format!("invalid remote path response for fs::{name} {location}"),
            span,
        ));
    }
    let value = std::str::from_utf8(&output).map_err(|cause| {
        ExecutionError::caused(
            format!("non-UTF-8 response for fs::{name} {location}"),
            span,
            cause,
        )
    })?;
    if matches!(name, "status" | "symlink_status") {
        return Ok(text(value));
    }
    match value {
        "true" => Ok(boolean(true)),
        "false" => Ok(boolean(false)),
        _ => Err(ExecutionError::new(
            format!("invalid remote Bool response for fs::{name} {location}: {value:?}"),
            span,
        )),
    }
}

fn nul_strings<'a>(output: &'a [u8], location: &Location, span: Span) -> Result<Vec<&'a str>> {
    if output.is_empty() {
        return Ok(Vec::new());
    }
    if output.last() != Some(&0) {
        return Err(ExecutionError::new(
            format!("invalid NUL-framed filesystem response from {location}"),
            span,
        ));
    }
    output[..output.len() - 1]
        .split(|byte| *byte == 0)
        .map(|path| {
            std::str::from_utf8(path).map_err(|cause| {
                ExecutionError::caused(
                    format!("non-UTF-8 filesystem path from {location}"),
                    span,
                    cause,
                )
            })
        })
        .collect()
}

// GNU filesystem tools are already required by the native SSH/WSL transfer
// protocol. User operands remain argv data, including spaces, newlines and '~'.
const REMOTE_FS: &str = r#"
set -eu
LC_ALL=C; export LC_ALL
action=$1; p=$2; q=${3-}
expand() { case $1 in '~') expanded=$HOME;; '~/'*) expanded=$HOME/${1#\~/};; /*) expanded=$1;; *) expanded=$PWD/$1;; esac; }
expand "$p"; p=$expanded
if [ "$#" -gt 2 ]; then
    case $action in set_permissions|set_readonly) ;; *) expand "$q"; q=$expanded;; esac
fi
fail() { printf '%s\n' "$*" >&2; exit 1; }
status() {
    if [ "$1" = link ]; then deref=; else deref=-L; fi
    if result=$(stat $deref -c '%F' -- "$p" 2>&1); then
        case $result in 'regular file'|'regular empty file') kind=regular;; directory) kind=directory;; 'symbolic link') kind=symlink;; 'block special file') kind=block;; 'character special file') kind=character;; fifo) kind=fifo;; socket) kind=socket;; *) kind=unknown;; esac
    else
        case $result in *': No such file or directory'|*': Not a directory') kind=not_found;; *) fail "$result";; esac
    fi
}
bool() { if "$@"; then printf true; else printf false; fi; }
case $action in
permissions) stat -L -c '%a' -- "$p";;
set_permissions) chmod "$q" -- "$p";;
is_readonly) mode=$(stat -L -c '%a' -- "$p"); bool test "$((0$mode & 0222))" = 0;;
set_readonly) if [ "$q" = true ]; then chmod a-w -- "$p"; else chmod a+w -- "$p"; fi;;
status) status follow; printf '%s' "$kind";;
symlink_status) status link; printf '%s' "$kind";;
is_symlink) status link; bool test "$kind" = symlink;;
is_regular_file) status follow; bool test "$kind" = regular;;
is_directory) status follow; bool test "$kind" = directory;;
is_block_file) status follow; bool test "$kind" = block;;
is_character_file) status follow; bool test "$kind" = character;;
is_fifo) status follow; bool test "$kind" = fifo;;
is_socket) status follow; bool test "$kind" = socket;;
is_other) status follow; case $kind in not_found|regular|directory|symlink) printf false;; *) printf true;; esac;;
is_empty)
    status follow
    case $kind in
        regular) size=$(stat -L -c '%s' -- "$p"); bool test "$size" = 0;;
        directory) entry=$(find "$p" -mindepth 1 -maxdepth 1 -printf x -quit); bool test -z "$entry";;
        *) fail "is_empty requires an existing regular file or directory: $p";;
    esac;;
absolute) printf '%s\0' "$p";;
canonical) readlink -e -z -- "$p";;
weakly_canonical) realpath -m -z -- "$p";;
read_symlink) readlink -z -- "$p";;
create_directory)
    status follow
    if [ "$kind" = directory ]; then printf false
    else mkdir -- "$p"; printf true; fi;;
create_directories)
    status follow
    if [ "$kind" = directory ]; then printf false
    else mkdir -p -- "$p"; printf true; fi;;
remove|remove_all)
    status link
    if [ "$kind" = not_found ]; then printf false
    elif [ "$action" = remove_all ]; then rm -rf -- "$p"; printf true
    elif [ "$kind" = directory ]; then rmdir -- "$p"; printf true
    else rm -- "$p"; printf true; fi;;
rename) mv -T -- "$p" "$q";;
create_hard_link) ln -- "$p" "$q";;
create_symlink|create_directory_symlink) ln -s -- "$2" "$q";;
copy_symlink) target=$(readlink -- "$p"; printf '.'); target=${target%?.}; ln -s -- "$target" "$q";;
equivalent)
    stat -L -c '' -- "$p" >/dev/null; stat -L -c '' -- "$q" >/dev/null
    left=$(stat -L -c '%d:%i' -- "$p"); right=$(stat -L -c '%d:%i' -- "$q"); bool test "$left" = "$right";;
directory_iterator) status follow; [ "$kind" = directory ] || fail "not a directory: $p"; find -H "$p" -mindepth 1 -maxdepth 1 -print0;;
recursive_directory_iterator) status follow; [ "$kind" = directory ] || fail "not a directory: $p"; find -H "$p" -mindepth 1 -print0;;
*) fail "unknown filesystem native: $action";;
esac
"#;

#[cfg(test)]
mod tests {
    use super::*;

    fn workspace() -> tempfile::TempDir {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/native-fs-v070");
        fs::create_dir_all(&root).unwrap();
        tempfile::tempdir_in(root).unwrap()
    }

    fn location(path: &Path) -> Location {
        Location {
            backend: Backend::Local,
            path: path.to_str().unwrap().to_owned(),
        }
    }

    fn boolean_result(result: NativeResult) -> bool {
        match result {
            NativeResult::Value(Value::Bool(value)) => value,
            _ => panic!("expected Bool"),
        }
    }

    fn string_result(result: NativeResult) -> String {
        match result {
            NativeResult::Value(Value::String(value)) => value,
            _ => panic!("expected String"),
        }
    }

    fn path_result_value(result: NativeResult) -> Location {
        match result {
            NativeResult::Value(Value::Path(value, _)) => value,
            _ => panic!("expected Path"),
        }
    }

    #[test]
    fn local_filesystem_lifecycle_and_endpoint_values() {
        let root = workspace();
        let cancel = CancellationToken::new();
        let span = Span::default();
        let directory = location(&root.path().join("dir"));
        assert!(boolean_result(
            dispatch(
                "create_directory",
                std::slice::from_ref(&directory),
                &cancel,
                span
            )
            .unwrap()
        ));
        assert!(!boolean_result(
            dispatch(
                "create_directory",
                std::slice::from_ref(&directory),
                &cancel,
                span
            )
            .unwrap()
        ));
        assert!(boolean_result(
            dispatch(
                "is_directory",
                std::slice::from_ref(&directory),
                &cancel,
                span
            )
            .unwrap()
        ));
        assert!(boolean_result(
            dispatch("is_empty", std::slice::from_ref(&directory), &cancel, span).unwrap()
        ));
        let name = if cfg!(windows) {
            "source '$(false)' α"
        } else {
            "source '$(false)'\nα"
        };
        let source = location(&root.path().join("dir").join(name));
        fs::write(&source.path, b"original bytes").unwrap();
        let inaccessible_child = location(&Path::new(&source.path).join("child"));
        for name in ["status", "symlink_status"] {
            assert_eq!(
                string_result(
                    dispatch(
                        name,
                        std::slice::from_ref(&inaccessible_child),
                        &cancel,
                        span
                    )
                    .unwrap()
                ),
                "not_found"
            );
        }
        for name in ["is_directory", "is_regular_file"] {
            assert!(!boolean_result(
                dispatch(
                    name,
                    std::slice::from_ref(&inaccessible_child),
                    &cancel,
                    span
                )
                .unwrap()
            ));
        }

        let destination = location(&root.path().join("dir/copy"));
        assert!(boolean_result(
            dispatch(
                "copy_file",
                &[source.clone(), destination.clone()],
                &cancel,
                span
            )
            .unwrap()
        ));
        assert_eq!(fs::read(&destination.path).unwrap(), b"original bytes");
        let linked = location(&root.path().join("dir/hard"));
        dispatch(
            "create_hard_link",
            &[source.clone(), linked.clone()],
            &cancel,
            span,
        )
        .unwrap();
        assert!(boolean_result(
            dispatch("equivalent", &[source.clone(), linked], &cancel, span).unwrap()
        ));
        let moved = location(&root.path().join("dir/moved"));
        dispatch(
            "rename",
            &[destination.clone(), moved.clone()],
            &cancel,
            span,
        )
        .unwrap();
        assert!(!Path::new(&destination.path).exists());
        assert_eq!(fs::read(&moved.path).unwrap(), b"original bytes");
        let NativeResult::Range(values) = dispatch(
            "directory_iterator",
            std::slice::from_ref(&directory),
            &cancel,
            span,
        )
        .unwrap() else {
            panic!("expected Range<Path>")
        };
        assert_eq!(values.len(), 3);
        assert!(
            values.iter().all(
                |value| matches!(value, Value::Path(path, _) if path.backend == Backend::Local)
            )
        );
        assert!(boolean_result(
            dispatch(
                "remove_all",
                std::slice::from_ref(&directory),
                &cancel,
                span
            )
            .unwrap()
        ));
        assert!(!boolean_result(
            dispatch("remove_all", &[directory], &cancel, span).unwrap()
        ));
    }

    #[cfg(unix)]
    #[test]
    fn link_status_recursive_iteration_and_unlink_do_not_follow_children() {
        use std::os::unix::fs::symlink;
        let root = workspace();
        let cancel = CancellationToken::new();
        let span = Span::default();
        fs::create_dir(root.path().join("tree")).unwrap();
        fs::create_dir(root.path().join("outside")).unwrap();
        fs::write(root.path().join("outside/retained"), b"safe").unwrap();
        symlink(root.path().join("outside"), root.path().join("tree/linked")).unwrap();
        symlink("missing", root.path().join("tree/dangling")).unwrap();
        let tree = location(&root.path().join("tree"));
        let dangling = location(&root.path().join("tree/dangling"));
        assert_eq!(
            string_result(
                dispatch("status", std::slice::from_ref(&dangling), &cancel, span).unwrap()
            ),
            "not_found"
        );
        assert_eq!(
            string_result(
                dispatch(
                    "symlink_status",
                    std::slice::from_ref(&dangling),
                    &cancel,
                    span
                )
                .unwrap()
            ),
            "symlink"
        );
        let NativeResult::Range(values) = dispatch(
            "recursive_directory_iterator",
            std::slice::from_ref(&tree),
            &cancel,
            span,
        )
        .unwrap() else {
            panic!("expected Range<Path>")
        };
        assert_eq!(values.len(), 2);
        assert_eq!(
            path_result_value(dispatch("read_symlink", &[dangling], &cancel, span).unwrap()).path,
            "missing"
        );
        assert!(boolean_result(
            dispatch("remove_all", &[tree], &cancel, span).unwrap()
        ));
        assert_eq!(
            fs::read(root.path().join("outside/retained")).unwrap(),
            b"safe"
        );
    }

    #[test]
    fn weak_canonical_resolves_existing_prefix_then_normalizes_missing_tail() {
        let root = workspace();
        let original = root.path().join("missing/../new");
        assert_eq!(
            weakly_canonical(&original).unwrap(),
            fs::canonicalize(root.path()).unwrap().join("new")
        );
    }

    #[test]
    fn remote_directory_response_larger_than_transport_buffer_keeps_all_entries() {
        let prefix = format!("/workspace/{}/", "long-directory/".repeat(8));
        let names: Vec<String> = (0..1500)
            .map(|index| format!("{prefix}entry-{index:04}\0"))
            .collect();
        let output = names.concat().into_bytes();
        assert!(output.len() > 64 * 1024);
        let location = Location {
            backend: Backend::Wsl,
            path: prefix,
        };
        let NativeResult::Range(values) =
            decode_remote("directory_iterator", &location, output, Span::default()).unwrap()
        else {
            panic!("expected Range<Path>")
        };
        assert_eq!(values.len(), names.len());
    }

    #[cfg(unix)]
    #[test]
    fn remote_shell_operates_on_real_files_and_preserves_framing() {
        use std::os::unix::fs::symlink;
        use std::process::{Command, Output};
        let root = workspace();
        let directory = root.path().join("literal '$(false)'\nα");
        let invoke = |name: &str, paths: &[&Path]| -> Output {
            Command::new("sh")
                .arg("-c")
                .arg(REMOTE_FS)
                .arg("msync-native-fs")
                .arg(name)
                .args(paths)
                .output()
                .unwrap()
        };
        let success = |output: Output| -> Vec<u8> {
            assert!(
                output.status.success(),
                "{}",
                String::from_utf8_lossy(&output.stderr)
            );
            output.stdout
        };
        assert_eq!(
            success(invoke("create_directories", &[&directory])),
            b"true"
        );
        assert_eq!(success(invoke("is_directory", &[&directory])), b"true");
        fs::write(directory.join(".hidden\nfile"), b"payload").unwrap();
        fs::create_dir(directory.join("nested")).unwrap();
        fs::write(directory.join("nested/child"), b"child").unwrap();
        let target = directory.join(".hidden\nfile");
        assert_eq!(
            success(invoke("status", &[&target.join("child")])),
            b"not_found"
        );
        assert_eq!(
            success(invoke("is_directory", &[&target.join("child")])),
            b"false"
        );

        let linked = directory.join("linked");
        symlink(".hidden\nfile", &linked).unwrap();
        let copied = directory.join("copied-link");
        success(invoke("copy_symlink", &[&linked, &copied]));
        assert_eq!(fs::read_link(&copied).unwrap(), Path::new(".hidden\nfile"));
        let fake_remote = Location {
            backend: Backend::Wsl,
            path: directory.to_str().unwrap().to_owned(),
        };
        let NativeResult::Range(values) = decode_remote(
            "recursive_directory_iterator",
            &fake_remote,
            success(invoke("recursive_directory_iterator", &[&directory])),
            Span::default(),
        )
        .unwrap() else {
            panic!("expected Range<Path>")
        };
        assert_eq!(values.len(), 5);
        assert!(values.iter().all(
            |value| matches!(value, Value::Path(location, _) if location.backend == Backend::Wsl)
        ));
        assert_eq!(success(invoke("equivalent", &[&target, &linked])), b"true");
        assert!(
            invoke("directory_iterator", &[&target])
                .status
                .code()
                .unwrap()
                != 0
        );
        let cycle = directory.join("cycle");
        symlink(&cycle, &cycle).unwrap();
        assert!(!invoke("is_directory", &[&cycle]).status.success());
        assert_eq!(success(invoke("remove_all", &[&directory])), b"true");
        assert_eq!(success(invoke("remove_all", &[&directory])), b"false");
    }
}
