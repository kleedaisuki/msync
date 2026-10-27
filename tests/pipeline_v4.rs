use std::path::PathBuf;

use msync::compiler::CompileContext;
use msync::compiler::structured::Session;
use msync::hir::{Input, InstructionKind, Output, ProcessId};
use msync::pipeline;

fn context() -> CompileContext {
    CompileContext {
        pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/pipeline-no-io-v4"),
        source_file: None,
    }
}

#[test]
fn macro_expansion_and_graph_compilation_need_no_filesystem_or_child() {
    let mut session = Session::new();
    let compiled = pipeline::compile(
        &mut session,
        "macro write(value, path):\n    value -> $pwd::path\nwrite(\"你好\\n\", output.txt)\n",
        &context(),
        &[],
    )
    .unwrap();
    assert_eq!(compiled.program.body.instructions.len(), 1);
    let InstructionKind::Transfer {
        source: Input::Literal(text),
        destination: Output::File { location, .. },
    } = &compiled.program.body.instructions[0].kind
    else {
        panic!()
    };
    assert_eq!(&**text, "你好\n");
    assert_eq!(
        PathBuf::from(&location.path),
        context().pwd.join("output.txt")
    );
    compiled.graph.verify(&compiled.program).unwrap();
    assert!(!context().pwd.exists());
}

#[test]
fn failed_effect_check_does_not_commit_macros_bindings_or_process_identities() {
    let mut session = Session::new();
    let source = "leaked := no\nmacro leaked_macro(x):\n    x -> out\ntransaction A:\n    child => $path::bash(\"-c\", \"cat\")\n    commit\n    \"data\" -> child::stdin\n";
    let errors = pipeline::compile(&mut session, source, &context(), &[]).unwrap_err();
    assert!(!errors.is_empty());
    assert!(session.bindings().is_empty());
    assert!(errors.iter().all(|error| {
        error.diagnostic.span.end <= source.len()
            && source.is_char_boundary(error.diagnostic.span.start)
    }));
    let next = pipeline::compile(
        &mut session,
        "child => $path::bash(\"-c\", \"exit 0\")\n",
        &context(),
        &[],
    )
    .unwrap();
    assert!(matches!(
        next.program.body.instructions[0].kind,
        InstructionKind::Spawn {
            process: ProcessId(0),
            ..
        }
    ));
}

#[test]
fn separate_submissions_retain_macro_environment_but_not_execution_effects() {
    let mut session = Session::new();
    pipeline::compile(
        &mut session,
        "root := before\nmacro save():\n    global root\n    root -> output\n",
        &context(),
        &[],
    )
    .unwrap();
    pipeline::compile(&mut session, "root := after\n", &context(), &[]).unwrap();
    let compiled = pipeline::compile(&mut session, "save()\n", &context(), &[]).unwrap();
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        ..
    } = &compiled.program.body.instructions[0].kind
    else {
        panic!()
    };
    assert_eq!(PathBuf::from(&location.path), context().pwd.join("before"));
}
