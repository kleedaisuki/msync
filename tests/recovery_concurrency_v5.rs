//! Independent runtimes share recovery discovery without revoking live ownership.

use std::fs;
use std::path::{Path, PathBuf};
use std::process::{Command, Output, Stdio};
use std::time::{Duration, Instant, SystemTime};

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{Options, RuntimeSession};
use msync::filesystem::recovery::Registry;
use same_file::Handle;
use tokio::io::AsyncWriteExt;
use tokio::net::{TcpListener, TcpStream};

const DEADLINE: Duration = Duration::from_secs(20);
const CHILD_ROOT: &str = "MSYNC_RECOVERY_CONCURRENCY_ROOT";
const CHILD_SCRATCH: &str = "MSYNC_RECOVERY_CONCURRENCY_SCRATCH";

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("recovery-concurrency-v5-")
        .tempdir_in(root)
        .unwrap()
}

fn compile(root: &Path, text: &str) -> msync::hir::Program {
    Session::new()
        .compile(
            &msync::language::parse(text).unwrap(),
            &CompileContext {
                pwd: root.to_owned(),
                source_file: None,
            },
        )
        .unwrap()
}

struct LiveWitness {
    root: PathBuf,
    root_identity: Handle,
    marker_bytes: Vec<u8>,
    record: PathBuf,
    record_identity: Handle,
    record_modified: SystemTime,
    record_bytes: Result<Vec<u8>, Option<i32>>,
}

fn record_is_live(path: &Path) -> bool {
    let file = fs::OpenOptions::new()
        .read(true)
        .write(true)
        .open(path)
        .unwrap();
    matches!(file.try_lock(), Err(fs::TryLockError::WouldBlock))
}

fn read_live_record(path: &Path) -> Result<Vec<u8>, Option<i32>> {
    fs::read(path).map_err(|error| {
        assert!(
            cfg!(windows)
                && (error.kind() == std::io::ErrorKind::PermissionDenied
                    || matches!(error.raw_os_error(), Some(32 | 33))),
            "unexpected live-record read error: {error}",
        );
        error.raw_os_error()
    })
}

impl LiveWitness {
    fn capture(runtime: &RuntimeSession) -> Self {
        let root = runtime
            .private_root()
            .unwrap()
            .expect("live flow owns an invocation")
            .to_owned();
        let record = runtime
            .private_stage_record()
            .unwrap()
            .expect("live invocation is durably registered")
            .to_owned();
        assert!(
            record_is_live(&record),
            "the runtime must hold its record lock"
        );
        Self {
            root_identity: Handle::from_path(&root).unwrap(),
            marker_bytes: fs::read(root.join(".msync-owned-stage-v1")).unwrap(),
            record_identity: Handle::from_path(&record).unwrap(),
            record_modified: fs::metadata(&record).unwrap().modified().unwrap(),
            // A Windows owner lock can prohibit reads through a second handle.
            record_bytes: read_live_record(&record),
            root,
            record,
        }
    }

    fn preserved(&self, runtime: &RuntimeSession) -> bool {
        runtime
            .private_root()
            .is_ok_and(|path| path == Some(self.root.as_path()))
            && runtime
                .private_stage_record()
                .is_ok_and(|path| path == Some(self.record.as_path()))
            && Handle::from_path(&self.root).is_ok_and(|identity| identity == self.root_identity)
            && Handle::from_path(&self.record)
                .is_ok_and(|identity| identity == self.record_identity)
            // The owner may create more payloads inside its own directory. Its
            // identity and ownership token, not directory mtime, are invariant.
            && fs::read(self.root.join(".msync-owned-stage-v1"))
                .is_ok_and(|bytes| bytes == self.marker_bytes)
            && fs::metadata(&self.record)
                .and_then(|m| m.modified())
                .is_ok_and(|time| time == self.record_modified)
            && record_is_live(&self.record)
            && read_live_record(&self.record) == self.record_bytes
    }
}

fn dead_stage_child(root: &Path, scratch: &Path) -> Output {
    let mut child = Command::new(std::env::current_exe().unwrap())
        .args(["--exact", "dead_stage_fixture", "--ignored", "--nocapture"])
        .env(CHILD_ROOT, root)
        .env(CHILD_SCRATCH, scratch)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let deadline = Instant::now() + DEADLINE;
    while child.try_wait().unwrap().is_none() {
        if Instant::now() >= deadline {
            child.kill().unwrap();
            let output = child.wait_with_output().unwrap();
            panic!(
                "dead-stage fixture timed out: {}",
                String::from_utf8_lossy(&output.stderr)
            );
        }
        std::thread::sleep(Duration::from_millis(5));
    }
    child.wait_with_output().unwrap()
}

async fn accept_live_flow(listener: &TcpListener, owner: &mut RuntimeSession) -> TcpStream {
    tokio::time::timeout(DEADLINE, async {
        loop {
            tokio::select! {
                accepted = listener.accept() => return accepted.unwrap().0,
                () = tokio::time::sleep(Duration::from_millis(5)) => {
                    if owner.has_ready_work() {
                        owner.poll_ready().await.unwrap_or_else(|error| {
                            panic!("background flow failed before its TCP connection: {error:?}")
                        });
                    }
                    assert!(owner.has_work(), "background flow settled before its TCP connection");
                }
            }
        }
    })
    .await
    .expect("live flow did not connect to its source within the deadline")
}

async fn shared_runtime_journey(with_dead_stage: bool) {
    let workspace = workspace();
    let root = fs::canonicalize(workspace.path()).unwrap();
    let scratch = root.join("scratch");
    fs::create_dir(&scratch).unwrap();
    fs::create_dir(root.join("control")).unwrap();
    fs::write(root.join("control/keep"), b"unregistered user data").unwrap();
    fs::create_dir(root.join("dead-parent")).unwrap();
    let control_modified = fs::metadata(root.join("control"))
        .unwrap()
        .modified()
        .unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let program = compile(
        &root,
        &format!(
            "wire => tcp://{}\nasync wire::/ -> flow-output\n",
            listener.local_addr().unwrap()
        ),
    );
    let mut owner = RuntimeSession::new(&Options::new(&scratch)).unwrap();
    tokio::time::timeout(DEADLINE, owner.submit(&program))
        .await
        .unwrap()
        .unwrap();
    let mut peer = accept_live_flow(&listener, &mut owner).await;
    peer.write_all(b"first chunk|").await.unwrap();
    assert!(owner.has_work(), "the source has not sent EOF");
    let witness = LiveWitness::capture(&owner);

    let dead = if with_dead_stage {
        let fixture_root = root.clone();
        let fixture_scratch = scratch.clone();
        let output =
            tokio::task::spawn_blocking(move || dead_stage_child(&fixture_root, &fixture_scratch))
                .await
                .unwrap();
        assert_eq!(
            output.status.code(),
            Some(73),
            "dead fixture: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        let paths: (PathBuf, PathBuf) =
            serde_json::from_slice(&fs::read(root.join("dead-paths.json")).unwrap()).unwrap();
        assert!(paths.0.join("payload").is_file());
        assert!(paths.1.is_file());
        Some(paths)
    } else {
        None
    };

    let mut visitor = RuntimeSession::new(&Options::new(&scratch)).unwrap();
    let work = compile(&root, "\"visitor finished\" -> visitor-output\n");
    let result = tokio::time::timeout(DEADLINE, visitor.execute(&work)).await;
    let live_preserved = owner.has_work() && witness.preserved(&owner);
    let control_preserved = fs::read(root.join("control/keep"))
        .is_ok_and(|bytes| bytes == b"unregistered user data")
        && fs::metadata(root.join("control"))
            .and_then(|m| m.modified())
            .is_ok_and(|time| time == control_modified);
    let dead_recovered = dead
        .as_ref()
        .is_none_or(|(stage, record)| !stage.exists() && !record.exists());
    drop(witness);
    // Release the external peer even when visitor recovery failed, so this test
    // never needs to abort a live owner merely to report the original failure.
    peer.write_all(b"last chunk").await.unwrap();
    peer.shutdown().await.unwrap();
    drop(peer);
    tokio::time::timeout(DEADLINE, owner.wait())
        .await
        .unwrap()
        .unwrap();
    tokio::time::timeout(DEADLINE, owner.shutdown())
        .await
        .unwrap()
        .unwrap();
    tokio::time::timeout(DEADLINE, visitor.shutdown())
        .await
        .unwrap()
        .unwrap();

    let report = result
        .expect("an unrelated live record blocked visitor startup")
        .expect("visitor could not recover alongside a live owner");
    assert_eq!(report.assignments, 1);
    assert!(
        live_preserved,
        "visitor modified the live invocation or its ownership record"
    );
    assert!(
        control_preserved,
        "visitor changed unregistered user data or its parent mtime"
    );
    assert!(
        dead_recovered,
        "startup skipped a dead record merely because another owner was live"
    );
    assert_eq!(
        fs::read(root.join("visitor-output")).unwrap(),
        b"visitor finished"
    );
    assert_eq!(
        fs::read(root.join("flow-output")).unwrap(),
        b"first chunk|last chunk"
    );
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn a_second_session_can_copy_while_the_first_session_retains_a_live_stream() {
    shared_runtime_journey(false).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn startup_recovers_a_dead_stage_without_revoking_an_unrelated_live_stream() {
    shared_runtime_journey(true).await;
}

#[test]
#[ignore]
fn dead_stage_fixture() {
    let root = PathBuf::from(std::env::var_os(CHILD_ROOT).expect("fixture root"));
    let scratch = PathBuf::from(std::env::var_os(CHILD_SCRATCH).expect("fixture scratch"));
    let parent = root.join("dead-parent");
    let stage = parent.join(".msync-publication-stage-dead-test");
    fs::create_dir(&stage).unwrap();
    let registry = Registry::new(&scratch).unwrap();
    let lease = registry.enter().unwrap();
    let registration = lease.register_stage(&parent, &stage).unwrap();
    fs::write(stage.join("payload"), b"orphaned private payload").unwrap();
    fs::write(
        root.join("dead-paths.json"),
        serde_json::to_vec(&(stage, registration.path())).unwrap(),
    )
    .unwrap();
    std::process::exit(73);
}
