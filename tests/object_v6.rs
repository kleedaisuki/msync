use std::fs;
use std::path::Path;

use msync::compiler::{CompileContext, structured::Session};
use msync::context::{self, Context};
use msync::execution::{self, Options};
use msync::hir::InstructionKind;
use msync::hir::control::{ControlInstruction, ExprKind, Type};
use msync::object::{CONTROL_FEATURE, ObjectModule};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("object-v6-")
        .tempdir_in(root)
        .unwrap()
}

fn compile(root: &Path, source: &str) -> ObjectModule {
    msync::pipeline::compile_object_named(
        &mut Session::new(),
        source,
        "code.msync",
        &CompileContext {
            pwd: root.to_owned(),
            source_file: Some(root.join("code.msync")),
        },
    )
    .unwrap()
}

#[test]
fn dynamic_object_relocates_nested_paths_and_observes_prior_write() {
    let workspace = workspace();
    let old = workspace.path().join("old");
    let new = workspace.path().join("new");
    fs::create_dir(&old).unwrap();
    fs::create_dir(&new).unwrap();
    let object = compile(
        &old,
        "\"payload\" -> input\nready = exists input\nif ready:\n    \"nested\" -> output\nelse:\n    \"wrong\" -> output\ndelete input\n",
    );
    assert_eq!(object.language_version, "0.6");
    assert!(
        object
            .features
            .iter()
            .any(|feature| feature == CONTROL_FEATURE)
    );
    let bytes = object.encode().unwrap();
    let payload = String::from_utf8_lossy(&bytes);
    assert!(!payload.contains(&format!("\"path\": \"{}/output\"", old.display())));
    let object = ObjectModule::decode(&bytes).unwrap();
    let context = Context::new(&CompileContext {
        pwd: new.clone(),
        source_file: Some(new.join("code.msync")),
    });
    let program = context::contextualize(&object, &context, &mut Session::new()).unwrap();
    execution::execute(&program.program, &Options::new(&new)).unwrap();
    assert_eq!(fs::read_to_string(new.join("output")).unwrap(), "nested");
    assert!(!new.join("input").exists());
    assert!(!old.join("output").exists());
}

#[test]
fn nested_slots_are_freshened_for_repeated_instantiation() {
    let workspace = workspace();
    let object = compile(
        workspace.path(),
        "ready = exists missing\nif ready:\n    \"yes\" -> output\n",
    );
    let context = Context::new(&CompileContext {
        pwd: workspace.path().to_owned(),
        source_file: None,
    });
    let mut session = Session::new();
    let first = context::contextualize(&object, &context, &mut session).unwrap();
    let second = context::contextualize(&object, &context, &mut session).unwrap();
    let slot = |program: &msync::hir::Program| {
        let InstructionKind::Control(ControlInstruction::Let { slot, .. }) =
            &program.body.instructions[0].kind
        else {
            panic!("expected dynamic snapshot");
        };
        *slot
    };
    assert_ne!(slot(&first.program), slot(&second.program));
}

#[test]
fn forged_types_undefined_slots_and_unguarded_control_are_rejected() {
    let workspace = workspace();
    let object = compile(
        workspace.path(),
        "ready = exists missing\nif ready:\n    \"yes\" -> output\n",
    );
    let mut wrong_type = object.clone();
    let InstructionKind::Control(ControlInstruction::Let { value, .. }) =
        &mut wrong_type.program.body.instructions[0].kind
    else {
        panic!()
    };
    value.ty = Type::String;
    assert!(wrong_type.validate().unwrap_err().message.contains("type"));
    let mut undefined = object.clone();
    let InstructionKind::Control(ControlInstruction::If { condition, .. }) =
        &mut undefined.program.body.instructions[1].kind
    else {
        panic!()
    };
    condition.kind = ExprKind::Slot(msync::hir::control::SlotId(999_999));
    assert!(
        undefined
            .validate()
            .unwrap_err()
            .message
            .contains("undefined runtime slot")
    );
    let mut old_reader = object;
    old_reader.language_version = "0.5".into();
    old_reader
        .features
        .retain(|feature| feature != CONTROL_FEATURE);
    assert!(old_reader.validate().unwrap_err().message.contains("0.6"));
}

#[test]
fn legacy_objects_keep_their_existing_feature_contract() {
    let workspace = workspace();
    let object = compile(workspace.path(), "\"legacy\" -> output\n");
    assert_eq!(object.language_version, "0.5");
    assert!(
        !object
            .features
            .iter()
            .any(|feature| feature == CONTROL_FEATURE)
    );
    ObjectModule::decode(&object.encode().unwrap()).unwrap();
}

#[test]
fn branch_only_process_cannot_escape_its_defining_arm() {
    let workspace = workspace();
    let object = compile(
        workspace.path(),
        "if exists flag:\n    P => $path::echo(\"x\")\n",
    );
    let mut other_arm = object.clone();
    let InstructionKind::Control(ControlInstruction::If {
        then_body,
        else_body,
        ..
    }) = &mut other_arm.program.body.instructions[0].kind
    else {
        panic!()
    };
    let index = then_body
        .instructions
        .iter()
        .position(|node| matches!(node.kind, InstructionKind::Await { .. }))
        .unwrap();
    else_body
        .instructions
        .push(then_body.instructions.remove(index));
    assert!(
        other_arm
            .validate()
            .unwrap_err()
            .message
            .contains("preceding local launch")
    );
    let mut after_branch = object;
    let InstructionKind::Control(ControlInstruction::If { then_body, .. }) =
        &mut after_branch.program.body.instructions[0].kind
    else {
        panic!()
    };
    let index = then_body
        .instructions
        .iter()
        .position(|node| matches!(node.kind, InstructionKind::Await { .. }))
        .unwrap();
    let await_node = then_body.instructions.remove(index);
    after_branch.program.body.instructions.push(await_node);
    assert!(
        after_branch
            .validate()
            .unwrap_err()
            .message
            .contains("preceding local launch")
    );
}

#[test]
fn forged_loop_cannot_repeatedly_consume_an_outer_process_port() {
    let workspace = workspace();
    let mut object = compile(
        workspace.path(),
        "release P => $path::echo(\"x\")\nP::stdout -> output\n",
    );
    let index = object
        .program
        .body
        .instructions
        .iter()
        .position(|node| matches!(node.kind, InstructionKind::Transfer { .. }))
        .unwrap();
    let transfer = object.program.body.instructions.remove(index);
    let wrapper_id = msync::hir::InstructionId(999_998);
    let mut origin = object
        .debug
        .origins
        .iter()
        .find(|origin| origin.instruction == transfer.id)
        .unwrap()
        .clone();
    origin.instruction = wrapper_id;
    object.debug.origins.push(origin);
    object.program.body.instructions.insert(
        index,
        msync::hir::Instruction {
            id: wrapper_id,
            span: transfer.span,
            expansion: transfer.expansion.clone(),
            kind: InstructionKind::Control(ControlInstruction::Loop {
                body: msync::hir::Region {
                    instructions: vec![transfer],
                },
            }),
        },
    );
    object.language_version = "0.6".into();
    object.features.push(CONTROL_FEATURE.into());
    assert!(
        object
            .validate()
            .unwrap_err()
            .message
            .contains("outer process stream")
    );
}

#[test]
fn forged_flat_pipeline_cannot_build_an_unbounded_cursor_chain() {
    let workspace = workspace();
    let mut object = compile(workspace.path(), "files := input/<.+>\n");
    let msync::relocatable::ExportValue::Range { range } = &mut object.exports[0].value else {
        panic!()
    };
    range.stages = vec![msync::hir::control::RangeStage::Reverse; 129];
    assert!(
        object
            .validate()
            .unwrap_err()
            .message
            .contains("128 stages")
    );
}

#[test]
fn dynamic_spawn_has_fresh_process_ids_and_validates_argument_types() {
    let workspace = workspace();
    let object = compile(
        workspace.path(),
        "argument = loop:\n    break \"value\"\nP => $path::echo(argument)\nP::stdout -> $pwd::pwd / argument\n",
    );
    let context = Context::new(&CompileContext {
        pwd: workspace.path().to_owned(),
        source_file: None,
    });
    let mut session = Session::new();
    let first = context::contextualize(&object, &context, &mut session).unwrap();
    let second = context::contextualize(&object, &context, &mut session).unwrap();
    let process = |program: &msync::hir::Program| {
        program
            .body
            .instructions
            .iter()
            .find_map(|node| match &node.kind {
                InstructionKind::Control(ControlInstruction::Spawn { process, .. }) => {
                    Some(*process)
                }
                _ => None,
            })
            .expect("typed dynamic program launch")
    };
    assert_ne!(process(&first.program), process(&second.program));
    for program in [&first.program, &second.program] {
        let port_process = program
            .body
            .instructions
            .iter()
            .find_map(|node| match &node.kind {
                InstructionKind::Control(ControlInstruction::Transfer {
                    source: msync::hir::control::TransferInput::Port { process, .. },
                    ..
                }) => Some(*process),
                _ => None,
            })
            .expect("typed dynamic process output");
        assert_eq!(port_process, process(program));
    }
    let mut malformed = object;
    let launch = malformed
        .program
        .body
        .instructions
        .iter_mut()
        .find_map(|node| match &mut node.kind {
            InstructionKind::Control(ControlInstruction::Spawn { arguments, .. }) => {
                Some(arguments)
            }
            _ => None,
        })
        .unwrap();
    launch[0] = msync::hir::control::Expr {
        ty: Type::Bool,
        kind: ExprKind::Bool(true),
    };
    assert!(
        malformed
            .validate()
            .unwrap_err()
            .message
            .contains("path or string")
    );
}
