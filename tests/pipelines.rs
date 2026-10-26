use std::fs;
use std::path::Path;

use msync::compiler::{Compiler, Program};
use msync::scheduler::{ResourceLimits, execute_plan_async, prepare};
use msync::syntax;
use tokio_util::sync::CancellationToken;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("pipeline-")
        .tempdir_in(root)
        .unwrap()
}

fn program(source: &str) -> Program {
    Compiler::new()
        .compile(&syntax::parse(source).unwrap())
        .unwrap()
}

fn path(path: &Path) -> String {
    format!("{:?}", path.to_string_lossy().replace('\\', "/"))
}

#[tokio::test]
async fn pipeline_requires_enough_atomic_capacity_before_connecting() {
    let root = workspace();
    let input = root.path().join("input");
    let output = root.path().join("output");
    fs::write(&input, "preserve original").unwrap();
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let source = format!(
        "wire => tcp://{}\n{} -> wire::/\nwire::/ -> {}\n",
        listener.local_addr().unwrap(),
        path(&input),
        path(&output)
    );
    let plan = prepare(&program(&source), true).unwrap();
    assert_eq!(plan.stream_groups, vec![vec![0, 1]]);
    let limits = ResourceLimits {
        jobs: 1,
        max_streams: 1,
        buffer_bytes: 1024,
        memory_bytes: 4096,
    };
    let error = execute_plan_async(&plan, &limits, CancellationToken::new())
        .await
        .unwrap_err();
    assert!(error.message.contains("co-start"), "{error}");
    assert!(!output.exists());
    assert_eq!(fs::read_to_string(&input).unwrap(), "preserve original");
    assert!(
        tokio::time::timeout(std::time::Duration::from_millis(50), listener.accept())
            .await
            .is_err()
    );
}

#[test]
fn live_preparation_rejects_an_unproduced_missing_source_before_any_writes() {
    let root = workspace();
    let source = root.path().join("present.txt");
    let first_output = root.path().join("first-output");
    let absent = root.path().join("absent");
    let output = root.path().join("output");
    fs::write(&source, "unchanged").unwrap();
    let input = program(&format!(
        "{} -> {}\n{} -> {}\n",
        path(&source),
        path(&first_output),
        path(&absent),
        path(&output)
    ));
    assert!(
        prepare(&input, true)
            .unwrap_err()
            .message
            .contains("no preceding plan")
    );
    assert!(!first_output.exists());
    assert!(!output.exists());
    assert!(
        prepare(&input, false).is_ok(),
        "offline preview remains conservative"
    );
}

#[cfg(unix)]
fn fifo(path: &Path) {
    assert!(
        std::process::Command::new("mkfifo")
            .arg(path)
            .status()
            .unwrap()
            .success()
    );
}

#[cfg(unix)]
#[tokio::test]
async fn composed_fifo_chain_co_starts_and_preserves_binary_data() {
    let root = workspace();
    let input = root.path().join("input.bin");
    let first = root.path().join("first.fifo");
    let second = root.path().join("second.fifo");
    let output = root.path().join("output.bin");
    let bytes: Vec<u8> = (0..262144).map(|index| (index % 251) as u8).collect();
    fs::write(&input, &bytes).unwrap();
    fifo(&first);
    fifo(&second);
    let source = format!(
        "{} -> {}\n{} -> {}\n{} -> {}\n",
        path(&input),
        path(&first),
        path(&first),
        path(&second),
        path(&second),
        path(&output)
    );
    let plan = prepare(&program(&source), true).unwrap();
    assert_eq!(plan.stream_groups, vec![vec![0, 1, 2]]);
    let limits = ResourceLimits {
        jobs: 1,
        max_streams: 3,
        buffer_bytes: 1024,
        memory_bytes: 3 * 1024,
    };
    let summary = tokio::time::timeout(
        std::time::Duration::from_secs(5),
        execute_plan_async(&plan, &limits, CancellationToken::new()),
    )
    .await
    .unwrap()
    .unwrap();
    assert_eq!(fs::read(&output).unwrap(), bytes);
    assert_eq!(summary.stream_bytes, bytes.len() as u64 * 3);
    assert_eq!(summary.max_active_streams, 3);
    assert_eq!(summary.peak_buffer_bytes, 3 * 1024);
}
