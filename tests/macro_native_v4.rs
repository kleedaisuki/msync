//! Typed program arguments and lexical globals through direct native execution.

use std::fs;
use std::io::{Read, Write};
use std::path::Path;
use std::time::Duration;

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{Options, RuntimeSession};
use msync::language;

const MARKER: &[u8] = b"MSYNC_TYPED_MACRO_PAYLOAD:";

#[test]
#[ignore = "native child fixture, launched only by the macro test"]
fn macro_echo_child_fixture() {
    let mut bytes = Vec::new();
    std::io::stdin().read_to_end(&mut bytes).unwrap();
    fs::write("state", &bytes).unwrap();
    let mut stdout = std::io::stdout().lock();
    stdout.write_all(MARKER).unwrap();
    stdout.write_all(&bytes).unwrap();
    stdout.flush().unwrap();
    std::process::exit(0);
}

#[tokio::test]
async fn whole_program_macro_arguments_preserve_payloads_and_lexical_globals() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let root = tempfile::Builder::new()
        .prefix("macro-native-")
        .tempdir_in(temporary)
        .unwrap();
    let executable = std::env::current_exe()
        .unwrap()
        .to_string_lossy()
        .replace('\\', "\\\\")
        .replace('"', "\\\"");
    let payload = "  typed program 可莉\n保留末尾空格  ";
    let scratch = root.path().join("scratch");
    fs::create_dir(&scratch).unwrap();
    fs::write(scratch.join("user-sentinel"), b"preserve user data").unwrap();
    let mut compiler = Session::new();
    let mut runtime = RuntimeSession::new(&Options::new(&scratch)).unwrap();
    let script = format!(
        "capture := captured\nmacro relay(program, bytes):\n    global capture\n    bytes -> program::stdin\n    program::stdout -> capture\ncapture := wrong-output\nasync child => $local::\"{executable}\"(\"--exact\", \"macro_echo_child_fixture\", \"--ignored\", \"--nocapture\")\nrelay(child, {payload:?})\nawait child\n"
    );
    let program = compiler
        .compile(
            &language::parse(&script).unwrap(),
            &CompileContext {
                pwd: root.path().to_owned(),
                source_file: None,
            },
        )
        .unwrap();
    tokio::time::timeout(Duration::from_secs(30), runtime.execute(&program))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(
        fs::read(root.path().join("state")).unwrap(),
        payload.as_bytes()
    );
    let output = fs::read(root.path().join("captured")).unwrap();
    let offset = output
        .windows(MARKER.len())
        .position(|bytes| bytes == MARKER)
        .unwrap();
    assert_eq!(&output[offset + MARKER.len()..], payload.as_bytes());
    assert!(!root.path().join("wrong-output").exists());
    runtime.shutdown().await.unwrap();
    assert_eq!(
        fs::read(scratch.join("user-sentinel")).unwrap(),
        b"preserve user data"
    );
}
