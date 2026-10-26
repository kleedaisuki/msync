use std::fs;
use std::sync::{Arc, Mutex};

use msync::telemetry;

static SESSION_LOCK: Mutex<()> = Mutex::new(());

#[test]
fn public_api_traces_local_file_with_parent_ids_and_can_be_disabled() {
    let _exclusive = SESSION_LOCK.lock().unwrap();
    let directory = tempfile::tempdir().unwrap();
    let destination = directory.path().join("trace.jsonl");
    let path = destination.to_str().unwrap();

    // Disabled instrumentation must not create an output or need initialization.
    drop(telemetry::span("disabled"));
    assert!(!destination.exists());

    telemetry::init(path).unwrap();
    {
        let mut root = telemetry::span("run");
        root.metric("operations", 1);
        {
            let mut parse = telemetry::span("parse");
            parse.finish_result(&Ok::<(), ()>(()));
        }
        {
            let mut compile = telemetry::span("compile");
            compile.finish_result(&Err::<(), _>("private source path is never recorded"));
        }
        root.error();
    }
    telemetry::flush().unwrap();
    assert!(telemetry::take_error().is_none());
    let first = fs::read_to_string(&destination).unwrap();
    assert_eq!(first.lines().count(), 3);
    assert!(!first.contains("private source"));
    assert!(first.contains("\"name\":\"parse\""));
    assert!(first.contains("\"name\":\"compile\""));
    assert!(first.contains("\"metrics\":{\"operations\":1}"));

    // Reconfiguration appends rather than truncating a prior trace.
    telemetry::init(path).unwrap();
    drop(telemetry::span("next_session"));
    telemetry::disable().unwrap();
    let complete = fs::read_to_string(&destination).unwrap();
    assert!(complete.starts_with(&first));
    assert_eq!(complete.lines().count(), 4);
    drop(telemetry::span("not_recorded"));
    telemetry::flush().unwrap();
    assert_eq!(fs::read_to_string(&destination).unwrap(), complete);
}

fn field<'a>(record: &'a str, name: &str) -> &'a str {
    let prefix = format!("\"{name}\":");
    let value = record.split_once(&prefix).unwrap().1;
    if let Some(quoted) = value.strip_prefix('"') {
        quoted.split_once('"').unwrap().0
    } else {
        value.split([',', '}']).next().unwrap()
    }
}

#[test]
fn concurrent_async_tasks_and_blocking_workers_form_one_rooted_trace() {
    let _exclusive = SESSION_LOCK.lock().unwrap();
    let directory = tempfile::tempdir().unwrap();
    let destination = directory.path().join("async.jsonl");
    telemetry::init(destination.to_str().unwrap()).unwrap();
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(3)
        .enable_all()
        .build()
        .unwrap();
    {
        let mut root = telemetry::span("schedule");
        let parent = telemetry::context();
        let failed = runtime.block_on(async move {
            let barrier = Arc::new(tokio::sync::Barrier::new(8));
            let mut jobs = tokio::task::JoinSet::new();
            for index in 0_u64..8 {
                let parent = parent.clone();
                let barrier = barrier.clone();
                jobs.spawn(async move {
                    assert!(telemetry::context().is_none());
                    let mut task = telemetry::linked_span("task", parent.as_ref());
                    task.metric("index", index);
                    let child = task.context();
                    barrier.wait().await;
                    tokio::task::yield_now().await;
                    assert!(telemetry::context().is_none());
                    let blocking_parent = child.clone();
                    tokio::task::spawn_blocking(move || {
                        telemetry::with_context(blocking_parent.as_ref(), || {
                            let mut copy = telemetry::span("copy");
                            copy.metric("files", 1);
                            copy.finish_result(&Ok::<(), ()>(()));
                        });
                        assert!(telemetry::context().is_none());
                    })
                    .await
                    .unwrap();
                    {
                        let mut stream = telemetry::linked_span("stream", child.as_ref());
                        tokio::task::yield_now().await;
                        stream.metric("bytes", 64);
                    }
                    let result = if index == 0 { Err(()) } else { Ok(()) };
                    task.finish_result(&result);
                    result
                });
            }
            let mut failed = false;
            while let Some(result) = jobs.join_next().await {
                failed |= result.unwrap().is_err();
            }
            failed
        });
        assert!(failed);
        root.error();
    }
    telemetry::disable().unwrap();
    let output = fs::read_to_string(destination).unwrap();
    let records: Vec<&str> = output.lines().collect();
    assert_eq!(records.len(), 25);
    let root_records: Vec<&&str> = records
        .iter()
        .filter(|record| field(record, "parent_span_id") == "null")
        .collect();
    assert_eq!(root_records.len(), 1);
    let root = *root_records[0];
    assert_eq!(field(root, "name"), "schedule");
    assert_eq!(field(root, "status"), "error");
    let trace = field(root, "trace_id");
    let root_id = field(root, "span_id");
    let by_id: std::collections::BTreeMap<&str, &str> = records
        .iter()
        .map(|record| (field(record, "span_id"), *record))
        .collect();
    assert_eq!(by_id.len(), records.len());
    for record in records {
        assert_eq!(field(record, "trace_id"), trace);
        let parent = field(record, "parent_span_id");
        match field(record, "name") {
            "schedule" => assert_eq!(parent, "null"),
            "task" => assert_eq!(parent, root_id),
            "copy" | "stream" => {
                assert_eq!(field(by_id[parent], "name"), "task");
            }
            name => panic!("unexpected trace phase: {name}"),
        }
    }
}

#[test]
fn retained_context_keeps_its_sink_after_reconfiguration_and_disable() {
    let _exclusive = SESSION_LOCK.lock().unwrap();
    let directory = tempfile::tempdir().unwrap();
    let original = directory.path().join("original.jsonl");
    let replacement = directory.path().join("replacement.jsonl");
    telemetry::init(original.to_str().unwrap()).unwrap();
    let parent = {
        let _root = telemetry::span("original");
        telemetry::context().unwrap()
    };
    telemetry::init(replacement.to_str().unwrap()).unwrap();
    drop(telemetry::span("replacement"));
    telemetry::disable().unwrap();
    let linked = telemetry::linked_span("task", Some(&parent));
    let child = linked.context().unwrap();
    std::thread::spawn(move || {
        telemetry::with_context(Some(&child), || {
            drop(telemetry::span("copy"));
        });
    })
    .join()
    .unwrap();
    drop(linked);
    parent.flush().unwrap();

    let first = fs::read_to_string(original).unwrap();
    let next = fs::read_to_string(replacement).unwrap();
    let records: Vec<&str> = first.lines().collect();
    assert_eq!(records.len(), 3);
    assert_eq!(next.lines().count(), 1);
    let trace = field(records[0], "trace_id");
    for record in records {
        assert_eq!(field(record, "trace_id"), trace);
    }
    assert_ne!(field(next.lines().next().unwrap(), "trace_id"), trace);
    assert!(telemetry::context().is_none());
}
