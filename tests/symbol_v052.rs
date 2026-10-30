//! Compile-time symbol functions are hygienic, generic and effect-free.

use std::path::PathBuf;

use msync::compiler::{Backend, CompileContext, structured::Session};
use msync::hir::graph::PlanningContext;
use msync::hir::{Input, InstructionKind};
use msync::{language, pipeline};

fn context(name: &str) -> CompileContext {
    let pwd = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/symbol-v052");
    CompileContext {
        source_file: Some(pwd.join(name)),
        pwd,
    }
}

fn compile(source: &str) -> msync::hir::Program {
    Session::new()
        .compile(&language::parse(source).unwrap(), &context("code.msync"))
        .unwrap()
}

fn input(program: &msync::hir::Program, index: usize) -> &Input {
    let InstructionKind::Transfer { source, .. } = &program.body.instructions[index].kind else {
        panic!("expected transfer")
    };
    source
}

#[test]
fn nested_generic_symbols_return_items_strings_and_spaces_without_hir() {
    let program = compile(
        "symbol identity(value):\n    return value\nsymbol compose(base, leaf):\n    result := identity(base) / identity(leaf)\n    return result\nspace => identity($wsl)\ncompose(space::~, \"agents\") -> output\nidentity(\"literal payload\") -> literal\nidentity(\"discarded\")\n",
    );
    assert_eq!(program.body.instructions.len(), 2);
    assert!(
        matches!(input(&program, 0), Input::File { location, .. } if location.backend == Backend::Wsl && location.path == "~/agents")
    );
    assert!(matches!(input(&program, 1), Input::Literal(text) if &**text == "literal payload"));
}

#[test]
fn returned_spaces_support_direct_namespace_selection() {
    let program = compile(
        "symbol identity(value):\n    return value\nidentity($wsl)::~/input -> output\nidentity($pwd)::pwd/input -> local\n",
    );
    assert!(
        matches!(input(&program, 0), Input::File { location, .. } if location.backend == Backend::Wsl && location.path == "~/input")
    );
    assert!(
        matches!(input(&program, 1), Input::File { location, .. } if location.backend == Backend::Local && location.path.ends_with("/input"))
    );
}

#[test]
fn lexical_globals_capture_prior_values_and_callers_are_not_captured() {
    let program = compile(
        "value := \"old\"\nsymbol captured():\n    global value\n    return value\nsymbol isolated():\n    return value\nvalue := \"new\"\ncaptured() -> captured-output\nisolated() -> isolated-output\nvalue -> original\n",
    );
    assert!(matches!(input(&program, 0), Input::Literal(text) if &**text == "old"));
    assert!(
        matches!(input(&program, 1), Input::File { location, .. } if location.path.ends_with("/value"))
    );
    assert!(matches!(input(&program, 2), Input::Literal(text) if &**text == "new"));
}

#[test]
fn generic_operation_types_are_checked_only_at_instantiation() {
    let mut session = Session::new();
    let definition = "symbol child(base):\n    return base / \"child\"\n";
    session
        .compile(
            &language::parse(definition).unwrap(),
            &context("defs.msync"),
        )
        .unwrap();
    session
        .compile(
            &language::parse("child(root) -> output\n").unwrap(),
            &context("good.msync"),
        )
        .unwrap();
    let errors = session
        .compile(
            &language::parse("child($pwd) -> output\n").unwrap(),
            &context("bad.msync"),
        )
        .unwrap_err();
    assert!(
        errors
            .iter()
            .any(|error| error.diagnostic.message.contains("not a space")),
        "{errors:?}"
    );
}

#[test]
fn foreign_symbol_errors_own_definitions_and_instantiation_sites() {
    let mut session = Session::new();
    pipeline::compile_named(
        &mut session,
        "symbol child(base):\n    return base / \"child\"\n",
        "defs.msync",
        &context("defs.msync"),
        &PlanningContext::default(),
    )
    .unwrap();
    let errors = pipeline::compile_named(
        &mut session,
        "child($pwd) -> output\n",
        "use.msync",
        &context("use.msync"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    let rendered = errors[0].render(false);
    for expected in [
        "defs.msync:2:",
        "use.msync:1:",
        "while instantiating symbol 'child'",
        "symbol parameter 'base' expands to space $pwd",
    ] {
        assert!(
            rendered.contains(expected),
            "missing {expected}: {rendered}"
        );
    }
}

#[test]
fn mandatory_return_and_effect_restrictions_are_enforced() {
    for source in [
        "symbol bad():\n    x := item\n",
        "symbol bad():\n    source -> target\n    return item\n",
        "symbol bad():\n    space => $wsl\n    return space\n",
        "symbol bad():\n    release\n    return item\n",
        "symbol bad():\n    execute \"file.msync\"\n    return item\n",
        "symbol bad():\n    return item\n    x := other\n",
    ] {
        let parsed = language::parse(source);
        assert!(
            parsed.is_err()
                || Session::new()
                    .compile(&parsed.unwrap(), &context("bad.msync"))
                    .is_err(),
            "{source}"
        );
    }
    assert!(
        Session::new()
            .compile(
                &language::parse("return item\n").unwrap(),
                &context("return.msync")
            )
            .is_err()
    );
}

#[test]
fn nested_calls_fail_arity_and_recursion_without_partial_bindings() {
    let mut session = Session::new();
    let source = "symbol recurse():\n    return recurse()\n";
    session
        .compile(&language::parse(source).unwrap(), &context("defs.msync"))
        .unwrap();
    let errors = session
        .compile(
            &language::parse("temporary := item\nresult := recurse()\n").unwrap(),
            &context("call.msync"),
        )
        .unwrap_err();
    assert!(
        errors
            .iter()
            .any(|error| error.diagnostic.message.contains("recursive")),
        "{errors:?}"
    );
    assert!(
        !session
            .bindings()
            .iter()
            .any(|binding| binding.starts_with("temporary :="))
    );
    assert!(
        session
            .compile(
                &language::parse("recurse(argument) -> output\n").unwrap(),
                &context("arity.msync")
            )
            .unwrap_err()[0]
            .diagnostic
            .message
            .contains("expects 0")
    );
}

#[test]
fn symbol_calls_do_not_create_program_launch_boundaries() {
    let program = compile(
        "symbol identity(value):\n    return value\nspace => identity($wsl)\n\"a\" -> one\n\"b\" -> two\n",
    );
    assert_eq!(program.body.instructions.len(), 2);
    assert_eq!(program.blocks().count(), 1);
}

#[test]
fn returned_executables_are_called_only_by_the_outer_program_invocation() {
    let program = compile(
        "symbol executable():\n    return $path::tool\np => executable()(\"exact argument\")\n",
    );
    let spawns = program
        .body
        .instructions
        .iter()
        .filter_map(|node| match &node.kind {
            InstructionKind::Spawn {
                executable,
                arguments,
                ..
            } => Some((executable, arguments)),
            _ => None,
        })
        .collect::<Vec<_>>();
    assert_eq!(spawns.len(), 1);
    assert_eq!(spawns[0].0.backend, Backend::Path);
    assert_eq!(spawns[0].0.path, "tool");
    assert!(
        matches!(spawns[0].1.as_slice(), [msync::hir::Argument::Text(text)] if text == "exact argument")
    );
}

#[test]
fn prior_shadowed_symbol_definition_remains_callable_lexically() {
    let program = compile(
        "symbol value(x):\n    return x\nsymbol value(x):\n    return value(x)\nvalue(\"captured definition\") -> output\n",
    );
    assert!(matches!(input(&program, 0), Input::Literal(text) if &**text == "captured definition"));
}

#[test]
fn symbol_definition_file_context_and_relocation_survive_a_later_caller() {
    let mut session = Session::new();
    let definition = context("definitions/defs.msync");
    pipeline::compile_relocatable_named(
        &mut session,
        "symbol asset():\n    return $file::parent / \"asset\"\n",
        "definitions/defs.msync",
        &definition,
        &PlanningContext::default(),
    )
    .unwrap();
    let caller = context("caller/use.msync");
    let lowered = pipeline::compile_relocatable_named(
        &mut session,
        "asset() -> output\n",
        "caller/use.msync",
        &caller,
        &PlanningContext::default(),
    )
    .unwrap();
    let module_file = definition.source_file.as_ref().unwrap();
    assert!(lowered.relocations.iter().any(|relocation| matches!(&relocation.path.anchor, msync::relocatable::Anchor::ModuleDirectory { module } if module == &module_file.to_string_lossy())));
    let bound = msync::context::contextualize_source(
        &lowered,
        &msync::context::Context::new(&caller),
        PlanningContext::default(),
    )
    .unwrap();
    assert!(
        matches!(input(&bound.program, 0), Input::File { location, .. } if std::path::Path::new(&location.path) == module_file.parent().unwrap().join("asset"))
    );
    let object = lowered.into_object().unwrap();
    let standalone = msync::context::contextualize(
        &object,
        &msync::context::Context::new(&caller),
        &mut Session::new(),
    )
    .unwrap();
    assert!(
        matches!(input(&standalone.program, 0), Input::File { location, .. } if std::path::Path::new(&location.path) == module_file.parent().unwrap().join("asset"))
    );
}

#[test]
fn namespace_capabilities_follow_call_context_while_global_items_stay_captured() {
    let mut session = Session::new();
    let definition = context("definitions.msync");
    pipeline::compile_named(
        &mut session,
        "space => $pwd\nsymbol cwd():\n    global space\n    return space::pwd\n",
        "definitions.msync",
        &definition,
        &PlanningContext::default(),
    )
    .unwrap();
    let mut caller = context("call.msync");
    caller.pwd = caller.pwd.join("different");
    let result = pipeline::compile_named(
        &mut session,
        "cwd() -> output\n",
        "call.msync",
        &caller,
        &PlanningContext::default(),
    )
    .unwrap();
    assert!(
        matches!(input(&result.program, 0), Input::File { location, .. } if std::path::Path::new(&location.path) == caller.pwd)
    );
}

#[test]
fn computed_member_values_select_existing_namespace_members_without_text_reparse() {
    let program = compile(
        "symbol identity(value):\n    return value\nparent := ~/unrelated\nidentity($file)::identity(\"parent\") / \"asset\" -> output\nidentity($pwd)::identity(\"pwd\") / \"asset\" -> cwd-output\n",
    );
    assert!(
        matches!(input(&program, 0), Input::File { location, .. } if std::path::Path::new(&location.path) == context("code.msync").source_file.unwrap().parent().unwrap().join("asset"))
    );
    assert!(
        matches!(input(&program, 1), Input::File { location, .. } if std::path::Path::new(&location.path) == context("code.msync").pwd.join("asset"))
    );
}

#[test]
fn argument_resolution_errors_stay_in_the_callers_owned_source() {
    let mut session = Session::new();
    pipeline::compile_named(
        &mut session,
        "symbol identity(value):\n    return value\n",
        "defs.msync",
        &context("defs.msync"),
        &PlanningContext::default(),
    )
    .unwrap();
    let source = "# caller has a distinct byte layout 可莉\nidentity(base / $pwd) -> output\n";
    let errors = pipeline::compile_named(
        &mut session,
        source,
        "caller.msync",
        &context("caller.msync"),
        &PlanningContext::default(),
    )
    .unwrap_err();
    let error = &errors[0];
    assert_eq!(&*error.source.name, "caller.msync");
    assert_eq!(
        &source[error.report.diagnostic.span.start..error.report.diagnostic.span.end],
        "$pwd"
    );
    let output = error.render(false);
    assert!(
        output.contains("while instantiating symbol 'identity'"),
        "{output}"
    );
    assert!(output.contains("defs.msync:1:"), "{output}");
}

#[test]
fn returned_program_spaces_are_aliases_not_additional_launches_or_joins() {
    let program = compile(
        "symbol identity(value):\n    return value\nrelease original => $path::tool()\nalias => identity(original)\nalias::stdout -> output\nacquire original\n",
    );
    assert_eq!(
        program
            .body
            .instructions
            .iter()
            .filter(|node| matches!(node.kind, InstructionKind::Spawn { .. }))
            .count(),
        1
    );
    assert_eq!(
        program
            .body
            .instructions
            .iter()
            .filter(|node| matches!(node.kind, InstructionKind::Await { .. }))
            .count(),
        1
    );
}

#[test]
fn export_only_symbol_items_keep_foreign_definition_modules_without_operations() {
    let mut compiler = Session::new();
    let definition = context("definitions/defs.msync");
    pipeline::compile_named(
        &mut compiler,
        "symbol asset():\n    return $file::parent / \"asset\"\n",
        "defs.msync",
        &definition,
        &PlanningContext::default(),
    )
    .unwrap();
    let caller = context("caller/code.msync");
    let lowered = pipeline::compile_relocatable_named(
        &mut compiler,
        "saved := asset()\n",
        "code.msync",
        &caller,
        &PlanningContext::default(),
    )
    .unwrap();
    assert!(lowered.compilation.program.body.instructions.is_empty());
    let object = lowered.into_object().unwrap();
    let mut linked = Session::new();
    msync::context::contextualize(
        &object,
        &msync::context::Context::new(&context("later/object.mobj")),
        &mut linked,
    )
    .unwrap();
    let program = linked
        .compile(
            &language::parse("saved -> output\n").unwrap(),
            &context("later/use.msync"),
        )
        .unwrap();
    assert!(
        matches!(input(&program, 0), Input::File { location, .. } if std::path::Path::new(&location.path) == definition.source_file.unwrap().parent().unwrap().join("asset"))
    );
}

#[test]
fn symbol_path_doubling_stops_before_allocating_oversized_join() {
    let mut source = "symbol grow(value):\n    return value / value\nseed := x\n".to_owned();
    // Each input remains below the bound; the final projected join exceeds it.
    for _ in 0..23 {
        source.push_str("seed := grow(seed)\n");
    }
    let errors = Session::new()
        .compile(&language::parse(&source).unwrap(), &context("size.msync"))
        .unwrap_err();
    assert!(
        errors[0]
            .diagnostic
            .message
            .contains("symbol value size limit")
    );
}

#[test]
fn symbol_string_limit_does_not_restrict_ordinary_payloads() {
    let payload = "x".repeat(8 * 1024 * 1024 + 1);
    let literal = format!("\"{payload}\" -> output\n");
    let program = compile(&literal);
    assert!(matches!(input(&program, 0), Input::Literal(text) if text.len() == payload.len()));
    let call =
        format!("symbol identity(value):\n    return value\nidentity(\"{payload}\") -> output\n");
    let errors = Session::new()
        .compile(
            &language::parse(&call).unwrap(),
            &context("string-size.msync"),
        )
        .unwrap_err();
    assert!(
        errors[0]
            .diagnostic
            .message
            .contains("symbol value size limit")
    );
}
