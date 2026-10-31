//! Finite remote statement ownership and restart discovery.

use std::ffi::OsString;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use serde::{Deserialize, Serialize};

use crate::endpoint::{Backend, Location};
use crate::filesystem::{Entry, InvocationRoot, Snapshot, SnapshotStore};
use crate::hir::{Input, Instruction, InstructionKind};
use crate::plan::FileClass;
use crate::source::InstructionOrigin;
use crate::syntax::Span;

use super::super::error::{ExecutionError, Result};
use super::super::{CleanupIssue, ExecutionReport, Options};

const REGISTRY: &str = ".msync-remote-recovery-v1";
const OWNER: &str = ".owner";
const OWNER_MAGIC: &[u8] = b"MSYNC-REMOTE-REGISTRY-1\n";
static NEXT: AtomicU64 = AtomicU64::new(0);

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Record {
    version: u32,
    backend: Backend,
    journal: String,
    target: String,
}

pub(super) struct Pending {
    path: PathBuf,
    lock: File,
}

impl Pending {
    pub(super) fn path(&self) -> &Path {
        &self.path
    }

    pub(super) fn create(
        scratch: &Path,
        backend: &Backend,
        journal: &str,
        target: &str,
        span: Span,
    ) -> Result<Self> {
        let directory = ensure_registry(scratch)
            .map_err(|cause| failure("create remote recovery registry", span, cause))?;
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        let name = format!(
            "{}-{nonce}-{}.json",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        );
        let path = directory.join(name);
        let temporary = path.with_extension("pending");
        let mut lock = OpenOptions::new()
            .read(true)
            .write(true)
            .create_new(true)
            .open(&temporary)
            .map_err(|cause| failure("create remote recovery record", span, cause))?;
        lock.try_lock().map_err(|cause| {
            failure("lock remote recovery record", span, io::Error::other(cause))
        })?;
        let record = Record {
            version: 1,
            backend: backend.clone(),
            journal: journal.into(),
            target: target.into(),
        };
        let bytes = serde_json::to_vec(&record)
            .map_err(|cause| failure("encode remote recovery record", span, cause))?;
        lock.write_all(&bytes)
            .and_then(|()| lock.sync_all())
            .map_err(|cause| failure("persist remote recovery record", span, cause))?;
        fs::rename(&temporary, &path)
            .map_err(|cause| failure("publish remote recovery record", span, cause))?;
        sync_directory(&directory)
            .map_err(|cause| failure("persist remote recovery registry", span, cause))?;
        Ok(Self { path, lock })
    }

    pub(super) fn finish(self, span: Span) -> Result<()> {
        let Self { path, lock } = self;
        fs::remove_file(&path)
            .map_err(|cause| failure("remove settled remote recovery record", span, cause))?;
        let result = sync_directory(path.parent().expect("registry parent"))
            .map_err(|cause| failure("persist remote recovery cleanup", span, cause));
        drop(lock);
        result
    }
}

pub(crate) fn may_need_recovery(scratch: &Path) -> bool {
    crate::filesystem::recovery::recovery_path_present(&scratch.join(REGISTRY))
}

/// Only pending records cause transport activity. A normal local invocation has
/// no remote dependency, and an active writer's locked record is not abandoned.
pub(crate) async fn recover(options: &Options) -> Result<()> {
    let Some(directory) = owned_registry(&options.scratch_root)
        .map_err(|cause| failure("verify remote recovery registry", Span::default(), cause))?
    else {
        return Ok(());
    };
    let entries = fs::read_dir(&directory)
        .map_err(|cause| failure("open remote recovery registry", Span::default(), cause))?;
    let mut paths = Vec::new();
    for entry in entries {
        let entry = entry
            .map_err(|cause| failure("read remote recovery registry", Span::default(), cause))?;
        if entry
            .path()
            .extension()
            .is_some_and(|extension| extension == "json")
        {
            paths.push(entry.path());
        }
    }
    paths.sort();
    for path in paths {
        let mut file = match OpenOptions::new().read(true).write(true).open(&path) {
            Ok(file) => file,
            Err(cause) if cause.kind() == io::ErrorKind::NotFound => continue,
            Err(cause) => {
                return Err(failure(
                    "open pending remote statement",
                    Span::default(),
                    cause,
                ));
            }
        };
        match file.try_lock() {
            Ok(()) => {}
            Err(std::fs::TryLockError::WouldBlock) => continue,
            Err(cause) => {
                return Err(failure(
                    "lock pending remote statement",
                    Span::default(),
                    io::Error::other(cause),
                ));
            }
        }
        let mut bytes = Vec::new();
        (&mut file)
            .take(1024 * 1024 + 1)
            .read_to_end(&mut bytes)
            .map_err(|cause| failure("read pending remote statement", Span::default(), cause))?;
        let record: Record = serde_json::from_slice(&bytes)
            .map_err(|cause| failure("decode pending remote statement", Span::default(), cause))?;
        if record.version != 1
            || !matches!(record.backend, Backend::Wsl | Backend::Ssh { .. })
            || !record.journal.starts_with('/')
            || !record.journal.contains("/.msync-v4.")
            || record.journal.contains(['\n', '\r', '\0'])
            || !record.target.starts_with('/')
        {
            return Err(ExecutionError::new(
                "invalid pending remote statement record; no effects started",
                Span::default(),
            ));
        }
        let recovery = tokio_util::sync::CancellationToken::new();
        let result = super::command(
            &record.backend,
            super::RECOVER,
            &[&record.journal],
            &recovery,
            Span::default(),
        )
        .await;
        match result {
            Ok(output)
                if [b"COMMITTED\n".as_slice(), b"ABORTED\n", b"UNCHANGED\n"]
                    .iter()
                    .any(|prefix| output.stdout.starts_with(prefix)) =>
            {
                fs::remove_file(&path).map_err(|cause| {
                    failure("remove recovered remote record", Span::default(), cause)
                })?;
                sync_directory(&directory).map_err(|cause| {
                    failure("persist remote recovery cleanup", Span::default(), cause)
                })?;
                drop(file);
            }
            result => {
                let mut error = ExecutionError::new(
                    format!(
                        "cannot recover remote statement for {}::{}; journal {}: {result:?}",
                        record.backend, record.target, record.journal
                    ),
                    Span::default(),
                );
                error.retained_paths.push(path);
                return Err(error.help("Reconnect the recorded endpoint and retry with the same scratch directory before starting new effects."));
            }
        }
    }
    Ok(())
}

/// The return value distinguishes a finite remote assignment from a byte flow;
/// ports and physical channels keep their separate asynchronous lifetime.
pub(crate) async fn execute(
    node: &Instruction,
    options: &Options,
    origin: Option<&InstructionOrigin>,
) -> Result<Option<ExecutionReport>> {
    let InstructionKind::Transfer {
        source,
        destination,
    } = &node.kind
    else {
        return Ok(None);
    };
    if matches!(source, Input::Port { .. }) {
        return Ok(None);
    }
    let Some(spec) = super::inspect(source, destination, node.span)? else {
        return Ok(None);
    };
    let output = spec.destination.clone();
    let input = source.clone();
    let span = node.span;
    let finite = crate::runtime::ssh::spawn_blocking(move || -> Result<bool> {
        if class(&output, false, span)? == FileClass::Stream {
            return Ok(false);
        }
        if let Input::File { location, .. } = input
            && class(&location, true, span)? == FileClass::Stream
        {
            return Ok(false);
        }
        Ok(true)
    })
    .await
    .map_err(|cause| failure("remote metadata worker failed", span, cause))??;
    if !finite {
        return Ok(None);
    }
    let source = source.clone();
    let owned_options = options.clone();
    let material = crate::runtime::ssh::spawn_blocking(move || {
        Material::capture(&source, &owned_options, span)
    })
    .await
    .map_err(|cause| failure("remote source capture worker failed", span, cause))?
    .map_err(|error| error.with_operand(origin, false))?;
    let result = spec
        .run(
            material.path.clone(),
            material.basename.clone(),
            options.limits.buffer_bytes,
            options.cancel.clone(),
            options.scratch_root.clone(),
        )
        .await
        .map_err(|error| error.with_operand(origin, true));
    let (assigned_files, assigned_bytes) = material.counts;
    let cleanup = material.close();
    match result {
        Ok(remote) => {
            if let Some(context) = crate::telemetry::context() {
                context.event(
                    "remote.publication",
                    &[],
                    &[
                        (
                            "transport.upload.files",
                            remote.transfer.copied_files as u64,
                        ),
                        ("transport.upload.bytes", remote.transfer.transferred_bytes),
                    ],
                );
            }
            let mut report = ExecutionReport {
                assignments: 1,
                assigned_files,
                assigned_bytes,
                published_roots: 1,
                ..Default::default()
            };
            report.cleanup.extend(remote.cleanup);
            report.cleanup.extend(cleanup);
            Ok(Some(report))
        }
        Err(mut error) => {
            for issue in cleanup {
                error.retained_paths.extend(issue.retained_paths);
                error.report.help.push(format!("cleanup: {}", issue.cause));
            }
            Err(error)
        }
    }
}

fn class(location: &Location, source: bool, span: Span) -> Result<FileClass> {
    if matches!(location.backend, Backend::Http { .. }) {
        return Ok(FileClass::Regular);
    }
    if matches!(location.backend, Backend::Tcp { .. }) {
        return Ok(FileClass::Stream);
    }
    if matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }) {
        let mut classes = crate::runtime::inspect_locations_live(
            &location.backend,
            std::slice::from_ref(&location.path),
        )
        .map_err(|message| ExecutionError::new(message, span))?;
        return classes
            .pop()
            .expect("one metadata record")
            .map_err(|message| ExecutionError::new(message, span));
    }
    crate::runtime::inspect_location(location, source)
        .map_err(|message| ExecutionError::new(message, span))
}

struct Material {
    invocation: InvocationRoot,
    assets: SnapshotStore,
    path: PathBuf,
    basename: Option<OsString>,
    counts: (usize, u64),
}

impl Material {
    fn capture(input: &Input, options: &Options, span: Span) -> Result<Self> {
        options.check(span)?;
        let private_parent = options
            .shared_invocation
            .as_ref()
            .map_or(options.scratch_root.as_path(), InvocationRoot::path);
        let invocation = InvocationRoot::create(private_parent)
            .map_err(|cause| failure("create remote statement storage", span, cause))?;
        let exclusion = options.shared_invocation.as_ref().unwrap_or(&invocation);
        let assets = match SnapshotStore::new_in(&invocation.path().join("assets"), exclusion) {
            Ok(assets) => assets,
            Err(cause) => {
                let mut error = failure("create remote source store", span, cause);
                if let Err(cause) = invocation.cleanup() {
                    error.retained_paths.push(invocation.path().to_owned());
                    error.report.help.push(format!("cleanup: {cause}"));
                }
                return Err(error);
            }
        };
        let path = invocation.path().join("payload");
        let mut owner = Self {
            invocation,
            assets,
            path,
            basename: None,
            counts: (0, 0),
        };
        if let Err(mut error) = owner.populate(input, options, span) {
            for issue in owner.close() {
                error.retained_paths.extend(issue.retained_paths);
                error.report.help.push(format!("cleanup: {}", issue.cause));
            }
            return Err(error);
        }
        Ok(owner)
    }

    fn populate(&mut self, input: &Input, options: &Options, span: Span) -> Result<()> {
        if let Some(registry) =
            crate::filesystem::recovery::Registry::open_existing(&options.scratch_root)
                .map_err(|cause| failure("verify local registry exclusion", span, cause))?
        {
            self.assets.exclude_registry(&registry).map_err(|cause| {
                failure(
                    "exclude local registry from remote source capture",
                    span,
                    cause,
                )
            })?;
        }
        if let Some(registry) = owned_registry(&options.scratch_root)
            .map_err(|cause| failure("verify remote registry exclusion", span, cause))?
        {
            self.assets
                .exclude_owned_directory(&registry)
                .map_err(|cause| {
                    failure("exclude remote registry from source capture", span, cause)
                })?;
        }
        let source_path = self.invocation.path().join("input");
        let selector = match input {
            Input::File {
                pattern: Some(pattern),
                ..
            } => Some(
                regex::Regex::new(&format!("\\A(?:{pattern})\\z"))
                    .map_err(|cause| failure("compile remote source selector", span, cause))?,
            ),
            _ => None,
        };
        let (snapshot, basename) = match input {
            Input::Literal(text) => {
                fs::write(&source_path, text.as_bytes())
                    .map_err(|cause| failure("materialize remote literal", span, cause))?;
                (self.assets.capture(&source_path), None)
            }
            Input::File { location, pattern } => {
                if let Some(path) = crate::runtime::local_path(location, true)
                    .map_err(|message| ExecutionError::new(message, span))?
                {
                    let basename = path.file_name().map(OsString::from);
                    let exclusion = options
                        .shared_invocation
                        .as_ref()
                        .unwrap_or(&self.invocation);
                    let snapshot = match &selector {
                        Some(selector) => {
                            self.assets.capture_selected_in(&path, exclusion, selector)
                        }
                        None => self.assets.capture_in(&path, exclusion),
                    };
                    (snapshot, basename)
                } else {
                    let receipt = crate::runtime::import_finite_source(
                        location,
                        &source_path,
                        pattern.as_deref(),
                        options.limits.buffer_bytes,
                        options.cancel.clone(),
                        span,
                    )
                    .map_err(|cause| failure("capture remote finite source", span, cause))?;
                    (self.assets.capture(&source_path), receipt.basename)
                }
            }
            Input::Port { .. } => unreachable!("ports are asynchronous flows"),
        };
        let snapshot =
            snapshot.map_err(|cause| failure("capture immutable remote source", span, cause))?;
        if snapshot.is_missing() {
            return Err(ExecutionError::new(
                "remote assignment source does not exist",
                span,
            ));
        }
        self.counts = snapshot
            .entries()
            .values()
            .filter_map(|entry| {
                let Entry::File { artifact, .. } = entry else {
                    return None;
                };
                Some(artifact.len())
            })
            .fold((0usize, 0u64), |(files, bytes), length| {
                (files + 1, bytes.saturating_add(length))
            });
        materialize(&snapshot, &self.path, span)?;
        drop(snapshot);
        self.basename = basename;
        Ok(())
    }

    fn close(mut self) -> Vec<CleanupIssue> {
        let root = self.invocation.path().to_owned();
        let result = self.assets.cleanup();
        drop(self.assets);
        let result = result.and_then(|()| self.invocation.cleanup());
        result
            .err()
            .map(|cause| CleanupIssue {
                cause: Box::new(cause),
                retained_paths: vec![root],
                resolved: false,
            })
            .into_iter()
            .collect()
    }
}

fn materialize(snapshot: &Snapshot, root: &Path, span: Span) -> Result<()> {
    for (relative, entry) in snapshot.entries() {
        let path = crate::filesystem::entry_path(root, relative);
        match entry {
            Entry::Directory { .. } => fs::create_dir(&path)
                .map_err(|cause| failure("materialize remote directory", span, cause))?,
            Entry::File { artifact, metadata } => {
                fs::copy(artifact.path(), &path)
                    .map_err(|cause| failure("materialize remote file", span, cause))?;
                metadata
                    .apply(&path)
                    .map_err(|cause| failure("apply remote file metadata", span, cause))?;
            }
        }
    }
    for (relative, entry) in snapshot.entries().iter().rev() {
        if let Entry::Directory { metadata } = entry {
            metadata
                .apply(&crate::filesystem::entry_path(root, relative))
                .map_err(|cause| failure("apply remote directory metadata", span, cause))?;
        }
    }
    Ok(())
}

fn sync_directory(directory: &Path) -> io::Result<()> {
    #[cfg(unix)]
    {
        File::open(directory)?.sync_all()
    }
    #[cfg(not(unix))]
    {
        let _ = directory;
        Ok(())
    }
}

/// A basename is not an ownership proof. Only a complete marker in an ordinary,
/// non-redirected directory authorizes identity-based snapshot exclusion.
pub(crate) fn owned_registry(scratch: &Path) -> io::Result<Option<PathBuf>> {
    let directory = scratch.join(REGISTRY);
    let metadata = match fs::symlink_metadata(&directory) {
        Ok(metadata) => metadata,
        Err(cause) if cause.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(cause) => return Err(cause),
    };
    if !metadata.is_dir() || redirected(&metadata) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "remote recovery registry is not an ordinary directory",
        ));
    }
    let marker = directory.join(OWNER);
    let metadata = fs::symlink_metadata(&marker)?;
    if !metadata.is_file() || redirected(&metadata) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "remote recovery registry has no ordinary ownership marker",
        ));
    }
    let mut bytes = Vec::new();
    File::open(marker)?
        .take(OWNER_MAGIC.len() as u64 + 1)
        .read_to_end(&mut bytes)?;
    if bytes != OWNER_MAGIC {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "remote recovery registry ownership marker is invalid",
        ));
    }
    Ok(Some(directory))
}

fn ensure_registry(scratch: &Path) -> io::Result<PathBuf> {
    if let Some(directory) = owned_registry(scratch)? {
        return Ok(directory);
    }
    fs::create_dir_all(scratch)?;
    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    let temporary = scratch.join(format!(
        ".msync-remote-registry-{}-{nonce}-{}.pending",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ));
    fs::create_dir(&temporary)?;
    let result = (|| {
        let mut marker = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(temporary.join(OWNER))?;
        marker.write_all(OWNER_MAGIC)?;
        marker.sync_all()?;
        drop(marker);
        sync_directory(&temporary)?;
        let directory = scratch.join(REGISTRY);
        match crate::io::publish_file(&temporary, &directory, true) {
            Ok(()) => {}
            Err(cause) if cause.kind() == io::ErrorKind::AlreadyExists => {
                fs::remove_dir_all(&temporary)?;
                return owned_registry(scratch)?.ok_or_else(|| {
                    io::Error::new(
                        io::ErrorKind::NotFound,
                        "remote registry disappeared during initialization",
                    )
                });
            }
            Err(cause) => return Err(cause),
        }
        sync_directory(scratch)?;
        Ok(directory)
    })();
    if result.is_err() && temporary.exists() {
        let _ = fs::remove_dir_all(temporary);
    }
    result
}

fn redirected(metadata: &fs::Metadata) -> bool {
    if metadata.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        metadata.file_attributes()
            & windows_sys::Win32::Storage::FileSystem::FILE_ATTRIBUTE_REPARSE_POINT
            != 0
    }
    #[cfg(not(windows))]
    false
}

fn failure(
    action: &str,
    span: Span,
    cause: impl std::error::Error + Send + Sync + 'static,
) -> ExecutionError {
    ExecutionError::caused(action, span, cause)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture() -> tempfile::TempDir {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix("remote-owner-v5-")
            .tempdir_in(root)
            .unwrap()
    }

    #[tokio::test]
    async fn startup_does_not_contact_an_active_writers_endpoint() {
        let root = fixture();
        let options = Options::new(root.path());
        let backend = Backend::Ssh {
            host: "not-a-real-host.invalid".into(),
            user: None,
            port: None,
        };
        let pending = Pending::create(
            root.path(),
            &backend,
            "/tmp/.msync-v4.test",
            "/tmp/destination",
            Span::default(),
        )
        .unwrap();
        recover(&options).await.unwrap();
        assert!(pending.path.exists());
        pending.finish(Span::default()).unwrap();
        assert_eq!(fs::read_dir(root.path().join(REGISTRY)).unwrap().count(), 1);
    }

    #[tokio::test]
    async fn incomplete_unpublished_record_is_not_a_remote_effect_obligation() {
        let root = fixture();
        ensure_registry(root.path()).unwrap();
        fs::write(
            root.path().join(REGISTRY).join("interrupted.pending"),
            b"{partial",
        )
        .unwrap();
        recover(&Options::new(root.path())).await.unwrap();
    }

    #[test]
    fn captured_tree_is_owned_and_retired_without_retaining_payloads() {
        let root = fixture();
        let source = root.path().join("source");
        fs::create_dir_all(source.join("empty")).unwrap();
        fs::write(source.join("config 可莉.toml"), b"captured").unwrap();
        let options = Options::new(root.path().join("scratch"));
        let input = Input::File {
            location: Location {
                backend: Backend::Local,
                path: source.to_string_lossy().into_owned(),
            },
            pattern: None,
        };
        let material = Material::capture(&input, &options, Span::default()).unwrap();
        let invocation = material.invocation.path().to_owned();
        fs::write(source.join("config 可莉.toml"), b"changed").unwrap();
        assert_eq!(
            fs::read(material.path.join("config 可莉.toml")).unwrap(),
            b"captured"
        );
        assert!(material.path.join("empty").is_dir());
        assert!(material.close().is_empty());
        assert!(!invocation.exists());
        assert_eq!(
            fs::read(source.join("config 可莉.toml")).unwrap(),
            b"changed"
        );
    }

    #[test]
    fn regular_file_materializes_at_its_exact_root_without_directory_suffix() {
        let root = fixture();
        let source = root.path().join("source-file");
        fs::write(&source, b"captured file").unwrap();
        let input = Input::File {
            location: Location {
                backend: Backend::Local,
                path: source.to_string_lossy().into_owned(),
            },
            pattern: None,
        };
        let material = Material::capture(
            &input,
            &Options::new(root.path().join("scratch")),
            Span::default(),
        )
        .unwrap();
        fs::write(&source, b"later source").unwrap();
        assert!(material.path.is_file());
        assert_eq!(fs::read(&material.path).unwrap(), b"captured file");
        assert_eq!(material.counts, (1, 13));
        assert_eq!(
            material.basename.as_deref(),
            Some(std::ffi::OsStr::new("source-file"))
        );
        assert!(material.close().is_empty());
    }

    #[test]
    fn literal_materializes_as_one_root_file() {
        let root = fixture();
        let input = Input::Literal("literal 可莉\n".into());
        let material = Material::capture(
            &input,
            &Options::new(root.path().join("scratch")),
            Span::default(),
        )
        .unwrap();
        assert!(material.path.is_file());
        assert_eq!(
            fs::read(&material.path).unwrap(),
            "literal 可莉\n".as_bytes()
        );
        assert_eq!(material.counts, (1, "literal 可莉\n".len() as u64));
        assert!(material.basename.is_none());
        assert!(material.close().is_empty());
    }

    #[test]
    fn failed_capture_retires_its_private_invocation() {
        let root = fixture();
        let options = Options::new(root.path().join("scratch"));
        let input = Input::File {
            location: Location {
                backend: Backend::Local,
                path: root.path().join("missing").to_string_lossy().into_owned(),
            },
            pattern: None,
        };
        assert!(Material::capture(&input, &options, Span::default()).is_err());
        assert_eq!(fs::read_dir(&options.scratch_root).unwrap().count(), 0);
    }

    #[test]
    fn selector_summary_counts_selected_payload_not_private_upload_bytes() {
        let root = fixture();
        let source = root.path().join("source");
        fs::create_dir(&source).unwrap();
        fs::write(source.join("selected.toml"), b"yes").unwrap();
        fs::write(source.join("unused.bin"), b"not assigned").unwrap();
        let options = Options::new(root.path().join("scratch"));
        let input = Input::File {
            location: Location {
                backend: Backend::Local,
                path: source.to_string_lossy().into_owned(),
            },
            pattern: Some(".*\\.toml".into()),
        };
        let material = Material::capture(&input, &options, Span::default()).unwrap();
        assert_eq!(material.counts, (1, 3));
        assert_eq!(fs::read_dir(&material.path).unwrap().count(), 1);
        assert_eq!(
            fs::read(material.path.join("selected.toml")).unwrap(),
            b"yes"
        );
        assert!(
            !material.path.join("unused.bin").exists(),
            "unselected data must never enter the upload payload"
        );
        assert!(material.close().is_empty());
    }

    #[test]
    fn unmatched_selector_has_an_empty_private_payload() {
        let root = fixture();
        let source = root.path().join("source");
        fs::create_dir(&source).unwrap();
        fs::write(source.join("unselected-secret"), b"never upload").unwrap();
        let input = Input::File {
            location: Location {
                backend: Backend::Local,
                path: source.to_string_lossy().into_owned(),
            },
            pattern: Some("absent".into()),
        };
        let options = Options::new(root.path().join("scratch"));
        let material = Material::capture(&input, &options, Span::default()).unwrap();
        assert_eq!(material.counts, (0, 0));
        assert!(material.path.is_dir());
        assert_eq!(fs::read_dir(&material.path).unwrap().count(), 0);
        assert_eq!(fs::read_dir(material.assets.root()).unwrap().count(), 0);
        assert!(material.close().is_empty());
    }

    #[cfg(unix)]
    #[test]
    fn remote_selector_is_whole_basename_even_with_multiline_regex() {
        let root = fixture();
        fs::write(root.path().join("selected"), b"yes").unwrap();
        fs::write(root.path().join("selected\nsuffix"), b"no").unwrap();
        let spec = super::super::Spec {
            destination: Location {
                backend: Backend::Wsl,
                path: "/tmp/unused/".into(),
            },
            mode: crate::language::DestinationMode::Container,
            span: Span::default(),
            pattern: Some("(?m)selected".into()),
            origin: None,
            literal: false,
        };
        assert_eq!(spec.names(root.path(), None).unwrap(), ["selected"]);
    }

    #[cfg(unix)]
    #[test]
    fn an_unselected_symlink_never_enters_the_upload_capture() {
        let root = fixture();
        let source = root.path().join("source");
        fs::create_dir(&source).unwrap();
        fs::write(source.join("selected"), b"yes").unwrap();
        std::os::unix::fs::symlink("absent", source.join("unselected")).unwrap();
        let input = Input::File {
            location: Location {
                backend: Backend::Local,
                path: source.to_string_lossy().into_owned(),
            },
            pattern: Some("selected".into()),
        };
        let material = Material::capture(
            &input,
            &Options::new(root.path().join("scratch")),
            Span::default(),
        )
        .unwrap();
        assert_eq!(material.counts, (1, 3));
        assert_eq!(fs::read_dir(&material.path).unwrap().count(), 1);
        assert!(!material.path.join("unselected").exists());
        assert!(material.close().is_empty());
    }

    #[test]
    fn a_matching_user_directory_is_not_claimed_or_hidden() {
        let root = fixture();
        let directory = root.path().join(REGISTRY);
        fs::create_dir(&directory).unwrap();
        fs::write(directory.join("user-data"), b"keep").unwrap();
        assert!(owned_registry(root.path()).is_err());
        assert!(ensure_registry(root.path()).is_err());
        assert_eq!(fs::read(directory.join("user-data")).unwrap(), b"keep");
        assert!(!directory.join(OWNER).exists());
    }

    #[test]
    fn source_capture_excludes_only_verified_runtime_registry() {
        let root = fixture();
        let source = root.path().join("source");
        let scratch = source.join(".temp");
        fs::create_dir_all(&scratch).unwrap();
        ensure_registry(&scratch).unwrap();
        fs::write(scratch.join("user-file"), b"user").unwrap();
        let input = Input::File {
            location: Location {
                backend: Backend::Local,
                path: source.to_string_lossy().into_owned(),
            },
            pattern: None,
        };
        let material = Material::capture(&input, &Options::new(&scratch), Span::default()).unwrap();
        assert_eq!(
            fs::read(material.path.join(".temp/user-file")).unwrap(),
            b"user"
        );
        assert!(!material.path.join(".temp").join(REGISTRY).exists());
        assert!(material.close().is_empty());
        assert_eq!(fs::read(scratch.join("user-file")).unwrap(), b"user");
    }

    #[test]
    fn shared_invocation_excludes_other_live_owners_not_user_scratch() {
        let root = fixture();
        let scratch = root.path().join(".temp");
        fs::create_dir(&scratch).unwrap();
        fs::write(scratch.join("user-file"), b"user").unwrap();
        let outer = InvocationRoot::create(&scratch).unwrap();
        fs::write(outer.path().join("another-owner-spool"), b"runtime").unwrap();
        let mut options = Options::new(&scratch);
        options.shared_invocation = Some(outer.clone());
        let input = Input::File {
            location: Location {
                backend: Backend::Local,
                path: root.path().to_string_lossy().into_owned(),
            },
            pattern: None,
        };
        let material = Material::capture(&input, &options, Span::default()).unwrap();
        let children: Vec<_> = fs::read_dir(material.path.join(".temp"))
            .unwrap()
            .map(|entry| entry.unwrap().file_name())
            .collect();
        assert_eq!(children, [OsString::from("user-file")]);
        assert!(material.close().is_empty());
        assert!(outer.path().join("another-owner-spool").exists());
        drop(options);
        outer.cleanup().unwrap();
        assert_eq!(fs::read(scratch.join("user-file")).unwrap(), b"user");
    }

    #[test]
    fn whole_tree_capture_excludes_live_local_recovery_records() {
        let root = fixture();
        let source = root.path().join("source");
        let scratch = source.join(".temp");
        fs::create_dir_all(&scratch).unwrap();
        fs::write(scratch.join("user-file"), b"user").unwrap();
        let registry = crate::filesystem::recovery::Registry::new(&scratch).unwrap();
        let parent = root.path().join("destination-parent");
        fs::create_dir(&parent).unwrap();
        let stage = parent.join(".msync-publication-external-owner");
        fs::create_dir(&stage).unwrap();
        let record = registry
            .enter()
            .unwrap()
            .register_stage(&parent, &stage)
            .unwrap();
        let record_path = record.path().to_owned();
        let input = Input::File {
            location: Location {
                backend: Backend::Local,
                path: source.to_string_lossy().into_owned(),
            },
            pattern: None,
        };
        let material = Material::capture(&input, &Options::new(&scratch), Span::default()).unwrap();
        assert_eq!(
            fs::read(material.path.join(".temp/user-file")).unwrap(),
            b"user"
        );
        assert!(!material.path.join(".temp/.msync-recovery-v1").exists());
        assert!(
            record_path.exists(),
            "source capture must not settle another live writer's record"
        );
        assert!(material.close().is_empty());
        assert!(record_path.exists());
        record.finish().unwrap();
        assert!(!record_path.exists());
        assert_eq!(fs::read(scratch.join("user-file")).unwrap(), b"user");
    }
}
