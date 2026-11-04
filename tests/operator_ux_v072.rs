//! Public CLI journeys for inferred operator values and source-free execution.

use std::fs;
use std::path::Path;
use std::process::{Command, Output, Stdio};

fn workspace() -> tempfile::TempDir {
    let parent = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&parent).unwrap();
    tempfile::Builder::new()
        .prefix("operator-ux-v072-")
        .tempdir_in(parent)
        .unwrap()
}

fn invoke(root: &Path, args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_msync"))
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

#[test]
fn inferred_scope_references_copy_real_files_before_and_after_source_deletion() {
    let root = workspace();
    fs::create_dir(root.path().join(".codex")).unwrap();
    fs::write(root.path().join(".codex/codex.prompt"), b"first prompt").unwrap();
    fs::write(
        root.path().join("entry.msync"),
        concat!(
            "endpoint := $ local\n",
            "macro copy(target, item):\n",
            "    target::item -> copied.prompt\n",
            "copy($endpoint, .codex/codex.prompt)\n",
        ),
    )
    .unwrap();

    export(root.path());
    success(root.path(), &["run", "entry.msync", "--dry-run"]);
    assert!(!root.path().join("copied.prompt").exists());
    success(root.path(), &["run", "entry.msync"]);
    assert_eq!(
        fs::read(root.path().join("copied.prompt")).unwrap(),
        b"first prompt"
    );

    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for (args, payload) in [
        (["run", "program.mobj"], "object sees the current prompt"),
        (["run-ir", "program.json"], "IR sees another prompt"),
    ] {
        fs::remove_file(root.path().join("copied.prompt")).unwrap();
        fs::write(root.path().join(".codex/codex.prompt"), payload).unwrap();
        success(root.path(), &args);
        assert_eq!(
            fs::read_to_string(root.path().join("copied.prompt")).unwrap(),
            payload
        );
    }
}

#[test]
fn scope_members_and_string_references_keep_their_inferred_types_in_artifacts() {
    let root = workspace();
    fs::create_dir(root.path().join("input directory")).unwrap();
    fs::write(root.path().join("input directory/file.txt"), b"path bytes").unwrap();
    fs::write(
        root.path().join("entry.msync"),
        concat!(
            "scope profile:\n",
            "    pattern := \".*\"\n",
            "    endpoint := $local\n",
            "pattern := profile::pattern\n",
            "$pattern -> literal.txt\n",
            "true := \"named true\"\n",
            "$true -> named.txt\n",
            "base := \"input directory\"\n",
            "file := profile::endpoint::($base / \"file.txt\")\n",
            "$file -> path.txt\n",
            "ready := true\n",
            "native present(path) -> Bool = \"fs.is_regular_file\"\n",
            "if $ready and present(path.txt):\n",
            "    \"typed native result\" -> status.txt\n",
        ),
    )
    .unwrap();
    export(root.path());
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        success(root.path(), &args);
        for (name, expected) in [
            ("literal.txt", ".*"),
            ("named.txt", "named true"),
            ("path.txt", "path bytes"),
            ("status.txt", "typed native result"),
        ] {
            assert_eq!(
                fs::read_to_string(root.path().join(name)).unwrap(),
                expected,
                "{args:?}: {name}"
            );
            fs::remove_file(root.path().join(name)).unwrap();
        }
    }
}

#[test]
fn generic_string_path_join_matrix_executes_from_source_object_and_ir() {
    let root = workspace();
    fs::create_dir(root.path().join("input directory")).unwrap();
    fs::write(
        root.path().join("input directory/file.txt"),
        b"joined bytes",
    )
    .unwrap();
    fs::write(
        root.path().join("entry.msync"),
        concat!(
            "string_base := \"input directory\"\n",
            "string_child := \"file.txt\"\n",
            "path_base := $local::\"input directory\"\n",
            "path_child := file.txt\n",
            "ss := string_base / string_child\n",
            "sp := string_base / path_child\n",
            "ps := path_base / string_child\n",
            "pp := path_base / path_child\n",
            "$ss -> string-string.txt\n",
            "$sp -> string-path.txt\n",
            "$ps -> path-string.txt\n",
            "$pp -> path-path.txt\n",
            "literal := \"input directory/file.txt\"\n",
            "$literal -> literal.txt\n",
            "native present(path) = \"fs.is_regular_file\"\n",
            "if present($ss) and present($sp) and present($ps) and present($pp):\n",
            "    \"all joins are paths\" -> native.txt\n",
        ),
    )
    .unwrap();
    export(root.path());
    for args in [
        ["run", "entry.msync"],
        ["run", "program.mobj"],
        ["run-ir", "program.json"],
    ] {
        success(root.path(), &args);
        for name in [
            "string-string.txt",
            "string-path.txt",
            "path-string.txt",
            "path-path.txt",
        ] {
            assert_eq!(
                fs::read(root.path().join(name)).unwrap(),
                b"joined bytes",
                "{args:?}: {name}"
            );
            fs::remove_file(root.path().join(name)).unwrap();
        }
        for (name, expected) in [
            ("literal.txt", "input directory/file.txt"),
            ("native.txt", "all joins are paths"),
        ] {
            assert_eq!(
                fs::read_to_string(root.path().join(name)).unwrap(),
                expected,
                "{args:?}: {name}"
            );
            fs::remove_file(root.path().join(name)).unwrap();
        }
        if args[1] == "entry.msync" {
            fs::remove_file(root.path().join("entry.msync")).unwrap();
        }
    }
}

#[test]
fn computed_paths_keep_literal_selector_spelling_through_native_calls_and_artifacts() {
    let root = workspace();
    fs::create_dir(root.path().join("selectors")).unwrap();
    fs::write(root.path().join("selectors/literal"), b"selected bytes").unwrap();
    // Angle brackets are valid ordinary filename characters on Unix. Windows
    // still checks/exports the inferred Path contract without doing invalid I/O.
    if !cfg!(windows) {
        fs::write(root.path().join("<literal>"), b"literal filename bytes").unwrap();
    }
    fs::write(
        root.path().join("entry.msync"),
        concat!(
            "literal_name := \"<literal>\"\n",
            "direct := \".\" / \"<literal>\"\n",
            "aliased := \".\" / literal_name\n",
            "qualified := $local::\".\" / literal_name\n",
            "native filename(path) = \"fs.filename\"\n",
            "native text(path) = \"fs.generic_string\"\n",
            "native_path := filename(direct)\n",
            "native_join := \".\" / native_path\n",
            "native_text := text(native_path)\n",
            "native_string_join := \".\" / native_text\n",
            "direct -> direct.txt\n",
            "aliased -> aliased.txt\n",
            "qualified -> qualified.txt\n",
            "native_join -> native-path.txt\n",
            "native_string_join -> native-string.txt\n",
            "destination_name := \"<copied>\"\n",
            "direct -> \".\" / destination_name\n",
            "selectors/<literal> -> selected/\n",
        ),
    )
    .unwrap();
    export(root.path());
    if cfg!(windows) {
        return;
    }
    for args in [
        ["run", "entry.msync"],
        ["run", "program.mobj"],
        ["run-ir", "program.json"],
    ] {
        success(root.path(), &args);
        for name in [
            "direct.txt",
            "aliased.txt",
            "qualified.txt",
            "native-path.txt",
            "native-string.txt",
            "<copied>",
        ] {
            assert_eq!(
                fs::read(root.path().join(name)).unwrap(),
                b"literal filename bytes",
                "{args:?}: {name}"
            );
            fs::remove_file(root.path().join(name)).unwrap();
        }
        assert_eq!(
            fs::read(root.path().join("selected/literal")).unwrap(),
            b"selected bytes",
            "{args:?}: explicit source selector"
        );
        fs::remove_dir_all(root.path().join("selected")).unwrap();
        if args[1] == "entry.msync" {
            fs::remove_file(root.path().join("entry.msync")).unwrap();
        }
    }
}

#[test]
fn an_unknown_reference_is_a_source_error_not_a_filename() {
    let root = workspace();
    fs::write(root.path().join("entry.msync"), "$missing -> output\n").unwrap();
    // Even a real file with this spelling must not turn an unresolved reference
    // into an implicitly selected path.
    fs::write(root.path().join("$missing"), b"not a lexical binding").unwrap();
    let output = invoke(root.path(), &["check", "entry.msync"]);
    assert!(!output.status.success());
    let diagnostic = String::from_utf8_lossy(&output.stderr);
    assert!(diagnostic.contains("entry.msync:1:"), "{diagnostic}");
    assert!(diagnostic.contains("missing"), "{diagnostic}");
    assert!(!root.path().join("output").exists());
}

#[test]
fn mutable_references_observe_current_runtime_cells_in_source_free_artifacts() {
    let root = workspace();
    fs::write(
        root.path().join("entry.msync"),
        concat!(
            "text = \"first\"\n",
            "$text -> first.txt\n",
            "text = \"second\"\n",
            "$text -> second.txt\n",
            "ready = exists first.txt\n",
            "if $ready:\n",
            "    \"observed\" -> before.txt\n",
            "ready = false\n",
            "if not $ready:\n",
            "    \"changed\" -> after.txt\n",
        ),
    )
    .unwrap();
    export(root.path());
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        success(root.path(), &args);
        for (name, expected) in [
            ("first.txt", "first"),
            ("second.txt", "second"),
            ("before.txt", "observed"),
            ("after.txt", "changed"),
        ] {
            assert_eq!(
                fs::read_to_string(root.path().join(name)).unwrap(),
                expected,
                "{args:?}: {name}"
            );
            fs::remove_file(root.path().join(name)).unwrap();
        }
    }
}

#[test]
fn quoted_windows_base_and_bare_child_compile_without_binding_the_host() {
    let root = workspace();
    fs::write(
        root.path().join("entry.msync"),
        "\"C:\\Users\\STONE\\.codex\"/codex.prompt -> destination\n",
    )
    .unwrap();
    export(root.path());
    assert!(!root.path().join("destination").exists());
    assert!(root.path().join("program.mobj").is_file());
    assert!(root.path().join("program.json").is_file());
}

#[test]
fn invalid_operator_operands_report_the_source_and_inferred_type() {
    let root = workspace();
    for (source, operator, inferred_type) in [
        ("true / leaf -> output\n", "/", "Bool"),
        ("\"literal\"::leaf -> output\n", "::", "String"),
    ] {
        fs::write(root.path().join("entry.msync"), source).unwrap();
        let output = invoke(root.path(), &["check", "entry.msync"]);
        assert!(!output.status.success(), "accepted {source}");
        let diagnostic = String::from_utf8_lossy(&output.stderr);
        assert!(diagnostic.contains("entry.msync:1:"), "{diagnostic}");
        assert!(diagnostic.contains(operator), "{diagnostic}");
        assert!(diagnostic.contains(inferred_type), "{diagnostic}");
        assert!(!root.path().join("output").exists());
    }
}
