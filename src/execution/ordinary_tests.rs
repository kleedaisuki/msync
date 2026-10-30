use super::*;
use crate::filesystem::durable::{Batch, Event};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("ordinary-owner-")
        .tempdir_in(root)
        .unwrap()
}

fn assert_no_payload(scratch: &Path) {
    for entry in fs::read_dir(scratch).unwrap() {
        let entry = entry.unwrap();
        assert_eq!(entry.file_name(), ".msync-recovery-v1");
        for record in fs::read_dir(entry.path()).unwrap() {
            assert_eq!(record.unwrap().file_name(), ".lock");
        }
    }
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
fn a_shared_capture_root_excludes_other_live_workers_without_hiding_user_scratch() {
    let root = workspace();
    let scratch = root.path().join(".temp");
    fs::create_dir(&scratch).unwrap();
    fs::write(scratch.join("user-data"), b"keep").unwrap();
    let outer = InvocationRoot::create(&scratch).unwrap();
    let mut options = Options::new(&scratch);
    options.shared_invocation = Some(outer.clone());
    let mut first = Ordinary::new(&options);
    let mut second = Ordinary::new(&options);
    first.storage = Some(Storage::new(&options, Span::default()).unwrap());
    second.storage = Some(Storage::new(&options, Span::default()).unwrap());
    for owner in [&first, &second] {
        fs::write(
            owner
                .storage
                .as_ref()
                .unwrap()
                .imports
                .join("private-payload"),
            b"hidden",
        )
        .unwrap();
    }
    {
        let storage = first.storage.as_ref().unwrap();
        let snapshot = storage.assets.capture_in(root.path(), &outer).unwrap();
        assert!(
            snapshot
                .entries()
                .contains_key(Path::new(".temp/user-data"))
        );
        let private = outer.path().strip_prefix(root.path()).unwrap();
        assert!(
            snapshot
                .entries()
                .keys()
                .all(|path| !path.starts_with(private))
        );
        assert!(
            snapshot
                .entries()
                .keys()
                .all(|path| !path.starts_with(".temp/.msync-recovery-v1"))
        );
    }
    assert!(first.close().is_empty());
    assert!(second.close().is_empty());
    drop(first);
    drop(second);
    drop(options);
    outer.cleanup().unwrap();
    assert_eq!(fs::read(scratch.join("user-data")).unwrap(), b"keep");
}

#[test]
fn replacing_an_active_backing_ancestor_is_rejected_without_changing_user_data() {
    let root = workspace();
    let source = root.path().join("source");
    let destination = root.path().join("out");
    fs::create_dir(&source).unwrap();
    fs::create_dir(&destination).unwrap();
    fs::write(source.join("new"), b"new").unwrap();
    fs::write(destination.join("old"), b"old").unwrap();
    let scratch = destination.join(".temp");
    let outer = InvocationRoot::create(&scratch).unwrap();
    let mut options = Options::new(&scratch);
    options.shared_invocation = Some(outer.clone());
    let mut owner = Ordinary::new(&options);
    let mut node = literal(destination.clone());
    node.kind = InstructionKind::Transfer {
        source: Input::File {
            location: Location {
                backend: Backend::Local,
                path: source.to_string_lossy().into_owned(),
            },
            pattern: None,
        },
        destination: Output::File {
            location: Location {
                backend: Backend::Local,
                path: destination.to_string_lossy().into_owned(),
            },
            mode: DestinationMode::ReplaceTree,
        },
    };
    let error = owner.execute(&node).err().unwrap();
    assert!(
        error
            .to_string()
            .contains("active execution or recovery storage"),
        "{error}"
    );
    assert!(
        error
            .report
            .help
            .iter()
            .any(|help| help.contains("--scratch"))
    );
    assert_eq!(fs::read(destination.join("old")).unwrap(), b"old");
    assert!(!destination.join("new").exists());
    outer.verify().unwrap();
    assert!(owner.close().is_empty());
    drop(owner);
    drop(options);
    outer.cleanup().unwrap();
}

#[test]
fn cancellation_before_dispatch_creates_no_storage_or_target() {
    let root = workspace();
    let options = Options::new(root.path().join("scratch"));
    options.cancel.cancel();
    let mut owner = Ordinary::new(&options);
    assert!(
        owner
            .execute(&literal(root.path().join("target")))
            .err()
            .unwrap()
            .is_cancelled()
    );
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
        assert_no_payload(&options.scratch_root);
        assert_eq!(fs::read(root.path().join(name)).unwrap(), b"value");
    }
    assert!(owner.close().is_empty());
}

#[test]
fn a_fresh_publication_guard_rejects_foreign_replacement_before_effects() {
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
        });
        let mut journals = Vec::new();
        storage
            .prepare(&assignments, Span::default(), &options, &mut journals)
            .unwrap();
        fs::rename(
            root.path().join("second"),
            root.path().join("foreign-before"),
        )
        .unwrap();
        fs::write(root.path().join("second"), b"foreign").unwrap();
        assert!(Batch::prepare(journals).is_err());
        assert_eq!(fs::read(root.path().join("first")).unwrap(), b"old-first");
        assert_eq!(fs::read(root.path().join("second")).unwrap(), b"foreign");
    }
    assert!(owner.close().is_empty());
    assert_no_payload(&options.scratch_root);
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
        let publication = storage
            .assets
            .prepare(&source, &root.path().join("target"))
            .unwrap();
        let mut batch = Batch::prepare(vec![publication]).unwrap();
        let cause = batch
            .publish_with(|event| {
                if event == Event::AfterInstall(0) {
                    assert_eq!(fs::read(root.path().join("target")).unwrap(), b"new");
                    options.cancel.cancel();
                    Err(io::Error::new(
                        io::ErrorKind::Interrupted,
                        CancelPublication,
                    ))
                } else {
                    Ok(())
                }
            })
            .unwrap_err();
        assert_eq!(cause.cause.kind(), io::ErrorKind::Interrupted);
        batch.abort().unwrap();
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"old");
        assert!(!batch.journal_path().exists());
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
        });
        let assignments = storage
            .publication_roots(&assignments, Span::default())
            .unwrap();
        assert_eq!(assignments.len(), 1);
        assert!(!root.path().join("created").exists());
        let mut journals = Vec::new();
        storage
            .prepare(&assignments, Span::default(), &options, &mut journals)
            .unwrap();
        let mut batch = Batch::prepare(journals).unwrap();
        let cause = batch
            .publish_with(|event| {
                if event == Event::AfterInstall(0) {
                    assert!(root.path().join("created/deep/a").is_file());
                    assert!(root.path().join("created/deep/b").is_file());
                    options.cancel.cancel();
                    Err(io::Error::new(
                        io::ErrorKind::Interrupted,
                        CancelPublication,
                    ))
                } else {
                    Ok(())
                }
            })
            .unwrap_err();
        assert_eq!(cause.cause.kind(), io::ErrorKind::Interrupted);
        batch.abort().unwrap();
        assert!(!root.path().join("created").exists());
        assert!(!batch.journal_path().exists());
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
                if event == crate::filesystem::PublishEvent::AfterInstall {
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

#[cfg(windows)]
fn assert_pipe_is_not_a_finite_resource(location: Location) {
    assert!(
        Source::inspect(&location, Span::default())
            .unwrap()
            .is_none()
    );
    assert!(local(&location, true, Span::default()).is_err());
    let mut node = literal(PathBuf::from("unused"));
    let InstructionKind::Transfer {
        destination: Output::File {
            location: target, ..
        },
        ..
    } = &mut node.kind
    else {
        unreachable!();
    };
    *target = location;
    assert!(Ready::inspect(&node, None).unwrap().is_none());
}

#[cfg(windows)]
#[test]
fn absent_named_pipe_is_rejected_before_finite_resource_probes() {
    assert_pipe_is_not_a_finite_resource(Location {
        backend: Backend::Local,
        path: r"\\.\pipe\msync-finite-owner-absent-regression".into(),
    });
}

#[cfg(windows)]
#[test]
fn environment_expanded_named_pipe_is_rejected_before_finite_resource_probes() {
    let output = std::process::Command::new(std::env::current_exe().unwrap())
        .args([
            "--exact",
            "execution::ordinary::tests::environment_named_pipe_child",
            "--ignored",
            "--nocapture",
        ])
        .env(
            "MSYNC_FINITE_PIPE_TEST_PATH",
            r"\\.\pipe\msync-finite-owner-env-absent-regression",
        )
        .output()
        .unwrap();
    assert!(String::from_utf8_lossy(&output.stdout).contains("1 passed"));
    assert!(
        output.status.success(),
        "status={} stdout={} stderr={}",
        output.status,
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

#[cfg(windows)]
#[test]
#[ignore = "isolated environment fixture subprocess"]
fn environment_named_pipe_child() {
    if std::env::var_os("MSYNC_FINITE_PIPE_TEST_PATH").is_none() {
        return;
    }
    assert_pipe_is_not_a_finite_resource(Location {
        backend: Backend::Env,
        path: "MSYNC_FINITE_PIPE_TEST_PATH".into(),
    });
}
