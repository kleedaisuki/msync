use msync::endpoint::{Backend, Location};
use msync::hir::control::{ControlInstruction, Expr, ExprKind, SlotId, Type};
use msync::hir::effects;
use msync::hir::graph::{self, Reason};
use msync::hir::{
    BasicBlock, BlockId, Input, Instruction, InstructionId, InstructionKind, Output, Program,
    Region,
};
use msync::language::DestinationMode;
use msync::syntax::Span;
use proptest::prelude::*;

fn node(id: usize, control: bool) -> Instruction {
    Instruction {
        id: InstructionId(id),
        kind: if control {
            InstructionKind::Control(ControlInstruction::Let {
                slot: SlotId(id),
                value: Expr {
                    ty: Type::Bool,
                    kind: ExprKind::Bool(true),
                },
            })
        } else {
            InstructionKind::Transfer {
                source: Input::Literal("value".into()),
                destination: Output::File {
                    location: Location {
                        backend: Backend::Local,
                        path: format!("out-{id}"),
                    },
                    mode: DestinationMode::Exact,
                },
            }
        },
        span: Span::default(),
        expansion: Vec::new(),
    }
}

proptest! {
    #[test]
    fn control_always_owns_its_block(controls in prop::collection::vec(any::<bool>(), 0..100)) {
        let program = Program { body: Region { instructions: controls.iter().enumerate().map(|(id, control)| node(id, *control)).collect() } };
        let blocks: Vec<_> = program.blocks().collect();
        prop_assert_eq!(blocks.iter().map(|block| block.instructions.len()).sum::<usize>(), controls.len());
        for block in blocks {
            if block.instructions.iter().any(|instruction| matches!(instruction.kind, InstructionKind::Control(_))) {
                prop_assert_eq!(block.instructions.len(), 1);
            }
        }
    }
}

#[test]
fn control_has_conservative_effects_even_for_manually_constructed_blocks() {
    let instructions = [node(0, false), node(1, true), node(2, false)];
    assert!(effects::summarize(&instructions[1]).unknown_external);
    let graph = graph::build_block_with_context(
        BasicBlock {
            id: BlockId(0),
            instructions: &instructions,
        },
        &graph::PlanningContext::default(),
    );
    assert!(
        graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(0)
                && edge.after == InstructionId(1)
                && edge.reasons.contains(&Reason::UnknownExternalEffects))
    );
    assert!(
        graph
            .dependencies
            .iter()
            .any(|edge| edge.before == InstructionId(1)
                && edge.after == InstructionId(2)
                && edge.reasons.contains(&Reason::UnknownExternalEffects))
    );
    assert!(
        !graph
            .commutations
            .iter()
            .any(|proof| proof.left == InstructionId(1) || proof.right == InstructionId(1))
    );
}

#[test]
fn preparation_preserves_control_and_detects_modified_typed_payload() {
    let program = Program {
        body: Region {
            instructions: vec![node(0, true)],
        },
    };
    let mut prepared = msync::preparation::prepare(&program, true).unwrap();
    prepared.verify().unwrap();
    assert!(prepared.to_string().contains("control let"));
    let InstructionKind::Control(ControlInstruction::Let { value, .. }) =
        &mut prepared.program.body.instructions[0].kind
    else {
        panic!("let control")
    };
    value.kind = ExprKind::Bool(false);
    assert!(prepared.verify().is_err());
}

#[test]
fn preparation_does_not_inspect_conditions_or_unselected_bodies() {
    let condition = Expr {
        ty: Type::Bool,
        kind: ExprKind::Exists(Box::new(Expr {
            ty: Type::Path,
            kind: ExprKind::Path {
                location: Location {
                    backend: Backend::Local,
                    path: "/missing-control-condition-v6".into(),
                },
                mode: DestinationMode::Exact,
                relocation: None,
            },
        })),
    };
    let body = Region {
        instructions: vec![Instruction {
            kind: InstructionKind::Transfer {
                source: Input::File {
                    location: Location {
                        backend: Backend::Local,
                        path: "/missing-control-source-v6".into(),
                    },
                    pattern: None,
                },
                destination: Output::File {
                    location: Location {
                        backend: Backend::Local,
                        path: "/missing-control-destination-v6".into(),
                    },
                    mode: DestinationMode::Exact,
                },
            },
            ..node(1, false)
        }],
    };
    let program = Program {
        body: Region {
            instructions: vec![Instruction {
                kind: InstructionKind::Control(ControlInstruction::If {
                    condition,
                    then_body: body,
                    else_body: Region::default(),
                }),
                ..node(0, true)
            }],
        },
    };
    let prepared = msync::preparation::prepare(&program, true).unwrap();
    prepared.verify().unwrap();
    assert!(prepared.facts.is_empty());
}

#[test]
fn fence_normalization_preserves_explicit_acquire_before_control() {
    let mut region = Region {
        instructions: vec![
            node(0, false),
            Instruction {
                kind: InstructionKind::Barrier {
                    kind: msync::hir::BarrierKind::Before,
                },
                ..node(1, false)
            },
            node(2, true),
        ],
    };
    let removed = msync::hir::rewrite::normalize_fences(&mut region);
    assert!(!removed.contains(&InstructionId(1)));
    assert!(matches!(
        region.instructions[1].kind,
        InstructionKind::Barrier {
            kind: msync::hir::BarrierKind::Before
        }
    ));
}
