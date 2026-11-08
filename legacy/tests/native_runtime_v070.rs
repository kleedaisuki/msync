//! Native IR remains typed and relocatable without executable source text.

use std::fs;
use std::path::Path;

use msync::compiler::{CompileContext, structured::Session};
use msync::context::{self, Context};
use msync::execution::{self, Options};
use msync::hir::InstructionKind;
use msync::hir::control::{ControlInstruction, ExprKind};
use msync::object::{NATIVE_STD_FEATURE, ObjectModule};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("native-runtime-v070-")
        .tempdir_in(root)
        .unwrap()
}

fn compile(root: &Path, source: &str) -> ObjectModule {
    msync::pipeline::compile_object_named(
        &mut Session::new(),
        source,
        "native.msync",
        &CompileContext {
            pwd: root.to_owned(),
            source_file: Some(root.join("native.msync")),
        },
    )
    .unwrap()
}

#[test]
fn native_range_and_mutation_round_trip_relocate_and_execute_without_source() {
    let workspace = workspace();
    let old = workspace.path().join("old");
    let new = workspace.path().join("new");
    fs::create_dir(&old).unwrap();
    fs::create_dir(&new).unwrap();
    fs::create_dir(new.join("entries")).unwrap();
    fs::write(new.join("entries/source"), "native data").unwrap();
    let mut module = compile(
        &old,
        "fs => $std::fs\nfs::create_directories(output)\nfor file in fs::directory_iterator(entries):\n    file -> output/\nfs::rename(output/source, output/renamed)\nif fs::is_regular_file(output/renamed):\n    \"done\" -> receipt\n",
    );
    assert_eq!(module.language_version, "0.7");
    assert!(
        module
            .features
            .iter()
            .any(|feature| feature == NATIVE_STD_FEATURE)
    );
    for source in &mut module.debug.sources {
        source.text = " ".repeat(source.text.len());
    }
    let module = ObjectModule::decode(&module.encode().unwrap()).unwrap();
    let context = Context::new(&CompileContext {
        pwd: new.clone(),
        source_file: Some(new.join("native.msync")),
    });
    let linked = context::contextualize(&module, &context, &mut Session::new()).unwrap();
    execution::execute(&linked.program, &Options::new(&new)).unwrap();
    assert_eq!(
        fs::read_to_string(new.join("output/renamed")).unwrap(),
        "native data"
    );
    assert_eq!(fs::read_to_string(new.join("receipt")).unwrap(), "done");
    assert!(!old.join("output").exists());
}

#[test]
fn native_feature_and_signature_are_checked_before_effects() {
    let workspace = workspace();
    let module = compile(
        workspace.path(),
        "fs => $std::fs\nready = fs::exists(missing)\n",
    );
    let mut missing_feature = module.clone();
    missing_feature
        .features
        .retain(|feature| feature != NATIVE_STD_FEATURE);
    assert!(missing_feature.validate().is_err());
    let mut wrong_signature = module;
    let InstructionKind::Control(ControlInstruction::Let { value, .. }) =
        &mut wrong_signature.program.body.instructions[0].kind
    else {
        panic!("native boolean result must remain executable IR");
    };
    let ExprKind::Native {
        name, arguments, ..
    } = &mut value.kind
    else {
        panic!("expected native scalar expression");
    };
    *name = "fs.rename".into();
    arguments.clear();
    assert!(
        wrong_signature
            .validate()
            .unwrap_err()
            .message
            .contains("signature")
    );
}

#[test]
fn unrequested_native_range_does_not_observe_missing_directory() {
    let workspace = workspace();
    let module = compile(
        workspace.path(),
        "fs => $std::fs\nfor file in fs::directory_iterator(missing) | $std::ranges::take(0):\n    file -> output/\n\"lazy\" -> receipt\n",
    );
    let context = Context::new(&CompileContext {
        pwd: workspace.path().to_owned(),
        source_file: None,
    });
    let linked = context::contextualize(&module, &context, &mut Session::new()).unwrap();
    execution::execute(&linked.program, &Options::new(workspace.path())).unwrap();
    assert_eq!(
        fs::read_to_string(workspace.path().join("receipt")).unwrap(),
        "lazy"
    );
    assert!(!workspace.path().join("output").exists());
}

#[test]
fn pure_path_projection_retains_raw_names_and_relocated_local_context() {
    let workspace = workspace();
    fs::write(workspace.path().join("member.txt"), "lexical path").unwrap();
    let module = compile(
        workspace.path(),
        "fs => $std::fs\nfor source in [nested/member.txt]:\n    name := fs::filename(source)\n    if fs::generic_string(name) == \"member.txt\":\n        $local::name -> copied\n",
    );
    let context = Context::new(&CompileContext {
        pwd: workspace.path().to_owned(),
        source_file: None,
    });
    let linked = context::contextualize(&module, &context, &mut Session::new()).unwrap();
    execution::execute(&linked.program, &Options::new(workspace.path())).unwrap();
    assert_eq!(
        fs::read_to_string(workspace.path().join("copied")).unwrap(),
        "lexical path"
    );
}

#[test]
fn explicit_local_path_projection_retains_the_captured_working_directory() {
    let workspace = workspace();
    fs::write(workspace.path().join("member.txt"), "explicit local").unwrap();
    let module = compile(
        workspace.path(),
        "fs => $std::fs\nname := fs::filename($local::nested/member.txt)\nname -> copied\n",
    );
    let context = Context::new(&CompileContext {
        pwd: workspace.path().to_owned(),
        source_file: None,
    });
    let linked = context::contextualize(&module, &context, &mut Session::new()).unwrap();
    execution::execute(&linked.program, &Options::new(workspace.path())).unwrap();
    assert_eq!(
        fs::read_to_string(workspace.path().join("copied")).unwrap(),
        "explicit local"
    );
}

#[test]
fn explicit_local_derived_names_retain_endpoint_provenance_in_source_free_objects() {
    let workspace = workspace();
    fs::write(workspace.path().join("member.txt"), "bound local").unwrap();
    let mut module = compile(
        workspace.path(),
        "fs => $std::fs\nremote => ssh://native-provenance-must-not-auth.invalid\nname := fs::filename($local::nested/member.txt)\ntext := fs::generic_string(name)\ntext -> raw\nname -> copied\ninvalid := remote::name\n",
    );
    for source in &mut module.debug.sources {
        source.text = " ".repeat(source.text.len());
    }
    let module = ObjectModule::decode(&module.encode().unwrap()).unwrap();
    let context = Context::new(&CompileContext {
        pwd: workspace.path().to_owned(),
        source_file: None,
    });
    let linked = context::contextualize(&module, &context, &mut Session::new()).unwrap();
    let error = execution::execute(&linked.program, &Options::new(workspace.path())).unwrap_err();
    assert!(
        error
            .report
            .diagnostic
            .message
            .contains("different endpoint"),
        "{error}"
    );
    assert_eq!(
        fs::read_to_string(workspace.path().join("raw")).unwrap(),
        "member.txt"
    );
    assert_eq!(
        fs::read_to_string(workspace.path().join("copied")).unwrap(),
        "bound local"
    );
}
