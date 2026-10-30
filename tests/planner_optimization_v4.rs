//! File-value, permission and namespace controls for structured plan rewrites.

use std::fs;
use std::path::Path;
use std::sync::Arc;

use msync::compiler::{CompileContext, structured::Session};
use msync::hir::effects::{EndpointKey, EndpointRole, OperationFacts};
use msync::hir::graph::PlanningContext;
use msync::hir::{self, Input, InstructionKind};
use msync::plan::FileClass;
use msync::preparation::{self, RewriteKind};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("planner-opt-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn compile(root: &Path, source: &str) -> hir::Program {
    Session::new()
        .compile(
            &msync::language::parse(source).unwrap(),
            &CompileContext {
                pwd: root.to_owned(),
                source_file: None,
            },
        )
        .unwrap()
}

#[test]
fn adjacent_creation_keeps_the_final_source_site_and_namespace_obligation() {
    let root = workspace();
    fs::write(root.path().join("one"), b"one").unwrap();
    fs::write(root.path().join("two"), b"final two").unwrap();
    let program = compile(root.path(), "one -> out\ntwo -> out\n");
    let prepared = preparation::prepare(&program, true).unwrap();
    assert_eq!(prepared.program.body.instructions.len(), 1);
    let remaining = &prepared.program.body.instructions[0];
    assert_eq!(remaining.id, program.body.instructions[1].id);
    assert_eq!(remaining.span, program.body.instructions[1].span);
    assert_eq!(
        prepared.facts[&remaining.id].destination,
        FileClass::Missing
    );
    assert!(!prepared.context.host_unstable.contains(&EndpointKey {
        instruction: remaining.id,
        role: EndpointRole::Destination
    }));
    assert!(
        prepared
            .rewrites
            .iter()
            .any(|rewrite| matches!(rewrite.kind,
        RewriteKind::CoalesceCreation { overwritten_by } if overwritten_by == remaining.id))
    );
    prepared.verify().unwrap();
    let mut tampered = prepared.clone();
    tampered
        .context
        .operation_facts
        .get_mut(&remaining.id)
        .unwrap()
        .destination = FileClass::Regular;
    tampered.facts.get_mut(&remaining.id).unwrap().destination = FileClass::Regular;
    assert!(tampered.verify().is_err());
}

#[test]
fn regular_type_alone_does_not_prove_cross_endpoint_alias_separation() {
    let root = workspace();
    fs::write(root.path().join("one"), b"one").unwrap();
    let program = compile(
        root.path(),
        "one -> out\n$wsl::/not-contacted/config.toml -> out\n",
    );
    let context = PlanningContext {
        operation_facts: program
            .body
            .instructions
            .iter()
            .enumerate()
            .map(|(index, node)| {
                (
                    node.id,
                    OperationFacts {
                        source: FileClass::Regular,
                        destination: if index == 0 {
                            FileClass::Missing
                        } else {
                            FileClass::Regular
                        },
                    },
                )
            })
            .collect(),
        ..PlanningContext::default()
    };
    let prepared = preparation::prepare_with_context(&program, false, context).unwrap();
    assert_eq!(prepared.program.body.instructions.len(), 2);
    let node = &prepared.program.body.instructions[1];
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        ..
    } = &node.kind
    else {
        panic!()
    };
    assert_eq!(location.backend, msync::endpoint::Backend::Wsl);
    assert_eq!(prepared.facts[&node.id].destination, FileClass::Regular);
    assert!(
        !prepared
            .rewrites
            .iter()
            .any(|rewrite| matches!(rewrite.kind, RewriteKind::CoalesceCreation { .. }))
    );
    assert!(!root.path().join("out").exists());
    prepared.verify().unwrap();
}

#[test]
fn creation_is_not_moved_across_a_sibling_write_or_a_value_observer() {
    for middle in ["\"sibling\" -> sibling\n", "out -> observed\n"] {
        let root = workspace();
        fs::write(root.path().join("one"), b"one").unwrap();
        fs::write(root.path().join("two"), b"two").unwrap();
        let program = compile(root.path(), &format!("one -> out\n{middle}two -> out\n"));
        let prepared = preparation::prepare(&program, true).unwrap();
        assert!(
            prepared
                .program
                .body
                .instructions
                .iter()
                .any(|node| node.id == program.body.instructions[0].id)
        );
        assert!(
            !prepared
                .rewrites
                .iter()
                .any(|rewrite| matches!(rewrite.kind, RewriteKind::CoalesceCreation { .. }))
        );
        prepared.verify().unwrap();
    }
}

#[test]
fn coalesced_creator_is_not_later_erased_across_a_sibling_namespace_write() {
    let root = workspace();
    for name in ["a", "b", "c"] {
        fs::write(root.path().join(name), name).unwrap();
    }
    let program = compile(
        root.path(),
        "a -> out\nb -> out\n\"sibling\" -> sibling\nc -> out\n",
    );
    let prepared = preparation::prepare(&program, true).unwrap();
    let ids = prepared
        .program
        .body
        .instructions
        .iter()
        .map(|node| node.id)
        .collect::<Vec<_>>();
    assert_eq!(
        ids,
        vec![
            hir::InstructionId(1),
            hir::InstructionId(2),
            hir::InstructionId(3)
        ]
    );
    assert_eq!(
        prepared.facts[&hir::InstructionId(1)].destination,
        FileClass::Missing
    );
    assert_eq!(
        prepared.facts[&hir::InstructionId(3)].destination,
        FileClass::Regular
    );
    prepared.verify().unwrap();
}

#[test]
fn repeated_adjacent_fusion_replays_creation_context_to_the_final_source_site() {
    let root = workspace();
    for name in ["a", "b", "c"] {
        fs::write(root.path().join(name), name).unwrap();
    }
    let program = compile(root.path(), "a -> out\nb -> out\nc -> out\n");
    let prepared = preparation::prepare(&program, true).unwrap();
    let [remaining] = prepared.program.body.instructions.as_slice() else {
        panic!()
    };
    assert_eq!(remaining.id, program.body.instructions[2].id);
    assert_eq!(remaining.span, program.body.instructions[2].span);
    assert_eq!(
        prepared.facts[&remaining.id].destination,
        FileClass::Missing
    );
    assert_eq!(
        prepared.context.existing_parent_frontiers[&remaining.id],
        prepared.context.existing_parent_frontiers[&program.body.instructions[0].id]
    );
    assert!(!prepared.context.host_unstable.contains(&EndpointKey {
        instruction: remaining.id,
        role: EndpointRole::Destination
    }));
    assert_eq!(
        prepared
            .rewrites
            .iter()
            .filter(|rewrite| matches!(rewrite.kind, RewriteKind::CoalesceCreation { .. }))
            .count(),
        2
    );
    prepared.verify().unwrap();
}

fn readonly_outcome(
    base: &Path,
    name: &str,
    existing: bool,
    same_source: bool,
    optimized: bool,
) -> (bool, Vec<u8>, bool) {
    let root = base.join(name);
    fs::create_dir(&root).unwrap();
    let first = root.join("first");
    fs::write(&first, b"readonly producer").unwrap();
    fs::write(root.join("second"), b"writable successor").unwrap();
    if existing {
        fs::write(root.join("out"), b"before").unwrap();
    }
    let writable = fs::metadata(&first).unwrap().permissions();
    let mut readonly = writable.clone();
    readonly.set_readonly(true);
    fs::set_permissions(&first, readonly).unwrap();
    let source = if same_source {
        "first -> out\nfirst -> out\n"
    } else {
        "first -> out\nsecond -> out\n"
    };
    let program = compile(&root, source);
    let prepared = preparation::prepare(&program, true).unwrap();
    let result = msync::execution::execute(
        if optimized {
            &prepared.program
        } else {
            &program
        },
        &msync::execution::Options::new(base.join(format!("scratch-{name}"))),
    );
    let output = root.join("out");
    let bytes = fs::read(&output).unwrap();
    let readonly = fs::metadata(&output).unwrap().permissions().readonly();
    fs::set_permissions(&first, writable.clone()).unwrap();
    fs::set_permissions(&output, writable).unwrap();
    if optimized {
        assert_eq!(prepared.program.body.instructions.len(), 2);
        prepared.verify().unwrap();
    }
    (result.is_ok(), bytes, readonly)
}

#[test]
fn readonly_producers_never_turn_a_later_overwrite_failure_into_success() {
    for existing in [false, true] {
        for same_source in [false, true] {
            let root = workspace();
            let reference =
                readonly_outcome(root.path(), "reference", existing, same_source, false);
            let optimized = readonly_outcome(root.path(), "optimized", existing, same_source, true);
            assert_eq!(
                reference, optimized,
                "existing={existing}, same_source={same_source}"
            );
            assert!(!reference.0);
            assert_eq!(reference.1, b"readonly producer");
            assert!(reference.2);
        }
    }
}

#[test]
fn rewrite_epoch_storage_is_shared_without_weakening_value_based_replay() {
    let root = workspace();
    fs::write(root.path().join("out"), b"before").unwrap();
    for name in ["one", "two", "three"] {
        fs::write(root.path().join(name), name).unwrap();
    }
    let program = compile(root.path(), "one -> out\ntwo -> out\nthree -> out\n");
    let prepared = preparation::prepare(&program, true).unwrap();
    assert_eq!(prepared.rewrites.len(), 2);
    assert!(Arc::ptr_eq(
        &prepared.rewrites[0].epoch,
        &prepared.rewrites[1].epoch
    ));
    let mut tampered = prepared.clone();
    Arc::make_mut(&mut tampered.rewrites[0].epoch)[0] = hir::InstructionId(usize::MAX);
    assert!(tampered.verify().is_err());
    prepared.verify().unwrap();
}

#[tokio::test]
async fn optimized_missing_target_executes_one_assignment_with_final_metadata() {
    let root = workspace();
    let first = root.path().join("first");
    let second = root.path().join("second");
    fs::write(&first, b"old input").unwrap();
    fs::write(&second, b"final payload").unwrap();
    let modified = std::time::UNIX_EPOCH + std::time::Duration::from_secs(978307200);
    fs::File::options()
        .write(true)
        .open(&second)
        .unwrap()
        .set_times(fs::FileTimes::new().set_modified(modified))
        .unwrap();
    let expected_mtime = fs::metadata(&second).unwrap().modified().unwrap();
    let prepared =
        preparation::prepare(&compile(root.path(), "first -> out\nsecond -> out\n"), true).unwrap();
    let mut runtime = msync::execution::RuntimeSession::new(&msync::execution::Options::new(
        root.path().join("scratch"),
    ))
    .unwrap();
    let report = runtime.execute_prepared(&prepared).await.unwrap();
    assert_eq!(report.assignments, 1);
    assert_eq!(report.assigned_files, 1);
    assert_eq!(fs::read(root.path().join("out")).unwrap(), b"final payload");
    assert_eq!(
        fs::metadata(root.path().join("out"))
            .unwrap()
            .modified()
            .unwrap(),
        expected_mtime
    );
    assert_eq!(
        fs::metadata(root.path().join("out")).unwrap().permissions(),
        fs::metadata(second).unwrap().permissions()
    );
    runtime.shutdown().await.unwrap();
}
