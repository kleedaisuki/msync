use std::time::Duration;

use msync::execution::{Options, RuntimeSession};
use msync::hir::control::{ControlInstruction, Expr, ExprKind, SlotId, Type};
use msync::hir::{Instruction, InstructionId, InstructionKind, Program, Region};
use msync::syntax::Span;

fn fixture() -> tempfile::TempDir {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/control-runtime-tests");
    std::fs::create_dir_all(&root).expect("create test root");
    tempfile::tempdir_in(root).expect("create test fixture")
}

fn instruction(id: usize, operation: ControlInstruction) -> Instruction {
    Instruction {
        id: InstructionId(id),
        kind: InstructionKind::Control(operation),
        span: Span::default(),
        expansion: Vec::new(),
    }
}

#[tokio::test]
async fn open_ended_loop_cooperatively_observes_cancellation() {
    let fixture = fixture();
    let options = Options::new(fixture.path());
    let cancel = options.cancel.clone();
    let mut session = RuntimeSession::new(&options).expect("runtime");
    let program = Program {
        body: Region {
            instructions: vec![instruction(
                0,
                ControlInstruction::Loop {
                    body: Region::default(),
                },
            )],
        },
    };
    let signal = tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(20)).await;
        cancel.cancel();
    });
    let result = tokio::time::timeout(Duration::from_secs(3), session.execute(&program))
        .await
        .expect("loop must yield to cancellation");
    assert!(result.is_err(), "cancellation is not successful completion");
    signal.await.expect("cancellation signal");
    session.shutdown().await.expect("clean shutdown");
}

#[tokio::test]
async fn scoped_expression_binding_restores_outer_slot_on_failure() {
    let fixture = fixture();
    let options = Options::new(fixture.path());
    let mut session = RuntimeSession::new(&options).expect("runtime");
    let slot = SlotId(0);
    let value = Expr {
        ty: Type::Bool,
        kind: ExprKind::Bool(true),
    };
    session
        .execute(&Program {
            body: Region {
                instructions: vec![instruction(0, ControlInstruction::Let { slot, value })],
            },
        })
        .await
        .expect("initialize retained slot");
    let expression = Expr {
        ty: Type::Bool,
        kind: ExprKind::Let {
            slot,
            value: Box::new(Expr {
                ty: Type::Bool,
                kind: ExprKind::Bool(false),
            }),
            body: Box::new(Expr {
                ty: Type::Bool,
                kind: ExprKind::Slot(SlotId(999)),
            }),
        },
    };
    assert!(
        session
            .execute(&Program {
                body: Region {
                    instructions: vec![instruction(
                        1,
                        ControlInstruction::Let {
                            slot: SlotId(1),
                            value: expression
                        }
                    )]
                }
            })
            .await
            .is_err()
    );
    // A false retained slot would select a deliberately uninitialized read.
    let probe = ControlInstruction::If {
        condition: Expr {
            ty: Type::Bool,
            kind: ExprKind::Slot(slot),
        },
        then_body: Region::default(),
        else_body: Region {
            instructions: vec![instruction(
                3,
                ControlInstruction::Let {
                    slot: SlotId(2),
                    value: Expr {
                        ty: Type::Bool,
                        kind: ExprKind::Slot(SlotId(999)),
                    },
                },
            )],
        },
    };
    session
        .execute(&Program {
            body: Region {
                instructions: vec![instruction(2, probe)],
            },
        })
        .await
        .expect("outer slot survives failed scoped evaluation");
    session.shutdown().await.expect("clean shutdown");
}

#[tokio::test]
async fn untraced_control_dispatch_preserves_nested_iteration_and_scalar_updates() {
    use msync::hir::control::{BinaryOp, RangeExpr, RangeSource};

    assert!(msync::telemetry::context().is_none());
    let fixture = fixture();
    let mut session = RuntimeSession::new(&Options::new(fixture.path())).unwrap();
    let text = |value: &str| Expr {
        ty: Type::String,
        kind: ExprKind::String(value.into()),
    };
    let state = SlotId(0);
    let item = SlotId(1);
    let program = Program {
        body: Region {
            instructions: vec![
                instruction(
                    0,
                    ControlInstruction::Let {
                        slot: state,
                        value: text("initial"),
                    },
                ),
                instruction(
                    1,
                    ControlInstruction::For {
                        slot: item,
                        range: RangeExpr {
                            source: RangeSource::Values(vec![text("first"), text("last")]),
                            stages: Vec::new(),
                            item_type: Type::String,
                        },
                        body: Region {
                            instructions: vec![instruction(
                                2,
                                ControlInstruction::Set {
                                    slot: state,
                                    value: Expr {
                                        ty: Type::String,
                                        kind: ExprKind::Slot(item),
                                    },
                                },
                            )],
                        },
                    },
                ),
                instruction(
                    3,
                    ControlInstruction::If {
                        condition: Expr {
                            ty: Type::Bool,
                            kind: ExprKind::Binary {
                                left: Box::new(Expr {
                                    ty: Type::String,
                                    kind: ExprKind::Slot(state),
                                }),
                                op: BinaryOp::Eq,
                                right: Box::new(text("last")),
                            },
                        },
                        then_body: Region::default(),
                        else_body: Region {
                            instructions: vec![instruction(
                                4,
                                ControlInstruction::Let {
                                    slot: SlotId(2),
                                    value: Expr {
                                        ty: Type::Bool,
                                        kind: ExprKind::Slot(SlotId(999)),
                                    },
                                },
                            )],
                        },
                    },
                ),
            ],
        },
    };
    let report = session.execute(&program).await.unwrap();
    assert_eq!(report.assignments, 0);
    assert!(!session.has_work());
    assert!(msync::telemetry::context().is_none());
    session.shutdown().await.unwrap();
}
