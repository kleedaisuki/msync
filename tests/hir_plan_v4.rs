use std::sync::Arc;

use msync::compiler::{Backend, Location};
use msync::hir::effects::{
    AccessKind, EndpointKey, EndpointRole, FifoEndpointProof, FifoRendezvousProof, FileResource,
    OperationFacts, Resource, Scope,
};
use msync::hir::graph::{self, Reason, Wait};
use msync::hir::{
    Argument, Input, Instruction, InstructionId, InstructionKind, Output, ProcessId, Program,
    ReadPort, Region,
};
use msync::language::DestinationMode;
use msync::plan::FileClass;
use msync::syntax::Span;
use proptest::prelude::*;

fn location(path: &str) -> Location {
    Location {
        backend: Backend::Local,
        path: path.into(),
    }
}

fn node(id: usize, kind: InstructionKind) -> Instruction {
    Instruction {
        id: InstructionId(id),
        kind,
        span: Span::default(),
        expansion: Vec::new(),
    }
}

fn literal(id: usize, path: &str) -> Instruction {
    node(
        id,
        InstructionKind::Transfer {
            source: Input::Literal(Arc::from("UTF-8 ✨")),
            destination: Output::File {
                location: location(path),
                mode: DestinationMode::Exact,
            },
        },
    )
}

fn copy(id: usize, source: &str, destination: &str) -> Instruction {
    node(
        id,
        InstructionKind::Transfer {
            source: Input::File {
                location: location(source),
                pattern: None,
            },
            destination: Output::File {
                location: location(destination),
                mode: DestinationMode::Exact,
            },
        },
    )
}

fn spawn(id: usize, process: usize) -> Instruction {
    node(
        id,
        InstructionKind::Spawn {
            process: ProcessId(process),
            executable: location("/usr/bin/python3"),
            arguments: vec![Argument::Text("worker.py".into())],
            cwd: "/workspace".into(),
        },
    )
}

fn port(id: usize, process: usize, port: ReadPort, path: &str) -> Instruction {
    node(
        id,
        InstructionKind::Transfer {
            source: Input::Port {
                process: ProcessId(process),
                port,
            },
            destination: Output::File {
                location: location(path),
                mode: DestinationMode::Exact,
            },
        },
    )
}

fn program(instructions: Vec<Instruction>) -> Program {
    Program {
        body: Region { instructions },
    }
}

fn finite_context(program: &Program) -> graph::PlanningContext {
    fn observe(region: &Region, context: &mut graph::PlanningContext) {
        for instruction in &region.instructions {
            if let InstructionKind::Transfer { .. } = &instruction.kind {
                context.operation_facts.insert(
                    instruction.id,
                    OperationFacts {
                        source: FileClass::Regular,
                        destination: FileClass::Regular,
                    },
                );
            }
        }
    }
    let mut context = graph::PlanningContext::default();
    observe(&program.body, &mut context);
    context
}

fn fifo_pair(writer: usize, reader: usize) -> FifoRendezvousProof {
    let endpoint = |instruction, role| {
        FifoEndpointProof::verified_fifo(
            EndpointKey {
                instruction: InstructionId(instruction),
                role,
            },
            location("/managed/fifo"),
            "native-fifo-fixture",
            "device-1:inode-42",
        )
        .unwrap()
    };
    FifoRendezvousProof::connect(
        endpoint(writer, EndpointRole::Destination),
        endpoint(reader, EndpointRole::Source),
    )
    .unwrap()
}

#[test]
fn native_fifo_identity_preserves_alias_wave_and_unpaired_consumer_order() {
    let endpoint = |instruction, role, path| {
        FifoEndpointProof::verified_fifo(
            EndpointKey {
                instruction: InstructionId(instruction),
                role,
            },
            location(path),
            "native-fifo-fixture",
            "same-device-and-inode",
        )
        .unwrap()
    };
    let ir = program(vec![
        literal(0, "/pipes/a"),
        copy(1, "/pipes/b", "/first"),
        literal(2, "/pipes/c"),
        copy(3, "/pipes/d", "/second"),
    ]);
    let mut context = finite_context(&ir);
    for writer in [0, 2] {
        context
            .operation_facts
            .get_mut(&InstructionId(writer))
            .unwrap()
            .destination = FileClass::Stream;
    }
    for reader in [1, 3] {
        context
            .operation_facts
            .get_mut(&InstructionId(reader))
            .unwrap()
            .source = FileClass::Stream;
    }
    context.physical_rendezvous = vec![
        FifoRendezvousProof::connect(
            endpoint(0, EndpointRole::Destination, "/pipes/a"),
            endpoint(1, EndpointRole::Source, "/pipes/b"),
        )
        .unwrap(),
        FifoRendezvousProof::connect(
            endpoint(2, EndpointRole::Destination, "/pipes/c"),
            endpoint(3, EndpointRole::Source, "/pipes/d"),
        )
        .unwrap(),
    ];
    let planned = graph::build_with_context(&ir, &context);
    assert!(planned.issues.is_empty(), "{:?}", planned.issues);
    for (before, after, wait) in [
        (0, 1, Wait::Admitted),
        (2, 3, Wait::Admitted),
        (0, 2, Wait::Completion),
        (1, 2, Wait::Completion),
        (1, 3, Wait::Completion),
    ] {
        assert!(
            planned
                .dependencies
                .iter()
                .any(|edge| edge.before == InstructionId(before)
                    && edge.after == InstructionId(after)
                    && edge.wait == wait),
            "missing {before}->{after} {wait:?}"
        );
    }
    assert!(planned.verify_with_context(&ir, &context).is_ok());

    let readers = program(vec![
        copy(0, "/pipes/a", "/first"),
        copy(1, "/pipes/b", "/second"),
    ]);
    let mut context = finite_context(&readers);
    for (id, path) in [(0, "/pipes/a"), (1, "/pipes/b")] {
        context
            .operation_facts
            .get_mut(&InstructionId(id))
            .unwrap()
            .source = FileClass::Stream;
        let proof = endpoint(id, EndpointRole::Source, path);
        context.physical_fifos.insert(proof.key(), proof);
    }
    let planned = graph::build_with_context(&readers, &context);
    assert!(
        planned
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(1)
                && edge.wait == Wait::Completion)
    );
    assert!(planned.commutations.is_empty());
    assert!(planned.verify_with_context(&readers, &context).is_ok());
}

#[test]
fn native_fifo_opposite_endpoints_rendezvous_after_admission_not_eof() {
    for reversed in [false, true] {
        let mut instructions = vec![
            literal(0, "/managed/fifo"),
            copy(1, "/managed/fifo", "/managed/output"),
        ];
        if reversed {
            instructions.reverse();
        }
        let ir = program(instructions);
        let mut context = finite_context(&ir);
        context
            .operation_facts
            .get_mut(&InstructionId(0))
            .unwrap()
            .destination = FileClass::Stream;
        context
            .operation_facts
            .get_mut(&InstructionId(1))
            .unwrap()
            .source = FileClass::Stream;
        let strict = graph::build_with_context(&ir, &context);
        assert!(
            strict
                .dependencies
                .iter()
                .any(|edge| edge.wait == Wait::Completion)
        );
        context.physical_rendezvous.push(fifo_pair(0, 1));
        let rendezvous = graph::build_with_context(&ir, &context);
        assert!(rendezvous.issues.is_empty(), "{:?}", rendezvous.issues);
        assert_eq!(rendezvous.dependencies.len(), 1);
        assert_eq!(rendezvous.dependencies[0].wait, Wait::Admitted);
        assert_eq!(rendezvous.physical_rendezvous, context.physical_rendezvous);
        assert!(rendezvous.verify_with_context(&ir, &context).is_ok());
        let mut tampered = rendezvous.clone();
        tampered.physical_rendezvous.clear();
        assert!(tampered.verify_with_context(&ir, &context).is_err());
    }
}

#[test]
fn fifo_rendezvous_does_not_waive_other_file_hazards_or_same_side_consumers() {
    let ir = program(vec![
        copy(0, "/managed/value", "/managed/fifo"),
        copy(1, "/managed/fifo", "/managed/value"),
        copy(2, "/managed/fifo", "/else/output"),
    ]);
    let mut context = finite_context(&ir);
    context
        .operation_facts
        .get_mut(&InstructionId(0))
        .unwrap()
        .destination = FileClass::Stream;
    for id in [1, 2] {
        context
            .operation_facts
            .get_mut(&InstructionId(id))
            .unwrap()
            .source = FileClass::Stream;
    }
    context.physical_rendezvous.push(fifo_pair(0, 1));
    let planned = graph::build_with_context(&ir, &context);
    assert!(
        planned
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(1)
                && edge.wait == Wait::Admitted)
    );
    assert!(
        planned
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(1)
                && edge.wait == Wait::Completion)
    );
    assert!(
        planned
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(1)
                && edge.after == InstructionId(2)
                && edge.wait == Wait::Completion)
    );
}

#[test]
fn fifo_rendezvous_rejects_stale_kinds_unstable_endpoints_and_mismatched_native_identity() {
    let ir = program(vec![
        literal(0, "/managed/fifo"),
        copy(1, "/managed/fifo", "/managed/output"),
    ]);
    let mut context = finite_context(&ir);
    context.physical_rendezvous.push(fifo_pair(0, 1));
    assert!(!graph::build_with_context(&ir, &context).issues.is_empty());
    context
        .operation_facts
        .get_mut(&InstructionId(0))
        .unwrap()
        .destination = FileClass::Stream;
    context
        .operation_facts
        .get_mut(&InstructionId(1))
        .unwrap()
        .source = FileClass::Stream;
    context.host_unstable.insert(EndpointKey {
        instruction: InstructionId(0),
        role: EndpointRole::Destination,
    });
    assert!(!graph::build_with_context(&ir, &context).issues.is_empty());
    let writer = fifo_pair(0, 1).writer().clone();
    let reader = FifoEndpointProof::verified_fifo(
        EndpointKey {
            instruction: InstructionId(1),
            role: EndpointRole::Source,
        },
        location("/managed/fifo"),
        "native-fifo-fixture",
        "different-inode",
    )
    .unwrap();
    assert!(FifoRendezvousProof::connect(writer.clone(), reader).is_err());
    assert!(FifoRendezvousProof::connect(writer.clone(), writer).is_err());
}

#[test]
fn repeated_fifo_waves_keep_same_side_order_and_reject_contracted_completion_cycles() {
    for readers_first in [false, true] {
        let mut instructions = vec![
            literal(0, "/managed/fifo"),
            copy(1, "/managed/fifo", "/a"),
            literal(2, "/managed/fifo"),
            copy(3, "/managed/fifo", "/b"),
        ];
        if readers_first {
            instructions.swap(0, 1);
            instructions.swap(1, 3);
        }
        let ir = program(instructions);
        let mut context = finite_context(&ir);
        for id in [0, 2] {
            context
                .operation_facts
                .get_mut(&InstructionId(id))
                .unwrap()
                .destination = FileClass::Stream;
        }
        for id in [1, 3] {
            context
                .operation_facts
                .get_mut(&InstructionId(id))
                .unwrap()
                .source = FileClass::Stream;
        }
        context.physical_rendezvous = vec![fifo_pair(0, 1), fifo_pair(2, 3)];
        let planned = graph::build_with_context(&ir, &context);
        if readers_first {
            assert!(
                planned
                    .issues
                    .iter()
                    .any(|issue| issue.message.contains("completion-dependency cycle")),
                "{:?}",
                planned.issues
            );
        } else {
            assert!(planned.issues.is_empty(), "{:?}", planned.issues);
            assert!(
                planned
                    .dependencies
                    .iter()
                    .any(|edge| edge.before == InstructionId(0)
                        && edge.after == InstructionId(2)
                        && edge.wait == Wait::Completion)
            );
            assert!(
                planned
                    .dependencies
                    .iter()
                    .any(|edge| edge.before == InstructionId(1)
                        && edge.after == InstructionId(3)
                        && edge.wait == Wait::Completion)
            );
        }
    }
}

#[test]
fn fifo_group_rejects_transitive_file_hazard_before_io_starts() {
    let ir = program(vec![
        copy(0, "/managed/a", "/managed/fifo"),
        copy(1, "/managed/b", "/managed/a"),
        copy(2, "/managed/fifo", "/managed/b"),
    ]);
    let mut context = finite_context(&ir);
    context
        .operation_facts
        .get_mut(&InstructionId(0))
        .unwrap()
        .destination = FileClass::Stream;
    context
        .operation_facts
        .get_mut(&InstructionId(2))
        .unwrap()
        .source = FileClass::Stream;
    context.physical_rendezvous.push(fifo_pair(0, 2));
    let planned = graph::build_with_context(&ir, &context);
    assert!(
        planned
            .issues
            .iter()
            .any(|issue| issue.message.contains("completion-dependency cycle")),
        "{:?}",
        planned.issues
    );
    assert!(planned.verify_with_context(&ir, &context).is_err());
}

#[test]
fn a_closed_native_fifo_cycle_is_rejected_without_an_independent_source() {
    let ir = program(vec![
        copy(0, "/managed/a", "/managed/b"),
        copy(1, "/managed/b", "/managed/a"),
    ]);
    let mut context = finite_context(&ir);
    for facts in context.operation_facts.values_mut() {
        facts.source = FileClass::Stream;
        facts.destination = FileClass::Stream;
    }
    let endpoint = |instruction, role, path, identity| {
        FifoEndpointProof::verified_fifo(
            EndpointKey {
                instruction: InstructionId(instruction),
                role,
            },
            location(path),
            "native-fifo-fixture",
            identity,
        )
        .unwrap()
    };
    context.physical_rendezvous = vec![
        FifoRendezvousProof::connect(
            endpoint(0, EndpointRole::Destination, "/managed/b", "fifo-b"),
            endpoint(1, EndpointRole::Source, "/managed/b", "fifo-b"),
        )
        .unwrap(),
        FifoRendezvousProof::connect(
            endpoint(1, EndpointRole::Destination, "/managed/a", "fifo-a"),
            endpoint(0, EndpointRole::Source, "/managed/a", "fifo-a"),
        )
        .unwrap(),
    ];
    let planned = graph::build_with_context(&ir, &context);
    assert!(
        planned
            .issues
            .iter()
            .any(|issue| issue.message.contains("closed FIFO cycle")),
        "{:?}",
        planned.issues
    );
}

#[test]
fn literal_writes_have_no_read_effect_and_disjoint_paths_commute() {
    let ir = program(vec![literal(0, "/workspace/a"), literal(1, "/workspace/b")]);
    let context = finite_context(&ir);
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.dependencies.is_empty());
    assert_eq!(graph.commutations.len(), 1);
    assert!(
        graph.nodes[0]
            .effects
            .accesses
            .iter()
            .all(|access| access.kind == AccessKind::Write)
    );
    assert_eq!(graph.nodes[0].effects.accesses[0].kind, AccessKind::Write);
    assert!(graph.verify_with_context(&ir, &context).is_ok());
    assert!(graph.is_execution_ready());
}

fn permitted_orders(graph: &graph::PlanGraph) -> Vec<Vec<usize>> {
    fn visit(graph: &graph::PlanGraph, order: &mut Vec<usize>, result: &mut Vec<Vec<usize>>) {
        if order.len() == graph.nodes.len() {
            result.push(order.clone());
            return;
        }
        for id in 0..graph.nodes.len() {
            if order.contains(&id)
                || graph
                    .dependencies
                    .iter()
                    .any(|edge| edge.after == InstructionId(id) && !order.contains(&edge.before.0))
            {
                continue;
            }
            order.push(id);
            visit(graph, order, result);
            order.pop();
        }
    }
    assert!(
        graph
            .dependencies
            .iter()
            .all(|edge| edge.wait == Wait::Completion)
    );
    let mut result = Vec::new();
    visit(graph, &mut Vec::new(), &mut result);
    result
}

#[test]
fn sibling_creations_commute_when_only_incidental_parent_mtime_differs() {
    let ir = program(vec![
        literal(0, "/shared/new-a"),
        literal(1, "/shared/new-b"),
    ]);
    let mut context = finite_context(&ir);
    for id in [0, 1] {
        context
            .operation_facts
            .get_mut(&InstructionId(id))
            .unwrap()
            .destination = FileClass::Missing;
        context
            .existing_parent_frontiers
            .insert(InstructionId(id), location("/shared"));
    }
    let graph = graph::build_with_context(&ir, &context);
    fn model(order: &[usize]) -> std::collections::BTreeMap<usize, &'static str> {
        order.iter().map(|id| (*id, "new contents")).collect()
    }
    let orders = permitted_orders(&graph);
    assert_eq!(orders.len(), 2);
    for order in orders {
        assert_eq!(model(&order), model(&[0, 1]));
    }
    for (node, expected) in graph.nodes.iter().zip(["/shared/new-a", "/shared/new-b"]) {
        assert!(node.effects.accesses.iter().any(|access| matches!(&access.resource, Resource::DirectoryEntry(entry) if entry.location.path == expected)));
    }
    assert!(graph.is_execution_ready());
}

#[test]
fn a_shared_missing_ancestor_is_a_real_namespace_dependency() {
    let ir = program(vec![
        literal(0, "/shared/new/a"),
        literal(1, "/shared/new/b"),
    ]);
    let mut context = finite_context(&ir);
    for id in [0, 1] {
        context
            .operation_facts
            .get_mut(&InstructionId(id))
            .unwrap()
            .destination = FileClass::Missing;
        context
            .existing_parent_frontiers
            .insert(InstructionId(id), location("/shared"));
    }
    let graph = graph::build_with_context(&ir, &context);
    assert_eq!(permitted_orders(&graph), vec![vec![0, 1]]);
    assert!(graph.dependencies.iter().any(|edge| edge.reasons.iter().any(|reason| matches!(reason, Reason::Hazard(conflict) if matches!(&conflict.before.resource, Resource::DirectoryEntry(entry) if entry.location.path == "/shared/new")))));
}

#[test]
fn directory_entry_changes_conflict_with_enumeration_and_explicit_directory_metadata() {
    let child = Resource::DirectoryEntry(FileResource::new(location("/shared/new"), Scope::Exact));
    let other =
        Resource::DirectoryEntry(FileResource::new(location("/shared/other"), Scope::Exact));
    let entries = Resource::DirectoryEntries(FileResource::new(location("/shared"), Scope::Exact));
    let metadata =
        Resource::DirectoryMetadata(FileResource::new(location("/shared"), Scope::Exact));
    assert!(!child.may_overlap(&other));
    assert!(child.may_overlap(&entries));
    assert!(entries.may_overlap(&child));
    assert!(child.may_overlap(&metadata));
    assert!(metadata.may_overlap(&child));
}

#[test]
fn entry_creation_does_not_alias_existing_sibling_file_bytes() {
    let ir = program(vec![
        literal(0, "/shared/new"),
        copy(1, "/shared/existing", "/elsewhere/output"),
    ]);
    let mut context = finite_context(&ir);
    context
        .operation_facts
        .get_mut(&InstructionId(0))
        .unwrap()
        .destination = FileClass::Missing;
    context
        .existing_parent_frontiers
        .insert(InstructionId(0), location("/shared"));
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.dependencies.is_empty());
    assert_eq!(permitted_orders(&graph).len(), 2);
    assert!(graph.is_execution_ready());
}

#[test]
fn directory_tree_reads_observe_namespace_metadata_changes() {
    let ir = program(vec![
        literal(0, "/shared/new"),
        copy(1, "/shared", "/tree-output"),
    ]);
    let mut context = finite_context(&ir);
    context
        .operation_facts
        .get_mut(&InstructionId(0))
        .unwrap()
        .destination = FileClass::Missing;
    context.operation_facts.insert(
        InstructionId(1),
        OperationFacts {
            source: FileClass::Directory,
            destination: FileClass::Missing,
        },
    );
    let graph = graph::build_with_context(&ir, &context);
    assert_eq!(permitted_orders(&graph), vec![vec![0, 1]]);
    assert!(graph.dependencies.iter().any(|edge| edge.reasons.iter().any(|reason| matches!(reason, Reason::Hazard(conflict) if matches!(conflict.before.resource, Resource::DirectoryEntry(_)) && conflict.after.kind == AccessKind::Read))));
}

#[test]
fn unknown_repeated_source_reads_are_consuming_until_replayability_is_proved() {
    let ir = program(vec![
        copy(0, "/physical-source", "/first"),
        copy(1, "/physical-source", "/second"),
    ]);
    let mut context = finite_context(&ir);
    for facts in context.operation_facts.values_mut() {
        facts.source = FileClass::Unknown;
    }
    let unrefined = graph::build_with_context(&ir, &context);
    fn consuming_model(order: &[usize]) -> [Vec<u8>; 2] {
        let mut unread = b"stream contents".to_vec();
        let mut outputs = [Vec::new(), Vec::new()];
        for id in order {
            outputs[*id] = std::mem::take(&mut unread);
        }
        outputs
    }
    assert_ne!(consuming_model(&[0, 1]), consuming_model(&[1, 0]));
    for order in permitted_orders(&unrefined) {
        assert_eq!(consuming_model(&order), consuming_model(&[0, 1]));
    }
    assert!(!unrefined.is_execution_ready());
    assert!(
        unrefined.nodes[0]
            .effects
            .accesses
            .iter()
            .any(|access| access.kind == AccessKind::Consume)
    );
    let finite = graph::build_with_context(&ir, &finite_context(&ir));
    assert_eq!(permitted_orders(&finite).len(), 2);
    assert!(finite.is_execution_ready());
    assert!(
        unrefined
            .verify_with_context(&ir, &finite_context(&ir))
            .is_err()
    );
}

#[test]
fn physical_streams_require_the_physical_scheduler_even_with_type_facts() {
    let mut instruction = copy(0, "/source", "/sink");
    if let InstructionKind::Transfer {
        source: Input::File { location, .. },
        destination: Output::File { location: sink, .. },
    } = &mut instruction.kind
    {
        location.backend = Backend::Tcp {
            host: "127.0.0.1".into(),
            port: 9000,
        };
        sink.backend = Backend::Tcp {
            host: "127.0.0.1".into(),
            port: 9001,
        };
    }
    let ir = program(vec![instruction]);
    let context = finite_context(&ir);
    let graph = graph::build_with_context(&ir, &context);
    assert!(
        graph.nodes[0]
            .effects
            .accesses
            .iter()
            .any(|access| access.kind == AccessKind::Consume)
    );
    assert!(
        !graph.nodes[0]
            .effects
            .accesses
            .iter()
            .any(|access| matches!(access.resource, Resource::DirectoryMetadata(_)))
    );
    assert_eq!(
        graph
            .refinements
            .iter()
            .filter(
                |requirement| requirement.kind == graph::RefinementKind::PhysicalStreamScheduling
            )
            .count(),
        2
    );
    assert!(!graph.is_execution_ready());
    assert!(
        graph.verify_with_context(&ir, &context).is_ok(),
        "a conservative certificate is not an executable physical-stream schedule"
    );
}

#[test]
fn type_premises_are_retained_and_unknown_destinations_are_not_execution_ready() {
    let ir = program(vec![literal(0, "/unknown")]);
    let unrefined = graph::build(&ir);
    assert!(
        unrefined
            .refinements
            .iter()
            .any(|requirement| requirement.kind == graph::RefinementKind::DestinationType)
    );
    assert!(!unrefined.is_execution_ready());
    let context = finite_context(&ir);
    let finite = graph::build_with_context(&ir, &context);
    assert_eq!(finite.operation_facts, context.operation_facts);
    assert!(finite.is_execution_ready());
    assert!(finite.verify(&ir).is_err());
}

#[test]
fn unknown_container_insertions_change_its_entry_set_and_kind_replacement_changes_the_binding() {
    let mut container = literal(0, "/root/container");
    if let InstructionKind::Transfer {
        destination: Output::File { mode, .. },
        ..
    } = &mut container.kind
    {
        *mode = DestinationMode::Container;
    }
    let ir = program(vec![container]);
    let mut context = finite_context(&ir);
    context
        .operation_facts
        .get_mut(&InstructionId(0))
        .unwrap()
        .destination = FileClass::Directory;
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.nodes[0].effects.accesses.iter().any(|access| access.kind == AccessKind::Write && matches!(&access.resource, Resource::DirectoryEntries(entries) if entries.location.path == "/root/container")));

    let mut replacement = copy(0, "/source-tree", "/root/file-becomes-tree");
    if let InstructionKind::Transfer {
        destination: Output::File { mode, .. },
        ..
    } = &mut replacement.kind
    {
        *mode = DestinationMode::ReplaceTree;
    }
    let ir = program(vec![replacement]);
    context.operation_facts.insert(
        InstructionId(0),
        OperationFacts {
            source: FileClass::Directory,
            destination: FileClass::Regular,
        },
    );
    context
        .existing_parent_frontiers
        .insert(InstructionId(0), location("/root"));
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.nodes[0].effects.accesses.iter().any(|access| access.kind == AccessKind::Write && matches!(&access.resource, Resource::DirectoryEntry(entry) if entry.location.path == "/root/file-becomes-tree")));
}

#[test]
fn selectors_retain_tree_predicates_and_explicit_entry_refinement() {
    let mut selection = copy(0, "/source-tree", "/container");
    if let InstructionKind::Transfer {
        source: Input::File { pattern, .. },
        destination: Output::File { mode, .. },
    } = &mut selection.kind
    {
        *pattern = Some(".*".into());
        *mode = DestinationMode::Container;
    }
    let ir = program(vec![selection]);
    let mut context = finite_context(&ir);
    context.operation_facts.insert(
        InstructionId(0),
        OperationFacts {
            source: FileClass::Directory,
            destination: FileClass::Directory,
        },
    );
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.nodes[0].effects.accesses.iter().any(|access| access.kind == AccessKind::Read && matches!(&access.resource, Resource::File(resource) if resource.scope == Scope::Tree)));
    assert!(
        graph
            .refinements
            .iter()
            .any(|requirement| requirement.kind == graph::RefinementKind::SelectorExpansion)
    );
    assert!(!graph.is_execution_ready());
}

#[test]
fn finite_http_type_facts_do_not_claim_gets_are_replayable_file_reads() {
    let mut instructions = vec![copy(0, "/counter", "/a"), copy(1, "/counter", "/b")];
    for instruction in &mut instructions {
        if let InstructionKind::Transfer {
            source: Input::File { location, .. },
            ..
        } = &mut instruction.kind
        {
            location.backend = Backend::Http {
                base: "https://example.invalid/api/".into(),
            };
        }
    }
    let ir = program(instructions);
    let context = finite_context(&ir);
    let graph = graph::build_with_context(&ir, &context);
    assert_eq!(permitted_orders(&graph), vec![vec![0, 1]]);
    assert!(
        graph.nodes[0]
            .effects
            .accesses
            .iter()
            .any(|access| access.kind == AccessKind::Consume
                && matches!(access.resource, Resource::HttpRequests(_)))
    );
    fn request_results(order: &[usize]) -> [usize; 2] {
        let mut outputs = [0, 0];
        for (counter, id) in order.iter().enumerate() {
            outputs[*id] = counter;
        }
        outputs
    }
    assert_ne!(request_results(&[0, 1]), request_results(&[1, 0]));
    for order in permitted_orders(&graph) {
        assert_eq!(request_results(&order), request_results(&[0, 1]));
    }
}

#[test]
fn different_http_paths_at_one_origin_remain_observably_sequenced() {
    let mut first = copy(0, "/increment", "/a");
    let mut second = copy(1, "/read", "/b");
    for (instruction, base) in [
        (&mut first, "https://example.invalid/api/"),
        (&mut second, "https://example.invalid/other/"),
    ] {
        if let InstructionKind::Transfer {
            source: Input::File { location, .. },
            ..
        } = &mut instruction.kind
        {
            location.backend = Backend::Http { base: base.into() };
        }
    }
    let ir = program(vec![first, second]);
    let graph = graph::build_with_context(&ir, &finite_context(&ir));
    assert_eq!(permitted_orders(&graph), vec![vec![0, 1]]);
    let pinned = program(vec![
        copy(0, "/immutable-artifact", "/a"),
        copy(1, "/immutable-artifact", "/b"),
    ]);
    let graph = graph::build_with_context(&pinned, &finite_context(&pinned));
    assert_eq!(
        permitted_orders(&graph).len(),
        2,
        "the fetched local version, not a finite HTTP HEAD classification, supplies replayability"
    );
}

#[test]
fn hierarchy_selectors_and_explicit_replacement_keep_tree_hazards() {
    let mut replacement = literal(0, "/workspace/tree");
    if let InstructionKind::Transfer {
        destination: Output::File { mode, .. },
        ..
    } = &mut replacement.kind
    {
        *mode = DestinationMode::ReplaceTree;
    }
    let ir = program(vec![
        replacement,
        copy(1, "/workspace/tree/child", "/output"),
    ]);
    let graph = graph::build(&ir);
    assert_eq!(graph.dependencies.len(), 1);
    assert!(
        matches!(&graph.nodes[0].effects.accesses[0].resource, Resource::File(resource) if resource.scope == Scope::Tree)
    );
    assert!(
        graph.dependencies[0]
            .reasons
            .iter()
            .any(|reason| matches!(reason, Reason::Hazard(_)))
    );
}

#[test]
fn normalization_does_not_need_io_and_native_wsl_aliases_stay_conservative() {
    let left = FileResource::new(location("D:\\work\\a\\..\\B"), Scope::Exact);
    let same = FileResource::new(location("d:/work/b"), Scope::Exact);
    let sibling = FileResource::new(location("D:/work/c"), Scope::Exact);
    let wsl = FileResource::new(
        Location {
            backend: Backend::Wsl,
            path: "/mnt/d/work/c".into(),
        },
        Scope::Exact,
    );
    assert!(left.may_overlap(&same));
    assert!(!left.may_overlap(&sibling));
    assert!(left.may_overlap(&wsl));
    assert!(
        FileResource::new(location("D:/work/name."), Scope::Exact)
            .may_overlap(&FileResource::new(location("D:/work/name"), Scope::Exact))
    );
    assert!(FileResource::new(location("D:/work/name:stream"), Scope::Exact).may_overlap(&sibling));
    assert!(
        FileResource::new(location("D:/work/Σ"), Scope::Exact)
            .may_overlap(&FileResource::new(location("D:/work/ς"), Scope::Exact))
    );
}

#[test]
fn unknown_endpoints_and_unresolved_roots_cannot_prove_commutation() {
    let resolved = FileResource::new(location("/workspace/a"), Scope::Exact);
    let relative = FileResource::new(location("a"), Scope::Exact);
    let env = FileResource::new(
        Location {
            backend: Backend::Env,
            path: "FILE".into(),
        },
        Scope::Exact,
    );
    assert!(resolved.may_overlap(&relative));
    assert!(resolved.may_overlap(&env));
}

#[test]
fn existing_parent_frontiers_bound_ancestor_bindings_without_serializing_existing_siblings() {
    let ir = program(vec![literal(0, "/left/a"), literal(1, "/right/b")]);
    let mut context = finite_context(&ir);
    for facts in context.operation_facts.values_mut() {
        facts.destination = FileClass::Missing;
    }
    context
        .existing_parent_frontiers
        .insert(InstructionId(0), location("/left"));
    context
        .existing_parent_frontiers
        .insert(InstructionId(1), location("/right"));
    let prepared = graph::build_with_context(&ir, &context);
    assert_eq!(permitted_orders(&prepared).len(), 2);
    assert_eq!(
        prepared.existing_parent_frontiers,
        context.existing_parent_frontiers
    );
    for (node, expected) in prepared.nodes.iter().zip(["/left/a", "/right/b"]) {
        let changed: Vec<_> = node
            .effects
            .accesses
            .iter()
            .filter_map(|access| match &access.resource {
                Resource::DirectoryEntry(entry) if access.kind == AccessKind::Write => {
                    Some(entry.location.path.as_str())
                }
                _ => None,
            })
            .collect();
        assert_eq!(changed, vec![expected]);
    }
    let same_parent = program(vec![literal(0, "/left/a"), literal(1, "/left/b")]);
    context
        .existing_parent_frontiers
        .insert(InstructionId(1), location("/left"));
    let prepared = graph::build_with_context(&same_parent, &context);
    assert_eq!(
        permitted_orders(&prepared).len(),
        2,
        "independent names in an existing parent commute; incidental parent mtime is not an ordering contract"
    );
}

#[test]
fn an_unrelated_parent_frontier_does_not_trim_real_missing_ancestor_bindings() {
    let ir = program(vec![literal(0, "/real/new/a"), literal(1, "/real/new/b")]);
    let mut context = finite_context(&ir);
    for facts in context.operation_facts.values_mut() {
        facts.destination = FileClass::Missing;
    }
    context
        .existing_parent_frontiers
        .insert(InstructionId(0), location("/unrelated"));
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.nodes[0].effects.accesses.iter().any(|access| matches!(&access.resource, Resource::DirectoryEntry(parent) if parent.location.path == "/real/new")));
    assert_eq!(permitted_orders(&graph), vec![vec![0, 1]]);
}

#[test]
fn duplicate_stream_owner_and_instruction_ids_are_rejected() {
    let ir = program(vec![
        spawn(0, 0),
        port(1, 0, ReadPort::Stdout, "/a"),
        port(1, 0, ReadPort::Stdout, "/b"),
    ]);
    let graph = graph::build(&ir);
    assert!(
        graph
            .issues
            .iter()
            .any(|issue| issue.message.contains("unique"))
    );
    assert!(graph.verify(&ir).is_err());
    let ir = program(vec![
        spawn(0, 0),
        port(1, 0, ReadPort::Stdout, "/a"),
        port(2, 0, ReadPort::Stdout, "/b"),
    ]);
    assert!(
        graph::build(&ir)
            .issues
            .iter()
            .any(|issue| issue.message.contains("one owner"))
    );
}

#[test]
fn certificate_tampering_and_source_changes_fail_replay() {
    let ir = program(vec![literal(0, "/a"), copy(1, "/a", "/b")]);
    let mut graph = graph::build(&ir);
    assert!(graph.verify(&ir).is_ok());
    graph.dependencies.clear();
    assert!(graph.verify(&ir).is_err());
    let ir = program(vec![literal(0, "/a"), literal(1, "/b")]);
    let graph = graph::build(&ir);
    assert!(
        graph
            .verify(&program(vec![literal(0, "/a"), literal(1, "/a")]))
            .is_err()
    );
}

#[derive(Clone, Debug)]
struct ModelOp {
    literal: bool,
    source: usize,
    destination: usize,
    value: u8,
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct ModelState {
    files: Vec<Option<Vec<u8>>>,
    reads: std::collections::BTreeMap<usize, Vec<u8>>,
}

fn evaluate(state: &mut ModelState, op: &ModelOp, id: usize) {
    let bytes = if op.literal {
        vec![op.value]
    } else {
        let bytes = state.files[op.source].as_ref().unwrap().clone();
        state.reads.insert(id, bytes.clone());
        bytes
    };
    state.files[op.destination] = Some(bytes);
}

fn model_instruction(id: usize, operation: &ModelOp) -> Instruction {
    // Resource indices are the independent oracle; lexical aliases are inputs
    // to the planner, not keys recovered from its own effect implementation.
    let source = if id.is_multiple_of(2) {
        format!("/r/./{}", operation.source)
    } else {
        format!("/r/unused/../{}", operation.source)
    };
    let destination = format!("/r/{}", operation.destination);
    if operation.literal {
        node(
            id,
            InstructionKind::Transfer {
                source: Input::Literal(Arc::from(char::from(operation.value).to_string())),
                destination: Output::File {
                    location: location(&destination),
                    mode: DestinationMode::Exact,
                },
            },
        )
    } else {
        copy(id, &source, &destination)
    }
}

fn all_orders_preserve(
    graph: &graph::PlanGraph,
    operations: &[ModelOp],
    expected: &ModelState,
    state: &ModelState,
    scheduled: u64,
) -> bool {
    if scheduled.count_ones() as usize == operations.len() {
        return state == expected;
    }
    let mut made_progress = false;
    for (id, operation) in operations.iter().enumerate() {
        if scheduled & (1 << id) != 0 {
            continue;
        }
        let blocked = graph
            .dependencies
            .iter()
            .any(|edge| edge.after == InstructionId(id) && scheduled & (1 << edge.before.0) == 0);
        if blocked {
            continue;
        }
        made_progress = true;
        let mut next = state.clone();
        evaluate(&mut next, operation, id);
        if !all_orders_preserve(graph, operations, expected, &next, scheduled | (1 << id)) {
            return false;
        }
    }
    made_progress
}

proptest! {
    #![proptest_config(ProptestConfig { cases: 48, max_shrink_iters: 512, ..ProptestConfig::default() })]

    #[test]
    fn every_permitted_schedule_matches_an_independent_assignment_model(
        input in prop::collection::vec((any::<bool>(), 0usize..3, 1usize..3, 32u8..127), 1..7)
    ) {
        let operations: Vec<_> = input.into_iter().map(|(literal, source, offset, value)| ModelOp {
            literal,
            source,
            destination: (source + offset) % 3,
            value,
        }).collect();
        let instructions = operations.iter().enumerate().map(|(id, operation)| model_instruction(id, operation)).collect();
        let ir = program(instructions);
        let context = finite_context(&ir);
        let graph = graph::build_with_context(&ir, &context);
        prop_assert!(graph.is_execution_ready());
        let initial = ModelState { files: vec![Some(vec![0]), Some(vec![1]), Some(vec![2])], reads: Default::default() };
        let mut expected = initial.clone();
        for (id, operation) in operations.iter().enumerate() {
            evaluate(&mut expected, operation, id);
        }
        prop_assert!(all_orders_preserve(&graph, &operations, &expected, &initial, 0), "an admitted schedule changed source-order file values or per-operation read observations: {operations:?}");
    }

    #[test]
    fn mixed_creations_and_overwrites_preserve_bytes_and_per_statement_reads(
        input in prop::collection::vec((any::<bool>(), 0usize..6, 3usize..6, 32u8..127), 1..7)
    ) {
        let initial = ModelState {
            files: vec![Some(vec![0]), Some(vec![1]), Some(vec![2]), None, None, None],
            reads: Default::default(),
        };
        let mut expected = initial.clone();
        let mut operations = Vec::new();
        let mut context = graph::PlanningContext::default();
        for (id, (literal, source, destination, value)) in input.into_iter().enumerate() {
            let source = if source == destination || expected.files[source].is_none() { source % 3 } else { source };
            let operation = ModelOp { literal, source, destination, value };
            context.operation_facts.insert(InstructionId(id), OperationFacts {
                source: FileClass::Regular,
                destination: if expected.files[destination].is_some() { FileClass::Regular } else { FileClass::Missing },
            });
            evaluate(&mut expected, &operation, id);
            operations.push(operation);
        }
        let ir = program(operations.iter().enumerate().map(|(id, operation)| model_instruction(id, operation)).collect());
        let graph = graph::build_with_context(&ir, &context);
        prop_assert!(graph.is_execution_ready());
        prop_assert!(all_orders_preserve(&graph, &operations, &expected, &initial, 0), "an admitted schedule changed finite resource values or read results: {operations:?}");
    }

}

fn fresh_sibling_outputs(ir: &Program) -> graph::PlanningContext {
    let mut context = finite_context(ir);
    for instruction in &ir.body.instructions {
        if let Some(facts) = context.operation_facts.get_mut(&instruction.id) {
            facts.destination = FileClass::Missing;
            context
                .existing_parent_frontiers
                .insert(instruction.id, location("/shared"));
        }
    }
    context
}

#[test]
fn a_pending_exit_status_does_not_block_an_independent_sibling_publication() {
    let ir = program(vec![
        port(0, 7, ReadPort::ExitCode, "/shared/status"),
        literal(1, "/shared/marker"),
    ]);
    let mut context = fresh_sibling_outputs(&ir);
    context.live_processes.push(ProcessId(7));
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.issues.is_empty(), "{:?}", graph.issues);
    assert!(graph.dependencies.is_empty(), "{:?}", graph.dependencies);
    assert_eq!(permitted_orders(&graph).len(), 2);
    assert!(
        graph
            .external_processes
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(0)
                && requirement.wait == Wait::ProcessExited(ProcessId(7)))
    );
}

#[test]
fn stdout_capture_does_not_block_an_independent_sibling_publication() {
    let ir = program(vec![
        port(0, 7, ReadPort::Stdout, "/shared/captured"),
        literal(1, "/shared/marker"),
    ]);
    let mut context = fresh_sibling_outputs(&ir);
    context.live_processes.push(ProcessId(7));
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.issues.is_empty(), "{:?}", graph.issues);
    assert!(graph.dependencies.is_empty(), "{:?}", graph.dependencies);
    assert_eq!(permitted_orders(&graph).len(), 2);
}

#[test]
fn independent_tcp_flows_can_admit_unique_files_in_the_same_existing_directory() {
    let mut instructions = vec![copy(0, "", "/shared/first"), copy(1, "", "/shared/second")];
    for (instruction, port) in instructions.iter_mut().zip([9001, 9002]) {
        let InstructionKind::Transfer {
            source: Input::File { location, .. },
            ..
        } = &mut instruction.kind
        else {
            unreachable!()
        };
        location.backend = Backend::Tcp {
            host: "127.0.0.1".into(),
            port,
        };
    }
    let ir = program(instructions);
    let mut context = fresh_sibling_outputs(&ir);
    for facts in context.operation_facts.values_mut() {
        facts.source = FileClass::Stream;
    }
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.issues.is_empty(), "{:?}", graph.issues);
    assert!(graph.dependencies.is_empty(), "{:?}", graph.dependencies);
    assert_eq!(permitted_orders(&graph).len(), 2);
}

#[cfg(windows)]
#[test]
fn windows_named_pipe_names_are_disjoint_from_disk_paths_and_other_pipe_names() {
    let resource = |path| FileResource::new(location(path), Scope::Exact);
    let pipe = resource(r"\\.\pipe\msync-first");
    let same = resource(r"\\.\PIPE\MSYNC-FIRST");
    let other = resource(r"\\.\pipe\msync-second");
    let disk = resource(r"D:\output\file");
    assert!(!pipe.may_overlap(&disk));
    assert!(!disk.may_overlap(&pipe));
    assert!(!pipe.may_overlap(&other));
    assert!(pipe.may_overlap(&same));
}
