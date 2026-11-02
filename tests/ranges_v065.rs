//! Ordered scalar ranges preserve inferred unions across source-free execution.

#[allow(
    dead_code,
    reason = "The shared bounded process fixture also exposes interactive methods."
)]
#[path = "support/journey_process.rs"]
mod journey_process;

use std::collections::BTreeSet;
use std::fs;
use std::io::Write;
use std::net::{Shutdown, TcpListener};
use std::path::{Path, PathBuf};
use std::process::Output;
use std::time::{Duration, Instant};

use journey_process::Process;
use msync::compiler::{CompileContext, structured::Session};
use msync::hir::control::Type;
use proptest::prelude::*;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/v065");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("ranges-")
        .tempdir_in(root)
        .unwrap()
}

fn invoke(root: &Path, args: &[&str]) -> Output {
    Process::start(Process::command(root, args)).finish()
}

fn success(output: Output) {
    assert!(
        output.status.success(),
        "stdout={} stderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

fn compile(source: &str) -> Result<msync::hir::Program, Vec<msync::syntax::Report>> {
    Session::new().compile(
        &msync::language::parse(source).unwrap(),
        &CompileContext {
            pwd: PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join(".temp/v065/range-static-never-created"),
            source_file: None,
        },
    )
}

fn scalar_type(index: u8) -> Type {
    match index % 3 {
        0 => Type::Bool,
        1 => Type::String,
        _ => Type::Path,
    }
}

#[test]
fn compact_unions_preserve_scalar_wire_forms_and_reject_noncanonical_members() {
    assert!(std::mem::size_of::<Type>() <= 2);
    assert_eq!(serde_json::to_string(&Type::Bool).unwrap(), "\"bool\"");
    let union = Type::join([Type::Path, Type::Bool, Type::String, Type::Bool]).unwrap();
    let json = serde_json::to_string(&union).unwrap();
    assert_eq!(json, "{\"union\":[\"bool\",\"string\",\"path\"]}");
    assert_eq!(serde_json::from_str::<Type>(&json).unwrap(), union);
    for invalid in [
        "{\"union\":[]}",
        "{\"union\":[\"bool\"]}",
        "{\"union\":[\"bool\",\"bool\"]}",
        "{\"union\":[\"path\",\"bool\"]}",
        "{\"union\":[\"bool\",\"space\"]}",
        "{\"union\":[\"bool\",{\"union\":[\"string\",\"path\"]}]}",
    ] {
        assert!(
            serde_json::from_str::<Type>(invalid).is_err(),
            "accepted {invalid}"
        );
    }
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(64))]
    #[test]
    fn inferred_union_is_order_independent_idempotent_and_accepts_every_member(indices in prop::collection::vec(0_u8..3, 0..32)) {
        let types: Vec<_> = indices.into_iter().map(scalar_type).collect();
        let inferred = Type::join(types.clone());
        prop_assert_eq!(&inferred, &Type::join(types.iter().rev().cloned()));
        if let Some(inferred) = inferred {
            prop_assert!(inferred.is_range_item());
            prop_assert_eq!(Type::join([inferred.clone(), inferred.clone()]), Some(inferred.clone()));
            for ty in types.into_iter().collect::<BTreeSet<_>>() {
                prop_assert!(inferred.accepts(&ty));
            }
        }
    }
}

#[test]
fn boolean_lists_expand_functions_and_preserve_iteration_order() {
    let root = workspace();
    fs::write(root.path().join("entry.msync"),
        "boolean identity(value):\n    return value\nitems := [identity(true), false, identity(true), false]\nif items | $view::front:\n    \"true\" -> first\nelse:\n    \"wrong\" -> first\nfor value in items:\n    if value:\n        \"true\" -> last\n    else:\n        \"false\" -> last\n").unwrap();
    success(invoke(root.path(), &["check", "entry.msync"]));
    assert!(!root.path().join("first").exists());
    success(invoke(root.path(), &["run", "entry.msync"]));
    assert_eq!(fs::read(root.path().join("first")).unwrap(), b"true");
    assert_eq!(fs::read(root.path().join("last")).unwrap(), b"false");
}

#[test]
fn mixed_lists_specialize_ordinary_callbacks_and_survive_object_and_ir() {
    let root = workspace();
    fs::write(root.path().join("entry.msync"),
        "symbol text(value):\n    return \"expanded\"\nboolean yes(value):\n    return true\napi => $view\nitems := [false, \"middle\", $pwd::\"spaced path\"]\nselected := items | api::filter(yes) | api::transform(text)\nfor value in selected:\n    value -> result\nif items | $view::contains(false):\n    \"bool\" -> found_bool\nif items | $view::contains(\"middle\"):\n    \"string\" -> found_string\nif items | $view::contains($pwd::\"spaced path\"):\n    \"path\" -> found_path\n").unwrap();
    success(invoke(
        root.path(),
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    success(invoke(
        root.path(),
        &["emit-ir", "entry.msync", "-o", "program.json"],
    ));
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        success(invoke(root.path(), &args));
        for (name, expected) in [
            ("result", "expanded"),
            ("found_bool", "bool"),
            ("found_string", "string"),
            ("found_path", "path"),
        ] {
            assert_eq!(
                fs::read_to_string(root.path().join(name)).unwrap(),
                expected
            );
            fs::remove_file(root.path().join(name)).unwrap();
        }
    }
}

#[test]
fn literal_range_items_and_callbacks_obey_their_inferred_types() {
    for source in [
        "items := [$local, true]\n",
        "items := [[true]]\n",
        "for item in [true, \"text\"]:\n    if item:\n        \"wrong\" -> output\n",
        "boolean identity(item):\n    return item\nitems := [true, \"text\"] | $view::filter(identity)\n",
    ] {
        assert!(compile(source).is_err(), "unexpectedly accepted {source}");
    }
    assert!(compile("boolean identity(item):\n    return item\nitems := [true, false] | $view::filter(identity)\n").is_ok());
}

#[test]
fn range_publication_retains_its_causal_instruction_context() {
    let root = workspace();
    fs::write(root.path().join("a"), b"first").unwrap();
    fs::write(root.path().join("b"), b"second").unwrap();
    fs::write(root.path().join("entry.msync"), "[a, b] -> output/\n").unwrap();
    success(invoke(
        root.path(),
        &["run", "entry.msync", "--trace", "trace.jsonl"],
    ));
    assert_eq!(fs::read(root.path().join("output/a")).unwrap(), b"first");
    assert_eq!(fs::read(root.path().join("output/b")).unwrap(), b"second");
    let records: Vec<serde_json::Value> = fs::read_to_string(root.path().join("trace.jsonl"))
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect();
    let spans: std::collections::BTreeMap<_, _> = records
        .iter()
        .filter(|record| record["kind"] == "span")
        .map(|record| (record["span_id"].as_str().unwrap(), record))
        .collect();
    let copy = spans
        .values()
        .find(|record| record["name"] == "copy")
        .expect("copy span");
    let mut ancestor = *copy;
    let mut found_instruction = false;
    let mut visited = BTreeSet::new();
    while let Some(parent) = ancestor["parent_span_id"].as_str() {
        assert!(visited.insert(parent), "cyclic trace ancestry");
        ancestor = spans[parent];
        assert_eq!(ancestor["trace_id"], copy["trace_id"]);
        found_instruction |= ancestor["name"] == "interpreter.instruction"
            && ancestor["attributes"]["instruction.kind"] == "range_transfer";
    }
    assert!(
        found_instruction,
        "range publication lost its instruction parent: {records:?}"
    );
}

#[test]
fn an_empty_range_does_not_wait_for_a_destination_it_will_not_touch() {
    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    fs::write(
        root.path().join("entry.msync"),
        format!(
            "wire => tcp://{}\nrelease wire::/ -> held\n[] -> held/\n\"after\" -> marker\n",
            listener.local_addr().unwrap(),
        ),
    )
    .unwrap();
    let mut child = Process::start(Process::command(root.path(), &["run", "entry.msync"]));
    let deadline = Instant::now() + Duration::from_secs(10);
    let mut stream = loop {
        match listener.accept() {
            Ok((stream, _)) => break stream,
            Err(error)
                if error.kind() == std::io::ErrorKind::WouldBlock && Instant::now() < deadline =>
            {
                std::thread::sleep(Duration::from_millis(5));
            }
            Err(error) => panic!("background source did not connect: {error}"),
        }
    };
    stream.write_all(b"held bytes").unwrap();
    let deadline = Instant::now() + Duration::from_secs(3);
    let marker_before_eof = loop {
        if root.path().join("marker").exists() {
            break true;
        }
        if Instant::now() >= deadline {
            break false;
        }
        std::thread::sleep(Duration::from_millis(5));
    };
    stream.shutdown(Shutdown::Write).unwrap();
    success(child.finish());
    assert!(
        marker_before_eof,
        "an empty range acquired an untouched destination"
    );
    assert_eq!(fs::read(root.path().join("marker")).unwrap(), b"after");
    assert_eq!(fs::read(root.path().join("held")).unwrap(), b"held bytes");
}
