//! Durable discovery of interrupted statement publications.
//!
//! The registry contains owned-stage and coordinator references, not payloads.
//! Registrations are durable before publication and survive owner termination.
//! Short index locks protect record discovery and retirement. Each live owner
//! locks only its own record; unrelated sessions and recovery remain independent.

use std::ffi::{OsStr, OsString};
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use serde::{Deserialize, Serialize};

const DIRECTORY: &str = ".msync-recovery-v1";
const GATE: &str = ".lock";
const RECORD_PREFIX: &str = "record-";
const JOURNAL_PREFIX: &str = ".msync-statement-";
const STAGE_MARKER: &str = ".msync-owned-stage-v1";
const MAX_RECORD_BYTES: u64 = 64 * 1024;
const MAX_RECORDS: usize = 16_384;
static NEXT_RECORD: AtomicU64 = AtomicU64::new(0);

#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Record {
    version: u32,
    platform: String,
    parent: Vec<u8>,
    volume: u64,
    index: u64,
    entry: RecordKind,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
enum RecordKind {
    Coordinator {
        journal: String,
    },
    Stage {
        workspace: String,
        volume: u64,
        index: u64,
        token: String,
        cleaning: bool,
    },
}

struct Pending {
    path: PathBuf,
    file: File,
    parent: PathBuf,
    parent_identity: (u64, u64),
    entry: RecordKind,
}

/// A private, persistent index beneath the configured execution scratch root.
#[derive(Clone, Debug)]
pub struct Registry {
    directory: PathBuf,
    identity: Arc<same_file::Handle>,
}

/// Retains the registry identity, never a lock across payload or program work.
#[must_use = "retain the lease through batch preparation and registration"]
#[derive(Debug)]
pub struct RegistryLease {
    registry: Registry,
}

/// Retain this owner until the batch has been finalized. Drop keeps recovery live.
#[must_use = "retain registration until publication finalizes, then call finish"]
#[derive(Debug)]
pub struct Registration {
    path: PathBuf,
    directory: PathBuf,
    file: Option<File>,
}

/// One registered coordinator inspected during startup recovery.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RecoveryReport {
    pub parent: PathBuf,
    pub journal: PathBuf,
    pub recovered: usize,
}

impl Registry {
    /// Retain an existing private registry without creating or changing it.
    /// Live record locks are deliberately not opened or read by this lookup.
    pub fn open_existing(scratch_root: &Path) -> io::Result<Option<Self>> {
        validate_absolute(scratch_root)?;
        checked(super::check_path(scratch_root))?;
        let directory = scratch_root.join(DIRECTORY);
        if checked(super::safe_metadata(&directory))?.is_none() {
            return Ok(None);
        }
        validate_directory(&directory)?;
        let identity = Arc::new(same_file::Handle::from_path(&directory)?);
        let _gate = open_gate(&directory)?;
        let registry = Self {
            directory,
            identity,
        };
        registry.verify()?;
        Ok(Some(registry))
    }

    pub fn new(scratch_root: &Path) -> io::Result<Self> {
        validate_absolute(scratch_root)?;
        checked(super::check_path(scratch_root))?;
        fs::create_dir_all(scratch_root)?;
        checked(super::check_path(scratch_root))?;
        let directory = scratch_root.join(DIRECTORY);
        let created = match create_private_directory(&directory) {
            Ok(()) => true,
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => false,
            Err(error) => return Err(error),
        };
        validate_directory(&directory)?;
        if created {
            checked(super::durable::sync_directory(scratch_root))?;
        }
        let gate = directory.join(GATE);
        let file = open_private_file(&gate, false)?;
        file.sync_all()?;
        checked(super::durable::sync_directory(&directory))?;
        let identity = Arc::new(same_file::Handle::from_path(&directory)?);
        Ok(Self {
            directory,
            identity,
        })
    }

    pub fn directory(&self) -> &Path {
        &self.directory
    }

    pub(crate) fn verify(&self) -> io::Result<()> {
        validate_directory(&self.directory)?;
        if same_file::Handle::from_path(&self.directory)? != *self.identity {
            return Err(invalid("recovery registry directory identity changed"));
        }
        Ok(())
    }

    pub(crate) fn excludes(&self, path: &Path) -> io::Result<bool> {
        self.verify()?;
        Ok(same_file::Handle::from_path(path)? == *self.identity)
    }

    pub(crate) fn contains_private(&self, path: &Path) -> io::Result<bool> {
        self.verify()?;
        for ancestor in path.ancestors() {
            match same_file::Handle::from_path(ancestor) {
                Ok(identity) if identity == *self.identity => return Ok(true),
                Ok(_) => {}
                Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                Err(error) => return Err(error),
            }
        }
        Ok(false)
    }

    /// Retain a verified registry identity for subsequent stage registrations.
    pub fn enter(&self) -> io::Result<RegistryLease> {
        self.verify()?;
        Ok(RegistryLease {
            registry: self.clone(),
        })
    }

    /// Register a prepared coordinator without retaining the index lock.
    pub fn register(&self, parent: &Path, journal: &Path) -> io::Result<Registration> {
        self.enter()?.register(parent, journal)
    }
}

impl RegistryLease {
    /// Register a prepared batch before admitting its first destination change.
    pub fn register(self, parent: &Path, journal: &Path) -> io::Result<Registration> {
        let Self { registry } = self;
        registry.verify()?;
        validate_absolute(parent)?;
        checked(super::check_path(parent))?;
        if journal.parent() != Some(parent) {
            return Err(invalid(
                "coordinator must be an immediate child of its parent",
            ));
        }
        let journal_name = journal
            .file_name()
            .and_then(OsStr::to_str)
            .ok_or_else(|| invalid("coordinator basename is not valid UTF-8"))?;
        validate_journal_name(journal_name)?;
        let metadata = checked(super::safe_metadata(journal))?
            .ok_or_else(|| invalid("statement coordinator does not exist"))?;
        if !metadata.is_dir() {
            return Err(invalid("statement coordinator is not a directory"));
        }
        let entry = RecordKind::Coordinator {
            journal: journal_name.into(),
        };
        persist_registration(&registry, parent, entry)
    }

    /// Register ownership before materializing a single payload byte. The marker
    /// and physical directory ID distinguish this stage from same-named user data.
    pub fn register_stage(&self, parent: &Path, workspace: &Path) -> io::Result<Registration> {
        self.registry.verify()?;
        validate_absolute(parent)?;
        checked(super::check_path(workspace))?;
        if workspace.parent() != Some(parent) {
            return Err(invalid("stage must be an immediate child of its parent"));
        }
        let name = workspace
            .file_name()
            .and_then(OsStr::to_str)
            .ok_or_else(|| invalid("stage basename is not valid UTF-8"))?;
        validate_stage_name(name)?;
        let metadata = checked(super::safe_metadata(workspace))?
            .ok_or_else(|| invalid("owned stage does not exist"))?;
        if !metadata.is_dir() || fs::read_dir(workspace)?.next().is_some() {
            return Err(invalid(
                "stage registration requires a newly allocated empty directory",
            ));
        }
        let (volume, index) = checked(super::durable::fingerprint(workspace))?;
        let token = unique_id()?;
        let marker = workspace.join(STAGE_MARKER);
        let mut file = open_private_file(&marker, true)?;
        file.write_all(token.as_bytes())?;
        file.sync_all()?;
        checked(super::durable::sync_directory(workspace))?;
        checked(super::durable::sync_directory(parent))?;
        let entry = RecordKind::Stage {
            workspace: name.into(),
            volume,
            index,
            token,
            cleaning: false,
        };
        persist_registration(&self.registry, parent, entry)
    }
}

fn persist_registration(
    registry: &Registry,
    parent: &Path,
    entry: RecordKind,
) -> io::Result<Registration> {
    let record = make_record(parent, entry)?;
    let bytes = serde_json::to_vec(&record).map_err(io::Error::other)?;
    if bytes.len() as u64 > MAX_RECORD_BYTES {
        return Err(invalid("recovery registration exceeds its size limit"));
    }
    let _index = lock_index(&registry.directory)?;
    let (temporary, path, mut file) = create_record(&registry.directory)?;
    file.try_lock().map_err(lock_error)?;
    file.write_all(&bytes)?;
    file.sync_all()?;
    super::move_new(&temporary, &path)?;
    checked(super::durable::sync_directory(&registry.directory))?;
    Ok(Registration {
        path,
        directory: registry.directory.clone(),
        file: Some(file),
    })
}

impl Registration {
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// First finalize the owner or transfer its lifetime to a durable coordinator.
    pub fn finish(mut self) -> io::Result<()> {
        let file = self.file.take().expect("live recovery registration");
        let _index = lock_index(&self.directory)?;
        if checked(super::safe_metadata(&self.path))?.is_none() {
            return Ok(());
        }
        ensure_same_file(&self.path, &file)?;
        fs::remove_file(&self.path)?;
        checked(super::durable::sync_directory(&self.directory))?;
        drop(file);
        Ok(())
    }
}

pub(crate) fn may_need_recovery(scratch_root: &Path) -> bool {
    recovery_path_present(&scratch_root.join(DIRECTORY))
}

pub(crate) fn recovery_path_present(path: &Path) -> bool {
    // Probe errors must reach the ordinary recovery owner, not look like absence.
    !matches!(fs::symlink_metadata(path), Err(cause) if matches!(cause.kind(), io::ErrorKind::NotFound | io::ErrorKind::NotADirectory))
}

/// Recover registered statements before admitting execution effects.
///
/// A missing registry is a read-only cold start. Record schemas, parent identities
/// and owner locks are validated before recovery starts. Coordinators are recovered
/// before their stages; each remaining stage renews its ownership proof at cleanup.
pub fn recover(scratch_root: &Path) -> io::Result<Vec<RecoveryReport>> {
    validate_absolute(scratch_root)?;
    checked(super::check_path(scratch_root))?;
    let directory = scratch_root.join(DIRECTORY);
    if checked(super::safe_metadata(&directory))?.is_none() {
        return Ok(Vec::new());
    }
    validate_directory(&directory)?;
    let mut pending = claim_records(&directory)?;
    // The index lock is released before any parent lock or filesystem recovery.
    pending.sort_by_key(|pending| matches!(pending.entry, RecordKind::Stage { .. }));
    let mut reports = Vec::with_capacity(pending.len());
    for mut pending in pending {
        if !record_exists(&pending.path, &pending.file)? {
            continue;
        }
        let (journal, recovered) = match &pending.entry {
            RecordKind::Coordinator { journal } => (
                pending.parent.join(journal),
                checked(super::durable::recover_journal(
                    &pending.parent,
                    OsStr::new(journal),
                ))?,
            ),
            RecordKind::Stage { workspace, .. } => {
                let journal = pending.parent.join(workspace);
                let recovered = recover_stage(&mut pending)?;
                (journal, recovered)
            }
        };
        {
            let _index = lock_index(&directory)?;
            if record_exists(&pending.path, &pending.file)? {
                fs::remove_file(&pending.path)?;
                checked(super::durable::sync_directory(&directory))?;
            }
        }
        reports.push(RecoveryReport {
            journal,
            parent: pending.parent,
            recovered,
        });
    }
    Ok(reports)
}

fn claim_records(directory: &Path) -> io::Result<Vec<Pending>> {
    let _index = lock_index(directory)?;
    let mut paths = Vec::new();
    for entry in fs::read_dir(directory)? {
        let entry = entry?;
        let name = entry.file_name();
        let Some(name) = name.to_str() else {
            return Err(invalid("unrecognized recovery registry entry"));
        };
        if name == GATE || name.starts_with(".pending-") {
            continue;
        }
        validate_record_name(name)?;
        paths.push(entry.path());
        if paths.len() > MAX_RECORDS {
            return Err(invalid("recovery registry contains too many records"));
        }
    }
    paths.sort();
    let pending = paths
        .into_iter()
        .map(load_registration)
        .collect::<io::Result<Vec<_>>>()?;
    Ok(pending.into_iter().flatten().collect())
}

fn load_registration(path: PathBuf) -> io::Result<Option<Pending>> {
    if checked(super::safe_metadata(&path))?.is_none() {
        return Ok(None);
    }
    validate_private_file(&path)?;
    let mut file = open_existing(&path)?;
    match file.try_lock() {
        Ok(()) => {}
        Err(fs::TryLockError::WouldBlock) => return Ok(None),
        Err(fs::TryLockError::Error(error)) => return Err(error),
    }
    ensure_same_file(&path, &file)?;
    if file.metadata()?.len() > MAX_RECORD_BYTES {
        return Err(invalid("recovery registration exceeds its size limit"));
    }
    let mut bytes = Vec::new();
    (&mut file)
        .take(MAX_RECORD_BYTES + 1)
        .read_to_end(&mut bytes)?;
    let record: Record = serde_json::from_slice(&bytes)
        .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error))?;
    if record.version != 1 || record.platform != platform() {
        return Err(invalid(
            "unsupported recovery registration version or platform",
        ));
    }
    match &record.entry {
        RecordKind::Coordinator { journal } => validate_journal_name(journal)?,
        RecordKind::Stage {
            workspace, token, ..
        } => {
            validate_stage_name(workspace)?;
            if token.is_empty()
                || token.len() > 128
                || !token
                    .bytes()
                    .all(|byte| byte.is_ascii_hexdigit() || byte == b'-')
            {
                return Err(invalid("invalid owned stage token"));
            }
        }
    }
    let parent = decode_path(record.parent)?;
    validate_absolute(&parent)?;
    checked(super::check_path(&parent))?;
    if checked(super::durable::fingerprint(&parent))? != (record.volume, record.index) {
        return Err(invalid(
            "recovery parent identity changed; registration retained",
        ));
    }
    Ok(Some(Pending {
        path,
        file,
        parent,
        parent_identity: (record.volume, record.index),
        entry: record.entry,
    }))
}

fn recover_stage(pending: &mut Pending) -> io::Result<usize> {
    let _parent = checked(super::durable::lock_parent(&pending.parent))?;
    if checked(super::durable::fingerprint(&pending.parent))? != pending.parent_identity {
        return Err(invalid(
            "recovery parent identity changed; registration retained",
        ));
    }
    // Promotion may have created its coordinator after the registry snapshot.
    // Fresh parent intent discovery, under the parent lock, owns that race.
    checked(super::durable::recover_locked(&pending.parent))?;
    let RecordKind::Stage {
        workspace,
        volume,
        index,
        token,
        cleaning,
    } = &pending.entry
    else {
        return Err(invalid("recovery entry is not an owned stage"));
    };
    let expected = (*volume, *index);
    let workspace = pending.parent.join(workspace);
    checked(super::check_path(&workspace))?;
    if checked(super::safe_metadata(&workspace))?.is_none() {
        return Ok(0);
    }
    if checked(super::durable::fingerprint(&workspace))? != expected {
        return Err(invalid(
            "owned stage directory identity changed; recovery retained",
        ));
    }
    if !cleaning {
        let marker = workspace.join(STAGE_MARKER);
        validate_private_file(&marker)?;
        let mut bytes = Vec::new();
        File::open(&marker)?.take(129).read_to_end(&mut bytes)?;
        if bytes != token.as_bytes() {
            return Err(invalid("owned stage marker changed; recovery retained"));
        }
        // Persist deletion authorization while the marker still proves ownership.
        // Recovery can then resume a partially deleted tree without requiring a
        // marker that a previous cleanup step may already have removed.
        let mut deleting = pending.entry.clone();
        if let RecordKind::Stage { cleaning, .. } = &mut deleting {
            *cleaning = true;
        }
        let record = make_record(&pending.parent, deleting.clone())?;
        pending.file = replace_record(&pending.path, &pending.file, &record)?;
        pending.entry = deleting;
    }
    let identity = same_file::Handle::from_path(&workspace)?;
    if checked(super::durable::fingerprint(&workspace))? != expected {
        return Err(invalid(
            "owned stage directory identity changed; recovery retained",
        ));
    }
    checked(super::remove_owned(&workspace, &identity))?;
    checked(super::durable::sync_directory(&pending.parent))?;
    Ok(1)
}

fn make_record(parent: &Path, entry: RecordKind) -> io::Result<Record> {
    let (volume, index) = checked(super::durable::fingerprint(parent))?;
    Ok(Record {
        version: 1,
        platform: platform().into(),
        parent: encode_path(parent),
        volume,
        index,
        entry,
    })
}

fn replace_record(path: &Path, previous: &File, record: &Record) -> io::Result<File> {
    let directory = path.parent().expect("registry record has parent");
    let bytes = serde_json::to_vec(record).map_err(io::Error::other)?;
    let _index = lock_index(directory)?;
    ensure_same_file(path, previous)?;
    let (temporary, _, mut file) = create_record(directory)?;
    file.write_all(&bytes)?;
    file.sync_all()?;
    fs::rename(&temporary, path)?;
    checked(super::durable::sync_directory(directory))?;
    file.try_lock().map_err(lock_error)?;
    ensure_same_file(path, &file)?;
    Ok(file)
}

fn validate_absolute(path: &Path) -> io::Result<()> {
    if !path.is_absolute()
        || path
            .components()
            .any(|part| matches!(part, Component::CurDir | Component::ParentDir))
    {
        return Err(invalid(
            "recovery paths must be absolute and lexically normalized",
        ));
    }
    Ok(())
}

fn validate_journal_name(name: &str) -> io::Result<()> {
    let suffix = name.strip_prefix(JOURNAL_PREFIX).unwrap_or_default();
    if suffix.is_empty()
        || suffix.len() > 128
        || !suffix
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
    {
        return Err(invalid("invalid statement coordinator basename"));
    }
    Ok(())
}

fn validate_stage_name(name: &str) -> io::Result<()> {
    let suffix = name
        .strip_prefix(".msync-publication-")
        .or_else(|| name.strip_prefix(JOURNAL_PREFIX))
        .or_else(|| name.strip_prefix("execute-"))
        .unwrap_or_default();
    if suffix.is_empty()
        || suffix.len() > 128
        || !suffix
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
    {
        return Err(invalid("invalid owned stage basename"));
    }
    Ok(())
}

fn validate_record_name(name: &str) -> io::Result<()> {
    let Some(suffix) = name
        .strip_prefix(RECORD_PREFIX)
        .and_then(|name| name.strip_suffix(".json"))
    else {
        return Err(invalid("unrecognized recovery registry entry"));
    };
    if suffix.is_empty()
        || suffix.len() > 128
        || !suffix
            .bytes()
            .all(|byte| byte.is_ascii_hexdigit() || byte == b'-')
    {
        return Err(invalid("invalid recovery record basename"));
    }
    Ok(())
}

fn create_record(directory: &Path) -> io::Result<(PathBuf, PathBuf, File)> {
    loop {
        let id = unique_id()?;
        let temporary = directory.join(format!(".pending-{id}.tmp"));
        let path = directory.join(format!("{RECORD_PREFIX}{id}.json"));
        match open_private_file(&temporary, true) {
            Ok(file) => return Ok((temporary, path, file)),
            Err(error) if error.kind() == io::ErrorKind::AlreadyExists => continue,
            Err(error) => return Err(error),
        }
    }
}

fn unique_id() -> io::Result<String> {
    let sequence = NEXT_RECORD.fetch_add(1, Ordering::Relaxed);
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_err(io::Error::other)?
        .as_nanos();
    Ok(format!("{:x}-{nanos:x}-{sequence:x}", std::process::id()))
}

fn open_gate(directory: &Path) -> io::Result<File> {
    let path = directory.join(GATE);
    validate_private_file(&path)?;
    open_existing(&path)
}

fn lock_index(directory: &Path) -> io::Result<File> {
    let gate = open_gate(directory)?;
    gate.lock()?;
    Ok(gate)
}

fn record_exists(path: &Path, file: &File) -> io::Result<bool> {
    if checked(super::safe_metadata(path))?.is_none() {
        return Ok(false);
    }
    ensure_same_file(path, file)?;
    Ok(true)
}

fn open_existing(path: &Path) -> io::Result<File> {
    let mut options = OpenOptions::new();
    options.read(true).write(true);
    no_follow(&mut options);
    let file = options.open(path)?;
    ensure_same_file(path, &file)?;
    Ok(file)
}

fn open_private_file(path: &Path, create_new: bool) -> io::Result<File> {
    checked(super::check_path(path))?;
    let mut options = OpenOptions::new();
    options.read(true).write(true);
    if create_new {
        options.create_new(true);
    } else {
        options.create(true).truncate(false);
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.mode(0o600);
    }
    no_follow(&mut options);
    let file = options.open(path)?;
    validate_private_file(path)?;
    ensure_same_file(path, &file)?;
    Ok(file)
}

fn create_private_directory(path: &Path) -> io::Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::DirBuilderExt;
        let mut builder = fs::DirBuilder::new();
        builder.mode(0o700);
        builder.create(path)
    }
    #[cfg(not(unix))]
    fs::create_dir(path)
}

fn validate_directory(path: &Path) -> io::Result<()> {
    checked(super::check_path(path))?;
    let metadata = fs::symlink_metadata(path)?;
    if !metadata.is_dir() {
        return Err(invalid("recovery registry is not an ordinary directory"));
    }
    validate_owner(&metadata)
}

fn validate_private_file(path: &Path) -> io::Result<()> {
    checked(super::check_path(path))?;
    let metadata = fs::symlink_metadata(path)?;
    if !metadata.is_file() {
        return Err(invalid("recovery record is not an ordinary file"));
    }
    validate_owner(&metadata)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        if metadata.nlink() != 1 {
            return Err(invalid("recovery records cannot have multiple hard links"));
        }
    }
    Ok(())
}

fn validate_owner(metadata: &fs::Metadata) -> io::Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        // SAFETY: geteuid has no preconditions and only reads process identity.
        if metadata.uid() != unsafe { libc::geteuid() } || metadata.mode() & 0o077 != 0 {
            return Err(invalid(
                "recovery registry requires current-user-only ownership and permissions",
            ));
        }
    }
    #[cfg(not(unix))]
    let _ = metadata;
    Ok(())
}

fn ensure_same_file(path: &Path, file: &File) -> io::Result<()> {
    checked(super::check_path(path))?;
    let opened = same_file::Handle::from_file(file.try_clone()?)?;
    if same_file::Handle::from_path(path)? != opened {
        return Err(invalid("recovery registration identity changed"));
    }
    Ok(())
}

fn no_follow(options: &mut OpenOptions) {
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.custom_flags(libc::O_NOFOLLOW | libc::O_CLOEXEC);
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::FILE_FLAG_OPEN_REPARSE_POINT;
        options.custom_flags(FILE_FLAG_OPEN_REPARSE_POINT);
    }
}

fn checked<T>(result: super::Result<T>) -> io::Result<T> {
    result.map_err(|error| io::Error::new(error.cause.kind(), error))
}

fn lock_error(error: fs::TryLockError) -> io::Error {
    match error {
        fs::TryLockError::WouldBlock => io::Error::new(
            io::ErrorKind::WouldBlock,
            "statement publication is still active; no recovery effects started",
        ),
        fs::TryLockError::Error(error) => error,
    }
}

fn invalid(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

#[cfg(unix)]
fn platform() -> &'static str {
    "unix"
}

#[cfg(windows)]
fn platform() -> &'static str {
    "windows"
}

#[cfg(unix)]
fn encode_path(path: &Path) -> Vec<u8> {
    use std::os::unix::ffi::OsStrExt;
    path.as_os_str().as_bytes().to_vec()
}

#[cfg(windows)]
fn encode_path(path: &Path) -> Vec<u8> {
    use std::os::windows::ffi::OsStrExt;
    path.as_os_str()
        .encode_wide()
        .flat_map(u16::to_le_bytes)
        .collect()
}

#[cfg(unix)]
fn decode_path(bytes: Vec<u8>) -> io::Result<PathBuf> {
    use std::os::unix::ffi::OsStringExt;
    if bytes.contains(&0) {
        return Err(invalid("recovery path contains NUL"));
    }
    Ok(OsString::from_vec(bytes).into())
}

#[cfg(windows)]
fn decode_path(bytes: Vec<u8>) -> io::Result<PathBuf> {
    use std::os::windows::ffi::OsStringExt;
    if !bytes.len().is_multiple_of(2) {
        return Err(invalid("recovery path has invalid UTF-16 storage"));
    }
    let wide = bytes
        .as_chunks::<2>()
        .0
        .iter()
        .copied()
        .map(u16::from_le_bytes)
        .collect::<Vec<_>>();
    if wide.contains(&0) {
        return Err(invalid("recovery path contains NUL"));
    }
    Ok(OsString::from_wide(&wide).into())
}
