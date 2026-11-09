//! Source-backed standard-library composition and typed host bindings.

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{self, Options};
use std::fs;
use std::path::Path;

#[test]
fn source_libraries_and_native_aliases_execute_in_real_workspace() {
    let parent = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/v070");
    fs::create_dir_all(&parent).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("stdlib-")
        .tempdir_in(parent)
        .unwrap();
    let root = workspace.path();
    fs::create_dir(root.join("input")).unwrap();
    fs::write(root.join("input/file.txt"), "payload").unwrap();
    let source = "fs => $std::fs\ntext => $std::string\nlogic => $std::bool\npaths => $std::path\nnative has_file(path) -> Bool = \"fs.is_regular_file\"\nnative erase(path) -> Void = \"fs.delete\"\nmacro copy_to(scope, file):\n    $local::file -> scope::file\nif logic::both(has_file(input/file.txt), text::contains(\"雪 flower\", \"雪\")):\n    paths::join(input, \"file.txt\") -> output.txt\nif fs::has_extension(\"file.txt\") and not fs::has_root_path(\"relative/file\"):\n    text::to_uppercase(text::trim(\"  klee  \")) -> text-output\nfor part in text::split(\"first,second\", \",\") | $std::ranges::take(1):\n    part -> split-output\nerase(output.txt)\nif not fs::exists(output.txt):\n    \"removed\" -> receipt\n";
    let source = format!(
        "{source}native directory(path) = \"fs.is_directory\"\nif directory(input):\n    \"directory\" -> directory-output\nnative keep(values, predicate) = \"ranges.filter\"\nnative make(items...) = \"ranges.of\"\nboolean wanted(item):\n    return item == \"first\"\nfor item in keep(make(\"first\", \"second\"), wanted):\n    item -> compiler-native-output\n"
    );
    let result = msync::pipeline::compile(
        &mut Session::new(),
        &source,
        &CompileContext {
            pwd: root.to_owned(),
            source_file: Some(root.join("main.msync")),
        },
        &[],
    )
    .unwrap();
    execution::execute(&result.program, &Options::new(root)).unwrap();
    assert_eq!(
        fs::read_to_string(root.join("text-output")).unwrap(),
        "KLEE"
    );
    assert_eq!(
        fs::read_to_string(root.join("split-output")).unwrap(),
        "first"
    );
    assert_eq!(fs::read_to_string(root.join("receipt")).unwrap(), "removed");
    assert!(!root.join("output.txt").exists());
    assert_eq!(
        fs::read_to_string(root.join("directory-output")).unwrap(),
        "directory"
    );
    assert_eq!(
        fs::read_to_string(root.join("compiler-native-output")).unwrap(),
        "first"
    );
}

#[test]
fn all_embedded_sources_parse_and_native_contracts_match_registry() {
    let context = CompileContext {
        pwd: Path::new(env!("CARGO_MANIFEST_DIR")).to_owned(),
        source_file: None,
    };
    for (name, text) in msync::stdlib::SOURCES {
        let document = msync::language::parse(text).unwrap();
        assert_eq!(
            msync::language::format(&msync::language::format(text).unwrap()).unwrap(),
            msync::language::format(text).unwrap()
        );
        Session::new()
            .compile(&document, &context)
            .unwrap_or_else(|error| panic!("stdlib {name}: {error:?}"));
    }
}
