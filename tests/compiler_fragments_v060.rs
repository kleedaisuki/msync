//! Dynamic path fragments preserve endpoint constraints without becoming files.
use std::fs;
use std::path::Path;

use msync::compiler::{CompileContext, structured::Session};
use msync::context::{self, Context};
use msync::execution::{self, Options};
use msync::object::ObjectModule;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("fragment-v060-")
        .tempdir_in(root)
        .unwrap()
}

fn program(root: &Path, suffix: &str) -> msync::context::ContextualizedModule {
    let context = CompileContext {
        pwd: root.to_owned(),
        source_file: Some(root.join("code.msync")),
    };
    let source = format!(
        "base := loop:\n    break root\nready = exists (base / {suffix})\nif ready:\n    \"yes\" -> result\n"
    );
    let object =
        msync::pipeline::compile_object_named(&mut Session::new(), &source, "code.msync", &context)
            .unwrap();
    let object = ObjectModule::decode(&object.encode().unwrap()).unwrap();
    context::contextualize(&object, &Context::new(&context), &mut Session::new()).unwrap()
}

#[test]
fn explicit_local_relative_fragment_survives_object_round_trip() {
    let root = workspace();
    fs::create_dir(root.path().join("root")).unwrap();
    fs::write(root.path().join("root/leaf"), b"exists").unwrap();
    let program = program(root.path(), "$local::leaf");
    execution::execute(&program.program, &Options::new(root.path())).unwrap();
    assert_eq!(fs::read(root.path().join("result")).unwrap(), b"yes");
}

#[test]
fn conflicting_fragment_endpoint_fails_before_any_transport() {
    let root = workspace();
    let program = program(root.path(), "$wsl::leaf");
    let error = execution::execute(&program.program, &Options::new(root.path())).unwrap_err();
    assert!(error.to_string().contains("endpoint"), "{error}");
    assert!(!root.path().join("result").exists());
}

#[test]
fn session_reset_does_not_recycle_live_runtime_slots() {
    let root = workspace();
    let context = CompileContext {
        pwd: root.path().to_owned(),
        source_file: None,
    };
    let code = msync::language::parse("ready = exists missing\n").unwrap();
    let mut session = Session::new();
    session.compile(&code, &context).unwrap();
    let first = session.runtime_slot_base();
    session.reset();
    assert_eq!(session.runtime_slot_base(), first);
    session.compile(&code, &context).unwrap();
    assert!(session.runtime_slot_base() > first);
}
