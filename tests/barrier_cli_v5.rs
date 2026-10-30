//! User-facing process and filesystem journeys across synchronization barriers.

use std::fs;
use std::io::{Read, Write};
use std::path::Path;
use std::process::{Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant};

const PAYLOAD_BYTES: usize = 2 * 1024 * 1024;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("barrier-cli-v5-")
        .tempdir_in(root)
        .unwrap()
}

fn literal(value: &str) -> String {
    format!("\"{}\"", value.replace('\\', "\\\\").replace('"', "\\\""))
}

fn child(name: &str, mode: &str) -> String {
    let executable = std::env::current_exe().unwrap();
    format!(
        "{name} => $local::{}(\"--exact\", \"barrier_child_fixture\", \"--ignored\", \"--nocapture\", \"--skip\", {})\n",
        literal(executable.to_str().unwrap()),
        literal(&format!("mode:{mode}")),
    )
}

fn invoke(root: &Path, arguments: &[&str], source: &str) -> Output {
    let script = root.join("journey.msync");
    fs::write(&script, source).unwrap();
    let mut command = command(root, arguments);
    command.arg(&script);
    capture(command, None)
}

fn command(root: &Path, arguments: &[&str]) -> Command {
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let mut command = Command::new(executable);
    command
        .args(arguments)
        .current_dir(root)
        .env("NO_COLOR", "1")
        .env(
            "MSYNC_SANDBOXIE_HOME",
            root.join("no-sandboxie-installation"),
        )
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    command
}

fn capture(mut command: Command, input: Option<Vec<u8>>) -> Output {
    if input.is_some() {
        command.stdin(Stdio::piped());
    }
    let mut process = command.spawn().unwrap();
    let input_writer = input.map(|input| {
        let mut stdin = process.stdin.take().unwrap();
        thread::spawn(move || stdin.write_all(&input))
    });
    let mut stdout = process.stdout.take().unwrap();
    let mut stderr = process.stderr.take().unwrap();
    let output_reader = thread::spawn(move || {
        let mut bytes = Vec::new();
        stdout.read_to_end(&mut bytes).unwrap();
        bytes
    });
    let error_reader = thread::spawn(move || {
        let mut bytes = Vec::new();
        stderr.read_to_end(&mut bytes).unwrap();
        bytes
    });
    let deadline = Instant::now() + Duration::from_secs(30);
    let status = loop {
        if let Some(status) = process.try_wait().unwrap() {
            break status;
        }
        if Instant::now() >= deadline {
            process.kill().unwrap();
            process.wait().unwrap();
            panic!("msync process did not complete its bounded barrier journey");
        }
        thread::sleep(Duration::from_millis(10));
    };
    if let Some(writer) = input_writer {
        writer.join().unwrap().unwrap();
    }
    Output {
        status,
        stdout: output_reader.join().unwrap(),
        stderr: error_reader.join().unwrap(),
    }
}

#[test]
fn ordinary_repl_statements_publish_before_the_next_statement_reads_them() {
    let root = workspace();
    let payload = "ordinary-UTF8-可莉 ".repeat(64 * 1024);
    let source = format!("{} -> first\nfirst -> second\n/quit\n", literal(&payload));
    let output = capture(
        command(
            root.path(),
            &[
                "repl",
                "--jobs",
                "2",
                "--buffer-size",
                "4096",
                "--memory-limit",
                "8192",
            ],
        ),
        Some(source.into_bytes()),
    );
    success(&output);
    for name in ["first", "second"] {
        let bytes = fs::read(root.path().join(name)).unwrap();
        assert_eq!(bytes.len(), payload.len());
        assert!(bytes == payload.as_bytes());
    }
}

fn success(output: &Output) {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr),
    );
    let diagnostic = String::from_utf8_lossy(&output.stderr);
    assert!(!diagnostic.contains("Sandboxie"), "{diagnostic}");
    assert!(!diagnostic.contains("SbieDll"), "{diagnostic}");
}

#[test]
fn a_bare_program_finishes_before_the_next_block_observes_its_created_file() {
    let root = workspace();
    let source = format!("{}generated -> copied\n", child("producer", "create"));
    let output = invoke(root.path(), &["run"], &source);
    success(&output);
    assert_eq!(
        fs::read(root.path().join("copied")).unwrap(),
        b"created externally"
    );
    assert_eq!(
        fs::read(root.path().join("generated")).unwrap(),
        b"created externally"
    );
}

#[test]
fn the_next_block_does_not_read_a_snapshot_taken_before_a_program_deleted_its_source() {
    let root = workspace();
    fs::write(
        root.path().join("victim"),
        b"must not survive as a cached source",
    )
    .unwrap();
    let source = format!("{}victim -> copied\n", child("deleter", "delete"));
    let output = invoke(root.path(), &["run"], &source);
    assert!(!output.status.success(), "{:?}", output);
    assert!(!root.path().join("victim").exists());
    assert!(!root.path().join("copied").exists());
    let diagnostic = String::from_utf8_lossy(&output.stderr);
    assert!(diagnostic.contains("victim"), "{diagnostic}");
    assert!(!diagnostic.contains("Sandboxie"), "{diagnostic}");
}

#[test]
fn awaiting_large_output_drains_both_pipes_before_the_ports_are_read() {
    let root = workspace();
    let source = format!(
        "async {}\"independent file\" -> independent\nawait producer\nproducer::stdout -> output\nproducer::stderr -> errors\nproducer::exit_code -> status\nproducer::pid -> pid\n",
        child("producer", "large"),
    );
    let output = invoke(root.path(), &["run"], &source);
    success(&output);
    assert_eq!(
        fs::read(root.path().join("independent")).unwrap(),
        b"independent file"
    );
    let bytes = fs::read(root.path().join("output")).unwrap();
    let begin = bytes
        .windows(b"BEGIN_PAYLOAD\n".len())
        .position(|part| part == b"BEGIN_PAYLOAD\n")
        .unwrap()
        + b"BEGIN_PAYLOAD\n".len();
    assert_eq!(
        &bytes[begin..begin + PAYLOAD_BYTES],
        vec![b'Q'; PAYLOAD_BYTES]
    );
    assert!(bytes[begin + PAYLOAD_BYTES..].starts_with(b"\nEND_PAYLOAD\n"));
    assert_eq!(
        fs::read(root.path().join("errors")).unwrap(),
        vec![b'E'; 256 * 1024]
    );
    assert_eq!(
        fs::read_to_string(root.path().join("status"))
            .unwrap()
            .trim(),
        "0"
    );
    assert!(
        fs::read_to_string(root.path().join("pid"))
            .unwrap()
            .trim()
            .parse::<u32>()
            .unwrap()
            > 0
    );
}

#[test]
fn an_external_nonzero_exit_is_observable_data_not_a_transaction_rollback() {
    let root = workspace();
    let source = format!(
        "{}child::exit_code -> status\nchild::stdout -> output\n\"continued\" -> after\n",
        child("child", "exit-seven"),
    );
    let output = invoke(root.path(), &["run"], &source);
    success(&output);
    assert_eq!(
        fs::read_to_string(root.path().join("status"))
            .unwrap()
            .trim(),
        "7"
    );
    assert!(
        String::from_utf8(fs::read(root.path().join("output")).unwrap())
            .unwrap()
            .contains("SELF_EXIT_SEVEN")
    );
    assert_eq!(
        fs::read(root.path().join("external-side-effect")).unwrap(),
        b"not rolled back"
    );
    assert_eq!(fs::read(root.path().join("after")).unwrap(), b"continued");
}

#[test]
fn a_macro_joins_its_own_unawaited_program_before_returning_to_its_caller() {
    let root = workspace();
    let command = child("producer", "create-after-trigger");
    let source = format!(
        "macro make():\n    async {}    \"release\" -> trigger\nmake()\ngenerated -> copied\n",
        command,
    );
    let output = invoke(root.path(), &["run"], &source);
    success(&output);
    assert_eq!(
        fs::read(root.path().join("copied")).unwrap(),
        b"created externally"
    );
}

#[test]
fn explicitly_awaiting_inside_a_macro_does_not_join_the_child_twice() {
    let root = workspace();
    let source = format!(
        "macro make():\n    async {}    await producer\n    producer::stdout -> result\nmake()\nresult -> copied\n",
        child("producer", "create"),
    );
    let output = invoke(root.path(), &["run"], &source);
    success(&output);
    assert!(
        String::from_utf8(fs::read(root.path().join("copied")).unwrap())
            .unwrap()
            .contains("CREATED_EXTERNALLY")
    );
}

#[test]
fn async_program_ports_can_feed_another_process_without_a_global_join() {
    let root = workspace();
    let source = format!(
        "async {}async {}producer::stdout -> consumer::stdin\nawait consumer\nawait producer\nconsumer::stdout -> captured\n",
        child("producer", "pipeline-producer"),
        child("consumer", "pipeline-consumer"),
    );
    let output = invoke(root.path(), &["run"], &source);
    success(&output);
    assert_eq!(
        fs::read(root.path().join("pipeline-direct")).unwrap(),
        b"consumed stream"
    );
    let captured = fs::read_to_string(root.path().join("captured")).unwrap();
    assert!(captured.contains("PIPELINE_CONSUMED"), "{captured}");
}

#[test]
#[ignore = "subprocess fixture invoked only by barrier CLI journeys"]
fn barrier_child_fixture() {
    let mode = std::env::args()
        .find_map(|argument| argument.strip_prefix("mode:").map(str::to_owned))
        .unwrap();
    match mode.as_str() {
        "create" => {
            fs::write("generated", b"created externally").unwrap();
            println!("CREATED_EXTERNALLY");
        }
        "delete" => fs::remove_file("victim").unwrap(),
        "large" => {
            let mut stdout = std::io::stdout().lock();
            stdout.write_all(b"BEGIN_PAYLOAD\n").unwrap();
            stdout.write_all(&vec![b'Q'; PAYLOAD_BYTES]).unwrap();
            stdout.write_all(b"\nEND_PAYLOAD\n").unwrap();
            stdout.flush().unwrap();
            std::io::stderr()
                .write_all(&vec![b'E'; 256 * 1024])
                .unwrap();
        }
        "exit-seven" => {
            fs::write("external-side-effect", b"not rolled back").unwrap();
            println!("SELF_EXIT_SEVEN");
            std::io::stdout().flush().unwrap();
            std::process::exit(7);
        }
        "create-after-trigger" => {
            let deadline = Instant::now() + Duration::from_secs(10);
            while !Path::new("trigger").exists() {
                assert!(Instant::now() < deadline, "macro never released its child");
                thread::sleep(Duration::from_millis(5));
            }
            fs::write("generated", b"created externally").unwrap();
        }
        "pipeline-producer" => {
            let mut stdout = std::io::stdout().lock();
            stdout.write_all(&vec![b'P'; PAYLOAD_BYTES]).unwrap();
            stdout.write_all(b"PIPELINE_END").unwrap();
        }
        "pipeline-consumer" => {
            let mut bytes = Vec::new();
            std::io::stdin().read_to_end(&mut bytes).unwrap();
            assert!(
                bytes
                    .windows(b"PIPELINE_END".len())
                    .any(|part| part == b"PIPELINE_END")
            );
            fs::write("pipeline-direct", b"consumed stream").unwrap();
            println!("PIPELINE_CONSUMED");
        }
        _ => panic!("unexpected child fixture mode {mode}"),
    }
}
