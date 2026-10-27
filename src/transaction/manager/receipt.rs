//! Data and directory-metadata receipts share one publication lifetime.

use std::fs::{self, File};
use std::io::{self, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};

use same_file::Handle;

use crate::transaction::filesystem::{FsError, NodeMetadata, Publication, PublishEvent, Recovery};

static NEXT_JOURNAL: AtomicU64 = AtomicU64::new(0);

#[derive(Debug, Default)]
pub(super) struct Batch {
    pub data: Vec<Publication>,
    pub metadata: Vec<DirectoryReceipt>,
    pub committed: bool,
}

impl Batch {
    pub fn journals(&self) -> impl Iterator<Item = &Path> {
        self.data.iter().map(Publication::journal).chain(
            self.metadata
                .iter()
                .map(|receipt| receipt.journal.as_path()),
        )
    }

    pub fn publish(
        &mut self,
        observe: &mut impl FnMut(&Path, PublishEvent) -> io::Result<()>,
        resolve_metadata: impl FnOnce(&mut [DirectoryReceipt]) -> Result<(), FsError>,
    ) -> Result<(), FsError> {
        for receipt in &mut self.data {
            let target = receipt.target().to_owned();
            receipt.publish_with(|event| observe(&target, event))?;
        }
        resolve_metadata(&mut self.metadata)?;
        for receipt in &mut self.metadata {
            receipt.apply(false)?;
            observe(&receipt.path, PublishEvent::AfterInstall).map_err(|cause| {
                failure(
                    "directory metadata publication cancelled",
                    &receipt.path,
                    cause,
                    Some(&receipt.journal),
                )
            })?;
        }
        Ok(())
    }

    pub fn restore(&mut self) -> Vec<FsError> {
        let mut failures = Vec::new();
        for receipt in self.data.iter_mut().rev() {
            if let Err(failure) = receipt.restore_before_image() {
                failures.push(failure);
            }
        }
        if !failures.is_empty() {
            return failures;
        }
        for receipt in self.metadata.iter_mut().rev() {
            if let Err(failure) = receipt.apply(true) {
                failures.push(failure);
            }
        }
        failures
    }

    pub fn cleanup(&mut self) -> Vec<FsError> {
        let mut failures = Vec::new();
        let mut retained = Vec::new();
        for mut receipt in self.data.drain(..) {
            if let Err(failure) = receipt.finalize() {
                failures.push(failure);
                retained.push(receipt);
            }
        }
        self.data = retained;
        if !self.data.is_empty() {
            return failures;
        }
        let mut retained = Vec::new();
        for receipt in self.metadata.drain(..) {
            // Removing sibling data journals changes the containing namespace's
            // mtime. Restore its final version only after those journals close.
            match receipt.finish(!self.committed) {
                Ok(()) => {}
                Err(failure) => {
                    retained.push(receipt);
                    failures.push(failure);
                }
            }
        }
        self.metadata = retained;
        failures
    }

    pub fn is_empty(&self) -> bool {
        self.data.is_empty() && self.metadata.is_empty()
    }
}

#[derive(Debug)]
pub(super) struct DirectoryReceipt {
    path: PathBuf,
    file: File,
    identity: Handle,
    before: NodeMetadata,
    after: NodeMetadata,
    journal: PathBuf,
    journal_identity: Handle,
}

impl DirectoryReceipt {
    pub(super) fn path(&self) -> &Path {
        &self.path
    }

    pub(super) fn bind_modified(&mut self, modified: std::time::SystemTime) -> Result<(), FsError> {
        let actual = Handle::from_path(&self.journal).map_err(|cause| {
            failure(
                "identify metadata receipt",
                &self.path,
                cause,
                Some(&self.journal),
            )
        })?;
        if actual != self.journal_identity {
            return Err(failure(
                "bind metadata receipt",
                &self.path,
                io::Error::other("metadata journal identity changed"),
                Some(&self.journal),
            ));
        }
        let mut after = self.after.clone();
        after.modified = modified;
        let mut record = fs::OpenOptions::new()
            .write(true)
            .truncate(true)
            .open(&self.journal)
            .map_err(|cause| {
                failure(
                    "open metadata receipt",
                    &self.path,
                    cause,
                    Some(&self.journal),
                )
            })?;
        writeln!(
            record,
            "target={:?}\nbefore={:?}\nafter={after:?}",
            self.path, self.before
        )
        .map_err(|cause| {
            failure(
                "bind metadata receipt",
                &self.path,
                cause,
                Some(&self.journal),
            )
        })?;
        self.after = after;
        Ok(())
    }

    pub fn prepare(path: &Path, after: NodeMetadata, store: &Path) -> Result<Self, FsError> {
        let metadata = fs::symlink_metadata(path)
            .map_err(|cause| failure("inspect directory metadata", path, cause, None))?;
        if !metadata.is_dir() || metadata.file_type().is_symlink() {
            return Err(failure(
                "prepare directory metadata",
                path,
                io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "metadata publication requires an existing ordinary directory",
                ),
                None,
            ));
        }
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            if metadata.file_attributes() & 0x400 != 0 {
                return Err(failure(
                    "prepare directory metadata",
                    path,
                    io::Error::new(
                        io::ErrorKind::InvalidInput,
                        "reparse directories cannot receive metadata publication",
                    ),
                    None,
                ));
            }
        }
        let before = NodeMetadata::capture(&metadata)
            .map_err(|cause| failure("capture directory metadata", path, cause, None))?;
        let file = open_directory(path)
            .map_err(|cause| failure("open directory metadata", path, cause, None))?;
        let identity = Handle::from_path(path)
            .map_err(|cause| failure("identify directory metadata", path, cause, None))?;
        let (journal, mut record) = journal(store)
            .map_err(|cause| failure("create metadata journal", path, cause, None))?;
        writeln!(
            record,
            "target={path:?}\nbefore={before:?}\nafter={after:?}"
        )
        .map_err(|cause| failure("record directory before-image", path, cause, Some(&journal)))?;
        let journal_identity = Handle::from_path(&journal)
            .map_err(|cause| failure("identify metadata journal", path, cause, Some(&journal)))?;
        Ok(Self {
            path: path.to_owned(),
            file,
            identity,
            before,
            after,
            journal,
            journal_identity,
        })
    }

    fn apply(&self, restore: bool) -> Result<(), FsError> {
        self.check()?;
        let metadata = if restore { &self.before } else { &self.after };
        self.file
            .set_times(fs::FileTimes::new().set_modified(metadata.modified))
            .and_then(|()| self.file.set_permissions(metadata.permissions.clone()))
            .map_err(|cause| {
                failure(
                    if restore {
                        "restore directory metadata"
                    } else {
                        "publish directory metadata"
                    },
                    &self.path,
                    cause,
                    Some(&self.journal),
                )
            })
    }

    fn check(&self) -> Result<(), FsError> {
        let actual = Handle::from_path(&self.path).map_err(|cause| {
            failure(
                "identify directory metadata target",
                &self.path,
                cause,
                Some(&self.journal),
            )
        })?;
        if actual != self.identity {
            return Err(failure(
                "validate directory metadata ownership",
                &self.path,
                io::Error::new(
                    io::ErrorKind::AlreadyExists,
                    "directory identity changed; another owner's metadata is never overwritten",
                ),
                Some(&self.journal),
            ));
        }
        Ok(())
    }

    fn finish(&self, restore: bool) -> Result<(), FsError> {
        self.apply(restore).and_then(|()| {
            let actual = Handle::from_path(&self.journal).map_err(|cause| {
                failure(
                    "identify retained metadata journal",
                    &self.path,
                    cause,
                    Some(&self.journal),
                )
            })?;
            if actual != self.journal_identity {
                return Err(failure(
                    "validate metadata journal ownership",
                    &self.path,
                    io::Error::new(
                        io::ErrorKind::AlreadyExists,
                        "metadata journal identity changed",
                    ),
                    Some(&self.journal),
                ));
            }
            fs::remove_file(&self.journal).map_err(|cause| {
                failure(
                    "remove metadata journal",
                    &self.path,
                    cause,
                    Some(&self.journal),
                )
            })
        })
    }
}

fn open_directory(path: &Path) -> io::Result<File> {
    let mut options = fs::OpenOptions::new();
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_READ_ATTRIBUTES,
            FILE_WRITE_ATTRIBUTES,
        };
        options
            .access_mode(FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT);
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options
            .read(true)
            .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC);
    }
    options.open(path)
}

fn journal(store: &Path) -> io::Result<(PathBuf, File)> {
    for _ in 0..128 {
        let path = store.join(format!(
            "metadata-{}-{}",
            std::process::id(),
            NEXT_JOURNAL.fetch_add(1, Ordering::Relaxed)
        ));
        match fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)
        {
            Ok(file) => return Ok((path, file)),
            Err(cause) if cause.kind() == io::ErrorKind::AlreadyExists => {}
            Err(cause) => return Err(cause),
        }
    }
    Err(io::Error::new(
        io::ErrorKind::AlreadyExists,
        "metadata journal names exhausted",
    ))
}

pub(super) fn failure(
    action: &'static str,
    path: &Path,
    cause: io::Error,
    journal: Option<&Path>,
) -> FsError {
    FsError {
        action,
        path: path.to_owned(),
        cause,
        journal: journal.map(Path::to_owned),
        recovery: if journal.is_some() {
            Recovery::JournalRetained
        } else {
            Recovery::Unchanged
        },
        recovery_error: None,
        help: None,
    }
}
