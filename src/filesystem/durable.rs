//! Recoverable statement publication. No transaction histories or undo API.
//!
//! A synced intent precedes every destructive rename. Until a synced decision
//! exists, recovery restores all before-images; afterwards it only releases
//! journals. A batch is one sibling set, not an OS-wide multi-path transaction.

use std::collections::BTreeSet;
use std::ffi::{OsStr, OsString};
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Component, Path, PathBuf};

use serde::{Deserialize, Serialize};

use super::{FsError, Publication, PublishEvent, Recovery, Result};

const PREFIX: &str = ".msync-statement-";
const MANIFEST: &str = "intent.json";
const DECISION: &str = "committed";
const COMMITTED: &[u8] = b"MSYNC-STATEMENT-COMMITTED-1\n";
const MAX_MANIFEST: u64 = 16 * 1024 * 1024;

/// Observers run after the preceding rename has been made recoverable.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Event {
    Prepared,
    AfterBackup(usize),
    AfterInstall(usize),
    BeforeCommit,
    Committed,
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(deny_unknown_fields)]
struct Identity {
    volume: u64,
    index: u64,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Member {
    target: Vec<u8>,
    journal: Vec<u8>,
    workspace: Identity,
    before: Option<Identity>,
    before_mode: Option<u32>,
    after: Option<Identity>,
}

#[derive(Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Intent {
    version: u32,
    platform: String,
    parent: Identity,
    members: Vec<Member>,
}

/// Owns a parent writer lock and every publication receipt until finalization.
/// Drop deliberately leaves recoverable state on disk, never silently rolls back.
pub struct Batch {
    parent: PathBuf,
    journal: PathBuf,
    intent: Intent,
    publications: Vec<Publication>,
    _lock: ParentLock,
    committed: bool,
    finished: bool,
}

impl Batch {
    /// Persist a complete sibling-set intent without changing destination roots.
    pub fn prepare(publications: Vec<Publication>) -> Result<Self> {
        Self::prepare_inner(publications, None).map(|(batch, _)| batch)
    }

    /// Register the empty coordinator before writing its first payload byte.
    /// The caller must durably register the full coordinator before retiring
    /// this stage receipt and the member-stage receipts or admitting publication.
    pub fn prepare_registered(
        publications: Vec<Publication>,
        lease: &super::recovery::RegistryLease,
    ) -> Result<(Self, super::recovery::Registration)> {
        let (batch, registration) = Self::prepare_inner(publications, Some(lease))?;
        Ok((
            batch,
            registration.expect("registered coordinator has stage receipt"),
        ))
    }

    fn prepare_inner(
        publications: Vec<Publication>,
        lease: Option<&super::recovery::RegistryLease>,
    ) -> Result<(Self, Option<super::recovery::Registration>)> {
        let first = publications.first().ok_or_else(|| {
            failure(
                "prepare durable statement",
                Path::new(""),
                io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "a durable batch requires at least one publication",
                ),
            )
        })?;
        let parent = first
            .target
            .parent()
            .expect("publication target has parent")
            .to_owned();
        for publication in &publications {
            if publication.target.parent() != Some(parent.as_path())
                || publication.workspace.parent() != Some(parent.as_path())
            {
                return Err(invalid(
                    &parent,
                    "a statement publication must have one sibling parent",
                ));
            }
            if publication.phase != super::Phase::Prepared {
                return Err(invalid(
                    &parent,
                    "only prepared publications can enter a durable statement",
                ));
            }
        }
        let lock = lock_parent(&parent)?;
        recover_locked(&parent)?;
        let mut members = Vec::with_capacity(publications.len());
        let mut targets = BTreeSet::new();
        for publication in &publications {
            let target = encoded(publication.target.file_name().expect("target basename"));
            if !targets.insert(target.clone()) {
                return Err(invalid(
                    &parent,
                    "a statement contains duplicate publication targets",
                ));
            }
            super::ensure_identity(&publication.target, publication.original.as_ref())?;
            sync_tree(&publication.new_path())?;
            sync_directory(&publication.workspace)?;
            members.push(Member {
                target,
                journal: encoded(publication.workspace.file_name().expect("journal basename")),
                workspace: identity(&publication.workspace)?.expect("prepared workspace exists"),
                before: identity(&publication.target)?,
                before_mode: directory_mode(&publication.target)?,
                after: identity(&publication.new_path())?,
            });
        }
        let intent = Intent {
            version: 1,
            platform: platform().into(),
            parent: identity(&parent)?.expect("parent exists"),
            members,
        };
        let journal = super::create_owned_directory(&parent, PREFIX.trim_end_matches('-'))?;
        let registration = lease
            .map(|lease| {
                lease.register_stage(&parent, &journal).map_err(|cause| {
                    retained(
                        failure("register private statement coordinator", &journal, cause),
                        &journal,
                    )
                })
            })
            .transpose()?;
        let payload = serde_json::to_vec(&intent).map_err(|cause| {
            failure(
                "encode durable statement",
                &journal,
                io::Error::other(cause),
            )
        })?;
        write_new(&journal.join("intent.pending"), &payload)?;
        super::move_new(&journal.join("intent.pending"), &journal.join(MANIFEST))
            .map_err(|cause| failure("install durable statement intent", &journal, cause))?;
        sync_directory(&journal)?;
        sync_directory(&parent)?;
        let batch = Self {
            parent,
            journal,
            intent,
            publications,
            _lock: lock,
            committed: false,
            finished: false,
        };
        Ok((batch, registration))
    }

    pub fn parent(&self) -> &Path {
        &self.parent
    }

    pub fn journal_path(&self) -> &Path {
        &self.journal
    }

    pub fn is_committed(&self) -> bool {
        self.committed
    }

    pub fn publish(&mut self) -> Result<()> {
        self.publish_with(|_| Ok(()))
    }

    /// A rejecting observer before `Committed` leaves a recoverable before-image.
    /// A rejecting `Committed` observer cannot revoke the durable decision.
    pub fn publish_with(&mut self, observe: impl FnMut(Event) -> io::Result<()>) -> Result<()> {
        self.publish_inner(observe)
            .map_err(|error| retained(error, &self.journal))
    }

    fn publish_inner(&mut self, mut observe: impl FnMut(Event) -> io::Result<()>) -> Result<()> {
        if self.finished || self.committed {
            return Err(invalid(
                &self.journal,
                "a durable statement can only publish once",
            ));
        }
        self.event(Event::Prepared, &mut observe)?;
        for index in 0..self.publications.len() {
            let parent = &self.parent;
            let workspace = self.publications[index].workspace.clone();
            let target = self.publications[index].target.clone();
            publish_member(&mut self.publications[index], |event| {
                sync_directory(&workspace).map_err(|error| error.cause)?;
                sync_directory(parent).map_err(|error| error.cause)?;
                let event = match event {
                    PublishEvent::AfterBackup => Event::AfterBackup(index),
                    PublishEvent::AfterInstall => Event::AfterInstall(index),
                };
                crash_hook(event, &target);
                observe(event)
            })?;
        }
        self.event(Event::BeforeCommit, &mut observe)?;
        write_new(&self.journal.join("commit.pending"), COMMITTED)?;
        super::move_new(
            &self.journal.join("commit.pending"),
            &self.journal.join(DECISION),
        )
        .map_err(|cause| failure("install statement commit decision", &self.journal, cause))?;
        // Namespace publication is irreversible even if the following directory
        // sync reports an uncertain persistence outcome. Retain the journals;
        // never restore old roots underneath an already visible decision.
        self.committed = true;
        sync_directory(&self.journal)?;
        self.event(Event::Committed, &mut observe)
    }

    /// Restore the complete old set. This is internal failure compensation, not
    /// a language-visible rollback operation or retained statement history.
    pub fn abort(&mut self) -> Result<()> {
        if self.finished {
            return Ok(());
        }
        if self.committed {
            return Err(invalid(
                &self.journal,
                "a committed statement cannot be aborted",
            ));
        }
        recover_intent(&self.parent, &self.journal, &self.intent, false)
            .map_err(|error| retained(error, &self.journal))?;
        self.finished = true;
        Ok(())
    }

    /// Release obsolete before-images only after the durable decision.
    pub fn finalize(&mut self) -> Result<()> {
        if self.finished {
            return Ok(());
        }
        if !self.committed {
            return Err(invalid(
                &self.journal,
                "publish or abort a statement before finalizing",
            ));
        }
        recover_intent(&self.parent, &self.journal, &self.intent, true)
            .map_err(|error| retained(error, &self.journal))?;
        self.finished = true;
        Ok(())
    }

    fn event(&self, event: Event, observe: &mut impl FnMut(Event) -> io::Result<()>) -> Result<()> {
        for publication in &self.publications {
            crash_hook(event, &publication.target);
        }
        observe(event).map_err(|cause| failure("observe durable statement", &self.journal, cause))
    }
}

/// Recover only statement coordinators directly below this known destination
/// parent. No filesystem traversal, process launch, or manager initialization.
pub fn recover_parent(parent: &Path) -> Result<usize> {
    let parent = super::absolute(parent)?;
    let _lock = lock_parent(&parent)?;
    recover_locked(&parent)
}

/// Recover one registered coordinator without admitting arbitrary child paths.
pub fn recover_journal(parent: &Path, name: &OsStr) -> Result<usize> {
    let parent = super::absolute(parent)?;
    let _lock = lock_parent(&parent)?;
    let name = decoded_name(&encoded(name))?;
    if !name.to_str().is_some_and(|value| value.starts_with(PREFIX)) {
        return Err(invalid(
            &parent,
            "registered recovery name is not a statement coordinator",
        ));
    }
    recover_one(&parent, &parent.join(name))
}

pub(super) fn recover_locked(parent: &Path) -> Result<usize> {
    let mut journals = Vec::new();
    for entry in fs::read_dir(parent)
        .map_err(|cause| failure("discover statement recovery", parent, cause))?
    {
        let entry = entry.map_err(|cause| failure("discover statement recovery", parent, cause))?;
        if entry
            .file_name()
            .to_str()
            .is_some_and(|name| name.starts_with(PREFIX))
        {
            journals.push(entry.path());
        }
    }
    journals.sort();
    let mut recovered = 0;
    for journal in journals {
        recovered += recover_one(parent, &journal)?;
    }
    Ok(recovered)
}

fn recover_one(parent: &Path, journal: &Path) -> Result<usize> {
    let Some(metadata) = super::safe_metadata(journal)? else {
        return Ok(0);
    };
    if !metadata.is_dir() {
        return Err(invalid(journal, "statement coordinator is not a directory"));
    }
    let intent_path = journal.join(MANIFEST);
    if !intent_path
        .try_exists()
        .map_err(|cause| failure("inspect statement intent", &intent_path, cause))?
    {
        // No intent is either pre-admission or post-finalization. Neither case
        // authorizes touching a destination. Leave unidentified orphan stages.
        return Ok(0);
    }
    let intent = read_intent(&intent_path)?;
    validate_intent(parent, &intent)?;
    recover_intent(parent, journal, &intent, decision(journal)?)
        .map_err(|error| retained(error, journal))?;
    Ok(1)
}

fn recover_intent(parent: &Path, journal: &Path, intent: &Intent, committed: bool) -> Result<()> {
    validate_intent(parent, intent)?;
    // Validate the entire recovery set before any rename, including a changed
    // target in a later member. Foreign replacements never become cleanup input.
    for member in &intent.members {
        validate_member(parent, member, committed)?;
    }
    if !committed {
        for member in intent.members.iter().rev() {
            restore_member(parent, member)?;
        }
        sync_directory(parent)?;
    }
    for member in &intent.members {
        let workspace = parent.join(decoded_name(&member.journal)?);
        if identity(&workspace)?.is_some() {
            let handle = super::identity_at(&workspace)?.expect("validated workspace exists");
            super::remove_owned(&workspace, &handle)?;
        }
    }
    // Persist the removal of before-images before removing the commit decision.
    sync_directory(parent)?;
    // Intent removal is the cleanup boundary. A crash after this point must not
    // reinterpret a partly deleted commit marker as an uncommitted statement.
    fs::remove_file(journal.join(MANIFEST))
        .map_err(|cause| failure("retire statement recovery intent", journal, cause))?;
    sync_directory(journal)?;
    let handle = super::identity_at(journal)?
        .ok_or_else(|| invalid(journal, "statement coordinator disappeared"))?;
    super::remove_owned(journal, &handle)?;
    sync_directory(parent)
}

fn restore_member(parent: &Path, member: &Member) -> Result<()> {
    let target = parent.join(decoded_name(&member.target)?);
    let workspace = parent.join(decoded_name(&member.journal)?);
    let old = workspace.join("old");
    let new = workspace.join("new");
    if identity(&target)? == member.before {
        return restore_directory_mode(&target, member.before_mode);
    }
    if identity(&old)?.is_some() {
        if identity(&target)?.is_some() {
            super::move_new(&target, &new)
                .map_err(|cause| failure("retain interrupted replacement", &target, cause))?;
            sync_directory(&workspace)?;
            sync_directory(parent)?;
        }
        super::move_new(&old, &target)
            .map_err(|cause| failure("restore statement before-image", &target, cause))?;
        sync_directory(&workspace)?;
        sync_directory(parent)?;
    } else if member.before.is_none() && identity(&target)?.is_some() {
        super::move_new(&target, &new)
            .map_err(|cause| failure("restore absent statement target", &target, cause))?;
        sync_directory(&workspace)?;
        sync_directory(parent)?;
    }
    if identity(&target)? != member.before {
        return Err(invalid(
            &target,
            "statement before-image could not be proven restored",
        ));
    }
    restore_directory_mode(&target, member.before_mode)
}

fn validate_member(parent: &Path, member: &Member, committed: bool) -> Result<()> {
    let target = parent.join(decoded_name(&member.target)?);
    let workspace = parent.join(decoded_name(&member.journal)?);
    let target_identity = identity(&target)?;
    match identity(&workspace)? {
        None if committed && target_identity == member.after => return Ok(()),
        None if !committed && target_identity == member.before => return Ok(()),
        Some(actual) if actual == member.workspace => {}
        _ => {
            return Err(invalid(
                &workspace,
                "statement workspace identity changed; recovery retained",
            ));
        }
    }
    let old = identity(&workspace.join("old"))?;
    let new = identity(&workspace.join("new"))?;
    if old.is_some() && old != member.before || new.is_some() && new != member.after {
        return Err(invalid(
            &workspace,
            "statement journal member identity changed; recovery retained",
        ));
    }
    if committed {
        if target_identity != member.after {
            return Err(invalid(
                &target,
                "committed statement target identity changed; recovery retained",
            ));
        }
    } else if target_identity.is_some()
        && target_identity != member.before
        && target_identity != member.after
    {
        return Err(invalid(
            &target,
            "external replacement blocks statement recovery",
        ));
    }
    Ok(())
}

fn validate_intent(parent: &Path, intent: &Intent) -> Result<()> {
    if intent.version != 1
        || intent.platform != platform()
        || identity(parent)? != Some(intent.parent)
        || intent.members.is_empty()
    {
        return Err(invalid(
            parent,
            "invalid or foreign statement recovery intent",
        ));
    }
    let mut targets = BTreeSet::new();
    let mut journals = BTreeSet::new();
    for member in &intent.members {
        if member.before_mode.is_some_and(|mode| mode & !0o7777 != 0) {
            return Err(invalid(
                parent,
                "statement before-image permissions are invalid",
            ));
        }
        let target = decoded_name(&member.target)?;
        let workspace = decoded_name(&member.journal)?;
        if target.to_str().is_some_and(|name| {
            name.starts_with(PREFIX)
                || name.starts_with(".msync-transaction-")
                || name.starts_with(".msync-publication-")
        }) || !workspace.to_str().is_some_and(|name| {
            name.starts_with(".msync-transaction-") || name.starts_with(".msync-publication-")
        }) || !targets.insert(target)
            || !journals.insert(workspace)
        {
            return Err(invalid(
                parent,
                "invalid or duplicate statement recovery member",
            ));
        }
    }
    Ok(())
}

fn read_intent(path: &Path) -> Result<Intent> {
    let metadata =
        super::safe_metadata(path)?.ok_or_else(|| invalid(path, "statement intent disappeared"))?;
    if !metadata.is_file() || metadata.len() > MAX_MANIFEST {
        return Err(invalid(
            path,
            "statement recovery intent is not a bounded ordinary file",
        ));
    }
    let mut bytes = Vec::new();
    File::open(path)
        .and_then(|file| file.take(MAX_MANIFEST + 1).read_to_end(&mut bytes))
        .map_err(|cause| failure("read statement recovery intent", path, cause))?;
    serde_json::from_slice(&bytes).map_err(|cause| {
        failure(
            "decode statement recovery intent",
            path,
            io::Error::new(io::ErrorKind::InvalidData, cause),
        )
    })
}

fn decision(journal: &Path) -> Result<bool> {
    let path = journal.join(DECISION);
    let Some(metadata) = super::safe_metadata(&path)? else {
        return Ok(false);
    };
    if !metadata.is_file() || metadata.len() != COMMITTED.len() as u64 {
        return Ok(false);
    }
    fs::read(&path)
        .map(|bytes| bytes == COMMITTED)
        .map_err(|cause| failure("read statement commit decision", &path, cause))
}

#[cfg(unix)]
pub(super) struct ParentLock {
    _file: File,
}

#[cfg(windows)]
pub(super) struct ParentLock {
    handle: windows_sys::Win32::Foundation::HANDLE,
    // Kernel mutex ownership is thread-affine; the batch must not cross threads.
    _owner: std::marker::PhantomData<std::rc::Rc<()>>,
}

#[cfg(windows)]
impl Drop for ParentLock {
    fn drop(&mut self) {
        // SAFETY: This guard owns a successfully acquired mutex, never crosses
        // threads, and closes its one owned handle after releasing ownership.
        unsafe {
            windows_sys::Win32::System::Threading::ReleaseMutex(self.handle);
            windows_sys::Win32::Foundation::CloseHandle(self.handle);
        }
    }
}

pub(super) fn lock_parent(parent: &Path) -> Result<ParentLock> {
    super::check_path(parent)?;
    #[cfg(unix)]
    {
        use std::os::unix::fs::{MetadataExt, OpenOptionsExt};
        let file = OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC)
            .open(parent)
            .map_err(|cause| failure("open statement publication parent", parent, cause))?;
        file.lock()
            .map_err(|cause| failure("lock statement publication parent", parent, cause))?;
        let metadata = file
            .metadata()
            .map_err(|cause| failure("identify locked statement parent", parent, cause))?;
        if identity(parent)?
            != Some(Identity {
                volume: metadata.dev(),
                index: metadata.ino(),
            })
        {
            return Err(invalid(
                parent,
                "statement parent changed while acquiring its lock",
            ));
        }
        Ok(ParentLock { _file: file })
    }
    #[cfg(windows)]
    {
        use windows_sys::Win32::Foundation::{CloseHandle, WAIT_ABANDONED, WAIT_OBJECT_0};
        use windows_sys::Win32::System::Threading::{CreateMutexW, INFINITE, WaitForSingleObject};
        let (volume, index) = fingerprint(parent)?;
        let name: Vec<u16> = format!("Global\\msync-publication-v1-{volume:016x}-{index:016x}")
            .encode_utf16()
            .chain(Some(0))
            .collect();
        // SAFETY: The NUL-terminated name lives through the call; null security
        // attributes select the caller's default access descriptor.
        let handle = unsafe { CreateMutexW(std::ptr::null(), 0, name.as_ptr()) };
        if handle.is_null() {
            return Err(failure(
                "open statement publication mutex",
                parent,
                io::Error::last_os_error(),
            ));
        }
        // SAFETY: This is a live owned kernel handle. Waiting happens only in
        // the blocking statement worker, not a Tokio executor thread.
        let wait = unsafe { WaitForSingleObject(handle, INFINITE) };
        if !matches!(wait, WAIT_OBJECT_0 | WAIT_ABANDONED) {
            let cause = io::Error::last_os_error();
            unsafe {
                CloseHandle(handle);
            }
            return Err(failure("lock statement publication parent", parent, cause));
        }
        let guard = ParentLock {
            handle,
            _owner: std::marker::PhantomData,
        };
        if fingerprint(parent)? != (volume, index) {
            return Err(invalid(
                parent,
                "statement parent changed while acquiring its mutex",
            ));
        }
        Ok(guard)
    }
}

fn write_new(path: &Path, bytes: &[u8]) -> Result<()> {
    let mut file = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .map_err(|cause| failure("create durable statement record", path, cause))?;
    file.write_all(bytes)
        .and_then(|()| file.sync_all())
        .map_err(|cause| failure("sync durable statement record", path, cause))
}

fn sync_tree(path: &Path) -> Result<()> {
    let Some(metadata) = super::safe_metadata(path)? else {
        return Ok(());
    };
    if metadata.is_dir() {
        for child in super::children(path)? {
            sync_tree(&child.path())?;
        }
        return sync_directory(path);
    }
    super::require_file(&metadata, path)?;
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        options.write(true);
        // This tree is private. Keep the original attributes, but acquire the
        // write handle FlushFileBuffers requires before restoring read-only.
        let original = metadata.permissions();
        if original.readonly() {
            let mut writable = original.clone();
            // Windows clears only the read-only attribute on owned staging,
            // unlike Unix permission-bit broadening.
            #[allow(clippy::permissions_set_readonly_false)]
            writable.set_readonly(false);
            fs::set_permissions(path, writable)
                .map_err(|cause| failure("prepare private file sync", path, cause))?;
        }
        let file = options.open(path);
        let restored = fs::set_permissions(path, original);
        restored.map_err(|cause| failure("restore private file sync attributes", path, cause))?;
        file.and_then(|file| file.sync_all())
            .map_err(|cause| failure("sync statement file", path, cause))
    }
    #[cfg(unix)]
    options
        .open(path)
        .and_then(|file| file.sync_all())
        .map_err(|cause| failure("sync statement file", path, cause))
}

fn directory_mode(path: &Path) -> Result<Option<u32>> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        Ok(super::safe_metadata(path)?
            .filter(fs::Metadata::is_dir)
            .map(|metadata| metadata.permissions().mode() & 0o7777))
    }
    #[cfg(windows)]
    {
        let _ = path;
        Ok(None)
    }
}

fn restore_directory_mode(path: &Path, mode: Option<u32>) -> Result<()> {
    #[cfg(unix)]
    if let Some(mode) = mode {
        use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
        let metadata = super::safe_metadata(path)?
            .filter(fs::Metadata::is_dir)
            .ok_or_else(|| invalid(path, "statement directory before-image disappeared"))?;
        // Renaming a read-only directory may temporarily require owner-write
        // access to update '..'. Persist its original mode before admission so
        // recovery also repairs a crash between chmod and its restoration.
        let directory = OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC)
            .open(path)
            .map_err(|cause| failure("open statement directory before-image", path, cause))?;
        if metadata.permissions().mode() & 0o7777 != mode {
            directory
                .set_permissions(fs::Permissions::from_mode(mode))
                .map_err(|cause| failure("restore statement directory permissions", path, cause))?;
        }
        // Even an already-correct mode may be an earlier recovery's unsynced
        // chmod. Persist it before retiring the recovery intent.
        directory
            .sync_all()
            .map_err(|cause| failure("sync statement directory permissions", path, cause))?;
    }
    #[cfg(windows)]
    let _ = (path, mode);
    Ok(())
}

fn publish_member(
    publication: &mut Publication,
    mut observe: impl FnMut(PublishEvent) -> io::Result<()>,
) -> Result<()> {
    let replace_file = !publication.absent
        && publication.original.is_some()
        && fs::metadata(&publication.target).is_ok_and(|meta| meta.is_file())
        && fs::metadata(publication.new_path()).is_ok_and(|meta| meta.is_file());
    if !replace_file {
        return publication.publish_with(observe);
    }
    publication.require_phase(super::Phase::Prepared)?;
    publication.check_workspace()?;
    super::ensure_identity(&publication.target, publication.original.as_ref())?;
    // An extra link retains the before-image without removing the public name.
    // The following replacement rename then has no absent-target window.
    if fs::hard_link(&publication.target, publication.old_path()).is_err() {
        return publication.publish_with(observe);
    }
    sync_directory(&publication.workspace)?;
    publication.phase = super::Phase::BackedUp;
    observe(PublishEvent::AfterBackup)
        .map_err(|cause| failure("observe statement before-image", &publication.target, cause))?;
    crate::io::publish_file(&publication.new_path(), &publication.target, false).map_err(
        |cause| {
            failure(
                "atomically replace statement file",
                &publication.target,
                cause,
            )
        },
    )?;
    publication.phase = super::Phase::Installed;
    observe(PublishEvent::AfterInstall)
        .map_err(|cause| failure("observe statement replacement", &publication.target, cause))?;
    publication.installed = super::identity_at(&publication.target)?;
    publication.phase = super::Phase::Published;
    Ok(())
}

pub(super) fn sync_directory(path: &Path) -> Result<()> {
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        OpenOptions::new()
            .read(true)
            .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC)
            .open(path)
            .and_then(|file| file.sync_all())
            .map_err(|cause| failure("sync statement directory", path, cause))
    }
    #[cfg(windows)]
    {
        // Win32 does not provide a portable directory fsync. Synchronous rename
        // plus synced intent/data protects process crashes; a hard-power-loss
        // guarantee additionally depends on NTFS/storage persistence semantics.
        let _ = path;
        Ok(())
    }
}

fn identity(path: &Path) -> Result<Option<Identity>> {
    let Some(metadata) = super::safe_metadata(path)? else {
        return Ok(None);
    };
    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        Ok(Some(Identity {
            volume: metadata.dev(),
            index: metadata.ino(),
        }))
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use std::os::windows::io::AsRawHandle;
        use windows_sys::Win32::Storage::FileSystem::{
            BY_HANDLE_FILE_INFORMATION, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT,
            GetFileInformationByHandle,
        };
        let _ = metadata;
        let file = OpenOptions::new()
            .access_mode(0)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
            .open(path)
            .map_err(|cause| failure("identify durable statement member", path, cause))?;
        let mut info = std::mem::MaybeUninit::<BY_HANDLE_FILE_INFORMATION>::uninit();
        // SAFETY: The live file owns the handle and Win32 initializes the full
        // fixed-size output structure on success.
        if unsafe { GetFileInformationByHandle(file.as_raw_handle(), info.as_mut_ptr()) } == 0 {
            return Err(failure(
                "identify durable statement member",
                path,
                io::Error::last_os_error(),
            ));
        }
        let info = unsafe { info.assume_init() };
        Ok(Some(Identity {
            volume: u64::from(info.dwVolumeSerialNumber),
            index: (u64::from(info.nFileIndexHigh) << 32) | u64::from(info.nFileIndexLow),
        }))
    }
}

pub(super) fn fingerprint(path: &Path) -> Result<(u64, u64)> {
    let value =
        identity(path)?.ok_or_else(|| invalid(path, "statement recovery parent disappeared"))?;
    Ok((value.volume, value.index))
}

fn encoded(name: &OsStr) -> Vec<u8> {
    #[cfg(unix)]
    {
        use std::os::unix::ffi::OsStrExt;
        name.as_bytes().to_vec()
    }
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        name.encode_wide().flat_map(u16::to_le_bytes).collect()
    }
}

fn decoded_name(bytes: &[u8]) -> Result<OsString> {
    #[cfg(unix)]
    let name = {
        use std::os::unix::ffi::OsStringExt;
        OsString::from_vec(bytes.to_vec())
    };
    #[cfg(windows)]
    let name = {
        use std::os::windows::ffi::OsStringExt;
        if !bytes.len().is_multiple_of(2) {
            return Err(invalid(Path::new(""), "invalid Windows recovery basename"));
        }
        OsString::from_wide(
            &bytes
                .as_chunks::<2>()
                .0
                .iter()
                .copied()
                .map(u16::from_le_bytes)
                .collect::<Vec<_>>(),
        )
    };
    let mut components = Path::new(&name).components();
    if !matches!(components.next(), Some(Component::Normal(_)))
        || components.next().is_some()
        || bytes.contains(&0) && cfg!(unix)
    {
        return Err(invalid(
            Path::new(""),
            "recovery names must be individual non-NUL basenames",
        ));
    }
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        if name
            .encode_wide()
            .any(|value| value == 0 || value == u16::from(b':'))
        {
            return Err(invalid(Path::new(""), "invalid Windows recovery basename"));
        }
    }
    Ok(name)
}

const fn platform() -> &'static str {
    if cfg!(windows) {
        "windows-utf16le"
    } else {
        "unix-bytes"
    }
}

fn crash_hook(event: Event, target: &Path) {
    let Some(expected) = std::env::var_os("MSYNC_TEST_CRASH_TARGET") else {
        return;
    };
    if Path::new(&expected) != target {
        return;
    }
    let Some(cut) = std::env::var_os("MSYNC_TEST_CRASH_AT") else {
        return;
    };
    let matches = match event {
        Event::Prepared => cut == "prepared",
        Event::AfterBackup(_) => cut == "after-backup",
        Event::AfterInstall(_) => cut == "after-install",
        Event::BeforeCommit => cut == "before-commit",
        Event::Committed => cut == "before-cleanup",
    };
    if matches {
        std::process::exit(86);
    }
}

fn invalid(path: &Path, message: &'static str) -> FsError {
    failure(
        "validate durable statement",
        path,
        io::Error::new(io::ErrorKind::InvalidData, message),
    )
}

fn failure(action: &'static str, path: &Path, cause: io::Error) -> FsError {
    let mut error = super::error(action, path, cause);
    error.recovery = Recovery::JournalRetained;
    error
}

fn retained(mut error: FsError, journal: &Path) -> FsError {
    error.journal = Some(journal.to_owned());
    error.recovery = Recovery::JournalRetained;
    error
}
