use std::path::PathBuf;

use msync::compiler::structured::Session;
use msync::compiler::{Backend, CompileContext};
use msync::hir::{Argument, BarrierKind, Input, InstructionKind, Output};
use msync::language::{self, DestinationMode, ExprKind, StatementKind};

fn compile(source: &str) -> msync::hir::Program {
    let context = CompileContext {
        pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
        source_file: None,
    };
    Session::new()
        .compile(&language::parse(source).unwrap(), &context)
        .unwrap()
}

fn transfer(source: &str) -> (Input, Output) {
    let mut instructions = compile(source).body.instructions;
    let InstructionKind::Transfer {
        source,
        destination,
    } = instructions.remove(0).kind
    else {
        panic!()
    };
    (source, destination)
}

#[test]
fn slash_is_structural_and_compact_or_spaced_forms_resolve_identically() {
    let left = transfer("$pwd / \"input file\" -> output\n");
    let right = transfer("$pwd/\"input file\" -> output\n");
    let (Input::File { location: left, .. }, _) = left else {
        panic!()
    };
    let (
        Input::File {
            location: right, ..
        },
        _,
    ) = right
    else {
        panic!()
    };
    assert_eq!(left, right);
    assert_eq!(
        PathBuf::from(left.path),
        PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("input file")
    );
    let document = language::parse("./\"file name\" -> target/.\n").unwrap();
    let StatementKind::Transfer {
        source,
        destination,
    } = &document.statements[0].kind
    else {
        panic!()
    };
    assert!(matches!(source.kind, ExprKind::Join { .. }));
    assert!(matches!(
        destination.kind,
        ExprKind::Join {
            mode: DestinationMode::ReplaceTree,
            ..
        }
    ));
}

#[test]
fn literal_data_is_not_a_path_until_the_join_operator_supplies_path_context() {
    assert!(
        matches!(transfer("\" file name \" -> target\n").0, Input::Literal(text) if &*text == " file name ")
    );
    let (Input::File { location, .. }, _) = transfer("\"foo\" / \"bar baz\" -> target\n") else {
        panic!()
    };
    assert!(location.path.ends_with("/foo/bar baz"));
    let (Input::File { location, .. }, _) = transfer("$local::\"file name\" -> target\n") else {
        panic!()
    };
    assert!(location.path.ends_with("/file name"));
    let (Input::File { location, .. }, _) =
        transfer("same := replaced\n$local::\"same\" -> target\n")
    else {
        panic!()
    };
    assert!(location.path.ends_with("/same"));
    let context = CompileContext {
        pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
        source_file: None,
    };
    assert!(
        Session::new()
            .compile(
                &language::parse("source -> \" bad path \"\n").unwrap(),
                &context
            )
            .is_err()
    );
    assert!(
        Session::new()
            .compile(
                &language::parse(". / \" bad component\" -> target\n").unwrap(),
                &CompileContext {
                    pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
                    source_file: None
                }
            )
            .is_err()
    );
}

#[test]
fn quoted_bindings_are_data_and_path_construction_remains_explicit() {
    let program = compile(
        "text := \" file name \"\ntext -> output\npath := ./\"file name\"\npath -> other\n",
    );
    assert!(
        matches!(&program.body.instructions[0].kind, InstructionKind::Transfer { source: Input::Literal(text), .. } if &**text == " file name ")
    );
    assert!(
        matches!(&program.body.instructions[1].kind, InstructionKind::Transfer { source: Input::File { location, .. }, .. } if location.path.ends_with("/./file name"))
    );
    let program = compile(
        "macro copy(base, leaf):\n    base -> $pwd::leaf\ncopy(source, \"destination name\")\n",
    );
    assert!(
        matches!(&program.body.instructions[0].kind, InstructionKind::Transfer { destination: Output::File { location, .. }, .. } if location.path.ends_with("/destination name"))
    );
}

#[test]
fn typed_bindings_roundtrip_without_turning_paths_into_data_or_pinning_relative_endpoints() {
    let source = "path := ./\"file name\"\ntext := \"literal bytes\"\ncontainer := folder/\nreplace := folder/.\nserver => https://example.com/base\nremote := server::item\n";
    let context = CompileContext {
        pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
        source_file: None,
    };
    let mut original = Session::new();
    original
        .compile(&language::parse(source).unwrap(), &context)
        .unwrap();
    let printed = original.bindings().join("\n");
    assert!(printed.contains("text := \"literal bytes\""));
    assert!(printed.contains("path := \".\"/"));
    let mut replayed = Session::new();
    replayed
        .compile(&language::parse(&printed).unwrap(), &context)
        .unwrap();
    let program = replayed.compile(&language::parse("$wsl::path -> out\ntext -> bytes\nsource -> container\nsource -> replace\nremote -> remote-out\n").unwrap(), &context).unwrap();
    assert!(
        matches!(&program.body.instructions[0].kind, InstructionKind::Transfer { source: Input::File { location, .. }, .. } if location.backend == Backend::Wsl && location.path.ends_with("file name"))
    );
    assert!(
        matches!(&program.body.instructions[1].kind, InstructionKind::Transfer { source: Input::Literal(text), .. } if &**text == "literal bytes")
    );
    assert!(matches!(
        &program.body.instructions[2].kind,
        InstructionKind::Transfer {
            destination: Output::File {
                mode: DestinationMode::Container,
                ..
            },
            ..
        }
    ));
    assert!(matches!(
        &program.body.instructions[3].kind,
        InstructionKind::Transfer {
            destination: Output::File {
                mode: DestinationMode::ReplaceTree,
                ..
            },
            ..
        }
    ));
    assert!(
        matches!(&program.body.instructions[4].kind, InstructionKind::Transfer { source: Input::File { location, .. }, .. } if matches!(&location.backend, Backend::Http { base } if base == "https://example.com/base"))
    );
}

#[test]
fn quoted_dot_and_slash_fragments_never_invent_destination_intent() {
    let (_, output) = transfer("source -> \".\"/\".\"\n");
    assert!(matches!(
        output,
        Output::File {
            mode: DestinationMode::Exact,
            ..
        }
    ));
    let (_, output) = transfer("source -> $local::\"folder/.\"\n");
    assert!(matches!(
        output,
        Output::File {
            mode: DestinationMode::Exact,
            ..
        }
    ));
    let (_, output) = transfer("source -> $local::\"folder/\"\n");
    assert!(matches!(
        output,
        Output::File {
            mode: DestinationMode::Exact,
            ..
        }
    ));
    let (_, output) = transfer("source -> $local::\"folder\"/\n");
    assert!(matches!(
        output,
        Output::File {
            mode: DestinationMode::Container,
            ..
        }
    ));
    let context = CompileContext {
        pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
        source_file: None,
    };
    for source in [
        "source -> $local::\"directory /file\"\n",
        ". / \"dir/ file\" -> target\n",
    ] {
        let rejected = language::parse(source).is_err()
            || Session::new()
                .compile(&language::parse(source).unwrap(), &context)
                .is_err();
        assert!(rejected, "{source}");
    }
}

#[test]
fn joined_macro_operands_are_lexical_values_not_source_text_substitutions() {
    let program = compile(
        "macro copy(base, leaf):\n    base / leaf -> output/\ncopy($wsl::~/.codex, \"agents with spaces\")\n",
    );
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        ..
    } = &program.body.instructions[0].kind
    else {
        panic!()
    };
    assert_eq!(location.backend, Backend::Wsl);
    assert_eq!(location.path, "~/.codex/agents with spaces");
    let program = compile(
        "macro copy(base, agents):\n    base/agents -> one\n    base/\"agents\" -> two\ncopy(root, renamed)\n",
    );
    let paths = program
        .body
        .instructions
        .iter()
        .map(|node| {
            let InstructionKind::Transfer {
                source: Input::File { location, .. },
                ..
            } = &node.kind
            else {
                panic!()
            };
            location.path.clone()
        })
        .collect::<Vec<_>>();
    assert!(paths[0].ends_with("/root/renamed"));
    assert!(paths[1].ends_with("/root/agents"));
}

#[test]
fn namespace_rooting_is_explicit_and_rooted_rhs_never_discards_a_base() {
    let (Input::File { location, .. }, _) = transfer("$wsl / \"/root/agents\" -> target\n") else {
        panic!()
    };
    assert_eq!(location.path, "/root/agents");
    let source = "folder / \"/absolute\" -> target\n";
    let errors = Session::new()
        .compile(
            &language::parse(source).unwrap(),
            &CompileContext {
                pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR")),
                source_file: None,
            },
        )
        .unwrap_err();
    assert_eq!(
        &source[errors[0].diagnostic.span.start..errors[0].diagnostic.span.end],
        "/"
    );
    assert!(errors[0].diagnostic.message.contains("rooted"));
}

#[test]
fn composed_executable_calls_and_selector_slashes_keep_their_grammar() {
    let program = compile("p => $pwd / \"tool name\"(\"exact arg\")\n");
    let [before, spawn, after] = program.body.instructions.as_slice() else {
        panic!("a synchronous invocation must lower to before, spawn, await")
    };
    assert!(matches!(
        before.kind,
        InstructionKind::Barrier {
            kind: BarrierKind::Before
        }
    ));
    let InstructionKind::Spawn {
        process,
        executable,
        arguments,
        ..
    } = &spawn.kind
    else {
        panic!("the invocation must follow its before fence")
    };
    assert!(matches!(after.kind, InstructionKind::Await { process: Some(id) } if id == *process));
    assert!(matches!(arguments.as_slice(), [Argument::Text(text)] if text == "exact arg"));
    assert!(executable.path.ends_with("/tool name"));
    let program = compile("server => https://example.com/base/path\nserver::item -> target\n");
    let InstructionKind::Transfer {
        source: Input::File { location, .. },
        ..
    } = &program.body.instructions[0].kind
    else {
        panic!()
    };
    assert!(matches!(location.backend, Backend::Http { .. }));
    let program = compile("directory/<a/b> -> target/\n");
    let InstructionKind::Transfer {
        source: Input::File { pattern, .. },
        ..
    } = &program.body.instructions[0].kind
    else {
        panic!()
    };
    assert_eq!(pattern.as_deref(), Some("a/b"));
    let program = compile("server => tcp://[::1]:9000\nserver::/ -> target\n");
    assert!(
        matches!(&program.body.instructions[0].kind, InstructionKind::Transfer { source: Input::File { location, .. }, .. } if matches!(&location.backend, Backend::Tcp { host, port: 9000 } if host == "::1"))
    );
}

#[test]
fn formatting_join_expressions_is_idempotent_and_keeps_literal_fragments_quoted() {
    let source = "macro copy(base, leaf):\n\tbase / leaf -> $pwd / \"destination name\" / .\ncopy($wsl::~/.codex, \"agents\")\n\" literal data \" -> target\n";
    let formatted = language::format(source).unwrap();
    assert_eq!(language::format(&formatted).unwrap(), formatted);
    assert!(formatted.contains("$pwd/\"destination name\"/."));
    assert!(formatted.contains("\" literal data \" -> target"));
    let program = compile(&formatted);
    assert_eq!(program.body.instructions.len(), 2);
}

#[test]
fn pathological_join_nesting_returns_a_source_diagnostic() {
    let source = format!(
        "{} -> target\n",
        std::iter::repeat_n("part", 1024)
            .collect::<Vec<_>>()
            .join("/")
    );
    let errors = language::parse(&source).unwrap_err();
    assert!(errors[0].diagnostic.message.contains("nesting"));
}
