use super::*;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("ordinary-owner-")
        .tempdir_in(root)
        .unwrap()
}

fn literal(target: PathBuf) -> Instruction {
    Instruction {
        id: InstructionId(1),
        span: Span::default(),
        expansion: Vec::new(),
        kind: InstructionKind::Transfer {
            source: Input::Literal("value".into()),
            destination: Output::File {
                location: Location {
                    backend: Backend::Local,
                    path: target.to_string_lossy().into_owned(),
                },
                mode: DestinationMode::Exact,
            },
        },
    }
}

#[test]
fn cancellation_before_dispatch_creates_no_storage_or_target() {
    let root = workspace();
    let options = Options::new(root.path().join("scratch"));
    options.cancel.cancel();
    let mut owner = Ordinary::new(&options);
    let result = owner.execute(&literal(root.path().join("target")));
    assert!(result.err().unwrap().is_cancelled());
    assert!(owner.storage.is_none());
    assert!(!root.path().join("scratch").exists());
    assert!(!root.path().join("target").exists());
    assert!(owner.close().is_empty());
}

#[test]
fn each_completed_statement_retires_its_captured_payload_before_the_next() {
    let root = workspace();
    let options = Options::new(root.path().join("scratch"));
    let mut owner = Ordinary::new(&options);
    for name in ["first", "second"] {
        let Attempt::Completed(report) = owner.execute(&literal(root.path().join(name))).unwrap()
        else {
            panic!("literal is an ordinary assignment");
        };
        assert!(report.cleanup.is_empty());
        assert!(owner.storage.is_none());
        assert_eq!(
            fs::read_dir(root.path().join("scratch")).unwrap().count(),
            0
        );
        assert_eq!(fs::read(root.path().join(name)).unwrap(), b"value");
    }
    assert!(owner.close().is_empty());
}

#[test]
fn a_later_publication_guard_failure_compensates_the_first_install() {
    let root = workspace();
    fs::write(root.path().join("source"), b"new").unwrap();
    fs::write(root.path().join("first"), b"old-first").unwrap();
    fs::write(root.path().join("second"), b"old-second").unwrap();
    let options = Options::new(root.path().join("scratch"));
    let mut owner = Ordinary::new(&options);
    owner.storage = Some(Storage::new(&options, Span::default()).unwrap());
    {
        let storage = owner.storage.as_ref().unwrap();
        let source = storage.assets.capture(&root.path().join("source")).unwrap();
        let assignments = ["first", "second"].map(|name| Assignment {
            target: root.path().join(name),
            snapshot: source.clone(),
            existed: true,
        });
        let mut parents = Parents::default();
        let mut journals = Vec::new();
        storage
            .prepare(
                &assignments,
                Span::default(),
                &options,
                &mut parents,
                &mut journals,
            )
            .unwrap();
        fs::remove_file(root.path().join("second")).unwrap();
        fs::write(root.path().join("second"), b"foreign").unwrap();
        let mut error = publish_all(&mut journals, &options, Span::default()).unwrap_err();
        compensate(&mut journals, &mut error);
        parents.rollback(Span::default(), &mut error);
        assert_eq!(fs::read(root.path().join("first")).unwrap(), b"old-first");
        assert_eq!(fs::read(root.path().join("second")).unwrap(), b"foreign");
        assert!(error.compensation.is_empty(), "{error}");
        assert!(error.retained_paths.is_empty(), "{error}");
    }
    assert!(owner.close().is_empty());
    assert_eq!(
        fs::read_dir(root.path().join("scratch")).unwrap().count(),
        0
    );
}

#[test]
fn cancellation_after_install_restores_the_before_image_before_cleanup() {
    let root = workspace();
    fs::write(root.path().join("source"), b"new").unwrap();
    fs::write(root.path().join("target"), b"old").unwrap();
    let options = Options::new(root.path().join("scratch"));
    let mut owner = Ordinary::new(&options);
    owner.storage = Some(Storage::new(&options, Span::default()).unwrap());
    {
        let storage = owner.storage.as_ref().unwrap();
        let source = storage.assets.capture(&root.path().join("source")).unwrap();
        let mut journal = storage
            .assets
            .prepare(&source, &root.path().join("target"))
            .unwrap();
        journal.publish().unwrap();
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"new");
        options.cancel.cancel();
        let mut error = ExecutionError::cancelled("cancelled after install", Span::default());
        compensate(std::slice::from_mut(&mut journal), &mut error);
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"old");
        assert!(error.compensation.is_empty());
        assert!(error.retained_paths.is_empty());
    }
    assert!(owner.close().is_empty());
}

#[test]
fn cancelled_selector_publication_removes_shared_created_parents_without_phantom_retention() {
    let root = workspace();
    fs::write(root.path().join("source"), b"new").unwrap();
    let options = Options::new(root.path().join("scratch"));
    let mut owner = Ordinary::new(&options);
    owner.storage = Some(Storage::new(&options, Span::default()).unwrap());
    {
        let storage = owner.storage.as_ref().unwrap();
        let source = storage.assets.capture(&root.path().join("source")).unwrap();
        let assignments = ["a", "b"].map(|name| Assignment {
            target: root.path().join("created/deep").join(name),
            snapshot: source.clone(),
            existed: false,
        });
        let mut parents = Parents::default();
        let mut journals = Vec::new();
        storage
            .prepare(
                &assignments,
                Span::default(),
                &options,
                &mut parents,
                &mut journals,
            )
            .unwrap();
        journals[0].publish().unwrap();
        options.cancel.cancel();
        let mut error = publish_all(&mut journals[1..], &options, Span::default()).unwrap_err();
        compensate(&mut journals, &mut error);
        parents.rollback(Span::default(), &mut error);
        assert!(!root.path().join("created").exists());
        assert!(error.retained_paths.is_empty(), "{error}");
        assert!(error.is_cancelled(), "{error}");
    }
    assert!(owner.close().is_empty());
}

#[test]
fn cancellation_inside_a_publication_is_not_misreported_as_a_native_failure() {
    let root = workspace();
    fs::write(root.path().join("source"), b"new").unwrap();
    fs::write(root.path().join("target"), b"old").unwrap();
    let options = Options::new(root.path().join("scratch"));
    let mut owner = Ordinary::new(&options);
    owner.storage = Some(Storage::new(&options, Span::default()).unwrap());
    {
        let storage = owner.storage.as_ref().unwrap();
        let source = storage.assets.capture(&root.path().join("source")).unwrap();
        let mut journal = storage
            .assets
            .prepare(&source, &root.path().join("target"))
            .unwrap();
        let cause = journal
            .publish_with(|event| {
                if event == crate::transaction::filesystem::PublishEvent::AfterInstall {
                    Err(io::Error::new(
                        io::ErrorKind::Interrupted,
                        CancelPublication,
                    ))
                } else {
                    Ok(())
                }
            })
            .unwrap_err();
        let mut error = publication_failure(cause, Span::default());
        compensate(std::slice::from_mut(&mut journal), &mut error);
        assert!(error.is_cancelled(), "{error}");
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"old");
    }
    assert!(owner.close().is_empty());
}
