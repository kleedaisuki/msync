//! Typed whole-program macro arguments through the actual native transaction path.

use std::fs;
use std::io::{Read, Write};
use std::path::Path;
use std::time::Duration;

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{Options, RuntimeSession};
use msync::language;

const MARKER: &[u8] = b"MSYNC_TYPED_MACRO_PAYLOAD:";

#[test]
#[ignore = "native child fixture, launched only by the mediated macro test"]
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

fn compile(compiler: &mut Session, work: &Path, text: &str) -> msync::hir::Program {
    compiler
        .compile(
            &language::parse(text).unwrap(),
            &CompileContext {
                pwd: work.to_owned(),
                source_file: None,
            },
        )
        .unwrap()
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn whole_program_macro_arguments_execute_and_undo_in_each_isolation_mode() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let root = tempfile::Builder::new()
        .prefix("macro-native-v4-")
        .tempdir_in(temporary)
        .unwrap();
    let executable = std::env::current_exe()
        .unwrap()
        .to_string_lossy()
        .replace('\\', "\\\\")
        .replace('"', "\\\"");
    let payload = "  typed program 可莉\n保留末尾空格  ";
    for (index, isolation) in [
        "read uncommitted",
        "read committed",
        "repeatable read",
        "serializable",
    ]
    .into_iter()
    .enumerate()
    {
        let work = root.path().join(format!("work-{index}"));
        fs::create_dir(&work).unwrap();
        fs::write(work.join("state"), b"origin").unwrap();
        let scratch = root.path().join(format!("scratch-{index}"));
        fs::create_dir(&scratch).unwrap();
        fs::write(scratch.join("user-sentinel"), b"preserve user data").unwrap();
        let mut compiler = Session::new();
        let mut runtime = RuntimeSession::new(&Options::new(&scratch)).unwrap();
        runtime.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
        let script = format!(
            "capture := captured\nmacro relay(program, bytes):\n    global capture\n    bytes -> program::stdin\n    program::stdout -> capture\ncapture := wrong-output\ntransaction A({isolation}):\n    child => $local::\"{executable}\"(\"--exact\", \"macro_echo_child_fixture\", \"--ignored\", \"--nocapture\")\n    relay(child, {payload:?})\n"
        );
        let program = compile(&mut compiler, &work, &script);
        tokio::time::timeout(Duration::from_secs(30), runtime.execute(&program))
            .await
            .unwrap_or_else(|_| panic!("native macro timed out in {isolation}"))
            .unwrap_or_else(|error| panic!("native macro failed in {isolation}: {error}"));
        assert_eq!(fs::read(work.join("state")).unwrap(), payload.as_bytes());
        let output = fs::read(work.join("captured")).unwrap();
        let offset = output
            .windows(MARKER.len())
            .position(|bytes| bytes == MARKER)
            .unwrap_or_else(|| panic!("missing child payload in {isolation}: {output:?}"));
        assert_eq!(&output[offset + MARKER.len()..], payload.as_bytes());
        assert!(!work.join("wrong-output").exists());
        let undo = compile(&mut compiler, &work, "rollback A\n");
        runtime.execute(&undo).await.unwrap();
        assert_eq!(fs::read(work.join("state")).unwrap(), b"origin");
        assert!(!work.join("captured").exists());
        runtime.shutdown().await.unwrap();
        let remaining = fs::read_dir(&scratch)
            .unwrap()
            .map(|entry| entry.unwrap().file_name())
            .collect::<Vec<_>>();
        assert_eq!(remaining, [std::ffi::OsString::from("user-sentinel")]);
        assert_eq!(
            fs::read(scratch.join("user-sentinel")).unwrap(),
            b"preserve user data"
        );
    }
}
