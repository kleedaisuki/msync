//! Scalar reassignment retains static folding and explicit runtime cell ownership.

use std::path::PathBuf;

use msync::compiler::{CompileContext, structured::Session};
use msync::hir::control::{ControlInstruction, ExprKind, SlotId, Type};
use msync::hir::{Input, InstructionKind, Program, Region};
use msync::language;

fn compile(source: &str) -> Program {
    Session::new()
        .compile(
            &language::parse(source).unwrap(),
            &CompileContext {
                pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
                source_file: None,
            },
        )
        .unwrap()
}

fn controls(region: &Region) -> Vec<&ControlInstruction> {
    region
        .instructions
        .iter()
        .filter_map(|instruction| {
            if let InstructionKind::Control(control) = &instruction.kind {
                Some(control)
            } else {
                None
            }
        })
        .collect()
}

#[test]
fn static_same_type_reassignment_still_folds_without_runtime_cells() {
    let program = compile(
        "enabled = false\nenabled = not enabled\ntext = \"before\"\ntext = \"after\"\nif enabled:\n    text -> result\n",
    );
    assert_eq!(program.body.instructions.len(), 1);
    assert!(matches!(&program.body.instructions[0].kind,
        InstructionKind::Transfer { source: Input::Literal(value), .. } if &**value == "after"));
}

#[test]
fn assigning_another_scalar_type_is_rejected_in_script_and_pure_function() {
    for source in [
        "value = false\nvalue = \"string\"\n",
        "value = \"string\"\nvalue = true\n",
        "boolean invalid():\n    value = false\n    value = \"string\"\n    return true\ninvalid()\n",
        "symbol invalid():\n    value = \"string\"\n    value = false\n    return value\ninvalid()\n",
    ] {
        let result = Session::new().compile(
            &language::parse(source).unwrap(),
            &CompileContext {
                pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
                source_file: None,
            },
        );
        assert!(result.is_err(), "unexpectedly accepted:\n{source}");
    }
}

#[test]
fn loop_toggle_updates_the_promoted_outer_bool_cell() {
    let program = compile(
        "enabled = false\nloop:\n    enabled = not enabled\n    break\nif enabled:\n    \"selected\" -> result\n",
    );
    let instructions = controls(&program.body);
    let slot = instructions
        .iter()
        .find_map(|control| match control {
            ControlInstruction::Let { slot, value } if value.ty == Type::Bool => Some(*slot),
            _ => None,
        })
        .expect("outer mutable cell");
    let body = instructions
        .iter()
        .find_map(|control| match control {
            ControlInstruction::Loop { body } => Some(body),
            _ => None,
        })
        .expect("runtime loop");
    let assignment = controls(body)
        .into_iter()
        .find_map(|control| match control {
            ControlInstruction::Set { slot, value } => Some((*slot, value)),
            _ => None,
        })
        .expect("loop assignment");
    assert_eq!(assignment.0, slot);
    assert_eq!(assignment.1.ty, Type::Bool);
    assert!(matches!(&assignment.1.kind, ExprKind::Not(operand)
        if matches!(operand.kind, ExprKind::Slot(id) if id == slot)));
    assert!(instructions.iter().any(|control| matches!(control,
        ControlInstruction::If { condition, .. } if matches!(condition.kind, ExprKind::Slot(id) if id == slot))));
}

#[test]
fn dynamic_conditional_arms_assign_the_same_outer_cell() {
    let program = compile(
        "enabled = false\nif exists \"marker\":\n    enabled = true\nelse:\n    enabled = false\nif enabled:\n    \"selected\" -> result\n",
    );
    let instructions = controls(&program.body);
    let slot = instructions
        .iter()
        .find_map(|control| match control {
            ControlInstruction::Let { slot, value } if value.ty == Type::Bool => Some(*slot),
            _ => None,
        })
        .expect("outer mutable cell");
    let (then_body, else_body) = instructions
        .iter()
        .find_map(|control| match control {
            ControlInstruction::If {
                condition,
                then_body,
                else_body,
            } if matches!(condition.kind, ExprKind::Exists(_)) => Some((then_body, else_body)),
            _ => None,
        })
        .expect("filesystem-dependent conditional");
    for body in [then_body, else_body] {
        assert!(controls(body).iter().any(|control| matches!(control,
            ControlInstruction::Set { slot: target, value } if *target == slot && value.ty == Type::Bool)));
    }
    assert!(instructions.iter().any(|control| matches!(control,
        ControlInstruction::If { condition, .. } if matches!(condition.kind, ExprKind::Slot(id) if id == slot))));
}

#[test]
fn macro_parameter_mutation_never_reuses_the_callers_cell() {
    let program = compile(
        "enabled = exists \"marker\"\nmacro toggle(value):\n    loop:\n        value = not value\n        break\ntoggle(enabled)\nif enabled:\n    \"selected\" -> result\n",
    );
    let instructions = controls(&program.body);
    let caller = instructions
        .iter()
        .find_map(|control| match control {
            ControlInstruction::Let { slot, value }
                if matches!(value.kind, ExprKind::Exists(_)) =>
            {
                Some(*slot)
            }
            _ => None,
        })
        .expect("caller predicate snapshot");
    let body = instructions
        .iter()
        .find_map(|control| match control {
            ControlInstruction::Loop { body } => Some(body),
            _ => None,
        })
        .expect("macro-local loop");
    let targets: Vec<SlotId> = controls(body)
        .iter()
        .filter_map(|control| match control {
            ControlInstruction::Set { slot, .. } => Some(*slot),
            _ => None,
        })
        .collect();
    assert!(!targets.is_empty());
    assert!(targets.iter().all(|slot| *slot != caller));
    assert!(instructions.iter().any(|control| matches!(control,
        ControlInstruction::If { condition, .. } if matches!(condition.kind, ExprKind::Slot(id) if id == caller))));
}
