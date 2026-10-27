use std::sync::Arc;

use msync::compiler::{Backend, Location};
use msync::hir::effects::{Access, AccessKind, Effects, FileResource, Resource, Scope};
use msync::hir::graph::{self, Reason, Wait};
use msync::hir::{
    Argument, Input, Instruction, InstructionId, InstructionKind, Output, ProcessId, Program,
    ReadPort, Region, RollbackScope, TransactionId,
};
use msync::language::DestinationMode;
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

#[test]
fn literal_writes_have_no_read_effect_and_disjoint_paths_commute() {
    let ir = program(vec![literal(0, "/workspace/a"), literal(1, "/workspace/b")]);
    let graph = graph::build(&ir);
    assert!(graph.dependencies.is_empty());
    assert_eq!(graph.commutations.len(), 1);
    assert_eq!(graph.nodes[0].effects.accesses.len(), 1);
    assert_eq!(graph.nodes[0].effects.accesses[0].kind, AccessKind::Write);
    assert!(graph.verify(&ir).is_ok());
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
    let graph = graph::build(&ir);
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
    assert!(graph.verify(&ir).is_ok());
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
    let graph = graph::build(&ir);
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
    assert!(graph.verify(&ir).is_ok());
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
    files: [Vec<u8>; 3],
    reads: std::collections::BTreeMap<usize, Vec<u8>>,
}

fn evaluate(state: &mut ModelState, op: &ModelOp, id: usize) {
    let bytes = if op.literal {
        vec![op.value]
    } else {
        let bytes = state.files[op.source].clone();
        state.reads.insert(id, bytes.clone());
        bytes
    };
    state.files[op.destination] = bytes;
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
        let instructions = operations.iter().enumerate().map(|(id, operation)| {
            // The oracle uses resource indices, not the planner's path keys.
            // Distinct lexical aliases intentionally identify the same model slot.
            let source = if id % 2 == 0 {
                format!("/r/./{}", operation.source)
            } else {
                format!("/r/unused/../{}", operation.source)
            };
            let destination = format!("/r/{}", operation.destination);
            if operation.literal {
                node(id, InstructionKind::Transfer {
                    source: Input::Literal(Arc::from(char::from(operation.value).to_string())),
                    destination: Output::File { location: location(&destination), mode: DestinationMode::Exact },
                })
            } else {
                copy(id, &source, &destination)
            }
        }).collect();
        let graph = graph::build(&program(instructions));
        let initial = ModelState { files: [vec![0], vec![1], vec![2]], reads: Default::default() };
        let mut expected = initial.clone();
        for (id, operation) in operations.iter().enumerate() {
            evaluate(&mut expected, operation, id);
        }
        prop_assert!(all_orders_preserve(&graph, &operations, &expected, &initial, 0), "an admitted schedule changed source-order file values or per-operation read observations: {operations:?}");
    }
}
