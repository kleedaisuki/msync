use std::fs;
use std::path::Path;
use std::process::{Command, Output};

use msync::compiler::{CompileContext, structured::Session};
use msync::hir::{Input, InstructionKind};
use msync::pipeline::{self, RelocatableCompilation};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("scripts-052-")
        .tempdir_in(root)
        .unwrap()
}

fn compile(
    root: &Path,
    source: &str,
    session: &mut Session,
) -> Result<RelocatableCompilation, Vec<msync::source::LocatedReport>> {
    let file = root.join("entry.msync");
    pipeline::compile_relocatable_named(
        session,
        source,
        file.to_str().unwrap(),
        &CompileContext {
            pwd: root.to_owned(),
            source_file: Some(file.clone()),
        },
        &Default::default(),
    )
}

fn invoke(root: &Path, args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root)
        .args(args)
        .output()
        .unwrap()
}

fn success(output: Output) -> Output {
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    output
}

#[test]
fn include_shares_values_templates_and_current_unit_exports() {
    let root = workspace();
    fs::write(root.path().join("piece.msync"), "payload := \"included\"\nmacro emit(destination):\n    global payload\n    payload -> destination\n").unwrap();
    let mut session = Session::new();
    let compiled = compile(
        root.path(),
        "include \"piece.msync\"\nemit(output)\n",
        &mut session,
    )
    .unwrap();
    assert!(
        session
            .bindings()
            .iter()
            .any(|binding| binding.starts_with("payload :="))
    );
    assert!(
        compiled
            .exports
            .iter()
            .any(|export| export.name == "payload")
    );
    assert_eq!(compiled.compilation.program.body.instructions.len(), 1);
    assert!(
        matches!(&compiled.compilation.program.body.instructions[0].kind, InstructionKind::Transfer { source: Input::Literal(text), .. } if &**text == "included")
    );
}

#[test]
fn execute_isolates_each_lexical_environment_but_preserves_caller_values() {
    let root = workspace();
    fs::write(
        root.path().join("child.msync"),
        "payload -> child-output\nprivate := \"child\"\n",
    )
    .unwrap();
    let mut session = Session::new();
    let compiled = compile(
        root.path(),
        "payload := \"parent\"\nexecute \"child.msync\"\npayload -> parent-output\n",
        &mut session,
    )
    .unwrap();
    assert!(
        !session
            .bindings()
            .iter()
            .any(|binding| binding.starts_with("private :="))
    );
    let nodes = &compiled.compilation.program.body.instructions;
    assert!(matches!(
        &nodes[0].kind,
        InstructionKind::Transfer {
            source: Input::File { .. },
            ..
        }
    ));
    assert!(
        matches!(&nodes[1].kind, InstructionKind::Transfer { source: Input::Literal(text), .. } if &**text == "parent")
    );
    assert_eq!(compiled.compilation.program.blocks().count(), 1);
}

#[test]
fn selectors_are_full_name_sorted_and_do_not_act_as_include_once() {
    let root = workspace();
    fs::create_dir(root.path().join("parts")).unwrap();
    fs::write(root.path().join("parts/b.msync"), "\"B\" -> output\n").unwrap();
    fs::write(root.path().join("parts/a.msync"), "\"A\" -> output\n").unwrap();
    let compiled = compile(root.path(), "execute parts/<a|a\\.msync|b\\.msync>\ninclude \"parts/a.msync\"\ninclude \"parts/a.msync\"\n", &mut Session::new()).unwrap();
    let values = compiled
        .compilation
        .program
        .body
        .instructions
        .iter()
        .map(|node| {
            let InstructionKind::Transfer {
                source: Input::Literal(text),
                ..
            } = &node.kind
            else {
                panic!()
            };
            text.to_string()
        })
        .collect::<Vec<_>>();
    assert_eq!(values, ["A", "B", "A", "A"]);
    assert_eq!(compiled.compilation.program.blocks().count(), 1);
}

#[test]
fn nested_sources_are_relative_to_containing_file_and_keep_file_context() {
    let root = workspace();
    fs::create_dir_all(root.path().join("scripts/sub")).unwrap();
    fs::write(
        root.path().join("scripts/top.msync"),
        "include \"sub/child.msync\"\n",
    )
    .unwrap();
    fs::write(
        root.path().join("scripts/sub/child.msync"),
        "\"file-context\" -> $file::parent/result\n\"caller-context\" -> cwd-result\n",
    )
    .unwrap();
    fs::write(
        root.path().join("entry.msync"),
        "include \"scripts/top.msync\"\n",
    )
    .unwrap();
    success(invoke(root.path(), &["run", "entry.msync"]));
    assert_eq!(
        fs::read(root.path().join("scripts/sub/result")).unwrap(),
        b"file-context"
    );
    assert_eq!(
        fs::read(root.path().join("cwd-result")).unwrap(),
        b"caller-context"
    );
    assert!(!root.path().join("scripts/sub/cwd-result").exists());
}

#[test]
fn cyclic_sources_fail_before_effects_and_do_not_commit_lexical_bindings() {
    let root = workspace();
    let source =
        "new_value := \"must not commit\"\n\"must not write\" -> output\ninclude \"other.msync\"\n";
    fs::write(root.path().join("entry.msync"), source).unwrap();
    fs::write(root.path().join("other.msync"), "include \"entry.msync\"\n").unwrap();
    let mut session = Session::new();
    let errors = compile(root.path(), source, &mut session).unwrap_err();
    assert!(errors[0].render(false).contains("cyclic source expansion"));
    assert!(session.bindings().is_empty());
    assert!(
        !invoke(root.path(), &["run", "entry.msync"])
            .status
            .success()
    );
    assert!(!root.path().join("output").exists());
}

#[test]
fn expanded_objects_run_after_all_source_files_are_removed() {
    let root = workspace();
    fs::write(
        root.path().join("child.msync"),
        "\"inlined code\" -> output\n",
    )
    .unwrap();
    fs::write(root.path().join("entry.msync"), "execute \"child.msync\"\n").unwrap();
    success(invoke(
        root.path(),
        &["compile", "entry.msync", "-o", "compiled.mobj"],
    ));
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    fs::remove_file(root.path().join("child.msync")).unwrap();
    success(invoke(root.path(), &["run", "compiled.mobj"]));
    assert_eq!(
        fs::read(root.path().join("output")).unwrap(),
        b"inlined code"
    );
}

#[test]
fn source_parse_errors_keep_child_and_directive_locations() {
    let root = workspace();
    fs::write(root.path().join("bad.msync"), "broken expression\n").unwrap();
    let errors = compile(root.path(), "include \"bad.msync\"\n", &mut Session::new()).unwrap_err();
    let output = errors[0].render(false);
    assert!(output.contains("bad.msync:1:"), "{output}");
    assert!(output.contains("entry.msync:1:"), "{output}");
    assert_eq!(output.matches("error:").count(), 1);
}

#[test]
fn expanded_file_context_survives_source_free_object_execution() {
    let root = workspace();
    fs::create_dir(root.path().join("sub")).unwrap();
    fs::write(
        root.path().join("sub/child.msync"),
        "\"owned file context\" -> $file::parent/result\n",
    )
    .unwrap();
    fs::write(
        root.path().join("entry.msync"),
        "include \"sub/child.msync\"\n",
    )
    .unwrap();
    success(invoke(
        root.path(),
        &["compile", "entry.msync", "-o", "compiled.mobj"],
    ));
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    fs::remove_file(root.path().join("sub/child.msync")).unwrap();
    success(invoke(root.path(), &["run", "compiled.mobj"]));
    assert_eq!(
        fs::read(root.path().join("sub/result")).unwrap(),
        b"owned file context"
    );
}

#[test]
fn module_paths_relocate_without_reopening_included_sources() {
    let root = workspace();
    fs::create_dir_all(root.path().join("old/sub")).unwrap();
    fs::create_dir_all(root.path().join("new/sub")).unwrap();
    fs::write(
        root.path().join("old/entry.msync"),
        "include \"sub/child.msync\"\n",
    )
    .unwrap();
    fs::write(
        root.path().join("old/sub/child.msync"),
        "\"relocated\" -> $file::parent/result\n",
    )
    .unwrap();
    success(invoke(
        root.path(),
        &["compile", "old/entry.msync", "-o", "new/moved.mobj"],
    ));
    fs::remove_file(root.path().join("old/entry.msync")).unwrap();
    fs::remove_file(root.path().join("old/sub/child.msync")).unwrap();
    success(invoke(root.path(), &["run", "new/moved.mobj"]));
    assert_eq!(
        fs::read(root.path().join("new/sub/result")).unwrap(),
        b"relocated"
    );
    assert!(!root.path().join("old/sub/result").exists());
}

#[test]
fn source_module_metadata_is_versioned_validated_and_caller_overridable() {
    use msync::object::{SOURCE_MODULES_FEATURE, SourceModulePath};
    use msync::relocatable::{Anchor, Path as RelocPath};
    let root = workspace();
    fs::create_dir(root.path().join("sub")).unwrap();
    fs::write(
        root.path().join("sub/child.msync"),
        "\"x\" -> $file::parent/result\n",
    )
    .unwrap();
    let module = compile(
        root.path(),
        "include \"sub/child.msync\"\n",
        &mut Session::new(),
    )
    .unwrap()
    .into_object()
    .unwrap();
    assert_eq!(module.source_modules.len(), 1);
    assert!(
        module
            .features
            .iter()
            .any(|feature| feature == SOURCE_MODULES_FEATURE)
    );
    let mut missing_flag = module.clone();
    missing_flag
        .features
        .retain(|feature| feature != SOURCE_MODULES_FEATURE);
    assert!(missing_flag.validate().is_err());
    let mut cyclic = module.clone();
    cyclic.source_modules[0].path = SourceModulePath::Contextual {
        path: RelocPath::new(
            Anchor::ModuleDirectory {
                module: cyclic.source_modules[0].name.clone(),
            },
            "child.msync",
        ),
    };
    assert!(cyclic.validate().is_err());
    let mut context = msync::context::Context::new(&CompileContext {
        pwd: root.path().to_owned(),
        source_file: Some(root.path().join("entry.msync")),
    });
    context.modules.insert(
        module.source_modules[0].name.clone(),
        root.path().join("override/child.msync"),
    );
    let linked = msync::context::contextualize(&module, &context, &mut Session::new()).unwrap();
    let InstructionKind::Transfer {
        destination: msync::hir::Output::File { location, .. },
        ..
    } = &linked.program.body.instructions[0].kind
    else {
        panic!()
    };
    assert_eq!(
        Path::new(&location.path),
        root.path().join("override/result")
    );
}

#[test]
fn script_expansion_does_not_insert_scheduling_fences_or_program_joins() {
    let root = workspace();
    let child = "release p => $path::tool()\np::stdout -> output\n";
    fs::write(root.path().join("child.msync"), child).unwrap();
    let expanded = compile(
        root.path(),
        "execute \"child.msync\"\nacquire\n",
        &mut Session::new(),
    )
    .unwrap();
    let direct = compile(
        root.path(),
        &format!("{child}acquire\n"),
        &mut Session::new(),
    )
    .unwrap();
    let shape = |compilation: &RelocatableCompilation| {
        compilation
            .compilation
            .program
            .body
            .instructions
            .iter()
            .map(|node| format!("{:?}", node.kind))
            .collect::<Vec<_>>()
    };
    assert_eq!(shape(&expanded), shape(&direct));
    assert_eq!(
        expanded.compilation.program.blocks().count(),
        direct.compilation.program.blocks().count()
    );
}

#[test]
fn selector_execute_environments_are_independent_while_include_is_shared() {
    let root = workspace();
    fs::create_dir(root.path().join("parts")).unwrap();
    fs::write(root.path().join("parts/a.msync"), "private := \"first\"\n").unwrap();
    fs::write(root.path().join("parts/b.msync"), "private -> output\n").unwrap();
    let execute = compile(
        root.path(),
        "execute parts/<.+\\.msync>\n",
        &mut Session::new(),
    )
    .unwrap();
    assert!(matches!(
        &execute.compilation.program.body.instructions[0].kind,
        InstructionKind::Transfer {
            source: Input::File { .. },
            ..
        }
    ));
    let include = compile(
        root.path(),
        "include parts/<.+\\.msync>\n",
        &mut Session::new(),
    )
    .unwrap();
    assert!(
        matches!(&include.compilation.program.body.instructions[0].kind, InstructionKind::Transfer { source: Input::Literal(text), .. } if &**text == "first")
    );
}

#[test]
fn a_cached_macro_definition_is_not_an_active_source_file_cycle() {
    let root = workspace();
    let definitions = "macro load():\n    execute $file::this\n\"from definition script\" -> $file::parent/result\n";
    let file = root.path().join("definitions.msync");
    fs::write(&file, definitions).unwrap();
    let mut session = Session::new();
    pipeline::compile_relocatable_named(
        &mut session,
        "macro load():\n    execute $file::this\n",
        file.to_str().unwrap(),
        &CompileContext {
            pwd: root.path().to_owned(),
            source_file: Some(file.clone()),
        },
        &Default::default(),
    )
    .unwrap();
    let compiled = compile(root.path(), "load()\n", &mut session).unwrap();
    compiled.into_object().unwrap();
}

#[test]
fn nested_source_manifest_dependencies_survive_object_relocation() {
    let root = workspace();
    fs::create_dir_all(root.path().join("old/scripts/sub")).unwrap();
    fs::create_dir_all(root.path().join("new/scripts/sub")).unwrap();
    fs::write(
        root.path().join("old/entry.msync"),
        "include \"scripts/top.msync\"\n",
    )
    .unwrap();
    fs::write(
        root.path().join("old/scripts/top.msync"),
        "include \"sub/child.msync\"\n",
    )
    .unwrap();
    fs::write(
        root.path().join("old/scripts/sub/child.msync"),
        "\"nested relocated\" -> $file::parent/result\n",
    )
    .unwrap();
    success(invoke(
        root.path(),
        &["compile", "old/entry.msync", "-o", "new/moved.mobj"],
    ));
    fs::remove_file(root.path().join("old/entry.msync")).unwrap();
    fs::remove_file(root.path().join("old/scripts/top.msync")).unwrap();
    fs::remove_file(root.path().join("old/scripts/sub/child.msync")).unwrap();
    success(invoke(root.path(), &["run", "new/moved.mobj"]));
    assert_eq!(
        fs::read(root.path().join("new/scripts/sub/result")).unwrap(),
        b"nested relocated"
    );
}

#[cfg(unix)]
#[test]
fn compiler_source_reads_reject_fifos_without_waiting_for_a_peer() {
    use std::ffi::CString;
    let root = workspace();
    let fifo = root.path().join("source.pipe");
    let name = CString::new(fifo.as_os_str().as_encoded_bytes()).unwrap();
    // SAFETY: the owned CString is NUL-terminated for the synchronous call.
    assert_eq!(unsafe { libc::mkfifo(name.as_ptr(), 0o600) }, 0);
    fs::write(root.path().join("entry.msync"), "include \"source.pipe\"\n").unwrap();
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root.path())
        .args(["compile", "entry.msync", "-o", "entry.mobj"])
        .stderr(std::process::Stdio::piped())
        .spawn()
        .unwrap();
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(2);
    loop {
        if child.try_wait().unwrap().is_some() {
            break;
        }
        if std::time::Instant::now() >= deadline {
            child.kill().unwrap();
            child.wait().unwrap();
            panic!("compiler blocked opening a source FIFO");
        }
        std::thread::sleep(std::time::Duration::from_millis(10));
    }
    let output = child.wait_with_output().unwrap();
    assert!(!output.status.success());
    assert!(String::from_utf8_lossy(&output.stderr).contains("finite ordinary file"));
}

#[test]
fn repeated_relative_and_absolute_source_preserves_one_relocatable_identity() {
    use msync::object::SourceModulePath;
    for absolute_first in [false, true] {
        let root = workspace();
        let old = root.path().join("old");
        let moved = root.path().join("moved");
        fs::create_dir(&old).unwrap();
        fs::create_dir(&moved).unwrap();
        let piece = old.join("piece.msync");
        fs::write(&piece, "location := $file::parent\n").unwrap();
        let relative = "include \"piece.msync\"\n";
        let absolute = format!(
            "include \"{}\"\n",
            piece.to_string_lossy().replace('\\', "/")
        );
        let directives = if absolute_first {
            format!("{absolute}{relative}")
        } else {
            format!("{relative}{absolute}")
        };
        let source = format!("{directives}\"relocated\" -> location/result\n");
        let module = compile(&old, &source, &mut Session::new())
            .unwrap()
            .into_object()
            .unwrap();
        assert_eq!(module.source_modules.len(), 1);
        assert!(matches!(
            module.source_modules[0].path,
            SourceModulePath::Contextual { .. }
        ));
        module.validate().unwrap();
        fs::remove_file(piece).unwrap();
        fs::write(moved.join("replay.mobj"), module.encode().unwrap()).unwrap();
        success(invoke(root.path(), &["run", "moved/replay.mobj"]));
        assert_eq!(fs::read(moved.join("result")).unwrap(), b"relocated");
        assert!(!old.join("result").exists());
        let context = msync::context::Context::new(&CompileContext {
            pwd: moved.clone(),
            source_file: Some(moved.join("entry.msync")),
        });
        let linked = msync::context::contextualize(&module, &context, &mut Session::new()).unwrap();
        let InstructionKind::Transfer {
            destination: msync::hir::Output::File { location, .. },
            ..
        } = &linked.program.body.instructions[0].kind
        else {
            panic!("expected transfer");
        };
        assert_eq!(Path::new(&location.path), moved.join("result"));
    }
}

#[test]
fn conflicting_contextual_source_bindings_report_both_directives() {
    let root = workspace();
    fs::write(
        root.path().join("piece.msync"),
        "location := $file::parent\n",
    )
    .unwrap();
    let source = "include \"piece.msync\"\ninclude $pwd::pwd/piece.msync\n";
    let errors = compile(root.path(), source, &mut Session::new()).unwrap_err();
    assert!(
        errors[0]
            .report
            .diagnostic
            .message
            .contains("conflicting source module bindings")
    );
    let rendered = errors[0].render(false);
    assert!(rendered.contains("previous source anchor"), "{rendered}");
    assert!(rendered.contains("conflicting source anchor"), "{rendered}");
}
