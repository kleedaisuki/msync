//! Interpreter-only probes. Build directly with rustc against a frozen msync rlib.
use std::path::{Path, PathBuf};
use std::time::Instant;

use msync::endpoint::{Backend, Location};
use msync::execution::{Options, execute};
use msync::hir::control::{
    BinaryOp, ControlInstruction as Control, Expr, ExprKind, RangeExpr, RangeQuery, RangeSource,
    SlotId, Type,
};
use msync::hir::{Instruction, InstructionId, InstructionKind, Program, Region};
use msync::language::DestinationMode;
use msync::syntax::Span;

fn expression(ty: Type, kind: ExprKind) -> Expr {
    Expr { ty, kind }
}
fn boolean(value: bool) -> Expr {
    expression(Type::Bool, ExprKind::Bool(value))
}
fn statement(id: usize, kind: Control) -> Instruction {
    Instruction {
        id: InstructionId(id),
        kind: InstructionKind::Control(kind),
        span: Span::default(),
        expansion: Vec::new(),
    }
}
fn region(instructions: Vec<Instruction>) -> Region {
    Region { instructions }
}
fn program(root: &Path, count: usize, filesystem: bool) -> Program {
    let condition = if filesystem {
        let path = expression(
            Type::Path,
            ExprKind::Path {
                location: Location {
                    backend: Backend::Local,
                    path: root.join("input").to_string_lossy().into_owned(),
                },
                mode: DestinationMode::Exact,
                relocation: None,
            },
        );
        expression(
            Type::Bool,
            ExprKind::Not(Box::new(expression(
                Type::Bool,
                ExprKind::RangeQuery {
                    range: Box::new(RangeExpr {
                        source: RangeSource::Regex {
                            root: Box::new(path),
                            pattern: "[a-z]+[.]toml".into(),
                        },
                        stages: Vec::new(),
                        item_type: Type::Path,
                    }),
                    query: RangeQuery::Empty,
                },
            ))),
        )
    } else {
        expression(
            Type::Bool,
            ExprKind::Binary {
                left: Box::new(expression(Type::String, ExprKind::Slot(SlotId(1)))),
                op: BinaryOp::Eq,
                right: Box::new(expression(Type::String, ExprKind::String("entry".into()))),
            },
        )
    };
    let values = (0..count)
        .map(|_| expression(Type::String, ExprKind::String("entry".into())))
        .collect();
    let body = region(vec![statement(
        2,
        Control::If {
            condition,
            then_body: region(vec![statement(
                3,
                Control::Set {
                    slot: SlotId(0),
                    value: boolean(true),
                },
            )]),
            else_body: region(vec![statement(
                4,
                Control::Let {
                    slot: SlotId(2),
                    value: expression(Type::Bool, ExprKind::Slot(SlotId(999))),
                },
            )]),
        },
    )]);
    Program {
        body: region(vec![
            statement(
                0,
                Control::Let {
                    slot: SlotId(0),
                    value: boolean(false),
                },
            ),
            statement(
                1,
                Control::For {
                    slot: SlotId(1),
                    range: RangeExpr {
                        source: RangeSource::Values(values),
                        stages: Vec::new(),
                        item_type: Type::String,
                    },
                    body,
                },
            ),
            statement(
                5,
                Control::If {
                    condition: expression(Type::Bool, ExprKind::Slot(SlotId(0))),
                    then_body: Region::default(),
                    else_body: region(vec![statement(
                        6,
                        Control::Let {
                            slot: SlotId(3),
                            value: expression(Type::Bool, ExprKind::Slot(SlotId(999))),
                        },
                    )]),
                },
            ),
        ]),
    }
}
fn main() {
    let count: usize = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "10000".into())
        .parse()
        .unwrap();
    let samples: usize = std::env::args()
        .nth(2)
        .unwrap_or_else(|| "7".into())
        .parse()
        .unwrap();
    assert!(count > 0 && samples > 0);
    let root: PathBuf = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/runtime-perf/fixture");
    std::fs::create_dir_all(root.join("input")).unwrap();
    std::fs::write(root.join("input/config.toml"), "verified input").unwrap();
    let options = Options::new(&root);
    for filesystem in [false, true] {
        let program = program(&root, count, filesystem);
        let case = if filesystem {
            "repeat-range"
        } else {
            "pure-control"
        };
        for sample in 0..samples + 1 {
            let start = Instant::now();
            execute(&program, &options).unwrap();
            let elapsed = start.elapsed();
            if sample > 0 {
                println!(
                    "{case},{count},{},{:.6}",
                    sample - 1,
                    elapsed.as_secs_f64() * 1000.0
                );
            }
        }
    }
}
