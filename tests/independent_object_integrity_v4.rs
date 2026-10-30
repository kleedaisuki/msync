//! Untrusted module tables are checked independently of the compiler's producer.

use std::fs;
use std::path::Path;
use std::sync::Arc;

use msync::compiler::CompileContext;
use msync::compiler::structured::Session;
use msync::context::{Context, contextualize, contextualize_source};
use msync::hir::{Input, InstructionId, InstructionKind};
use msync::object::{MAGIC, ObjectModule, is_object};
use msync::relocatable::Slot;
use proptest::prelude::*;
use serde_json::{Value, json};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("independent-integrity-")
        .tempdir_in(root)
        .unwrap()
}

fn context(root: &Path) -> CompileContext {
    CompileContext {
        pwd: root.to_owned(),
        source_file: Some(root.join("integrity.msync")),
    }
}

fn compile(root: &Path, text: &str) -> ObjectModule {
    let context = context(root);
    msync::pipeline::compile_object_named(
        &mut Session::new(),
        text,
        context.source_file.as_ref().unwrap().to_str().unwrap(),
        &context,
    )
    .unwrap()
}

fn module(root: &Path) -> ObjectModule {
    compile(
        root,
        "\"可莉\" -> $pwd::pwd / \"one\"\n$pwd::pwd / \"one\" -> $pwd::pwd / \"two\"\n",
    )
}

fn frame(value: &Value) -> Vec<u8> {
    let mut bytes = MAGIC.to_vec();
    bytes.extend(serde_json::to_vec(value).unwrap());
    bytes
}

fn assert_rejected(value: &Value, reason: &str, root: &Path) {
    assert!(
        ObjectModule::decode(&frame(value)).is_err(),
        "accepted corrupt {reason}"
    );
    assert!(fs::read_dir(root).unwrap().next().is_none());
}

#[test]
fn structurally_valid_json_cannot_forge_instruction_or_relocation_references() {
    let root = workspace();
    let baseline = serde_json::to_value(module(root.path())).unwrap();
    let mut cases = Vec::new();

    let mut duplicate = baseline.clone();
    duplicate["program"]["body"]["instructions"][1]["id"] =
        duplicate["program"]["body"]["instructions"][0]["id"].clone();
    cases.push(("duplicate instruction ID", duplicate));

    let mut absent = baseline.clone();
    absent["relocations"][0]["instruction"] = json!(999_999);
    cases.push(("absent relocation instruction", absent));

    let mut wrong_slot = baseline.clone();
    wrong_slot["relocations"][0]["slot"] =
        serde_json::to_value(Slot::Argument { index: 0 }).unwrap();
    cases.push(("argument relocation on a Transfer", wrong_slot));

    let mut repeated_slot = baseline.clone();
    let entry = repeated_slot["relocations"][0].clone();
    repeated_slot["relocations"]
        .as_array_mut()
        .unwrap()
        .push(entry);
    cases.push(("duplicate relocation slot", repeated_slot));

    let mut missing_relocation = baseline.clone();
    missing_relocation["relocations"]
        .as_array_mut()
        .unwrap()
        .remove(0);
    cases.push((
        "missing authoritative relocation for a cleared operand",
        missing_relocation,
    ));

    for (reason, value) in cases {
        assert_rejected(&value, reason, root.path());
    }
}

#[test]
fn debug_coordinates_must_reference_real_sources_and_utf8_boundaries() {
    let root = workspace();
    let object = module(root.path());
    let origin = &object.debug.origins[0];
    let source = &object.debug.sources[origin.site.source].text;
    let inside_cjk = source.find('可').unwrap() + 1;
    assert!(!source.is_char_boundary(inside_cjk));
    let baseline = serde_json::to_value(&object).unwrap();

    let mut split_codepoint = baseline.clone();
    split_codepoint["debug"]["origins"][0]["site"]["span"]["start"] = json!(inside_cjk);
    assert_rejected(
        &split_codepoint,
        "UTF-8-interior source offset",
        root.path(),
    );

    let mut outside_source = baseline.clone();
    outside_source["debug"]["origins"][0]["definition"]["span"]["end"] = json!(source.len() + 1);
    assert_rejected(
        &outside_source,
        "source interval outside retained text",
        root.path(),
    );

    let mut absent_source = baseline;
    absent_source["debug"]["origins"][0]["site"]["source"] = json!(999_999);
    assert_rejected(&absent_source, "absent debug source", root.path());
}

#[test]
fn unknown_versions_runtime_proofs_and_duplicate_members_are_not_source_fallbacks() {
    let root = workspace();
    let object = module(root.path());
    let baseline = serde_json::to_value(&object).unwrap();

    let mut future = baseline.clone();
    future["format_version"] = json!(object.format_version + 1);
    let future_bytes = frame(&future);
    assert!(is_object(&future_bytes));
    assert_rejected(&future, "unsupported format version", root.path());

    let mut unknown_opcode = baseline.clone();
    unknown_opcode["program"]["body"]["instructions"][0]["kind"]["kind"] =
        json!("trusted_native_machine_code");
    assert_rejected(&unknown_opcode, "unknown operation tag", root.path());

    for name in [
        "process_effects",
        "operation_facts",
        "pinned_inputs",
        "runtime_certificate",
    ] {
        let mut forged = baseline.clone();
        forged[name] = json!({"provider": "verified", "trusted": true});
        assert_rejected(&forged, name, root.path());
    }

    let payload = serde_json::to_string(&object).unwrap();
    let version = format!("\"format_version\":{}", object.format_version);
    assert!(payload.contains(&version));
    let duplicate = payload.replacen(&version, &format!("{version},{version}"), 1);
    let mut bytes = MAGIC.to_vec();
    bytes.extend_from_slice(duplicate.as_bytes());
    assert!(is_object(&bytes));
    assert!(ObjectModule::decode(&bytes).is_err());
    assert!(is_object(&MAGIC[..MAGIC.len() - 1]));
    assert!(ObjectModule::decode(&MAGIC[..MAGIC.len() - 1]).is_err());
    assert!(fs::read_dir(root.path()).unwrap().next().is_none());
}

#[test]
fn removed_transaction_syntax_cannot_enter_an_executable_object() {
    let root = workspace();
    for text in [
        "transaction x:\n    \"temporary\" -> result\n",
        "commit\n",
        "rollback\n",
        "rollback x\n",
    ] {
        let error = msync::pipeline::compile_object_named(
            &mut Session::new(),
            text,
            "removed-transactions.msync",
            &context(root.path()),
        );
        assert!(
            error.is_err(),
            "removed transaction statement compiled: {text:?}"
        );
    }
    assert!(fs::read_dir(root.path()).unwrap().next().is_none());
}

#[test]
fn an_await_cannot_reference_a_process_absent_from_the_executable_module() {
    let root = workspace();
    let mut object = compile(
        root.path(),
        "async p => $path::not_started_by_validation()\nawait p\n",
    );
    let await_instruction = object
        .program
        .body
        .instructions
        .iter_mut()
        .find(|instruction| matches!(instruction.kind, InstructionKind::Await { .. }))
        .expect("explicit await must survive compilation");
    let InstructionKind::Await { process } = &mut await_instruction.kind else {
        unreachable!()
    };
    *process = Some(msync::hir::ProcessId(999_999));
    assert_rejected(
        &serde_json::to_value(&object).unwrap(),
        "await of an absent process",
        root.path(),
    );
}

#[test]
fn a_failed_context_binding_does_not_install_the_modules_exports() {
    let root = workspace();
    let object = compile(
        root.path(),
        "uncommitted_namespace => $pwd\n$env::MSYNC_INTEGRITY_ROOT/input -> output\n",
    );
    let mut bindings = Context::new(&context(root.path()));
    bindings
        .environment
        .insert("MSYNC_INTEGRITY_ROOT".into(), String::new());
    let mut session = Session::new();
    assert!(contextualize(&object, &bindings, &mut session).is_err());
    let reference =
        msync::language::parse("uncommitted_namespace::pwd/input -> another\n").unwrap();
    assert!(session.compile(&reference, &context(root.path())).is_err());
    assert!(fs::read_dir(root.path()).unwrap().next().is_none());
}

#[test]
fn foreign_definition_modules_need_semantic_bindings_not_debug_filename_guesses() {
    let root = workspace();
    let definition = CompileContext {
        pwd: root.path().join("definitions"),
        source_file: Some(root.path().join("definitions/library.msync")),
    };
    let caller = CompileContext {
        pwd: root.path().join("caller"),
        source_file: Some(root.path().join("caller/main.msync")),
    };
    let mut compiler = Session::new();
    msync::pipeline::compile_relocatable_named(
        &mut compiler,
        "macro read_definition(des):\n    $file::parent/input -> des\n",
        definition.source_file.as_ref().unwrap().to_str().unwrap(),
        &definition,
        &Default::default(),
    )
    .unwrap();
    let compiled = msync::pipeline::compile_relocatable_named(
        &mut compiler,
        "read_definition($pwd::pwd / \"output\")\n",
        caller.source_file.as_ref().unwrap().to_str().unwrap(),
        &caller,
        &Default::default(),
    )
    .unwrap();
    let defining_module = definition.source_file.as_ref().unwrap().to_string_lossy();
    assert_eq!(
        compiled.module_bindings.get(defining_module.as_ref()),
        definition.source_file.as_ref()
    );
    assert!(contextualize_source(&compiled, &Context::new(&caller), Default::default()).is_ok());
    let mut source_bindings = Context::new(&caller);
    source_bindings.modules = compiled.module_bindings.clone();
    let object = compiled.into_object().unwrap();
    assert!(contextualize(&object, &source_bindings, &mut Session::new()).is_ok());

    let artifact_bindings = Context::new(&caller);
    // New executable objects carry explicit semantic source bindings. Debug
    // filenames remain presentation-only and cannot redirect those bindings.
    assert!(contextualize(&object, &artifact_bindings, &mut Session::new()).is_ok());
    let mut renamed_debug = object.clone();
    for source in &mut renamed_debug.debug.sources {
        source.name = "untrusted-debug-name.msync".into();
    }
    let linked = contextualize(&renamed_debug, &artifact_bindings, &mut Session::new()).unwrap();
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        ..
    } = &linked.program.body.instructions[0].kind
    else {
        panic!()
    };
    assert_eq!(Path::new(&location.path), definition.pwd.join("input"));
    // Old-format objects retain their explicit caller-binding requirement.
    let mut legacy = object.clone();
    legacy.source_modules.clear();
    legacy
        .features
        .retain(|feature| feature != msync::object::SOURCE_MODULES_FEATURE);
    legacy.validate().unwrap();
    assert!(contextualize(&legacy, &artifact_bindings, &mut Session::new()).is_err());
    assert!(fs::read_dir(root.path()).unwrap().next().is_none());
}

#[test]
fn a_historical_captured_value_is_not_relocated_like_a_fresh_module_file_expression() {
    let root = workspace();
    let definition = CompileContext {
        pwd: root.path().join("definitions"),
        source_file: Some(root.path().join("definitions/library.msync")),
    };
    let caller = CompileContext {
        pwd: root.path().join("caller"),
        source_file: Some(root.path().join("caller/main.msync")),
    };
    let mut compiler = Session::new();
    let first = msync::pipeline::compile_relocatable_named(
        &mut compiler,
        "captured := $file::parent\n",
        definition.source_file.as_ref().unwrap().to_str().unwrap(),
        &definition,
        &Default::default(),
    )
    .unwrap();
    contextualize_source(&first, &Context::new(&definition), Default::default()).unwrap();
    let next = msync::pipeline::compile_relocatable_named(
        &mut compiler,
        "captured/input -> output\n",
        caller.source_file.as_ref().unwrap().to_str().unwrap(),
        &caller,
        &Default::default(),
    )
    .unwrap();
    let bound = contextualize_source(&next, &Context::new(&caller), Default::default()).unwrap();
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        ..
    } = &bound.program.body.instructions[0].kind
    else {
        panic!("the captured value must remain a file input");
    };
    assert_eq!(Path::new(&location.path), definition.pwd.join("input"));
    assert!(fs::read_dir(root.path()).unwrap().next().is_none());
}

#[tokio::test]
async fn typed_operations_not_embedded_debug_source_are_the_executable_authority() {
    let root = workspace();
    let mut object = module(root.path());
    let InstructionKind::Transfer { source, .. } = &mut object.program.body.instructions[0].kind
    else {
        panic!("fixture's first operation must be a Transfer");
    };
    *source = Input::Literal(Arc::from("object operation is authoritative"));
    assert!(
        object
            .debug
            .sources
            .iter()
            .any(|source| source.text.contains("可莉"))
    );
    let decoded = ObjectModule::decode(&object.encode().unwrap()).unwrap();
    let bound = contextualize(
        &decoded,
        &Context::new(&context(root.path())),
        &mut Session::new(),
    )
    .unwrap();
    assert!(fs::read_dir(root.path()).unwrap().next().is_none());
    let mut runtime = msync::execution::RuntimeSession::new(&msync::execution::Options::new(
        root.path().join(".temp"),
    ))
    .unwrap();
    let report = runtime.execute(&bound.program).await.unwrap();
    assert_eq!(report.assignments, 2);
    assert_eq!(
        fs::read(root.path().join("one")).unwrap(),
        b"object operation is authoritative"
    );
    assert_eq!(
        fs::read(root.path().join("two")).unwrap(),
        b"object operation is authoritative"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn an_external_program_can_create_a_source_before_its_block_is_evaluated() {
    let root = workspace();
    #[cfg(windows)]
    let producer = "producer => $path::cmd(\"/d\", \"/c\", \"echo produced>created\")\n";
    #[cfg(not(windows))]
    let producer = "producer => $path::sh(\"-c\", \"printf produced > created\")\n";
    let text = format!("{producer}$pwd::pwd / \"created\" -> $pwd::pwd / \"copied\"\n");
    let object = compile(root.path(), &text);
    let decoded = ObjectModule::decode(&object.encode().unwrap()).unwrap();
    let instantiated = contextualize(
        &decoded,
        &Context::new(&context(root.path())),
        &mut Session::new(),
    )
    .unwrap();
    assert!(!root.path().join("created").exists());
    assert!(!root.path().join("copied").exists());

    let mut runtime = msync::execution::RuntimeSession::new(&msync::execution::Options::new(
        root.path().join(".temp"),
    ))
    .unwrap();
    let report = runtime.execute(&instantiated.program).await.unwrap();
    assert_eq!(report.assignments, 1);
    assert_eq!(
        fs::read_to_string(root.path().join("copied"))
            .unwrap()
            .trim(),
        "produced"
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn an_external_programs_deletion_is_observed_after_its_completion_barrier() {
    let root = workspace();
    #[cfg(windows)]
    let remover = "remover => $path::cmd(\"/d\", \"/c\", \"del stale\")\n";
    #[cfg(not(windows))]
    let remover = "remover => $path::sh(\"-c\", \"rm stale\")\n";
    let text = format!(
        "\"before\" -> $pwd::pwd / \"stale\"\n{remover}$pwd::pwd / \"stale\" -> $pwd::pwd / \"copied\"\n"
    );
    let object = compile(root.path(), &text);
    let instantiated = contextualize(
        &object,
        &Context::new(&context(root.path())),
        &mut Session::new(),
    )
    .unwrap();
    let mut runtime = msync::execution::RuntimeSession::new(&msync::execution::Options::new(
        root.path().join(".temp"),
    ))
    .unwrap();
    assert!(runtime.execute(&instantiated.program).await.is_err());
    assert!(!root.path().join("stale").exists());
    assert!(!root.path().join("copied").exists());
    runtime.shutdown().await.unwrap();
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(24))]

    #[test]
    fn arbitrary_absent_relocation_ids_are_rejected_without_dense_id_allocation(id in 2usize..1_000_000) {
        let root = workspace();
        let mut object = module(root.path());
        object.relocations[0].instruction = InstructionId(id);
        let value = serde_json::to_value(&object).unwrap();
        prop_assert!(ObjectModule::decode(&frame(&value)).is_err());
        prop_assert!(fs::read_dir(root.path()).unwrap().next().is_none());
    }
}
