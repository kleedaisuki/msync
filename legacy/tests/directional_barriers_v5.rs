//! Directional fence normalization precedes serial block interpretation.

use std::collections::BTreeSet;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use msync::compiler::{CompileContext, structured::Session};
use msync::endpoint::{Backend, Location};
use msync::hir::graph::{self, Wait};
use msync::hir::{
    BarrierKind, Input, Instruction, InstructionId, InstructionKind, Output, ProcessId, Program,
    Region,
};
use msync::language::DestinationMode;
use msync::source::Source;
use msync::syntax::Span;
use proptest::prelude::*;

fn context() -> CompileContext {
    let pwd =
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/directional-fences-not-created");
    CompileContext {
        source_file: Some(pwd.join("module.msync")),
        pwd,
    }
}

fn compile(text: &str) -> Program {
    Session::new()
        .compile(&msync::language::parse(text).unwrap(), &context())
        .unwrap()
}

fn transfer_ids(program: &Program) -> Vec<InstructionId> {
    program
        .body
        .instructions
        .iter()
        .filter_map(|node| matches!(node.kind, InstructionKind::Transfer { .. }).then_some(node.id))
        .collect()
}

fn position(program: &Program, id: InstructionId) -> usize {
    program
        .body
        .instructions
        .iter()
        .position(|node| node.id == id)
        .unwrap()
}

fn node(id: usize, kind: InstructionKind) -> Instruction {
    Instruction {
        id: InstructionId(id),
        kind,
        span: Span {
            start: id,
            end: id + 1,
        },
        expansion: Vec::new(),
    }
}

fn transfer(id: usize, name: &str) -> Instruction {
    node(
        id,
        InstructionKind::Transfer {
            source: Input::Literal(Arc::from(name)),
            destination: Output::File {
                location: Location {
                    backend: Backend::Local,
                    path: context().pwd.join(name).to_string_lossy().into_owned(),
                },
                mode: DestinationMode::Exact,
            },
        },
    )
}

#[test]
fn async_allows_following_file_work_into_the_preceding_block() {
    let program = compile("\"left\" -> left\nasync \"right\" -> right\n");
    assert_eq!(program.blocks().count(), 1);
    assert_eq!(transfer_ids(&program), [InstructionId(0), InstructionId(2)]);
    assert_eq!(program.body.instructions.len(), 2);
}

#[test]
fn await_allows_preceding_file_work_into_the_following_block() {
    let program = compile("await \"left\" -> left\n\"right\" -> right\n");
    assert_eq!(program.blocks().count(), 1);
    assert_eq!(transfer_ids(&program), [InstructionId(0), InstructionId(2)]);
    assert_eq!(program.body.instructions.len(), 2);
}

#[test]
fn opposing_fences_preserve_the_forbidden_crossing_directions() {
    let mut program = Program {
        body: Region {
            instructions: vec![
                transfer(0, "earlier"),
                node(
                    1,
                    InstructionKind::Barrier {
                        kind: BarrierKind::Before,
                    },
                ),
                transfer(2, "middle"),
                node(
                    3,
                    InstructionKind::Barrier {
                        kind: BarrierKind::After,
                    },
                ),
                transfer(4, "later"),
            ],
        },
    };
    msync::hir::rewrite::normalize_fences(&mut program.body);
    assert_eq!(
        transfer_ids(&program),
        [InstructionId(0), InstructionId(2), InstructionId(4)]
    );
    assert!(position(&program, InstructionId(0)) < position(&program, InstructionId(1)));
    assert!(position(&program, InstructionId(2)) < position(&program, InstructionId(1)));
    assert!(position(&program, InstructionId(1)) < position(&program, InstructionId(3)));
    assert!(position(&program, InstructionId(3)) < position(&program, InstructionId(4)));
    assert_eq!(program.blocks().count(), 2);
}

#[test]
fn reciprocal_vacuous_fences_disappear_instead_of_retaining_empty_blocks() {
    let program = compile("await \"left\" -> left\nasync \"right\" -> right\n");
    assert_eq!(program.blocks().count(), 1);
    assert_eq!(program.body.instructions.len(), 2);
    assert_eq!(transfer_ids(&program), [InstructionId(0), InstructionId(3)]);
}

#[test]
fn real_data_hazards_survive_fence_elimination() {
    let program = compile("\"old\" -> state\nasync \"new\" -> state\nstate -> result\n");
    assert_eq!(program.blocks().count(), 1);
    let ids = transfer_ids(&program);
    assert_eq!(ids, [InstructionId(0), InstructionId(2), InstructionId(3)]);
    let graph = graph::build(&program);
    assert!(graph.issues.is_empty());
    for (before, after) in [(ids[0], ids[1]), (ids[1], ids[2])] {
        assert!(graph.dependencies.iter().any(|edge| {
            edge.before == before && edge.after == after && edge.wait == Wait::Completion
        }));
    }
    graph.verify(&program).unwrap();
}

#[test]
fn real_launch_and_join_controls_still_order_future_file_observation() {
    let program = compile("Maker => $path::not-a-real-generator()\ngenerated -> result\n");
    let launch = program
        .body
        .instructions
        .iter()
        .find(|node| matches!(node.kind, InstructionKind::Spawn { .. }))
        .unwrap();
    let join = program
        .body
        .instructions
        .iter()
        .find(|node| matches!(node.kind, InstructionKind::Await { process: Some(_) }))
        .unwrap();
    let output = program
        .body
        .instructions
        .iter()
        .find(|node| matches!(node.kind, InstructionKind::Transfer { .. }))
        .unwrap();
    assert!(position(&program, launch.id) < position(&program, join.id));
    assert!(position(&program, join.id) < position(&program, output.id));
    let block_of = |id| {
        program
            .blocks()
            .find(|block| block.instructions.iter().any(|node| node.id == id))
            .unwrap()
            .id
    };
    assert!(block_of(launch.id) < block_of(join.id));
    assert!(block_of(join.id) < block_of(output.id));
    assert!(matches!(
        program.body.instructions[0].kind,
        InstructionKind::Barrier {
            kind: BarrierKind::Before
        }
    ));
}

#[test]
fn async_output_flow_and_targeted_await_keep_the_user_three_block_shape() {
    let program = compile(
        "async Program => $path::not-a-real-program()\nProgram::stdout -> result\nawait Program\n",
    );
    assert_eq!(program.blocks().count(), 3);
    assert_eq!(
        program
            .body
            .instructions
            .iter()
            .filter(|node| matches!(
                node.kind,
                InstructionKind::Await {
                    process: Some(ProcessId(0))
                }
            ))
            .count(),
        1
    );
    let join_all = compile("async Program => $path::not-a-real-program()\nawait\n");
    assert_eq!(
        join_all
            .body
            .instructions
            .iter()
            .filter(|node| matches!(node.kind, InstructionKind::Await { process: None }))
            .count(),
        1
    );
}

#[test]
fn a_native_observation_anchor_does_not_depend_on_a_synthetic_launch_fence() {
    let mut program = Program {
        body: generated_region(&[0, 2, 0, 4]),
    };
    msync::hir::rewrite::normalize_fences(&mut program.body);
    let blocks: Vec<_> = program.blocks().collect();
    assert_eq!(blocks.len(), 2);
    assert_eq!(
        blocks[0]
            .instructions
            .iter()
            .map(|node| node.id)
            .collect::<Vec<_>>(),
        [InstructionId(0), InstructionId(2)],
    );
    assert!(matches!(
        blocks[1].instructions,
        [Instruction {
            kind: InstructionKind::Spawn { .. },
            ..
        }],
    ));
}

#[test]
fn eliminated_fences_do_not_leave_dangling_macro_debug_origins() {
    let text = "macro work():\n    \"left\" -> left\n    async \"right\" -> right\nwork()\n";
    let source = Source::new("directional.msync", text);
    let lowered = Session::new()
        .compile_named(&msync::language::parse(text).unwrap(), &context(), source)
        .unwrap();
    assert_eq!(lowered.program.blocks().count(), 1);
    let actual: BTreeSet<_> = lowered
        .program
        .body
        .instructions
        .iter()
        .map(|node| node.id)
        .collect();
    let debug: BTreeSet<_> = lowered.origins.keys().copied().collect();
    assert_eq!(actual, debug);
    assert_eq!(actual, BTreeSet::from([InstructionId(0), InstructionId(2)]));
    for origin in lowered.origins.values() {
        assert!(!origin.expansion.is_empty());
        assert!(origin.site.span.end <= origin.site.source.text.len());
        assert!(origin.definition.span.end <= origin.definition.source.text.len());
    }
    assert!(
        lowered
            .relocations
            .iter()
            .all(|entry| actual.contains(&entry.instruction))
    );
}

#[test]
fn exported_objects_retain_normalized_operations_and_precise_source_ownership() {
    let text = "\"left\" -> left\nasync \"right\" -> right\n";
    let module = msync::pipeline::compile_object_named(
        &mut Session::new(),
        text,
        "directional.msync",
        &context(),
    )
    .unwrap();
    assert_eq!(module.program.blocks().count(), 1);
    let decoded = msync::object::ObjectModule::decode(&module.encode().unwrap()).unwrap();
    assert_eq!(
        serde_json::to_value(&decoded.program).unwrap(),
        serde_json::to_value(&module.program).unwrap()
    );
    let (_, _, origins, _) = decoded.debug.materialize().unwrap();
    let ids: BTreeSet<_> = decoded
        .program
        .body
        .instructions
        .iter()
        .map(|node| node.id)
        .collect();
    assert_eq!(ids, origins.keys().copied().collect());
    assert_eq!(ids, BTreeSet::from([InstructionId(0), InstructionId(2)]));
    let bound = msync::context::contextualize(
        &decoded,
        &msync::context::Context::new(&context()),
        &mut Session::new(),
    )
    .unwrap();
    assert_eq!(bound.program.blocks().count(), 1);
    for instruction in bound.program.body.instructions {
        let InstructionKind::Transfer {
            destination: Output::File { location, .. },
            ..
        } = instruction.kind
        else {
            panic!("the object must contain exactly its two file transfers")
        };
        assert_eq!(
            Path::new(&location.path).parent(),
            Some(context().pwd.as_path())
        );
    }
}

#[test]
fn redundant_adjacent_fences_do_not_keep_duplicate_control_nodes() {
    let mut region = Region {
        instructions: vec![
            transfer(0, "earlier"),
            node(
                1,
                InstructionKind::Barrier {
                    kind: BarrierKind::Before,
                },
            ),
            node(
                2,
                InstructionKind::Barrier {
                    kind: BarrierKind::Before,
                },
            ),
            transfer(3, "middle"),
            node(
                4,
                InstructionKind::Barrier {
                    kind: BarrierKind::After,
                },
            ),
            node(
                5,
                InstructionKind::Barrier {
                    kind: BarrierKind::After,
                },
            ),
            transfer(6, "later"),
        ],
    };
    let removed = msync::hir::rewrite::normalize_fences(&mut region);
    assert_eq!(removed.len(), 2);
    for direction in [BarrierKind::Before, BarrierKind::After] {
        assert_eq!(region.instructions.iter().filter(|node| matches!(node.kind, InstructionKind::Barrier { kind } if kind == direction)).count(), 1);
    }
    assert_eq!(Program { body: region }.blocks().count(), 2);
}
#[test]
fn original_fence_slots_allow_a_middle_transfer_to_join_following_work() {
    let mut program = Program {
        body: generated_region(&[1, 0, 2, 0]),
    };
    msync::hir::rewrite::normalize_fences(&mut program.body);
    assert_eq!(transfer_ids(&program), [InstructionId(1), InstructionId(3)]);
    assert_eq!(program.blocks().count(), 1);
}

#[test]
fn original_fence_slots_do_not_freeze_a_bad_intermediate_grouping() {
    let mut program = Program {
        body: generated_region(&[0, 1, 2, 1, 0, 2, 0]),
    };
    msync::hir::rewrite::normalize_fences(&mut program.body);
    assert_eq!(
        transfer_ids(&program),
        [InstructionId(0), InstructionId(4), InstructionId(6)]
    );
    let groups: Vec<_> = program
        .blocks()
        .map(|block| {
            block
                .instructions
                .iter()
                .filter_map(|node| {
                    matches!(node.kind, InstructionKind::Transfer { .. }).then_some(node.id)
                })
                .collect::<Vec<_>>()
        })
        .filter(|group| !group.is_empty())
        .collect();
    assert_eq!(
        groups,
        [
            vec![InstructionId(0)],
            vec![InstructionId(4), InstructionId(6)]
        ]
    );
    assert_eq!(program.blocks().count(), 2);
}

fn minimum_transfer_groups(kinds: &[u8]) -> usize {
    let fences: Vec<_> = kinds
        .iter()
        .enumerate()
        .filter(|(_, kind)| **kind != 0)
        .map(|(position, kind)| (position, *kind))
        .collect();
    let intervals: Vec<_> = kinds
        .iter()
        .enumerate()
        .filter(|(_, kind)| **kind == 0)
        .map(|(position, _)| {
            let lower = fences
                .iter()
                .enumerate()
                .filter(|(_, (site, kind))| *site < position && *kind == 2)
                .map(|(ordinal, _)| ordinal + 1)
                .max()
                .unwrap_or(0);
            let upper = fences
                .iter()
                .enumerate()
                .filter(|(_, (site, kind))| *site > position && *kind == 1)
                .map(|(ordinal, _)| ordinal)
                .min()
                .unwrap_or(fences.len());
            (lower, upper)
        })
        .collect();
    let mut optimum = vec![usize::MAX; intervals.len() + 1];
    optimum[0] = 0;
    for end in 1..=intervals.len() {
        let mut lower = 0;
        let mut upper = fences.len();
        for start in (0..end).rev() {
            lower = lower.max(intervals[start].0);
            upper = upper.min(intervals[start].1);
            if lower > upper {
                break;
            }
            optimum[end] = optimum[end].min(optimum[start] + 1);
        }
    }
    optimum[intervals.len()]
}
fn generated_region(kinds: &[u8]) -> Region {
    Region {
        instructions: kinds
            .iter()
            .enumerate()
            .map(|(id, kind)| match kind {
                0 => transfer(id, &format!("file-{id}")),
                1 => node(
                    id,
                    InstructionKind::Barrier {
                        kind: BarrierKind::Before,
                    },
                ),
                2 => node(
                    id,
                    InstructionKind::Barrier {
                        kind: BarrierKind::After,
                    },
                ),
                3 => node(id, InstructionKind::Await { process: None }),
                _ => node(
                    id,
                    InstructionKind::Spawn {
                        process: ProcessId(id),
                        executable: Location {
                            backend: Backend::Local,
                            path: "not-inspected".into(),
                        },
                        arguments: Vec::new(),
                        cwd: context().pwd,
                    },
                ),
            })
            .collect(),
    }
}

proptest! {
#![proptest_config(ProptestConfig::with_cases(96))]

#[test]
fn normalization_is_idempotent_and_preserves_every_effectful_operation(
    kinds in prop::collection::vec(0u8..5, 0..72)
) {
    let mut region = generated_region(&kinds);
    let before: Vec<_> = region.instructions.iter().filter(|node| !matches!(node.kind, InstructionKind::Barrier { .. })).map(|node| serde_json::to_value(node).unwrap()).collect();
    let original_fences: BTreeSet<_> = region.instructions.iter().filter(|node| matches!(node.kind, InstructionKind::Barrier { .. })).map(|node| node.id).collect();
    let removed = msync::hir::rewrite::normalize_fences(&mut region);
    let after: Vec<_> = region.instructions.iter().filter(|node| !matches!(node.kind, InstructionKind::Barrier { .. })).map(|node| serde_json::to_value(node).unwrap()).collect();
    prop_assert_eq!(before, after);
    prop_assert_eq!(removed.iter().copied().collect::<BTreeSet<_>>().len(), removed.len());
    prop_assert!(removed.iter().all(|id| original_fences.contains(id)));
    let once = serde_json::to_value(&region).unwrap();
    prop_assert!(msync::hir::rewrite::normalize_fences(&mut region).is_empty());
    prop_assert_eq!(once, serde_json::to_value(&region).unwrap());
}

#[test]
fn normalized_transfer_groups_match_an_independent_dynamic_program(
    kinds in prop::collection::vec(0u8..3, 0..36)
) {
    let expected = minimum_transfer_groups(&kinds);
    let mut program = Program { body: generated_region(&kinds) };
    msync::hir::rewrite::normalize_fences(&mut program.body);
    let actual = program.blocks().filter(|block| block.instructions.iter().any(|node| matches!(node.kind, InstructionKind::Transfer { .. }))).count();
    prop_assert_eq!(actual, expected);
    prop_assert_eq!(program.blocks().count(), expected);
}}
