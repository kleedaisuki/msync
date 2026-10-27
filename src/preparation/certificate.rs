use super::optimize;
use crate::hir::{self, InstructionKind, Output, Region};
pub(super) fn same_program(left: &hir::Program, right: &hir::Program) -> bool {
    if left.transactions.len() != right.transactions.len() {
        return false;
    }
    if !left
        .transactions
        .iter()
        .zip(&right.transactions)
        .all(|(a, b)| {
            a.id == b.id && a.name == b.name && a.isolation == b.isolation && a.span == b.span
        })
    {
        return false;
    }
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
                        InstructionKind::Transaction {
                            transaction: a,
                            body: ab,
                        },
                        InstructionKind::Transaction {
                            transaction: b,
                            body: bb,
                        },
                    ) => a == b && same_region(ab, bb),
                    (
                        InstructionKind::Commit { transaction: a },
                        InstructionKind::Commit { transaction: b },
                    ) => a == b,
                    (
                        InstructionKind::Rollback {
                            transaction: a,
                            scope: ascope,
                        },
                        InstructionKind::Rollback {
                            transaction: b,
                            scope: bscope,
                        },
                    ) => a == b && ascope == bscope,
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
