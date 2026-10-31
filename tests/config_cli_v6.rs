//! Configuration is observable policy, applied before effects and below explicit CLI options.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("config-cli-v6-")
        .tempdir_in(root)
        .unwrap()
}

fn command(root: &Path) -> Command {
    let mut command = Command::new(env!("CARGO_BIN_EXE_msync"));
    command
        .current_dir(root)
        .env("HOME", root.join("home"))
        .env("USERPROFILE", root.join("home"))
        .env("XDG_CONFIG_HOME", root.join("xdg"))
        .env("APPDATA", root.join("appdata"))
        .env("NO_COLOR", "1")
        .env_remove("MSYNC_CONFIG")
        .env_remove("MSYNC_STATE_DIR");
    command
}

fn invoke(command: &mut Command) -> Output {
    let mut child = command
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let deadline = Instant::now() + Duration::from_secs(20);
    while child.try_wait().unwrap().is_none() {
        if Instant::now() >= deadline {
            child.kill().unwrap();
            let output = child.wait_with_output().unwrap();
            panic!(
                "configuration journey timed out\nstdout={}\nstderr={}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            );
        }
        thread::sleep(Duration::from_millis(5));
    }
    child.wait_with_output().unwrap()
}

fn run(root: &Path, arguments: &[&str]) -> Output {
    invoke(command(root).args(arguments))
}

fn success(output: &Output) {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

fn effective(output: &Output) -> toml::Value {
    success(output);
    toml::from_str(&String::from_utf8_lossy(&output.stdout)).unwrap()
}

fn canonical(root: &Path) -> PathBuf {
    if cfg!(windows) {
        root.join("appdata/msync/config.toml")
    } else {
        root.join("xdg/msync/config.toml")
    }
}

fn write_config(path: &Path, contents: &str) {
    fs::create_dir_all(path.parent().unwrap()).unwrap();
    fs::write(path, contents).unwrap();
}

#[test]
fn missing_configuration_preserves_defaults_and_exposes_their_provenance() {
    let root = workspace();
    let output = run(root.path(), &["config"]);
    let settings = effective(&output);
    assert_eq!(settings["execution"]["jobs"].as_integer(), Some(4));
    assert_eq!(settings["execution"]["max_streams"].as_integer(), Some(16));
    assert_eq!(
        settings["execution"]["buffer_size"].as_integer(),
        Some(65536)
    );
    assert_eq!(
        settings["execution"]["memory_limit"].as_integer(),
        Some(64 * 1024 * 1024)
    );
    assert!(String::from_utf8_lossy(&output.stdout).contains("default"));
    assert!(!root.path().join("home").exists());
    assert!(!root.path().join("xdg").exists());
    assert!(!root.path().join("appdata").exists());
}

#[test]
fn canonical_configuration_wins_over_legacy_and_explicit_flags_win_over_configuration() {
    let root = workspace();
    let preferred = canonical(root.path());
    write_config(&preferred, "[execution]\njobs = 2\nmax_streams = 3\n");
    write_config(
        &root.path().join("home/.msync/config.toml"),
        "[execution]\njobs = 1\nmax_streams = 1\n",
    );
    let output = run(root.path(), &["config", "--jobs", "7"]);
    let settings = effective(&output);
    assert_eq!(settings["execution"]["jobs"].as_integer(), Some(7));
    assert_eq!(settings["execution"]["max_streams"].as_integer(), Some(3));
    let rendered = String::from_utf8_lossy(&output.stdout);
    assert!(rendered.contains("command line"), "{rendered}");
    assert!(rendered.contains("config.toml"), "{rendered}");
}

#[test]
fn legacy_configuration_is_used_only_when_the_canonical_file_is_absent() {
    let root = workspace();
    write_config(
        &root.path().join("home/.msync/config.toml"),
        "[execution]\njobs = 2\n",
    );
    let output = run(root.path(), &["config"]);
    assert_eq!(
        effective(&output)["execution"]["jobs"].as_integer(),
        Some(2)
    );
    write_config(&canonical(root.path()), "[execution]\njobs = 5\n");
    let output = run(root.path(), &["config"]);
    assert_eq!(
        effective(&output)["execution"]["jobs"].as_integer(),
        Some(5)
    );
}

#[test]
fn explicit_configuration_anchors_relative_paths_and_keeps_the_state_environment_override() {
    let root = workspace();
    let config = root.path().join("settings/custom.toml");
    write_config(
        &config,
        "[execution]\njobs = 2\nstate_dir = \"relative-state\"\n",
    );
    let config_arg = config.to_str().unwrap();
    let output = run(root.path(), &["config", "--config", config_arg]);
    let settings = effective(&output);
    assert_eq!(
        Path::new(settings["execution"]["state_dir"].as_str().unwrap()),
        root.path().join("settings/relative-state")
    );
    assert!(!root.path().join("settings/relative-state").exists());
    let override_path = root.path().join("environment-state");
    let output = invoke(
        command(root.path())
            .args(["config", "--config", config_arg])
            .env("MSYNC_STATE_DIR", &override_path),
    );
    let settings = effective(&output);
    assert_eq!(
        Path::new(settings["execution"]["state_dir"].as_str().unwrap()),
        override_path
    );
    assert!(String::from_utf8_lossy(&output.stdout).contains("MSYNC_STATE_DIR"));
}

#[test]
fn no_config_bypasses_a_malformed_discovered_file_but_explicit_missing_paths_fail() {
    let root = workspace();
    write_config(&canonical(root.path()), "[execution\njobs = [\n");
    let output = run(root.path(), &["config", "--no-config"]);
    assert_eq!(
        effective(&output)["execution"]["jobs"].as_integer(),
        Some(4)
    );
    let output = run(root.path(), &["config", "--config", "does-not-exist.toml"]);
    assert!(!output.status.success());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(error.contains("does-not-exist.toml"), "{error}");
    assert!(error.contains("config"), "{error}");
}

#[test]
fn help_and_version_do_not_open_even_an_explicit_malformed_configuration() {
    let root = workspace();
    write_config(&root.path().join("broken.toml"), "[execution\njobs = [\n");
    for arguments in [
        vec!["--help", "--config", "broken.toml"],
        vec!["--version", "--config", "broken.toml"],
        vec!["--help", "--config", "missing.toml"],
    ] {
        let output = run(root.path(), &arguments);
        success(&output);
        assert!(output.stderr.is_empty());
    }
    assert!(!root.path().join(".temp").exists());
}

#[test]
fn configuration_errors_name_the_file_key_and_exact_line_before_any_effect() {
    let root = workspace();
    fs::write(root.path().join("entry.msync"), "\"payload\" -> result\n").unwrap();
    for (contents, key, line) in [
        ("[execution]\njobz = 2\n", "jobz", 2),
        ("[execution]\njobs = 0\n", "jobs", 2),
        ("[execution]\nbuffer_size = true\n", "buffer_size", 2),
        (
            "[execution]\nbuffer_size = \"128KiB\"\nmemory_limit = \"64KiB\"\n",
            "memory_limit",
            3,
        ),
    ] {
        fs::write(root.path().join("invalid.toml"), contents).unwrap();
        let output = run(
            root.path(),
            &["run", "entry.msync", "--config", "invalid.toml"],
        );
        assert!(!output.status.success(), "accepted {contents:?}");
        let error = String::from_utf8_lossy(&output.stderr);
        assert!(error.contains("invalid.toml"), "{error}");
        assert!(error.contains(key), "{error}");
        assert!(error.contains(&format!(":{line}:")), "{error}");
        assert!(!root.path().join("result").exists());
        assert!(!root.path().join(".temp").exists());
    }
}

#[test]
fn a_real_transfer_runs_with_configured_limits_and_preserves_literal_bytes() {
    let root = workspace();
    fs::write(
        root.path().join("input"),
        b"configuration is applied before actual IO\n",
    )
    .unwrap();
    fs::write(root.path().join("entry.msync"), "input -> output\n").unwrap();
    fs::write(
        root.path().join("config.toml"),
        "[execution]\njobs = 1\nmax_streams = 1\nbuffer_size = \"1KiB\"\nmemory_limit = \"2KiB\"\n",
    )
    .unwrap();
    success(&run(
        root.path(),
        &["run", "entry.msync", "--config", "config.toml"],
    ));
    assert_eq!(
        fs::read(root.path().join("input")).unwrap(),
        fs::read(root.path().join("output")).unwrap()
    );
}

#[test]
fn configured_program_spool_limit_is_enforced_and_cli_can_raise_it() {
    let root = workspace();
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    fs::write(
        root.path().join("entry.msync"),
        format!("P => $local::{executable}(\"--version\")\nP::stdout -> version\n"),
    )
    .unwrap();
    fs::write(
        root.path().join("config.toml"),
        "[execution]\nspool_limit = 1\n",
    )
    .unwrap();
    let output = run(
        root.path(),
        &["run", "entry.msync", "--config", "config.toml"],
    );
    assert!(!output.status.success());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(
        error.contains("spool") || error.contains("output"),
        "{error}"
    );
    success(&run(
        root.path(),
        &[
            "run",
            "entry.msync",
            "--config",
            "config.toml",
            "--spool-limit",
            "1KiB",
        ],
    ));
    assert!(
        fs::read_to_string(root.path().join("version"))
            .unwrap()
            .starts_with("msync ")
    );
}

#[test]
fn a_working_directory_config_is_not_implicitly_trusted() {
    let root = workspace();
    fs::write(root.path().join("config.toml"), "[execution]\njobs = 99\n").unwrap();
    let output = run(root.path(), &["config"]);
    assert_eq!(
        effective(&output)["execution"]["jobs"].as_integer(),
        Some(4)
    );
}

#[test]
fn cross_field_validation_uses_the_merged_configuration_not_unrelated_cli_defaults() {
    let root = workspace();
    fs::write(
        root.path().join("config.toml"),
        "[execution]\nmemory_limit = \"256MiB\"\n",
    )
    .unwrap();
    let output = run(
        root.path(),
        &[
            "config",
            "--config",
            "config.toml",
            "--buffer-size",
            "128MiB",
        ],
    );
    let settings = effective(&output);
    assert_eq!(
        settings["execution"]["buffer_size"].as_integer(),
        Some(128 * 1024 * 1024)
    );
    assert_eq!(
        settings["execution"]["memory_limit"].as_integer(),
        Some(256 * 1024 * 1024)
    );
}

#[cfg(unix)]
#[test]
fn configuration_never_waits_for_a_named_pipe_writer() {
    use std::ffi::CString;
    use std::os::unix::ffi::OsStrExt;

    let root = workspace();
    let pipe = root.path().join("config.pipe");
    let native = CString::new(pipe.as_os_str().as_bytes()).unwrap();
    assert_eq!(unsafe { libc::mkfifo(native.as_ptr(), 0o600) }, 0);
    let name = pipe.to_str().unwrap();
    success(&run(root.path(), &["--help", "--config", name]));
    let output = run(root.path(), &["config", "--config", name]);
    assert!(!output.status.success());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(error.contains("config.pipe"), "{error}");
    assert!(
        error.contains("regular") || error.contains("file"),
        "{error}"
    );
}

#[test]
fn telemetry_paths_are_config_relative_and_inspection_or_disable_never_creates_the_database() {
    let root = workspace();
    let config = root.path().join("settings/observed.toml");
    write_config(
        &config,
        "[telemetry]\nenabled = true\nbackend = \"sqlite\"\npath = \"events/session.sqlite\"\n",
    );
    let expected = root.path().join("settings/events/session.sqlite");
    let name = config.to_str().unwrap();
    let output = run(root.path(), &["config", "--config", name]);
    let settings = effective(&output);
    assert_eq!(settings["telemetry"]["enabled"].as_bool(), Some(true));
    assert_eq!(
        Path::new(settings["telemetry"]["path"].as_str().unwrap()),
        expected
    );
    assert!(!expected.exists());
    assert!(!expected.parent().unwrap().exists());
    fs::write(root.path().join("entry.msync"), "\"payload\" -> result\n").unwrap();
    success(&run(
        root.path(),
        &["run", "entry.msync", "--config", name, "--no-telemetry"],
    ));
    assert_eq!(fs::read(root.path().join("result")).unwrap(), b"payload");
    assert!(!expected.exists());
    assert!(!expected.parent().unwrap().exists());
    fs::create_dir_all(expected.parent().unwrap()).unwrap();
    success(&run(
        root.path(),
        &["check", "entry.msync", "--config", name],
    ));
    let bytes = fs::read(&expected).unwrap();
    assert!(bytes.starts_with(b"SQLite format 3\0"));
    let output = run(
        root.path(),
        &["telemetry", "summary", expected.to_str().unwrap()],
    );
    success(&output);
    let summary: serde_json::Value = serde_json::from_slice(&output.stdout).unwrap();
    assert_eq!(summary["sessions"].as_u64(), Some(1));
    assert!(summary["spans"].as_u64().unwrap() > 0);
}

#[test]
fn pathological_telemetry_and_progress_values_report_the_exact_configuration_key() {
    let root = workspace();
    for (table, key, value) in [
        ("telemetry", "busy_timeout_ms", "2147483648"),
        ("telemetry", "flush_interval_ms", "86400001"),
        ("telemetry", "queue_capacity", "1000001"),
        ("telemetry", "max_database_bytes", "\"512KiB\""),
        ("telemetry", "max_database_bytes", "\"8589934592GiB\""),
        ("progress", "interval_ms", "0"),
        ("progress", "delay_ms", "86400001"),
    ] {
        fs::write(
            root.path().join("invalid.toml"),
            format!("[{table}]\n{key} = {value}\n"),
        )
        .unwrap();
        let output = run(root.path(), &["config", "--config", "invalid.toml"]);
        assert!(!output.status.success(), "accepted {table}.{key}={value}");
        let error = String::from_utf8_lossy(&output.stderr);
        assert!(error.contains("invalid.toml:2:"), "{error}");
        assert!(error.contains(&format!("{table}.{key}")), "{error}");
        assert!(!root.path().join(".temp").exists());
    }
}

#[test]
fn conflicting_configuration_or_exporter_flags_fail_before_creating_a_trace() {
    let root = workspace();
    for arguments in [
        vec!["config", "--config", "one.toml", "--no-config"],
        vec![
            "config",
            "--trace",
            "one.jsonl",
            "--telemetry-db",
            "one.sqlite",
        ],
        vec!["config", "--trace", "one.jsonl", "--no-telemetry"],
        vec!["config", "--telemetry-db", "one.sqlite", "--no-telemetry"],
    ] {
        let output = run(root.path(), &arguments);
        assert!(!output.status.success(), "accepted {arguments:?}");
        assert!(!root.path().join("one.jsonl").exists());
        assert!(!root.path().join("one.sqlite").exists());
        let error = String::from_utf8_lossy(&output.stderr);
        assert!(error.contains("--"), "{error}");
    }
}

#[test]
fn effective_configuration_round_trips_without_turning_unset_paths_into_literals() {
    let root = workspace();
    let first = run(root.path(), &["config", "--no-config"]);
    let expected = effective(&first);
    assert!(expected["telemetry"].get("path").is_none());
    fs::write(root.path().join("rendered.toml"), &first.stdout).unwrap();
    let reloaded = run(root.path(), &["config", "--config", "rendered.toml"]);
    assert_eq!(effective(&reloaded), expected);

    let first = invoke(
        command(root.path())
            .env_remove("HOME")
            .env_remove("USERPROFILE")
            .args(["config", "--no-config"]),
    );
    let expected = effective(&first);
    assert!(expected["execution"].get("state_dir").is_none());
    assert!(expected["telemetry"].get("path").is_none());
    fs::write(root.path().join("no-home.toml"), &first.stdout).unwrap();
    let reloaded = invoke(
        command(root.path())
            .env_remove("HOME")
            .env_remove("USERPROFILE")
            .args(["config", "--config", "no-home.toml"]),
    );
    assert_eq!(effective(&reloaded), expected);
    assert!(!root.path().join("none").exists());
    assert!(!root.path().join("default").exists());
}

#[test]
fn a_default_telemetry_destination_is_created_only_for_an_enabled_actual_command() {
    let root = workspace();
    fs::write(
        root.path().join("observed.toml"),
        "[telemetry]\nenabled = true\n",
    )
    .unwrap();
    fs::write(root.path().join("entry.msync"), "\"payload\" -> result\n").unwrap();
    let parent = root.path().join("home/.msync/state");
    let database = parent.join("telemetry.sqlite");
    success(&run(root.path(), &["config", "--config", "observed.toml"]));
    success(&run(root.path(), &["--help", "--config", "observed.toml"]));
    success(&run(
        root.path(),
        &[
            "check",
            "entry.msync",
            "--config",
            "observed.toml",
            "--no-telemetry",
        ],
    ));
    assert!(!parent.exists());
    success(&run(
        root.path(),
        &["check", "entry.msync", "--config", "observed.toml"],
    ));
    assert!(
        fs::read(database)
            .unwrap()
            .starts_with(b"SQLite format 3\0")
    );
    assert!(!root.path().join("result").exists());
}

#[test]
fn platform_resource_bounds_report_configuration_keys_before_any_effect() {
    let root = workspace();
    let too_many_permits = (tokio::sync::Semaphore::MAX_PERMITS + 1).to_string();
    let oversized_buffer = format!("\"{}\"", isize::MAX as u128 + 1);
    for (key, value) in [
        ("jobs", too_many_permits.clone()),
        ("max_streams", too_many_permits),
        ("buffer_size", oversized_buffer),
    ] {
        fs::write(
            root.path().join("platform.toml"),
            format!("[execution]\n{key} = {value}\n"),
        )
        .unwrap();
        let output = run(root.path(), &["config", "--config", "platform.toml"]);
        assert!(!output.status.success(), "accepted execution.{key}={value}");
        let error = String::from_utf8_lossy(&output.stderr);
        assert!(error.contains("platform.toml:2:"), "{error}");
        assert!(error.contains(&format!("execution.{key}")), "{error}");
        assert!(!root.path().join(".temp").exists());
        assert!(!root.path().join("home").exists());
    }
}

#[test]
fn unsigned_cli_byte_budgets_remain_parseable_and_round_trip() {
    let root = workspace();
    let maximum = u64::MAX.to_string();
    let mut arguments = vec!["config", "--no-config", "--spool-limit", maximum.as_str()];
    if usize::BITS == 64 {
        arguments.extend(["--memory-limit", maximum.as_str()]);
    }
    let first = run(root.path(), &arguments);
    let expected = effective(&first);
    assert_eq!(
        expected["execution"]["spool_limit"].as_str(),
        Some(maximum.as_str())
    );
    if usize::BITS == 64 {
        assert_eq!(
            expected["execution"]["memory_limit"].as_str(),
            Some(maximum.as_str())
        );
    }
    fs::write(root.path().join("unsigned.toml"), &first.stdout).unwrap();
    let reloaded = run(root.path(), &["config", "--config", "unsigned.toml"]);
    assert_eq!(effective(&reloaded), expected);
    assert!(!root.path().join(".temp").exists());
    assert!(!root.path().join("home").exists());
}

#[test]
fn zero_sampled_configuration_does_not_admit_a_default_destination() {
    let root = workspace();
    fs::write(
        root.path().join("sampled.toml"),
        "[telemetry]\nenabled = true\nbackend = \"sqlite\"\nsample_per_million = 0\n",
    )
    .unwrap();
    fs::write(root.path().join("entry.msync"), "\"payload\" -> result\n").unwrap();
    let no_home = invoke(
        command(root.path())
            .env_remove("HOME")
            .env_remove("USERPROFILE")
            .args(["check", "entry.msync", "--config", "sampled.toml"]),
    );
    success(&no_home);
    success(&run(
        root.path(),
        &["check", "entry.msync", "--config", "sampled.toml"],
    ));
    assert!(!root.path().join("home").exists());
    assert!(!root.path().join(".temp").exists());
    assert!(!root.path().join("result").exists());
    assert!(!root.path().join("telemetry.sqlite").exists());
}
