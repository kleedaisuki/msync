#[cfg(unix)]
#[path = "support/literal_string.rs"]
mod literal_string;

use std::fs;
use std::path::Path;
use std::process::Command;

use msync::compiler::{CompileContext, structured::Session};
use msync::context::Context;
use msync::hir::{BarrierKind, Instruction, InstructionId, InstructionKind, Program, Region};
use msync::object::ObjectModule;
use msync::pipeline;
use msync::source::{InstructionOrigin, Origins, Source, SourceMap};
use msync::syntax::Span;

fn workspace() -> tempfile::TempDir {
    let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&base).unwrap();
    tempfile::Builder::new()
        .prefix("compiler-acceptance-")
        .tempdir_in(base)
        .unwrap()
}

fn context(root: &Path) -> CompileContext {
    CompileContext {
        pwd: root.to_owned(),
        source_file: Some(root.join("entry.msync")),
    }
}

fn check(root: &Path, source: &str) {
    fs::write(root.join("entry.msync"), source).unwrap();
    let output = Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root)
        .args(["--no-config", "check", "entry.msync"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
}

#[test]
fn flat_call_width_is_independent_of_nested_stack_depth() {
    let work = workspace();
    for width in [129, 257] {
        let calls = std::iter::repeat_n("id(\"a\")", width)
            .collect::<Vec<_>>()
            .join(", ");
        let source = format!("symbol id(value):\n    return value\n[{calls}] -> out/\n");
        check(work.path(), &source);
        let context = context(work.path());
        let loaded = pipeline::compile_relocatable_named(
            &mut Session::new(),
            &source,
            context.source_file.as_ref().unwrap().to_str().unwrap(),
            &context,
            &Default::default(),
        )
        .unwrap()
        .into_loaded_object()
        .unwrap();
        let encoded = loaded.encode().unwrap();
        ObjectModule::decode_loaded(&encoded)
            .unwrap()
            .contextualize(&Context::new(&context), &mut Session::new())
            .unwrap();
    }
}

#[test]
fn composed_scope_width_uses_a_balanced_representation() {
    let work = workspace();
    let mut source = "scope := $pwd\n".to_owned();
    source.push_str(&"scope := scope::($pwd)\n".repeat(257));
    source.push_str("scope::leaf -> output\n");
    check(work.path(), &source);
    let context = context(work.path());
    let object =
        pipeline::compile_object_named(&mut Session::new(), &source, "scopes.msync", &context)
            .unwrap();
    let loaded = ObjectModule::decode_loaded(&object.encode().unwrap()).unwrap();
    let program = loaded
        .contextualize(&Context::new(&context), &mut Session::new())
        .unwrap();
    let InstructionKind::Transfer {
        source: msync::hir::Input::File { location, .. },
        ..
    } = &program.program.body.instructions[0].kind
    else {
        panic!("expected selected file");
    };
    assert_eq!(Path::new(&location.path), work.path().join("leaf"));
}

#[cfg(unix)]
#[test]
fn quoted_native_filenames_keep_whitespace_newlines_and_backslashes() {
    let work = workspace();
    for (index, name) in [" leading", "trailing ", "line\nslash\\ 可莉"]
        .into_iter()
        .enumerate()
    {
        fs::write(work.path().join(name), b"literal native filename").unwrap();
        let source = format!(
            "$local::{} -> copied-{index}\n",
            literal_string::quoted(name)
        );
        fs::write(work.path().join("entry.msync"), &source).unwrap();
        let output = Command::new(env!("CARGO_BIN_EXE_msync"))
            .current_dir(work.path())
            .args(["--no-config", "run", "entry.msync"])
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{name:?}: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(
            fs::read(work.path().join(format!("copied-{index}"))).unwrap(),
            b"literal native filename"
        );
    }
}

#[test]
fn repeated_includes_do_not_spend_a_file_or_aggregate_byte_quota() {
    let work = workspace();
    fs::write(work.path().join("empty.msync"), "# ordinary input\n").unwrap();
    check(work.path(), &"include \"empty.msync\"\n".repeat(1025));
    let comment = format!("#{}\n", "x".repeat(8 * 1024 * 1024));
    fs::write(work.path().join("large.msync"), &comment).unwrap();
    check(work.path(), &"include \"large.msync\"\n".repeat(5));
}

#[test]
fn source_selection_and_object_bindings_accept_more_than_1024_modules() {
    let work = workspace();
    let parts = work.path().join("parts");
    fs::create_dir(&parts).unwrap();
    for index in 0..1025 {
        fs::write(
            parts.join(format!("part-{index:04}.msync")),
            format!("location_{index} := $file::this\n"),
        )
        .unwrap();
    }
    let source = "include parts/<.+\\.msync>\n";
    check(work.path(), source);
    let context = context(work.path());
    let object = pipeline::compile_object_named(
        &mut Session::new(),
        source,
        context.source_file.as_ref().unwrap().to_str().unwrap(),
        &context,
    )
    .unwrap();
    assert_eq!(object.source_modules.len(), 1025);
    ObjectModule::decode_loaded(&object.encode().unwrap())
        .unwrap()
        .contextualize(&Context::new(&context), &mut Session::new())
        .unwrap();
}

#[test]
fn object_wire_size_is_not_a_format_quota() {
    let work = workspace();
    let context = context(work.path());
    let object = pipeline::compile_object_named(
        &mut Session::new(),
        "\"value\" -> output\n",
        "wire.msync",
        &context,
    )
    .unwrap();
    let mut bytes = object.encode().unwrap();
    bytes.resize(64 * 1024 * 1024 + 1, b' ');
    ObjectModule::decode_loaded(&bytes).unwrap();
}

#[test]
fn object_instruction_width_is_not_a_format_quota() {
    let count = 100_001;
    let source = Source::new("generated.msync", "");
    let mut sources = SourceMap::default();
    sources.insert(source.clone());
    let mut instructions = Vec::with_capacity(count);
    let mut origins = Origins::default();
    for index in 0..count {
        let id = InstructionId(index);
        instructions.push(Instruction {
            id,
            kind: InstructionKind::Barrier {
                kind: BarrierKind::Before,
            },
            span: Span::default(),
            expansion: Vec::new(),
        });
        origins.insert(
            id,
            InstructionOrigin {
                site: source.at(Span::default()),
                definition: source.at(Span::default()),
                expansion: Vec::new(),
                operands: None,
            },
        );
    }
    let object = ObjectModule::from_parts(
        Program {
            body: Region { instructions },
        },
        Vec::new(),
        Vec::new(),
        source,
        sources,
        origins,
        Default::default(),
        "generated.msync".into(),
    )
    .unwrap();
    assert_eq!(object.program.body.instructions.len(), count);
}
