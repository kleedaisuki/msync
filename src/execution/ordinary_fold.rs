//! Adjacent-write specialization requires fresh statement-local runtime proofs.
//!
//! The runtime binds candidate addresses only under retained raw-operand guards.
//! Canonical writes can then propagate types without falsely invalidating another
//! spelling of the same target. This ephemeral bound program never replaces the
//! semantic run: every erased raw traversal is rechecked, the final source recipe
//! is validated again at acquisition, and deoptimization resumes the original IR.

use super::*;

use crate::hir::effects::EndpointRole;
use same_file::Handle;

pub(in crate::execution) enum FoldAttempt {
    Completed(ExecutionReport),
    Deoptimized,
}

/// Pure shape filtering keeps unrelated copies off the metadata/preparation path.
fn eligible(first: &Instruction, last: &Instruction) -> bool {
    crate::hir::rewrite::AdjacentOverwrite::recognize(first, last).is_some()
}

impl Ordinary {
    pub(in crate::execution) fn try_fold_run(
        &mut self,
        run: &[Instruction],
    ) -> Result<FoldAttempt> {
        self.fold_run_after_acquire(run, || {})
    }

    #[cfg(test)]
    fn fold_after_acquire(
        &mut self,
        first: &Instruction,
        last: &Instruction,
        acquired: impl FnOnce(),
    ) -> Result<FoldAttempt> {
        self.fold_run_after_acquire(&[first.clone(), last.clone()], acquired)
    }

    fn fold_run_after_acquire(
        &mut self,
        run: &[Instruction],
        acquired: impl FnOnce(),
    ) -> Result<FoldAttempt> {
        let (Some(first), Some(last)) = (run.first(), run.last()) else {
            return Ok(FoldAttempt::Deoptimized);
        };
        self.options.check(first.span)?;
        // A literal preserves destination permissions, so its metadata can
        // depend on an erased earlier write. A file survivor replaces them.
        if run.len() < 2
            || !matches!(exact(last), Some((Input::File { pattern: None, .. }, _)))
            || !run.windows(2).all(|pair| eligible(&pair[0], &pair[1]))
        {
            return Ok(FoldAttempt::Deoptimized);
        }
        self.inspect_progress(last);
        let Some(guard) = Guard::capture(&run[..run.len() - 1]) else {
            return Ok(FoldAttempt::Deoptimized);
        };
        let mut specialization = crate::telemetry::span("runtime.specialize.adjacent");
        if !guard.valid() {
            return Ok(FoldAttempt::Deoptimized);
        }
        // The final read still validates its raw source immediately before
        // acquisition; only its guarded destination is already bound here.
        let mut survivor = last.clone();
        let InstructionKind::Transfer {
            destination: Output::File { location, .. },
            ..
        } = &mut survivor.kind
        else {
            return Ok(FoldAttempt::Deoptimized);
        };
        *location = bound(&guard.target.path);
        let Ok(Some(ready)) = Ready::inspect(&survivor, self.origin.as_ref()) else {
            return Ok(FoldAttempt::Deoptimized);
        };
        if ready.target != guard.target.path {
            return Ok(FoldAttempt::Deoptimized);
        }
        if self.storage.is_none() {
            self.storage = Some(Storage::new(&self.options, last.span)?);
        }
        let result = self.fold_pinned(&ready, guard, acquired);
        let cleanup = self.close();
        let result = settle_cleanup(result, cleanup, last.span);
        specialization.finish_result(&result);
        if matches!(result, Ok(FoldAttempt::Completed(_))) {
            specialization.metric("assignments.eliminated", (run.len() - 1) as u64);
        } else {
            specialization.attribute("phase", "deoptimized");
        }
        result
    }

    fn fold_pinned(
        &mut self,
        ready: &Ready,
        guard: Guard,
        acquired: impl FnOnce(),
    ) -> Result<FoldAttempt> {
        let mut trace = copy_trace(ready, "runtime-folded-assignment");
        let result = self.fold_snapshot(ready, guard, acquired);
        trace.finish_result(&result);
        match &result {
            Ok(FoldAttempt::Completed(report)) => {
                trace.attribute("phase", "committed");
                trace.metric("files.assigned", report.assigned_files as u64);
                trace.metric("bytes.assigned", report.assigned_bytes);
            }
            Ok(FoldAttempt::Deoptimized) => trace.attribute("phase", "guard-fallback"),
            Err(error) => {
                trace.attribute("phase", "failed");
                trace.record_error("copy.runtime-fold", error);
                if error.is_cancelled() {
                    trace.cancelled();
                }
            }
        }
        result
    }

    fn fold_snapshot(
        &mut self,
        ready: &Ready,
        guard: Guard,
        acquired: impl FnOnce(),
    ) -> Result<FoldAttempt> {
        observe(
            self.progress.as_ref(),
            ready,
            Phase::Acquire,
            &ExecutionReport::default(),
        );
        let storage = self.storage.as_mut().expect("fold owns its source store");
        let selection = storage.source(ready, &self.options);
        self.options.check(ready.span)?;
        let Ok(Some(selection)) = selection else {
            return Ok(FoldAttempt::Deoptimized);
        };
        let Some(Entry::File { artifact, .. }) = selection.snapshot.entries().get(Path::new(""))
        else {
            return Ok(FoldAttempt::Deoptimized);
        };
        let bytes = artifact.len();
        acquired();
        if !guard.valid() || relation(&selection, &ready.target, ready.span).is_err() {
            return Ok(FoldAttempt::Deoptimized);
        }
        let Ok(snapshot) = storage.assignment(&selection.snapshot, &ready.target, ready) else {
            return Ok(FoldAttempt::Deoptimized);
        };
        let assignments = [Assignment {
            target: ready.target.clone(),
            snapshot,
        }];
        let mut report = ExecutionReport {
            assigned_files: 1,
            assigned_bytes: bytes,
            ..ExecutionReport::default()
        };
        observe(self.progress.as_ref(), ready, Phase::Stage, &report);
        let published = storage.publish_checked(
            &assignments,
            ready,
            &self.options,
            self.progress.as_ref(),
            &mut report,
            Some(move || guard.valid()),
        )?;
        if !published {
            return Ok(FoldAttempt::Deoptimized);
        }
        report.assignments = 1;
        Ok(FoldAttempt::Completed(report))
    }
}

fn exact(node: &Instruction) -> Option<(&Input, &Location)> {
    match &node.kind {
        InstructionKind::Transfer {
            source,
            destination:
                Output::File {
                    location,
                    mode: DestinationMode::Exact,
                },
        } => Some((source, location)),
        _ => None,
    }
}

struct Guard {
    sources: Vec<SourceStamp>,
    target: Stamp,
    parent: Stamp,
}

impl Guard {
    fn capture(discarded: &[Instruction]) -> Option<Self> {
        let first = discarded.first()?;
        let (_, target) = exact(first)?;
        let target = Stamp::endpoint(target, EndpointRole::Destination, first.span)?;
        if !matches!(target.kind, Kind::Missing | Kind::File) || target.readonly {
            return None;
        }
        let mut sources = Vec::new();
        for node in discarded {
            let (source, _) = exact(node)?;
            let location = match source {
                Input::Literal(_) => continue,
                Input::File {
                    location,
                    pattern: None,
                } => location,
                _ => return None,
            };
            let stamp = Stamp::endpoint(location, EndpointRole::Source, node.span)?;
            if stamp.kind != Kind::File || stamp.readonly || stamp.same_identity(&target) {
                return None;
            }
            sources.push(SourceStamp { stamp });
        }
        let mut parent = target.path.parent()?;
        let parent = loop {
            let stamp = Stamp::capture(parent.to_owned(), first.span)?;
            if stamp.kind != Kind::Missing {
                break stamp;
            }
            parent = parent.parent()?;
        };
        if parent.kind != Kind::Directory || parent.readonly {
            return None;
        }
        Some(Self {
            sources,
            target,
            parent,
        })
    }

    fn valid(&self) -> bool {
        self.target.valid()
            && self.parent.valid()
            && self
                .sources
                .iter()
                .all(|source| source.admissible_against(&self.target))
    }
}

struct SourceStamp {
    stamp: Stamp,
}

fn bound(path: &Path) -> Location {
    Location {
        backend: Backend::Local,
        path: path.to_string_lossy().into_owned(),
    }
}

impl SourceStamp {
    fn admissible_against(&self, target: &Stamp) -> bool {
        if !self.stamp.traversable() {
            return false;
        }
        let Ok(Some(metadata)) = metadata(&self.stamp.path, Span::default()) else {
            return false;
        };
        if !metadata.is_file() || metadata.permissions().readonly() {
            return false;
        }
        let Ok(file) = fs::File::open(&self.stamp.path) else {
            return false;
        };
        let Ok(metadata) = file.metadata() else {
            return false;
        };
        if !metadata.is_file() || metadata.permissions().readonly() {
            return false;
        }
        let Ok(identity) = Handle::from_file(file) else {
            return false;
        };
        // The certified surviving file replaces all metadata as well as bytes.
        // Historical source identity/mode is not observable; the intermediate
        // assignment must still be an admissible ordinary-file overwrite.
        target
            .identity
            .as_ref()
            .is_none_or(|target| identity != *target)
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    Missing,
    File,
    Directory,
}

struct Stamp {
    path: PathBuf,
    traversal: Option<Traversal>,
    kind: Kind,
    identity: Option<Handle>,
    permissions: u32,
    readonly: bool,
}

// Canonical identity alone loses erased traversal obligations: an ancestor may
// stop being a directory or become a link while the final object remains intact.
struct Traversal {
    location: Location,
    role: EndpointRole,
}

impl Stamp {
    fn endpoint(location: &Location, role: EndpointRole, span: Span) -> Option<Self> {
        let path = local(location, role == EndpointRole::Source, span).ok()?;
        let mut stamp = Self::capture(path, span)?;
        stamp.traversal = Some(Traversal {
            location: location.clone(),
            role,
        });
        Some(stamp)
    }

    fn capture(path: PathBuf, span: Span) -> Option<Self> {
        let metadata = metadata(&path, span).ok()?;
        let Some(metadata) = metadata else {
            return Some(Self {
                path,
                traversal: None,
                kind: Kind::Missing,
                identity: None,
                permissions: 0,
                readonly: false,
            });
        };
        let kind = if metadata.is_file() {
            Kind::File
        } else if metadata.is_dir() {
            Kind::Directory
        } else {
            return None;
        };
        let identity = Handle::from_path(&path).ok()?;
        Some(Self {
            path,
            traversal: None,
            kind,
            identity: Some(identity),
            permissions: permissions(&metadata),
            readonly: metadata.permissions().readonly(),
        })
    }

    fn traversable(&self) -> bool {
        if let Some(traversal) = &self.traversal {
            let resolved = local(
                &traversal.location,
                traversal.role == EndpointRole::Source,
                Span::default(),
            );
            if !resolved.is_ok_and(|resolved| resolved == self.path) {
                return false;
            }
        }
        true
    }

    fn valid(&self) -> bool {
        if !self.traversable() {
            return false;
        }
        let Some(current) = Self::capture(self.path.clone(), Span::default()) else {
            return false;
        };
        current.kind == self.kind
            && current.permissions == self.permissions
            && current.identity == self.identity
    }

    fn same_identity(&self, other: &Self) -> bool {
        self.identity.is_some() && self.identity == other.identity
    }
}

#[cfg(unix)]
fn permissions(metadata: &fs::Metadata) -> u32 {
    use std::os::unix::fs::PermissionsExt;
    metadata.permissions().mode()
}

#[cfg(windows)]
fn permissions(metadata: &fs::Metadata) -> u32 {
    u32::from(metadata.permissions().readonly())
}

#[cfg(not(any(unix, windows)))]
fn permissions(metadata: &fs::Metadata) -> u32 {
    u32::from(metadata.permissions().readonly())
}

fn settle_cleanup(
    result: Result<FoldAttempt>,
    cleanup: Vec<CleanupIssue>,
    span: Span,
) -> Result<FoldAttempt> {
    match result {
        Ok(FoldAttempt::Completed(mut report)) => {
            report.cleanup.extend(cleanup);
            Ok(FoldAttempt::Completed(report))
        }
        other if cleanup.is_empty() => other,
        other => {
            let mut error = other.err().unwrap_or_else(|| {
                ExecutionError::new("cannot discard invalidated runtime specialization", span)
            });
            for issue in cleanup {
                error.retained_paths.extend(issue.retained_paths);
                error.report.help.push(format!("cleanup: {}", issue.cause));
            }
            Err(error)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn scratch_payload_count(root: &Path) -> usize {
        if !root.join("scratch").exists() {
            return 0;
        }
        fs::read_dir(root.join("scratch"))
            .unwrap()
            .filter(|entry| {
                let entry = entry.as_ref().unwrap();
                if entry.file_name() != ".msync-recovery-v1" {
                    return true;
                }
                assert!(
                    fs::read_dir(entry.path())
                        .unwrap()
                        .all(|entry| entry.unwrap().file_name() == ".lock")
                );
                false
            })
            .count()
    }

    fn fixture() -> tempfile::TempDir {
        let scratch = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&scratch).unwrap();
        tempfile::Builder::new()
            .prefix("ordinary-fold-")
            .tempdir_in(scratch)
            .unwrap()
    }

    #[test]
    fn a_literal_survivor_does_not_erase_the_previous_files_permission_flow() {
        let root = fixture();
        fs::write(root.path().join("first"), b"first").unwrap();
        fs::write(root.path().join("last"), b"last").unwrap();
        let mut nodes = pair(root.path());
        if let InstructionKind::Transfer { source, .. } = &mut nodes[1].kind {
            *source = Input::Literal("literal".into());
        }
        let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
        let result = owner
            .fold_after_acquire(&nodes[0], &nodes[1], || {
                panic!("a literal survivor needs a different metadata proof")
            })
            .unwrap();
        assert!(matches!(result, FoldAttempt::Deoptimized));
        assert!(!root.path().join("target").exists());
        assert!(owner.storage.is_none());
    }

    fn pair(root: &Path) -> [Instruction; 2] {
        let context = crate::compiler::CompileContext {
            pwd: root.to_owned(),
            source_file: None,
        };
        crate::pipeline::compile(
            &mut crate::compiler::structured::Session::new(),
            "first -> target\nlast -> target\n",
            &context,
            &[],
        )
        .unwrap()
        .program
        .body
        .instructions
        .try_into()
        .unwrap()
    }

    #[test]
    fn changing_the_unused_source_after_acquisition_deoptimizes_without_publication() {
        let root = fixture();
        fs::write(root.path().join("first"), b"first").unwrap();
        fs::write(root.path().join("last"), b"last").unwrap();
        fs::write(root.path().join("target"), b"old").unwrap();
        let nodes = pair(root.path());
        let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
        let result = owner
            .fold_after_acquire(&nodes[0], &nodes[1], || {
                fs::remove_file(root.path().join("first")).unwrap();
            })
            .unwrap();
        assert!(matches!(result, FoldAttempt::Deoptimized));
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"old");
        assert!(owner.storage.is_none());
        assert_eq!(scratch_payload_count(root.path()), 0);
    }

    #[test]
    fn a_readable_writable_source_rotation_keeps_the_final_file_specialization() {
        let root = fixture();
        fs::write(root.path().join("first"), b"first").unwrap();
        fs::write(root.path().join("last"), b"last").unwrap();
        fs::write(root.path().join("target"), b"old").unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(root.path().join("first"), fs::Permissions::from_mode(0o644))
                .unwrap();
            fs::set_permissions(root.path().join("last"), fs::Permissions::from_mode(0o640))
                .unwrap();
        }
        let old_source = Handle::from_path(root.path().join("first")).unwrap();
        let mut final_metadata =
            NodeMetadata::capture(&fs::metadata(root.path().join("last")).unwrap()).unwrap();
        final_metadata.modified = std::time::UNIX_EPOCH + std::time::Duration::from_secs(978307200);
        final_metadata.apply(&root.path().join("last")).unwrap();
        let nodes = pair(root.path());
        let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
        let mut acquired = false;
        let result = owner
            .fold_after_acquire(&nodes[0], &nodes[1], || {
                acquired = true;
                let replacement = root.path().join("replacement");
                fs::write(&replacement, b"rotated first").unwrap();
                #[cfg(unix)]
                {
                    use std::os::unix::fs::PermissionsExt;
                    fs::set_permissions(&replacement, fs::Permissions::from_mode(0o600)).unwrap();
                }
                fs::rename(replacement, root.path().join("first")).unwrap();
            })
            .unwrap();
        assert!(acquired);
        let FoldAttempt::Completed(report) = result else {
            panic!("the discarded source's historical inode/mode is not an observable");
        };
        assert_eq!(report.assignments, 1);
        assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
        assert_ne!(
            Handle::from_path(root.path().join("first")).unwrap(),
            old_source
        );
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"last");
        assert_eq!(
            fs::read(root.path().join("first")).unwrap(),
            b"rotated first"
        );
        let metadata = fs::metadata(root.path().join("target")).unwrap();
        assert_eq!(metadata.modified().unwrap(), final_metadata.modified);
        assert_eq!(
            metadata.permissions().readonly(),
            final_metadata.permissions.readonly()
        );
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            assert_eq!(metadata.permissions().mode() & 0o777, 0o640);
            assert_eq!(
                fs::metadata(root.path().join("first"))
                    .unwrap()
                    .permissions()
                    .mode()
                    & 0o777,
                0o600
            );
        }
        assert!(owner.storage.is_none());
        assert_eq!(scratch_payload_count(root.path()), 0);
        assert!(fs::read_dir(root.path()).unwrap().all(|entry| {
            !entry
                .unwrap()
                .file_name()
                .to_string_lossy()
                .starts_with(".msync-publication")
        }));
    }

    #[test]
    fn source_rotation_to_a_target_alias_or_readonly_file_still_deoptimizes() {
        for alias in [false, true] {
            let root = fixture();
            fs::write(root.path().join("first"), b"first").unwrap();
            fs::write(root.path().join("last"), b"last").unwrap();
            fs::write(root.path().join("target"), b"old").unwrap();
            let writable = fs::metadata(root.path().join("first"))
                .unwrap()
                .permissions();
            let nodes = pair(root.path());
            let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
            let mut acquired = false;
            let result = owner
                .fold_after_acquire(&nodes[0], &nodes[1], || {
                    acquired = true;
                    if alias {
                        fs::remove_file(root.path().join("first")).unwrap();
                        fs::hard_link(root.path().join("target"), root.path().join("first"))
                            .unwrap();
                    } else {
                        let replacement = root.path().join("replacement");
                        fs::write(&replacement, b"rotated readonly").unwrap();
                        fs::rename(replacement, root.path().join("first")).unwrap();
                        let mut readonly = writable.clone();
                        readonly.set_readonly(true);
                        fs::set_permissions(root.path().join("first"), readonly).unwrap();
                    }
                })
                .unwrap();
            assert!(acquired);
            assert!(matches!(result, FoldAttempt::Deoptimized));
            assert_eq!(fs::read(root.path().join("target")).unwrap(), b"old");
            if alias {
                assert!(
                    same_file::is_same_file(root.path().join("first"), root.path().join("target"))
                        .unwrap()
                );
            } else {
                assert_eq!(
                    fs::read(root.path().join("first")).unwrap(),
                    b"rotated readonly"
                );
                assert!(
                    fs::metadata(root.path().join("first"))
                        .unwrap()
                        .permissions()
                        .readonly()
                );
                fs::set_permissions(root.path().join("first"), writable).unwrap();
            }
            assert!(owner.storage.is_none());
            assert_eq!(scratch_payload_count(root.path()), 0);
        }
    }

    #[test]
    fn every_discarded_source_in_a_run_is_guarded_after_acquisition() {
        let root = fixture();
        let mut text = String::new();
        for index in 0..20 {
            fs::write(
                root.path().join(format!("source-{index}")),
                format!("value {index}"),
            )
            .unwrap();
            text.push_str(&format!("source-{index} -> target\n"));
        }
        fs::write(root.path().join("target"), b"old").unwrap();
        let context = crate::compiler::CompileContext {
            pwd: root.path().to_owned(),
            source_file: None,
        };
        let program = crate::pipeline::compile(
            &mut crate::compiler::structured::Session::new(),
            &text,
            &context,
            &[],
        )
        .unwrap()
        .program;
        let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
        let result = owner
            .fold_run_after_acquire(&program.body.instructions, || {
                fs::remove_file(root.path().join("source-10")).unwrap();
            })
            .unwrap();
        assert!(matches!(result, FoldAttempt::Deoptimized));
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"old");
        assert!(owner.storage.is_none());
        assert_eq!(scratch_payload_count(root.path()), 0);
    }

    #[test]
    fn erased_operand_traversals_are_rechecked_after_acquisition() {
        for role in [EndpointRole::Source, EndpointRole::Destination] {
            let root = fixture();
            fs::create_dir(root.path().join("step")).unwrap();
            fs::write(root.path().join("first"), b"first").unwrap();
            fs::write(root.path().join("last"), b"last").unwrap();
            fs::write(root.path().join("target"), b"old").unwrap();
            let nodes = traversal_pair(root.path(), role);
            let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
            let mut acquired = false;
            let result = owner
                .fold_after_acquire(&nodes[0], &nodes[1], || {
                    acquired = true;
                    fs::remove_dir(root.path().join("step")).unwrap();
                    fs::write(root.path().join("step"), b"not a directory").unwrap();
                })
                .unwrap();
            assert!(
                acquired,
                "the {role:?} candidate must reach the post-acquire guard; prepared={:?}",
                crate::preparation::prepare(
                    &Program {
                        body: crate::hir::Region {
                            instructions: nodes.to_vec()
                        },
                    },
                    true
                )
            );
            assert!(matches!(result, FoldAttempt::Deoptimized));
            assert_eq!(fs::read(root.path().join("target")).unwrap(), b"old");
            assert_eq!(fs::read(root.path().join("first")).unwrap(), b"first");
            assert!(owner.storage.is_none());
            assert_eq!(scratch_payload_count(root.path()), 0);
        }
    }

    fn traversal_pair(root: &Path, role: EndpointRole) -> [Instruction; 2] {
        let mut nodes = pair(root);
        for (index, node) in nodes.iter_mut().enumerate() {
            match (&mut node.kind, role, index) {
                (
                    InstructionKind::Transfer {
                        source: Input::File { location, .. },
                        ..
                    },
                    EndpointRole::Source,
                    0,
                ) => {
                    location.path = root.join("step/../first").to_string_lossy().into_owned();
                }
                (
                    InstructionKind::Transfer {
                        destination: Output::File { location, .. },
                        ..
                    },
                    EndpointRole::Destination,
                    _,
                ) => {
                    location.path = root.join("step/../target").to_string_lossy().into_owned();
                }
                _ => {}
            }
        }
        nodes
    }
    #[test]
    fn the_post_stage_guard_keeps_the_original_namespace_and_settles_journals() {
        let root = fixture();
        fs::write(root.path().join("first"), b"first").unwrap();
        fs::write(root.path().join("last"), b"last").unwrap();
        let nodes = pair(root.path());
        let guard = Guard::capture(&nodes[..1]).unwrap();
        let ready = Ready::inspect(&nodes[1], None).unwrap().unwrap();
        let options = Options::new(root.path().join("scratch"));
        let mut owner = Ordinary::new(&options);
        owner.storage = Some(Storage::new(&options, Span::default()).unwrap());
        {
            let storage = owner.storage.as_mut().unwrap();
            let selection = storage.source(&ready, &options).unwrap().unwrap();
            let assignments = [Assignment {
                target: ready.target.clone(),
                snapshot: selection.snapshot,
            }];
            let mut report = ExecutionReport::default();
            let first = root.path().join("first");
            let valid = move || {
                fs::remove_file(&first).unwrap();
                guard.valid()
            };
            assert!(
                !storage
                    .publish_checked(
                        &assignments,
                        &ready,
                        &options,
                        None,
                        &mut report,
                        Some(valid)
                    )
                    .unwrap()
            );
        }
        assert!(!root.path().join("target").exists());
        assert!(owner.close().is_empty());
    }
}
