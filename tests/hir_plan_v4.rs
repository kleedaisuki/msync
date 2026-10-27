use std::sync::Arc;

use msync::compiler::{Backend, Location};
use msync::hir::effects::{
    Access, AccessKind, Effects, FileResource, OperationFacts, Resource, Scope,
};
use msync::hir::graph::{self, Reason, Wait};
use msync::hir::{
    Argument, Input, Instruction, InstructionId, InstructionKind, Output, ProcessId, Program,
    ReadPort, Region, RollbackScope, TransactionId,
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
        transactions: Vec::new(),
    }
}

fn finite_context(program: &Program) -> graph::PlanningContext {
    fn observe(region: &Region, context: &mut graph::PlanningContext) {
        for instruction in &region.instructions {
            match &instruction.kind {
                InstructionKind::Transfer { .. } => {
                    context.operation_facts.insert(
                        instruction.id,
                        OperationFacts {
                            source: FileClass::Regular,
                            destination: FileClass::Regular,
                        },
                    );
                }
                InstructionKind::Transaction { body, .. } => observe(body, context),
                _ => {}
            }
        }
    }
    let mut context = graph::PlanningContext::default();
    observe(&program.body, &mut context);
    context
}

#[test]
fn literal_writes_have_no_read_effect_and_disjoint_paths_commute() {
    let ir = program(vec![literal(0, "/workspace/a"), literal(1, "/workspace/b")]);
    let context = finite_context(&ir);
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.dependencies.is_empty());
    assert_eq!(graph.commutations.len(), 1);
    assert_eq!(graph.nodes[0].effects.accesses.len(), 1);
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
fn sibling_creations_order_observable_parent_metadata_not_just_distinct_file_bytes() {
    let ir = program(vec![
        literal(0, "/shared/new-a"),
        literal(1, "/shared/new-b"),
    ]);
    let mut context = finite_context(&ir);
    for facts in context.operation_facts.values_mut() {
        facts.destination = FileClass::Missing;
    }
    let graph = graph::build_with_context(&ir, &context);
    fn model(order: &[usize]) -> (Vec<(usize, &'static str)>, usize) {
        let mut files = std::collections::BTreeMap::new();
        let mut parent_modified_by = usize::MAX;
        for id in order {
            files.insert(*id, "new contents");
            parent_modified_by = *id;
        }
        (files.into_iter().collect(), parent_modified_by)
    }
    assert_ne!(
        model(&[0, 1]),
        model(&[1, 0]),
        "file values alone cannot reveal reversed parent mtime writes"
    );
    for order in permitted_orders(&graph) {
        assert_eq!(model(&order), model(&[0, 1]));
    }
    assert!(graph.dependencies.iter().any(|edge| edge.reasons.iter().any(|reason| matches!(reason, Reason::Hazard(conflict) if matches!(conflict.before.resource, Resource::DirectoryMetadata(_)) && matches!(conflict.after.resource, Resource::DirectoryMetadata(_))))));
    assert!(graph.is_execution_ready());
}

#[test]
fn parent_metadata_write_does_not_alias_existing_sibling_file_bytes() {
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
    assert!(graph.dependencies.iter().any(|edge| edge.reasons.iter().any(|reason| matches!(reason, Reason::Hazard(conflict) if matches!(conflict.before.resource, Resource::DirectoryMetadata(_)) && conflict.after.kind == AccessKind::Read))));
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
fn container_insertions_update_the_container_metadata_and_kind_changes_update_its_parent() {
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
    let writes: Vec<_> = graph.nodes[0]
        .effects
        .accesses
        .iter()
        .filter_map(|access| match &access.resource {
            Resource::DirectoryMetadata(resource) if access.kind == AccessKind::Write => {
                Some(resource.location.path.as_str())
            }
            _ => None,
        })
        .collect();
    assert_eq!(writes, vec!["/root/container"]);

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
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.nodes[0].effects.accesses.iter().any(|access| access.kind == AccessKind::Write && matches!(&access.resource, Resource::DirectoryMetadata(resource) if resource.location.path == "/root")));
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
fn child_effect_barrier_waits_for_exit_not_just_launch() {
    let ir = program(vec![
        literal(0, "/before"),
        spawn(1, 7),
        literal(2, "/after"),
    ]);
    let graph = graph::build(&ir);
    assert!(
        graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(1)
                && edge.wait == Wait::Completion)
    );
    assert!(
        graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(1)
                && edge.after == InstructionId(2)
                && edge.wait == Wait::ProcessExited(ProcessId(7)))
    );
    assert!(graph.nodes[1].effects.unknown_external);
}

#[test]
fn process_pipe_services_co_start_and_scalars_use_distinct_events() {
    let ir = program(vec![
        spawn(0, 42),
        node(
            1,
            InstructionKind::Transfer {
                source: Input::Literal(Arc::from("hello")),
                destination: Output::Stdin(ProcessId(42)),
            },
        ),
        port(2, 42, ReadPort::Stdout, "/stdout"),
        port(3, 42, ReadPort::Stderr, "/stderr"),
        port(4, 42, ReadPort::Pid, "/pid"),
        port(5, 42, ReadPort::ExitCode, "/exit"),
        port(6, 42, ReadPort::ExitCode, "/exit-again"),
    ]);
    let context = finite_context(&ir);
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.issues.is_empty(), "{:?}", graph.issues);
    assert_eq!(graph.co_start.len(), 1);
    assert_eq!(
        graph.co_start[0].members,
        vec![InstructionId(1), InstructionId(2), InstructionId(3)]
    );
    for id in 1..=4 {
        assert!(
            graph
                .dependencies
                .iter()
                .any(|edge| edge.before == InstructionId(0)
                    && edge.after == InstructionId(id)
                    && edge.wait == Wait::ProcessStarted(ProcessId(42)))
        );
    }
    for id in 5..=6 {
        assert!(
            graph
                .dependencies
                .iter()
                .any(|edge| edge.before == InstructionId(0)
                    && edge.after == InstructionId(id)
                    && edge.wait == Wait::ProcessExited(ProcessId(42)))
        );
    }
    assert!(graph.verify_with_context(&ir, &context).is_ok());
}

fn pipeline() -> Program {
    program(vec![
        spawn(0, 0),
        spawn(1, 1),
        node(
            2,
            InstructionKind::Transfer {
                source: Input::Port {
                    process: ProcessId(1),
                    port: ReadPort::Stdout,
                },
                destination: Output::Stdin(ProcessId(0)),
            },
        ),
    ])
}

fn mediated_effects(path: &str) -> graph::ProcessEffects {
    graph::ProcessEffects::mediated(
        Effects {
            accesses: vec![Access {
                resource: Resource::File(FileResource::new(location(path), Scope::Tree)),
                kind: AccessKind::Write,
            }],
            unknown_external: false,
        },
        "verified-test-view-provider",
        format!("view-{path}"),
    )
    .unwrap()
}

#[test]
fn an_unrefined_program_pipeline_is_not_hidden_as_a_safe_acyclic_schedule() {
    let graph = graph::build(&pipeline());
    assert!(graph.issues.iter().any(|issue| {
        issue
            .message
            .contains("pipe service depends on that process exiting")
    }));
}

#[test]
fn verified_disjoint_process_views_enable_pipeline_launch_without_argv_guessing() {
    let ir = pipeline();
    let context = graph::PlanningContext {
        live_processes: Vec::new(),
        process_effects: [
            (ProcessId(0), mediated_effects("/p-private")),
            (ProcessId(1), mediated_effects("/q-private")),
        ]
        .into(),
        ..graph::PlanningContext::default()
    };
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.issues.is_empty(), "{:?}", graph.issues);
    assert!(
        !graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0) && edge.after == InstructionId(1))
    );
    assert_eq!(graph.co_start.len(), 2);
    assert!(graph.verify_with_context(&ir, &context).is_ok());
    assert!(graph.verify(&ir).is_err());
    assert_eq!(
        graph.process_proofs[&ProcessId(0)].provider(),
        "verified-test-view-provider"
    );
}

#[test]
fn a_view_certificate_does_not_erase_overlapping_process_effects() {
    let ir = pipeline();
    let context = graph::PlanningContext {
        live_processes: Vec::new(),
        process_effects: [
            (ProcessId(0), mediated_effects("/shared")),
            (ProcessId(1), mediated_effects("/shared")),
        ]
        .into(),
        ..graph::PlanningContext::default()
    };
    let graph = graph::build_with_context(&ir, &context);
    assert!(
        graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(1)
                && edge.wait == Wait::ProcessExited(ProcessId(0)))
    );
    assert!(!graph.issues.is_empty());
    assert!(
        graph::ProcessEffects::mediated(
            Effects {
                unknown_external: true,
                ..Effects::default()
            },
            "provider",
            "view"
        )
        .is_err()
    );
}

#[test]
fn pipe_filesystem_hazard_is_not_erased_to_make_a_cohort_schedulable() {
    let ir = program(vec![
        spawn(0, 0),
        port(1, 0, ReadPort::Stdout, "/same"),
        node(
            2,
            InstructionKind::Transfer {
                source: Input::File {
                    location: location("/same"),
                    pattern: None,
                },
                destination: Output::Stdin(ProcessId(0)),
            },
        ),
    ]);
    let graph = graph::build(&ir);
    assert!(
        graph
            .issues
            .iter()
            .any(|issue| issue.message.contains("stage/version"))
    );
    assert!(graph.verify(&ir).is_err());
}

#[test]
fn checkpoint_quiesces_children_and_prevents_cross_epoch_motion() {
    let ir = program(vec![
        spawn(0, 0),
        node(
            1,
            InstructionKind::Commit {
                transaction: TransactionId(0),
            },
        ),
        literal(2, "/after"),
    ]);
    let graph = graph::build(&ir);
    assert!(
        graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(1)
                && edge.wait == Wait::ProcessExited(ProcessId(0))
                && edge.reasons.contains(&Reason::CheckpointBoundary))
    );
    assert!(
        graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(1)
                && edge.after == InstructionId(2)
                && edge.wait == Wait::Completion)
    );
}

#[test]
fn checkpoint_cannot_gate_a_live_stdin_service_behind_process_exit() {
    let ir = program(vec![
        spawn(0, 0),
        node(
            1,
            InstructionKind::Commit {
                transaction: TransactionId(0),
            },
        ),
        node(
            2,
            InstructionKind::Transfer {
                source: Input::Literal(Arc::from("input")),
                destination: Output::Stdin(ProcessId(0)),
            },
        ),
    ]);
    let graph = graph::build(&ir);
    assert!(
        graph
            .issues
            .iter()
            .any(|issue| issue.message.contains("crosses a checkpoint"))
    );
    assert!(graph.verify(&ir).is_err());
}

#[test]
fn retained_processes_constrain_the_next_submission_even_without_port_mentions() {
    let ir = program(vec![literal(0, "/a"), literal(1, "/b")]);
    let graph = graph::build_with_live_processes(&ir, [ProcessId(88)]);
    assert_eq!(graph.external_processes.len(), 2);
    assert!(
        graph
            .external_processes
            .iter()
            .all(|requirement| requirement.wait == Wait::ProcessExited(ProcessId(88)))
    );
    assert!(
        graph
            .verify_with_live_processes(&ir, [ProcessId(88)])
            .is_ok()
    );
    assert!(graph.verify(&ir).is_err());
}

#[test]
fn imported_ports_can_be_serviced_while_unrelated_work_awaits_their_process() {
    let ir = program(vec![
        port(0, 88, ReadPort::Stdout, "/stdout"),
        literal(1, "/b"),
        port(2, 88, ReadPort::ExitCode, "/exit"),
    ]);
    let graph = graph::build(&ir);
    assert!(
        graph
            .external_processes
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(0)
                && requirement.wait == Wait::ProcessStarted(ProcessId(88)))
    );
    assert!(
        graph
            .external_processes
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(1)
                && requirement.wait == Wait::ProcessExited(ProcessId(88)))
    );
    assert!(
        graph
            .external_processes
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(2)
                && requirement.wait == Wait::ProcessExited(ProcessId(88)))
    );
    assert!(graph.verify(&ir).is_ok());
}

#[test]
fn nested_invalid_pipe_schedules_fail_the_outer_certificate() {
    let ir = program(vec![node(
        10,
        InstructionKind::Transaction {
            transaction: TransactionId(0),
            body: Region {
                instructions: vec![
                    spawn(0, 0),
                    node(
                        1,
                        InstructionKind::Commit {
                            transaction: TransactionId(0),
                        },
                    ),
                    node(
                        2,
                        InstructionKind::Transfer {
                            source: Input::Literal(Arc::from("input")),
                            destination: Output::Stdin(ProcessId(0)),
                        },
                    ),
                ],
            },
        },
    )]);
    let graph = graph::build(&ir);
    assert!(!graph.issues.is_empty());
    assert!(graph.verify(&ir).is_err());
}

#[test]
fn named_rollback_retains_origin_and_conflicts_with_later_accesses() {
    let ir = program(vec![
        node(
            0,
            InstructionKind::Transaction {
                transaction: TransactionId(77),
                body: Region {
                    instructions: vec![literal(1, "/managed/x")],
                },
            },
        ),
        node(
            2,
            InstructionKind::Rollback {
                transaction: TransactionId(77),
                scope: RollbackScope::Begin,
            },
        ),
        copy(3, "/managed/x", "/result"),
        literal(4, "/unrelated"),
    ]);
    let context = finite_context(&ir);
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.dependencies.iter().any(|edge| {
        edge.before == InstructionId(0)
            && edge.after == InstructionId(2)
            && edge
                .reasons
                .contains(&Reason::RetainedTransactionOrigin(TransactionId(77)))
    }));
    assert!(
        graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(2) && edge.after == InstructionId(3))
    );
    assert!(
        !graph
            .dependencies
            .iter()
            .any(|edge| edge.after == InstructionId(4))
    );
    assert_eq!(
        graph.nodes[0].children.as_ref().unwrap().nodes[0].instruction,
        InstructionId(1)
    );
    assert!(graph.verify_with_context(&ir, &context).is_ok());
}

#[test]
fn previous_submission_rollback_is_unknown_instead_of_a_false_empty_effect() {
    let ir = program(vec![
        node(
            0,
            InstructionKind::Rollback {
                transaction: TransactionId(99),
                scope: RollbackScope::Begin,
            },
        ),
        literal(1, "/unrelated"),
    ]);
    let graph = graph::build(&ir);
    assert!(graph.nodes[0].effects.unknown_external);
    assert_eq!(graph.dependencies.len(), 1);
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
    parent_modified_by: Option<usize>,
}

fn evaluate(state: &mut ModelState, op: &ModelOp, id: usize) {
    let bytes = if op.literal {
        vec![op.value]
    } else {
        let bytes = state.files[op.source].as_ref().unwrap().clone();
        state.reads.insert(id, bytes.clone());
        bytes
    };
    if state.files[op.destination].is_none() {
        state.parent_modified_by = Some(id);
    }
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
        let initial = ModelState { files: vec![Some(vec![0]), Some(vec![1]), Some(vec![2])], reads: Default::default(), parent_modified_by: None };
        let mut expected = initial.clone();
        for (id, operation) in operations.iter().enumerate() {
            evaluate(&mut expected, operation, id);
        }
        prop_assert!(all_orders_preserve(&graph, &operations, &expected, &initial, 0), "an admitted schedule changed source-order file values or per-operation read observations: {operations:?}");
    }

    #[test]
    fn mixed_creations_and_overwrites_preserve_bytes_reads_and_parent_metadata(
        input in prop::collection::vec((any::<bool>(), 0usize..6, 3usize..6, 32u8..127), 1..7)
    ) {
        let initial = ModelState {
            files: vec![Some(vec![0]), Some(vec![1]), Some(vec![2]), None, None, None],
            reads: Default::default(),
            parent_modified_by: None,
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
        prop_assert!(all_orders_preserve(&graph, &operations, &expected, &initial, 0), "an admitted schedule changed finite resource values, read results or parent-directory metadata: {operations:?}");
    }
}
