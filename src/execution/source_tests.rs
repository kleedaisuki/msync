use super::*;
use crate::compiler::{CompileContext, structured::Session};
use crate::hir::graph::PlanningContext;
use crate::pipeline;

fn bind_capture_fixture(runtime: &mut RuntimeSession, compilation: &pipeline::Compilation) {
    let submission = runtime.ledger.next_submission().unwrap();
    let orders = runtime
        .ledger
        .publication_orders(&compilation.program)
        .unwrap();
    let instruction = compilation.program.body.instructions[0].id;
    let bindings = Bindings {
        nodes: BTreeMap::from([(
            instruction,
            OwnerKey::new("source-capture-fixture").unwrap(),
        )]),
        ..Bindings::default()
    };
    let plan = SubmissionPlan::bind(
        submission,
        Arc::new(compilation.program.clone()),
        compilation.origins.clone(),
        compilation.planning.clone(),
        compilation.graph.clone(),
        bindings,
    )
    .unwrap();
    runtime.active_work = Some(WorkNode {
        key: plan.node_key(instruction).unwrap(),
        order: orders[&instruction],
        transaction: TransactionId(0),
        physical: None,
        plan: plan.clone(),
    });
    runtime.plan = Some(plan);
}

#[test]
fn live_domain_enrolls_future_operands_without_probing_their_old_ancestors() {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    let root = tempfile::tempdir_in(temporary).unwrap();
    fs::write(root.path().join("program"), b"executable metadata fixture").unwrap();
    fs::write(root.path().join("node"), b"old ordinary file").unwrap();
    fs::write(root.path().join("argument"), b"not a required input").unwrap();
    let context = CompileContext {
        pwd: root.path().into(),
        source_file: None,
    };
    let mut compiler = Session::new();
    let compiled = pipeline::compile(
        &mut compiler,
        "transaction T:\n    p => $pwd::program($pwd::argument/child)\n    $pwd::node/../node/x -> observed\n    \"bytes\" -> $pwd::node/../node/literal\n    p::stdout -> $pwd::node/../node/stdout\n",
        &context,
        &[],
    )
    .unwrap();
    let prepared = crate::preparation::prepare(&compiled.program, true).unwrap();
    let options = Options::new(root.path().join("scratch"));
    let domain = live_domain(&prepared.program, &options, &prepared.context).unwrap();
    assert_eq!(domain.roots, vec![fs::canonicalize(root.path()).unwrap()]);
    assert!(domain.transfers.values().any(|transfer| {
        matches!(&transfer.source, Source::Local(path) if path.components().any(|part| part == std::path::Component::ParentDir))
    }));
    assert_eq!(
        fs::read(root.path().join("node")).unwrap(),
        b"old ordinary file"
    );
    assert!(!options.scratch_root.exists());
    assert!(!root.path().join("observed").exists());
}

#[tokio::test]
async fn saturated_terminal_channel_reconciles_persistent_process_exit() {
    let scratch = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    let mut runtime = RuntimeSession::new(&Options::new(scratch)).unwrap();
    let process = ProcessId(19);
    runtime
        .programs
        .insert(process, Program::exited_fixture(process, TransactionId(7)));
    let unrelated = Terminal::Flow(NodeKey {
        submission: events::SubmissionId(999),
        instruction: InstructionId(0),
    });
    while runtime.terminal_tx.try_send(unrelated.clone()).is_ok() {}
    assert!(
        runtime
            .terminal_tx
            .try_send(Terminal::Process(process))
            .is_err()
    );
    while runtime.terminal_rx.try_recv().is_ok() {}
    assert!(
        runtime.has_ready_work(),
        "persistent exit must wake service even after hints drain"
    );
    runtime.drain_terminals().unwrap();
    assert!(matches!(
        runtime
            .ledger
            .outcome(&Event::Process(process, ProcessEvent::Exited)),
        Some(Outcome::Succeeded)
    ));
    assert!(
        !runtime.has_ready_work(),
        "an observed exit must not busy-spin service"
    );
}

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
    bind_capture_fixture(&mut runtime, &caller);
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
    bind_capture_fixture(&mut runtime, &caller);
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
