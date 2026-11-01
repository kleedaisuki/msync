//! Adjacent overwritten files share the surviving value's ordinary file stage.
//! Admission observes actual operand kinds once. After acquisition the owned
//! bytes and metadata are the value; public paths are not repeatedly re-proved.

use super::*;

#[cfg(test)]
use crate::hir::effects::EndpointRole;
#[cfg(test)]
use same_file::Handle;

pub(in crate::execution) enum FoldAttempt {
    Completed(ExecutionReport),
    Deoptimized,
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
        // Literal writes inherit the preceding destination mode, unlike a file
        // survivor whose captured metadata replaces that earlier value.
        if run.len() < 2
            || !run.windows(2).all(|pair| {
                crate::hir::rewrite::AdjacentOverwrite::recognize(&pair[0], &pair[1]).is_some()
            })
        {
            return Ok(FoldAttempt::Deoptimized);
        }
        let Some((Input::File { .. }, target)) = exact(last) else {
            return Ok(FoldAttempt::Deoptimized);
        };
        let Ok(target) = local(target, false, last.span) else {
            return Ok(FoldAttempt::Deoptimized);
        };
        if !ordinary_target(&target) || !discarded_sources_ready(&run[..run.len() - 1]) {
            return Ok(FoldAttempt::Deoptimized);
        }
        let Some((source, _)) = exact(last) else {
            unreachable!()
        };
        let Ok(Some((mut source, None))) = Source::input(source, last.span) else {
            return Ok(FoldAttempt::Deoptimized);
        };
        if !independent_source(&mut source, &target) {
            return Ok(FoldAttempt::Deoptimized);
        }
        let ready = Ready {
            instruction: last.id,
            source,
            target,
            mode: DestinationMode::Exact,
            pattern: None,
            span: last.span,
        };
        self.inspect_progress(last);
        let mut specialization = crate::telemetry::span("runtime.specialize.adjacent");
        let result = match &ready.source {
            Source::Local(path) => {
                let Some(metadata) = regular_metadata(path) else {
                    return Ok(FoldAttempt::Deoptimized);
                };
                let mut trace = copy_trace(&ready, "runtime-folded-assignment");
                let copied =
                    self.copy_file_after_acquire(&ready, &ready.target, Some(&metadata), acquired);
                trace.finish_result(&copied);
                copied.map(FoldAttempt::Completed)
            }
            Source::Remote(_) => self
                .execute_remote_after_acquire(&ready, "runtime-folded-assignment", acquired)
                .map(|attempt| match attempt {
                    Attempt::Completed(report) => FoldAttempt::Completed(report),
                    Attempt::Unsupported => FoldAttempt::Deoptimized,
                }),
            Source::Literal(_) => unreachable!("only file survivors are admitted"),
        };
        specialization.finish_result(&result);
        if matches!(result, Ok(FoldAttempt::Completed(_))) {
            specialization.metric("assignments.eliminated", (run.len() - 1) as u64);
        }
        result
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

fn ordinary_target(path: &Path) -> bool {
    match fs::symlink_metadata(path) {
        Ok(metadata) => metadata.is_file(),
        Err(cause) => cause.kind() == io::ErrorKind::NotFound,
    }
}

fn discarded_sources_ready(discarded: &[Instruction]) -> bool {
    discarded.iter().all(|node| {
        let Some((source, _)) = exact(node) else {
            return false;
        };
        let Input::File {
            location,
            pattern: None,
        } = source
        else {
            return matches!(source, Input::Literal(_));
        };
        let Ok(Some(path)) = crate::runtime::local_path(location, true) else {
            return false;
        };
        if !fs::symlink_metadata(&path).is_ok_and(|metadata| metadata.is_file()) {
            return false;
        }
        let mut options = fs::OpenOptions::new();
        options.read(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.custom_flags(libc::O_NONBLOCK);
        }
        options
            .open(path)
            .and_then(|file| file.metadata())
            .is_ok_and(|metadata| metadata.is_file())
    })
}

fn regular_metadata(path: &Path) -> Option<NodeMetadata> {
    let metadata = fs::symlink_metadata(path).ok()?;
    metadata
        .is_file()
        .then(|| NodeMetadata::capture(&metadata).ok())
        .flatten()
}

fn independent_source(source: &mut Source, target: &Path) -> bool {
    match source {
        // Reading the erased target name observes an earlier write in this same
        // program. A different hard-link name keeps its old value after rename.
        Source::Local(path) => path != target,
        Source::Remote(location) if location.backend == Backend::Wsl => {
            let target = Location {
                backend: Backend::Local,
                path: target.to_string_lossy().into_owned(),
            };
            if !crate::runtime::native_windows_filesystem(&target) {
                return false;
            }
            let Some((resolved, crate::plan::FileClass::Regular)) =
                crate::runtime::wsl_native_file(location)
            else {
                return false;
            };
            *location = resolved;
            true
        }
        _ => false,
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
    fn removing_an_unused_source_after_acquisition_keeps_the_captured_value() {
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
        assert!(matches!(result, FoldAttempt::Completed(_)));
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"last");
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
    fn unused_source_alias_and_readonly_changes_do_not_revalidate_public_state() {
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
            assert!(matches!(result, FoldAttempt::Completed(_)));
            assert_eq!(fs::read(root.path().join("target")).unwrap(), b"last");
            if alias {
                assert_eq!(fs::read(root.path().join("first")).unwrap(), b"old");
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
    fn discarded_sources_are_observed_only_at_admission() {
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
        assert!(matches!(result, FoldAttempt::Completed(_)));
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"value 19");
        assert!(owner.storage.is_none());
        assert_eq!(scratch_payload_count(root.path()), 0);
    }

    #[test]
    fn admitted_operand_addresses_are_not_resolved_again_after_acquisition() {
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
                "the {role:?} candidate must acquire the surviving value; prepared={:?}",
                crate::preparation::prepare(
                    &Program {
                        body: crate::hir::Region {
                            instructions: nodes.to_vec()
                        },
                    },
                    true
                )
            );
            assert!(matches!(result, FoldAttempt::Completed(_)));
            assert_eq!(fs::read(root.path().join("target")).unwrap(), b"last");
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
    fn staged_bytes_and_metadata_survive_source_removal_and_target_rotation() {
        let root = fixture();
        fs::write(root.path().join("first"), b"first").unwrap();
        fs::write(root.path().join("last"), b"last").unwrap();
        let nodes = pair(root.path());
        let mut expected =
            NodeMetadata::capture(&fs::metadata(root.path().join("last")).unwrap()).unwrap();
        expected.modified = std::time::UNIX_EPOCH + std::time::Duration::from_secs(978307200);
        expected.apply(&root.path().join("last")).unwrap();
        let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
        let result = owner
            .fold_after_acquire(&nodes[0], &nodes[1], || {
                fs::remove_file(root.path().join("last")).unwrap();
                fs::write(
                    root.path().join("target"),
                    b"public target changed during copy",
                )
                .unwrap();
            })
            .unwrap();
        assert!(matches!(result, FoldAttempt::Completed(_)));
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"last");
        assert_eq!(
            fs::metadata(root.path().join("target"))
                .unwrap()
                .modified()
                .unwrap(),
            expected.modified
        );
        assert!(!root.path().join("scratch").exists());
    }

    #[test]
    fn a_survivor_reading_the_written_name_retains_the_same_program_dependency() {
        let root = fixture();
        fs::write(root.path().join("first"), b"A").unwrap();
        fs::write(root.path().join("target"), b"old").unwrap();
        let mut nodes = pair(root.path());
        let InstructionKind::Transfer { source, .. } = &mut nodes[1].kind else {
            unreachable!()
        };
        *source = Input::File {
            location: Location {
                backend: Backend::Local,
                path: root.path().join("target").to_string_lossy().into_owned(),
            },
            pattern: None,
        };
        let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
        assert!(matches!(
            owner.try_fold_run(&nodes).unwrap(),
            FoldAttempt::Deoptimized
        ));
        for node in &nodes {
            assert!(matches!(
                owner.execute(node).unwrap(),
                Attempt::Completed(_)
            ));
        }
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"A");
        assert!(!root.path().join("scratch").exists());
    }

    #[test]
    fn discarded_directory_assignments_are_not_erased_as_file_overwrites() {
        let root = fixture();
        fs::create_dir(root.path().join("first")).unwrap();
        fs::write(root.path().join("first/child"), b"directory value").unwrap();
        fs::write(root.path().join("last"), b"last").unwrap();
        let nodes = pair(root.path());
        let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
        assert!(matches!(
            owner.try_fold_run(&nodes).unwrap(),
            FoldAttempt::Deoptimized
        ));
        assert!(matches!(
            owner.execute(&nodes[0]).unwrap(),
            Attempt::Completed(_)
        ));
        assert!(owner.execute(&nodes[1]).is_err());
        assert_eq!(
            fs::read(root.path().join("target/child")).unwrap(),
            b"directory value"
        );
    }

    #[cfg(unix)]
    #[test]
    fn a_discarded_link_can_change_the_following_target_address() {
        let root = fixture();
        fs::write(root.path().join("referent"), b"referent").unwrap();
        std::os::unix::fs::symlink("referent", root.path().join("first")).unwrap();
        fs::write(root.path().join("last"), b"last").unwrap();
        fs::write(root.path().join("target"), b"old").unwrap();
        let nodes = pair(root.path());
        let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
        assert!(matches!(
            owner.try_fold_run(&nodes).unwrap(),
            FoldAttempt::Deoptimized
        ));
        for node in &nodes {
            assert!(matches!(
                owner.execute(node).unwrap(),
                Attempt::Completed(_)
            ));
        }
        assert_eq!(
            fs::read_link(root.path().join("target")).unwrap(),
            Path::new("referent")
        );
        assert_eq!(fs::read(root.path().join("referent")).unwrap(), b"last");
    }

    #[cfg(unix)]
    proptest::proptest! {
        #![proptest_config(proptest::test_runner::Config::with_cases(32))]
        #[test]
        fn folded_file_chains_match_ordinary_bytes_and_metadata(
            payloads in proptest::collection::vec(proptest::collection::vec(proptest::num::u8::ANY, 0..256), 6),
            choices in proptest::collection::vec(0usize..8, 2..14),
        ) {
            use std::os::unix::fs::PermissionsExt;
            let root = fixture();
            for (index, payload) in payloads.iter().enumerate() {
                let path = root.path().join(format!("value-{index}"));
                fs::write(&path, payload).unwrap();
                let metadata = NodeMetadata::new(fs::Permissions::from_mode([0o400, 0o600, 0o640, 0o755][index % 4]), std::time::UNIX_EPOCH + std::time::Duration::new(1_000_000 + index as u64, 123_456_789));
                metadata.apply(&path).unwrap();
            }
            let mut outcomes = Vec::new();
            for optimized in [false, true] {
                let label = if optimized { "folded" } else { "ordinary" };
                let target = root.path().join(format!("{label}-target"));
                let alias = root.path().join(format!("{label}-alias"));
                fs::write(&target, b"initial value").unwrap();
                NodeMetadata::new(fs::Permissions::from_mode(0o640), std::time::UNIX_EPOCH + std::time::Duration::from_secs(1_100_000)).apply(&target).unwrap();
                fs::hard_link(&target, &alias).unwrap();
                let nodes = choices.iter().enumerate().map(|(id, choice)| {
                    let source = match choice { 0 => target.clone(), 1 => alias.clone(), _ => root.path().join(format!("value-{}", choice - 2)) };
                    Instruction { id: InstructionId(id), span: Span::default(), expansion: Vec::new(), kind: InstructionKind::Transfer {
                        source: Input::File { location: Location { backend: Backend::Local, path: source.to_string_lossy().into_owned() }, pattern: None },
                        destination: Output::File { location: Location { backend: Backend::Local, path: target.to_string_lossy().into_owned() }, mode: DestinationMode::Exact },
                    } }
                }).collect::<Vec<_>>();
                let mut owner = Ordinary::new(&Options::new(root.path().join(format!("{label}-scratch"))));
                let folded = optimized && matches!(owner.try_fold_run(&nodes).unwrap(), FoldAttempt::Completed(_));
                if !folded {
                    for node in &nodes { assert!(matches!(owner.execute(node).unwrap(), Attempt::Completed(_))); }
                }
                let metadata = fs::metadata(&target).unwrap();
                outcomes.push((fs::read(&target).unwrap(), metadata.permissions().mode() & 0o777, metadata.modified().unwrap()));
                proptest::prop_assert!(owner.storage.is_none());
            }
            proptest::prop_assert_eq!(&outcomes[0], &outcomes[1]);
        }
    }
}
