//! Literal String bytes survive real source, formatting, object and JSON journeys.

use std::collections::BTreeMap;
use std::fs;
use std::io::Write;
use std::path::Path;
use std::process::{Command, Output, Stdio};
use std::sync::Arc;

fn workspace() -> tempfile::TempDir {
    let parent = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&parent).unwrap();
    tempfile::Builder::new()
        .prefix("literal-string-journey-")
        .tempdir_in(parent)
        .unwrap()
}

// The source contract only doubles a delimiter. Backslashes and real control
// bytes are never rewritten; this deliberately does not call the source writer.
fn quoted(value: &str) -> String {
    format!("\"{}\"", value.replace('"', "\"\""))
}

fn invoke(root: &Path, args: &[&str]) -> Output {
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    Command::new(executable)
        .current_dir(root)
        .args(["--no-config", "--color", "never"])
        .args(args)
        .stdin(Stdio::null())
        .output()
        .unwrap()
}

fn success(root: &Path, args: &[&str]) -> Output {
    let output = invoke(root, args);
    assert!(
        output.status.success(),
        "{args:?}\nstdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    output
}

fn export(root: &Path) {
    success(root, &["check", "entry.msync"]);
    success(root, &["compile", "entry.msync", "-o", "program.mobj"]);
    success(root, &["emit-ir", "entry.msync", "-o", "program.json"]);
}

fn literal_payload_journey(payloads: &[&str]) {
    let root = workspace();
    let mut source = String::new();
    for (index, payload) in payloads.iter().enumerate() {
        source.push_str(&format!("{} -> value-{index}\n", quoted(payload)));
    }
    fs::write(root.path().join("entry.msync"), &source).unwrap();
    export(root.path());
    let formatted = success(root.path(), &["fmt", "--stdout", "entry.msync"]).stdout;
    fs::write(root.path().join("formatted.msync"), &formatted).unwrap();
    let reformatted = success(root.path(), &["fmt", "--stdout", "formatted.msync"]).stdout;
    assert_eq!(formatted, reformatted, "formatting must be idempotent");

    for args in [["run", "entry.msync"], ["run", "formatted.msync"]] {
        success(root.path(), &args);
        for (index, payload) in payloads.iter().enumerate() {
            let path = root.path().join(format!("value-{index}"));
            assert_eq!(
                fs::read(&path).unwrap(),
                payload.as_bytes(),
                "{args:?}, value-{index}"
            );
            fs::remove_file(path).unwrap();
        }
    }
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    fs::remove_file(root.path().join("formatted.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        success(root.path(), &args);
        for (index, payload) in payloads.iter().enumerate() {
            let path = root.path().join(format!("value-{index}"));
            assert_eq!(
                fs::read(&path).unwrap(),
                payload.as_bytes(),
                "{args:?}, value-{index}"
            );
            fs::remove_file(path).unwrap();
        }
    }
}

#[test]
fn pasted_windows_strings_remain_literal_in_source_and_source_free_artifacts() {
    literal_payload_journey(&[r"D:\Code\agents\target\xmlsquish\artifacts"]);
}

#[test]
fn real_control_bytes_and_doubled_quotes_survive_formatting_and_artifacts() {
    literal_payload_journey(&[
        r"\t\n\r\\\",
        r"\\server\share\target\new\runtime\",
        "embedded \"quote\" # :: / -> [] () and trailing\\",
        "first line\n# msync: 0.7\n# msync: 0.3\n    # still String data\nthird\tfield\r\nfourth\rlast",
        "可莉🌟\n\"quoted line\"\n\\target",
    ]);
}

#[test]
fn native_paths_with_target_new_and_runtime_need_no_backslash_doubling() {
    let root = workspace();
    let input = if cfg!(windows) {
        root.path()
            .join("target")
            .join("new")
            .join("runtime")
            .join("artifacts")
    } else {
        root.path().join(r"target\new\runtime").join("artifacts")
    };
    fs::create_dir_all(&input).unwrap();
    let base = input.to_str().unwrap();
    let source = format!(
        concat!(
            "base := {}\n",
            "name := \"codex.prompt\"\n",
            "file := codex.prompt\n",
            "physical := $local::{}\n",
            "native normalized(path) = \"fs.lexically_normal\"\n",
            "base / name -> string-string\n",
            "base / file -> string-path\n",
            "physical / name -> path-string\n",
            "physical / file -> path-path\n",
            "normalized(physical) / \"codex.prompt\" -> native-path\n",
        ),
        quoted(base),
        quoted(base),
    );
    fs::write(root.path().join("entry.msync"), &source).unwrap();
    export(root.path());
    let formatted = success(root.path(), &["fmt", "--stdout", "entry.msync"]).stdout;
    fs::write(root.path().join("formatted.msync"), formatted).unwrap();
    for (args, payload) in [
        (["run", "entry.msync"], "direct input"),
        (["run", "formatted.msync"], "formatted input"),
        (["run", "program.mobj"], "source-free object input"),
        (["run-ir", "program.json"], "source-free JSON input"),
    ] {
        if args[1] == "program.mobj" {
            fs::remove_file(root.path().join("entry.msync")).unwrap();
            fs::remove_file(root.path().join("formatted.msync")).unwrap();
        }
        fs::write(input.join("codex.prompt"), payload).unwrap();
        success(root.path(), &args);
        for name in [
            "string-string",
            "string-path",
            "path-string",
            "path-path",
            "native-path",
        ] {
            let path = root.path().join(name);
            assert_eq!(
                fs::read_to_string(&path).unwrap(),
                payload,
                "{args:?}: {name}"
            );
            fs::remove_file(path).unwrap();
        }
    }
}

#[test]
fn compiled_literal_bytes_do_not_reparse_legacy_debug_source_or_json_escapes() {
    use msync::endpoint::{Backend, Location};
    use msync::hir::{Input, Instruction, InstructionId, InstructionKind, Output, Program, Region};
    use msync::language::DestinationMode;
    use msync::object::ObjectModule;
    use msync::relocatable::{Anchor, Path as RelocatablePath, Relocation, Slot};
    use msync::source::{InstructionOrigin, Source, SourceMap};
    use msync::syntax::Span;

    let root = workspace();
    // An old compiler decoded these actual control bytes before serializing.
    // Its debug source intentionally retains the old backslash spellings.
    let legacy_source = Source::new(
        "deleted-legacy.msync",
        "\"old\\tvalue\\n\\\\tail\\r\\n\" -> legacy.out\n",
    );
    let payload = "old\tvalue\n\\tail\r\n";
    let site = legacy_source.at(Span::default());
    let origin = InstructionOrigin {
        site: site.clone(),
        definition: site.clone(),
        expansion: Vec::new(),
        operands: Some((site.clone(), site)),
    };
    let instruction = Instruction {
        id: InstructionId(0),
        span: Span::default(),
        expansion: Vec::new(),
        kind: InstructionKind::Transfer {
            source: Input::Literal(Arc::from(payload)),
            destination: Output::File {
                location: Location {
                    backend: Backend::Local,
                    path: "producer/legacy.out".into(),
                },
                mode: DestinationMode::Exact,
            },
        },
    };
    let object = ObjectModule::from_parts(
        Program {
            body: Region {
                instructions: vec![instruction],
            },
        },
        vec![Relocation {
            instruction: InstructionId(0),
            slot: Slot::Destination,
            path: RelocatablePath::new(Anchor::WorkingDirectory, "legacy.out"),
        }],
        Vec::new(),
        legacy_source,
        SourceMap::default(),
        BTreeMap::from([(InstructionId(0), origin)]),
        BTreeMap::new(),
        "deleted-legacy.msync".into(),
    )
    .unwrap();
    let encoded = object.encode().unwrap();
    let decoded = ObjectModule::decode(&encoded).unwrap();
    assert_eq!(
        decoded.encode().unwrap(),
        encoded,
        "wire JSON escaping stays byte-preserving"
    );
    fs::write(root.path().join("legacy.mobj"), encoded).unwrap();
    assert!(!root.path().join("deleted-legacy.msync").exists());
    success(root.path(), &["run", "legacy.mobj"]);
    assert_eq!(
        fs::read(root.path().join("legacy.out")).unwrap(),
        payload.as_bytes()
    );
}

#[test]
fn repl_command_lines_inside_multiline_strings_are_data_not_effects() {
    let root = workspace();
    fs::create_dir(root.path().join("moved")).unwrap();
    let payload = "header\n/quit\n/reset\n/cd moved\n/help\n/pwd\nfooter";
    let source = format!(
        "marker := \"retained binding\"\n{} -> literal.out\nmarker -> after.out\n/quit\n",
        quoted(payload)
    );
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let mut child = Command::new(executable)
        .current_dir(root.path())
        .args(["--no-config", "--color", "never", "repl"])
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
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(
        fs::read(root.path().join("literal.out")).unwrap(),
        payload.as_bytes()
    );
    assert_eq!(
        fs::read(root.path().join("after.out")).unwrap(),
        b"retained binding"
    );
    assert!(!root.path().join("moved/literal.out").exists());
    assert!(!root.path().join("moved/after.out").exists());
}
