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
    child
        .stdin
        .take()
        .unwrap()
        .write_all(input.as_bytes())
        .unwrap();
    child.wait_with_output().unwrap()
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
    assert!(out.contains("base := ."));
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
fn pwd_endpoint_and_prefix_resolve_against_repl_current_directory() {
    let directory = workspace();
    fs::create_dir(directory.path().join("work")).unwrap();
    fs::write(directory.path().join("work/source.txt"), "cwd-owned").unwrap();
    let output = invoke(
        directory.path(),
        &["repl"],
        "/cd work\n$pwd::source.txt -> $pwd/first.txt\n$pwd/source.txt -> $pwd::second.txt\n/quit\n",
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
        stdout(&planned).contains("plan: 1 operation(s), 1 eliminated"),
        "{}",
        stdout(&planned)
    );
    assert!(stdout(&planned).contains("overwritten by #1"));
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
fn trace_file_survives_exit_and_connects_all_pipeline_phases_without_paths() {
    let directory = workspace();
    fs::write(
        directory.path().join("private-source.txt"),
        "private payload",
    )
    .unwrap();
    let result = invoke(
        directory.path(),
        &["run", "-", "--trace", "trace.jsonl"],
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
    assert!(lines.len() >= 6);
    let trace_id = string_field(lines[0], "trace_id");
    let span_ids: std::collections::HashSet<&str> = lines
        .iter()
        .map(|line| string_field(line, "span_id"))
        .collect();
    assert_eq!(trace_id.len(), 32);
    assert_eq!(span_ids.len(), lines.len(), "span IDs must be unique");
    for line in &lines {
        assert!(line.starts_with('{') && line.ends_with('}'));
        assert_eq!(string_field(line, "trace_id"), trace_id);
        assert_eq!(string_field(line, "span_id").len(), 16);
        assert!(line.contains("\"duration_ns\":"));
        if !line.contains("\"parent_span_id\":null") {
            assert!(
                span_ids.contains(string_field(line, "parent_span_id")),
                "parent must exist in the session: {line}"
            );
        }
    }
    let root = lines
        .iter()
        .filter(|line| line.contains("\"parent_span_id\":null"))
        .collect::<Vec<_>>();
    assert_eq!(root.len(), 1, "one end-to-end session must have one root");
    assert_eq!(string_field(root[0], "name"), "session");
    assert!(!trace.contains("private-source"));
    assert!(!trace.contains("private-destination"));
    assert!(!trace.contains("private payload"));
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
    let server = thread::spawn(move || {
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            match listener.accept() {
                Ok((mut connection, _)) => {
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
    server.join().unwrap();
    assert_success(&result);
    assert_eq!(
        fs::read(directory.path().join("help.txt")).unwrap(),
        b"hello via HTTP"
    );
}
