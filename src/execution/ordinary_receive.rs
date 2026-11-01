//! Received files are already private versions: move them into the destination
//! stage instead of capturing and materializing their bytes again.

use super::*;

pub(super) enum Incoming {
    Captured { basename: Option<OsString> },
    Stream,
}

impl Ordinary {
    pub(super) fn execute_remote(&self, ready: &Ready) -> Result<Attempt> {
        self.execute_remote_after_acquire(ready, "ordinary-assignment", || {})
    }

    pub(super) fn execute_remote_after_acquire(
        &self,
        ready: &Ready,
        kind: &'static str,
        acquired: impl FnOnce(),
    ) -> Result<Attempt> {
        let Source::Remote(location) = &ready.source else {
            unreachable!("remote admission selected by source type");
        };
        let span = ready.span;
        let existing = metadata(&ready.target, span)?;
        match ready.mode {
            DestinationMode::Exact if existing.as_ref().is_some_and(fs::Metadata::is_dir) => {
                return Err(ExecutionError::new(
                    "the exact destination is an existing directory",
                    span,
                ));
            }
            DestinationMode::Container | DestinationMode::ReplaceTree
                if existing.as_ref().is_some_and(|metadata| !metadata.is_dir()) =>
            {
                return Err(ExecutionError::new(
                    "the destination needs to be a directory",
                    span,
                ));
            }
            _ => {}
        }
        let parent = if ready.mode == DestinationMode::Container
            && existing.as_ref().is_some_and(fs::Metadata::is_dir)
        {
            ready.target.as_path()
        } else {
            ready
                .target
                .parent()
                .ok_or_else(|| ExecutionError::new("destination needs a parent", span))?
        };
        let temporary = InvocationRoot::create(parent)
            .map_err(|cause| failure("cannot create download stage", span, cause))?;
        let payload = temporary.path().join("payload");
        let mut trace = copy_trace(ready, kind);
        let result = (|| {
            let Incoming::Captured { basename } = self.receive_source(location, &payload, ready)?
            else {
                return Ok(Attempt::Unsupported);
            };
            acquired();
            self.options.check(span)?;
            let target =
                if ready.mode == DestinationMode::Container && ready.pattern.is_none() {
                    ready.target.join(basename.ok_or_else(|| {
                        ExecutionError::new("container source has no basename", span)
                    })?)
                } else {
                    ready.target.clone()
                };
            let mut nodes = Vec::new();
            collect_native(&payload, Path::new(""), &mut nodes, &self.options, span)?;
            let mut report = ExecutionReport {
                assignments: 1,
                ..ExecutionReport::default()
            };
            for node in &nodes {
                match node.kind {
                    NativeKind::File { bytes } => {
                        report.assigned_files += 1;
                        report.assigned_bytes = report.assigned_bytes.saturating_add(bytes);
                    }
                    NativeKind::Symlink { .. } => report.assigned_files += 1,
                    NativeKind::Directory => {}
                }
            }
            if ready.pattern.is_some()
                && nodes.len() == 1
                && matches!(nodes[0].kind, NativeKind::Directory)
            {
                return Ok(Attempt::Completed(report));
            }
            if ready.mode == DestinationMode::ReplaceTree {
                if !matches!(
                    nodes.first().map(|node| &node.kind),
                    Some(NativeKind::Directory)
                ) {
                    return Err(ExecutionError::new(
                        "directory replacement needs a directory source",
                        span,
                    ));
                }
                crate::filesystem::remove_tree(&target)
                    .map_err(|cause| failure("cannot remove replaced directory", span, cause))?;
            }
            observe(self.progress.as_ref(), ready, Phase::Publish, &report);
            install_received(&payload, &target, &self.options, span)?;
            report.published_roots = 1;
            Ok(Attempt::Completed(report))
        })();
        trace.finish_result(&result);
        let cleanup = temporary.cleanup();
        match (result, cleanup) {
            (Ok(Attempt::Completed(mut report)), Err(cause)) => {
                report
                    .cleanup
                    .push(super::cleanup(cause, temporary.path().to_owned()));
                Ok(Attempt::Completed(report))
            }
            (Err(mut error), Err(cause)) => {
                error.report.help.push(format!(
                    "cannot remove temporary download {}: {cause}",
                    temporary.path().display()
                ));
                Err(error)
            }
            (result, _) => result,
        }
    }

    pub(super) fn receive_source(
        &self,
        location: &Location,
        payload: &Path,
        ready: &Ready,
    ) -> Result<Incoming> {
        let mut trace = crate::telemetry::span("ordinary.acquire");
        let result = if location.backend == Backend::Wsl {
            use super::super::wsl_source::{Acquisition, Request};
            super::super::wsl_source::acquire(
                Request {
                    source: location,
                    stage: payload,
                    mode: ready.mode,
                    pattern: ready.pattern.as_deref(),
                },
                &self.options,
                ready.span,
            )
            .map(|received| match received {
                Acquisition::Captured { basename } => Incoming::Captured {
                    basename: Some(basename),
                },
                Acquisition::Stream => Incoming::Stream,
            })
        } else {
            crate::runtime::import_finite_source(
                location,
                payload,
                ready.pattern.as_deref(),
                self.options.limits.buffer_bytes,
                self.options.cancel.clone(),
                ready.span,
            )
            .map(|receipt| Incoming::Captured {
                basename: receipt.basename,
            })
            .map_err(|cause| ExecutionError::caused("cannot receive source", ready.span, cause))
        };
        trace.finish_result(&result);
        result
    }
}

fn install_received(source: &Path, target: &Path, options: &Options, span: Span) -> Result<()> {
    options.check(span)?;
    let source_metadata = fs::symlink_metadata(source)
        .map_err(|cause| ExecutionError::caused("cannot inspect received data", span, cause))?;
    if !source_metadata.is_dir() {
        return install_received_file(source, target, &source_metadata, options, span);
    }
    let existed = metadata(target, span)?.is_some();
    if !existed {
        fs::create_dir_all(target.parent().unwrap_or_else(|| Path::new("."))).map_err(|cause| {
            ExecutionError::caused("cannot create destination parent", span, cause)
        })?;
        match crate::filesystem::move_new(source, target) {
            Ok(()) => return Ok(()),
            Err(cause) if cause.kind() == io::ErrorKind::CrossesDevices => {}
            Err(cause) => {
                return Err(ExecutionError::caused(
                    "cannot install received directory",
                    span,
                    cause,
                ));
            }
        }
    }
    fs::create_dir_all(target)
        .map_err(|cause| ExecutionError::caused("cannot merge received directory", span, cause))?;
    // Only this incoming directory will be discarded. Granting write access
    // permits consuming its children without altering any original source inode.
    let mut permissions = source_metadata.permissions();
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        permissions.set_mode(permissions.mode() | 0o700);
    }
    #[cfg(windows)]
    {
        #[allow(clippy::permissions_set_readonly_false)]
        permissions.set_readonly(false);
    }
    fs::set_permissions(source, permissions).map_err(|cause| {
        ExecutionError::caused("cannot consume received directory", span, cause)
    })?;
    let mut children = fs::read_dir(source)
        .map_err(|cause| {
            ExecutionError::caused("cannot enumerate received directory", span, cause)
        })?
        .collect::<io::Result<Vec<_>>>()
        .map_err(|cause| ExecutionError::caused("cannot read received directory", span, cause))?;
    children.sort_by_key(fs::DirEntry::file_name);
    for child in children {
        install_received(
            &child.path(),
            &target.join(child.file_name()),
            options,
            span,
        )?;
    }
    if !existed {
        NodeMetadata::capture(&source_metadata)
            .and_then(|metadata| metadata.apply(target))
            .map_err(|cause| {
                ExecutionError::caused("cannot apply received directory metadata", span, cause)
            })?;
    }
    Ok(())
}

fn install_received_file(
    source: &Path,
    target: &Path,
    metadata: &fs::Metadata,
    options: &Options,
    span: Span,
) -> Result<()> {
    let stage = crate::io::StagedFile::new(target, false)
        .map_err(|cause| ExecutionError::caused("cannot stage received file", span, cause))?;
    fs::remove_file(stage.path()).map_err(|cause| {
        ExecutionError::caused("cannot prepare received file stage", span, cause)
    })?;
    match fs::rename(source, stage.path()) {
        Ok(()) => {}
        Err(cause) if cause.kind() == io::ErrorKind::CrossesDevices => {
            if metadata.file_type().is_symlink() {
                let link = fs::read_link(source).map_err(|cause| {
                    ExecutionError::caused("cannot read received link", span, cause)
                })?;
                #[cfg(windows)]
                let directory = {
                    use std::os::windows::fs::FileTypeExt;
                    metadata.file_type().is_symlink_dir()
                };
                #[cfg(not(windows))]
                let directory = false;
                crate::filesystem::create_symlink(&link, stage.path(), directory).map_err(
                    |cause| ExecutionError::caused("cannot move received link", span, cause),
                )?;
                NodeMetadata::capture(metadata)
                    .and_then(|metadata| metadata.apply_link(stage.path()))
                    .map_err(|cause| {
                        ExecutionError::caused("cannot apply received link metadata", span, cause)
                    })?;
            } else {
                fs::OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(stage.path())
                    .map_err(|cause| {
                        ExecutionError::caused("cannot create received file stage", span, cause)
                    })?;
                crate::io::copy_file(
                    source,
                    stage.path(),
                    options.limits.buffer_bytes,
                    &options.cancel,
                )
                .map_err(|cause| {
                    ExecutionError::caused(
                        "cannot move received file across filesystems",
                        span,
                        cause,
                    )
                })?;
                NodeMetadata::capture(metadata)
                    .and_then(|metadata| metadata.apply(stage.path()))
                    .map_err(|cause| {
                        ExecutionError::caused("cannot restore received metadata", span, cause)
                    })?;
            }
        }
        Err(cause) => {
            return Err(ExecutionError::caused(
                "cannot move received file to final stage",
                span,
                cause,
            ));
        }
    }
    options.check(span)?;
    if metadata.is_file() {
        crate::filesystem::sync_file(stage.path())
            .map_err(|cause| failure("cannot flush received file", span, cause))?;
    }
    stage
        .commit()
        .map_err(|cause| ExecutionError::caused("cannot publish received file", span, cause))
}
