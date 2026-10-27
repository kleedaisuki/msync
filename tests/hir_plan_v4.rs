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
                && edge.wait == Wait::ProcessSealed(ProcessId(7)))
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
    assert!(graph.issues.is_empty());
    assert_eq!(graph.cohort_refinements.len(), 1);
    assert_eq!(
        graph.cohort_refinements[0].kind,
        graph::CohortRefinementKind::SharedMutableView
    );
    assert!(!graph.is_execution_ready());
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
                && edge.wait == Wait::ProcessSealed(ProcessId(0)))
    );
    assert!(graph.issues.is_empty());
    assert!(!graph.cohort_refinements.is_empty());
    assert!(!graph.is_execution_ready());
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

fn shared_view(context: &mut graph::PlanningContext, processes: &[usize], view: &str, epoch: &str) {
    let resource = Resource::File(FileResource::new(location("/managed"), Scope::Tree));
    for process in processes {
        context.process_effects.insert(
            ProcessId(*process),
            graph::ProcessEffects::mediated(
                Effects {
                    accesses: vec![
                        Access {
                            resource: resource.clone(),
                            kind: AccessKind::Read,
                        },
                        Access {
                            resource: resource.clone(),
                            kind: AccessKind::Write,
                        },
                    ],
                    unknown_external: false,
                },
                "verified-native-view",
                view,
            )
            .unwrap(),
        );
    }
    context.cohort_views.push(
        graph::CohortViewProof::shared(
            "runtime-owner",
            epoch,
            processes.iter().map(|process| {
                (
                    ProcessId(*process),
                    context.process_effects[&ProcessId(*process)].clone(),
                )
            }),
        )
        .unwrap(),
    );
}

fn connect(id: usize, source: usize, destination: usize) -> Instruction {
    node(
        id,
        InstructionKind::Transfer {
            source: Input::Port {
                process: ProcessId(source),
                port: ReadPort::Stdout,
            },
            destination: Output::Stdin(ProcessId(destination)),
        },
    )
}

#[test]
fn an_actual_shared_epoch_admits_connected_children_without_claiming_disjoint_effects() {
    let ir = program(vec![
        literal(0, "/managed/before"),
        spawn(1, 10),
        spawn(2, 11),
        connect(3, 10, 11),
        port(4, 11, ReadPort::Stdout, "/managed/output"),
        copy(5, "/managed/output", "/managed/after"),
    ]);
    let mut context = finite_context(&ir);
    shared_view(
        &mut context,
        &[10, 11],
        "retained-native-backing-A",
        "view-epoch-A",
    );
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.issues.is_empty(), "{:?}", graph.issues);
    assert!(graph.is_execution_ready(), "{:?}", graph.cohort_refinements);
    assert_eq!(graph.shared_cohorts.len(), 1);
    assert_eq!(
        graph.shared_cohorts[0].launches,
        vec![
            (ProcessId(10), InstructionId(1)),
            (ProcessId(11), InstructionId(2))
        ]
    );
    assert!(
        graph.shared_cohorts[0]
            .effects
            .accesses
            .iter()
            .any(|access| access.kind == AccessKind::Read)
    );
    assert!(
        graph.shared_cohorts[0]
            .effects
            .accesses
            .iter()
            .any(|access| access.kind == AccessKind::Write)
    );
    assert!(graph.dependencies.iter().any(|edge| {
        edge.before == InstructionId(1)
            && edge.after == InstructionId(2)
            && edge.wait == Wait::ProcessStarted(ProcessId(10))
            && edge
                .reasons
                .contains(&Reason::SharedViewEpoch(graph::CohortId(0)))
    }));
    assert!(
        !graph
            .dependencies
            .iter()
            .any(|edge| edge.after == InstructionId(2)
                && matches!(edge.wait, Wait::ProcessSealed(_) | Wait::CohortSealed(_)))
    );
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
            .any(|edge| edge.after == InstructionId(5)
                && edge.wait == Wait::CohortSealed(graph::CohortId(0))),
        "foreign RAW/WAW conflicts await the complete shared export, not one member's early exit"
    );
    assert!(
        graph
            .publication_dependencies
            .iter()
            .any(|edge| edge.after == InstructionId(4)
                && edge.wait == Wait::CohortSealed(graph::CohortId(0)))
    );
    assert!(graph.verify_with_context(&ir, &context).is_ok());
}

#[test]
fn shared_view_certificates_reject_separate_views_and_mismatched_runtime_membership() {
    let first = mediated_effects("/first");
    let second = mediated_effects("/second");
    assert!(
        graph::CohortViewProof::shared(
            "owner",
            "epoch",
            [(ProcessId(0), first), (ProcessId(1), second)]
        )
        .is_err()
    );
    let ir = pipeline();
    let mut context = finite_context(&ir);
    shared_view(&mut context, &[0, 1], "native-backing", "epoch");
    let effects = context.process_effects[&ProcessId(0)].clone();
    assert!(
        graph::CohortViewProof::shared(
            "",
            "epoch",
            [
                (ProcessId(0), effects.clone()),
                (ProcessId(1), effects.clone())
            ]
        )
        .is_err()
    );
    assert!(
        graph::CohortViewProof::shared(
            "owner",
            "",
            [
                (ProcessId(0), effects.clone()),
                (ProcessId(1), effects.clone())
            ]
        )
        .is_err()
    );
    assert!(
        graph::CohortViewProof::shared(
            "owner",
            "epoch",
            [(ProcessId(0), effects.clone()), (ProcessId(0), effects)]
        )
        .is_err()
    );
    context
        .process_effects
        .insert(ProcessId(1), mediated_effects("/unrelated-certificate"));
    let graph = graph::build_with_context(&ir, &context);
    assert!(!graph.issues.is_empty());
    assert!(graph.shared_cohorts.is_empty());
    assert!(!graph.is_execution_ready());
}

#[test]
fn checkpoint_epochs_create_separate_groups_and_cannot_reuse_one_live_view_proof() {
    let ir = program(vec![
        spawn(0, 0),
        spawn(1, 1),
        connect(2, 0, 1),
        node(
            3,
            InstructionKind::Commit {
                transaction: TransactionId(7),
            },
        ),
        spawn(4, 2),
        spawn(5, 3),
        connect(6, 2, 3),
    ]);
    let mut context = finite_context(&ir);
    shared_view(
        &mut context,
        &[0, 1],
        "native-view-before-commit",
        "epoch-0",
    );
    shared_view(&mut context, &[2, 3], "native-view-after-commit", "epoch-1");
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.issues.is_empty(), "{:?}", graph.issues);
    assert!(graph.is_execution_ready());
    assert_eq!(graph.shared_cohorts.len(), 2);
    assert!(
        graph
            .dependencies
            .iter()
            .any(|edge| edge.after == InstructionId(3)
                && edge.wait == Wait::CohortSealed(graph::CohortId(0)))
    );
    assert!(
        graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(3)
                && edge.after == InstructionId(4)
                && edge.wait == Wait::Completion)
    );
    let broken = program(vec![
        spawn(0, 0),
        node(
            1,
            InstructionKind::Commit {
                transaction: TransactionId(7),
            },
        ),
        spawn(2, 1),
        connect(3, 0, 1),
    ]);
    let mut context = finite_context(&broken);
    shared_view(
        &mut context,
        &[0, 1],
        "incorrectly-reused-view",
        "same-epoch",
    );
    let graph = graph::build_with_context(&broken, &context);
    assert!(
        !graph.issues.is_empty(),
        "a native identity does not authorize crossing a checkpoint"
    );
    assert!(graph.shared_cohorts.is_empty());
    assert!(!graph.is_execution_ready());
}

#[test]
fn a_retained_peer_and_a_real_prepared_fork_admit_a_cross_submission_connection() {
    let ir = program(vec![
        spawn(0, 1),
        connect(1, 0, 1),
        copy(2, "/managed/file", "/after"),
    ]);
    let mut context = finite_context(&ir);
    context.live_processes.push(ProcessId(0));
    shared_view(
        &mut context,
        &[0, 1],
        "actual-retained-fork-lease",
        "still-open-epoch",
    );
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.issues.is_empty(), "{:?}", graph.issues);
    assert!(graph.is_execution_ready());
    assert!(
        graph
            .external_processes
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(0)
                && requirement.process == ProcessId(0)
                && requirement.wait == Wait::ProcessStarted(ProcessId(0)))
    );
    assert!(
        !graph
            .external_processes
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(0)
                && matches!(
                    requirement.wait,
                    Wait::ProcessSealed(_) | Wait::CohortSealed(_)
                ))
    );
    assert!(
        graph
            .external_processes
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(2)
                && requirement.wait == Wait::CohortSealed(graph::CohortId(0)))
    );
    context.cohort_views.clear();
    let pending = graph::build_with_context(&ir, &context);
    assert!(!pending.cohort_refinements.is_empty());
    assert!(!pending.is_execution_ready());
}

#[test]
fn cohort_outputs_are_published_after_the_whole_view_even_between_member_launches() {
    let ir = program(vec![
        spawn(0, 0),
        port(1, 0, ReadPort::Pid, "/managed/pid"),
        spawn(2, 1),
        connect(3, 0, 1),
    ]);
    let mut context = finite_context(&ir);
    shared_view(&mut context, &[0, 1], "one-native-mutable-view", "epoch");
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.issues.is_empty());
    assert!(graph.is_execution_ready(), "{:?}", graph.cohort_refinements);
    assert!(
        !graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(1) && edge.after == InstructionId(2)),
        "a deferred output's publication cannot block launching the peer needed to seal that view"
    );
    assert!(graph.publication_dependencies.iter().any(|edge| {
        edge.before == InstructionId(2)
            && edge.after == InstructionId(1)
            && edge.wait == Wait::CohortSealed(graph::CohortId(0))
            && edge
                .reasons
                .iter()
                .any(|reason| matches!(reason, Reason::SharedViewPublication { .. }))
    }));
    assert!(graph.verify_with_context(&ir, &context).is_ok());
}

#[test]
fn shared_view_pins_and_full_origin_rollback_keep_distinct_proof_boundaries() {
    let body = Region {
        instructions: vec![
            spawn(0, 0),
            spawn(1, 1),
            node(
                2,
                InstructionKind::Transfer {
                    source: Input::File {
                        location: location("/managed/prompt"),
                        pattern: None,
                    },
                    destination: Output::Stdin(ProcessId(0)),
                },
            ),
            connect(3, 0, 1),
            port(4, 1, ReadPort::Stdout, "/managed/output"),
        ],
    };
    let ir = program(vec![
        node(
            10,
            InstructionKind::Transaction {
                transaction: TransactionId(7),
                body,
            },
        ),
        node(
            11,
            InstructionKind::Rollback {
                transaction: TransactionId(7),
                scope: RollbackScope::Begin,
            },
        ),
        copy(12, "/managed/output", "/restored"),
    ]);
    let mut context = finite_context(&ir);
    shared_view(
        &mut context,
        &[0, 1],
        "transaction-native-view",
        "transaction-epoch",
    );
    let pending = graph::build_with_context(&ir, &context);
    assert!(pending.issues.is_empty(), "{:?}", pending.issues);
    assert!(
        pending
            .refinements
            .iter()
            .any(|refinement| refinement.kind == graph::RefinementKind::InputVersion)
    );
    context.pinned_inputs.insert(
        InstructionId(2),
        graph::VersionReadProof::pinned_file(
            location("/managed/prompt"),
            "manager-retained-reader",
            "selected-pending-version",
        )
        .unwrap(),
    );
    let prepared = graph::build_with_context(&ir, &context);
    assert!(prepared.is_execution_ready(), "{:?}", prepared.issues);
    let child = prepared.nodes[0].children.as_ref().unwrap();
    assert_eq!(child.shared_cohorts.len(), 1);
    assert!(
        child
            .publication_dependencies
            .iter()
            .any(|edge| edge.after == InstructionId(4)
                && edge.wait == Wait::CohortSealed(graph::CohortId(0)))
    );
    assert!(prepared.dependencies.iter().any(|edge| {
        edge.before == InstructionId(10)
            && edge.after == InstructionId(11)
            && edge
                .reasons
                .contains(&Reason::RetainedTransactionOrigin(TransactionId(7)))
    }));
    assert!(
        prepared
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(11) && edge.after == InstructionId(12))
    );
    assert!(prepared.verify_with_context(&ir, &context).is_ok());
}

#[test]
fn compiler_fed_a1_b2_rollback_does_not_reacquire_the_transaction_streams() {
    let source = r#"transaction A1(read uncommitted):
    p => $path::python3("worker.py")
    "hello" -> p::stdin
    p::stdout -> output.txt
    p::stderr -> errors.txt
    commit
    p::exit_code -> status.txt
    commit
    p::exit_code -> discarded.txt
    rollback
rollback A1
transaction B2(serializable):
    verifier => $path::python3("verify.py")
    verifier::pid -> pid.txt
"#;
    let document = msync::language::parse(source).unwrap();
    let context = msync::compiler::CompileContext::capture(None).unwrap();
    let mut compiler = msync::compiler::structured::Session::new();
    let program = compiler.compile(&document, &context).unwrap();
    let graph = graph::build(&program);
    assert!(graph.issues.is_empty(), "{:?}", graph.issues);
    assert!(graph.verify(&program).is_ok());
    let rollback = graph
        .nodes
        .iter()
        .find(|node| {
            matches!(
                program
                    .body
                    .instructions
                    .iter()
                    .find(|instruction| instruction.id == node.instruction)
                    .map(|instruction| &instruction.kind),
                Some(InstructionKind::Rollback {
                    scope: RollbackScope::Begin,
                    ..
                })
            )
        })
        .unwrap();
    assert!(
        rollback
            .effects
            .accesses
            .iter()
            .all(|access| access.kind == AccessKind::Write
                && matches!(
                    access.resource,
                    Resource::File(_) | Resource::DirectoryMetadata(_)
                ))
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
    assert!(graph.issues.is_empty());
    assert!(
        graph
            .refinements
            .iter()
            .any(|refinement| refinement.instruction == InstructionId(2)
                && refinement.kind == graph::RefinementKind::InputVersion)
    );
    assert!(graph.dependencies.iter().any(|edge| {
        edge.before == InstructionId(1)
            && edge.after == InstructionId(2)
            && edge
                .reasons
                .iter()
                .any(|reason| matches!(reason, Reason::Hazard(_)))
    }));
    assert!(!graph.is_execution_ready());
    assert!(graph.verify(&ir).is_ok());
}

#[test]
fn existing_parent_frontiers_bound_namespace_writes_without_dropping_the_parent() {
    let ir = program(vec![literal(0, "/left/a"), literal(1, "/right/b")]);
    let mut context = finite_context(&ir);
    for facts in context.operation_facts.values_mut() {
        facts.destination = FileClass::Missing;
    }
    let unrefined = graph::build_with_context(&ir, &context);
    assert_eq!(
        permitted_orders(&unrefined),
        vec![vec![0, 1]],
        "without parent metadata, either parent may itself need creation under '/'"
    );
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
    for (node, expected) in prepared.nodes.iter().zip(["/left", "/right"]) {
        let changed: Vec<_> = node
            .effects
            .accesses
            .iter()
            .filter_map(|access| match &access.resource {
                Resource::DirectoryMetadata(directory) if access.kind == AccessKind::Write => {
                    Some(directory.location.path.as_str())
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
        permitted_orders(&prepared),
        vec![vec![0, 1]],
        "the proved existing directory's observable metadata is still written"
    );
}

#[test]
fn an_unrelated_parent_frontier_does_not_trim_real_namespace_ancestors() {
    let ir = program(vec![literal(0, "/real/new"), literal(1, "/other/new")]);
    let mut context = finite_context(&ir);
    for facts in context.operation_facts.values_mut() {
        facts.destination = FileClass::Missing;
    }
    context
        .existing_parent_frontiers
        .insert(InstructionId(0), location("/unrelated"));
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.nodes[0].effects.accesses.iter().any(|access| matches!(&access.resource, Resource::DirectoryMetadata(parent) if parent.location.path == "/")));
    assert_eq!(permitted_orders(&graph), vec![vec![0, 1]]);
}

fn stdin_file_program(path: &str) -> Program {
    program(vec![
        spawn(0, 0),
        node(
            1,
            InstructionKind::Transfer {
                source: Input::File {
                    location: location(path),
                    pattern: None,
                },
                destination: Output::Stdin(ProcessId(0)),
            },
        ),
    ])
}

#[test]
fn own_file_input_cycles_are_pending_version_obligations_not_frontend_rejections() {
    let ir = stdin_file_program("/managed/prompt");
    let mut context = finite_context(&ir);
    context
        .process_effects
        .insert(ProcessId(0), mediated_effects("/managed"));
    let pending = graph::build_with_context(&ir, &context);
    assert!(pending.issues.is_empty(), "{:?}", pending.issues);
    assert!(
        pending
            .refinements
            .iter()
            .any(|refinement| refinement.kind == graph::RefinementKind::InputVersion)
    );
    assert!(
        pending
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(1)
                && edge.wait == Wait::ProcessSealed(ProcessId(0)))
    );
    assert!(!pending.is_execution_ready());
    assert!(pending.verify_with_context(&ir, &context).is_ok());

    context.pinned_inputs.insert(
        InstructionId(1),
        graph::VersionReadProof::pinned_file(location("/different"), "reader", "wrong-location")
            .unwrap(),
    );
    let wrong = graph::build_with_context(&ir, &context);
    assert!(
        wrong
            .refinements
            .iter()
            .any(|refinement| refinement.kind == graph::RefinementKind::InputVersion)
    );
    assert!(
        wrong
            .dependencies
            .iter()
            .any(|edge| edge.wait == Wait::ProcessSealed(ProcessId(0)))
    );
    assert!(!wrong.is_execution_ready());
    assert!(
        !wrong.nodes[1]
            .effects
            .accesses
            .iter()
            .any(|access| matches!(access.resource, Resource::SnapshotFile(_)))
    );
}

#[test]
fn an_actual_retained_manager_version_allows_stdin_to_co_start_with_private_writes() {
    use msync::language::Isolation;
    use msync::transaction::filesystem::Entry;
    use msync::transaction::manager::Manager;
    let temporary = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    std::fs::create_dir_all(&temporary).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("hir-version-proof-")
        .tempdir_in(&temporary)
        .unwrap();
    let root = workspace.path().join("managed");
    std::fs::create_dir(&root).unwrap();
    let prompt = root.join("prompt");
    std::fs::write(&prompt, "committed").unwrap();
    let payload = workspace.path().join("payload");
    std::fs::write(&payload, "pending version").unwrap();
    let mut manager = Manager::new(&workspace.path().join("snapshots")).unwrap();
    manager.enroll(&root).unwrap();
    let transaction = TransactionId(9);
    manager.begin(transaction, Isolation::Serializable).unwrap();
    let pending = manager.capture(&payload).unwrap();
    manager
        .replace_snapshot(transaction, &prompt, &pending)
        .unwrap();
    let lease = manager.read_snapshot(transaction, &prompt).unwrap();
    let Some(Entry::File { artifact, .. }) = lease.entries().get(std::path::Path::new("")) else {
        panic!("expected a captured regular-file input")
    };
    assert_eq!(
        std::fs::read_to_string(artifact.path()).unwrap(),
        "pending version"
    );
    assert_eq!(std::fs::read_to_string(&prompt).unwrap(), "committed");

    let logical = prompt.to_str().unwrap();
    let ir = stdin_file_program(logical);
    let mut context = finite_context(&ir);
    context
        .process_effects
        .insert(ProcessId(0), mediated_effects(root.to_str().unwrap()));
    context.pinned_inputs.insert(
        InstructionId(1),
        graph::VersionReadProof::pinned_file(
            location(logical),
            "manager-immutable-snapshot",
            format!("{}:transaction-9:read-1", workspace.path().display()),
        )
        .unwrap(),
    );
    let prepared = graph::build_with_context(&ir, &context);
    assert!(prepared.issues.is_empty(), "{:?}", prepared.issues);
    assert!(prepared.is_execution_ready(), "{:?}", prepared.refinements);
    assert!(
        prepared
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(1)
                && edge.wait == Wait::ProcessStarted(ProcessId(0)))
    );
    assert!(
        !prepared
            .dependencies
            .iter()
            .any(|edge| edge.wait == Wait::ProcessSealed(ProcessId(0)))
    );
    assert!(
        prepared.nodes[1]
            .effects
            .accesses
            .iter()
            .any(|access| access.kind == AccessKind::Read
                && matches!(access.resource, Resource::SnapshotFile(_)))
    );
    assert!(prepared.verify_with_context(&ir, &context).is_ok());
    assert_eq!(prepared.pinned_inputs, context.pinned_inputs);

    std::fs::write(&payload, "later private version").unwrap();
    let later = manager.capture(&payload).unwrap();
    manager
        .replace_snapshot(transaction, &prompt, &later)
        .unwrap();
    manager.finish(transaction).unwrap();
    assert_eq!(
        std::fs::read_to_string(&prompt).unwrap(),
        "later private version"
    );
    assert_eq!(
        std::fs::read_to_string(artifact.path()).unwrap(),
        "pending version",
        "the actual immutable lease, not a regular-file type, justifies independence"
    );
    assert!(lease.entries().contains_key(std::path::Path::new("")));
}

#[test]
fn a_pin_is_not_inferred_for_ordinary_file_reads_or_raw_network_sources() {
    let ir = program(vec![literal(0, "/same"), copy(1, "/same", "/output")]);
    let context = finite_context(&ir);
    let graph = graph::build_with_context(&ir, &context);
    assert!(
        graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(1)
                && edge.wait == Wait::Completion)
    );
    assert!(
        !graph.nodes[1]
            .effects
            .accesses
            .iter()
            .any(|access| matches!(access.resource, Resource::SnapshotFile(_)))
    );
    let http = Location {
        backend: Backend::Http {
            base: "https://example.invalid".into(),
        },
        path: "/data".into(),
    };
    assert!(graph::VersionReadProof::pinned_file(http, "HEAD", "regular").is_err());
    let tcp = Location {
        backend: Backend::Tcp {
            host: "127.0.0.1".into(),
            port: 9000,
        },
        path: "/".into(),
    };
    assert!(graph::VersionReadProof::pinned_file(tcp, "socket-type", "regular").is_err());
}

#[test]
fn program_output_drain_and_its_logical_file_publication_are_distinct_phases() {
    let mut ir = stdin_file_program("/managed/prompt");
    ir.body
        .instructions
        .push(port(2, 0, ReadPort::Stdout, "/managed/output"));
    ir.body
        .instructions
        .push(copy(3, "/managed/output", "/else/result"));
    let mut context = finite_context(&ir);
    context
        .process_effects
        .insert(ProcessId(0), mediated_effects("/managed"));
    context.pinned_inputs.insert(
        InstructionId(1),
        graph::VersionReadProof::pinned_file(
            location("/managed/prompt"),
            "manager-reader",
            "retained-version-1",
        )
        .unwrap(),
    );
    let prepared = graph::build_with_context(&ir, &context);
    assert!(prepared.issues.is_empty(), "{:?}", prepared.issues);
    assert!(prepared.is_execution_ready(), "{:?}", prepared.refinements);
    assert!(
        prepared
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(2)
                && edge.wait == Wait::ProcessStarted(ProcessId(0)))
    );
    assert!(!prepared.dependencies.iter().any(
        |edge| edge.after == InstructionId(2) && edge.wait == Wait::ProcessSealed(ProcessId(0))
    ));
    assert!(prepared.publication_dependencies.iter().any(|edge| {
        edge.before == InstructionId(0)
            && edge.after == InstructionId(2)
            && edge.wait == Wait::ProcessSealed(ProcessId(0))
            && edge
                .reasons
                .contains(&Reason::OutputPublication(ProcessId(0)))
    }));
    assert!(prepared.publication_dependencies.iter().any(|edge| edge.after == InstructionId(2) && edge.reasons.iter().any(|reason| matches!(reason, Reason::Hazard(conflict) if conflict.after.kind == AccessKind::Write))));
    assert!(
        prepared
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(2)
                && edge.after == InstructionId(3)
                && edge.wait == Wait::Completion),
        "later file readers need the logical published assignment, not just a drained stage"
    );
    assert!(
        prepared
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(3)
                && edge.wait == Wait::ProcessSealed(ProcessId(0)))
    );
    assert!(prepared.verify_with_context(&ir, &context).is_ok());

    fn final_target(order: &[&str]) -> &'static str {
        let mut target = "old contents";
        for event in order {
            target = match *event {
                "seal private delta" => "child wrote this path",
                "publish stdout assignment" => "stdout bytes",
                _ => target,
            };
        }
        target
    }
    assert_ne!(
        final_target(&["publish stdout assignment", "seal private delta"]),
        "stdout bytes"
    );
    assert_eq!(
        final_target(&[
            "drain private spool",
            "seal private delta",
            "publish stdout assignment"
        ]),
        "stdout bytes"
    );
    let mut tampered = prepared.clone();
    tampered.publication_dependencies.clear();
    assert!(
        tampered.verify_with_context(&ir, &context).is_err(),
        "co-start safety does not justify discarding publication order"
    );
}

#[test]
fn unknown_output_type_keeps_an_explicit_staging_obligation_until_prepared() {
    let ir = program(vec![
        spawn(0, 0),
        port(1, 0, ReadPort::Stdout, "/managed/output"),
    ]);
    let mut context = finite_context(&ir);
    context
        .process_effects
        .insert(ProcessId(0), mediated_effects("/managed"));
    context
        .operation_facts
        .get_mut(&InstructionId(1))
        .unwrap()
        .destination = FileClass::Unknown;
    let pending = graph::build_with_context(&ir, &context);
    assert!(pending.issues.is_empty(), "{:?}", pending.issues);
    assert!(
        pending
            .refinements
            .iter()
            .any(|refinement| refinement.kind == graph::RefinementKind::OutputPublication)
    );
    assert!(!pending.is_execution_ready());
    assert!(
        pending.publication_dependencies.is_empty(),
        "unknown file/socket destinations are not silently assumed to be private stages"
    );
    assert!(pending.dependencies.iter().any(
        |edge| edge.after == InstructionId(1) && edge.wait == Wait::ProcessSealed(ProcessId(0))
    ));
    context
        .operation_facts
        .get_mut(&InstructionId(1))
        .unwrap()
        .destination = FileClass::Missing;
    context
        .existing_parent_frontiers
        .insert(InstructionId(1), location("/managed"));
    let prepared = graph::build_with_context(&ir, &context);
    assert!(prepared.is_execution_ready());
    assert!(prepared.publication_dependencies.iter().any(
        |edge| edge.after == InstructionId(1) && edge.wait == Wait::ProcessSealed(ProcessId(0))
    ));
}

#[test]
fn mutable_file_sources_do_not_get_the_program_output_publication_exception() {
    let ir = program(vec![
        spawn(0, 0),
        copy(1, "/managed/input", "/managed/output"),
    ]);
    let mut context = finite_context(&ir);
    context
        .process_effects
        .insert(ProcessId(0), mediated_effects("/managed"));
    let graph = graph::build_with_context(&ir, &context);
    assert!(graph.publication_dependencies.is_empty());
    assert!(graph.dependencies.iter().any(|edge| edge.before == InstructionId(0) && edge.after == InstructionId(1) && edge.wait == Wait::ProcessSealed(ProcessId(0)) && edge.reasons.iter().any(|reason| matches!(reason, Reason::Hazard(conflict) if conflict.after.kind == AccessKind::Read))));
}

#[test]
fn retained_process_ports_keep_resource_hazards_and_separate_output_publication() {
    let mut ir = program(vec![
        port(0, 88, ReadPort::Stdout, "/managed/output"),
        port(1, 88, ReadPort::Pid, "/managed/pid"),
    ]);
    let mut context = finite_context(&ir);
    context
        .process_effects
        .insert(ProcessId(88), mediated_effects("/managed"));
    let prepared = graph::build_with_context(&ir, &context);
    assert!(
        prepared
            .external_processes
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(0)
                && requirement.wait == Wait::ProcessStarted(ProcessId(88)))
    );
    assert!(
        !prepared
            .external_processes
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(0)
                && requirement.wait == Wait::ProcessSealed(ProcessId(88)))
    );
    assert!(
        prepared
            .external_publications
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(0)
                && requirement.wait == Wait::ProcessSealed(ProcessId(88)))
    );
    assert!(
        prepared
            .external_publications
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(1)
                && requirement.wait == Wait::ProcessSealed(ProcessId(88))),
        "capturing an immediate pid does not erase the sealed-view publication hazard"
    );
    ir.body.instructions[0] = node(
        0,
        InstructionKind::Transfer {
            source: Input::File {
                location: location("/managed/prompt"),
                pattern: None,
            },
            destination: Output::Stdin(ProcessId(88)),
        },
    );
    let pending = graph::build_with_context(&ir, &context);
    assert!(
        pending
            .external_processes
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(0)
                && requirement.wait == Wait::ProcessSealed(ProcessId(88)))
    );
    context.pinned_inputs.insert(
        InstructionId(0),
        graph::VersionReadProof::pinned_file(
            location("/managed/prompt"),
            "retained-reader",
            "lease-1",
        )
        .unwrap(),
    );
    let prepared = graph::build_with_context(&ir, &context);
    assert!(
        !prepared
            .external_processes
            .iter()
            .any(|requirement| requirement.instruction == InstructionId(0)
                && requirement.wait == Wait::ProcessSealed(ProcessId(88)))
    );
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
                && edge.wait == Wait::ProcessSealed(ProcessId(0))
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
            .all(|requirement| requirement.wait == Wait::ProcessSealed(ProcessId(88)))
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
                && requirement.wait == Wait::ProcessSealed(ProcessId(88)))
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
