use super::*;
use crate::hir::{Instruction, InstructionKind};
use crate::language::DestinationMode;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("deferred-domain-")
        .tempdir_in(root)
        .unwrap()
}

fn location(path: &Path) -> Location {
    Location {
        backend: Backend::Local,
        path: path.to_string_lossy().into_owned(),
    }
}

fn program(source: Input, destination: &Path) -> Program {
    Program {
        body: Region {
            instructions: vec![Instruction {
                id: InstructionId(0),
                span: Span::default(),
                expansion: vec![],
                kind: InstructionKind::Transfer {
                    source,
                    destination: Output::File {
                        location: location(destination),
                        mode: DestinationMode::Exact,
                    },
                },
            }],
        },
        transactions: vec![],
    }
}

#[test]
fn deferred_source_enrolls_directory_without_resolving_future_ancestor() {
    let workspace = workspace();
    fs::write(workspace.path().join("node"), b"host file before producer").unwrap();
    let raw = PathBuf::from(format!("{}/node/../node/x", workspace.path().display()));
    let program = program(
        Input::File {
            location: location(&raw),
            pattern: None,
        },
        &workspace.path().join("out"),
    );
    let unstable = BTreeSet::from([EndpointKey {
        instruction: InstructionId(0),
        role: EndpointRole::Source,
    }]);
    let options = Options::new(workspace.path().join(".temp"));
    assert!(Domain::resolve(&program, &options).is_err());
    let domain = Domain::resolve_deferred(&program, &options, &unstable).unwrap();
    assert_eq!(
        domain.roots,
        vec![fs::canonicalize(workspace.path()).unwrap()]
    );
    assert!(
        matches!(&domain.transfers[&InstructionId(0)].source, Source::Local(path) if path == &raw)
    );
    assert!(!workspace.path().join("out").exists());
    assert!(!options.scratch_root.exists());

    fs::remove_file(workspace.path().join("node")).unwrap();
    fs::create_dir(workspace.path().join("node")).unwrap();
    fs::write(workspace.path().join("node/x"), b"producer bytes").unwrap();
    assert_eq!(
        normalize(&raw, Span::default()).unwrap(),
        fs::canonicalize(workspace.path().join("node/x")).unwrap()
    );
}

#[test]
fn deferred_destination_keeps_raw_parent_walk_until_native_validation() {
    let workspace = workspace();
    fs::write(workspace.path().join("node"), b"host file before producer").unwrap();
    let raw = PathBuf::from(format!("{}/node/../node/out", workspace.path().display()));
    let program = program(Input::Literal("bytes".into()), &raw);
    let unstable = BTreeSet::from([EndpointKey {
        instruction: InstructionId(0),
        role: EndpointRole::Destination,
    }]);
    let options = Options::new(workspace.path().join(".temp"));
    assert!(Domain::resolve(&program, &options).is_err());
    let domain = Domain::resolve_deferred(&program, &options, &unstable).unwrap();
    assert_eq!(domain.transfers[&InstructionId(0)].destination, raw);
    assert_eq!(
        fs::read(workspace.path().join("node")).unwrap(),
        b"host file before producer"
    );
    assert!(!options.scratch_root.exists());
    fs::remove_file(workspace.path().join("node")).unwrap();
    fs::create_dir(workspace.path().join("node")).unwrap();
    assert_eq!(
        normalize(&raw, Span::default()).unwrap(),
        fs::canonicalize(workspace.path().join("node"))
            .unwrap()
            .join("out")
    );
}

#[test]
fn unrelated_invalid_endpoint_is_not_deferred_by_another_operand_marker() {
    let workspace = workspace();
    fs::write(workspace.path().join("node"), b"not a directory").unwrap();
    let program = program(
        Input::Literal("bytes".into()),
        &workspace.path().join("node/out"),
    );
    let unstable = BTreeSet::from([
        EndpointKey {
            instruction: InstructionId(99),
            role: EndpointRole::Destination,
        },
        EndpointKey {
            instruction: InstructionId(0),
            role: EndpointRole::Source,
        },
    ]);
    let options = Options::new(workspace.path().join(".temp"));
    assert!(Domain::resolve_deferred(&program, &options, &unstable).is_err());
    assert!(!options.scratch_root.exists());
    assert_eq!(fs::read_dir(workspace.path()).unwrap().count(), 1);
}

#[test]
fn deferred_enrollment_does_not_fall_back_to_volume_capture() {
    let workspace = workspace();
    let root = workspace.path().ancestors().last().unwrap();
    assert!(deferred_envelope(root, Span::default()).is_err());
}

#[cfg(windows)]
#[test]
fn deferred_verbatim_spelling_preserves_parent_components_with_dsl_separators() {
    let workspace = workspace();
    fs::write(workspace.path().join("node"), b"old file").unwrap();
    let root = fs::canonicalize(workspace.path()).unwrap();
    let raw = PathBuf::from(format!("{}/node/../node/x", root.display()));
    let absolute = raw_absolute(&raw, Span::default()).unwrap();
    assert!(
        absolute
            .components()
            .any(|part| part == Component::ParentDir)
    );
    assert_eq!(deferred_envelope(&absolute, Span::default()).unwrap(), root);
    assert!(normalize(&absolute, Span::default()).is_err());
}

#[cfg(unix)]
#[test]
fn deferred_enrollment_refuses_links_before_the_unknown_suffix() {
    let workspace = workspace();
    let real = workspace.path().join("real");
    fs::create_dir(&real).unwrap();
    let link = workspace.path().join("link");
    std::os::unix::fs::symlink(&real, &link).unwrap();
    assert!(deferred_envelope(&link.join("future/child"), Span::default()).is_err());
}
