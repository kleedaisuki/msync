//! Actual WSL projections of Windows files must retain local alias provenance.
#![cfg(windows)]

use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{Options, RuntimeSession};

fn wsl(arguments: &[&str]) -> String {
    let output = Command::new("wsl.exe")
        .env("WSL_UTF8", "1")
        .args(["--exec"])
        .args(arguments)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "WSL failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    String::from_utf8(output.stdout).unwrap().trim().to_owned()
}

fn workspace() -> tempfile::TempDir {
    let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&base).unwrap();
    tempfile::Builder::new()
        .prefix("wsl-projection-v5-")
        .tempdir_in(base)
        .unwrap()
}

fn projected(path: &Path) -> String {
    let guest = wsl(&["wslpath", "-a", "-u", path.to_str().unwrap()]);
    let filesystem = wsl(&["stat", "-f", "-c", "%t", "--", &guest]);
    assert_ne!(
        filesystem, "ef53",
        "fixture must exercise the non-ext4 projected fallback"
    );
    guest
}

fn quoted(value: &str) -> String {
    serde_json::to_string(value).unwrap()
}

async fn run(root: &Path, scratch: &Path, script: &str) -> Result<(), String> {
    let compiled = msync::pipeline::compile(
        &mut Session::new(),
        script,
        &CompileContext {
            pwd: root.to_owned(),
            source_file: None,
        },
        &[],
    )
    .unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(scratch)).unwrap();
    runtime.set_origins(compiled.origins);
    let result = runtime
        .execute(&compiled.program)
        .await
        .map(|_| ())
        .map_err(|error| error.to_string());
    runtime.shutdown().await.unwrap();
    result
}

#[tokio::test]
#[ignore = "actual Windows NTFS fixture projected through WSL DrvFS/9p"]
async fn projected_file_cannot_replace_its_same_windows_file() {
    let root = workspace();
    let target = root.path().join("same-file");
    fs::write(&target, b"must remain unchanged").unwrap();
    let guest = projected(&target);
    let script = format!("$wsl::{} -> $pwd::pwd / \"same-file\"\n", quoted(&guest));
    let result = run(root.path(), &root.path().join("scratch"), &script).await;
    assert!(
        result.is_err(),
        "projected same-object source was incorrectly treated as a distinct import"
    );
    assert_eq!(fs::read(&target).unwrap(), b"must remain unchanged");
}

#[tokio::test]
#[ignore = "actual Windows NTFS fixture projected through WSL DrvFS/9p"]
async fn projected_directory_cannot_publish_inside_itself() {
    let root = workspace();
    let source = root.path().join("source");
    fs::create_dir(&source).unwrap();
    fs::write(source.join("payload"), b"unchanged source").unwrap();
    let guest = projected(&source);
    let script = format!(
        "$wsl::{} -> $pwd::pwd / \"source/child\"/.\n",
        quoted(&guest)
    );
    let result = run(root.path(), &root.path().join("scratch"), &script).await;
    assert!(
        result.is_err(),
        "projected ancestor overlap was incorrectly hidden by import staging"
    );
    assert!(!source.join("child").exists());
    assert_eq!(
        fs::read(source.join("payload")).unwrap(),
        b"unchanged source"
    );
}

fn inventory(root: &Path) -> Vec<PathBuf> {
    fn visit(root: &Path, path: &Path, paths: &mut Vec<PathBuf>) {
        for entry in fs::read_dir(path).unwrap() {
            let path = entry.unwrap().path();
            paths.push(path.strip_prefix(root).unwrap().to_owned());
            if path.is_dir() {
                visit(root, &path, paths);
            }
        }
    }
    let mut paths = Vec::new();
    visit(root, root, &mut paths);
    paths.sort();
    paths
}

#[tokio::test]
#[ignore = "actual Windows NTFS fixture projected through WSL DrvFS/9p"]
async fn projected_capture_excludes_only_owned_runtime_backing_and_registry() {
    let root = workspace();
    let source = root.path().join("source");
    let scratch = source.join(".temp");
    fs::create_dir_all(scratch.join("execute-user-owned")).unwrap();
    fs::create_dir_all(scratch.join(".msync-publication-user-data")).unwrap();
    fs::write(source.join("payload"), b"source data").unwrap();
    fs::write(scratch.join("user"), b"user scratch data").unwrap();
    fs::write(
        scratch.join("execute-user-owned/keep"),
        b"user invocation lookalike",
    )
    .unwrap();
    fs::write(
        scratch.join(".msync-publication-user-data/keep"),
        b"user publication lookalike",
    )
    .unwrap();
    let expected = inventory(&source);
    let guest = projected(&source);
    let script = format!(
        "$wsl::{} -> $pwd::pwd / \"destination\"/.\n",
        quoted(&guest)
    );
    run(root.path(), &scratch, &script).await.unwrap();
    let destination = root.path().join("destination");
    assert_eq!(
        inventory(&destination),
        expected,
        "capture must retain user .temp contents without copying active execute backing or recovery registry"
    );
    assert_eq!(
        fs::read(destination.join("payload")).unwrap(),
        b"source data"
    );
    assert_eq!(
        fs::read(destination.join(".temp/user")).unwrap(),
        b"user scratch data"
    );
    assert_eq!(
        fs::read(destination.join(".temp/execute-user-owned/keep")).unwrap(),
        b"user invocation lookalike"
    );
    assert_eq!(
        fs::read(destination.join(".temp/.msync-publication-user-data/keep")).unwrap(),
        b"user publication lookalike"
    );
}
