//! Optional transport clients are inventoried without executing them.

use std::{fs, path::Path, process::Command};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("doctor-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn invoke(root: &Path) -> Command {
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let mut command = Command::new(executable);
    command
        .current_dir(root)
        .args(["doctor", "--color", "never"])
        .env("PATH", root)
        .env("NO_COLOR", "1");
    command
}

#[test]
fn missing_optional_transports_do_not_disable_native_execution() {
    let root = workspace();
    let output = invoke(root.path()).output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(output.stderr.is_empty());
    let text = String::from_utf8(output.stdout).unwrap();
    for expected in [
        "read-only capability inventory",
        "ordinary file assignments",
        "asynchronous external programs",
        "available (built in)",
        "No isolation provider or host driver is required",
        "runtime unverified",
        "not checked",
        "not launch",
        "do not disable",
    ] {
        assert!(text.contains(expected), "missing {expected}: {text}");
    }
    assert!(!text.contains("Sandboxie"), "{text}");
    assert!(!text.contains("bubblewrap"), "{text}");
    assert!(!text.contains('\u{1b}'));
    assert!(!text.contains("-->"));
    assert!(!text.contains('^'));
    assert_eq!(fs::read_dir(root.path()).unwrap().count(), 0);
}

#[test]
fn a_located_client_is_not_executed_or_reported_as_verified() {
    let root = workspace();
    let name = if cfg!(windows) { "curl.exe" } else { "curl" };
    let client = root.path().join(name);
    let bytes = b"not an executable; inventory must only inspect this path";
    fs::write(&client, bytes).unwrap();
    let output = invoke(root.path()).output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(output.stderr.is_empty());
    let text = String::from_utf8(output.stdout).unwrap();
    assert!(
        text.contains("HTTP sources: located; runtime unverified"),
        "{text}"
    );
    assert!(text.contains(&client.display().to_string()), "{text}");
    assert_eq!(fs::read(&client).unwrap(), bytes);
    assert_eq!(fs::read_dir(root.path()).unwrap().count(), 1);
}

#[test]
fn obsolete_provider_configuration_is_ignored_even_when_nonlocal_or_hostile() {
    let root = workspace();
    for configured in [
        "missing\u{1b}[31m\nprovider",
        r"\\msync-doctor-no-remote-probe\share\Sandboxie",
    ] {
        let output = invoke(root.path())
            .env("MSYNC_SANDBOXIE_HOME", configured)
            .output()
            .unwrap();
        assert!(output.status.success());
        assert!(output.stderr.is_empty());
        let text = String::from_utf8(output.stdout).unwrap();
        assert!(!text.contains('\u{1b}'));
        assert!(!text.contains("-->"));
        assert!(!text.contains("Sandboxie"), "{text}");
        assert!(!text.contains(configured), "{text}");
    }
    assert_eq!(fs::read_dir(root.path()).unwrap().count(), 0);
}

#[cfg(windows)]
#[test]
fn network_search_paths_are_skipped_without_opening_remote_clients() {
    let root = workspace();
    let output = invoke(root.path())
        .env("PATH", r"\\msync-doctor-no-remote-probe\share")
        .output()
        .unwrap();
    assert!(output.status.success());
    assert!(output.stderr.is_empty());
    let text = String::from_utf8(output.stdout).unwrap();
    assert!(!text.contains("msync-doctor-no-remote-probe"), "{text}");
    assert_eq!(fs::read_dir(root.path()).unwrap().count(), 0);
}
