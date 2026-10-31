use std::fs;
use std::path::Path;

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{self, Options};
use msync::pipeline;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("types-v065-")
        .tempdir_in(root)
        .unwrap()
}

#[test]
fn mixed_path_string_join_uses_captured_context_not_the_test_process_directory() {
    let root = workspace();
    for (name, payload) in [
        ("left directory", "left bytes"),
        ("right directory", "right bytes"),
    ] {
        fs::create_dir(root.path().join(name)).unwrap();
        fs::write(root.path().join(name).join("leaf"), payload).unwrap();
    }
    let source = "symbol child(item):\n    return item/\"leaf\"\nstate = \"start\"\nfor item in [$pwd::\"left directory\", \"right directory\"] | $view::transform(child):\n    if state == \"start\":\n        item -> first\n        state = \"next\"\n    else:\n        item -> second\n";
    let context = CompileContext {
        pwd: root.path().to_owned(),
        source_file: None,
    };
    let compilation = pipeline::compile(&mut Session::new(), source, &context, &[]).unwrap();
    execution::execute(
        &compilation.program,
        &Options::new(root.path().join("scratch")),
    )
    .unwrap();
    assert_eq!(fs::read(root.path().join("first")).unwrap(), b"left bytes");
    assert_eq!(
        fs::read(root.path().join("second")).unwrap(),
        b"right bytes"
    );
}

#[test]
fn typed_value_extensions_require_explicit_object_feature_negotiation() {
    let root = workspace();
    let mut module = pipeline::compile_object_named(
        &mut Session::new(),
        "items := [true, \"literal\"]\n",
        "typed.msync",
        &CompileContext {
            pwd: root.path().to_owned(),
            source_file: None,
        },
    )
    .unwrap();
    assert!(
        module
            .features
            .iter()
            .any(|feature| feature == msync::object::TYPED_VALUES_FEATURE)
    );
    module
        .features
        .retain(|feature| feature != msync::object::TYPED_VALUES_FEATURE);
    assert!(
        module
            .validate()
            .unwrap_err()
            .to_string()
            .contains("feature")
    );
}

#[test]
fn range_feature_tracks_bool_sources_even_when_projection_erases_the_type() {
    let root = workspace();
    let module = pipeline::compile_object_named(
        &mut Session::new(),
        "symbol text(item):\n    return \"text\"\nfor item in [true] | $view::transform(text):\n    item -> output\n",
        "typed.msync",
        &CompileContext { pwd: root.path().to_owned(), source_file: None },
    ).unwrap();
    assert!(
        module
            .features
            .iter()
            .any(|feature| feature == msync::object::TYPED_VALUES_FEATURE)
    );
}

#[test]
fn file_scope_does_not_discard_a_symbol_results_explicit_remote_endpoint() {
    let root = workspace();
    let reports = pipeline::compile_object_named(
        &mut Session::new(),
        "symbol remote_member():\n    return $wsl::this\nitem := $file::remote_member()\n",
        "typed.msync",
        &CompileContext {
            pwd: root.path().to_owned(),
            source_file: Some(root.path().join("typed.msync")),
        },
    )
    .unwrap_err();
    assert!(
        reports
            .iter()
            .any(|report| report.report.diagnostic.message.contains("remote path"))
    );
}

#[test]
fn computed_scope_chains_do_not_repeat_function_specialization_exponentially() {
    let root = workspace();
    let mut scope = "local()".to_owned();
    for _ in 0..20 {
        scope = format!("({scope}::local())");
    }
    let source = format!(
        "symbol local():\n    return $local\nsymbol child():\n    return payload\nitem := {scope}::child()\n"
    );
    pipeline::compile_object_named(
        &mut Session::new(),
        &source,
        "typed.msync",
        &CompileContext {
            pwd: root.path().to_owned(),
            source_file: None,
        },
    )
    .unwrap();
}

#[test]
fn an_api_scope_returned_by_a_function_uses_the_same_typed_member_dispatch() {
    let root = workspace();
    let source = "symbol api():\n    return $view\nfor item in api()::single(\"typed API\"):\n    item -> output\n";
    let context = CompileContext {
        pwd: root.path().to_owned(),
        source_file: None,
    };
    let compilation = pipeline::compile(&mut Session::new(), source, &context, &[]).unwrap();
    execution::execute(
        &compilation.program,
        &Options::new(root.path().join("scratch")),
    )
    .unwrap();
    assert_eq!(fs::read(root.path().join("output")).unwrap(), b"typed API");
}
