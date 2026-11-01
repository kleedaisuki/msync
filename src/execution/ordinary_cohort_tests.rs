use super::*;

fn workspace() -> tempfile::TempDir {
    let scratch = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&scratch).unwrap();
    tempfile::Builder::new()
        .prefix("ordinary-cohort-")
        .tempdir_in(scratch)
        .unwrap()
}

fn transfer(source: Input, destination: &Path, mode: DestinationMode) -> Instruction {
    Instruction {
        id: InstructionId(1),
        span: Span::default(),
        expansion: Vec::new(),
        kind: InstructionKind::Transfer {
            source,
            destination: Output::File {
                location: Location {
                    backend: Backend::Local,
                    path: destination.to_string_lossy().into_owned(),
                },
                mode,
            },
        },
    }
}

fn file(path: &Path) -> Input {
    Input::File {
        location: Location {
            backend: Backend::Local,
            path: path.to_string_lossy().into_owned(),
        },
        pattern: None,
    }
}

fn completed(owner: &mut Ordinary, nodes: &[Instruction]) -> ExecutionReport {
    match owner.execute_cohort(nodes).unwrap() {
        Attempt::Completed(report) => {
            assert!(report.cleanup.is_empty());
            report
        }
        Attempt::Unsupported => panic!("finite cohort must use ordinary publication"),
    }
}

#[test]
fn container_merges_replace_files_and_repeating_the_same_cohort_is_idempotent() {
    let root = workspace();
    let destination = root.path().join("destination");
    fs::create_dir(&destination).unwrap();
    fs::write(root.path().join("a"), b"new a").unwrap();
    fs::write(root.path().join("b"), b"new b").unwrap();
    fs::write(destination.join("b"), b"old b").unwrap();
    let nodes = ["a", "b"].map(|name| {
        transfer(
            file(&root.path().join(name)),
            &destination,
            DestinationMode::Container,
        )
    });
    let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
    for _ in 0..2 {
        let report = completed(&mut owner, &nodes);
        assert_eq!(report.assigned_files, 2);
        assert_eq!(fs::read(destination.join("a")).unwrap(), b"new a");
        assert_eq!(fs::read(destination.join("b")).unwrap(), b"new b");
        assert!(owner.storage.is_none());
    }
    assert!(owner.close().is_empty());
}

#[test]
fn missing_parent_and_all_members_publish_as_one_statement() {
    let root = workspace();
    for name in ["a", "b"] {
        fs::write(root.path().join(name), name.as_bytes()).unwrap();
    }
    let destination = root.path().join("deep/container");
    let nodes = ["a", "b"].map(|name| {
        transfer(
            file(&root.path().join(name)),
            &destination,
            DestinationMode::Container,
        )
    });
    let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
    let report = completed(&mut owner, &nodes);
    assert_eq!(report.assignments, 1);
    assert_eq!(report.assigned_files, 2);
    assert_eq!(report.published_roots, 1);
    assert_eq!(fs::read(destination.join("a")).unwrap(), b"a");
    assert_eq!(fs::read(destination.join("b")).unwrap(), b"b");
    assert!(owner.close().is_empty());
}

#[test]
fn empty_and_unsupported_cohorts_never_activate_storage() {
    let root = workspace();
    let scratch = root.path().join("scratch");
    fs::write(&scratch, b"ordinary user file").unwrap();
    let mut owner = Ordinary::new(&Options::new(&scratch));
    assert_eq!(completed(&mut owner, &[]).assignments, 0);
    let destination = root.path().join("out");
    let nodes = [
        transfer(
            Input::Literal("first".into()),
            &destination,
            DestinationMode::Exact,
        ),
        transfer(
            Input::Port {
                process: crate::hir::ProcessId(0),
                port: crate::hir::ReadPort::Stdout,
            },
            &destination,
            DestinationMode::Exact,
        ),
    ];
    assert!(matches!(
        owner.execute_cohort(&nodes).unwrap(),
        Attempt::Unsupported
    ));
    assert!(owner.storage.is_none());
    assert!(!destination.exists());
    assert_eq!(fs::read(&scratch).unwrap(), b"ordinary user file");
}

#[test]
fn exact_cohort_replacement_keeps_order_without_duplicate_publications() {
    let root = workspace();
    let destination = root.path().join("out");
    let nodes = ["first", "last"].map(|text| {
        transfer(
            Input::Literal(text.into()),
            &destination,
            DestinationMode::Exact,
        )
    });
    let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
    let report = completed(&mut owner, &nodes);
    assert_eq!(report.assignments, 1);
    assert_eq!(report.published_roots, 1);
    assert_eq!(fs::read(destination).unwrap(), b"last");
}

#[test]
fn repeated_container_names_merge_in_selected_order() {
    let root = workspace();
    for (parent, name) in [("first", "a"), ("second", "b")] {
        let directory = root.path().join(parent).join("same");
        fs::create_dir_all(&directory).unwrap();
        fs::write(directory.join(name), name.as_bytes()).unwrap();
    }
    let destination = root.path().join("out");
    let nodes = ["first/same", "second/same"].map(|name| {
        transfer(
            file(&root.path().join(name)),
            &destination,
            DestinationMode::Container,
        )
    });
    let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
    let report = completed(&mut owner, &nodes);
    assert_eq!(report.published_roots, 1);
    assert_eq!(fs::read(destination.join("same/a")).unwrap(), b"a");
    assert_eq!(fs::read(destination.join("same/b")).unwrap(), b"b");
}

#[test]
fn merging_a_directory_preserves_unrelated_destination_inodes_and_avoids_scratch() {
    let root = workspace();
    let source = root.path().join("source/same");
    fs::create_dir_all(&source).unwrap();
    fs::write(source.join("data"), b"new").unwrap();
    let destination = root.path().join("destination");
    fs::create_dir_all(destination.join("same")).unwrap();
    fs::write(destination.join("same/data"), b"old").unwrap();
    fs::write(destination.join("same/retained"), b"unrelated bytes").unwrap();
    let directory = same_file::Handle::from_path(destination.join("same")).unwrap();
    let retained = same_file::Handle::from_path(destination.join("same/retained")).unwrap();
    let nodes = [transfer(
        file(&source),
        &destination,
        DestinationMode::Container,
    )];
    let scratch = root.path().join("not-needed");
    let mut owner = Ordinary::new(&Options::new(&scratch));
    for _ in 0..2 {
        completed(&mut owner, &nodes);
        assert_eq!(fs::read(destination.join("same/data")).unwrap(), b"new");
        assert_eq!(
            fs::read(destination.join("same/retained")).unwrap(),
            b"unrelated bytes"
        );
        assert_eq!(
            same_file::Handle::from_path(destination.join("same")).unwrap(),
            directory
        );
        assert_eq!(
            same_file::Handle::from_path(destination.join("same/retained")).unwrap(),
            retained
        );
        assert!(!scratch.exists());
    }
}
