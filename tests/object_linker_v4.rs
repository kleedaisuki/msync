//! Lexical context substitution never observes or executes the filesystem.

use std::fs;
use std::path::Path;

use msync::compiler::{CompileContext, structured::Session};
use msync::context::{self, Context, Obligation};
use msync::endpoint::Backend;
use msync::hir::{Input, InstructionKind, Output};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("object-linker-")
        .tempdir_in(root)
        .unwrap()
}

fn module(root: &Path, source: &str) -> msync::object::ObjectModule {
    msync::pipeline::compile_object_named(
        &mut Session::new(),
        source,
        "code.msync",
        &CompileContext {
            pwd: root.to_owned(),
            source_file: Some(root.join("code.msync")),
        },
    )
    .unwrap()
}

fn context(root: &Path) -> Context {
    Context::new(&CompileContext {
        pwd: root.to_owned(),
        source_file: Some(root.join("loaded.object")),
    })
}

fn transfer_paths(node: &msync::hir::Instruction) -> (&str, &str) {
    let InstructionKind::Transfer {
        source: Input::File {
            location: source, ..
        },
        destination: Output::File {
            location: destination,
            ..
        },
    } = &node.kind
    else {
        panic!("expected file transfer")
    };
    (&source.path, &destination.path)
}

#[test]
fn module_context_relocates_cwd_home_and_debug_without_touching_endpoints() {
    let fixture = workspace();
    let old = fixture.path().join("old");
    let new = fixture.path().join("new");
    fs::create_dir(&old).unwrap();
    fs::create_dir(&new).unwrap();
    let object = module(
        &old,
        "macro copy(src, des):\n    src -> des\ncopy($pwd / \"missing\", $pwd / \"output\")\n$local::~/also-missing -> $pwd / \"home-output\"\n",
    );
    let encoded = object.encode().unwrap();
    let object = msync::object::ObjectModule::decode(&encoded).unwrap();
    let mut bindings = context(&new);
    bindings.home = Some(new.join("different-home"));
    let linked = context::contextualize(&object, &bindings, &mut Session::new()).unwrap();
    let (source, destination) = transfer_paths(&linked.program.body.instructions[0]);
    assert_eq!(Path::new(source), new.join("missing"));
    assert_eq!(Path::new(destination), new.join("output"));
    let (source, destination) = transfer_paths(&linked.program.body.instructions[1]);
    assert_eq!(Path::new(source), new.join("different-home/also-missing"));
    assert_eq!(Path::new(destination), new.join("home-output"));
    assert_eq!(&*linked.source.name, "code.msync");
    assert!(
        linked.origins[&linked.program.body.instructions[0].id]
            .definition
            .source
            .text
            .contains("macro copy")
    );
    assert!(!old.join(".temp").exists());
    assert!(!new.join(".temp").exists());
    assert!(!new.join("output").exists());
}

#[test]
fn absolute_constants_under_the_compile_directory_are_not_relocated_by_prefix() {
    let fixture = workspace();
    let constant = fixture
        .path()
        .join("literal-constant")
        .to_string_lossy()
        .replace('\\', "\\\\");
    let object = module(
        fixture.path(),
        &format!("$local::\"{constant}\" -> $pwd / \"output\"\n"),
    );
    let new = fixture.path().join("another");
    fs::create_dir(&new).unwrap();
    let linked = context::contextualize(&object, &context(&new), &mut Session::new()).unwrap();
    let (source, destination) = transfer_paths(&linked.program.body.instructions[0]);
    assert_eq!(Path::new(source), fixture.path().join("literal-constant"));
    assert_eq!(Path::new(destination), new.join("output"));
}

#[test]
fn module_file_is_the_loaded_input_not_the_original_debug_filename() {
    let fixture = workspace();
    let object = module(
        fixture.path(),
        "$file::this -> self-copy\n$file::parent / \"asset\" -> asset-copy\n",
    );
    let moved = fixture.path().join("moved");
    fs::create_dir(&moved).unwrap();
    let bindings = context(&moved);
    let linked = context::contextualize(&object, &bindings, &mut Session::new()).unwrap();
    assert_eq!(
        Path::new(transfer_paths(&linked.program.body.instructions[0]).0),
        moved.join("loaded.object")
    );
    assert_eq!(
        Path::new(transfer_paths(&linked.program.body.instructions[1]).0),
        moved.join("asset")
    );
    let mut missing = bindings;
    missing.module_file = None;
    let error = context::contextualize(&object, &missing, &mut Session::new()).unwrap_err();
    assert!(error.message.contains("module-file symbol"));
    assert!(error.instruction.is_some());
    assert!(error.site.is_some());
    assert!(!error.help.is_empty());
}

#[test]
fn missing_environment_binding_does_not_install_candidate_exports() {
    let fixture = workspace();
    let name = format!(
        "MSYNC_MISSING_{}",
        fixture
            .path()
            .file_name()
            .unwrap()
            .to_string_lossy()
            .replace('-', "_")
    );
    let object = module(
        fixture.path(),
        &format!("new_binding := \"candidate\"\n$env::{name}/file -> output\n"),
    );
    let mut session = Session::new();
    let initial = CompileContext {
        pwd: fixture.path().to_owned(),
        source_file: None,
    };
    session
        .compile(
            &msync::language::parse("existing := \"retained\"\n").unwrap(),
            &initial,
        )
        .unwrap();
    let error =
        context::contextualize(&object, &context(fixture.path()), &mut session).unwrap_err();
    assert!(error.message.contains(&name));
    assert!(
        session
            .bindings()
            .iter()
            .any(|binding| binding.starts_with("existing :="))
    );
    assert!(
        !session
            .bindings()
            .iter()
            .any(|binding| binding.starts_with("new_binding :="))
    );
    let mut bindings = context(fixture.path());
    bindings.environment.insert(
        name,
        fixture
            .path()
            .join("environment-root")
            .to_string_lossy()
            .into_owned(),
    );
    let linked = context::contextualize(&object, &bindings, &mut session).unwrap();
    assert_eq!(
        Path::new(transfer_paths(&linked.program.body.instructions[0]).0),
        fixture.path().join("environment-root/file")
    );
}

#[test]
fn repeated_process_modules_rebase_ids_and_preserve_consumed_port_contracts() {
    let fixture = workspace();
    let object = module(
        fixture.path(),
        "p => $path::not_installed_for_linking(\"exact argv\")\np::stdout -> output\n",
    );
    let ctx = CompileContext {
        pwd: fixture.path().to_owned(),
        source_file: None,
    };
    let mut session = Session::new();
    session
        .compile(
            &msync::language::parse("q => $path::earlier()\n").unwrap(),
            &ctx,
        )
        .unwrap();
    let first = context::contextualize(&object, &context(fixture.path()), &mut session).unwrap();
    let second = context::contextualize(&object, &context(fixture.path()), &mut session).unwrap();
    let process = |program: &msync::hir::Program| {
        program
            .body
            .instructions
            .iter()
            .find_map(|instruction| match instruction.kind {
                InstructionKind::Spawn { process, .. } => Some(process),
                _ => None,
            })
            .expect("expected spawn between its barriers")
    };
    assert_ne!(process(&first.program), process(&second.program));
    for module in [&first, &second] {
        let expected = process(&module.program);
        assert!(module.program.body.instructions.iter().any(|instruction| {
            matches!(instruction.kind, InstructionKind::Await { process: Some(waited) } if waited == expected)
        }));
    }
    assert!(
        second
            .obligations
            .iter()
            .any(|obligation| matches!(obligation, Obligation::SearchPath { name, .. } if name == "not_installed_for_linking"))
    );
    assert!(
        session
            .compile(
                &msync::language::parse("p::stdout -> again\n").unwrap(),
                &ctx
            )
            .is_err()
    );
    assert!(
        session
            .compile(&msync::language::parse("p::pid -> pid\n").unwrap(), &ctx)
            .is_ok()
    );
    assert!(!fixture.path().join("output").exists());
}

#[test]
fn captured_pwd_values_freeze_but_namespace_exports_follow_later_submissions() {
    let fixture = workspace();
    let object = module(
        fixture.path(),
        "frozen := $pwd / \"input\"\ndynamic => $pwd\n",
    );
    let instance = fixture.path().join("instance");
    let later = fixture.path().join("later");
    fs::create_dir(&instance).unwrap();
    fs::create_dir(&later).unwrap();
    let mut session = Session::new();
    context::contextualize(&object, &context(&instance), &mut session).unwrap();
    let program = session
        .compile(
            &msync::language::parse("frozen -> captured\ndynamic / \"input\" -> dynamic-copy\n")
                .unwrap(),
            &CompileContext {
                pwd: later.clone(),
                source_file: None,
            },
        )
        .unwrap();
    assert_eq!(
        Path::new(transfer_paths(&program.body.instructions[0]).0),
        instance.join("input")
    );
    assert_eq!(
        Path::new(transfer_paths(&program.body.instructions[1]).0),
        later.join("input")
    );
}

#[test]
fn relative_wsl_paths_remain_runtime_obligations_without_an_explicit_guest_context() {
    let fixture = workspace();
    let object = module(fixture.path(), "$wsl::relative/file -> output\n");
    let mut bindings = context(fixture.path());
    let deferred = context::contextualize(&object, &bindings, &mut Session::new()).unwrap();
    assert_eq!(
        transfer_paths(&deferred.program.body.instructions[0]).0,
        "relative/file"
    );
    assert!(deferred.obligations.iter().any(|obligation| matches!(
        obligation,
        Obligation::InheritedEndpointCwd {
            backend: Backend::Wsl,
            ..
        }
    )));
    assert!(fs::read_dir(fixture.path()).unwrap().next().is_none());
    bindings
        .endpoint_cwds
        .insert(Backend::Wsl, "/actual/guest/context".into());
    let linked = context::contextualize(&object, &bindings, &mut Session::new()).unwrap();
    assert_eq!(
        transfer_paths(&linked.program.body.instructions[0]).0,
        "/actual/guest/context/relative/file"
    );
    let home = module(fixture.path(), "$wsl::~/config -> output\n");
    let linked =
        context::contextualize(&home, &context(fixture.path()), &mut Session::new()).unwrap();
    assert_eq!(
        transfer_paths(&linked.program.body.instructions[0]).0,
        "~/config"
    );
    assert!(
        linked
            .obligations
            .iter()
            .any(|obligation| matches!(obligation, Obligation::ForeignHome { .. }))
    );
    assert!(fs::read_dir(fixture.path()).unwrap().next().is_none());
}

#[test]
fn contextualization_accepts_future_paths_without_searching_or_starting_a_program() {
    let fixture = workspace();
    let object = module(
        fixture.path(),
        "async p => $path::not_installed_during_contextualization()\n$pwd / \"future-source\" -> $pwd / \"future-output\"\nawait p\n",
    );
    let future = fixture.path().join("directory-created-only-later");
    let instantiated =
        context::contextualize(&object, &context(&future), &mut Session::new()).unwrap();
    assert!(instantiated.obligations.iter().any(|obligation| matches!(
        obligation,
        Obligation::SearchPath { name, .. } if name == "not_installed_during_contextualization"
    )));
    let transfer = instantiated
        .program
        .body
        .instructions
        .iter()
        .find(|instruction| matches!(instruction.kind, InstructionKind::Transfer { .. }))
        .unwrap();
    let (source, destination) = transfer_paths(transfer);
    assert_eq!(Path::new(source), future.join("future-source"));
    assert_eq!(Path::new(destination), future.join("future-output"));
    assert!(!future.exists());
}

#[test]
fn object_optimization_hints_never_cross_an_explicit_directional_barrier() {
    let fixture = workspace();
    let straight = module(fixture.path(), "\"first\" -> output\n\"last\" -> output\n");
    assert_eq!(straight.constraints.len(), 1);
    for source in [
        "\"first\" -> output\nasync \"last\" -> output\n",
        "await \"first\" -> output\n\"last\" -> output\n",
    ] {
        let barrier = module(fixture.path(), source);
        assert!(barrier.constraints.is_empty(), "{source}");
        let encoded = barrier.encode().unwrap();
        assert!(
            msync::object::ObjectModule::decode(&encoded)
                .unwrap()
                .constraints
                .is_empty()
        );
    }
}
