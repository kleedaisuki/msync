use super::*;
use crate::compiler::{CompileContext, structured::Session};
use crate::hir::graph::PlanningContext;
use crate::pipeline;

#[tokio::test]
async fn later_submission_flow_keeps_its_named_macro_operand_after_ids_are_reused() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let root = tempfile::tempdir_in(temporary).unwrap();
    let context = CompileContext {
        pwd: root.path().into(),
        source_file: None,
    };
    let mut compiler = Session::new();
    let planning = PlanningContext::default();
    pipeline::compile_named(
        &mut compiler,
        "macro wire(src):\n    src -> target\n",
        "definition.msync",
        &context,
        &planning,
    )
    .unwrap();
    let text = "\n\nwire(input)\n";
    let caller = pipeline::compile_named(
        &mut compiler,
        text,
        "later-input.msync",
        &context,
        &planning,
    )
    .unwrap();
    let origin = caller.origins.values().next().unwrap().clone();
    let mut runtime = RuntimeSession::new(&Options::new(root.path().join("scratch"))).unwrap();
    runtime.set_origins(caller.origins.clone());
    runtime.active_origin = Some(origin);
    let socket = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    let port = socket.local_addr().unwrap().port();
    drop(socket);
    runtime
        .physical_input(
            InstructionId(0),
            Location {
                backend: Backend::Tcp {
                    host: "127.0.0.1".into(),
                    port,
                },
                path: "/".into(),
            },
            Box::pin(tokio::io::sink()),
            TransactionId(0),
            Span::default(),
            CancellationToken::new(),
        )
        .unwrap();
    let unrelated = pipeline::compile_named(
        &mut compiler,
        "other -> output\n",
        "unrelated.msync",
        &context,
        &planning,
    )
    .unwrap();
    assert_eq!(
        caller.origins.keys().next(),
        unrelated.origins.keys().next()
    );
    runtime.set_origins(unrelated.origins.clone());
    runtime.active_origin = unrelated.origins.values().next().cloned();
    let failure = tokio::time::timeout(
        std::time::Duration::from_secs(5),
        runtime.quiesce(TransactionId(0)),
    )
    .await
    .unwrap()
    .unwrap_err();
    let located = failure
        .origin
        .as_ref()
        .unwrap()
        .report(*failure.report, None);
    assert_eq!(&*located.source.name, "later-input.msync");
    let span = located.report.diagnostic.span;
    assert_eq!(&text[span.start..span.end], "input");
    let rendered = located.render(false);
    assert!(rendered.contains("definition.msync"), "{rendered}");
    assert!(rendered.contains("3:6"), "{rendered}");
    assert!(!rendered.contains("unrelated.msync"), "{rendered}");
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn broken_program_pipe_keeps_the_destination_argument_and_macro_definition() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let root = tempfile::tempdir_in(temporary).unwrap();
    let context = CompileContext {
        pwd: root.path().into(),
        source_file: None,
    };
    let mut compiler = Session::new();
    let planning = PlanningContext::default();
    pipeline::compile_named(
        &mut compiler,
        "macro wire(src, dst):\n    src -> dst\n",
        "pipe-definition.msync",
        &context,
        &planning,
    )
    .unwrap();
    let text = "\nwire(input, destination)\n";
    let caller =
        pipeline::compile_named(&mut compiler, text, "pipe-input.msync", &context, &planning)
            .unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(root.path().join("scratch"))).unwrap();
    runtime.active_origin = caller.origins.values().next().cloned();
    let (writer, reader) = tokio::io::duplex(1);
    drop(reader);
    runtime
        .flow(
            Box::pin(std::io::Cursor::new(b"bytes".to_vec())),
            Box::pin(writer),
            TransactionId(0),
            None,
            Span::default(),
        )
        .unwrap();
    runtime.active_origin = None;
    let failure = tokio::time::timeout(
        std::time::Duration::from_secs(5),
        runtime.quiesce(TransactionId(0)),
    )
    .await
    .unwrap()
    .unwrap_err();
    let located = failure
        .origin
        .as_ref()
        .unwrap()
        .report(*failure.report, None);
    assert_eq!(&*located.source.name, "pipe-input.msync");
    let span = located.report.diagnostic.span;
    assert_eq!(&text[span.start..span.end], "destination");
    assert!(located.render(false).contains("pipe-definition.msync"));
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn source_less_submissions_do_not_reuse_registered_instruction_origins() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let root = tempfile::tempdir_in(temporary).unwrap();
    let context = CompileContext {
        pwd: root.path().into(),
        source_file: None,
    };
    let compiled = pipeline::compile_named(
        &mut Session::new(),
        "input -> target\n",
        "input.msync",
        &context,
        &PlanningContext::default(),
    )
    .unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(root.path().join("scratch"))).unwrap();
    runtime.set_origins(compiled.origins);
    runtime.submit(&hir::Program::default()).await.unwrap();
    assert!(!runtime.origins.is_empty());
    runtime.submit(&hir::Program::default()).await.unwrap();
    assert!(runtime.origins.is_empty());
    runtime.shutdown().await.unwrap();
}
