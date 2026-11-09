//! Independent runtimes do ordinary work without inspecting another owner.

use std::fs;
use std::path::Path;
use std::time::Duration;

use msync::compiler::{CompileContext, structured::Session};
use msync::execution::{Options, RuntimeSession};
use same_file::Handle;
use tokio::io::AsyncWriteExt;
use tokio::net::TcpListener;

const DEADLINE: Duration = Duration::from_secs(20);

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("independent-runtimes-")
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

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn a_second_session_copies_while_a_first_session_retains_a_live_stream() {
    let work = workspace();
    let root = fs::canonicalize(work.path()).unwrap();
    let scratch = root.join("scratch");
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let stream = compile(
        &root,
        &format!(
            "wire => tcp://{}\nasync wire::/ -> flow-output\n",
            listener.local_addr().unwrap()
        ),
    );
    let mut owner = RuntimeSession::new(&Options::new(&scratch)).unwrap();
    tokio::time::timeout(DEADLINE, owner.submit(&stream))
        .await
        .unwrap()
        .unwrap();
    let mut peer = tokio::time::timeout(DEADLINE, listener.accept())
        .await
        .unwrap()
        .unwrap()
        .0;
    peer.write_all(b"first chunk|").await.unwrap();
    assert!(owner.has_work());
    let private = owner.private_root().unwrap().unwrap().to_owned();
    let identity = Handle::from_path(&private).unwrap();
    assert!(owner.private_stage_record().unwrap().is_none());

    let legacy = scratch.join(".msync-recovery-v1");
    fs::create_dir(&legacy).unwrap();
    fs::write(legacy.join("foreign-record"), b"leave existing state alone").unwrap();
    let mut visitor = RuntimeSession::new(&Options::new(&scratch)).unwrap();
    let copy = compile(&root, "\"visitor finished\" -> visitor-output\n");
    let result = tokio::time::timeout(DEADLINE, visitor.execute(&copy)).await;
    let preserved = owner.has_work()
        && owner.private_root().unwrap() == Some(private.as_path())
        && Handle::from_path(&private).unwrap() == identity;

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

    let report = result.expect("unrelated live stream blocked copy").unwrap();
    assert_eq!(report.assignments, 1);
    assert!(preserved, "visitor replaced another runtime's live owner");
    assert_eq!(
        fs::read(root.join("visitor-output")).unwrap(),
        b"visitor finished"
    );
    assert_eq!(
        fs::read(root.join("flow-output")).unwrap(),
        b"first chunk|last chunk"
    );
    assert_eq!(
        fs::read(legacy.join("foreign-record")).unwrap(),
        b"leave existing state alone"
    );
}
