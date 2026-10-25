use msync::compiler::{Backend, CopyOp, Location, Program};
use msync::runtime::{ExecutionOptions, execute};
use msync::syntax::Span;
use std::fs;
use std::path::Path;
use tempfile::TempDir;

fn temporary_directory() -> TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::tempdir_in(root).unwrap()
}

fn program(source: &Path, target: &Path, container: bool, pattern: Option<&str>) -> Program {
    Program {
        operations: vec![CopyOp {
            source: Location {
                backend: Backend::Local,
                path: source.to_str().unwrap().into(),
            },
            destination: Location {
                backend: Backend::Local,
                path: format!("{}{}", target.display(), if container { "/" } else { "" }),
            },
            pattern: pattern.map(str::to_owned),
            span: Span { start: 0, end: 1 },
        }],
    }
}

#[test]
fn overwrite_and_preserve_name_are_distinct() {
    let dir = temporary_directory();
    let source = dir.path().join("config.toml");
    let target = dir.path().join("out/config.toml");
    fs::write(&source, "one").unwrap();
    let options = ExecutionOptions::default();
    let report = execute(&program(&source, &target, false, None), &options).unwrap();
    assert_eq!(report.copied_files, 1);
    fs::write(&source, "two").unwrap();
    execute(&program(&source, &target, false, None), &options).unwrap();
    assert_eq!(fs::read_to_string(&target).unwrap(), "two");
    assert!(
        execute(
            &program(&source, target.parent().unwrap(), true, None),
            &options
        )
        .is_err()
    );
    assert_eq!(fs::read_to_string(&target).unwrap(), "two");
}

#[test]
fn recursive_merge_and_full_name_regex() {
    let dir = temporary_directory();
    let source = dir.path().join("agents");
    fs::create_dir_all(source.join("nested")).unwrap();
    fs::write(source.join("a.toml"), "a").unwrap();
    fs::write(source.join("b.toml.old"), "b").unwrap();
    fs::write(source.join("nested/c.toml"), "c").unwrap();
    let target = dir.path().join("out");
    execute(
        &program(&source, &target, true, Some(r".+\.toml")),
        &ExecutionOptions::default(),
    )
    .unwrap();
    assert_eq!(fs::read_to_string(target.join("a.toml")).unwrap(), "a");
    assert!(!target.join("b.toml.old").exists());
    assert!(!target.join("nested").exists());
    let report = execute(
        &program(&source, &dir.path().join("tree"), false, None),
        &ExecutionOptions::default(),
    )
    .unwrap();
    assert_eq!(report.copied_files, 3);
    assert!(dir.path().join("tree/nested/c.toml").exists());
}

#[test]
fn preflight_prevents_partial_conflict_mutation() {
    let dir = temporary_directory();
    let source = dir.path().join("source");
    let out = dir.path().join("out/source");
    fs::create_dir_all(&source).unwrap();
    fs::create_dir_all(&out).unwrap();
    fs::write(source.join("a"), "a").unwrap();
    fs::write(source.join("z"), "new").unwrap();
    fs::write(out.join("z"), "old").unwrap();
    assert!(
        execute(
            &program(&source, out.parent().unwrap(), true, None),
            &ExecutionOptions::default()
        )
        .is_err()
    );
    assert!(!out.join("a").exists());
    assert_eq!(fs::read_to_string(out.join("z")).unwrap(), "old");
}

#[test]
fn same_file_hardlink_and_recursive_self_are_rejected_without_data_loss() {
    let dir = temporary_directory();
    let source = dir.path().join("a");
    let link = dir.path().join("b");
    fs::write(&source, "keep").unwrap();
    fs::hard_link(&source, &link).unwrap();
    for target in [&source, &link] {
        assert!(
            execute(
                &program(&source, target, false, None),
                &ExecutionOptions::default()
            )
            .is_err()
        );
        assert_eq!(fs::read_to_string(&source).unwrap(), "keep");
    }
    let tree = dir.path().join("tree");
    fs::create_dir(&tree).unwrap();
    assert!(
        execute(
            &program(&tree, &tree.join("nested"), false, None),
            &ExecutionOptions::default()
        )
        .is_err()
    );
    assert!(!tree.join("nested").exists());
}

#[test]
fn dry_run_is_offline_and_has_no_side_effects() {
    let dir = temporary_directory();
    let target = dir.path().join("never-created");
    let p = program(&dir.path().join("missing"), &target, false, None);
    let report = execute(&p, &ExecutionOptions { dry_run: true }).unwrap();
    assert_eq!(report.actions.len(), 1);
    assert_eq!(report.copied_files, 0);
    assert!(!target.exists());
}

#[test]
fn directory_to_non_slash_target_merges_in_place() {
    let dir = temporary_directory();
    let source = dir.path().join("in");
    let target = dir.path().join("out");
    fs::create_dir(&source).unwrap();
    fs::create_dir(&target).unwrap();
    fs::write(source.join("new"), "new").unwrap();
    fs::write(target.join("old"), "old").unwrap();
    execute(
        &program(&source, &target, false, None),
        &ExecutionOptions::default(),
    )
    .unwrap();
    assert!(target.join("new").exists());
    assert!(target.join("old").exists());
    assert!(!target.join("in").exists());
}

#[cfg(unix)]
#[test]
fn symlinks_in_source_or_destination_are_rejected() {
    use std::os::unix::fs::symlink;
    let dir = temporary_directory();
    let source = dir.path().join("source");
    let link = dir.path().join("link");
    fs::write(&source, "keep").unwrap();
    symlink(&source, &link).unwrap();
    let target = dir.path().join("target");
    assert!(
        execute(
            &program(&link, &target, false, None),
            &ExecutionOptions::default()
        )
        .is_err()
    );
    assert!(
        execute(
            &program(&source, &link, false, None),
            &ExecutionOptions::default()
        )
        .is_err()
    );
    assert!(!target.exists());
}

#[test]
fn http_download_runs_real_transport_then_local_conflict_policy() {
    use std::io::{Read, Write};
    use std::net::TcpListener;
    use std::thread;
    let dir = temporary_directory();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let server = thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        let mut request = [0_u8; 4096];
        let count = stream.read(&mut request).unwrap();
        let request = std::str::from_utf8(&request[..count]).unwrap();
        assert!(
            request.starts_with("GET /v1/help.html HTTP/1.1\r\n"),
            "{request}"
        );
        stream
            .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 4\r\nConnection: close\r\n\r\nhelp")
            .unwrap();
    });
    let target = dir.path().join("help.html");
    let mut p = program(&dir.path().join("unused"), &target, false, None);
    p.operations[0].source = Location {
        backend: Backend::Http {
            base: format!("http://{address}/"),
        },
        path: "~/v1/help.html".into(),
    };
    let report = execute(&p, &ExecutionOptions::default()).unwrap();
    server.join().unwrap();
    assert_eq!(report.copied_files, 1);
    assert_eq!(fs::read_to_string(target).unwrap(), "help");
}

#[test]
fn http_destinations_are_rejected_even_in_dry_run() {
    let dir = temporary_directory();
    let mut p = program(
        &dir.path().join("source"),
        &dir.path().join("target"),
        false,
        None,
    );
    p.operations[0].destination.backend = Backend::Http {
        base: "https://example.com/".into(),
    };
    assert!(execute(&p, &ExecutionOptions { dry_run: true }).is_err());
}

#[cfg(windows)]
#[test]
#[ignore = "requires an installed WSL distribution; run explicitly with --ignored"]
fn real_wsl_round_trip_and_remote_conflict_policy() {
    use std::process::Command;
    let dir = temporary_directory();
    let source = dir.path().join("a'b ;$(x).txt");
    fs::write(&source, "round trip").unwrap();
    let remote_dir_local = dir.path().join("remote");
    let mapping = Command::new("wsl")
        .args(["--exec", "wslpath", "-a", "-u"])
        .arg(&remote_dir_local)
        .output()
        .unwrap();
    assert!(
        mapping.status.success(),
        "{}",
        String::from_utf8_lossy(&mapping.stderr)
    );
    let remote_dir = String::from_utf8(mapping.stdout)
        .unwrap()
        .trim_end()
        .to_owned();
    let mut upload = program(&source, &remote_dir_local, true, None);
    upload.operations[0].destination = Location {
        backend: Backend::Wsl,
        path: format!("{remote_dir}/"),
    };
    let report = execute(&upload, &ExecutionOptions::default()).unwrap();
    assert_eq!(report.copied_files, 1);
    assert_eq!(
        fs::read_to_string(remote_dir_local.join(source.file_name().unwrap())).unwrap(),
        "round trip"
    );
    assert!(execute(&upload, &ExecutionOptions::default()).is_err());
    let target = dir.path().join("returned.txt");
    let mut download = program(&source, &target, false, None);
    download.operations[0].source = Location {
        backend: Backend::Wsl,
        path: format!(
            "{remote_dir}/{}",
            source.file_name().unwrap().to_str().unwrap()
        ),
    };
    execute(&download, &ExecutionOptions::default()).unwrap();
    assert_eq!(fs::read_to_string(target).unwrap(), "round trip");
}

proptest::proptest! {
    #![proptest_config(proptest::test_runner::Config::with_cases(32))]
    #[test]
    fn arbitrary_bytes_round_trip_and_overwrite_is_idempotent(bytes in proptest::collection::vec(proptest::prelude::any::<u8>(), 0..8192)) {
        let dir = temporary_directory();
        let source = dir.path().join("source.bin");
        let target = dir.path().join("nested/target.bin");
        fs::write(&source, &bytes).unwrap();
        let plan = program(&source, &target, false, None);
        execute(&plan, &ExecutionOptions::default()).unwrap();
        execute(&plan, &ExecutionOptions::default()).unwrap();
        proptest::prop_assert_eq!(fs::read(target).unwrap(), bytes);
    }
}

#[test]
fn http_paths_with_glob_characters_are_one_literal_download() {
    use std::io::{Read, Write};
    use std::net::TcpListener;
    use std::thread;
    let dir = temporary_directory();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let server = thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        let mut request = [0_u8; 4096];
        let count = stream.read(&mut request).unwrap();
        let request = std::str::from_utf8(&request[..count]).unwrap();
        assert!(
            request.starts_with("GET /v1/a[1]%7Bx%7D%20file.html HTTP/1.1\r\n"),
            "{request}"
        );
        stream
            .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nliteral")
            .unwrap();
    });
    let target = dir.path().join("download.html");
    let mut p = program(&dir.path().join("unused"), &target, false, None);
    p.operations[0].source = Location {
        backend: Backend::Http {
            base: format!("http://{address}/"),
        },
        path: "~/v1/a[1]{x} file.html".into(),
    };
    let report = execute(&p, &ExecutionOptions::default()).unwrap();
    server.join().unwrap();
    assert_eq!(report.copied_files, 1);
    assert_eq!(fs::read_to_string(target).unwrap(), "literal");
}
