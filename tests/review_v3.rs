//! Independent regression probes for planning and native I/O redesigns.

use proptest::prelude::*;

#[test]
fn publishing_a_file_replaces_its_path_not_other_hardlink_bindings() {
    use msync::{
        compiler::{Backend, CopyOp, Location, Program},
        runtime::{ExecutionOptions, execute},
        syntax::Span,
    };
    use std::{fs, path::Path};

    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let source = directory.path().join("replacement");
    let destination = directory.path().join("destination");
    let alias = directory.path().join("other-binding");
    fs::write(&source, b"new complete bytes").unwrap();
    fs::write(&destination, b"old complete bytes").unwrap();
    fs::hard_link(&destination, &alias).unwrap();
    assert!(same_file::is_same_file(&destination, &alias).unwrap());
    let local = |path: &Path| Location {
        backend: Backend::Local,
        path: path.to_string_lossy().into_owned(),
    };
    execute(
        &Program {
            operations: vec![CopyOp {
                source: local(&source),
                destination: local(&destination),
                pattern: None,
                span: Span::default(),
            }],
        },
        &ExecutionOptions::default(),
    )
    .unwrap();

    assert_eq!(fs::read(&destination).unwrap(), b"new complete bytes");
    assert_eq!(fs::read(&alias).unwrap(), b"old complete bytes");
    assert_eq!(fs::read(&source).unwrap(), b"new complete bytes");
    assert!(!same_file::is_same_file(&destination, &alias).unwrap());
    assert_eq!(fs::read_dir(directory.path()).unwrap().count(), 3);
}

#[cfg(windows)]
#[test]
#[ignore = "requires WSL Python 3; executes the exact production remote helper"]
fn remote_file_helper_requires_an_explicit_complete_finish_frame() {
    use std::{
        fs,
        io::Write,
        path::Path,
        process::{Command, Stdio},
    };
    let source = include_str!("../src/streams.rs");
    let helper = source
        .split("const REMOTE_PYTHON: &str = r#\"")
        .nth(1)
        .unwrap()
        .split("\"#;")
        .next()
        .unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let target = directory.path().join("protected-target");
    let mapped = Command::new("wsl")
        .args(["--exec", "wslpath", "-a", "-u"])
        .arg(&target)
        .output()
        .unwrap();
    assert!(mapped.status.success());
    let remote = String::from_utf8(mapped.stdout).unwrap().trim().to_owned();
    let data = |body: &[u8]| {
        let mut frame = (body.len() as u64).to_be_bytes().to_vec();
        frame.extend_from_slice(body);
        frame
    };
    let mut finished = data(b"complete new bytes");
    finished.extend_from_slice(&0_u64.to_be_bytes());
    let mut truncated = 10_u64.to_be_bytes().to_vec();
    truncated.extend_from_slice(b"short");
    for (name, frames, successful) in [
        ("raw EOF after DATA", data(b"incomplete prefix"), false),
        ("truncated DATA frame", truncated, false),
        (
            "oversized frame header",
            65_u64.to_be_bytes().to_vec(),
            false,
        ),
        ("explicit FINISH", finished, true),
    ] {
        fs::write(&target, "old complete bytes").unwrap();
        let mut child = Command::new("wsl")
            .args([
                "--exec", "timeout", "20", "python3", "-u", "-c", helper, &remote, "w", "64",
            ])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let _ = child.stdin.take().unwrap().write_all(&frames);
        let output = child.wait_with_output().unwrap();
        assert_eq!(
            output.status.success(),
            successful,
            "{name}: {}",
            String::from_utf8_lossy(&output.stderr)
        );
        assert_eq!(
            fs::read_to_string(&target).unwrap(),
            if successful {
                "complete new bytes"
            } else {
                "old complete bytes"
            },
            "{name}: an unauthorized or truncated stream must not publish"
        );
        assert_eq!(
            fs::read_dir(directory.path()).unwrap().count(),
            1,
            "{name}: unpublished stage leaked after helper exit"
        );
    }
}

#[cfg(unix)]
#[test]
#[ignore = "requires native POSIX permissions, not DrvFs; run explicitly on tmpfs/ext4"]
fn finite_copy_preserves_source_execute_mode_over_existing_target() {
    use msync::{
        compiler::{Backend, CopyOp, Location, Program},
        runtime::{ExecutionOptions, execute},
        syntax::Span,
    };
    use std::{fs, os::unix::fs::PermissionsExt, path::Path};
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let source = directory.path().join("tool");
    let target = directory.path().join("installed-tool");
    fs::write(&source, "new executable").unwrap();
    fs::write(&target, "old non-executable").unwrap();
    fs::set_permissions(&source, fs::Permissions::from_mode(0o755)).unwrap();
    fs::set_permissions(&target, fs::Permissions::from_mode(0o644)).unwrap();
    let local = |path: &Path| Location {
        backend: Backend::Local,
        path: path.to_string_lossy().into_owned(),
    };
    execute(
        &Program {
            operations: vec![CopyOp {
                source: local(&source),
                destination: local(&target),
                pattern: None,
                span: Span::default(),
            }],
        },
        &ExecutionOptions::default(),
    )
    .unwrap();
    assert_eq!(fs::read_to_string(&target).unwrap(), "new executable");
    assert_eq!(
        fs::metadata(target).unwrap().permissions().mode() & 0o777,
        0o755
    );
}

#[cfg(unix)]
#[test]
#[ignore = "requires unprivileged caller without CAP_DAC_OVERRIDE and native POSIX permissions"]
fn atomic_file_publication_does_not_bypass_an_unwritable_existing_target() {
    use msync::{
        compiler::{Backend, CopyOp, Location, Program},
        runtime::{ExecutionOptions, execute},
        syntax::Span,
    };
    use std::{fs, os::unix::fs::PermissionsExt, path::Path};
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let source = directory.path().join("source");
    let target = directory.path().join("protected-target");
    fs::write(&source, "replacement").unwrap();
    fs::write(&target, "protected original").unwrap();
    fs::set_permissions(&target, fs::Permissions::from_mode(0o400)).unwrap();
    assert_eq!(
        fs::OpenOptions::new()
            .write(true)
            .open(&target)
            .unwrap_err()
            .kind(),
        std::io::ErrorKind::PermissionDenied,
        "fixture must actually deny non-truncating writes before testing the atomic replacement path"
    );
    let local = |path: &Path| Location {
        backend: Backend::Local,
        path: path.to_string_lossy().into_owned(),
    };
    let outcome = execute(
        &Program {
            operations: vec![CopyOp {
                source: local(&source),
                destination: local(&target),
                pattern: None,
                span: Span::default(),
            }],
        },
        &ExecutionOptions::default(),
    );
    assert!(
        outcome.is_err(),
        "atomic rename must not bypass the existing target's write-access contract"
    );
    assert_eq!(fs::read_to_string(&target).unwrap(), "protected original");
}

#[cfg(windows)]
#[tokio::test]
async fn a_reset_stream_source_does_not_publish_its_partial_file() {
    use msync::compiler::{Backend, CopyOp, Location};
    use msync::streams::transfer;
    use msync::syntax::Span;
    use std::{fs, path::Path, time::Duration};
    use tokio::{
        io::AsyncWriteExt,
        net::TcpListener,
        time::{sleep, timeout},
    };
    use tokio_util::sync::CancellationToken;

    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let target = directory.path().join("configuration");
    fs::write(&target, "old complete configuration").unwrap();
    let source = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = source.local_addr().unwrap();
    let operation = CopyOp {
        source: Location {
            backend: Backend::Tcp {
                host: address.ip().to_string(),
                port: address.port(),
            },
            path: "/".into(),
        },
        destination: Location {
            backend: Backend::Local,
            path: target.to_string_lossy().into_owned(),
        },
        pattern: None,
        span: Span::default(),
    };
    let sender = tokio::spawn(async move {
        let (mut stream, _) = source.accept().await.unwrap();
        stream
            .write_all(b"incomplete network prefix")
            .await
            .unwrap();
        sleep(Duration::from_millis(80)).await;
        socket2::SockRef::from(&stream)
            .set_linger(Some(Duration::ZERO))
            .unwrap();
        drop(stream);
    });
    let _outcome = timeout(
        Duration::from_secs(5),
        transfer(&operation, 1024, CancellationToken::new()),
    )
    .await
    .unwrap();
    sender.await.unwrap();
    assert_eq!(
        fs::read_to_string(&target).unwrap(),
        "old complete configuration",
        "TCP reset is not a successful EOF and must not publish a partial staged file"
    );
    assert_eq!(
        fs::read_dir(directory.path()).unwrap().count(),
        1,
        "discarded stage must be cleaned after physical I/O closes"
    );
}

#[test]
fn indexed_frontiers_keep_many_co_start_waves_ordered_without_a_global_fence() {
    use msync::compiler::{Backend, CopyOp, Location, Program};
    use msync::plan::{FileClass, OperationKinds, Plan};
    use msync::syntax::Span;

    let local = |path: String| Location {
        backend: Backend::Local,
        path,
    };
    let mut operations = Vec::new();
    let mut kinds = Vec::new();
    for wave in 0..24 {
        let input = if wave == 0 {
            ".temp/review-v3-flow/seed".into()
        } else {
            format!(".temp/review-v3-flow/snapshot-{}", wave - 1)
        };
        for (source, destination, source_kind, destination_kind) in [
            (
                input,
                ".temp/review-v3-flow/fifo".into(),
                FileClass::Regular,
                FileClass::Stream,
            ),
            (
                ".temp/review-v3-flow/fifo".into(),
                format!(".temp/review-v3-flow/output-{wave}"),
                FileClass::Stream,
                FileClass::Missing,
            ),
            (
                format!(".temp/review-v3-flow/output-{wave}"),
                format!(".temp/review-v3-flow/snapshot-{wave}"),
                FileClass::Missing,
                FileClass::Missing,
            ),
        ] {
            operations.push(CopyOp {
                source: local(source),
                destination: local(destination),
                pattern: None,
                span: Span::default(),
            });
            kinds.push(OperationKinds {
                source: source_kind,
                destination: destination_kind,
            });
        }
    }
    operations.push(CopyOp {
        source: local(".temp/review-v3-independent/input".into()),
        destination: local(".temp/review-v3-independent/output".into()),
        pattern: None,
        span: Span::default(),
    });
    kinds.push(OperationKinds {
        source: FileClass::Regular,
        destination: FileClass::Missing,
    });
    let plan = Plan::build(&Program { operations }, &kinds);
    assert!(plan.issues.is_empty(), "{:?}", plan.issues);
    assert_eq!(plan.stream_groups.len(), 24);
    let mut owners: Vec<usize> = (0..plan.nodes.len()).collect();
    for group in &plan.stream_groups {
        for &member in group {
            owners[member] = group[0];
        }
    }
    let mut reachable = vec![vec![false; plan.nodes.len()]; plan.nodes.len()];
    for node in &plan.nodes {
        for &dependency in &node.dependencies {
            reachable[owners[dependency]][owners[node.id]] = true;
        }
    }
    for middle in 0..plan.nodes.len() {
        for before in 0..plan.nodes.len() {
            for after in 0..plan.nodes.len() {
                reachable[before][after] |= reachable[before][middle] && reachable[middle][after];
            }
        }
    }
    let id = |original: usize| {
        plan.nodes
            .iter()
            .find(|node| node.original_index == original)
            .unwrap()
            .id
    };
    for wave in 0..24 {
        let group = owners[id(wave * 3)];
        let snapshot = owners[id(wave * 3 + 2)];
        assert!(
            reachable[group][snapshot],
            "wave {wave} snapshot lost its producer order"
        );
        if wave != 23 {
            assert!(
                reachable[snapshot][owners[id((wave + 1) * 3)]],
                "wave {wave} snapshot must finish before next co-start group"
            );
        }
    }
    assert!(
        plan.nodes[id(72)].dependencies.is_empty(),
        "unrelated finite copy must not inherit stream barriers"
    );
}

#[test]
fn repl_loaded_source_context_is_dynamic_but_value_alias_captures_its_file() {
    use std::{
        fs,
        io::Write,
        path::Path,
        process::{Command, Stdio},
    };
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    fs::create_dir_all(directory.path().join("scripts-one")).unwrap();
    fs::create_dir_all(directory.path().join("scripts-two")).unwrap();
    fs::create_dir_all(directory.path().join("next-cwd")).unwrap();
    let definitions = "where => $file\nsnapshot := $file::this\n";
    let usage = "where::this -> $pwd::pwd/dynamic.msync\nsnapshot -> $pwd::pwd/captured.msync\n";
    fs::write(
        directory.path().join("scripts-one/define.msync"),
        definitions,
    )
    .unwrap();
    fs::write(directory.path().join("scripts-two/use.msync"), usage).unwrap();
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .args(["repl", "--color", "never"])
        .current_dir(directory.path())
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child.stdin.take().unwrap().write_all(b"/load scripts-one/define.msync\n/cd next-cwd\n/load ../scripts-two/use.msync\nwhere::this -> unavailable.msync\nsnapshot -> snapshot-again.msync\n/quit\n").unwrap();
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(String::from_utf8_lossy(&output.stderr).contains("no source-file context"));
    assert_eq!(
        fs::read_to_string(directory.path().join("next-cwd/dynamic.msync")).unwrap(),
        usage
    );
    assert_eq!(
        fs::read_to_string(directory.path().join("next-cwd/captured.msync")).unwrap(),
        definitions
    );
    assert_eq!(
        fs::read_to_string(directory.path().join("next-cwd/snapshot-again.msync")).unwrap(),
        definitions
    );
    assert!(!directory.path().join("next-cwd/unavailable.msync").exists());
}

fn graph_preserves_pairwise_hazards(descriptions: &[(u8, u8, u8, u8, u8, u8)]) {
    use msync::compiler::{Backend, CopyOp, Location, Program};
    use msync::plan::{Effects, FileClass, OperationKinds, Plan};
    use msync::syntax::Span;
    use std::{fs, path::Path};

    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let first = directory.path().join("hardlink-original");
    let alias = directory.path().join("hardlink-alias");
    fs::write(&first, "same inode").unwrap();
    fs::hard_link(&first, &alias).unwrap();
    let paths: Vec<String> = [
        "hardlink-original",
        "hardlink-alias",
        "x",
        "x/a",
        "x/a/b",
        "x/a/c",
        "x/b",
        "y",
        "y/a",
        "y/a/b",
        "z",
        "z/a",
    ]
    .iter()
    .map(|path| {
        directory
            .path()
            .join(path)
            .to_string_lossy()
            .replace('\\', "/")
    })
    .collect();
    let backend = |tag: u8| match tag % 7 {
        0 => Backend::Local,
        1 => Backend::Wsl,
        2 => Backend::Env,
        3 => Backend::Path,
        4 => Backend::Ssh {
            host: "review.invalid".into(),
            user: None,
            port: None,
        },
        5 => Backend::Http {
            base: "https://review.invalid/".into(),
        },
        _ => Backend::Tcp {
            host: "127.0.0.1".into(),
            port: 9,
        },
    };
    let kind = |tag: u8| match tag % 4 {
        0 => FileClass::Regular,
        1 => FileClass::Missing,
        2 => FileClass::Directory,
        _ => FileClass::Unknown,
    };
    let location = |tag: u8, path: &str| {
        let backend = backend(tag);
        let path = if cfg!(windows)
            && matches!(backend, Backend::Wsl)
            && path.as_bytes().get(1) == Some(&b':')
        {
            format!("/mnt/{}/{}", path[..1].to_ascii_lowercase(), &path[3..])
        } else {
            path.to_string()
        };
        Location { backend, path }
    };
    let operations = descriptions
        .iter()
        .map(|&(sb, sp, db, dp, _, _)| CopyOp {
            source: location(sb, &paths[sp as usize % paths.len()]),
            destination: location(db, &paths[dp as usize % paths.len()]),
            pattern: None,
            span: Span::default(),
        })
        .collect();
    let kinds: Vec<OperationKinds> = descriptions
        .iter()
        .map(|&(_, _, _, _, sk, dk)| OperationKinds {
            source: kind(sk),
            destination: kind(dk),
        })
        .collect();
    let plan = Plan::build(&Program { operations }, &kinds);
    assert!(plan.stream_groups.is_empty());
    let conflicts = |before: &Effects, after: &Effects| {
        let overlaps = |left: &[msync::plan::Resource], right: &[msync::plan::Resource]| {
            left.iter()
                .any(|left| right.iter().any(|right| left.overlaps(right)))
        };
        overlaps(&before.writes, &after.reads)
            || overlaps(&before.reads, &after.writes)
            || overlaps(&before.writes, &after.writes)
            || overlaps(&before.consumes, &after.reads)
            || overlaps(&before.reads, &after.consumes)
            || overlaps(&before.consumes, &after.writes)
            || overlaps(&before.writes, &after.consumes)
    };
    let mut ancestors = vec![vec![false; plan.nodes.len()]; plan.nodes.len()];
    for after in 0..plan.nodes.len() {
        for &before in &plan.nodes[after].dependencies {
            assert!(before < after);
            let (earlier, current) = ancestors.split_at_mut(after);
            current[0][before] = true;
            for (known, inherited) in current[0].iter_mut().zip(&earlier[before]).take(before) {
                *known |= inherited;
            }
        }
        for (before, ordered) in ancestors[after].iter().copied().enumerate().take(after) {
            assert!(
                !conflicts(&plan.nodes[before].effects, &plan.nodes[after].effects) || ordered,
                "raw hazard lost between retained nodes {before} and {after}, descriptions={descriptions:?}, plan={plan}"
            );
        }
    }
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(96))]
    #[test]
    fn indexed_frontiers_preserve_all_pairwise_hazards_with_hierarchy_and_wildcards(
        descriptions in prop::collection::vec((0_u8..6, 0_u8..12, 0_u8..6, 0_u8..12, 0_u8..4, 0_u8..4), 0..75)
    ) {
        graph_preserves_pairwise_hazards(&descriptions);
    }
}

#[cfg(windows)]
#[tokio::test]
async fn windows_kernel_file_to_tcp_starts_at_the_open_source_cursor() {
    use msync::io::{FlowEnd, file_to_tcp};
    use std::{
        fs,
        io::{Seek, SeekFrom},
        path::Path,
        time::Duration,
    };
    use tokio::{
        io::{AsyncReadExt, AsyncWriteExt},
        net::{TcpListener, TcpStream},
        time::timeout,
    };
    use tokio_util::sync::CancellationToken;

    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    let directory = tempfile::tempdir_in(root).unwrap();
    let path = directory.path().join("input");
    let payload: Vec<u8> = (0..262144).map(|index| (index % 251) as u8).collect();
    fs::write(&path, &payload).unwrap();
    let mut source = fs::File::open(path).unwrap();
    source.seek(SeekFrom::Start(31)).unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let mut client = TcpStream::connect(listener.local_addr().unwrap())
        .await
        .unwrap();
    let receiver = tokio::spawn(async move {
        let (mut stream, _) = listener.accept().await.unwrap();
        let mut payload = Vec::new();
        stream.read_to_end(&mut payload).await.unwrap();
        payload
    });
    let progress = timeout(
        Duration::from_secs(5),
        file_to_tcp(&source, &client, 65536, &CancellationToken::new()),
    )
    .await
    .unwrap()
    .unwrap();
    let returned_position = source.stream_position().unwrap();
    if progress.end.is_none() {
        source.seek(SeekFrom::Start(31 + progress.bytes)).unwrap();
        let mut source = tokio::fs::File::from_std(source);
        tokio::io::copy(&mut source, &mut client).await.unwrap();
    } else {
        assert_eq!(progress.end, Some(FlowEnd::SourceClosed));
        assert_eq!(progress.bytes, payload.len() as u64 - 31);
    }
    client.shutdown().await.unwrap();
    drop(client);
    let received = timeout(Duration::from_secs(5), receiver)
        .await
        .unwrap()
        .unwrap();
    assert_eq!(
        received.len(),
        payload.len() - 31,
        "kernel prefix={progress:?}, returned source cursor={returned_position}; fallback must not repeat accepted bytes"
    );
    assert_eq!(received, payload[31..]);
    assert_eq!(returned_position, 31);
}
