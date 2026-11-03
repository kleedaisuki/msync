use std::fs;
use std::path::Path;

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{self, Options};
use msync::pipeline;

#[test]
fn namespace_aliases_and_lexical_functions_publish_real_files() {
    let parent = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/v070");
    fs::create_dir_all(&parent).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("namespaces-")
        .tempdir_in(parent)
        .unwrap();
    let source = "scope hello:\n    codex := input\n    symbol child(name):\n        global codex\n        return codex/name\n    macro copy(source, destination):\n        source -> destination\n    scope nested:\n        file := input/leaf\nalias => hello\nremote => $local\nalias::codex/leaf -> remote::output\nargument := \"leaf\"\nhello::child(argument) -> function-output\nhello::copy(input/leaf, macro-output)\nhello::nested::file -> nested-output\n";
    fs::create_dir(workspace.path().join("input")).unwrap();
    fs::write(workspace.path().join("input/leaf"), "namespace bytes").unwrap();
    let context = CompileContext {
        pwd: workspace.path().to_owned(),
        source_file: None,
    };
    let compilation = pipeline::compile(&mut Session::new(), source, &context, &[]).unwrap();
    execution::execute(
        &compilation.program,
        &Options::new(workspace.path().join("scratch")),
    )
    .unwrap();
    for file in ["output", "function-output", "macro-output", "nested-output"] {
        assert_eq!(
            fs::read(workspace.path().join(file)).unwrap(),
            b"namespace bytes"
        );
    }
}

#[test]
fn scope_bindings_complete_without_starting_a_repl_block() {
    for line in ["scope := $pwd", "scope => $local"] {
        let mut buffer = msync::language::SubmissionBuffer::default();
        let msync::language::Submission::Ready(source) = buffer.push_line(line).unwrap() else {
            panic!("a binding must not wait for an indented scope body: {line}");
        };
        let document = msync::language::parse(&source).unwrap();
        assert!(matches!(&document.statements[0].kind,
            msync::language::StatementKind::Binding { name, .. } if name.text == "scope"));
    }
}

#[test]
fn scopes_and_native_declarations_format_idempotently_and_buffer_in_repl() {
    let source = "scope fs:\n\tnative exists(path) -> Bool = \"fs.exists\"\n\tnative entries(path) -> Range<Path> = \"fs.entries\"\n\tnative delete(path) -> Void = \"fs.delete\"\n";
    let formatted = msync::language::format(source).unwrap();
    assert_eq!(msync::language::format(&formatted).unwrap(), formatted);
    let mut buffer = msync::language::SubmissionBuffer::default();
    assert_eq!(
        buffer.push_line("scope hello:").unwrap(),
        msync::language::Submission::Pending
    );
    assert_eq!(
        buffer.push_line("    codex := ~/.codex").unwrap(),
        msync::language::Submission::Pending
    );
    assert!(matches!(
        buffer.push_line("").unwrap(),
        msync::language::Submission::Ready(_)
    ));
    assert_eq!(
        "0.7"
            .parse::<msync::language::Version>()
            .unwrap()
            .to_string(),
        "0.7"
    );
}

#[test]
fn qualified_keyword_member_calls_are_not_prefix_operators() {
    use msync::language::{ExprKind, StatementKind};
    let document = msync::language::parse("ready = fs::exists(file)\n").unwrap();
    let StatementKind::Binding { value, .. } = &document.statements[0].kind else {
        panic!("binding")
    };
    let ExprKind::Select { item, .. } = &value.kind else {
        panic!("namespace selection")
    };
    assert!(matches!(item.kind, ExprKind::Call { .. }));
}

#[test]
fn unknown_namespace_members_do_not_become_paths() {
    let context = CompileContext {
        pwd: Path::new(env!("CARGO_MANIFEST_DIR")).to_owned(),
        source_file: None,
    };
    let error = pipeline::compile(
        &mut Session::new(),
        "scope hello:\n    file := input\nhello::missing -> output\n",
        &context,
        &[],
    )
    .unwrap_err();
    assert!(format!("{error:?}").contains("scope has no member 'missing'"));
}

#[test]
fn lexical_scope_ranges_survive_source_free_objects() {
    let parent = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/v070");
    fs::create_dir_all(&parent).unwrap();
    let workspace = tempfile::Builder::new()
        .prefix("namespace-objects-")
        .tempdir_in(parent)
        .unwrap();
    let root = workspace.path();
    fs::write(root.join("input"), "captured namespace bytes").unwrap();
    fs::write(root.join("main.msync"), "scope hello:\n    file := input\nalias => hello\nsymbol member_name():\n    return file\nfor namespace in [hello, alias]:\n    namespace::file -> output\n    namespace::member_name() -> computed-output\n").unwrap();
    let run = |arguments: &[&str]| {
        let output = std::process::Command::new(env!("CARGO_BIN_EXE_msync"))
            .current_dir(root)
            .args(arguments)
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
    };
    run(&["compile", "main.msync", "-o", "main.mobj"]);
    fs::remove_file(root.join("main.msync")).unwrap();
    run(&["run", "main.mobj"]);
    assert_eq!(
        fs::read(root.join("output")).unwrap(),
        b"captured namespace bytes"
    );
    assert_eq!(
        fs::read(root.join("computed-output")).unwrap(),
        b"captured namespace bytes"
    );
}

#[test]
fn standard_namespace_aliases_remain_callable_after_object_export_import() {
    let context = CompileContext {
        pwd: Path::new(env!("CARGO_MANIFEST_DIR")).to_owned(),
        source_file: None,
    };
    let mut session = Session::new();
    let module = pipeline::compile_object_named(
        &mut session,
        "fs => $std::fs\nranges => $std::ranges\nbools => $std::bool\n",
        "api.msync",
        &context,
    )
    .unwrap();
    let module = msync::object::ObjectModule::decode(&module.encode().unwrap()).unwrap();
    let mut imported = Session::new();
    imported
        .import_object_exports(&module.exports, &Default::default(), &module.program)
        .unwrap();
    let result = pipeline::compile(
        &mut imported,
        "present = fs::exists(Cargo.toml)\nitems := ranges::take([\"first\", \"second\"], 1)\nfor item in items:\n    item -> .temp/v070-imported-namespace-output\n",
        &context,
        &[],
    );
    assert!(result.is_ok(), "{result:?}");
}

#[test]
fn inferred_native_declarations_preserve_source_signatures() {
    use msync::language::{NativeType, StatementKind};
    let source = "scope ranges:\n    native filter(values, predicate) = \"ranges.filter\"\n    native single(value) = \"ranges.single\"\n    native exists(path) -> Bool = \"fs.exists\"\n";
    let formatted = msync::language::format(source).unwrap();
    assert_eq!(formatted, source);
    assert_eq!(msync::language::format(&formatted).unwrap(), formatted);
    let document = msync::language::parse(&formatted).unwrap();
    let StatementKind::Scope { body, .. } = &document.statements[0].kind else {
        panic!("scope")
    };
    let StatementKind::Native { result, .. } = &body[0].kind else {
        panic!("native")
    };
    assert_eq!(*result, NativeType::Inferred);
    let StatementKind::Native { result, .. } = &body[2].kind else {
        panic!("native")
    };
    assert_eq!(*result, NativeType::Bool);
}
