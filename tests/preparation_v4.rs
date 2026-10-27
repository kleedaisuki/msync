//! Metadata, type-epoch and rewrite-certificate journeys for structured plans.

use std::collections::BTreeMap;
use std::fs;
use std::path::Path;

use msync::compiler::CompileContext;
use msync::compiler::structured::Session;
use msync::endpoint::Backend;
use msync::hir::effects::OperationFacts;
use msync::hir::graph::PlanningContext;
use msync::hir::{self, Input, InstructionKind, Output, Region};
use msync::language;
use msync::plan::FileClass;
use msync::preparation::{self, RewriteKind};
use proptest::prelude::*;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("preparation-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn compile(root: &Path, text: &str) -> hir::Program {
    Session::new()
        .compile(
            &language::parse(text).unwrap(),
            &CompileContext {
                pwd: root.to_owned(),
                source_file: None,
            },
        )
        .unwrap()
}

fn transfers(region: &Region) -> Vec<&hir::Instruction> {
    let mut output = Vec::new();
    for node in &region.instructions {
        match &node.kind {
            InstructionKind::Transfer { .. } => output.push(node),
            InstructionKind::Transaction { body, .. } => output.extend(transfers(body)),
            _ => {}
        }
    }
    output
}

#[test]
fn overwritten_existing_regular_assignments_collapse_without_creating_a_new_namespace_event() {
    let root = workspace();
    fs::write(root.path().join("one"), "first").unwrap();
    fs::write(root.path().join("two"), "second").unwrap();
    fs::write(root.path().join("output"), "existing").unwrap();
    let program = compile(root.path(), "one -> output\ntwo -> output\n");
    let prepared = preparation::prepare(&program, true).unwrap();
    assert_eq!(prepared.program.body.instructions.len(), 1);
    let remaining = &prepared.program.body.instructions[0];
    assert_eq!(
        prepared.facts[&remaining.id].destination,
        FileClass::Regular
    );
    assert!(
        prepared
            .rewrites
            .iter()
            .any(|rewrite| matches!(rewrite.kind, RewriteKind::DeadWrite { .. }))
    );
    assert_eq!(
        fs::read_to_string(root.path().join("output")).unwrap(),
        "existing"
    );
    prepared.verify().unwrap();
}

#[test]
fn differing_source_endpoints_do_not_prevent_dead_store_proof_with_explicit_type_evidence() {
    let root = workspace();
    fs::write(root.path().join("one"), "first").unwrap();
    fs::write(root.path().join("output"), "existing").unwrap();
    let program = compile(
        root.path(),
        "one -> output\n$wsl::/not-contacted/config.toml -> output\n",
    );
    let mut context = PlanningContext::default();
    for node in &program.body.instructions {
        context.operation_facts.insert(
            node.id,
            OperationFacts {
                source: FileClass::Regular,
                destination: FileClass::Regular,
            },
        );
    }
    let prepared = preparation::prepare_with_context(&program, false, context).unwrap();
    assert_eq!(prepared.program.body.instructions.len(), 1);
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        ..
    } = &prepared.program.body.instructions[0].kind
    else {
        panic!()
    };
    assert_eq!(location.backend, Backend::Wsl);
    assert_eq!(
        fs::read_to_string(root.path().join("output")).unwrap(),
        "existing"
    );
    prepared.verify().unwrap();
}

fn pending_root_write(root: &Path) -> PlanningContext {
    use msync::hir::effects::{Access, AccessKind, Effects, FileResource, Resource, Scope};
    use msync::hir::graph::ProcessEffects;
    let process = hir::ProcessId(900);
    let mut context = PlanningContext {
        live_processes: vec![process],
        ..PlanningContext::default()
    };
    context.process_effects.insert(
        process,
        ProcessEffects::mediated(
            Effects {
                accesses: vec![Access {
                    resource: Resource::File(FileResource::new(
                        msync::endpoint::Location {
                            backend: Backend::Local,
                            path: root.to_string_lossy().into_owned(),
                        },
                        Scope::Tree,
                    )),
                    kind: AccessKind::Write,
                }],
                unknown_external: false,
            },
            "retained-native-view",
            "pending-import-epoch",
        )
        .unwrap(),
    );
    context
}

#[test]
fn a_previous_live_root_writer_invalidates_host_metadata_before_its_import_gate() {
    let root = workspace();
    fs::write(root.path().join("one"), "first").unwrap();
    fs::write(root.path().join("two"), "second").unwrap();
    fs::write(root.path().join("output"), "host version before import").unwrap();
    let program = compile(root.path(), "one -> output\ntwo -> output\n");
    let prepared =
        preparation::prepare_with_context(&program, true, pending_root_write(root.path())).unwrap();
    assert_eq!(transfers(&prepared.program.body).len(), 2);
    assert!(prepared.rewrites.is_empty());
    assert_eq!(
        prepared.facts[&program.body.instructions[0].id].source,
        FileClass::Unknown
    );
    assert_eq!(
        prepared.facts[&program.body.instructions[0].id].destination,
        FileClass::Unknown
    );
    assert!(!prepared.is_execution_ready());
    assert!(
        !prepared
            .context
            .existing_parent_frontiers
            .contains_key(&program.body.instructions[0].id)
    );
    prepared.verify().unwrap();
}

#[test]
fn pending_imports_defer_host_missing_source_and_directory_destination_errors() {
    let root = workspace();
    fs::create_dir(root.path().join("output")).unwrap();
    let program = compile(root.path(), "produced-later -> output\n");
    let prepared =
        preparation::prepare_with_context(&program, true, pending_root_write(root.path())).unwrap();
    let facts = prepared.facts[&program.body.instructions[0].id];
    assert_eq!(facts.source, FileClass::Unknown);
    assert_eq!(facts.destination, FileClass::Unknown);
    assert!(!prepared.is_execution_ready());
    prepared.verify().unwrap();
}

#[test]
fn pending_write_defers_enotdir_probes_without_collapsing_unverified_parent_segments() {
    let root = workspace();
    fs::write(
        root.path().join("blocker"),
        "currently a non-directory ancestor",
    )
    .unwrap();
    fs::write(root.path().join("source"), "source after import").unwrap();
    for text in [
        "blocker/child -> output\n",
        "blocker/../source -> output\n",
        "source -> blocker/child\n",
    ] {
        let original = compile(root.path(), text);
        let prepared =
            preparation::prepare_with_context(&original, true, pending_root_write(root.path()))
                .unwrap();
        let node = &prepared.original.body.instructions[0];
        assert_eq!(prepared.facts[&node.id].source, FileClass::Unknown);
        assert_eq!(prepared.facts[&node.id].destination, FileClass::Unknown);
        assert!(!prepared.is_execution_ready());
        assert!(prepared.rewrites.is_empty());
        assert!(
            !prepared
                .context
                .existing_parent_frontiers
                .contains_key(&node.id)
        );
        if text.contains("../") {
            let InstructionKind::Transfer {
                source: Input::File { location, .. },
                ..
            } = &node.kind
            else {
                panic!()
            };
            assert!(Path::new(&location.path).is_absolute());
            assert!(
                Path::new(&location.path)
                    .components()
                    .any(|part| part == std::path::Component::ParentDir)
            );
        }
        prepared.verify().unwrap();
    }
    let mut malformed = compile(root.path(), "source -> output\n");
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        ..
    } = &mut malformed.body.instructions[0].kind
    else {
        panic!()
    };
    location.path.push('\0');
    let errors = preparation::prepare_with_context_detailed(
        &malformed,
        true,
        pending_root_write(root.path()),
    )
    .unwrap_err();
    assert_eq!(errors[0].operand, preparation::Operand::Source);
    assert!(errors[0].report.diagnostic.message.contains("NUL"));
}

#[test]
fn unrelated_or_readonly_writer_does_not_suppress_real_enotdir_errors() {
    use msync::hir::effects::AccessKind;
    use msync::hir::graph::ProcessEffects;
    let root = workspace();
    fs::write(root.path().join("blocker"), "not a directory").unwrap();
    fs::create_dir(root.path().join("unrelated")).unwrap();
    let program = compile(root.path(), "blocker/child -> output\n");
    let unrelated = pending_root_write(&root.path().join("unrelated"));
    let mut readonly = pending_root_write(root.path());
    let effects = readonly
        .process_effects
        .get_mut(&hir::ProcessId(900))
        .unwrap();
    let mut footprint = effects.footprint().clone();
    for access in &mut footprint.accesses {
        access.kind = AccessKind::Read;
    }
    *effects = ProcessEffects::mediated(footprint, "readonly-native-view", "read-epoch").unwrap();
    for context in [unrelated, readonly] {
        let errors =
            preparation::prepare_with_context_detailed(&program, true, context).unwrap_err();
        assert_eq!(errors[0].operand, preparation::Operand::Source);
        assert_eq!(errors[0].instruction, Some(program.body.instructions[0].id));
        assert!(
            errors[0]
                .report
                .help
                .iter()
                .any(|help| help.contains("blocker"))
        );
    }
}

#[test]
fn unrelated_or_readonly_live_footprints_do_not_erase_regular_file_type_proofs() {
    use msync::hir::effects::AccessKind;
    use msync::hir::graph::ProcessEffects;
    let root = workspace();
    fs::write(root.path().join("one"), "first").unwrap();
    fs::write(root.path().join("two"), "second").unwrap();
    fs::write(root.path().join("output"), "existing").unwrap();
    fs::create_dir(root.path().join("unrelated")).unwrap();
    let program = compile(root.path(), "one -> output\ntwo -> output\n");
    let unrelated = pending_root_write(&root.path().join("unrelated"));
    let mut readonly = pending_root_write(root.path());
    let effects = readonly
        .process_effects
        .get_mut(&hir::ProcessId(900))
        .unwrap();
    let mut footprint = effects.footprint().clone();
    for access in &mut footprint.accesses {
        access.kind = AccessKind::Read;
    }
    *effects = ProcessEffects::mediated(footprint, "readonly-native-view", "read-epoch").unwrap();
    for context in [unrelated, readonly] {
        let prepared = preparation::prepare_with_context(&program, true, context).unwrap();
        assert_eq!(transfers(&prepared.program.body).len(), 1);
        assert_eq!(
            prepared.facts[&program.body.instructions[0].id].source,
            FileClass::Regular
        );
        prepared.verify().unwrap();
    }
}

#[test]
fn an_actual_pinned_input_retains_its_type_despite_a_pending_path_replacement() {
    use msync::hir::graph::VersionReadProof;
    use msync::transaction::filesystem::{Entry, SnapshotStore};
    let root = workspace();
    let managed = root.path().join("managed");
    fs::create_dir(&managed).unwrap();
    fs::write(managed.join("source"), "retained version").unwrap();
    let mut store = SnapshotStore::new(&root.path().join("versions")).unwrap();
    let snapshot = store.capture(&managed.join("source")).unwrap();
    let Entry::File { artifact, .. } = &snapshot.entries()[Path::new("")] else {
        panic!()
    };
    let program = compile(root.path(), "managed/source -> output\n");
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        ..
    } = &program.body.instructions[0].kind
    else {
        panic!()
    };
    let mut context = pending_root_write(&managed);
    context.pinned_inputs.insert(
        program.body.instructions[0].id,
        VersionReadProof::pinned_file(
            location.clone(),
            "retained-test-store",
            artifact.path().to_string_lossy(),
        )
        .unwrap(),
    );
    fs::remove_file(managed.join("source")).unwrap();
    let mut mismatched = context.clone();
    mismatched.pinned_inputs.insert(
        program.body.instructions[0].id,
        VersionReadProof::pinned_file(
            msync::endpoint::Location {
                backend: Backend::Local,
                path: managed.join("other").to_string_lossy().into_owned(),
            },
            "retained-test-store",
            "different-file-version",
        )
        .unwrap(),
    );
    let uncertain = preparation::prepare_with_context(&program, true, mismatched).unwrap();
    assert_eq!(
        uncertain.facts[&program.body.instructions[0].id].source,
        FileClass::Unknown
    );
    assert!(!uncertain.is_execution_ready());
    let prepared = preparation::prepare_with_context(&program, true, context).unwrap();
    assert_eq!(
        prepared.facts[&program.body.instructions[0].id].source,
        FileClass::Regular
    );
    assert_eq!(prepared.context.pinned_inputs.len(), 1);
    assert!(prepared.rewrites.is_empty());
    prepared.verify().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn runtime_owned_epoch_facts_survive_an_outstanding_opaque_process() {
    let root = workspace();
    let program = compile(root.path(), "source -> output\n");
    let context = PlanningContext {
        live_processes: vec![hir::ProcessId(900)],
        operation_facts: BTreeMap::from([(
            program.body.instructions[0].id,
            OperationFacts {
                source: FileClass::Regular,
                destination: FileClass::Missing,
            },
        )]),
        ..PlanningContext::default()
    };
    let prepared = preparation::prepare_with_context(&program, true, context).unwrap();
    assert_eq!(
        prepared.facts[&program.body.instructions[0].id].source,
        FileClass::Regular
    );
    prepared.verify().unwrap();
}

#[test]
fn literal_backed_file_versions_keep_their_metadata_producer() {
    let root = workspace();
    let program = compile(
        root.path(),
        "\"one\" -> middle\nmiddle -> output\n\"two\" -> middle\n",
    );
    let prepared = preparation::prepare(&program, true).unwrap();
    assert_eq!(prepared.program.body.instructions.len(), 3);
    assert!(matches!(&prepared.program.body.instructions[1].kind,
        InstructionKind::Transfer { source: Input::File { location, .. }, .. }
        if Path::new(&location.path) == root.path().join("middle")));
    prepared.verify().unwrap();
}

#[test]
fn literal_overwrite_retains_the_permission_producing_file_assignment() {
    let root = workspace();
    let mut outcomes = Vec::new();
    let source_mtime = std::time::UNIX_EPOCH + std::time::Duration::from_secs(1_600_000_000);
    for (name, optimized) in [("reference", false), ("optimized", true)] {
        let work = root.path().join(name);
        fs::create_dir(&work).unwrap();
        let source = work.join("source");
        let target = work.join("target");
        fs::write(&source, "source bytes").unwrap();
        fs::write(&target, "old target").unwrap();
        let writable = fs::metadata(&target).unwrap().permissions();
        fs::File::options()
            .write(true)
            .open(&source)
            .unwrap()
            .set_times(fs::FileTimes::new().set_modified(source_mtime))
            .unwrap();
        #[cfg(windows)]
        {
            let mut permissions = fs::metadata(&source).unwrap().permissions();
            permissions.set_readonly(true);
            fs::set_permissions(&source, permissions).unwrap();
        }
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&source, fs::Permissions::from_mode(0o750)).unwrap();
            fs::set_permissions(&target, fs::Permissions::from_mode(0o600)).unwrap();
        }
        let original = compile(&work, "source -> target\n\"literal\" -> target\n");
        let planned = preparation::prepare(&original, true).unwrap();
        assert_eq!(transfers(&planned.program.body).len(), 2);
        assert!(planned.rewrites.is_empty());
        planned.verify().unwrap();
        let program = if optimized {
            &planned.program
        } else {
            &original
        };
        let options = msync::execution::Options::new(root.path().join(format!("scratch-{name}")));
        let result = msync::execution::execute(program, &options);
        let metadata = fs::metadata(&target).unwrap();
        let bytes = fs::read(&target).unwrap();
        let permissions = metadata.permissions();
        let copied_mtime = metadata.modified().unwrap() == source_mtime;
        // Restore original writable metadata before any assertion can abort
        // cleanup of a readonly Windows fixture.
        fs::set_permissions(&source, writable.clone()).unwrap();
        fs::set_permissions(&target, writable).unwrap();
        #[cfg(windows)]
        assert!(permissions.readonly());
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            assert_eq!(permissions.mode() & 0o777, 0o750);
            assert!(result.is_ok(), "{result:?}");
            assert_eq!(bytes, b"literal");
            assert!(!copied_mtime);
        }
        outcomes.push((result.is_ok(), bytes, permissions.readonly(), copied_mtime));
    }
    assert_eq!(outcomes[0], outcomes[1]);
}

#[test]
fn redundant_later_copy_keeps_the_first_creation_and_source_metadata() {
    let root = workspace();
    fs::write(root.path().join("source"), "same source version").unwrap();
    let program = compile(root.path(), "source -> output\n$local::source -> output\n");
    let prepared = preparation::prepare(&program, true).unwrap();
    assert_eq!(prepared.program.body.instructions.len(), 1);
    assert_eq!(
        prepared.program.body.instructions[0].id,
        program.body.instructions[0].id
    );
    assert!(matches!(
        prepared.rewrites[0].kind,
        RewriteKind::RedundantWrite { .. }
    ));
    assert_eq!(
        prepared.facts[&program.body.instructions[0].id].destination,
        FileClass::Missing
    );
    prepared.verify().unwrap();
}

#[test]
fn a_missing_destination_creation_is_not_deleted_when_the_later_source_version_differs() {
    let root = workspace();
    fs::write(root.path().join("one"), "one").unwrap();
    fs::write(root.path().join("two"), "two").unwrap();
    let prepared = preparation::prepare(
        &compile(root.path(), "one -> output\ntwo -> output\n"),
        true,
    )
    .unwrap();
    assert_eq!(prepared.program.body.instructions.len(), 2);
    assert!(prepared.rewrites.is_empty());
}

#[test]
fn optimized_and_reference_execution_keep_literal_file_metadata_correlations() {
    let root = workspace();
    for (name, optimized) in [("reference", false), ("optimized", true)] {
        let work = root.path().join(name);
        fs::create_dir(&work).unwrap();
        let original = compile(&work, "\"literal value\" -> middle\nmiddle -> output\n");
        let planned = preparation::prepare(&original, true).unwrap();
        assert!(matches!(
            &planned.program.body.instructions[1].kind,
            InstructionKind::Transfer {
                source: Input::File { .. },
                ..
            }
        ));
        let program = if optimized {
            &planned.program
        } else {
            &original
        };
        let options = msync::execution::Options::new(root.path().join(format!("scratch-{name}")));
        let report = msync::execution::execute(program, &options).unwrap();
        assert!(report.cleanup.is_empty());
        assert_eq!(
            fs::read(work.join("middle")).unwrap(),
            fs::read(work.join("output")).unwrap()
        );
        assert_eq!(
            fs::metadata(work.join("middle"))
                .unwrap()
                .modified()
                .unwrap(),
            fs::metadata(work.join("output"))
                .unwrap()
                .modified()
                .unwrap()
        );
    }
}

#[test]
fn regular_file_forwarding_exposes_dead_writes_until_a_fixed_point() {
    let root = workspace();
    fs::write(root.path().join("origin"), "one").unwrap();
    fs::write(root.path().join("newer"), "two").unwrap();
    fs::write(root.path().join("middle"), "existing").unwrap();
    let program = compile(
        root.path(),
        "origin -> middle\nmiddle -> output\nnewer -> middle\n",
    );
    let prepared = preparation::prepare(&program, true).unwrap();
    assert_eq!(prepared.program.body.instructions.len(), 2);
    assert!(
        prepared
            .rewrites
            .iter()
            .any(|rewrite| matches!(rewrite.kind, RewriteKind::Forward { .. }))
    );
    assert!(
        prepared
            .rewrites
            .iter()
            .any(|rewrite| matches!(rewrite.kind, RewriteKind::DeadWrite { .. }))
    );
    prepared.verify().unwrap();
}

#[test]
fn forwarding_never_reads_a_newer_version_of_an_overwritten_origin() {
    let root = workspace();
    fs::write(root.path().join("origin"), "initial").unwrap();
    let program = compile(
        root.path(),
        "origin -> middle\n\"newer\" -> origin\nmiddle -> output\n",
    );
    let prepared = preparation::prepare(&program, true).unwrap();
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        ..
    } = &prepared.program.body.instructions.last().unwrap().kind
    else {
        panic!()
    };
    assert_eq!(Path::new(&location.path), root.path().join("middle"));
    prepared.verify().unwrap();
}

#[test]
fn commits_and_rollback_versions_are_observation_boundaries() {
    let root = workspace();
    let program = compile(
        root.path(),
        "transaction A:\n    \"one\" -> value\n    commit\n    \"two\" -> value\n    rollback\n    value -> copy\nrollback A\n",
    );
    let prepared = preparation::prepare(&program, true).unwrap();
    assert_eq!(transfers(&prepared.program.body).len(), 3);
    let source = transfers(&prepared.program.body)[2];
    assert_eq!(prepared.facts[&source.id].source, FileClass::Regular);
    prepared.verify().unwrap();
}

#[test]
fn named_origin_undo_does_not_erase_unrelated_type_state() {
    let root = workspace();
    let program = compile(
        root.path(),
        "transaction A:\n    \"a\" -> a\n\"b\" -> b\nrollback A\nb -> output\n",
    );
    let prepared = preparation::prepare(&program, true).unwrap();
    let last = prepared.program.body.instructions.last().unwrap();
    assert_eq!(prepared.facts[&last.id].source, FileClass::Regular);
    let missing = compile(
        root.path(),
        "transaction A:\n    \"a\" -> a\nrollback A\na -> output\n",
    );
    assert!(preparation::prepare(&missing, true).is_err());
}

#[test]
fn discarded_pending_writes_do_not_become_named_undo_footprints() {
    let root = workspace();
    let program = compile(
        root.path(),
        "transaction A:\n    \"a\" -> a\n    commit\n    \"discarded\" -> b\n    rollback\n\"unrelated\" -> b\nrollback A\nb -> output\n",
    );
    let prepared = preparation::prepare(&program, true).unwrap();
    assert_eq!(
        prepared.facts[&program.body.instructions.last().unwrap().id].source,
        FileClass::Regular
    );
    prepared.verify().unwrap();
}

#[test]
fn parent_directory_kinds_follow_successful_creations_and_origin_undo() {
    let root = workspace();
    let program = compile(
        root.path(),
        "transaction A:\n    \"data\" -> new/deep/file\n    new -> snapshot\nrollback A\n",
    );
    let prepared = preparation::prepare(&program, true).unwrap();
    let second = transfers(&prepared.program.body)[1];
    assert_eq!(prepared.facts[&second.id].source, FileClass::Directory);
    let undone = compile(
        root.path(),
        "transaction A:\n    \"data\" -> new/deep/file\nrollback A\nnew -> snapshot\n",
    );
    assert!(preparation::prepare(&undone, true).is_err());
}

#[test]
fn metadata_type_validation_distinguishes_directory_modes_and_selectors() {
    let root = workspace();
    fs::create_dir(root.path().join("source")).unwrap();
    fs::create_dir(root.path().join("target")).unwrap();
    fs::write(root.path().join("file"), "file").unwrap();
    for script in [
        "file -> target\n",
        "source -> file\n",
        "file -> target/.\n",
        "\"text\" -> target/\n",
        "file/<.*> -> target/\n",
    ] {
        assert!(
            preparation::prepare(&compile(root.path(), script), true).is_err(),
            "{script}"
        );
    }
    let prepared =
        preparation::prepare(&compile(root.path(), "source -> target/.\n"), true).unwrap();
    assert_eq!(
        prepared.facts.values().next().unwrap().source,
        FileClass::Directory
    );
    assert!(root.path().join("target").is_dir());
    assert_eq!(fs::read_dir(root.path().join("target")).unwrap().count(), 0);
}

#[test]
fn programs_are_not_launched_and_future_outputs_remain_unknown_until_runtime_refinement() {
    let root = workspace();
    let executable = root.path().join("not-a-real-executable");
    fs::write(&executable, "not executable file data").unwrap();
    let program = compile(
        root.path(),
        "worker => $pwd::not-a-real-executable(\"do not run\")\nfuture-output -> copy\n",
    );
    let prepared = preparation::prepare(&program, true).unwrap();
    assert!(!prepared.is_execution_ready());
    assert!(
        prepared
            .facts
            .values()
            .any(|facts| facts.source == FileClass::Unknown)
    );
    assert!(!root.path().join("copy").exists());
}

#[test]
fn a_program_with_no_file_ports_still_requires_a_verified_resource_view() {
    let root = workspace();
    fs::write(root.path().join("placeholder"), "metadata only").unwrap();
    let program = compile(
        root.path(),
        "program => $pwd::placeholder(\"not executed\")\n",
    );
    let prepared = preparation::prepare(&program, true).unwrap();
    assert!(!prepared.is_execution_ready());
    assert!(prepared.context.process_effects.is_empty());
}

#[test]
fn existing_parent_frontiers_restore_disjoint_creation_scheduling_and_are_certified() {
    let root = workspace();
    fs::create_dir(root.path().join("left")).unwrap();
    fs::create_dir(root.path().join("right")).unwrap();
    let program = compile(root.path(), "\"a\" -> left/a\n\"b\" -> right/b\n");
    let mut prepared = preparation::prepare(&program, true).unwrap();
    assert_eq!(prepared.context.existing_parent_frontiers.len(), 2);
    assert!(prepared.graph.dependencies.is_empty());
    assert_eq!(prepared.graph.commutations.len(), 1);
    prepared.verify().unwrap();
    prepared.context.existing_parent_frontiers.clear();
    assert!(prepared.verify().is_err());
    let siblings = preparation::prepare(
        &compile(root.path(), "\"a\" -> left/a\n\"b\" -> left/b\n"),
        true,
    )
    .unwrap();
    assert!(!siblings.graph.dependencies.is_empty());
}

#[test]
fn a_private_program_write_and_unpinned_input_is_an_explicit_version_refinement() {
    use msync::hir::effects::{Access, AccessKind, Effects, FileResource, Resource, Scope};
    use msync::hir::graph::{ProcessEffects, RefinementKind};
    let root = workspace();
    fs::write(root.path().join("program"), "metadata placeholder").unwrap();
    fs::write(
        root.path().join("prompt"),
        "immutable input not acquired by planning",
    )
    .unwrap();
    let program = compile(
        root.path(),
        "p => $pwd::program()\n$pwd::prompt -> p::stdin\n",
    );
    let mut context = PlanningContext::default();
    let footprint = Effects {
        accesses: vec![Access {
            resource: Resource::File(FileResource::new(
                msync::endpoint::Location {
                    backend: Backend::Local,
                    path: root.path().to_string_lossy().into_owned(),
                },
                Scope::Tree,
            )),
            kind: AccessKind::Write,
        }],
        unknown_external: false,
    };
    context.process_effects.insert(
        hir::ProcessId(0),
        ProcessEffects::mediated(footprint, "test-view-premise", "private-epoch").unwrap(),
    );
    context.operation_facts.insert(
        program.body.instructions[1].id,
        OperationFacts {
            source: FileClass::Regular,
            destination: FileClass::Stream,
        },
    );
    let prepared = preparation::prepare_with_context(&program, true, context).unwrap();
    assert!(
        prepared
            .graph
            .refinements
            .iter()
            .any(|pending| pending.kind == RefinementKind::InputVersion)
    );
    assert!(!prepared.is_execution_ready());
    prepared.verify().unwrap();
}

#[test]
fn an_explicitly_retained_older_input_version_is_not_forwarded_by_path_alone() {
    use msync::hir::graph::VersionReadProof;
    use msync::transaction::filesystem::{Entry, SnapshotStore};
    let root = workspace();
    fs::write(root.path().join("a"), "new value").unwrap();
    fs::write(root.path().join("b"), "old retained value").unwrap();
    let mut store = SnapshotStore::new(&root.path().join("versions")).unwrap();
    let snapshot = store.capture(&root.path().join("b")).unwrap();
    let Entry::File { artifact, .. } = &snapshot.entries()[Path::new("")] else {
        panic!()
    };
    let program = compile(root.path(), "a -> b\nb -> output\n");
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        ..
    } = &program.body.instructions[1].kind
    else {
        panic!()
    };
    let mut context = PlanningContext::default();
    context.pinned_inputs.insert(
        program.body.instructions[1].id,
        VersionReadProof::pinned_file(
            location.clone(),
            "owned-test-version-store",
            artifact.path().to_string_lossy(),
        )
        .unwrap(),
    );
    let prepared = preparation::prepare_with_context(&program, true, context).unwrap();
    assert!(prepared.rewrites.is_empty());
    assert!(matches!(&prepared.program.body.instructions[1].kind,
        InstructionKind::Transfer { source: Input::File { location, .. }, .. }
        if Path::new(&location.path) == root.path().join("b")));
    prepared.verify().unwrap();
    drop(snapshot);
    store.cleanup().unwrap();
}

#[test]
fn http_is_not_requested_or_eliminated_when_preparing_a_finite_source() {
    let root = workspace();
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let program = compile(
        root.path(),
        &format!(
            "web => http://{}\nweb::resource -> output\nweb::resource -> output\n",
            listener.local_addr().unwrap()
        ),
    );
    let prepared = preparation::prepare(&program, true).unwrap();
    assert_eq!(prepared.program.body.instructions.len(), 2);
    assert!(prepared.rewrites.is_empty());
    assert_eq!(
        listener.accept().unwrap_err().kind(),
        std::io::ErrorKind::WouldBlock
    );
}

#[test]
fn runtime_epoch_types_override_host_absence_without_forging_new_files() {
    let root = workspace();
    let program = compile(root.path(), "pending -> output\n");
    let mut context = PlanningContext::default();
    context.operation_facts.insert(
        program.body.instructions[0].id,
        OperationFacts {
            source: FileClass::Regular,
            destination: FileClass::Missing,
        },
    );
    let prepared = preparation::prepare_with_context(&program, true, context).unwrap();
    assert!(prepared.is_execution_ready());
    assert!(!root.path().join("pending").exists());
}

#[test]
fn certificates_replay_after_sources_disappear_and_reject_modified_payloads_or_premises() {
    let root = workspace();
    fs::write(root.path().join("source"), "data").unwrap();
    let program = compile(root.path(), "source -> middle\nmiddle -> output\n");
    let mut prepared = preparation::prepare(&program, true).unwrap();
    fs::remove_file(root.path().join("source")).unwrap();
    prepared.verify().unwrap();
    prepared.context.operation_facts.clear();
    assert!(prepared.verify().is_err());
    let mut literal =
        preparation::prepare(&compile(root.path(), "\"data\" -> output\n"), true).unwrap();
    let InstructionKind::Transfer { source, .. } = &mut literal.program.body.instructions[0].kind
    else {
        panic!()
    };
    *source = Input::Literal("tampered".into());
    assert!(literal.verify().is_err());
}

#[cfg(unix)]
#[test]
fn fifo_inspection_never_opens_a_connection_or_claims_a_completion_only_stream_plan() {
    use std::os::unix::ffi::OsStrExt;
    let root = workspace();
    let fifo = root.path().join("input");
    let name = std::ffi::CString::new(fifo.as_os_str().as_bytes()).unwrap();
    assert_eq!(unsafe { libc::mkfifo(name.as_ptr(), 0o600) }, 0);
    let prepared = preparation::prepare(&compile(root.path(), "input -> output\n"), true).unwrap();
    assert!(!prepared.is_execution_ready());
    assert_eq!(
        prepared.facts.values().next().unwrap().source,
        FileClass::Stream
    );
}

fn evaluate(
    program: &hir::Program,
    root: &Path,
    initial: &BTreeMap<String, String>,
) -> BTreeMap<String, String> {
    let mut values = initial.clone();
    for node in &program.body.instructions {
        let InstructionKind::Transfer {
            source,
            destination: Output::File { location, .. },
        } = &node.kind
        else {
            panic!()
        };
        let text = match source {
            Input::Literal(text) => text.to_string(),
            Input::File { location, .. } => values[Path::new(&location.path)
                .strip_prefix(root)
                .unwrap()
                .to_str()
                .unwrap()]
            .clone(),
            _ => panic!(),
        };
        values.insert(
            Path::new(&location.path)
                .strip_prefix(root)
                .unwrap()
                .to_str()
                .unwrap()
                .into(),
            text,
        );
    }
    values
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(24))]
    #[test]
    fn fixed_point_preserves_the_independent_file_value_model(operations in prop::collection::vec((any::<bool>(), 0usize..3,0usize..2),1..9)) {
        let root = workspace();
        let names = ["a","b","c"];
        let initial = names.iter().map(|name| (name.to_string(), format!("initial-{name}"))).collect::<BTreeMap<_,_>>();
        for (name, value) in &initial { fs::write(root.path().join(name), value).unwrap(); }
        let mut source = String::new();
        for (literal,destination,offset) in operations {
            if literal { source.push_str(&format!("\"literal-{offset}\" -> {}\n",names[destination])); }
            else { source.push_str(&format!("{} -> {}\n", names[(destination+1+offset)%3],names[destination])); }
        }
        let program = compile(root.path(), &source);
        let prepared = preparation::prepare(&program,true).unwrap();
        prop_assert_eq!(evaluate(&program,root.path(),&initial),evaluate(&prepared.program,root.path(),&initial));
        prop_assert!(prepared.verify().is_ok());
    }
}
