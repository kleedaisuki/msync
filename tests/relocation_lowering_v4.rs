//! Relocations follow typed value provenance, never an absolute-path prefix guess.

use std::path::{Path, PathBuf};

use msync::compiler::{
    CompileContext,
    structured::{Lowered, Session},
};
use msync::hir::{InstructionId, InstructionKind};
use msync::relocatable::{Anchor, ExportValue, Path as RelocPath, Slot};
use msync::source::Source;

fn context(name: &str) -> CompileContext {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/relocation-lowering");
    CompileContext {
        pwd: root.clone(),
        source_file: Some(root.join(name)),
    }
}

fn lower(session: &mut Session, text: &str, context: &CompileContext) -> Lowered {
    session
        .compile_named(
            &msync::language::parse(text).unwrap(),
            context,
            Source::new(
                context
                    .source_file
                    .as_ref()
                    .unwrap()
                    .to_string_lossy()
                    .into_owned(),
                text,
            ),
        )
        .unwrap()
}

fn relocation(lowered: &Lowered, instruction: usize, slot: Slot) -> &RelocPath {
    &lowered
        .relocations
        .iter()
        .find(|entry| entry.instruction == InstructionId(instruction) && entry.slot == slot)
        .unwrap()
        .path
}

#[test]
fn macro_parameters_preserve_home_and_pwd_anchors_without_machine_paths() {
    let compiled = lower(
        &mut Session::new(),
        "macro copy(codex, des):\n    codex/config.toml -> des/config.toml\n    codex/agents -> des/agents/.\ncodex := ~/.codex\ncopy($local::codex, $pwd::local)\ncopy($wsl::codex, $pwd::wsl)\n",
        &context("code.msync"),
    );
    assert_eq!(compiled.program.body.instructions.len(), 4);
    for (index, suffix) in [
        (0, ".codex/config.toml"),
        (1, ".codex/agents"),
        (2, ".codex/config.toml"),
        (3, ".codex/agents"),
    ] {
        assert_eq!(
            relocation(&compiled, index, Slot::Source),
            &RelocPath::new(Anchor::Home, suffix)
        );
    }
    assert_eq!(
        relocation(&compiled, 0, Slot::Destination),
        &RelocPath::new(Anchor::WorkingDirectory, "local/config.toml")
    );
    assert_eq!(
        relocation(&compiled, 3, Slot::Destination),
        &RelocPath::new(Anchor::WorkingDirectory, "wsl/agents/")
    );
}

#[test]
fn an_explicit_absolute_constant_under_compile_pwd_is_not_made_relocatable() {
    let context = context("code.msync");
    let absolute = context
        .pwd
        .join("absolute")
        .to_string_lossy()
        .replace('\\', "\\\\");
    let compiled = lower(
        &mut Session::new(),
        &format!("$local::\"{absolute}\" -> result\n$pwd::pwd / \"relative\" -> second\n"),
        &context,
    );
    assert!(
        !compiled
            .relocations
            .iter()
            .any(|entry| entry.instruction == InstructionId(0) && entry.slot == Slot::Source)
    );
    assert_eq!(
        relocation(&compiled, 1, Slot::Source),
        &RelocPath::new(Anchor::WorkingDirectory, "relative")
    );
}

#[test]
fn captured_module_paths_keep_definition_identity_across_macro_call_sources() {
    let definition = context("definitions.msync");
    let mut caller = context("caller.msync");
    caller.pwd.push("different-caller");
    caller.source_file = Some(caller.pwd.join("caller.msync"));
    let mut compiler = Session::new();
    lower(
        &mut compiler,
        "captured := $file::parent\nmacro emit(des):\n    global captured\n    captured/input -> des\n    $file::parent/direct -> second\n",
        &definition,
    );
    let compiled = lower(&mut compiler, "emit($pwd::pwd / \"out\")\n", &caller);
    let module = definition
        .source_file
        .unwrap()
        .to_string_lossy()
        .into_owned();
    assert!(
        !compiled
            .relocations
            .iter()
            .any(|entry| entry.instruction == InstructionId(0) && entry.slot == Slot::Source)
    );
    let msync::hir::InstructionKind::Transfer {
        source: msync::hir::Input::File { location, .. },
        ..
    } = &compiled.program.body.instructions[0].kind
    else {
        panic!("expected file")
    };
    assert_eq!(Path::new(&location.path), definition.pwd.join("input"));
    assert_eq!(
        relocation(&compiled, 1, Slot::Source),
        &RelocPath::new(Anchor::ModuleDirectory { module }, "direct")
    );
}

#[test]
fn a_prior_unit_capture_does_not_rebind_to_a_later_compile_cwd() {
    let first = context("first.msync");
    let mut second = context("second.msync");
    second.pwd.push("different-cwd");
    second.source_file = Some(second.pwd.join("second.msync"));
    let mut compiler = Session::new();
    lower(
        &mut compiler,
        "frozen := $pwd::pwd / \"input\"\nns => $pwd\n",
        &first,
    );
    let compiled = lower(
        &mut compiler,
        "again := frozen\nagain -> output\nns::pwd / \"input\" -> dynamic\n",
        &second,
    );
    assert!(
        !compiled
            .relocations
            .iter()
            .any(|entry| entry.instruction == InstructionId(0) && entry.slot == Slot::Source)
    );
    let msync::hir::InstructionKind::Transfer {
        source: msync::hir::Input::File { location, .. },
        ..
    } = &compiled.program.body.instructions[0].kind
    else {
        panic!("expected file")
    };
    assert_eq!(Path::new(&location.path), first.pwd.join("input"));
    assert_eq!(
        relocation(&compiled, 1, Slot::Source),
        &RelocPath::new(Anchor::WorkingDirectory, "input")
    );
    let exports = compiler.object_exports(&compiled.program);
    assert!(!exports.iter().any(|export| export.name == "frozen"));
    let frozen = exports
        .into_iter()
        .find(|export| export.name == "again")
        .unwrap();
    assert!(matches!(
        frozen.value,
        ExportValue::Path {
            relocation: None,
            ..
        }
    ));
}

#[test]
fn program_relocations_distinguish_literal_argv_from_paths_and_cwd() {
    let compiled = lower(
        &mut Session::new(),
        "p => $path::python(\"literal argument\", $pwd::pwd / \"run.py\", $env::WORK/input)\np::stdout -> output\n",
        &context("code.msync"),
    );
    let spawn = compiled
        .program
        .body
        .instructions
        .iter()
        .find_map(|node| matches!(node.kind, InstructionKind::Spawn { .. }).then_some(node.id.0))
        .expect("program launch");
    assert_eq!(
        relocation(&compiled, spawn, Slot::Executable),
        &RelocPath::new(
            Anchor::SearchPath {
                name: "python".into()
            },
            ""
        )
    );
    assert_eq!(
        relocation(&compiled, spawn, Slot::Cwd),
        &RelocPath::new(Anchor::WorkingDirectory, "")
    );
    assert!(
        !compiled
            .relocations
            .iter()
            .any(|entry| entry.instruction == InstructionId(spawn)
                && entry.slot == Slot::Argument { index: 0 })
    );
    assert_eq!(
        relocation(&compiled, spawn, Slot::Argument { index: 1 }),
        &RelocPath::new(Anchor::WorkingDirectory, "run.py")
    );
    assert_eq!(
        relocation(&compiled, spawn, Slot::Argument { index: 2 }),
        &RelocPath::new(
            Anchor::Environment {
                name: "WORK".into()
            },
            "input"
        )
    );
}

#[test]
fn selectors_are_not_part_of_a_relocated_directory_path() {
    let compiled = lower(
        &mut Session::new(),
        "$pwd::pwd / \"source\" / <.+\\.toml> -> $pwd::pwd / \"target\"/\n",
        &context("code.msync"),
    );
    assert_eq!(
        relocation(&compiled, 0, Slot::Source),
        &RelocPath::new(Anchor::WorkingDirectory, "source/")
    );
}

#[test]
fn absolute_path_dialects_are_not_classified_by_the_build_host() {
    let compiled = lower(
        &mut Session::new(),
        "$local::\"C:/fixed/input\" -> result\n$local::\"/fixed/input\" -> second\n",
        &context("code.msync"),
    );
    assert!(
        compiled
            .relocations
            .iter()
            .all(|entry| entry.slot != Slot::Source)
    );
    assert!(Path::new("/fixed/input").has_root());
}

#[test]
fn runtime_exports_omit_macro_templates_and_foreign_processes() {
    let context = context("code.msync");
    let mut compiler = Session::new();
    lower(&mut compiler, "old => $path::old_tool()\n", &context);
    let current = lower(
        &mut compiler,
        "macro private_template(x):\n    x -> unused\nvalue := \"data\"\nfresh => $path::fresh_tool()\n\"file\" -> target\n",
        &context,
    );
    let exports = compiler.object_exports(&current.program);
    assert!(
        !exports
            .iter()
            .any(|export| export.name == "old" || export.name == "private_template")
    );
    assert!(exports.iter().any(
        |export| export.name == "fresh" && matches!(export.value, ExportValue::Process { .. })
    ));
    assert!(exports.iter().any(
        |export| export.name == "value" && matches!(export.value, ExportValue::Literal { .. })
    ));
}

#[test]
fn importing_link_bound_values_freezes_paths_but_keeps_namespaces_dynamic() {
    let first = context("code.msync");
    let mut compiler = Session::new();
    let compiled = lower(
        &mut compiler,
        "captured := $pwd::pwd / \"input\"\nns => $pwd\n",
        &first,
    );
    let mut exports = compiler.object_exports(&compiled.program);
    let frozen = first
        .pwd
        .join("linked/input")
        .to_string_lossy()
        .into_owned();
    for export in &mut exports {
        if let ExportValue::Path {
            path, relocation, ..
        } = &mut export.value
        {
            *path = frozen.clone();
            *relocation = None;
        }
    }
    let mut imported = Session::new();
    imported
        .import_object_exports(&exports, &compiler.object_export_sites(), &compiled.program)
        .unwrap();
    let next = CompileContext {
        pwd: first.pwd.join("next"),
        source_file: first.source_file,
    };
    let used = lower(
        &mut imported,
        "captured -> output\nns::pwd / \"dynamic\" -> other\n",
        &next,
    );
    let msync::hir::InstructionKind::Transfer {
        source: msync::hir::Input::File { location, .. },
        ..
    } = &used.program.body.instructions[0].kind
    else {
        panic!("expected file")
    };
    assert_eq!(location.path, frozen);
    assert!(
        !used
            .relocations
            .iter()
            .any(|entry| entry.instruction == InstructionId(0) && entry.slot == Slot::Source)
    );
    assert_eq!(
        relocation(&used, 1, Slot::Source),
        &RelocPath::new(Anchor::WorkingDirectory, "dynamic")
    );
}

#[test]
fn importing_runtime_exports_preserves_linear_ports_and_replayable_scalars() {
    let context = context("code.msync");
    let mut compiler = Session::new();
    let compiled = lower(
        &mut compiler,
        "p => $path::tool()\np::stdout -> output\np::pid -> initial-pid\n",
        &context,
    );
    let mut imported = Session::new();
    imported
        .import_object_exports(
            &compiler.object_exports(&compiled.program),
            &compiler.object_export_sites(),
            &compiled.program,
        )
        .unwrap();
    imported
        .import_object_consumptions(&compiled.program, &compiled.origins)
        .unwrap();
    let rejected = imported
        .compile_named(
            &msync::language::parse("p::stdout -> again\n").unwrap(),
            &context,
            Source::new("next.msync", "p::stdout -> again\n"),
        )
        .unwrap_err();
    assert!(
        rejected[0]
            .report
            .diagnostic
            .message
            .contains("only one owner")
    );
    assert!(!rejected[0].related.is_empty());
    lower(
        &mut imported,
        "p::pid -> next-pid\np::pid -> more-pid\n",
        &context,
    );
}
