//! Acceptance journeys use the public CLI, real files, and reusable compiled objects.

use std::fs;
use std::io::Write;
use std::path::Path;
use std::process::{Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("cli-060-")
        .tempdir_in(root)
        .unwrap()
}

fn run(root: &Path, args: &[&str]) -> Output {
    run_with_input(root, args, None)
}

fn run_with_input(root: &Path, args: &[&str], input: Option<&[u8]>) -> Output {
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root)
        .args(args)
        .stdin(if input.is_some() {
            Stdio::piped()
        } else {
            Stdio::null()
        })
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    if let Some(input) = input {
        child.stdin.take().unwrap().write_all(input).unwrap();
    }
    let deadline = Instant::now() + Duration::from_secs(20);
    while child.try_wait().unwrap().is_none() {
        if Instant::now() >= deadline {
            child.kill().unwrap();
            let output = child.wait_with_output().unwrap();
            panic!(
                "CLI timed out: {args:?}\nstdout={}\nstderr={}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            );
        }
        thread::sleep(Duration::from_millis(10));
    }
    child.wait_with_output().unwrap()
}

fn success(output: Output) {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

fn write_script(root: &Path, source: &str) {
    fs::write(root.join("entry.msync"), source).unwrap();
}

#[test]
fn exists_observes_prior_writes_and_delete_at_runtime_without_check_effects() {
    let root = workspace();
    write_script(
        root.path(),
        "# msync: 0.6\nboolean present(path):\n    return exists path\n\"payload\" -> victim\nif present(victim):\n    \"seen\" -> before\nelse:\n    \"wrong\" -> before\ndelete \"victim\"\nif not exists victim:\n    \"gone\" -> after\nelse:\n    \"wrong\" -> after\n",
    );
    success(run(root.path(), &["check", "entry.msync"]));
    success(run(root.path(), &["run", "entry.msync", "--dry-run"]));
    for name in ["victim", "before", "after"] {
        assert!(!root.path().join(name).exists(), "check created {name}");
    }
    success(run(root.path(), &["run", "entry.msync"]));
    assert!(!root.path().join("victim").exists());
    assert_eq!(fs::read(root.path().join("before")).unwrap(), b"seen");
    assert_eq!(fs::read(root.path().join("after")).unwrap(), b"gone");
}

#[test]
fn typed_boolean_string_conditions_survive_object_and_ir_without_source() {
    let root = workspace();
    write_script(
        root.path(),
        "boolean any(a, b, c):\n    return a or b or c\nlabel = \"klee\"\nready = not false and (label >= \"kle\") and label != \"q\"\nif any(false, ready, false):\n    if exists \"switch\":\n        label -> selected\n    else:\n        \"absent\" -> selected\nelse if true:\n    \"wrong\" -> selected\nelse:\n    \"also wrong\" -> selected\n",
    );
    success(run(
        root.path(),
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    success(run(
        root.path(),
        &["emit-ir", "entry.msync", "-o", "program.json"],
    ));
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        fs::write(root.path().join("switch"), b"on").unwrap();
        success(run(root.path(), &args));
        assert_eq!(fs::read(root.path().join("selected")).unwrap(), b"klee");
        fs::remove_file(root.path().join("switch")).unwrap();
        success(run(root.path(), &args));
        assert_eq!(fs::read(root.path().join("selected")).unwrap(), b"absent");
    }
}

#[test]
fn lazy_range_pipeline_filters_real_paths_and_transforms_to_literal_data() {
    let root = workspace();
    fs::create_dir(root.path().join("inputs")).unwrap();
    for name in ["a.txt", "b.txt", "c.bin"] {
        fs::write(root.path().join("inputs").join(name), name).unwrap();
    }
    write_script(
        root.path(),
        "boolean present(path):\n    return exists path\nsymbol message(path):\n    return \"mapped\"\nfor text in inputs/<.+\\.txt> | views::filter(present) | views::transform(message) | views::take(1):\n    text -> result\nif inputs/<.+\\.txt> | ranges::any_of(present):\n    \"nonempty\" -> reduction\n",
    );
    success(run(root.path(), &["check", "entry.msync"]));
    assert!(!root.path().join("result").exists());
    success(run(root.path(), &["run", "entry.msync"]));
    assert_eq!(fs::read(root.path().join("result")).unwrap(), b"mapped");
    assert_eq!(
        fs::read(root.path().join("reduction")).unwrap(),
        b"nonempty"
    );
    success(run(
        root.path(),
        &["compile", "entry.msync", "-o", "pipeline.mobj"],
    ));
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    fs::remove_file(root.path().join("result")).unwrap();
    success(run(root.path(), &["run", "pipeline.mobj"]));
    assert_eq!(fs::read(root.path().join("result")).unwrap(), b"mapped");
}

#[test]
fn unused_regex_range_is_not_enumerated_before_an_earlier_write() {
    let root = workspace();
    write_script(
        root.path(),
        "files := inputs/<.+\\.txt>\n\"created at runtime\" -> inputs/new.txt\nfor file in files:\n    file -> copied\n",
    );
    success(run(root.path(), &["check", "entry.msync"]));
    assert!(!root.path().join("inputs").exists());
    success(run(root.path(), &["run", "entry.msync"]));
    assert_eq!(
        fs::read(root.path().join("copied")).unwrap(),
        b"created at runtime"
    );
}

#[test]
fn nested_loops_break_only_the_innermost_loop_and_yield_typed_values() {
    let root = workspace();
    write_script(
        root.path(),
        "text = loop:\n    loop:\n        break\n    break \"literal\"\npath := loop:\n    break selected\nflag = loop:\n    break true\nspace => loop:\n    break $local\nif flag:\n    text -> path\n    space::selected -> copied\nloop:\n    if exists copied:\n        break\n    \"wrong\" -> failed\n",
    );
    success(run(root.path(), &["run", "entry.msync"]));
    assert_eq!(fs::read(root.path().join("selected")).unwrap(), b"literal");
    assert_eq!(fs::read(root.path().join("copied")).unwrap(), b"literal");
    assert!(!root.path().join("failed").exists());
}

#[test]
fn nested_templates_capture_parent_globals_and_drop_only_names_not_files() {
    let root = workspace();
    write_script(
        root.path(),
        "root := output\nmacro outer():\n    global root\n    label = \"parent\"\n    macro middle():\n        macro inner():\n            global root, label\n            boolean enabled():\n                boolean nested():\n                    return true\n                return nested()\n            symbol identity(value):\n                symbol nested(item):\n                    return item\n                return nested(value)\n            if enabled():\n                identity(label) -> root\n        inner()\n    middle()\nouter()\ndrop root, outer\nif exists \"output\":\n    \"retained\" -> retained\n",
    );
    success(run(root.path(), &["run", "entry.msync"]));
    assert_eq!(fs::read(root.path().join("output")).unwrap(), b"parent");
    assert_eq!(fs::read(root.path().join("retained")).unwrap(), b"retained");
}

#[test]
fn deleting_a_directory_is_an_error_and_preserves_its_contents() {
    let root = workspace();
    fs::create_dir(root.path().join("protected")).unwrap();
    fs::write(root.path().join("protected/child"), b"keep").unwrap();
    write_script(root.path(), "delete protected\n");
    success(run(root.path(), &["check", "entry.msync"]));
    let output = run(root.path(), &["run", "entry.msync"]);
    assert!(!output.status.success());
    assert_eq!(
        fs::read(root.path().join("protected/child")).unwrap(),
        b"keep"
    );
}

#[cfg(unix)]
#[test]
fn delete_unlinks_a_symlink_without_deleting_its_target() {
    let root = workspace();
    fs::write(root.path().join("target"), b"keep").unwrap();
    std::os::unix::fs::symlink("target", root.path().join("link")).unwrap();
    write_script(root.path(), "delete link\n");
    success(run(root.path(), &["run", "entry.msync"]));
    assert!(fs::symlink_metadata(root.path().join("link")).is_err());
    assert_eq!(fs::read(root.path().join("target")).unwrap(), b"keep");
}

#[test]
fn exists_assignment_captures_one_runtime_observation_instead_of_a_thunk() {
    let root = workspace();
    write_script(
        root.path(),
        "\"payload\" -> victim\npresent = exists victim\ndelete victim\nif present:\n    \"snapshot\" -> observed\nelse:\n    \"recomputed\" -> observed\n",
    );
    success(run(root.path(), &["run", "entry.msync"]));
    assert!(!root.path().join("victim").exists());
    assert_eq!(fs::read(root.path().join("observed")).unwrap(), b"snapshot");
}

#[test]
fn unused_and_zero_take_ranges_do_not_enumerate_missing_directories() {
    let root = workspace();
    write_script(
        root.path(),
        "boolean present(path):\n    return exists path\nunused := missing/<.+>\nfor path in missing/<.+> | views::filter(present) | views::take(0):\n    \"wrong\" -> pulled\n\"done\" -> completed\n",
    );
    success(run(root.path(), &["run", "entry.msync"]));
    assert!(!root.path().join("missing").exists());
    assert!(!root.path().join("pulled").exists());
    assert_eq!(fs::read(root.path().join("completed")).unwrap(), b"done");
}

#[test]
fn moved_objects_relocate_paths_inside_control_regions_and_exists() {
    let root = workspace();
    write_script(
        root.path(),
        "if exists switch and exists \"switch\":\n    \"relocated\" -> selected\nelse:\n    \"wrong cwd\" -> selected\n",
    );
    success(run(
        root.path(),
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    let moved = root.path().join("moved");
    fs::create_dir(&moved).unwrap();
    fs::rename(root.path().join("program.mobj"), moved.join("program.mobj")).unwrap();
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    fs::write(moved.join("switch"), b"present only in moved cwd").unwrap();
    success(run(&moved, &["run", "program.mobj"]));
    assert_eq!(fs::read(moved.join("selected")).unwrap(), b"relocated");
    assert!(!root.path().join("selected").exists());
}

#[cfg(unix)]
#[test]
fn runtime_boolean_short_circuit_skips_filesystem_errors() {
    let root = workspace();
    fs::write(root.path().join("present"), b"yes").unwrap();
    std::os::unix::fs::symlink("cycle", root.path().join("cycle")).unwrap();
    write_script(
        root.path(),
        "first = exists present\nif first or exists cycle:\n    \"or skipped\" -> or-result\nsecond = exists absent\nif second and exists cycle:\n    \"wrong\" -> and-result\nelse:\n    \"and skipped\" -> and-result\n",
    );
    success(run(root.path(), &["run", "entry.msync"]));
    assert_eq!(
        fs::read(root.path().join("or-result")).unwrap(),
        b"or skipped"
    );
    assert_eq!(
        fs::read(root.path().join("and-result")).unwrap(),
        b"and skipped"
    );
}

#[test]
fn range_adaptors_preserve_order_and_reducers_have_static_path_or_bool_results() {
    let root = workspace();
    fs::create_dir(root.path().join("inputs")).unwrap();
    fs::write(root.path().join("inputs/a.txt"), b"A").unwrap();
    fs::write(root.path().join("inputs/b.txt"), b"B").unwrap();
    write_script(
        root.path(),
        "boolean present(path):\n    return exists path\nboolean never(path):\n    return false\nfiles := inputs/<.+\\.txt>\nfirst := files | ranges::front()\nlast := files | ranges::back()\nfound := files | ranges::find_if(present)\nfirst -> first-output\nlast -> last-output\nfound -> found-output\nfor file in files | views::drop(1) | views::take(1):\n    file -> dropped-output\nfor file in files | views::reverse() | views::take(1):\n    file -> reversed-output\nif (files | ranges::all_of(present)) and (files | ranges::none_of(never)) and not (files | ranges::empty()):\n    \"true\" -> reduction-output\n",
    );
    success(run(root.path(), &["run", "entry.msync"]));
    for (name, expected) in [
        ("first-output", b"A"),
        ("last-output", b"B"),
        ("found-output", b"A"),
        ("dropped-output", b"B"),
        ("reversed-output", b"B"),
    ] {
        assert_eq!(fs::read(root.path().join(name)).unwrap(), expected);
    }
    assert_eq!(
        fs::read(root.path().join("reduction-output")).unwrap(),
        b"true"
    );
}

#[test]
fn repl_retains_runtime_boolean_slots_across_submissions_and_function_captures() {
    let root = workspace();
    let source = b"\"payload\" -> victim\npresent = exists victim\nboolean captured():\n    global present\n    return present\n\ndelete victim\nlater = exists absent\nif captured() and not later:\n    \"snapshot survived\" -> observed\n\n/quit\n";
    success(run_with_input(root.path(), &["repl"], Some(source)));
    assert!(!root.path().join("victim").exists());
    assert_eq!(
        fs::read(root.path().join("observed")).unwrap(),
        b"snapshot survived"
    );
}

#[test]
fn reloading_an_object_in_repl_does_not_overwrite_existing_dynamic_slots() {
    let root = workspace();
    write_script(
        root.path(),
        "inside = exists switch\nif inside:\n    \"present\" -> object-output\nelse:\n    \"absent\" -> object-output\n",
    );
    success(run(
        root.path(),
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    fs::write(root.path().join("switch"), b"on").unwrap();
    let source = b"saved = exists switch\n/load program.mobj\ndelete switch\n/load program.mobj\nif saved:\n    \"saved\" -> saved-output\nelse:\n    \"overwritten\" -> saved-output\n\n/quit\n";
    success(run_with_input(root.path(), &["repl"], Some(source)));
    assert_eq!(
        fs::read(root.path().join("object-output")).unwrap(),
        b"absent"
    );
    assert_eq!(
        fs::read(root.path().join("saved-output")).unwrap(),
        b"saved"
    );
}

#[test]
fn mutation_loop_exits_and_retains_updated_boolean_and_string_values() {
    let root = workspace();
    write_script(
        root.path(),
        "done = false\nlabel = \"before\"\nloop:\n    if done:\n        break\n    label = \"after\"\n    done = true\nif done and label == \"after\":\n    label -> observed\nelse:\n    \"stale outer state\" -> observed\n",
    );
    success(run(root.path(), &["run", "entry.msync"]));
    assert_eq!(fs::read(root.path().join("observed")).unwrap(), b"after");
}

#[test]
fn mutation_dynamic_branch_updates_outer_values_but_keeps_fresh_bindings_local() {
    let root = workspace();
    fs::write(root.path().join("marker"), b"select runtime branch").unwrap();
    write_script(
        root.path(),
        "ready = false\nlabel = \"before\"\nif exists marker:\n    ready = true\n    label = \"after\"\n    branch_local = \"must not escape\"\nelse:\n    label = \"wrong branch\"\nif ready and label == \"after\":\n    label -> observed\nelse:\n    \"stale outer state\" -> observed\n",
    );
    let output = run_with_input(
        root.path(),
        &["repl"],
        Some(b"/load entry.msync\n/bindings\n/quit\n"),
    );
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(fs::read(root.path().join("observed")).unwrap(), b"after");
    let bindings = String::from_utf8_lossy(&output.stdout);
    assert!(!bindings.contains("branch_local"), "{bindings}");
}

#[test]
fn mutation_object_and_ir_retain_loop_state_after_source_removal() {
    let root = workspace();
    write_script(
        root.path(),
        "done = false\nlabel = \"before\"\nloop:\n    if done:\n        break\n    label = \"serialized mutation\"\n    done = true\nif done and label == \"serialized mutation\":\n    label -> observed\nelse:\n    \"stale state\" -> observed\n",
    );
    success(run(
        root.path(),
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    success(run(
        root.path(),
        &["emit-ir", "entry.msync", "-o", "program.json"],
    ));
    assert!(!root.path().join("observed").exists());
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        success(run(root.path(), &args));
        assert_eq!(
            fs::read(root.path().join("observed")).unwrap(),
            b"serialized mutation"
        );
        fs::remove_file(root.path().join("observed")).unwrap();
    }
}

#[test]
fn repl_cd_filename_roles_use_the_selected_cwd_without_touching_the_original() {
    let root = workspace();
    fs::write(root.path().join("marker"), b"original must remain").unwrap();
    fs::create_dir(root.path().join("target")).unwrap();
    fs::write(root.path().join("target/marker"), b"target must be removed").unwrap();
    let source = b"/cd target\nbefore = exists \"marker\"\ndelete \"marker\"\nafter = exists \"marker\"\nif before and not after:\n    \"correct cwd\" -> observed\nelse:\n    \"wrong cwd\" -> observed\n\n/quit\n";
    success(run_with_input(root.path(), &["repl"], Some(source)));
    assert_eq!(
        fs::read(root.path().join("marker")).unwrap(),
        b"original must remain"
    );
    assert!(!root.path().join("target/marker").exists());
    assert_eq!(
        fs::read(root.path().join("target/observed")).unwrap(),
        b"correct cwd"
    );
    assert!(!root.path().join("observed").exists());
}

#[test]
fn dynamic_spawn_string_arguments_use_a_loop_returned_executable_in_objects_and_ir() {
    let root = workspace();
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    write_script(
        root.path(),
        &format!(
            "exe := loop:\n    break $local::{executable}\nfor arg in ranges::of(\"--version\", \"--help\"):\n    P => exe(arg)\n    P::stdout -> \"outputs\" / arg\n"
        ),
    );
    success(run(
        root.path(),
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    success(run(
        root.path(),
        &["emit-ir", "entry.msync", "-o", "program.json"],
    ));
    assert!(!root.path().join("outputs").exists());
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        success(run(root.path(), &args));
        assert_eq!(
            fs::read_to_string(root.path().join("outputs/--version"))
                .unwrap()
                .trim(),
            concat!("msync ", env!("CARGO_PKG_VERSION"))
        );
        let help = fs::read_to_string(root.path().join("outputs/--help")).unwrap();
        assert!(help.contains("USAGE"), "{help}");
        assert!(help.contains("msync check FILE"), "{help}");
        fs::remove_dir_all(root.path().join("outputs")).unwrap();
    }
}

#[test]
fn dynamic_spawn_path_arguments_run_real_child_scripts_from_objects_and_ir() {
    let root = workspace();
    fs::create_dir(root.path().join("tasks")).unwrap();
    fs::write(
        root.path().join("tasks/first.msync"),
        "\"first child\" -> first-output\n",
    )
    .unwrap();
    fs::write(
        root.path().join("tasks/second.msync"),
        "\"second child\" -> second-output\n",
    )
    .unwrap();
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    write_script(
        root.path(),
        &format!(
            "for script in tasks/<.+\\.msync>:\n    P => $local::{executable}(\"run\", script)\n"
        ),
    );
    success(run(
        root.path(),
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    success(run(
        root.path(),
        &["emit-ir", "entry.msync", "-o", "program.json"],
    ));
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        success(run(root.path(), &args));
        assert_eq!(
            fs::read(root.path().join("first-output")).unwrap(),
            b"first child"
        );
        assert_eq!(
            fs::read(root.path().join("second-output")).unwrap(),
            b"second child"
        );
        fs::remove_file(root.path().join("first-output")).unwrap();
        fs::remove_file(root.path().join("second-output")).unwrap();
    }
}

#[cfg(unix)]
#[test]
fn dynamic_spawn_bidirectional_ports_transfer_loop_values_in_objects_and_ir() {
    let root = workspace();
    write_script(
        root.path(),
        "release P => $path::cat()\ntext = loop:\n    break \"dynamic stdin payload\"\ndestination := loop:\n    break observed\ntext -> P::stdin\nP::stdout -> destination\nacquire P\n",
    );
    success(run(
        root.path(),
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    success(run(
        root.path(),
        &["emit-ir", "entry.msync", "-o", "program.json"],
    ));
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    for args in [["run", "program.mobj"], ["run-ir", "program.json"]] {
        success(run(root.path(), &args));
        assert_eq!(
            fs::read(root.path().join("observed")).unwrap(),
            b"dynamic stdin payload"
        );
        fs::remove_file(root.path().join("observed")).unwrap();
    }
}
