//! Public v0.6.5 journeys derive expectations from types, effects and file bytes.

#[path = "support/journey_process.rs"]
mod journey_process;

use std::fs;
use std::io::Write;
use std::net::{Shutdown, TcpListener, TcpStream};
use std::path::Path;
use std::process::Output;
use std::thread;
use std::time::{Duration, Instant};

use journey_process::Process;
use msync::compiler::{CompileContext, structured::Session};
use msync::endpoint::Backend;
use msync::hir::{BarrierKind, Input, Instruction, InstructionId, InstructionKind};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let root = tempfile::Builder::new()
        .prefix("acceptance-v065-")
        .tempdir_in(root)
        .unwrap();
    fs::create_dir(root.path().join("home")).unwrap();
    root
}

fn invoke(root: &Path, args: &[&str]) -> Output {
    Process::start(Process::command(root, args)).finish()
}

fn success(output: Output) -> Output {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    output
}

fn write_source(root: &Path, source: &str) {
    fs::write(root.join("entry.msync"), source).unwrap();
}

fn assert_outputs(root: &Path, expected: &[(&str, &[u8])]) {
    for (name, bytes) in expected {
        assert_eq!(fs::read(root.join(name)).unwrap(), *bytes, "{name}");
    }
}

fn remove_outputs(root: &Path, expected: &[(&str, &[u8])]) {
    for (name, _) in expected {
        fs::remove_file(root.join(name)).unwrap();
    }
}

fn source_object_and_ir(root: &Path, source: &str, expected: &[(&str, &[u8])]) {
    write_source(root, source);
    for args in [
        ["check", "entry.msync"].as_slice(),
        ["plan", "entry.msync"].as_slice(),
        ["run", "entry.msync", "--dry-run"].as_slice(),
    ] {
        success(invoke(root, args));
    }
    for (name, _) in expected {
        assert!(!root.join(name).exists(), "preview created {name}");
    }
    success(invoke(
        root,
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    success(invoke(
        root,
        &["emit-ir", "entry.msync", "-o", "program.json"],
    ));
    success(invoke(root, &["run", "entry.msync"]));
    assert_outputs(root, expected);
    fs::remove_file(root.join("entry.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        remove_outputs(root, expected);
        success(invoke(root, &args));
        assert_outputs(root, expected);
    }
}

#[test]
fn qualified_symbol_result_keeps_remote_scope_and_deferred_home_without_wsl_io() {
    let root = workspace();
    write_source(
        root.path(),
        "symbol codex(file):\n    return ~/.codex/file\nsymbol differently_named(value):\n    return codex(value)\nconfig := $wsl::codex(config.toml)\nconfig -> output\nother := $wsl::differently_named(config.toml)\nother -> alternate\n",
    );
    success(invoke(root.path(), &["check", "entry.msync"]));
    success(invoke(root.path(), &["run", "entry.msync", "--dry-run"]));
    success(invoke(
        root.path(),
        &["emit-ir", "entry.msync", "-o", "program.json"],
    ));
    assert!(!root.path().join("output").exists());
    assert!(!root.path().join("alternate").exists());
    let artifact = msync::ir::Artifact::from_json(
        &fs::read_to_string(root.path().join("program.json")).unwrap(),
    )
    .unwrap();
    let msync::ir::Artifact::Structured(ir) = artifact else {
        panic!("emit-ir did not retain the structured execution model");
    };
    let context = CompileContext {
        pwd: root.path().to_owned(),
        source_file: Some(root.path().join("entry.msync")),
    };
    let bound = ir.contextualize(&context, &mut Session::new()).unwrap();
    let sources = bound
        .program
        .body
        .instructions
        .iter()
        .filter_map(|node| match &node.kind {
            InstructionKind::Transfer {
                source: Input::File { location, .. },
                ..
            } => Some(location),
            _ => None,
        })
        .collect::<Vec<_>>();
    assert_eq!(sources.len(), 2, "both calls must retain file operands");
    let local_home = root.path().join("home");
    for source in sources {
        assert_eq!(source.backend, Backend::Wsl);
        assert!(source.path.ends_with(".codex/config.toml"), "{source:?}");
        assert!(
            !source.path.contains(local_home.to_string_lossy().as_ref()),
            "a remote function result captured local HOME: {source:?}"
        );
    }
}

#[test]
fn inferred_join_traits_and_scope_returning_functions_execute_from_source_free_artifacts() {
    let root = workspace();
    fs::create_dir(root.path().join("inputs")).unwrap();
    fs::write(root.path().join("inputs/entry.txt"), b"joined bytes").unwrap();
    fs::create_dir(root.path().join("input directory")).unwrap();
    fs::write(
        root.path().join("input directory/a file 可莉.txt"),
        b"scoped bytes",
    )
    .unwrap();
    fs::create_dir(root.path().join("home/.codex")).unwrap();
    fs::write(root.path().join("home/.codex/config.toml"), b"home bytes").unwrap();
    source_object_and_ir(
        root.path(),
        "symbol join(left, right):\n    return left/right\nsymbol where():\n    return $pwd\nsymbol codex(file):\n    return ~/.codex/file\nsymbol path_text():\n    return \"~/.codex/config.toml\"\njoin(\"inputs\", \"entry.txt\") -> string-string\njoin(inputs, \"entry.txt\") -> path-string\njoin(\"inputs\", entry.txt) -> string-path\njoin(inputs, entry.txt) -> path-path\n($local::where())::\"input directory\"/\"a file 可莉.txt\" -> scope-function\n($local::$pwd)::\"input directory\"/\"a file 可莉.txt\" -> nested-scope\n$local::codex(config.toml) -> home-function\npath_text() -> quoted-tilde-data\n$local::path_text() -> quoted-home-function\n",
        &[
            ("string-string", b"joined bytes"),
            ("path-string", b"joined bytes"),
            ("string-path", b"joined bytes"),
            ("path-path", b"joined bytes"),
            ("scope-function", b"scoped bytes"),
            ("nested-scope", b"scoped bytes"),
            ("home-function", b"home bytes"),
            ("quoted-tilde-data", b"~/.codex/config.toml"),
            ("quoted-home-function", b"home bytes"),
        ],
    );
}

#[test]
fn boolean_literal_ranges_expand_nested_functions_and_preserve_order_through_views() {
    let root = workspace();
    fs::write(root.path().join("switch"), b"present").unwrap();
    source_object_and_ir(
        root.path(),
        "boolean flip(flag):\n    boolean identity(value):\n        return value\n    return not identity(flag)\nready = exists switch\nstate = \"start\"\nfor flag in [ready, false, ready] | $view::transform(flip):\n    if state == \"start\" and not flag:\n        state = \"first\"\n    else if state == \"first\" and flag:\n        state = \"second\"\n    else if state == \"second\" and not flag:\n        state = \"done\"\n    else:\n        state = \"wrong\"\nstate -> order\nif [false, flip(false)] | $view::any_of(flip):\n    \"yes\" -> reduced\nelse:\n    \"wrong\" -> reduced\n",
        &[("order", b"done"), ("reduced", b"yes")],
    );
}

#[test]
fn string_and_scoped_path_lists_preserve_insertion_order_not_directory_sort_order() {
    let root = workspace();
    for (name, bytes) in [
        ("first", b"one".as_slice()),
        ("second item", b"two".as_slice()),
        ("third", b"three".as_slice()),
    ] {
        fs::write(root.path().join(name), bytes).unwrap();
    }
    source_object_and_ir(
        root.path(),
        "symbol identity(value):\n    symbol inner(item):\n        return item\n    return inner(value)\nsymbol path(name):\n    return $pwd::name\nsecond = \"first\"\nstate = \"start\"\nfor item in [identity(\"third\"), second, identity(\"second\")]:\n    if state == \"start\" and item == \"third\":\n        state = \"third\"\n    else if state == \"third\" and item == \"first\":\n        state = \"first\"\n    else if state == \"first\" and item == \"second\":\n        state = \"done\"\n    else:\n        state = \"wrong\"\nstate -> string-order\nfiles := [path(\"third\"), first, $local::\"second item\"]\n(files | $view::front) -> first-bytes\n(files | $view::back) -> last-bytes\n",
        &[
            ("string-order", b"done"),
            ("first-bytes", b"three"),
            ("last-bytes", b"two"),
        ],
    );
}

#[test]
fn mixed_scalar_lists_support_total_callbacks_without_opening_unused_paths() {
    let root = workspace();
    source_object_and_ir(
        root.path(),
        "symbol single(value):\n    return value\nsymbol constant(item):\n    return \"mapped\"\nboolean accepts(item):\n    return true\nitems := [true, \"literal\", $pwd::missing]\nodd = false\nfor item in items | $view::filter(accepts) | $view::transform(constant):\n    odd = not odd\nif odd:\n    \"three items\" -> count\nelse:\n    \"wrong\" -> count\nif items | $view::transform(constant) | $view::contains(\"mapped\"):\n    \"yes\" -> mapped\nelse:\n    \"wrong\" -> mapped\nsingle(\"ordinary user function\") -> lexical\nfor value in $view::single(\"builtin API\"):\n    value -> builtin\n",
        &[
            ("count", b"three items"),
            ("mapped", b"yes"),
            ("lexical", b"ordinary user function"),
            ("builtin", b"builtin API"),
        ],
    );
    assert!(!root.path().join("missing").exists());
}

#[test]
fn path_string_union_uses_the_same_join_trait_through_source_free_range_callbacks() {
    let root = workspace();
    for (directory, bytes) in [
        ("first directory", b"first bytes".as_slice()),
        ("second directory", b"second bytes".as_slice()),
    ] {
        fs::create_dir(root.path().join(directory)).unwrap();
        fs::write(root.path().join(directory).join("entry.txt"), bytes).unwrap();
    }
    source_object_and_ir(
        root.path(),
        "symbol leaf(base):\n    return base / \"entry.txt\"\neven = true\nfor file in [\"first directory\", $pwd::\"second directory\"] | $view::transform(leaf):\n    even = not even\n    file -> selected\nif even:\n    \"two\" -> count\nelse:\n    \"wrong\" -> count\n",
        &[("selected", b"second bytes"), ("count", b"two")],
    );
}

#[test]
fn unsupported_operator_and_range_domains_fail_before_any_earlier_transfer() {
    for invalid in [
        "item := true / path\n",
        "item := path / false\n",
        "item := \"not scope\"::path\n",
        "item := $pwd::true\n",
        "if release \"not bool\":\n    \"wrong\" -> branch\n",
        "if acquire path:\n    \"wrong\" -> branch\n",
        "if acqrel \"false\":\n    \"wrong\" -> branch\n",
        "items := [[true]]\nfor item in items:\n    \"wrong\" -> branch\n",
        "Program => $path::not-a-real-executable()\nitems := [Program]\n",
        "Program => $path::not-a-real-executable()\nitems := [Program::/]\n",
        "for item in [true, \"false\"]:\n    if item:\n        \"wrong\" -> branch\n",
        "boolean negate(item):\n    return not item\nitems := [true, \"false\"] | $view::filter(negate)\n",
        "symbol leaf(item):\n    return item / \"child\"\nitems := [true, \"directory\"] | $view::transform(leaf)\n",
        "items := [true, false]\nitems -> output/\n",
        "items := [true, \"literal\", $pwd::missing]\nitems -> output/\n",
    ] {
        let root = workspace();
        write_source(
            root.path(),
            &format!("\"must not publish\" -> sentinel\n{invalid}"),
        );
        for action in ["check", "run"] {
            let output = invoke(root.path(), &[action, "entry.msync"]);
            assert!(
                !output.status.success(),
                "{action} accepted:\n{invalid}\nstdout={}\nstderr={}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            );
            assert!(
                !root.path().join("sentinel").exists(),
                "an invalid typed unit published an earlier statement"
            );
            assert!(!root.path().join("branch").exists());
            assert!(!root.path().join("output").exists());
        }
    }
}

fn accept(listener: &TcpListener) -> TcpStream {
    let deadline = Instant::now() + Duration::from_secs(15);
    loop {
        match listener.accept() {
            Ok((peer, _)) => return peer,
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                assert!(Instant::now() < deadline, "released source never connected");
                thread::sleep(Duration::from_millis(5));
            }
            Err(error) => panic!("source acceptance failed: {error}"),
        }
    }
}

fn published(path: &Path, expected: &[u8]) {
    let deadline = Instant::now() + Duration::from_secs(15);
    loop {
        match fs::read(path) {
            Ok(bytes) if bytes == expected => return,
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(error) => panic!("cannot inspect publication {}: {error}", path.display()),
        }
        assert!(
            Instant::now() < deadline,
            "selected branch never published {}",
            path.display()
        );
        thread::sleep(Duration::from_millis(5));
    }
}

fn remove_identity_and_source_sites(value: &mut serde_json::Value) {
    match value {
        serde_json::Value::Object(fields) => {
            fields.remove("span");
            fields.remove("expansion");
            fields.remove("id");
            for child in fields.values_mut() {
                remove_identity_and_source_sites(child);
            }
        }
        serde_json::Value::Array(values) => {
            for child in values {
                remove_identity_and_source_sites(child);
            }
        }
        _ => {}
    }
}

#[test]
fn conditional_modifiers_preserve_compiled_directional_scheduling_of_the_whole_if() {
    for (modifier, asynchronous, awaited) in [
        ("release", true, false),
        ("acquire", false, true),
        ("acqrel", true, true),
    ] {
        let root = workspace();
        let body =
            "exists switch:\n    \"selected\" -> branch\nelse:\n    \"alternate\" -> branch\n";
        let source = format!("\"before\" -> before\nif {modifier} {body}\"after\" -> after\n");
        let parsed = msync::language::parse(&source).unwrap();
        let msync::language::StatementKind::Scheduled {
            asynchronous: actual_async,
            awaited: actual_awaited,
            statement,
        } = &parsed.statements[1].kind
        else {
            panic!("{modifier} lost its directional scheduling wrapper");
        };
        assert_eq!((*actual_async, *actual_awaited), (asynchronous, awaited));
        assert!(matches!(
            &statement.kind,
            msync::language::StatementKind::If { else_body, .. } if else_body.len() == 1
        ));
        let plain = msync::language::parse(&format!(
            "\"before\" -> before\nif {body}\"after\" -> after\n"
        ))
        .unwrap();
        let context = CompileContext {
            pwd: root.path().to_owned(),
            source_file: None,
        };
        let mut current =
            serde_json::to_value(Session::new().compile(&parsed, &context).unwrap()).unwrap();
        let mut established = Session::new().compile(&plain, &context).unwrap();
        let next_id = msync::hir::walk::instructions(&established.body)
            .iter()
            .map(|node| node.id.0)
            .max()
            .unwrap()
            + 1;
        for (enabled, kind, id) in [
            (asynchronous, BarrierKind::Before, next_id),
            (awaited, BarrierKind::After, next_id + 1),
        ] {
            if !enabled {
                continue;
            }
            let position = established
                .body
                .instructions
                .iter()
                .position(|node| {
                    matches!(
                        &node.kind,
                        InstructionKind::Control(
                            msync::hir::control::ControlInstruction::If { .. }
                        )
                    )
                })
                .unwrap();
            let position = position + usize::from(kind == BarrierKind::After);
            established.body.instructions.insert(
                position,
                Instruction {
                    id: InstructionId(id),
                    kind: InstructionKind::Barrier { kind },
                    span: Default::default(),
                    expansion: Vec::new(),
                },
            );
        }
        msync::hir::rewrite::normalize_fences(&mut established.body);
        let mut established = serde_json::to_value(established).unwrap();
        remove_identity_and_source_sites(&mut current);
        remove_identity_and_source_sites(&mut established);
        assert_eq!(
            current, established,
            "{modifier} changed directional fences"
        );
    }
}

#[test]
fn conditional_scheduling_does_not_globally_join_an_unrelated_released_flow() {
    for (modifier, present) in [
        ("release", true),
        ("release", false),
        ("acquire", true),
        ("acquire", false),
        ("acqrel", true),
        ("acqrel", false),
    ] {
        let root = workspace();
        if present {
            fs::write(root.path().join("switch"), b"present").unwrap();
        }
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        listener.set_nonblocking(true).unwrap();
        let mut repl = Process::start(Process::command(root.path(), &["repl", "--color", "never"]));
        repl.send(
            format!(
                "wire => tcp://{}\nrelease wire::/ -> held-output\n",
                listener.local_addr().unwrap()
            )
            .as_bytes(),
        );
        let mut peer = accept(&listener);
        peer.write_all(b"held source bytes").unwrap();
        repl.until(|stdout, stderr| {
            assert!(!stderr.contains("error:"), "{stderr}");
            stdout.contains("running in background")
        });
        repl.send(
            format!(
                "if {modifier} exists switch:\n    \"selected\" -> branch\nelse:\n    \"alternate\" -> branch\n\n/help\n"
            )
            .as_bytes(),
        );
        repl.until(|stdout, stderr| {
            assert!(!stderr.contains("error:"), "{stderr}");
            stdout.contains("REPL commands")
        });
        let expected: &[u8] = if present { b"selected" } else { b"alternate" };
        published(&root.path().join("branch"), expected);
        assert!(
            !root.path().join("held-output").exists(),
            "{modifier} published a finite source before EOF"
        );
        peer.shutdown(Shutdown::Write).unwrap();
        drop(peer);
        repl.until(|stdout, stderr| {
            assert!(!stderr.contains("error:"), "{stderr}");
            stdout.contains("job 1: completed")
        });
        repl.send(b"/quit\n");
        success(repl.finish());
        assert_eq!(
            fs::read(root.path().join("held-output")).unwrap(),
            b"held source bytes"
        );
    }
}

#[test]
fn implicit_regex_and_literal_path_ranges_merge_existing_members_and_retry() {
    for range in ["inputs/<.+>", "[$pwd::inputs/a, $pwd::inputs/b]"] {
        let root = workspace();
        fs::create_dir(root.path().join("inputs")).unwrap();
        fs::write(root.path().join("inputs/a"), b"new a").unwrap();
        fs::write(root.path().join("inputs/b"), b"new b").unwrap();
        fs::create_dir(root.path().join("output")).unwrap();
        fs::write(root.path().join("output/b"), b"old b").unwrap();
        fs::write(root.path().join("output/unrelated"), b"keep").unwrap();
        write_source(root.path(), &format!("{range} -> output/\n"));
        success(invoke(root.path(), &["run", "entry.msync"]));
        assert_outputs(
            root.path(),
            &[
                ("output/a", b"new a"),
                ("output/b", b"new b"),
                ("output/unrelated", b"keep"),
            ],
        );
        fs::write(root.path().join("inputs/b"), b"retry b").unwrap();
        success(invoke(root.path(), &["run", "entry.msync"]));
        assert_outputs(
            root.path(),
            &[
                ("output/a", b"new a"),
                ("output/b", b"retry b"),
                ("output/unrelated", b"keep"),
            ],
        );
    }
}

#[test]
fn scope_range_members_execute_through_source_free_artifacts() {
    let root = workspace();
    source_object_and_ir(
        root.path(),
        "items := [$local]\nfor endpoint in items:\n    \"scope member\" -> endpoint::branch\n",
        &[("branch", b"scope member")],
    );
}

#[test]
fn no_match_and_empty_ranges_do_not_create_a_destination() {
    for range in ["inputs/<no-match>", "[]"] {
        let root = workspace();
        fs::create_dir(root.path().join("inputs")).unwrap();
        fs::write(root.path().join("inputs/other"), b"not selected").unwrap();
        write_source(root.path(), &format!("{range} -> absent/\n"));
        success(invoke(root.path(), &["run", "entry.msync"]));
        assert!(!root.path().join("absent").exists());
    }
}
