use std::fs;

use msync::telemetry;

#[test]
fn public_api_traces_local_file_with_parent_ids_and_can_be_disabled() {
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
