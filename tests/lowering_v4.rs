use std::path::PathBuf;

use msync::compiler::structured::Session;
use msync::compiler::{Backend, CompileContext};
use msync::hir::{self, Argument, Input, InstructionKind, Output, ReadPort, RollbackScope};
use msync::language::{self, DestinationMode, Isolation};

fn context() -> CompileContext {
    CompileContext {
        pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/lowering-v4"),
        source_file: None,
    }
}

fn compile(session: &mut Session, source: &str) -> hir::Program {
    session
        .compile(&language::parse(source).unwrap(), &context())
        .unwrap()
}

fn rejected(session: &mut Session, source: &str) -> Vec<msync::syntax::Report> {
    session
        .compile(&language::parse(source).unwrap(), &context())
        .unwrap_err()
}

fn transfer(instruction: &hir::Instruction) -> (&Input, &Output) {
    let InstructionKind::Transfer {
        source,
        destination,
    } = &instruction.kind
    else {
        panic!("expected a transfer, got {:?}", instruction.kind);
    };
    (source, destination)
}

fn input_path(input: &Input) -> &msync::compiler::Location {
    let Input::File { location, .. } = input else {
        panic!("expected file input, got {input:?}");
    };
    location
}

fn output_path(output: &Output) -> (&msync::compiler::Location, DestinationMode) {
    let Output::File { location, mode } = output else {
        panic!("expected file output, got {output:?}");
    };
    (location, *mode)
}

#[test]
fn user_macro_expands_typed_remote_sources_and_contextual_destination_items() {
    let source = "macro copy_codex(.codex, des):\n    .codex/agents -> $pwd::des/agents/.\ncopy_codex($wsl::~/.codex, wsl)\ncopy_codex($local::~/.codex, local)\n";
    let program = compile(&mut Session::new(), source);
    assert_eq!(program.body.instructions.len(), 2);
    for (instruction, backend, item) in [
        (&program.body.instructions[0], Backend::Wsl, "wsl"),
        (&program.body.instructions[1], Backend::Local, "local"),
    ] {
        let (input, output) = transfer(instruction);
        let input = input_path(input);
        assert_eq!(input.backend, backend);
        assert_eq!(input.path, "~/.codex/agents");
        let (output, mode) = output_path(output);
        assert_eq!(output.backend, Backend::Local);
        assert!(output.path.ends_with(&format!("/{item}/agents/")));
        assert_eq!(mode, DestinationMode::ReplaceTree);
        assert_eq!(instruction.expansion.len(), 1);
        assert!(source[instruction.span.start..instruction.span.end].starts_with("copy_codex("));
    }
}

#[test]
fn macro_global_uses_definition_snapshot_and_locals_do_not_leak() {
    let mut session = Session::new();
    let program = compile(
        &mut session,
        "root := before\nmacro save(destination):\n    global root\n    temp := root/file\n    temp -> destination\nroot := after\nsave(result)\nroot/file -> other\n",
    );
    assert_eq!(program.body.instructions.len(), 2);
    assert!(
        input_path(transfer(&program.body.instructions[0]).0)
            .path
            .ends_with("/before/file")
    );
    assert!(
        input_path(transfer(&program.body.instructions[1]).0)
            .path
            .ends_with("/after/file")
    );
    assert!(
        !session
            .bindings()
            .iter()
            .any(|binding| binding.starts_with("temp :="))
    );
}

#[test]
fn macros_do_not_implicitly_capture_callers_or_replace_substrings() {
    let mut session = Session::new();
    let program = compile(
        &mut session,
        "caller := private\nmacro move(x):\n    x/file -> xtra/file\n    caller -> other\nmove(base)\n",
    );
    let (source, destination) = transfer(&program.body.instructions[0]);
    assert!(input_path(source).path.ends_with("/base/file"));
    assert!(output_path(destination).0.path.ends_with("/xtra/file"));
    assert!(
        input_path(transfer(&program.body.instructions[1]).0)
            .path
            .ends_with("/caller")
    );
}

#[test]
fn explicit_global_must_precede_definition_and_cannot_replace_a_parameter() {
    let errors = rejected(
        &mut Session::new(),
        "macro save():\n    global late\n    late -> result\nlate := created\nsave()\n",
    );
    assert!(errors[0].diagnostic.message.contains("not declared before"));
    assert!(
        errors[0]
            .labels
            .iter()
            .any(|label| label.message.contains("expanding"))
    );
    let errors = rejected(
        &mut Session::new(),
        "root := before\nmacro save(root):\n    global root\nsave(other)\n",
    );
    assert!(
        errors[0]
            .diagnostic
            .message
            .contains("conflicts with a parameter")
    );
}

#[test]
fn namespace_arguments_and_stream_arguments_retain_types() {
    let program = compile(
        &mut Session::new(),
        "macro remote(space):\n    space::file -> output\nmacro collect(stream):\n    stream -> text\nremote($wsl)\nprogram => $path::python(\"-c\", \"print('你好')\")\ncollect(program::stdout)\n",
    );
    assert_eq!(
        input_path(transfer(&program.body.instructions[0]).0).backend,
        Backend::Wsl
    );
    assert!(matches!(
        transfer(&program.body.instructions[2]).0,
        Input::Port {
            port: ReadPort::Stdout,
            ..
        }
    ));
}

#[test]
fn a_macro_can_call_only_prior_macros_and_recursion_is_explicitly_rejected() {
    let program = compile(
        &mut Session::new(),
        "macro first(x):\n    x -> output\nmacro second(x):\n    first(x)\nsecond(source)\n",
    );
    assert_eq!(program.body.instructions.len(), 1);
    assert_eq!(program.body.instructions[0].expansion.len(), 2);
    let errors = rejected(
        &mut Session::new(),
        "macro loop(x):\n    loop(x)\nloop(file)\n",
    );
    assert!(errors[0].diagnostic.message.contains("recursive"));
    let errors = rejected(
        &mut Session::new(),
        "macro first():\n    later()\nmacro later():\n    \"x\" -> out\nfirst()\n",
    );
    assert!(
        errors[0]
            .diagnostic
            .message
            .contains("unknown macro 'later'")
    );
}

#[test]
fn macro_source_file_is_definition_bound_but_pwd_is_call_bound() {
    let mut session = Session::new();
    let mut definition = context();
    definition.source_file = Some(definition.pwd.join("definitions/source.msync"));
    session
        .compile(
            &language::parse("macro files():\n    $file::this -> $pwd::output\n").unwrap(),
            &definition,
        )
        .unwrap();
    let mut invocation = context();
    invocation.pwd = invocation.pwd.join("caller");
    invocation.source_file = Some(invocation.pwd.join("caller.msync"));
    let program = session
        .compile(&language::parse("files()\n").unwrap(), &invocation)
        .unwrap();
    let (source, destination) = transfer(&program.body.instructions[0]);
    assert_eq!(
        PathBuf::from(&input_path(source).path),
        definition.source_file.unwrap()
    );
    assert_eq!(
        PathBuf::from(&output_path(destination).0.path),
        invocation.pwd.join("output")
    );
}

#[test]
fn process_call_preserves_direct_argv_and_all_typed_ports() {
    let program = compile(
        &mut Session::new(),
        "program => $path::python(\"-c\", \"print('a -> b; $HOME')\", input.txt)\n\"你好\\n\" -> program::stdin\nprogram::stdout -> output.txt\nprogram::stderr -> errors.txt\nprogram::pid -> pid\nprogram::exit_code -> status1\nprogram::exit_code -> status2\nprogram::meta -> metadata\n",
    );
    let InstructionKind::Spawn {
        process,
        executable,
        arguments,
        cwd,
    } = &program.body.instructions[0].kind
    else {
        panic!()
    };
    assert_eq!(process.0, 0);
    assert_eq!(executable.backend, Backend::Path);
    assert_eq!(executable.path, "python");
    assert_eq!(cwd, &context().pwd);
    assert!(matches!(&arguments[0], Argument::Text(value) if value == "-c"));
    assert!(matches!(&arguments[1], Argument::Text(value) if value == "print('a -> b; $HOME')"));
    assert!(matches!(&arguments[2], Argument::Path(value) if value.path.ends_with("/input.txt")));
    assert!(
        matches!(transfer(&program.body.instructions[1]).0, Input::Literal(text) if text.as_ref() == "你好\n")
    );
    assert!(
        matches!(transfer(&program.body.instructions[1]).1, Output::Stdin(id) if id == process)
    );
    assert_eq!(program.body.instructions.len(), 8);
}

#[test]
fn consuming_streams_have_one_owner_and_compile_failure_does_not_consume_them() {
    let mut session = Session::new();
    compile(&mut session, "p => $path::python(\"-V\")\n");
    let errors = rejected(&mut session, "p::stdout -> first\ncommit\n");
    assert!(
        errors[0]
            .diagnostic
            .message
            .contains("enclosing transaction")
    );
    compile(&mut session, "p::stdout -> accepted\n");
    let errors = rejected(&mut session, "p::stdout -> duplicate\n");
    assert!(errors[0].diagnostic.message.contains("only one owner"));
    assert_eq!(errors[0].labels.len(), 1);
}

#[test]
fn process_directions_and_unknown_items_are_diagnosed_before_execution() {
    let mut session = Session::new();
    compile(&mut session, "p => $path::python()\n");
    assert!(
        rejected(&mut session, "p::stdin -> out\n")[0]
            .diagnostic
            .message
            .contains("not a readable")
    );
    assert!(
        rejected(&mut session, "source -> p::stdout\n")[0]
            .diagnostic
            .message
            .contains("not a writable")
    );
    assert!(
        rejected(&mut session, "p::unknown -> out\n")[0]
            .diagnostic
            .message
            .contains("unknown program item")
    );
}

#[test]
fn transaction_checkpoints_named_origin_and_shared_bindings_are_distinct() {
    let program = compile(
        &mut Session::new(),
        "transaction A1(read uncommitted):\n    result := output\n    \"first\" -> result\n    commit\n    \"second\" -> result\n    rollback\nrollback A1\n\"third\" -> result\ntransaction B2:\n    \"ok\" -> verifier\n",
    );
    assert_eq!(program.transactions.len(), 2);
    assert_eq!(
        program.transactions[0].isolation,
        Isolation::ReadUncommitted
    );
    assert_eq!(program.transactions[1].isolation, Isolation::Serializable);
    let InstructionKind::Transaction { transaction, body } = &program.body.instructions[0].kind
    else {
        panic!()
    };
    assert!(
        matches!(body.instructions[1].kind, InstructionKind::Commit { transaction: id } if id == *transaction)
    );
    assert!(matches!(
        body.instructions[3].kind,
        InstructionKind::Rollback {
            scope: RollbackScope::Checkpoint,
            ..
        }
    ));
    assert!(matches!(
        program.body.instructions[1].kind,
        InstructionKind::Rollback {
            scope: RollbackScope::Begin,
            ..
        }
    ));
    assert!(
        output_path(transfer(&program.body.instructions[2]).1)
            .0
            .path
            .ends_with("/output")
    );
}

#[test]
fn other_transaction_rollback_is_forbidden_inside_a_transaction() {
    let errors = rejected(
        &mut Session::new(),
        "transaction A:\n    \"a\" -> output\ntransaction B:\n    rollback A\n",
    );
    assert!(errors[0].diagnostic.message.contains("not allowed inside"));
    assert!(
        errors[0]
            .help
            .iter()
            .any(|help| help.contains("bare rollback"))
    );
}

#[test]
fn ids_are_not_recycled_across_submissions_or_reset() {
    let mut session = Session::new();
    let first = compile(
        &mut session,
        "p => $path::python()\ntransaction A:\n    \"a\" -> out\n",
    );
    let second = compile(&mut session, "q => $path::python()\nrollback A\n");
    let InstructionKind::Spawn { process: first, .. } = first.body.instructions[0].kind else {
        panic!()
    };
    let InstructionKind::Spawn {
        process: second, ..
    } = second.body.instructions[0].kind
    else {
        panic!()
    };
    assert!(second.0 > first.0);
    session.reset();
    let third = compile(&mut session, "r => $path::python()\n");
    let InstructionKind::Spawn { process: third, .. } = third.body.instructions[0].kind else {
        panic!()
    };
    assert!(third.0 > second.0);
}

#[test]
fn failed_compilation_does_not_leak_lexical_or_macro_bindings() {
    let mut session = Session::new();
    compile(&mut session, "existing := retained\n");
    let before = session.bindings();
    assert!(
        !rejected(
            &mut session,
            "new := discarded\nmacro unused():\n    \"x\" -> output\ncommit\n"
        )
        .is_empty()
    );
    assert_eq!(session.bindings(), before);
}

#[test]
fn path_modes_survive_aliases_and_explicit_replace_is_not_a_container() {
    let program = compile(
        &mut Session::new(),
        "replace := folder/.\ncontainer := folder/\nsource -> replace\nsource -> container\nsource -> plain\n",
    );
    let (_, first) = transfer(&program.body.instructions[0]);
    let (path, mode) = output_path(first);
    assert!(path.path.ends_with("/folder/"));
    assert_eq!(mode, DestinationMode::ReplaceTree);
    assert_eq!(
        output_path(transfer(&program.body.instructions[1]).1).1,
        DestinationMode::Container
    );
    assert_eq!(
        output_path(transfer(&program.body.instructions[2]).1).1,
        DestinationMode::Exact
    );
}

#[test]
fn read_only_transports_and_selector_policy_reuse_existing_compiler_rules() {
    let errors = rejected(
        &mut Session::new(),
        "web => https://example.com\nsource -> web::out\n",
    );
    assert!(errors[0].diagnostic.message.contains("read-only"));
    let errors = rejected(&mut Session::new(), "source/<(> -> output/\n");
    assert!(errors[0].diagnostic.message.contains("unclosed group"));
    let program = compile(&mut Session::new(), "source/<.*\\.txt> -> output/\n");
    assert!(
        matches!(transfer(&program.body.instructions[0]).0, Input::File { pattern: Some(value), .. } if value == ".*\\.txt")
    );
}

#[test]
fn replacement_marker_keeps_the_directory_root_identity() {
    let errors = rejected(&mut Session::new(), "source -> $local::/.\n");
    assert!(errors[0].diagnostic.message.contains("filesystem root"));
    let errors = rejected(&mut Session::new(), "source -> $wsl::/.\n");
    assert!(errors[0].diagnostic.message.contains("filesystem root"));
    let program = compile(&mut Session::new(), "source -> $wsl::/tree/.\n");
    let (destination, mode) = output_path(transfer(&program.body.instructions[0]).1);
    assert_eq!(destination.path, "/tree/");
    assert_eq!(mode, DestinationMode::ReplaceTree);
    #[cfg(windows)]
    {
        let errors = rejected(&mut Session::new(), "source -> $local::C:/.\n");
        assert!(errors[0].diagnostic.message.contains("filesystem root"));
        let program = compile(&mut Session::new(), "source -> $local::C:/tree/.\n");
        let (destination, mode) = output_path(transfer(&program.body.instructions[0]).1);
        assert_eq!(destination.path, "C:/tree/");
        assert_eq!(mode, DestinationMode::ReplaceTree);
    }
}

#[test]
fn a_shadowing_macro_can_call_the_prior_definition_with_the_same_name() {
    let program = compile(
        &mut Session::new(),
        "macro save(x):\n    x -> original\nmacro save(x):\n    save(x)\nsave(input)\n",
    );
    assert_eq!(program.body.instructions.len(), 1);
    assert_eq!(program.body.instructions[0].expansion.len(), 2);
    assert!(
        output_path(transfer(&program.body.instructions[0]).1)
            .0
            .path
            .ends_with("/original")
    );
}

#[test]
fn manually_constructed_nested_macro_ast_is_rejected_even_if_not_called() {
    let span = msync::syntax::Span { start: 0, end: 1 };
    let name = |text: &str| language::Name {
        text: text.to_owned(),
        span,
    };
    let document = language::Document {
        statements: vec![language::Statement {
            span,
            kind: language::StatementKind::Macro {
                name: name("outer"),
                parameters: vec![],
                body: vec![language::Statement {
                    span,
                    kind: language::StatementKind::Macro {
                        name: name("inner"),
                        parameters: vec![],
                        body: vec![],
                    },
                }],
            },
        }],
    };
    let errors = Session::new().compile(&document, &context()).unwrap_err();
    assert!(
        errors[0]
            .diagnostic
            .message
            .contains("cannot declare another macro")
    );
}

#[test]
fn qualified_quoted_source_remains_a_path_not_a_literal() {
    let program = compile(
        &mut Session::new(),
        "$local::\"folder name/file.txt\" -> ./\"destination name.txt\"\n\"folder name/file.txt\" -> literal.txt\n",
    );
    let (source, destination) = transfer(&program.body.instructions[0]);
    assert!(input_path(source).path.ends_with("/folder name/file.txt"));
    assert!(
        output_path(destination)
            .0
            .path
            .ends_with("/destination name.txt")
    );
    assert!(
        matches!(transfer(&program.body.instructions[1]).0, Input::Literal(text) if text.as_ref() == "folder name/file.txt")
    );
}

#[test]
fn a_transaction_does_not_create_a_macro_lexical_scope() {
    let program = compile(
        &mut Session::new(),
        "transaction A:\n    root := captured\n    macro save():\n        global root\n        root -> output\nsave()\n",
    );
    assert_eq!(program.body.instructions.len(), 2);
    assert!(
        input_path(transfer(&program.body.instructions[1]).0)
            .path
            .ends_with("/captured")
    );
}

#[test]
fn nul_text_cannot_be_passed_as_native_argv() {
    let span = msync::syntax::Span { start: 0, end: 1 };
    let callee = language::Expr {
        span,
        source: msync::syntax::OperandSource::unmapped(span),
        kind: language::ExprKind::Path(language::PathExpr {
            qualifier: Some(language::Name {
                text: "$path".to_owned(),
                span,
            }),
            path: "python".to_owned(),
            mode: DestinationMode::Exact,
        }),
    };
    let argument = language::Expr {
        kind: language::ExprKind::Literal("a\0b".to_owned()),
        span,
        source: msync::syntax::OperandSource::unmapped(span),
    };
    let document = language::Document {
        statements: vec![language::Statement {
            span,
            kind: language::StatementKind::Binding {
                name: language::Name {
                    text: "p".to_owned(),
                    span,
                },
                op: language::BindingOp::Space,
                value: language::Expr {
                    span,
                    source: msync::syntax::OperandSource::unmapped(span),
                    kind: language::ExprKind::Call {
                        callee: Box::new(callee),
                        arguments: vec![argument],
                    },
                },
            },
        }],
    };
    let errors = Session::new().compile(&document, &context()).unwrap_err();
    assert!(errors[0].diagnostic.message.contains("cannot contain NUL"));
    assert!(errors[0].help[0].contains("stdin"));
}
