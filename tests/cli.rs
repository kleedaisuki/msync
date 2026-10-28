use std::fs;
use std::io::{Read, Write};
use std::net::TcpListener;
use std::path::Path;
use std::process::{Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("cli-")
        .tempdir_in(root)
        .unwrap()
}

fn command(directory: &Path) -> Command {
    let mut command = Command::new(env!("CARGO_BIN_EXE_msync"));
    command.current_dir(directory).env_remove("NO_COLOR");
    command
}

fn invoke(directory: &Path, arguments: &[&str], input: &str) -> Output {
    invoke_command(command(directory).args(arguments), input)
}

fn invoke_command(command: &mut Command, input: &str) -> Output {
    let mut child = command
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let written = child.stdin.take().unwrap().write_all(input.as_bytes());
    let output = child.wait_with_output().unwrap();
    if let Err(cause) = written {
        assert!(
            cause.kind() == std::io::ErrorKind::BrokenPipe && !output.status.success(),
            "stdin failed unexpectedly: {cause}; stderr={}",
            String::from_utf8_lossy(&output.stderr)
        );
    }
    output
}

fn stdout(output: &Output) -> String {
    String::from_utf8(output.stdout.clone()).unwrap()
}
fn stderr(output: &Output) -> String {
    String::from_utf8(output.stderr.clone()).unwrap()
}

fn string_field<'a>(line: &'a str, name: &str) -> &'a str {
    let prefix = format!("\"{name}\":\"");
    line.split_once(&prefix)
        .unwrap()
        .1
        .split('"')
        .next()
        .unwrap()
}

fn assert_success(output: &Output) {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        stdout(output),
        stderr(output)
    );
}

#[test]
fn stdin_pipeline_checks_and_plans_without_copying_files() {
    let directory = workspace();
    fs::create_dir(directory.path().join("configs")).unwrap();
    fs::write(directory.path().join("configs/a.toml"), "configuration").unwrap();
    let script = "home := configs\nhome/a.toml -> output/a.toml\n";
    let checked = invoke(directory.path(), &["check", "-"], script);
    assert_success(&checked);
    assert_eq!(stdout(&checked), "ok\n");
    let compiled = invoke(directory.path(), &["compile", "-"], script);
    assert_success(&compiled);
    let plan = stdout(&compiled).replace("\\\\", "/");
    assert!(plan.contains("configs/a.toml"), "{plan}");
    assert!(plan.contains("output/a.toml"), "{plan}");
    assert!(!directory.path().join("output").exists());
}

#[test]
fn local_copy_journey_overwrites_then_preserves_insert_conflict() {
    let directory = workspace();
    fs::create_dir(directory.path().join("source")).unwrap();
    fs::write(
        directory.path().join("source/config.toml"),
        "new configuration",
    )
    .unwrap();
    fs::create_dir(directory.path().join("output")).unwrap();
    fs::write(
        directory.path().join("output/config.toml"),
        "old configuration",
    )
    .unwrap();
    let overwritten = invoke(
        directory.path(),
        &["run", "-"],
        "source/config.toml -> output/config.toml\n",
    );
    assert_success(&overwritten);
    assert_eq!(
        fs::read_to_string(directory.path().join("output/config.toml")).unwrap(),
        "new configuration"
    );
    fs::write(
        directory.path().join("source/config.toml"),
        "must not clobber",
    )
    .unwrap();
    let conflict = invoke(
        directory.path(),
        &["run", "-"],
        "source/config.toml -> output/\n",
    );
    assert!(!conflict.status.success());
    assert!(stderr(&conflict).contains("already exists"));
    assert!(stderr(&conflict).contains("source/config.toml -> output/"));
    assert_eq!(
        fs::read_to_string(directory.path().join("output/config.toml")).unwrap(),
        "new configuration"
    );
}

#[test]
fn selectors_match_direct_entries_and_full_names_only() {
    let directory = workspace();
    fs::create_dir_all(directory.path().join("source/nested")).unwrap();
    fs::write(directory.path().join("source/direct.toml"), "yes").unwrap();
    fs::write(directory.path().join("source/direct.toml.bak"), "no").unwrap();
    fs::write(directory.path().join("source/nested/hidden.toml"), "no").unwrap();
    let copied = invoke(
        directory.path(),
        &["run", "-"],
        "source/<.+\\.toml> -> output/\n",
    );
    assert_success(&copied);
    assert_eq!(
        fs::read_to_string(directory.path().join("output/direct.toml")).unwrap(),
        "yes"
    );
    assert!(!directory.path().join("output/direct.toml.bak").exists());
    assert!(!directory.path().join("output/nested").exists());
}

#[test]
fn dry_run_is_offline_and_never_copies() {
    let directory = workspace();
    let preview = invoke(
        directory.path(),
        &["run", "-", "--dry-run"],
        "remote => https://127.0.0.1:1/\nremote::missing -> output\n",
    );
    assert_success(&preview);
    assert!(stdout(&preview).contains("preview"));
    assert!(!directory.path().join("output").exists());
    assert!(!directory.path().join(".temp").exists());
}

#[test]
fn formatter_defaults_to_in_place_and_stdout_check_remain_read_only() {
    let directory = workspace();
    let file = directory.path().join("sample.msync");
    let original = "# preserved\nsource->destination # inline\n";
    fs::write(&file, original).unwrap();
    for flag in ["--dry-run", "-n"] {
        let result = invoke(directory.path(), &["fmt", "sample.msync", flag], "");
        assert!(!result.status.success());
        assert!(stderr(&result).contains("--check"));
        assert_eq!(fs::read_to_string(&file).unwrap(), original);
    }
    let printed = invoke(directory.path(), &["fmt", "sample.msync", "--stdout"], "");
    assert_success(&printed);
    assert!(stdout(&printed).contains("source -> destination"));
    assert!(stdout(&printed).contains("# preserved"));
    assert!(stdout(&printed).contains("# inline"));
    assert_eq!(fs::read_to_string(&file).unwrap(), original);
    assert!(
        !invoke(directory.path(), &["fmt", "sample.msync", "--check"], "")
            .status
            .success()
    );
    assert_eq!(
        fs::read_to_string(&file).unwrap(),
        original,
        "--check must never rewrite"
    );
    assert_success(&invoke(directory.path(), &["fmt", "sample.msync"], ""));
    let written = fs::read_to_string(&file).unwrap();
    assert_eq!(written, stdout(&printed));
    assert_success(&invoke(
        directory.path(),
        &["fmt", "sample.msync", "--check"],
        "",
    ));
    assert_success(&invoke(
        directory.path(),
        &["fmt", "sample.msync", "--write"],
        "",
    ));
    assert_eq!(fs::read_to_string(file).unwrap(), written);
}

#[test]
fn repl_slash_commands_keep_bindings_recover_from_errors_and_load_files() {
    let directory = workspace();
    fs::create_dir(directory.path().join("work")).unwrap();
    fs::write(directory.path().join("work/source.txt"), "payload").unwrap();
    fs::write(
        directory.path().join("work/load.msync"),
        "base/source.txt -> result.txt\n",
    )
    .unwrap();
    fs::write(
        directory.path().join("work/check.msync"),
        "transient => $wsl\nbase/source.txt -> checked.txt\n",
    )
    .unwrap();
    fs::write(
        directory.path().join("work/dirty.msync"),
        "source.txt->formatted.txt\n",
    )
    .unwrap();
    let session = "/help\n/?\n/cd work\n/pwd\nbase := .\n/bindings\n/check check.msync\n/plan load.msync\n/fmt dirty.msync\nunknown::source -> ignored\n/load load.msync\n/dry-run on\nbase/source.txt -> preview.txt\n/dry-run\n/bindings\n/reset\n/bindings\n/quit\n";
    let result = invoke(directory.path(), &["repl"], session);
    assert_success(&result);
    let out = stdout(&result);
    assert!(out.contains("REPL commands"));
    assert!(out.contains("base :="), "{out}");
    assert!(out.contains("dry-run: on"));
    assert!(out.contains("bindings cleared"));
    assert!(
        !out.contains("transient =>"),
        "check must not leak bindings: {out}"
    );
    assert!(!out.contains("msync>"), "piped REPL must not print prompts");
    assert!(stderr(&result).contains("unknown endpoint"));
    assert_eq!(
        fs::read_to_string(directory.path().join("work/result.txt")).unwrap(),
        "payload"
    );
    assert!(!directory.path().join("work/checked.txt").exists());
    assert!(!directory.path().join("work/preview.txt").exists());
    assert_eq!(
        fs::read_to_string(directory.path().join("work/dirty.msync")).unwrap(),
        "source.txt -> formatted.txt\n"
    );
    assert!(
        !directory.path().join("work/formatted.txt").exists(),
        "/fmt formats but does not execute"
    );
}

#[test]
fn help_and_repl_help_highlight_real_ansi_and_stay_plain_when_piped() {
    let directory = workspace();
    for (arguments, input) in [
        (vec!["--help", "--color", "always"], ""),
        (vec!["repl", "--color", "always"], "/help\n/?\n/quit\n"),
    ] {
        let colored = invoke(directory.path(), &arguments, input);
        assert_success(&colored);
        let output = stdout(&colored);
        assert!(
            output.contains("\u{1b}["),
            "help needs actual ANSI highlights, not just colored errors: {output}"
        );
        assert!(
            !output.contains("\\x1b["),
            "ANSI escapes must not be printed literally"
        );
    }
    let plain = invoke(directory.path(), &["--help"], "");
    assert_success(&plain);
    assert!(!stdout(&plain).contains('\u{1b}'));
    for option in [
        "--jobs",
        "--max-streams",
        "--buffer-size",
        "--memory-limit",
        "--stdout",
    ] {
        assert!(
            stdout(&plain).contains(option),
            "missing documented option {option}"
        );
    }
    for item in ["$pwd::pwd", "$file::this", "$file::parent"] {
        assert!(
            stdout(&plain).contains(item),
            "help must document namespace item {item}"
        );
    }
    let no_color = invoke_command(
        command(directory.path())
            .args(["--help"])
            .env("NO_COLOR", "1"),
        "",
    );
    assert_success(&no_color);
    assert!(!stdout(&no_color).contains('\u{1b}'));
    let disabled = invoke(directory.path(), &["--help", "--color", "never"], "");
    assert_success(&disabled);
    assert!(!stdout(&disabled).contains('\u{1b}'));
}

#[test]
fn pwd_namespace_item_and_alias_resolve_against_repl_current_directory() {
    let directory = workspace();
    fs::create_dir(directory.path().join("work")).unwrap();
    fs::write(directory.path().join("work/source.txt"), "cwd-owned").unwrap();
    let output = invoke(
        directory.path(),
        &["repl"],
        "/cd work\n$pwd::pwd/source.txt -> $pwd::pwd/first.txt\nthis => $pwd\nthis::pwd/source.txt -> second.txt\n/quit\n",
    );
    assert_success(&output);
    assert!(stderr(&output).is_empty(), "{}", stderr(&output));
    for name in ["first.txt", "second.txt"] {
        assert_eq!(
            fs::read_to_string(directory.path().join("work").join(name)).unwrap(),
            "cwd-owned"
        );
    }
}

#[test]
fn pwd_namespace_rebinding_is_dynamic_but_path_alias_captures_its_value() {
    let directory = workspace();
    for (name, content) in [("first", "first context"), ("second", "second context")] {
        fs::create_dir(directory.path().join(name)).unwrap();
        fs::write(directory.path().join(name).join("source.txt"), content).unwrap();
    }
    let output = invoke(
        directory.path(),
        &["repl"],
        "/cd first\nspace => $pwd\nfrozen := $pwd::pwd\n/cd ../second\nspace::pwd/source.txt -> dynamic.txt\nfrozen/source.txt -> captured.txt\n/quit\n",
    );
    assert_success(&output);
    assert!(stderr(&output).is_empty(), "{}", stderr(&output));
    assert_eq!(
        fs::read_to_string(directory.path().join("second/dynamic.txt")).unwrap(),
        "second context"
    );
    assert_eq!(
        fs::read_to_string(directory.path().join("second/captured.txt")).unwrap(),
        "first context"
    );
}

#[test]
fn explicit_legacy_pwd_forms_keep_their_migration_diagnostics() {
    let directory = workspace();
    fs::write(directory.path().join("source.txt"), "legacy source").unwrap();
    for source in [
        "$pwd::source.txt -> target.txt\n",
        "$pwd/source.txt -> target.txt\n",
    ] {
        let output = invoke(directory.path(), &["--syntax", "0.3", "check", "-"], source);
        assert!(
            !output.status.success(),
            "legacy namespace misuse must not silently change meaning"
        );
        assert!(stderr(&output).contains("$pwd::pwd"), "{}", stderr(&output));
        assert!(!directory.path().join("target.txt").exists());
    }
    assert_success(&invoke(
        directory.path(),
        &["--syntax", "0.3", "run", "-"],
        "source.txt -> target.txt\n",
    ));
    assert_eq!(
        fs::read_to_string(directory.path().join("target.txt")).unwrap(),
        "legacy source"
    );
}

#[test]
fn file_namespace_distinguishes_same_basename_scripts_from_an_unrelated_cwd() {
    let directory = workspace();
    let caller = directory.path().join("caller");
    fs::create_dir(&caller).unwrap();
    let script = "$file::parent/asset.txt -> $file::parent/output.txt\n$file::this -> $file::parent/script-copy.msync\n";
    for (name, contents) in [("first", "first asset"), ("second", "second asset")] {
        let root = directory.path().join(name);
        fs::create_dir(&root).unwrap();
        fs::write(root.join("asset.txt"), contents).unwrap();
        fs::write(root.join("sync.msync"), script).unwrap();
        let result = invoke(
            &caller,
            &["run", root.join("sync.msync").to_str().unwrap()],
            "",
        );
        assert_success(&result);
        assert_eq!(
            fs::read_to_string(root.join("output.txt")).unwrap(),
            contents
        );
        assert_eq!(
            fs::read_to_string(root.join("script-copy.msync")).unwrap(),
            script
        );
    }
    assert!(!caller.join("output.txt").exists());
    assert!(!caller.join("script-copy.msync").exists());
}

#[test]
fn load_source_context_is_scoped_namespace_aliases_are_dynamic_and_values_persist() {
    let directory = workspace();
    for (name, contents) in [("first", "first asset"), ("second", "second asset")] {
        let root = directory.path().join(name);
        fs::create_dir(&root).unwrap();
        fs::write(root.join("asset.txt"), contents).unwrap();
    }
    fs::write(directory.path().join("first/load.msync"), "file => $file\nfrozen := $file::parent\nfile::parent/asset.txt -> $file::parent/output.txt\n").unwrap();
    fs::write(
        directory.path().join("second/load.msync"),
        "file::parent/asset.txt -> $file::parent/output.txt\n",
    )
    .unwrap();
    let output = invoke(
        directory.path(),
        &["repl"],
        "/load first/load.msync\n/load second/load.msync\n$file::this -> leaked-direct.msync\nfile::this -> leaked-alias.msync\nfrozen/asset.txt -> captured.txt\n/quit\n",
    );
    assert_success(&output);
    let diagnostic = stderr(&output);
    assert!(diagnostic.contains("source-file context"), "{diagnostic}");
    assert_eq!(
        fs::read_to_string(directory.path().join("first/output.txt")).unwrap(),
        "first asset"
    );
    assert_eq!(
        fs::read_to_string(directory.path().join("second/output.txt")).unwrap(),
        "second asset"
    );
    assert_eq!(
        fs::read_to_string(directory.path().join("captured.txt")).unwrap(),
        "first asset"
    );
    assert!(!directory.path().join("leaked-direct.msync").exists());
    assert!(!directory.path().join("leaked-alias.msync").exists());
}

#[test]
fn stdin_has_no_file_context_or_implicit_file_alias_and_namespace_formatting_is_idempotent() {
    let directory = workspace();
    let missing_context = invoke(
        directory.path(),
        &["check", "-"],
        "$file::parent/asset.txt -> output.txt\n",
    );
    assert!(!missing_context.status.success());
    assert!(stderr(&missing_context).contains("source-file context"));
    let unbound_alias = invoke(
        directory.path(),
        &["check", "-"],
        "file::this -> output.txt\n",
    );
    assert!(!unbound_alias.status.success());
    assert!(
        stderr(&unbound_alias).contains("unknown endpoint")
            || stderr(&unbound_alias).contains("unknown namespace")
    );
    let file = directory.path().join("namespaces.msync");
    fs::write(
        &file,
        "file=>$file\nthis:=$pwd::pwd\nfile::parent/asset.txt->this/output.txt # keep namespace\n",
    )
    .unwrap();
    assert_success(&invoke(directory.path(), &["fmt", "namespaces.msync"], ""));
    let first = fs::read_to_string(&file).unwrap();
    assert!(first.contains("file => $file"));
    assert!(first.contains("this := $pwd::pwd"));
    assert_success(&invoke(directory.path(), &["fmt", "namespaces.msync"], ""));
    assert_eq!(fs::read_to_string(file).unwrap(), first);
}

#[test]
fn directory_target_without_trailing_slash_is_rejected_without_mutation() {
    let directory = workspace();
    fs::create_dir(directory.path().join("source")).unwrap();
    fs::create_dir(directory.path().join("destination")).unwrap();
    fs::write(directory.path().join("source/new.txt"), "new").unwrap();
    fs::write(directory.path().join("destination/existing.txt"), "old").unwrap();
    let output = invoke(directory.path(), &["run", "-"], "source -> destination\n");
    assert!(!output.status.success());
    let diagnostic = stderr(&output);
    assert!(diagnostic.contains("directory"));
    assert!(
        diagnostic.contains("1:11"),
        "the destination type error belongs on its operand: {diagnostic}"
    );
    assert!(
        diagnostic.contains("append") && diagnostic.contains('/'),
        "show the actionable container-mode fix: {diagnostic}"
    );
    assert!(!directory.path().join("destination/new.txt").exists());
    assert_eq!(
        fs::read_to_string(directory.path().join("destination/existing.txt")).unwrap(),
        "old"
    );
}

#[test]
fn live_plan_eliminates_dead_file_write_before_bounded_execution() {
    let directory = workspace();
    fs::write(directory.path().join("first.txt"), "first source").unwrap();
    fs::write(directory.path().join("last.txt"), "last source").unwrap();
    fs::write(directory.path().join("target.txt"), "original target").unwrap();
    let script = "first.txt -> target.txt\nlast.txt -> target.txt\n";
    let planned = invoke(directory.path(), &["plan", "-"], script);
    assert_success(&planned);
    assert!(
        stdout(&planned).matches("transfer\n").count() == 1,
        "{}",
        stdout(&planned)
    );
    assert!(stdout(&planned).contains("eliminated; overwritten by 1"));
    assert_eq!(
        fs::read_to_string(directory.path().join("target.txt")).unwrap(),
        "original target",
        "planning may inspect metadata but must not copy"
    );
    let executed = invoke(
        directory.path(),
        &[
            "run",
            "-",
            "--jobs",
            "2",
            "--max-streams",
            "3",
            "--buffer-size",
            "1024",
            "--memory-limit",
            "2048",
        ],
        script,
    );
    assert_success(&executed);
    assert_eq!(
        fs::read_to_string(directory.path().join("target.txt")).unwrap(),
        "last source"
    );
    assert_eq!(
        fs::read_to_string(directory.path().join("first.txt")).unwrap(),
        "first source"
    );
    assert_eq!(
        fs::read_to_string(directory.path().join("last.txt")).unwrap(),
        "last source"
    );
}

#[test]
fn cli_rejects_invalid_resource_limits_before_writing() {
    let directory = workspace();
    fs::write(directory.path().join("source.txt"), "new source").unwrap();
    fs::write(directory.path().join("target.txt"), "preserved target").unwrap();
    for flags in [
        vec!["--jobs", "0"],
        vec!["--max-streams", "0"],
        vec!["--buffer-size", "0"],
        vec!["--buffer-size", "1024", "--memory-limit", "1023"],
    ] {
        let mut args = vec!["run", "-"];
        args.extend(flags);
        let output = invoke(directory.path(), &args, "source.txt -> target.txt\n");
        assert!(
            !output.status.success(),
            "invalid limits were accepted: {args:?}"
        );
        assert!(!stderr(&output).is_empty());
        assert_eq!(
            fs::read_to_string(directory.path().join("target.txt")).unwrap(),
            "preserved target"
        );
    }
}

#[test]
fn argument_rejection_does_not_require_consuming_a_large_stdin() {
    let directory = workspace();
    fs::write(directory.path().join("target.txt"), "untouched").unwrap();
    let input = "source.txt -> target.txt\n".repeat(65_536);
    let output = invoke(directory.path(), &["run", "-", "--jobs", "0"], &input);
    assert!(!output.status.success());
    assert!(
        stderr(&output).contains("positive integer"),
        "{}",
        stderr(&output)
    );
    assert_eq!(
        fs::read_to_string(directory.path().join("target.txt")).unwrap(),
        "untouched"
    );
}

#[test]
fn endpoint_typo_diagnostic_points_to_exact_token_and_suggests_binding() {
    let directory = workspace();
    let source = "remote => $local\nremtoe::input.txt -> output.txt\n";
    let output = invoke(
        directory.path(),
        &["check", "-", "--color", "never"],
        source,
    );
    assert!(!output.status.success());
    let diagnostic = stderr(&output);
    assert!(
        diagnostic.contains("remote"),
        "diagnostic should suggest the similarly named binding: {diagnostic}"
    );
    assert!(diagnostic.contains("2:1"), "{diagnostic}");
    let underline = diagnostic.lines().find(|line| line.contains('^')).unwrap();
    assert_eq!(
        underline.chars().filter(|ch| *ch == '^').count(),
        "remtoe".len(),
        "underline the qualifier, not the whole statement: {diagnostic}"
    );
}

#[test]
fn pipe_diagnostics_are_plain_and_forced_color_is_available() {
    let directory = workspace();
    let source = "目录 := source\nunknown::目录 -> output\n";
    let plain = invoke(directory.path(), &["check", "-"], source);
    assert!(!plain.status.success());
    let plain_error = stderr(&plain);
    assert!(!plain_error.contains('\u{1b}'));
    assert!(plain_error.contains("unknown::目录 -> output"));
    assert!(plain_error.contains("2:1"));
    assert!(plain_error.contains('^'));
    let colored = invoke(
        directory.path(),
        &["check", "-", "--color", "always"],
        source,
    );
    assert!(!colored.status.success());
    assert!(stderr(&colored).contains("\u{1b}["));
    let disabled = invoke_command(
        command(directory.path())
            .args(["check", "-", "--color", "never"])
            .env("CLICOLOR_FORCE", "1"),
        source,
    );
    assert!(!stderr(&disabled).contains('\u{1b}'));
    let no_color = invoke_command(
        command(directory.path())
            .args(["check", "-"])
            .env("NO_COLOR", "1"),
        source,
    );
    assert!(!stderr(&no_color).contains('\u{1b}'));
}

#[test]
fn env_and_path_endpoints_use_the_child_environment() {
    let directory = workspace();
    fs::create_dir(directory.path().join("bin")).unwrap();
    fs::create_dir(directory.path().join("target")).unwrap();
    fs::write(directory.path().join("bin/tool.exe"), "portable tool").unwrap();
    let result = invoke_command(
        command(directory.path())
            .args(["run", "-"])
            .env("PATH", directory.path().join("bin"))
            .env("MSYNC_TEST_TARGET", directory.path().join("target")),
        "$path::tool.exe -> $env::MSYNC_TEST_TARGET/\n",
    );
    assert_success(&result);
    assert_eq!(
        fs::read_to_string(directory.path().join("target/tool.exe")).unwrap(),
        "portable tool"
    );
}

#[test]
fn trace_file_survives_exit_and_connects_all_pipeline_phases_without_payload_capture() {
    let directory = workspace();
    fs::write(
        directory.path().join("private-source.txt"),
        "private payload",
    )
    .unwrap();
    let result = invoke_command(
        command(directory.path())
            .args(["run", "-", "--trace", "trace.jsonl"])
            .env(
                "MSYNC_TEST_TELEMETRY_SECRET",
                "sensitive env value must not leak",
            ),
        "private-source.txt -> private-destination.txt\n",
    );
    assert_success(&result);
    let trace = fs::read_to_string(directory.path().join("trace.jsonl")).unwrap();
    for name in [
        "session",
        "submission",
        "parse",
        "compile",
        "execute",
        "copy",
    ] {
        assert!(
            trace.contains(&format!("\"name\":\"{name}\"")),
            "missing span {name}: {trace}"
        );
    }
    let lines: Vec<&str> = trace.lines().collect();
    let spans: Vec<&str> = lines
        .iter()
        .copied()
        .filter(|line| string_field(line, "kind") == "span")
        .collect();
    assert!(spans.len() >= 6);
    let trace_id = string_field(lines[0], "trace_id");
    let span_ids: std::collections::HashSet<&str> = spans
        .iter()
        .map(|line| string_field(line, "span_id"))
        .collect();
    assert_eq!(trace_id.len(), 32);
    assert_eq!(
        span_ids.len(),
        spans.len(),
        "completed span IDs must be unique; events reference an existing span"
    );
    for line in &lines {
        assert!(line.starts_with('{') && line.ends_with('}'));
        assert_eq!(string_field(line, "trace_id"), trace_id);
        assert_eq!(string_field(line, "span_id").len(), 16);
        match string_field(line, "kind") {
            "event" => {
                assert!(
                    span_ids.contains(string_field(line, "span_id")),
                    "events must reference an existing owner span: {line}"
                );
                assert!(line.contains("\"timestamp_unix_ns\":"));
            }
            "span" => {
                assert!(line.contains("\"duration_ns\":"));
                if !line.contains("\"parent_span_id\":null") {
                    assert!(
                        span_ids.contains(string_field(line, "parent_span_id")),
                        "parent must exist in the session: {line}"
                    );
                }
            }
            kind => panic!("unsupported trace record kind: {kind}"),
        }
    }
    let root = spans
        .iter()
        .filter(|line| line.contains("\"parent_span_id\":null"))
        .collect::<Vec<_>>();
    assert_eq!(root.len(), 1, "one end-to-end session must have one root");
    assert_eq!(string_field(root[0], "name"), "session");
    let copy = spans
        .iter()
        .find(|line| string_field(line, "name") == "copy")
        .unwrap();
    assert!(
        copy.contains("\"publication.policy\":\"namespace-receipt\""),
        "{copy}"
    );
    assert!(copy.contains("\"phase\":\"committed\""), "{copy}");
    assert!(
        copy.contains("private-source.txt") && copy.contains("private-destination.txt"),
        "{copy}"
    );
    assert!(copy.contains("\"files.assigned\":1"), "{copy}");
    assert!(
        !copy.contains("\"bytes.committed\""),
        "the transaction publisher owns committed-byte metrics: {copy}"
    );
    assert!(!trace.contains("private payload"));
    assert!(!trace.contains("MSYNC_TEST_TELEMETRY_SECRET"));
    assert!(!trace.contains("sensitive env value must not leak"));
}

#[test]
fn trace_records_failure_and_cli_returns_failure() {
    let directory = workspace();
    let result = invoke(
        directory.path(),
        &["check", "-", "--trace", "failure.jsonl"],
        "unknown::source -> target\n",
    );
    assert!(!result.status.success());
    let trace = fs::read_to_string(directory.path().join("failure.jsonl")).unwrap();
    for name in ["compile", "submission", "session"] {
        let record = trace
            .lines()
            .find(|line| string_field(line, "name") == name)
            .unwrap();
        assert_eq!(
            string_field(record, "status"),
            "error",
            "failure must propagate through phase parents"
        );
    }
}

#[test]
fn http_fetch_journey_uses_real_local_server_and_preserves_body() {
    let directory = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    listener.set_nonblocking(true).unwrap();
    let (stop, stopped) = std::sync::mpsc::channel();
    let server = thread::spawn(move || {
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            match listener.accept() {
                Ok((mut connection, _)) => {
                    connection.set_nonblocking(false).unwrap();
                    connection
                        .set_read_timeout(Some(Duration::from_secs(5)))
                        .unwrap();
                    let mut request = Vec::new();
                    let mut buffer = [0_u8; 1024];
                    while !request.windows(4).any(|part| part == b"\r\n\r\n") {
                        let size = connection.read(&mut buffer).unwrap();
                        if size == 0 {
                            break;
                        }
                        request.extend_from_slice(&buffer[..size]);
                    }
                    let request = String::from_utf8_lossy(&request);
                    if request.starts_with("HEAD /base/help.txt HTTP/") {
                        connection.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 14\r\nConnection: close\r\n\r\n").unwrap();
                    } else {
                        assert!(request.starts_with("GET /base/help.txt HTTP/"), "{request}");
                        connection.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 14\r\nConnection: close\r\n\r\nhello via HTTP").unwrap();
                        return;
                    }
                }
                Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                    if stopped.try_recv().is_ok() {
                        return;
                    }
                    assert!(Instant::now() < deadline, "HTTP client never connected");
                    thread::sleep(Duration::from_millis(10));
                }
                Err(error) => panic!("HTTP accept failed: {error}"),
            }
        }
    });
    let result = invoke(
        directory.path(),
        &["run", "-"],
        &format!("web => http://{address}/base/\nweb::~/help.txt -> help.txt\n"),
    );
    let _ = stop.send(());
    let served = server.join();
    assert_success(&result);
    served.unwrap();
    assert_eq!(
        fs::read(directory.path().join("help.txt")).unwrap(),
        b"hello via HTTP"
    );
}
