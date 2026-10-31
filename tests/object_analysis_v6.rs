//! Borrowed object analyses preserve tables, provenance and identity freshening.

use std::collections::{BTreeMap, BTreeSet};
use std::path::PathBuf;

use msync::compiler::{CompileContext, structured::Session};
use msync::context::{Context, contextualize};
use msync::object::ObjectModule;
use proptest::prelude::*;
use serde_json::Value;

fn slot_ids(value: &Value, slots: &mut BTreeSet<u64>) {
    match value {
        Value::Array(values) => values.iter().for_each(|value| slot_ids(value, slots)),
        Value::Object(fields) => {
            if let Some(slot) = fields.get("slot").and_then(Value::as_u64) {
                slots.insert(slot);
            }
            if fields.get("kind").and_then(Value::as_str) == Some("slot") {
                slots.insert(fields["value"].as_u64().unwrap());
            }
            fields.values().for_each(|value| slot_ids(value, slots));
        }
        _ => {}
    }
}

fn normalize_slots(value: &mut Value, slots: &BTreeMap<u64, u64>) {
    match value {
        Value::Array(values) => values
            .iter_mut()
            .for_each(|value| normalize_slots(value, slots)),
        Value::Object(fields) => {
            if let Some(slot) = fields.get_mut("slot") {
                *slot = Value::from(slots[&slot.as_u64().unwrap()]);
            }
            if fields.get("kind").and_then(Value::as_str) == Some("slot") {
                let old = fields["value"].as_u64().unwrap();
                fields.insert("value".into(), Value::from(slots[&old]));
            }
            fields
                .values_mut()
                .for_each(|value| normalize_slots(value, slots));
        }
        _ => {}
    }
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(16))]

    #[test]
    fn object_analysis_preserves_tables_exports_and_freshened_programs(count in 1usize..8) {
        let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/object-analysis-v6-never-created");
        let context = CompileContext { pwd: root.join("compile"), source_file: Some(root.join("compile/code.msync")) };
        let mut code = "boolean stable(path):\n    observed = exists path\n    return observed == observed\nitems := inputs/<.*> | views::filter(stable) | views::take(2)\n".to_owned();
        for index in 0..count {
            code.push_str(&format!("ready{index} = stable($file::parent / \"marker-{index}\")\nif ready{index}:\n    for item in items:\n        delete item\n"));
        }
        let object = msync::pipeline::compile_object_named(&mut Session::new(), &code, "code.msync", &context).unwrap();
        let before = object.encode().unwrap();
        let rebuilt = object.clone().with_source_modules(Vec::new()).unwrap();
        prop_assert_eq!(&before, &rebuilt.encode().unwrap());
        let decoded = ObjectModule::decode(&before).unwrap();
        let context = Context::new(&CompileContext { pwd: root.join("load"), source_file: Some(root.join("load/code.msync")) });
        let mut session = Session::new();
        let first = contextualize(&decoded, &context, &mut session).unwrap();
        let second = contextualize(&decoded, &context, &mut session).unwrap();
        let mut first_json = serde_json::to_value(&first.program).unwrap();
        let mut second_json = serde_json::to_value(&second.program).unwrap();
        let mut first_slots = BTreeSet::new();
        let mut second_slots = BTreeSet::new();
        slot_ids(&first_json, &mut first_slots);
        slot_ids(&second_json, &mut second_slots);
        prop_assert!(!first_slots.is_empty());
        prop_assert_eq!(first_slots.len(), second_slots.len());
        prop_assert!(first_slots.is_disjoint(&second_slots));
        let logical_slots = |slots: &BTreeSet<u64>| slots.iter().enumerate().map(|(index, old)| (*old, index as u64)).collect::<BTreeMap<_, _>>();
        normalize_slots(&mut first_json, &logical_slots(&first_slots));
        normalize_slots(&mut second_json, &logical_slots(&second_slots));
        prop_assert_eq!(first_json, second_json);
        prop_assert_eq!(&*first.source.text, &code);
        prop_assert_eq!(first.origins.len(), second.origins.len());
        for (id, origin) in &first.origins {
            let other = &second.origins[id];
            prop_assert_eq!(origin.site.span, other.site.span);
            prop_assert_eq!(&origin.definition.source.text, &other.definition.source.text);
        }
        prop_assert_eq!(decoded.encode().unwrap(), before);
    }
}

#[test]
fn public_instruction_walk_includes_expression_loop_processes_in_preorder() {
    use msync::hir::InstructionKind;
    use msync::hir::control::{ControlInstruction, ExprKind};

    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/object-walk-v6-never-created");
    let context = CompileContext {
        pwd: root.clone(),
        source_file: Some(root.join("code.msync")),
    };
    let code = "selected = loop:\n    P => $path::echo(\"not executed\")\n    break \"done\"\nselected -> output\n";
    let object =
        msync::pipeline::compile_object_named(&mut Session::new(), code, "code.msync", &context)
            .unwrap();
    let instructions = msync::hir::walk::instructions(&object.program.body);
    let ids = instructions
        .iter()
        .map(|node| node.id)
        .collect::<BTreeSet<_>>();
    assert_eq!(ids.len(), instructions.len());
    assert_eq!(instructions.len(), object.debug.origins.len());
    assert!(matches!(&instructions[0].kind,
        InstructionKind::Control(ControlInstruction::Let { value, .. })
            if matches!(value.kind, ExprKind::Loop { .. })));
    assert!(instructions.iter().skip(1).any(|node| matches!(
        node.kind,
        InstructionKind::Spawn { .. } | InstructionKind::Control(ControlInstruction::Spawn { .. })
    )));
    assert!(instructions.iter().skip(1).any(|node| matches!(
        node.kind,
        InstructionKind::Control(ControlInstruction::Break { .. })
    )));
}
