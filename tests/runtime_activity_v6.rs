use std::path::PathBuf;
use std::time::Duration;

use msync::endpoint::{Backend, Location};
use msync::execution::{Options, RuntimeSession, WorkId};
use msync::hir::{
    Argument, Instruction, InstructionId, InstructionKind, ProcessId, Program, Region,
};
use msync::process::{Process, ProcessConfig};
use msync::syntax::Span;

fn fixture() -> tempfile::TempDir {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/activity-tests");
    std::fs::create_dir_all(&root).unwrap();
    tempfile::tempdir_in(root).unwrap()
}

fn child(name: &str) -> std::process::Command {
    let mut command = std::process::Command::new(std::env::current_exe().unwrap());
    command.args(["--ignored", "--exact", name, "--nocapture"]);
    command
}

fn launch(process: usize, name: &str, cwd: PathBuf) -> Instruction {
    Instruction {
        id: InstructionId(process),
        kind: InstructionKind::Spawn {
            process: ProcessId(process),
            executable: Location {
                backend: Backend::Local,
                path: std::env::current_exe()
                    .unwrap()
                    .to_string_lossy()
                    .into_owned(),
            },
            arguments: ["--ignored", "--exact", name, "--nocapture"]
                .into_iter()
                .map(|value| Argument::Text(value.into()))
                .collect(),
            cwd,
        },
        span: Span::default(),
        expansion: Vec::new(),
    }
}

#[test]
#[ignore = "subprocess fixture"]
fn child_quick() {
    std::thread::sleep(Duration::from_millis(30));
}

#[test]
#[ignore = "subprocess fixture"]
fn child_slow() {
    std::thread::sleep(Duration::from_millis(300));
}

#[test]
#[ignore = "subprocess fixture"]
fn child_late_output() {
    std::thread::sleep(Duration::from_millis(150));
    println!("output after the leader exited");
}

#[test]
#[ignore = "subprocess fixture"]
fn child_with_inherited_output() {
    let descendant = child("child_late_output")
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::inherit())
        .stderr(std::process::Stdio::inherit())
        .spawn()
        .unwrap();
    // The fixture leader exits while its descendant still owns the inherited pipes.
    std::mem::forget(descendant);
}

#[tokio::test]
async fn no_work_wait_stays_pending_and_is_safe_to_cancel() {
    let root = fixture();
    let mut runtime = RuntimeSession::new(&Options::new(root.path())).unwrap();
    assert!(
        tokio::time::timeout(Duration::from_millis(20), runtime.wait_ready())
            .await
            .is_err()
    );
    assert!(!runtime.has_work());
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn any_completion_wakes_without_waiting_for_an_earlier_slow_child() {
    let root = fixture();
    let mut runtime = RuntimeSession::new(&Options::new(root.path())).unwrap();
    runtime
        .submit(&Program {
            body: Region {
                instructions: vec![
                    launch(0, "child_slow", root.path().into()),
                    launch(1, "child_quick", root.path().into()),
                ],
            },
        })
        .await
        .unwrap();
    tokio::time::timeout(Duration::from_millis(200), runtime.wait_ready())
        .await
        .expect("quick child should wake the idle interpreter");
    runtime.poll_ready().await.unwrap();
    let work = runtime.work_ids();
    assert!(work.contains(&WorkId::Program(ProcessId(0))));
    assert!(!work.contains(&WorkId::Program(ProcessId(1))));
    assert_eq!(
        runtime.work_instructions()[&WorkId::Program(ProcessId(0))],
        InstructionId(0)
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn completion_signal_waits_for_inherited_output_not_only_the_leader() {
    let root = fixture();
    let mut process = Process::spawn_native(
        child("child_with_inherited_output"),
        ProcessConfig::new(root.path()),
    )
    .unwrap();
    tokio::time::timeout(Duration::from_secs(3), process.exit_signal().cancelled())
        .await
        .unwrap();
    let signal = process.completion_signal();
    assert!(
        tokio::time::timeout(Duration::from_millis(20), signal.cancelled())
            .await
            .is_err()
    );
    tokio::time::timeout(Duration::from_secs(3), signal.cancelled())
        .await
        .unwrap();
    process.wait().await.unwrap();
}

#[tokio::test]
async fn repeated_background_flow_sites_keep_distinct_live_work_identities() {
    use msync::hir::control::{
        ControlInstruction as Control, Expr, ExprKind, RangeExpr, RangeSource, SlotId,
        TransferInput, TransferOutput, Type,
    };
    use msync::language::DestinationMode;
    use tokio::io::AsyncWriteExt;

    let root = fixture();
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let source = Expr {
        ty: Type::Path,
        kind: ExprKind::Path {
            location: Location {
                backend: Backend::Tcp {
                    host: "127.0.0.1".into(),
                    port: listener.local_addr().unwrap().port(),
                },
                path: "/".into(),
            },
            mode: DestinationMode::Exact,
            relocation: None,
        },
    };
    let destination = Expr {
        ty: Type::Path,
        kind: ExprKind::Join {
            base: Box::new(Expr {
                ty: Type::Path,
                kind: ExprKind::Path {
                    location: Location {
                        backend: Backend::Local,
                        path: root.path().to_string_lossy().into_owned(),
                    },
                    mode: DestinationMode::Exact,
                    relocation: None,
                },
            }),
            suffix: Box::new(Expr {
                ty: Type::String,
                kind: ExprKind::Slot(SlotId(0)),
            }),
            mode: DestinationMode::Exact,
            suffix_backend: None,
        },
    };
    let program = Program {
        body: Region {
            instructions: vec![Instruction {
                id: InstructionId(0),
                span: Span::default(),
                expansion: Vec::new(),
                kind: InstructionKind::Control(Control::For {
                    slot: SlotId(0),
                    range: RangeExpr {
                        source: RangeSource::Values(
                            ["one", "two"]
                                .into_iter()
                                .map(|name| Expr {
                                    ty: Type::String,
                                    kind: ExprKind::String(name.into()),
                                })
                                .collect(),
                        ),
                        stages: Vec::new(),
                        item_type: Type::String,
                    },
                    body: Region {
                        instructions: vec![Instruction {
                            id: InstructionId(1),
                            span: Span::default(),
                            expansion: Vec::new(),
                            kind: InstructionKind::Control(Control::Transfer {
                                source: TransferInput::Scalar(source),
                                destination: TransferOutput::File {
                                    path: destination,
                                    mode: DestinationMode::Exact,
                                },
                            }),
                        }],
                    },
                }),
            }],
        },
    };
    let mut runtime = RuntimeSession::new(&Options::new(root.path())).unwrap();
    runtime.submit(&program).await.unwrap();
    let (mut first, _) = tokio::time::timeout(Duration::from_secs(3), listener.accept())
        .await
        .unwrap()
        .unwrap();
    let (mut second, _) = tokio::time::timeout(Duration::from_secs(3), listener.accept())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(runtime.work_ids().len(), 2);
    #[cfg(unix)]
    {
        let target = root.path().join("kept");
        let link = root.path().join("leaf-link");
        std::fs::write(&target, b"must remain").unwrap();
        std::os::unix::fs::symlink(&target, &link).unwrap();
        let deletion = Program {
            body: Region {
                instructions: vec![Instruction {
                    id: InstructionId(2),
                    span: Span::default(),
                    expansion: Vec::new(),
                    kind: InstructionKind::Control(Control::Delete {
                        path: Expr {
                            ty: Type::Path,
                            kind: ExprKind::Path {
                                location: Location {
                                    backend: Backend::Local,
                                    path: link.to_string_lossy().into_owned(),
                                },
                                mode: DestinationMode::Exact,
                                relocation: None,
                            },
                        },
                    }),
                }],
            },
        };
        tokio::time::timeout(Duration::from_secs(1), runtime.submit(&deletion))
            .await
            .unwrap()
            .unwrap();
        assert!(std::fs::symlink_metadata(&link).is_err());
        assert_eq!(std::fs::read(target).unwrap(), b"must remain");
        let real = root.path().join("real");
        std::fs::create_dir(&real).unwrap();
        std::fs::write(real.join("file"), b"through ancestor").unwrap();
        std::os::unix::fs::symlink(&real, root.path().join("linked_dir")).unwrap();
        let local = |path: PathBuf| Expr {
            ty: Type::Path,
            kind: ExprKind::Path {
                location: Location {
                    backend: Backend::Local,
                    path: path.to_string_lossy().into_owned(),
                },
                mode: DestinationMode::Exact,
                relocation: None,
            },
        };
        let node = |id: usize, operation: Control| Instruction {
            id: InstructionId(id),
            span: Span::default(),
            expansion: Vec::new(),
            kind: InstructionKind::Control(operation),
        };
        let alias = local(root.path().join("linked_dir/file"));
        let observation = Program {
            body: Region {
                instructions: vec![
                    node(
                        3,
                        Control::If {
                            condition: Expr {
                                ty: Type::Bool,
                                kind: ExprKind::Exists(Box::new(alias.clone())),
                            },
                            then_body: Region {
                                instructions: vec![node(
                                    4,
                                    Control::Transfer {
                                        source: TransferInput::Scalar(Expr {
                                            ty: Type::String,
                                            kind: ExprKind::String("seen".into()),
                                        }),
                                        destination: TransferOutput::File {
                                            path: local(root.path().join("seen")),
                                            mode: DestinationMode::Exact,
                                        },
                                    },
                                )],
                            },
                            else_body: Region::default(),
                        },
                    ),
                    node(5, Control::Delete { path: alias }),
                ],
            },
        };
        tokio::time::timeout(Duration::from_secs(1), runtime.submit(&observation))
            .await
            .unwrap()
            .unwrap();
        assert_eq!(std::fs::read(root.path().join("seen")).unwrap(), b"seen");
        assert!(!real.join("file").exists());
    }
    assert!(
        runtime
            .work_instructions()
            .values()
            .all(|instruction| *instruction == InstructionId(1))
    );
    first.write_all(b"first").await.unwrap();
    first.shutdown().await.unwrap();
    tokio::time::timeout(Duration::from_secs(3), runtime.wait_ready())
        .await
        .unwrap();
    runtime.poll_ready().await.unwrap();
    assert_eq!(
        runtime.work_ids().len(),
        1,
        "settling one iteration must not retire the other"
    );
    second.write_all(b"second").await.unwrap();
    second.shutdown().await.unwrap();
    runtime.wait().await.unwrap();
    assert!(runtime.work_ids().is_empty());
    let mut payloads = [
        std::fs::read(root.path().join("one")).unwrap(),
        std::fs::read(root.path().join("two")).unwrap(),
    ];
    payloads.sort();
    assert_eq!(payloads, [b"first".to_vec(), b"second".to_vec()]);
    runtime.shutdown().await.unwrap();
}
