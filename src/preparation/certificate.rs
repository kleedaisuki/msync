use super::optimize;
use crate::hir::{self, InstructionKind, Output, Region};
pub(super) fn same_program(left: &hir::Program, right: &hir::Program) -> bool {
    same_region(&left.body, &right.body)
}

fn same_region(left: &Region, right: &Region) -> bool {
    left.instructions.len() == right.instructions.len()
        && left
            .instructions
            .iter()
            .zip(&right.instructions)
            .all(|(a, b)| {
                if a.id != b.id || a.span != b.span || a.expansion != b.expansion {
                    return false;
                }
                match (&a.kind, &b.kind) {
                    (
                        InstructionKind::Transfer {
                            source: a,
                            destination: ad,
                        },
                        InstructionKind::Transfer {
                            source: b,
                            destination: bd,
                        },
                    ) => optimize::value(a) == optimize::value(b) && same_output(ad, bd),
                    (
                        InstructionKind::Barrier { kind: a },
                        InstructionKind::Barrier { kind: b },
                    ) => a == b,
                    (
                        InstructionKind::Await { process: a },
                        InstructionKind::Await { process: b },
                    ) => a == b,
                    (
                        InstructionKind::Spawn {
                            process: a,
                            executable: ae,
                            arguments: aa,
                            cwd: ac,
                        },
                        InstructionKind::Spawn {
                            process: b,
                            executable: be,
                            arguments: ba,
                            cwd: bc,
                        },
                    ) => {
                        a == b
                            && ae == be
                            && ac == bc
                            && aa.len() == ba.len()
                            && aa.iter().zip(ba).all(|(a, b)| match (a, b) {
                                (hir::Argument::Text(a), hir::Argument::Text(b)) => a == b,
                                (hir::Argument::Path(a), hir::Argument::Path(b)) => a == b,
                                _ => false,
                            })
                    }
                    _ => false,
                }
            })
}

fn same_output(left: &Output, right: &Output) -> bool {
    match (left, right) {
        (
            Output::File {
                location: a,
                mode: am,
            },
            Output::File {
                location: b,
                mode: bm,
            },
        ) => a == b && am == bm,
        (Output::Stdin(a), Output::Stdin(b)) => a == b,
        _ => false,
    }
}

#[cfg(test)]
mod tests {
    use super::same_program;
    use crate::hir::{
        BarrierKind, Instruction, InstructionId, InstructionKind, ProcessId, Program, Region,
    };
    use crate::syntax::Span;

    fn program(kind: InstructionKind) -> Program {
        Program {
            body: Region {
                instructions: vec![Instruction {
                    id: InstructionId(0),
                    kind,
                    span: Span::default(),
                    expansion: Vec::new(),
                }],
            },
        }
    }

    #[test]
    fn certificates_preserve_barrier_direction() {
        let before = program(InstructionKind::Barrier {
            kind: BarrierKind::Before,
        });
        let after = program(InstructionKind::Barrier {
            kind: BarrierKind::After,
        });
        assert!(same_program(&before, &before.clone()));
        assert!(!same_program(&before, &after));
    }

    #[test]
    fn certificates_preserve_await_target() {
        let all = program(InstructionKind::Await { process: None });
        let first = program(InstructionKind::Await {
            process: Some(ProcessId(1)),
        });
        let second = program(InstructionKind::Await {
            process: Some(ProcessId(2)),
        });
        assert!(same_program(&first, &first.clone()));
        assert!(!same_program(&all, &first));
        assert!(!same_program(&first, &second));
    }
}
