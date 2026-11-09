#![cfg(unix)]

use std::fs;
use std::io::{Read, Write};
use std::path::Path;
use std::process::{Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

fn run(root: &Path, source: &str) {
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .args(["--no-config", "run", "-"])
        .current_dir(root)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(source.as_bytes())
        .unwrap();
    let mut stdout = child.stdout.take().unwrap();
    let mut stderr = child.stderr.take().unwrap();
    let out = thread::spawn(move || {
        let mut bytes = Vec::new();
        stdout.read_to_end(&mut bytes).unwrap();
        bytes
    });
    let err = thread::spawn(move || {
        let mut bytes = Vec::new();
        stderr.read_to_end(&mut bytes).unwrap();
        bytes
    });
    let deadline = Instant::now() + Duration::from_secs(10);
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        if Instant::now() >= deadline {
            child.kill().unwrap();
            child.wait().unwrap();
            panic!("FIFO participants did not reach EOF: {source}");
        }
        thread::sleep(Duration::from_millis(5));
    };
    let output = out.join().unwrap();
    let error = err.join().unwrap();
    assert!(
        status.success(),
        "{source}\nstdout={}\nstderr={}",
        String::from_utf8_lossy(&output),
        String::from_utf8_lossy(&error)
    );
}

#[test]
fn fifo_participant_groups_allow_fan_in_and_fan_out_without_premature_eof() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let root = tempfile::Builder::new()
        .prefix("fifo-participants-")
        .tempdir_in(temporary)
        .unwrap();
    for (writers, readers) in [(1, 1), (2, 1), (1, 2), (3, 2), (2, 3)] {
        let work = root.path().join(format!("{writers}-{readers}"));
        fs::create_dir(&work).unwrap();
        assert!(
            Command::new("mkfifo")
                .arg(work.join("wire"))
                .status()
                .unwrap()
                .success()
        );
        let mut source = String::new();
        let mut expected = Vec::new();
        for writer in 0..writers {
            let bytes = vec![b'a' + writer as u8; 41 + writer];
            fs::write(work.join(format!("in-{writer}")), &bytes).unwrap();
            expected.extend(bytes);
            source.push_str(&format!("in-{writer} -> wire\n"));
        }
        for reader in 0..readers {
            source.push_str(&format!("wire -> out-{reader}\n"));
        }
        run(&work, &source);
        let mut actual = Vec::new();
        for reader in 0..readers {
            actual.extend(fs::read(work.join(format!("out-{reader}"))).unwrap());
        }
        expected.sort_unstable();
        actual.sort_unstable();
        assert_eq!(actual, expected, "{writers} writers / {readers} readers");
    }
}

#[test]
fn native_stream_paths_follow_selected_directory_and_fifo_aliases() {
    use std::os::unix::fs::symlink;
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let root = tempfile::Builder::new()
        .prefix("fifo-alias-")
        .tempdir_in(temporary)
        .unwrap();
    fs::create_dir(root.path().join("real")).unwrap();
    fs::write(root.path().join("real/input"), b"normal linked endpoint").unwrap();
    assert!(
        Command::new("mkfifo")
            .arg(root.path().join("real/wire"))
            .status()
            .unwrap()
            .success()
    );
    symlink("real", root.path().join("alias")).unwrap();
    symlink("wire", root.path().join("real/channel")).unwrap();
    run(
        root.path(),
        "alias/input -> alias/channel\nalias/wire -> output\n",
    );
    assert_eq!(
        fs::read(root.path().join("output")).unwrap(),
        b"normal linked endpoint"
    );
}
