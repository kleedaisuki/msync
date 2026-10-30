use std::fs;
use std::path::Path;

use msync::compiler::{Backend, CompileContext, Compiler, Program};
use msync::ir::{Artifact, Contextual};
use msync::{scheduler, syntax};

fn context(root: &Path) -> CompileContext {
    CompileContext {
        pwd: root.to_owned(),
        source_file: Some(root.join("scripts/source.msync")),
    }
}

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("ir-")
        .tempdir_in(root)
        .unwrap()
}

fn signature(program: &Program) -> Vec<(String, String, Option<String>)> {
    program
        .operations
        .iter()
        .map(|op| {
            (
                format!("{}::{}", op.source.backend, op.source.path),
                format!("{}::{}", op.destination.backend, op.destination.path),
                op.pattern.clone(),
            )
        })
        .collect()
}

#[test]
fn relocation_resolves_names_but_binds_namespace_roots_at_reuse() {
    let root = workspace();
    let script = syntax::parse("here => $pwd\nfile => $file\nbase := here::pwd/configs\nbase/<.+\\.toml> -> file::parent/out/\n").unwrap();
    let portable = Compiler::new().compile_relocatable(&script).unwrap();
    let json = Artifact::Relocatable(portable).to_json().unwrap();
    assert!(!json.contains("msync-type-witness"));
    assert!(!json.contains(root.path().to_str().unwrap()));
    assert!(!json.contains("here::"));
    assert!(!json.contains("base/"));
    let artifact = Artifact::from_json(&json).unwrap();
    for subdir in ["first", "second"] {
        let ctx = context(&root.path().join(subdir));
        let contextual = artifact.bind(&ctx).unwrap();
        let direct = Compiler::new().compile_with_context(&script, &ctx).unwrap();
        assert_eq!(signature(contextual.program()), signature(&direct));
        assert_eq!(
            contextual.program().operations[0].source.path,
            format!("{}/configs/", ctx.pwd.display())
        );
    }
}

#[test]
fn artifacts_replay_real_files_and_contextual_paths_do_not_relocate() {
    let root = workspace();
    let first = root.path().join("first");
    let second = root.path().join("second");
    fs::create_dir_all(&first).unwrap();
    fs::create_dir_all(&second).unwrap();
    fs::write(first.join("source"), b"first").unwrap();
    fs::write(second.join("source"), b"second").unwrap();
    let script = syntax::parse("source -> output\n").unwrap();
    let portable = Artifact::Relocatable(Compiler::new().compile_relocatable(&script).unwrap());
    let captured = portable.bind(&context(&first)).unwrap();
    let frozen = Artifact::from_json(&Artifact::Contextual(captured).to_json().unwrap()).unwrap();
    let bound = frozen.bind(&context(&second)).unwrap();
    let plan = scheduler::prepare(bound.program(), true).unwrap();
    scheduler::execute_plan(&plan, &scheduler::ResourceLimits::default()).unwrap();
    assert_eq!(fs::read(first.join("output")).unwrap(), b"first");
    assert!(!second.join("output").exists());
    let bound = portable.bind(&context(&second)).unwrap();
    let plan = scheduler::prepare(bound.program(), true).unwrap();
    scheduler::execute_plan(&plan, &scheduler::ResourceLimits::default()).unwrap();
    assert_eq!(fs::read(second.join("output")).unwrap(), b"second");
    fs::remove_file(second.join("source")).unwrap();
    assert!(
        scheduler::prepare(bound.program(), true).is_err(),
        "reuse must not cache old file-kind proofs"
    );
}

#[test]
fn contextual_capture_freezes_relative_local_paths_and_preserves_container_marker() {
    let root = workspace();
    let script = syntax::parse("input -> output/\n").unwrap();
    let ctx = context(root.path());
    let program = Compiler::new().compile_with_context(&script, &ctx).unwrap();
    let captured = Contextual::capture(&program, &ctx).unwrap();
    assert!(Path::new(&captured.program().operations[0].source.path).is_absolute());
    assert!(
        captured.program().operations[0]
            .destination
            .path
            .ends_with('/')
    );
    assert_eq!(
        captured.program().operations[0].source.backend,
        Backend::Local
    );
}

#[test]
fn source_file_is_required_only_when_binding_a_referenced_file_root() {
    let root = workspace();
    let script = syntax::parse("$file::this -> backup\n").unwrap();
    let portable = Compiler::new().compile_relocatable(&script).unwrap();
    let artifact =
        Artifact::from_json(&Artifact::Relocatable(portable).to_json().unwrap()).unwrap();
    let no_file = CompileContext {
        pwd: root.path().to_owned(),
        source_file: None,
    };
    assert!(
        artifact.bind(&no_file).unwrap_err()[0]
            .diagnostic
            .message
            .contains("source-file context")
    );
}

#[test]
fn imported_artifacts_reject_unknown_versions_invalid_paths_and_read_only_sinks() {
    let script = syntax::parse("input -> output\n").unwrap();
    let json = Artifact::Relocatable(Compiler::new().compile_relocatable(&script).unwrap())
        .to_json()
        .unwrap();
    assert!(Artifact::from_json(&json.replace("\"version\": 1", "\"version\": 99")).is_err());
    assert!(Artifact::from_json(&json.replace("\"output\"", "\"\"")).is_err());
    assert!(
        Artifact::from_json(&json.replace("\"backend\": null", "\"backend\": \"Path\"")).is_err()
    );
    assert!(Artifact::from_json("{}").is_err());
}

#[test]
fn portable_compile_preserves_existing_captured_bindings_and_transactional_environment() {
    let root = workspace();
    let mut compiler = Compiler::new();
    compiler
        .compile_with_context(
            &syntax::parse("captured := $pwd::pwd\n").unwrap(),
            &context(root.path()),
        )
        .unwrap();
    let before = compiler.bindings();
    let script =
        syntax::parse("transient := $pwd::pwd\ncaptured/input -> transient/out\n").unwrap();
    let portable = compiler.compile_relocatable(&script).unwrap();
    assert_eq!(compiler.bindings(), before);
    let later = context(&root.path().join("later"));
    let bound = portable.bind(&later).unwrap();
    assert_eq!(
        bound.program().operations[0].source.path,
        root.path().join("input").to_str().unwrap()
    );
    assert_eq!(
        bound.program().operations[0].destination.path,
        later.pwd.join("out").to_str().unwrap()
    );
    assert!(
        compiler
            .compile_relocatable(&syntax::parse("bad := $file::this/child\n").unwrap())
            .is_err()
    );
    assert_eq!(compiler.bindings(), before);
}

#[test]
fn ipv6_endpoints_round_trip_without_becoming_path_qualifiers() {
    let root = workspace();
    for endpoint in ["ssh://[::1]:2222", "tcp://[::1]:9000"] {
        let path = if endpoint.starts_with("tcp") {
            "/"
        } else {
            "input"
        };
        let script =
            syntax::parse(&format!("remote => {endpoint}\nremote::{path} -> output\n")).unwrap();
        let portable = Artifact::Relocatable(Compiler::new().compile_relocatable(&script).unwrap());
        let artifact = Artifact::from_json(&portable.to_json().unwrap()).unwrap();
        let bound = artifact.bind(&context(root.path())).unwrap();
        let json = Artifact::Contextual(bound).to_json().unwrap();
        Artifact::from_json(&json).unwrap();
    }
}

proptest::proptest! {
    #[test]
    fn symbolic_aliases_match_normal_frontend_after_context_binding(
        selector in proptest::bool::ANY,
        version in 0_usize..32,
        use_file in proptest::bool::ANY,
    ) {
        let root = workspace();
        let ctx = context(&root.path().join(format!("context-{version}")));
        let namespace = if use_file { "$file::parent" } else { "$pwd::pwd" };
        let source = if selector { "renamed/<.+\\.toml>" } else { "renamed/file" };
        let destination = if selector { "destination/" } else { "destination/file" };
        let text = format!("endpoint => $local\nroot := {namespace}\nrenamed := root/subdir\n{source} -> endpoint::{destination}\n");
        let script = syntax::parse(&text).unwrap();
        let portable = Compiler::new().compile_relocatable(&script).unwrap();
        let direct = Compiler::new().compile_with_context(&script, &ctx).unwrap();
        let direct = Contextual::capture(&direct, &ctx).unwrap();
        let rebound = Artifact::from_json(&Artifact::Relocatable(portable).to_json().unwrap()).unwrap().bind(&ctx).unwrap();
        proptest::prop_assert_eq!(signature(direct.program()), signature(rebound.program()));
    }
}

#[test]
fn prepared_snapshot_keeps_unoptimized_transfers_without_cached_kind_proofs() {
    let root = workspace();
    let ctx = context(root.path());
    fs::write(root.path().join("first"), b"first").unwrap();
    fs::write(root.path().join("second"), b"second").unwrap();
    let script = syntax::parse(
        "$pwd::pwd/first -> $pwd::pwd/target\n$pwd::pwd/second -> $pwd::pwd/target\n",
    )
    .unwrap();
    let original = Compiler::new().compile_with_context(&script, &ctx).unwrap();
    let prepared = scheduler::prepare_snapshot_spanned(&original, true, &[]).unwrap();
    assert_eq!(prepared.plan.nodes.len(), 1);
    assert_eq!(prepared.program.operations.len(), 2);
    let frozen = Contextual::capture(&prepared.program, &ctx).unwrap();
    let json = Artifact::Contextual(frozen).to_json().unwrap();
    fs::remove_file(root.path().join("second")).unwrap();
    let frozen = Artifact::from_json(&json).unwrap().bind(&ctx).unwrap();
    assert!(scheduler::prepare(frozen.program(), true).is_err());
}

#[cfg(windows)]
#[test]
fn contextual_capture_resolves_native_drive_relative_paths() {
    let ctx = CompileContext::capture(None).unwrap();
    let drive = ctx.pwd.to_str().unwrap().chars().next().unwrap();
    let script = syntax::parse(&format!("{drive}:input -> {drive}:output\n")).unwrap();
    let program = Compiler::new().compile_with_context(&script, &ctx).unwrap();
    let captured = Contextual::capture(&program, &ctx).unwrap();
    for operation in &captured.program().operations {
        assert!(Path::new(&operation.source.path).is_absolute());
        assert!(Path::new(&operation.destination.path).is_absolute());
    }
    Artifact::Contextual(captured).to_json().unwrap();
}
