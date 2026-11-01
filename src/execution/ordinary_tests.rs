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
    if scratch.exists() {
        assert_eq!(fs::read_dir(scratch).unwrap().count(), 0);
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
fn replacement_has_no_before_image_or_stale_destination_identity_gate() {
    let root = workspace();
    fs::write(root.path().join("source"), b"new").unwrap();
    fs::write(root.path().join("target"), b"old").unwrap();
    let mut assets = SnapshotStore::new(&root.path().join("scratch")).unwrap();
    {
        let source = assets.capture(&root.path().join("source")).unwrap();
        let mut replacement = assets
            .prepare(&source, &root.path().join("target"))
            .unwrap();
        fs::write(root.path().join("target"), b"changed since staging").unwrap();
        replacement.publish().unwrap();
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"new");
        assert!(!replacement.journal().join("old").exists());
        replacement.finalize().unwrap();
    }
    assets.cleanup().unwrap();
}

#[test]
fn interrupted_batch_keeps_completed_files_and_retry_finishes() {
    let root = workspace();
    let mut assets = SnapshotStore::new(&root.path().join("scratch")).unwrap();
    fs::write(root.path().join("source"), b"new").unwrap();
    for name in ["first", "second"] {
        fs::write(root.path().join(name), b"old").unwrap();
    }
    {
        let source = assets.capture(&root.path().join("source")).unwrap();
        let replacements = ["first", "second"]
            .map(|name| assets.prepare(&source, &root.path().join(name)).unwrap())
            .into();
        let mut batch = Batch::prepare(replacements).unwrap();
        let error = batch
            .publish_with(|event| {
                if event == Event::AfterInstall(0) {
                    Err(io::Error::new(io::ErrorKind::Interrupted, "cancelled"))
                } else {
                    Ok(())
                }
            })
            .unwrap_err();
        assert_eq!(error.cause.kind(), io::ErrorKind::Interrupted);
        batch.abort().unwrap();
        assert_eq!(fs::read(root.path().join("first")).unwrap(), b"new");
        assert_eq!(fs::read(root.path().join("second")).unwrap(), b"old");
        let replacements = ["first", "second"]
            .map(|name| assets.prepare(&source, &root.path().join(name)).unwrap())
            .into();
        let mut retry = Batch::prepare(replacements).unwrap();
        retry.publish().unwrap();
        retry.finalize().unwrap();
        for name in ["first", "second"] {
            assert_eq!(fs::read(root.path().join(name)).unwrap(), b"new");
        }
    }
    assets.cleanup().unwrap();
}

#[test]
fn cancellation_before_install_leaves_target_and_cleans_only_stage() {
    let root = workspace();
    let mut assets = SnapshotStore::new(&root.path().join("scratch")).unwrap();
    fs::write(root.path().join("source"), b"new").unwrap();
    fs::write(root.path().join("target"), b"old").unwrap();
    {
        let source = assets.capture(&root.path().join("source")).unwrap();
        let mut replacement = assets
            .prepare(&source, &root.path().join("target"))
            .unwrap();
        let error = replacement
            .publish_with(|_| Err(io::Error::new(io::ErrorKind::Interrupted, "cancelled")))
            .unwrap_err();
        assert!(publication_failure(error, Span::default()).is_cancelled());
        replacement.finalize().unwrap();
        assert_eq!(fs::read(root.path().join("target")).unwrap(), b"old");
    }
    assets.cleanup().unwrap();
}

#[test]
fn simple_file_and_literal_assignments_do_not_create_execution_storage() {
    let root = workspace();
    let scratch = root.path().join("unneeded-scratch");
    fs::write(&scratch, b"scratch can even be an ordinary user file").unwrap();
    let mut owner = Ordinary::new(&Options::new(&scratch));
    let first = literal(root.path().join("source"));
    assert!(matches!(
        owner.execute(&first).unwrap(),
        Attempt::Completed(_)
    ));
    let mut copied = literal(root.path().join("nested/copied"));
    let InstructionKind::Transfer { source, .. } = &mut copied.kind else {
        unreachable!()
    };
    *source = Input::File {
        location: Location {
            backend: Backend::Local,
            path: root.path().join("source").to_string_lossy().into_owned(),
        },
        pattern: None,
    };
    assert!(matches!(
        owner.execute(&copied).unwrap(),
        Attempt::Completed(_)
    ));
    assert_eq!(
        fs::read(root.path().join("nested/copied")).unwrap(),
        b"value"
    );
    assert!(owner.storage.is_none());
    assert_eq!(
        fs::read(&scratch).unwrap(),
        b"scratch can even be an ordinary user file"
    );
}

#[cfg(unix)]
#[test]
fn recursive_copy_preserves_links_instead_of_following_or_refusing_them() {
    let root = workspace();
    let source = root.path().join("source");
    fs::create_dir(&source).unwrap();
    fs::write(source.join("data"), b"payload").unwrap();
    std::os::unix::fs::symlink("data", source.join("alias")).unwrap();
    std::os::unix::fs::symlink("missing", source.join("dangling")).unwrap();
    std::os::unix::fs::symlink(".", source.join("cycle")).unwrap();
    let mut node = literal(root.path().join("copy"));
    let InstructionKind::Transfer { source: input, .. } = &mut node.kind else {
        unreachable!()
    };
    *input = Input::File {
        location: Location {
            backend: Backend::Local,
            path: source.to_string_lossy().into_owned(),
        },
        pattern: None,
    };
    let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
    assert!(matches!(
        owner.execute(&node).unwrap(),
        Attempt::Completed(_)
    ));
    for (name, target) in [("alias", "data"), ("dangling", "missing"), ("cycle", ".")] {
        assert_eq!(
            fs::read_link(root.path().join("copy").join(name)).unwrap(),
            Path::new(target)
        );
    }
    assert_eq!(
        fs::read(root.path().join("copy/alias")).unwrap(),
        b"payload"
    );
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

#[cfg(unix)]
proptest::proptest! {
    #![proptest_config(proptest::test_runner::Config::with_cases(24))]
    #[test]
    fn replacing_a_file_is_atomic_for_existing_readers(payload in proptest::collection::vec(proptest::num::u8::ANY, 0..8192)) {
        use std::io::Read;
        let root = workspace();
        let source = root.path().join("source");
        let target = root.path().join("target");
        fs::write(&source, &payload).unwrap();
        fs::write(&target, b"old complete bytes").unwrap();
        let mut reader = fs::File::open(&target).unwrap();
        let mut node = literal(target.clone());
        let InstructionKind::Transfer { source: input, .. } = &mut node.kind else { unreachable!() };
        *input = Input::File { location: Location { backend: Backend::Local, path: source.to_string_lossy().into_owned() }, pattern: None };
        let scratch = root.path().join("not-created");
        let mut owner = Ordinary::new(&Options::new(&scratch));
        let Attempt::Completed(report) = owner.execute(&node).unwrap() else { unreachable!() };
        proptest::prop_assert_eq!(report.assigned_bytes, payload.len() as u64);
        proptest::prop_assert_eq!(fs::read(&target).unwrap(), payload);
        let mut observed = Vec::new();
        reader.read_to_end(&mut observed).unwrap();
        proptest::prop_assert_eq!(observed, b"old complete bytes");
        proptest::prop_assert!(!scratch.exists());
        proptest::prop_assert_eq!(fs::read_dir(root.path()).unwrap().count(), 2);
    }
}

#[cfg(unix)]
#[test]
fn ordinary_replacement_uses_kernel_namespace_permissions_not_readonly_bit_guesses() {
    use std::os::unix::fs::PermissionsExt;
    let root = workspace();
    fs::write(root.path().join("source"), b"new").unwrap();
    fs::write(root.path().join("target"), b"old").unwrap();
    fs::set_permissions(
        root.path().join("target"),
        fs::Permissions::from_mode(0o400),
    )
    .unwrap();
    let mut node = literal(root.path().join("target"));
    let InstructionKind::Transfer { source, .. } = &mut node.kind else {
        unreachable!()
    };
    *source = Input::File {
        location: Location {
            backend: Backend::Local,
            path: root.path().join("source").to_string_lossy().into_owned(),
        },
        pattern: None,
    };
    let mut owner = Ordinary::new(&Options::new(root.path().join("scratch")));
    assert!(matches!(
        owner.execute(&node).unwrap(),
        Attempt::Completed(_)
    ));
    assert_eq!(fs::read(root.path().join("target")).unwrap(), b"new");
}

#[test]
fn native_directory_copy_excludes_only_the_current_invocation_and_keeps_user_temp_data() {
    let root = workspace();
    let source = root.path().join("source");
    let scratch = source.join(".temp");
    fs::create_dir_all(&scratch).unwrap();
    fs::write(scratch.join("user-data"), b"keep").unwrap();
    let invocation = InvocationRoot::create(&scratch).unwrap();
    fs::write(invocation.path().join("private-spool"), b"internal").unwrap();
    let mut options = Options::new(&scratch);
    options.shared_invocation = Some(invocation.clone());
    let mut node = literal(root.path().join("copied"));
    let InstructionKind::Transfer { source: input, .. } = &mut node.kind else {
        unreachable!()
    };
    *input = Input::File {
        location: Location {
            backend: Backend::Local,
            path: source.to_string_lossy().into_owned(),
        },
        pattern: None,
    };
    let mut owner = Ordinary::new(&options);
    assert!(matches!(
        owner.execute(&node).unwrap(),
        Attempt::Completed(_)
    ));
    assert_eq!(
        fs::read(root.path().join("copied/.temp/user-data")).unwrap(),
        b"keep"
    );
    assert_eq!(
        fs::read_dir(root.path().join("copied/.temp"))
            .unwrap()
            .count(),
        1
    );
    drop(owner);
    drop(options);
    invocation.cleanup().unwrap();
}

#[cfg(unix)]
#[test]
fn standalone_relative_and_dangling_links_are_copied_as_links() {
    let root = workspace();
    fs::write(root.path().join("data"), b"bytes stay at target").unwrap();
    for (name, target) in [("relative", "data"), ("dangling", "not-created")] {
        std::os::unix::fs::symlink(target, root.path().join(name)).unwrap();
        let modified = filetime::FileTime::from_unix_time(1_600_000_000, 123_456_789);
        filetime::set_symlink_file_times(root.path().join(name), modified, modified).unwrap();
        let copied = root.path().join(format!("copy-{name}"));
        let mut node = literal(copied.clone());
        let InstructionKind::Transfer { source, .. } = &mut node.kind else {
            unreachable!()
        };
        *source = Input::File {
            location: Location {
                backend: Backend::Local,
                path: root.path().join(name).to_string_lossy().into_owned(),
            },
            pattern: None,
        };
        let mut owner = Ordinary::new(&Options::new(root.path().join("unused-scratch")));
        assert!(matches!(
            owner.execute(&node).unwrap(),
            Attempt::Completed(_)
        ));
        assert_eq!(fs::read_link(&copied).unwrap(), Path::new(target));
        assert_eq!(
            fs::symlink_metadata(&copied).unwrap().modified().unwrap(),
            fs::symlink_metadata(root.path().join(name))
                .unwrap()
                .modified()
                .unwrap()
        );
    }
    assert!(!root.path().join("unused-scratch").exists());
    assert_eq!(
        fs::read(root.path().join("data")).unwrap(),
        b"bytes stay at target"
    );
}
