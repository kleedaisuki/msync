use std::collections::BTreeMap;
use std::sync::Arc;

use msync::endpoint::{Backend, Location};
use msync::hir::{self, Input, Instruction, InstructionId, InstructionKind, Output, Region};
use msync::language::DestinationMode;
use msync::object::{MAGIC, ObjectModule};
use msync::relocatable::{Anchor, Path, Relocation, Slot};
use msync::source::{InstructionOrigin, Source, SourceMap};
use msync::syntax::Span;

fn fixture() -> ObjectModule {
    let source = Source::new("compile-host/code.msync", "可莉");
    let site = source.at(Span::default());
    let origin = InstructionOrigin {
        site: site.clone(),
        definition: site.clone(),
        expansion: Vec::new(),
        operands: Some((site.clone(), site)),
    };
    let node = Instruction {
        id: InstructionId(0),
        span: Span::default(),
        expansion: Vec::new(),
        kind: InstructionKind::Transfer {
            source: Input::Literal(Arc::from("UTF-8 可莉\0data")),
            destination: Output::File {
                location: Location {
                    backend: Backend::Local,
                    path: "compile-host/private/out".into(),
                },
                mode: DestinationMode::Exact,
            },
        },
    };
    let program = hir::Program {
        body: Region {
            instructions: vec![node],
        },
    };
    ObjectModule::from_parts(
        program,
        vec![Relocation {
            instruction: InstructionId(0),
            slot: Slot::Destination,
            path: Path::new(Anchor::WorkingDirectory, "out"),
        }],
        Vec::new(),
        source,
        SourceMap::default(),
        BTreeMap::from([(InstructionId(0), origin)]),
        BTreeMap::new(),
        "compile-host/code.msync".into(),
    )
    .unwrap()
}

fn unchecked(module: &ObjectModule) -> Vec<u8> {
    let mut bytes = MAGIC.to_vec();
    bytes.extend(serde_json::to_vec(module).unwrap());
    bytes
}

#[test]
fn versioned_round_trip_clears_only_contextual_operands_and_owns_debug_sources() {
    let module = fixture();
    let bytes = module.encode().unwrap();
    assert!(msync::object::is_object(&bytes));
    assert!(!String::from_utf8_lossy(&bytes).contains("compile-host/private/out"));
    let decoded = ObjectModule::decode(&bytes).unwrap();
    let payload = String::from_utf8_lossy(&bytes);
    assert!(!payload.contains("native_program_view"));
    assert!(!payload.contains("worker_executable"));
    assert!(!payload.contains("\"transactions\""));
    let (_, sources, origins, _) = decoded.debug.materialize().unwrap();
    assert_eq!(sources.iter().count(), 1);
    assert_eq!(origins[&InstructionId(0)].site.source.text.as_ref(), "可莉");
    let (_, _, second, _) = decoded.debug.materialize().unwrap();
    assert_ne!(
        origins[&InstructionId(0)].site.source.id,
        second[&InstructionId(0)].site.source.id
    );
    assert_eq!(decoded.encode().unwrap(), bytes);
}

#[test]
fn unknown_versions_and_undeclared_or_forged_tables_are_rejected() {
    let mut module = fixture();
    module.format_version += 1;
    assert!(ObjectModule::decode(&unchecked(&module)).is_err());
    let mut obsolete = serde_json::to_value(fixture()).unwrap();
    obsolete["program"]["transactions"] = serde_json::json!([]);
    let mut bytes = MAGIC.to_vec();
    bytes.extend(serde_json::to_vec(&obsolete).unwrap());
    assert!(ObjectModule::decode(&bytes).is_err());
    let mut module = fixture();
    module.imports.clear();
    assert!(ObjectModule::decode(&unchecked(&module)).is_err());
    let mut module = fixture();
    module.relocations.push(module.relocations[0].clone());
    assert!(ObjectModule::decode(&unchecked(&module)).is_err());
    let mut module = fixture();
    module
        .program
        .body
        .instructions
        .push(module.program.body.instructions[0].clone());
    assert!(ObjectModule::decode(&unchecked(&module)).is_err());
}

#[test]
fn source_spans_require_actual_utf8_boundaries_not_renderer_clamping() {
    let mut module = fixture();
    module.debug.origins[0].site.span = Span { start: 1, end: 2 };
    assert!(ObjectModule::decode(&unchecked(&module)).is_err());
    let mut module = fixture();
    module.debug.origins[0].definition.source = usize::MAX;
    assert!(ObjectModule::decode(&unchecked(&module)).is_err());
}

#[test]
fn malformed_payloads_and_unknown_fields_do_not_fall_back_to_source() {
    let mut bytes = MAGIC.to_vec();
    bytes.extend_from_slice(b"{bad object");
    assert!(msync::object::is_object(&bytes));
    assert!(ObjectModule::decode(&bytes).is_err());
    let mut value = serde_json::to_value(fixture()).unwrap();
    value.as_object_mut().unwrap().insert(
        "trusted_process_effects".into(),
        serde_json::json!({"all_paths_are_pure": true}),
    );
    let mut bytes = MAGIC.to_vec();
    bytes.extend(serde_json::to_vec(&value).unwrap());
    assert!(ObjectModule::decode(&bytes).is_err());
}

#[test]
fn symbolic_destinations_do_not_become_equal_merely_because_slots_are_unbound() {
    let mut module = fixture();
    let mut second = module.program.body.instructions[0].clone();
    second.id = InstructionId(1);
    module.program.body.instructions.push(second);
    let mut origin = module.debug.origins[0].clone();
    origin.instruction = InstructionId(1);
    module.debug.origins.push(origin);
    let mut relocation = module.relocations[0].clone();
    relocation.instruction = InstructionId(1);
    relocation.path.suffix = "other".into();
    module.relocations.push(relocation);
    let (source, sources, origins, sites) = module.debug.materialize().unwrap();
    let rebuilt = ObjectModule::from_parts(
        module.program,
        module.relocations,
        module.exports,
        source,
        sources,
        origins,
        sites,
        module.main_module,
    )
    .unwrap();
    assert!(rebuilt.constraints.is_empty());
    rebuilt.validate().unwrap();
}

#[test]
fn explicit_absolute_constants_are_not_relocated_by_a_compile_host_prefix_guess() {
    let mut module = fixture();
    let InstructionKind::Transfer { source, .. } = &mut module.program.body.instructions[0].kind
    else {
        unreachable!()
    };
    *source = Input::File {
        location: Location {
            backend: Backend::Local,
            path: "/compile-host/private/fixed".into(),
        },
        pattern: None,
    };
    let (source, sources, origins, sites) = module.debug.materialize().unwrap();
    let rebuilt = ObjectModule::from_parts(
        module.program,
        module.relocations,
        module.exports,
        source,
        sources,
        origins,
        sites,
        module.main_module,
    )
    .unwrap();
    assert!(
        String::from_utf8(rebuilt.encode().unwrap())
            .unwrap()
            .contains("/compile-host/private/fixed")
    );
    assert_eq!(rebuilt.relocations.len(), 1);
}
