//! Independent regression probes from review.
#[test]
fn directory_copy_to_ancestor_does_not_mutate_its_source() {
    use msync::compiler::{Backend, CopyOp, Location, Program};
    use msync::runtime::{ExecutionOptions, execute};
    use msync::syntax::Span;
    use std::{fs, path::Path};

    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let source = directory.path().join("nested");
    fs::create_dir_all(source.join("nested")).unwrap();
    fs::write(source.join("item"), "original root item").unwrap();
    fs::write(source.join("nested/item"), "different nested item").unwrap();
    let program = Program {
        operations: vec![CopyOp {
            source: Location {
                backend: Backend::Local,
                path: source.to_string_lossy().into_owned(),
            },
            destination: Location {
                backend: Backend::Local,
                path: directory.path().to_string_lossy().into_owned(),
            },
            pattern: None,
            span: Span { start: 0, end: 0 },
        }],
    };
    let result = execute(&program, &ExecutionOptions::default());
    assert_eq!(
        fs::read_to_string(source.join("item")).unwrap(),
        "original root item"
    );
    assert!(
        result.is_err(),
        "overlapping ancestor copy should fail before mutation"
    );
    assert!(!directory.path().join("item").exists());
}

#[cfg(windows)]
#[test]
fn http_remote_basename_cannot_escape_scratch_directory() {
    use msync::compiler::{Backend, CopyOp, Location, Program};
    use msync::runtime::{ExecutionOptions, execute};
    use msync::syntax::Span;
    use std::fs;
    use std::io::{Read, Write};
    use std::net::TcpListener;
    use std::path::Path;
    use std::sync::mpsc;
    use std::thread;
    use std::time::Duration;

    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let victim = directory.path().join("unrelated.txt");
    fs::write(&victim, "preserve me").unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let address = listener.local_addr().unwrap();
    let (stop, stopped) = mpsc::channel();
    let server = thread::spawn(move || {
        let mut connection = loop {
            match listener.accept() {
                Ok((connection, _)) => break connection,
                Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                    if stopped.try_recv().is_ok() {
                        return;
                    }
                    thread::sleep(Duration::from_millis(5));
                }
                Err(error) => panic!("{error}"),
            }
        };
        connection.set_nonblocking(false).unwrap();
        connection
            .set_read_timeout(Some(Duration::from_secs(5)))
            .unwrap();
        let mut request = [0_u8; 4096];
        let _ = connection.read(&mut request).unwrap();
        connection
            .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nnetwork")
            .unwrap();
    });
    let program = Program {
        operations: vec![CopyOp {
            source: Location {
                backend: Backend::Http {
                    base: format!("http://{address}/"),
                },
                path: format!("folder/{}", victim.display()),
            },
            destination: Location {
                backend: Backend::Local,
                path: directory
                    .path()
                    .join("output.txt")
                    .to_string_lossy()
                    .into_owned(),
            },
            pattern: None,
            span: Span { start: 0, end: 0 },
        }],
    };
    let _ = execute(&program, &ExecutionOptions::default());
    let _ = stop.send(());
    server.join().unwrap();
    assert_eq!(fs::read_to_string(victim).unwrap(), "preserve me");
}
