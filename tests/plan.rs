use std::collections::{BTreeMap, BTreeSet};

use msync::compiler::{Backend, CopyOp, Location, Program};
use msync::plan::{FileClass, Hazard, OperationKinds, Plan};
use msync::syntax::Span;
use proptest::prelude::*;

fn location(path: &str) -> Location {
    Location {
        backend: Backend::Local,
        path: path.into(),
    }
}

fn op(source: &str, destination: &str) -> CopyOp {
    CopyOp {
        source: location(source),
        destination: location(destination),
        pattern: None,
        span: Span::default(),
    }
}

fn regular() -> OperationKinds {
    OperationKinds {
        source: FileClass::Regular,
        destination: FileClass::Regular,
    }
}

fn files(operations: Vec<CopyOp>) -> Plan {
    let kinds = vec![regular(); operations.len()];
    Plan::build(&Program { operations }, &kinds)
}

#[test]
fn remote_absolute_and_relative_paths_do_not_prove_the_same_destination() {
    let backend = Backend::Ssh {
        host: "planner.invalid".into(),
        user: None,
        port: None,
    };
    let mut operations = vec![op("a", "out"), op("b", "/out")];
    for operation in &mut operations {
        operation.source.backend = backend.clone();
        operation.destination.backend = backend.clone();
    }
    let plan = files(operations);
    assert_eq!(plan.nodes.len(), 2);
    assert!(plan.removed.is_empty());
    // The remote working directory is unknown, so conflicts remain conservative.
    assert_eq!(plan.nodes[1].dependencies, vec![0]);
}

#[test]
fn a_relative_remote_descendant_does_not_prove_an_absolute_parent_kind() {
    let backend = Backend::Ssh {
        host: "planner.invalid".into(),
        user: None,
        port: None,
    };
    let mut operations = vec![op("a", "tree/file"), op("/tree", "output")];
    for operation in &mut operations {
        operation.source.backend = backend.clone();
        operation.destination.backend = backend.clone();
    }
    let plan = Plan::build(
        &Program { operations },
        &[regular(), OperationKinds::default()],
    );
    assert_eq!(plan.nodes[1].kinds.source, FileClass::Unknown);
}

#[test]
fn ancestor_write_cannot_retire_a_read_with_an_external_hardlink_alias() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    std::fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let tree = directory.path().join("tree");
    let other = directory.path().join("other");
    std::fs::create_dir(&tree).unwrap();
    std::fs::create_dir(&other).unwrap();
    let original = tree.join("file");
    let alias = directory.path().join("alias");
    let input = directory.path().join("input");
    std::fs::write(&original, "old").unwrap();
    std::fs::write(&input, "new").unwrap();
    std::fs::hard_link(&original, &alias).unwrap();
    let text = |path: &std::path::Path| path.to_string_lossy().into_owned();
    let program = Program {
        operations: vec![
            op(&text(&original), &text(&directory.path().join("snapshot"))),
            op(&text(&other), &text(&tree)),
            op(&text(&input), &text(&alias)),
        ],
    };
    let directory_kind = OperationKinds {
        source: FileClass::Directory,
        destination: FileClass::Directory,
    };
    let plan = Plan::build(&program, &[regular(), directory_kind, regular()]);
    assert!(plan.nodes[2].dependencies.contains(&0));
    assert!(!plan.nodes[2].dependencies.contains(&1));
}

#[test]
fn profiled_planning_reuses_parent_probes_but_keeps_ordinary_file_identity_handles() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    std::fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let source = directory.path().join("source");
    std::fs::write(&source, "value").unwrap();
    let operations = (0..64)
        .map(|index| {
            op(
                source.to_str().unwrap(),
                directory
                    .path()
                    .join(format!("new-parent/file{index}"))
                    .to_str()
                    .unwrap(),
            )
        })
        .collect::<Vec<_>>();
    let kinds = vec![
        OperationKinds {
            source: FileClass::Regular,
            destination: FileClass::Missing
        };
        operations.len()
    ];
    let (plan, stats) = Plan::build_profiled(&Program { operations }, &kinds);
    assert_eq!(plan.nodes.len(), 64);
    assert_eq!(stats.resource_cache_misses, 65);
    assert_eq!(stats.identity_handles, 1);
    assert!(stats.canonicalize_probes <= 68, "{stats:?}");
    assert_eq!(stats.dependency_candidates, 0);
}

#[test]
fn final_write_eliminates_prior_file_copy_even_across_source_endpoints() {
    let mut last = op("~/.codex/config.toml", "~/Desktop/config.toml");
    last.source.backend = Backend::Wsl;
    let plan = files(vec![
        op("~/.codex/config.toml", "~/Desktop/config.toml"),
        last,
    ]);
    assert_eq!(plan.nodes.len(), 1);
    assert_eq!(plan.nodes[0].original_index, 1);
    assert_eq!(plan.nodes[0].operation.source.backend, Backend::Wsl);
    assert_eq!(plan.removed[0].original_index, 0);
    assert!(
        plan.to_string()
            .contains("overwritten by #1 before any read")
    );
}

#[test]
fn observed_intermediate_value_is_forwarded_before_dead_store_elimination() {
    let plan = files(vec![op("a", "b"), op("b", "c"), op("x", "b")]);
    assert_eq!(plan.nodes.len(), 2);
    assert_eq!(plan.nodes[0].operation.source.path, "a");
    assert_eq!(plan.nodes[0].operation.destination.path, "c");
    assert_eq!(plan.forwarded.len(), 1);
    assert_eq!(plan.removed[0].original_index, 0);
}

#[test]
fn forwarding_does_not_read_a_newer_source_version() {
    let plan = files(vec![op("a", "b"), op("x", "a"), op("b", "c")]);
    assert_eq!(plan.nodes.len(), 3);
    assert!(plan.forwarded.is_empty());
    assert!(plan.nodes[1].dependencies.contains(&0));
    assert!(plan.nodes[2].dependencies.contains(&0));
    assert!(plan.edges.iter().any(|edge| edge.before == 0
        && edge.after == 1
        && edge.hazards.contains(&Hazard::WriteAfterRead)));
}

#[test]
fn fixed_point_exposes_forwarding_after_an_intervening_dead_write_disappears() {
    let plan = files(vec![
        op("a", "b"),
        op("x", "a"),
        op("b", "c"),
        op("y", "a"),
        op("z", "b"),
    ]);
    assert_eq!(plan.nodes.len(), 3);
    assert_eq!(plan.nodes[0].operation.source.path, "a");
    assert_eq!(plan.nodes[0].operation.destination.path, "c");
    assert!(plan.optimization_rounds >= 3);
}

#[test]
fn missing_intermediates_inherit_regular_file_type_from_their_producers() {
    let program = Program {
        operations: vec![op("a", "b"), op("b", "c"), op("x", "b")],
    };
    let kinds = [
        OperationKinds {
            source: FileClass::Regular,
            destination: FileClass::Missing,
        },
        OperationKinds {
            source: FileClass::Missing,
            destination: FileClass::Missing,
        },
        OperationKinds {
            source: FileClass::Regular,
            destination: FileClass::Missing,
        },
    ];
    let plan = Plan::build(&program, &kinds);
    assert_eq!(plan.nodes.len(), 2);
    assert_eq!(plan.nodes[0].kinds.source, FileClass::Regular);
    assert_eq!(plan.nodes[0].operation.source.path, "a");
}

#[test]
fn streams_are_observable_but_do_not_globally_block_independent_file_work() {
    let program = Program {
        operations: vec![op("pipe", "log"), op("a", "b"), op("pipe", "other-log")],
    };
    let stream = OperationKinds {
        source: FileClass::Stream,
        destination: FileClass::Missing,
    };
    let plan = Plan::build(&program, &[stream, regular(), stream]);
    assert_eq!(plan.nodes.len(), 3);
    assert!(plan.nodes[1].dependencies.is_empty());
    assert!(plan.nodes[2].dependencies.contains(&0));
    assert!(
        plan.edges
            .iter()
            .any(|edge| edge.hazards.contains(&Hazard::ConsumingRead))
    );
    assert!(plan.removed.is_empty());
}

#[test]
fn repeated_consuming_reads_form_a_chain_not_a_dense_history() {
    let operations = (0..96)
        .map(|index| op("pipe", &format!("log-{index}")))
        .collect();
    let stream = OperationKinds {
        source: FileClass::Stream,
        destination: FileClass::Missing,
    };
    let plan = Plan::build(&Program { operations }, &[stream; 96]);
    assert_eq!(plan.edges.len(), 95);
    for index in 1..plan.nodes.len() {
        assert_eq!(plan.nodes[index].dependencies, vec![index - 1]);
    }
}

#[test]
fn complementary_stream_endpoints_co_start_instead_of_waiting_for_eof() {
    let writer = OperationKinds {
        source: FileClass::Regular,
        destination: FileClass::Stream,
    };
    let reader = OperationKinds {
        source: FileClass::Stream,
        destination: FileClass::Missing,
    };
    for (operations, kinds) in [
        (
            vec![op("input", "fifo"), op("fifo", "output")],
            vec![writer, reader],
        ),
        (
            vec![op("fifo", "output"), op("input", "fifo")],
            vec![reader, writer],
        ),
    ] {
        let plan = Plan::build(&Program { operations }, &kinds);
        assert_eq!(plan.stream_groups, vec![vec![0, 1]]);
        assert_eq!(plan.stream_connections.len(), 1);
        assert!(plan.nodes.iter().all(|node| node.dependencies.is_empty()));
        assert!(plan.issues.is_empty());
    }
}

#[test]
fn a_multi_pipe_chain_forms_one_bounded_co_start_cohort() {
    let kinds = [
        OperationKinds {
            source: FileClass::Regular,
            destination: FileClass::Stream,
        },
        OperationKinds {
            source: FileClass::Stream,
            destination: FileClass::Stream,
        },
        OperationKinds {
            source: FileClass::Stream,
            destination: FileClass::Missing,
        },
    ];
    let plan = Plan::build(
        &Program {
            operations: vec![
                op("input", "fifo-a"),
                op("fifo-a", "fifo-b"),
                op("fifo-b", "output"),
            ],
        },
        &kinds,
    );
    assert_eq!(plan.stream_groups, vec![vec![0, 1, 2]]);
    assert_eq!(plan.stream_connections.len(), 2);
    assert!(plan.edges.is_empty());
    assert!(plan.issues.is_empty());
}

#[test]
fn repeated_producer_consumer_waves_keep_completion_order_between_cohorts() {
    let writer = OperationKinds {
        source: FileClass::Regular,
        destination: FileClass::Stream,
    };
    let reader = OperationKinds {
        source: FileClass::Stream,
        destination: FileClass::Missing,
    };
    let plan = Plan::build(
        &Program {
            operations: vec![
                op("a", "fifo"),
                op("fifo", "out-a"),
                op("b", "fifo"),
                op("fifo", "out-b"),
            ],
        },
        &[writer, reader, writer, reader],
    );
    assert_eq!(plan.stream_groups, vec![vec![0, 1], vec![2, 3]]);
    assert_eq!(plan.nodes[2].dependencies, vec![0, 1]);
    assert_eq!(plan.nodes[3].dependencies, vec![0, 1]);
    assert!(plan.issues.is_empty());
}

#[test]
fn ambiguous_channel_roles_and_closed_feedback_are_diagnosed() {
    let writer = OperationKinds {
        source: FileClass::Regular,
        destination: FileClass::Stream,
    };
    let reader = OperationKinds {
        source: FileClass::Stream,
        destination: FileClass::Missing,
    };
    let duplex = OperationKinds {
        source: FileClass::Stream,
        destination: FileClass::Stream,
    };
    let ambiguous = Plan::build(
        &Program {
            operations: vec![op("a", "fifo"), op("fifo", "out-a"), op("fifo", "out-b")],
        },
        &[writer, reader, reader],
    );
    assert!(
        ambiguous
            .issues
            .iter()
            .any(|issue| issue.message.contains("producer(s)"))
    );
    let self_feedback = Plan::build(
        &Program {
            operations: vec![op("fifo", "fifo")],
        },
        &[duplex],
    );
    assert!(
        self_feedback
            .issues
            .iter()
            .any(|issue| issue.message.contains("feed itself"))
    );
    let cycle = Plan::build(
        &Program {
            operations: vec![op("fifo-a", "fifo-b"), op("fifo-b", "fifo-a")],
        },
        &[duplex, duplex],
    );
    assert!(
        cycle
            .issues
            .iter()
            .any(|issue| issue.message.contains("closed stream cycle"))
    );
}

#[test]
fn file_hazards_and_contracted_dependency_cycles_are_not_erased_for_co_start() {
    let writer = OperationKinds {
        source: FileClass::Regular,
        destination: FileClass::Stream,
    };
    let reader = OperationKinds {
        source: FileClass::Stream,
        destination: FileClass::Regular,
    };
    let same_file = Plan::build(
        &Program {
            operations: vec![op("file", "fifo"), op("fifo", "file")],
        },
        &[writer, reader],
    );
    assert_eq!(same_file.nodes[1].dependencies, vec![0]);
    assert!(
        same_file
            .issues
            .iter()
            .any(|issue| issue.message.contains("finish before another"))
    );
    let crossed = Plan::build(
        &Program {
            operations: vec![
                op("a", "fifo"),
                op("b", "fifo"),
                op("fifo", "out-a"),
                op("fifo", "out-b"),
            ],
        },
        &[writer, writer, reader, reader],
    );
    assert!(
        crossed
            .issues
            .iter()
            .any(|issue| issue.message.contains("completion-dependency cycle"))
    );
}

#[test]
fn tcp_stream_marker_is_an_exact_channel_not_a_directory_container() {
    let endpoint = Backend::Tcp {
        host: "127.0.0.1".into(),
        port: 45678,
    };
    let mut writer = op("input", "/");
    writer.destination.backend = endpoint.clone();
    let mut reader = op("/", "output");
    reader.source.backend = endpoint;
    let kinds = [
        OperationKinds {
            source: FileClass::Regular,
            destination: FileClass::Stream,
        },
        OperationKinds {
            source: FileClass::Stream,
            destination: FileClass::Missing,
        },
    ];
    let plan = Plan::build(
        &Program {
            operations: vec![writer, reader],
        },
        &kinds,
    );
    assert_eq!(plan.nodes[0].effects.writes[0].location.path, "/");
    assert_eq!(plan.nodes[1].kinds.source, FileClass::Stream);
    assert_eq!(plan.stream_groups, vec![vec![0, 1]]);
    assert!(plan.issues.is_empty());
}

#[cfg(windows)]
#[tokio::test]
async fn planning_a_named_pipe_does_not_consume_a_connection() {
    use tokio::net::windows::named_pipe::ServerOptions;
    let name = format!(r"\\.\pipe\msync-plan-{}", std::process::id());
    let server = ServerOptions::new()
        .first_pipe_instance(true)
        .create(&name)
        .unwrap();
    let program = Program {
        operations: vec![op(&name, ".temp/never-opened-by-plan")],
    };
    let kinds = [OperationKinds {
        source: FileClass::Stream,
        destination: FileClass::Missing,
    }];
    let plan = Plan::build(&program, &kinds);
    assert_eq!(plan.nodes.len(), 1);
    assert!(
        tokio::time::timeout(std::time::Duration::from_millis(40), server.connect())
            .await
            .is_err()
    );
}

#[cfg(unix)]
#[test]
fn planning_a_fifo_does_not_wait_for_a_writer() {
    use std::os::unix::ffi::OsStrExt;
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    std::fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let fifo = directory.path().join("fifo");
    let name = std::ffi::CString::new(fifo.as_os_str().as_bytes()).unwrap();
    // SAFETY: name is a live NUL-terminated path to an exclusively owned fixture.
    assert_eq!(unsafe { libc::mkfifo(name.as_ptr(), 0o600) }, 0);
    let program = Program {
        operations: vec![op(
            fifo.to_str().unwrap(),
            directory.path().join("output").to_str().unwrap(),
        )],
    };
    let (send, receive) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        send.send(Plan::build(
            &program,
            &[OperationKinds {
                source: FileClass::Stream,
                destination: FileClass::Missing,
            }],
        ))
        .unwrap()
    });
    assert_eq!(
        receive
            .recv_timeout(std::time::Duration::from_secs(1))
            .expect("Plan must not open a FIFO")
            .nodes
            .len(),
        1
    );
    assert!(!directory.path().join("output").exists());
}

#[test]
fn directory_effects_cover_children_and_are_not_eliminated_as_file_assignments() {
    let program = Program {
        operations: vec![
            op("tree", "output"),
            op("x", "output/child"),
            op("other-tree", "output"),
        ],
    };
    let tree = OperationKinds {
        source: FileClass::Directory,
        destination: FileClass::Missing,
    };
    let plan = Plan::build(&program, &[tree, regular(), tree]);
    assert_eq!(plan.nodes.len(), 3);
    assert!(plan.nodes[1].dependencies.contains(&0));
    assert!(plan.nodes[2].dependencies.contains(&1));
    assert!(plan.removed.is_empty());
}

#[test]
fn preserve_name_insertions_remain_observable_operations_and_conflict_on_same_child() {
    let plan = files(vec![
        op("one/file", "container/"),
        op("two/file", "container/"),
        op("other", "independent"),
    ]);
    assert_eq!(plan.nodes.len(), 3);
    assert!(plan.nodes[1].dependencies.contains(&0));
    assert!(plan.nodes[2].dependencies.is_empty());
}

#[test]
fn offline_unknown_kinds_do_not_guess_away_streams_or_directory_contents() {
    let program = Program {
        operations: vec![op("a", "b"), op("x", "b")],
    };
    let plan = Plan::offline(&program);
    assert_eq!(plan.nodes.len(), 2);
    assert_eq!(plan.nodes[1].dependencies, vec![0]);
    assert!(plan.removed.is_empty());
}

#[test]
fn forwarding_retains_download_snapshots_instead_of_duplicating_remote_reads() {
    let mut download = op("~/remote-file", "snapshot");
    download.source.backend = Backend::Ssh {
        host: "example.test".into(),
        user: None,
        port: None,
    };
    let plan = files(vec![download, op("snapshot", "copy")]);
    assert!(plan.forwarded.is_empty());
    assert_eq!(plan.nodes[1].dependencies, vec![0]);
    let mut download = op("help.html", "snapshot");
    download.source.backend = Backend::Http {
        base: "https://example.test/".into(),
    };
    let plan = files(vec![download, op("snapshot", "copy")]);
    assert!(plan.forwarded.is_empty());
}

#[test]
fn overlapping_nonexact_write_prevents_dead_store_elimination() {
    let plan = files(vec![
        op("a", "target"),
        op("x", "target/child"),
        op("b", "target"),
    ]);
    assert_eq!(plan.nodes.len(), 3);
    assert_eq!(plan.nodes[1].dependencies, vec![0]);
    assert_eq!(plan.nodes[2].dependencies, vec![1]);
}

#[test]
fn explicit_relative_spellings_share_a_resource_identity() {
    let plan = files(vec![op("a", "directory/../target"), op("b", "./target")]);
    assert_eq!(plan.nodes.len(), 1);
}

#[test]
fn hardlink_aliases_add_dependencies_without_becoming_elimination_proofs() {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    std::fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let first = directory.path().join("first");
    let alias = directory.path().join("alias");
    std::fs::write(&first, "data").unwrap();
    std::fs::hard_link(&first, &alias).unwrap();
    let first = first.to_string_lossy();
    let alias = alias.to_string_lossy();
    let plan = files(vec![op("source-a", &first), op("source-b", &alias)]);
    assert_eq!(plan.nodes.len(), 2);
    assert_eq!(plan.nodes[1].dependencies, vec![0]);
    let same_file = files(vec![op(&first, &alias), op("source-b", &alias)]);
    assert_eq!(
        same_file.nodes.len(),
        2,
        "an invalid same-file copy is not silently optimized away"
    );
    let forwarded_alias = files(vec![op(&first, "intermediate"), op("intermediate", &alias)]);
    assert!(
        forwarded_alias.forwarded.is_empty(),
        "forwarding must not introduce a same-inode copy"
    );
}

fn apply(operations: &[CopyOp], mut state: BTreeMap<String, u64>) -> BTreeMap<String, u64> {
    for operation in operations {
        let value = state[&operation.source.path];
        state.insert(operation.destination.path.clone(), value);
    }
    state
}

fn reverse_ready(plan: &Plan, mut state: BTreeMap<String, u64>) -> BTreeMap<String, u64> {
    let mut remaining: BTreeSet<usize> = (0..plan.nodes.len()).collect();
    let mut done = vec![false; plan.nodes.len()];
    while !remaining.is_empty() {
        let index = *remaining
            .iter()
            .rev()
            .find(|&&index| {
                plan.nodes[index]
                    .dependencies
                    .iter()
                    .all(|&dependency| done[dependency])
            })
            .expect("acyclic dependency graph");
        let operation = &plan.nodes[index].operation;
        let value = state[&operation.source.path];
        state.insert(operation.destination.path.clone(), value);
        done[index] = true;
        remaining.remove(&index);
    }
    state
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(192))]
    #[test]
    fn graph_and_fixed_point_preserve_sequential_file_state(assignments in prop::collection::vec((0_u8..8, 0_u8..8), 0..40)) {
        let names: Vec<String> = (0..8).map(|index| format!(".temp/plan-model/file-{index}")).collect();
        let operations: Vec<CopyOp> = assignments.iter().map(|&(source, destination)| op(&names[source as usize], &names[destination as usize])).collect();
        let initial: BTreeMap<String, u64> = names.into_iter().enumerate().map(|(index, name)| (name, index as u64)).collect();
        let expected = apply(&operations, initial.clone());
        let plan = files(operations);
        prop_assert_eq!(apply(&plan.program().operations, initial.clone()), expected.clone());
        prop_assert_eq!(reverse_ready(&plan, initial), expected);
        prop_assert!(plan.nodes.iter().all(|node| node.dependencies.iter().all(|&dependency| dependency < node.id)));
        let kinds: Vec<_> = plan.nodes.iter().map(|node| node.kinds).collect();
        let fixed = Plan::build(&plan.program(), &kinds);
        let paths = |plan: &Plan| plan.nodes.iter().map(|node| (node.operation.source.clone(), node.operation.destination.clone())).collect::<Vec<_>>();
        prop_assert_eq!(paths(&fixed), paths(&plan));
        prop_assert!(fixed.removed.is_empty());
        prop_assert!(fixed.forwarded.is_empty());
    }
}
