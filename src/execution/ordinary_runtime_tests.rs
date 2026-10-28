use std::future::{Future, poll_fn};
use std::task::Poll;

use super::*;

#[tokio::test]
async fn dropped_waiter_retains_the_real_worker_until_stop_joins_it() {
    let scratch = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&scratch).unwrap();
    let root = tempfile::Builder::new()
        .prefix("ordinary-owner-")
        .tempdir_in(scratch)
        .unwrap();
    let context = crate::compiler::CompileContext {
        pwd: root.path().to_owned(),
        source_file: None,
    };
    let program = crate::compiler::structured::Session::new()
        .compile(
            &crate::language::parse("\"payload\" -> target\n").unwrap(),
            &context,
        )
        .unwrap();
    let options = Options::new(root.path().join(".temp"));
    let mut runtime = RuntimeSession::new(&options).unwrap();
    let mut worker_options = options.clone();
    worker_options.cancel = runtime.cancellation();
    let (started_tx, started_rx) = std::sync::mpsc::channel();
    let (release_tx, release_rx) = std::sync::mpsc::channel();
    runtime.ordinary_task = Some(tokio::task::spawn_blocking(move || {
        started_tx.send(()).unwrap();
        release_rx.recv().unwrap();
        ordinary_submission(program, &worker_options, &Default::default(), None)
    }));
    started_rx.recv().unwrap();
    let mut waiter = Box::pin(runtime.join_ordinary());
    poll_fn(|cx| {
        assert!(waiter.as_mut().poll(cx).is_pending());
        Poll::Ready(())
    })
    .await;
    drop(waiter);
    assert!(runtime.has_work());
    let mut stopping = Box::pin(runtime.cancel_active());
    poll_fn(|cx| {
        assert!(stopping.as_mut().poll(cx).is_pending());
        Poll::Ready(())
    })
    .await;
    assert!(!root.path().join("target").exists());
    release_tx.send(()).unwrap();
    stopping.await.unwrap();
    assert!(!runtime.has_work());
    let next = crate::compiler::structured::Session::new()
        .compile(
            &crate::language::parse("\"next\" -> target\n").unwrap(),
            &context,
        )
        .unwrap();
    runtime.submit(&next).await.unwrap();
    assert_eq!(
        fs::read_to_string(root.path().join("target")).unwrap(),
        "next"
    );
    runtime.shutdown().await.unwrap();
}
