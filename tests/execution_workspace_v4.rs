use std::fs;
use std::path::Path;
#[cfg(windows)]
use std::path::PathBuf;
use std::time::{Duration, UNIX_EPOCH};

use msync::compiler::CompileContext;
use msync::compiler::structured::Session;
use msync::execution::{self, Options, RuntimeSession};
use msync::filesystem::{InvocationRoot, SnapshotStore};
use msync::pipeline;

#[allow(dead_code)]
#[path = "support/statement_storage.rs"]
mod statement_storage;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("execution-workspace-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn set_modified(path: &Path) {
    let mut options = fs::OpenOptions::new();
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_FLAG_BACKUP_SEMANTICS, FILE_WRITE_ATTRIBUTES,
        };
        options
            .access_mode(FILE_WRITE_ATTRIBUTES)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS);
    }
    #[cfg(not(windows))]
    options.read(true);
    options
        .open(path)
        .unwrap()
        .set_times(
            fs::FileTimes::new().set_modified(UNIX_EPOCH + Duration::from_secs(1_000_000_000)),
        )
        .unwrap();
}

async fn run_live(runtime: &mut RuntimeSession, compiler: &mut Session, cwd: &Path, script: &str) {
    let context = CompileContext {
        pwd: cwd.to_owned(),
        source_file: None,
    };
    let compilation = pipeline::compile(compiler, script, &context, &[]).unwrap();
    tokio::time::timeout(
        Duration::from_secs(10),
        runtime.execute(&compilation.program),
    )
    .await
    .unwrap()
    .unwrap();
}

fn assert_scratch_contents(path: &Path, users: &[&str], runtime: Option<&RuntimeSession>) {
    let private = runtime.and_then(|runtime| runtime.private_root().unwrap());
    let stage_record = runtime.and_then(|runtime| runtime.private_stage_record().unwrap());
    let mut expected = users
        .iter()
        .map(|name| std::ffi::OsString::from(*name))
        .collect::<std::collections::BTreeSet<_>>();
    for entry in fs::read_dir(path).unwrap() {
        let entry = entry.unwrap();
        if expected.remove(&entry.file_name()) {
            continue;
        }
        if entry.file_name() == ".msync-recovery-v1"
            || entry.file_name() == ".msync-remote-recovery-v1"
        {
            statement_storage::assert_registry_metadata_with_owner(&entry.path(), stage_record);
            continue;
        }
        let private = private.unwrap_or_else(|| {
            panic!(
                "unexpected retained scratch entry: {}",
                entry.path().display()
            )
        });
        assert!(
            same_file::is_same_file(entry.path(), private).unwrap(),
            "scratch entry is not the current verified runtime owner: {}",
            entry.path().display()
        );
        statement_storage::assert_private_root_metadata(&entry.path());
    }
    assert!(
        expected.is_empty(),
        "missing user scratch entries: {expected:?}"
    );
}

#[tokio::test]
async fn ordinary_missing_file_retires_payload_without_capturing_unrelated_children() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    let payload = vec![b'P'; 65536];
    fs::write(cwd.join("input"), &payload).unwrap();
    fs::write(cwd.join("unrelated"), vec![b'X'; 1024 * 1024]).unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
    let mut compiler = Session::new();
    run_live(&mut runtime, &mut compiler, &cwd, "input -> output\n").await;
    assert_eq!(fs::read(cwd.join("output")).unwrap(), payload);
    assert_scratch_contents(&cwd.join(".temp"), &[], Some(&runtime));
    assert_eq!(
        fs::read(cwd.join("unrelated")).unwrap(),
        vec![b'X'; 1024 * 1024]
    );
    run_live(
        &mut runtime,
        &mut compiler,
        &cwd,
        "$pwd::pwd -> $pwd::pwd/../copy/.\n",
    )
    .await;
    assert_eq!(
        fs::read(workspace.path().join("copy/output")).unwrap(),
        payload
    );
    assert_eq!(
        fs::read(workspace.path().join("copy/unrelated"))
            .unwrap()
            .len(),
        1024 * 1024
    );
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn sparse_files_extend_while_an_existing_file_flow_in_another_directory_is_active() {
    use tokio::io::AsyncWriteExt;
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    let other = workspace.path().join("other");
    fs::create_dir(&cwd).unwrap();
    fs::create_dir(&other).unwrap();
    fs::write(cwd.join("a1"), b"first").unwrap();
    fs::write(cwd.join("a2"), b"second").unwrap();
    fs::create_dir(cwd.join("nested")).unwrap();
    fs::write(cwd.join("nested/a3"), b"third").unwrap();
    fs::write(other.join("existing"), b"old").unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
    let mut compiler = Session::new();
    run_live(&mut runtime, &mut compiler, &cwd, "a1 -> new1\n").await;
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let context = CompileContext {
        pwd: cwd.clone(),
        source_file: None,
    };
    let stream = pipeline::compile(
        &mut compiler,
        &format!(
            "wire => tcp://{}\nwire::/ -> $pwd::pwd/../other/existing\n",
            listener.local_addr().unwrap()
        ),
        &context,
        &[],
    )
    .unwrap();
    runtime.submit(&stream.program).await.unwrap();
    let mut peer = tokio::time::timeout(Duration::from_secs(5), listener.accept())
        .await
        .unwrap()
        .unwrap()
        .0;
    let second = pipeline::compile(&mut compiler, "a2 -> new2\n", &context, &[]).unwrap();
    tokio::time::timeout(Duration::from_secs(5), runtime.submit(&second.program))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(cwd.join("new2")).unwrap(), b"second");
    let third = pipeline::compile(&mut compiler, "nested/a3 -> new3\n", &context, &[]).unwrap();
    runtime.submit(&third.program).await.unwrap();
    assert_eq!(fs::read(cwd.join("new3")).unwrap(), b"third");
    let fourth = pipeline::compile(&mut compiler, "\"fourth\" -> new4\n", &context, &[]).unwrap();
    runtime.submit(&fourth.program).await.unwrap();
    assert_eq!(fs::read(cwd.join("new4")).unwrap(), b"fourth");
    assert_eq!(fs::read(other.join("existing")).unwrap(), b"old");
    peer.write_all(b"new").await.unwrap();
    peer.shutdown().await.unwrap();
    tokio::time::timeout(Duration::from_secs(5), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(other.join("existing")).unwrap(), b"new");
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn sparse_sibling_parents_promote_before_enrolling_their_common_parent() {
    use tokio::io::AsyncWriteExt;

    let workspace = workspace();
    let common = workspace.path().join("work");
    let first = common.join("a1");
    let second = common.join("a2");
    let other = workspace.path().join("other");
    fs::create_dir_all(first.join("nested")).unwrap();
    fs::create_dir(&second).unwrap();
    fs::create_dir(&other).unwrap();
    fs::write(first.join("seed"), b"first").unwrap();
    fs::write(second.join("seed"), b"second").unwrap();
    fs::write(first.join("nested/input"), b"nested payload").unwrap();
    fs::write(second.join("unobserved"), b"untouched sibling").unwrap();
    fs::write(other.join("existing"), b"old").unwrap();
    let scratch = workspace.path().join("state");
    let mut runtime = RuntimeSession::new(&Options::new(scratch.clone())).unwrap();
    let mut compiler = Session::new();
    run_live(&mut runtime, &mut compiler, &first, "seed -> copy\n").await;
    run_live(&mut runtime, &mut compiler, &second, "seed -> copy\n").await;
    assert_scratch_contents(&scratch, &[], Some(&runtime));
    run_live(
        &mut runtime,
        &mut compiler,
        &common,
        "\"parent payload\" -> parent-file\n",
    )
    .await;

    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let context = CompileContext {
        pwd: other.clone(),
        source_file: None,
    };
    let stream = pipeline::compile(
        &mut compiler,
        &format!(
            "wire => tcp://{}\nwire::/ -> existing\n",
            listener.local_addr().unwrap()
        ),
        &context,
        &[],
    )
    .unwrap();
    tokio::time::timeout(Duration::from_secs(5), runtime.submit(&stream.program))
        .await
        .unwrap()
        .unwrap();
    let mut peer = tokio::time::timeout(Duration::from_secs(5), listener.accept())
        .await
        .unwrap()
        .unwrap()
        .0;
    assert!(!runtime.work_ids().is_empty());

    let context = CompileContext {
        pwd: first.clone(),
        source_file: None,
    };
    let copy = pipeline::compile(
        &mut compiler,
        "nested/input -> nested-copy\n",
        &context,
        &[],
    )
    .unwrap();
    tokio::time::timeout(Duration::from_secs(5), runtime.submit(&copy.program))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(
        fs::read(first.join("nested-copy")).unwrap(),
        b"nested payload"
    );
    assert_eq!(
        fs::read(common.join("parent-file")).unwrap(),
        b"parent payload"
    );
    assert_eq!(fs::read(first.join("copy")).unwrap(), b"first");
    assert_eq!(fs::read(second.join("copy")).unwrap(), b"second");
    assert_eq!(
        fs::read(second.join("unobserved")).unwrap(),
        b"untouched sibling"
    );
    assert_eq!(fs::read(other.join("existing")).unwrap(), b"old");
    assert!(!runtime.work_ids().is_empty());
    peer.write_all(b"new").await.unwrap();
    peer.shutdown().await.unwrap();
    tokio::time::timeout(Duration::from_secs(5), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(other.join("existing")).unwrap(), b"new");
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn sparse_root_upgrade_preserves_an_active_immutable_reader_of_a_known_file() {
    use tokio::io::AsyncReadExt;
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    fs::create_dir(cwd.join("nested")).unwrap();
    let payload = vec![b'R'; 4 * 1024 * 1024];
    fs::write(cwd.join("a1"), &payload).unwrap();
    fs::write(cwd.join("nested/a2"), b"second").unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
    let mut compiler = Session::new();
    run_live(&mut runtime, &mut compiler, &cwd, "a1 -> new1\n").await;
    let socket = socket2::Socket::new(
        socket2::Domain::IPV4,
        socket2::Type::STREAM,
        Some(socket2::Protocol::TCP),
    )
    .unwrap();
    socket.set_recv_buffer_size(1024).unwrap();
    socket.set_nonblocking(true).unwrap();
    socket
        .bind(&std::net::SocketAddr::from(([127, 0, 0, 1], 0)).into())
        .unwrap();
    socket.listen(4).unwrap();
    let listener = tokio::net::TcpListener::from_std(socket.into()).unwrap();
    let context = CompileContext {
        pwd: cwd.clone(),
        source_file: None,
    };
    let stream = pipeline::compile(
        &mut compiler,
        &format!(
            "sink => tcp://{}\na1 -> sink::/\n",
            listener.local_addr().unwrap()
        ),
        &context,
        &[],
    )
    .unwrap();
    runtime.submit(&stream.program).await.unwrap();
    let mut peer = tokio::time::timeout(Duration::from_secs(5), listener.accept())
        .await
        .unwrap()
        .unwrap()
        .0;
    assert!(!runtime.work_ids().is_empty());
    let second = pipeline::compile(&mut compiler, "nested/a2 -> new2\n", &context, &[]).unwrap();
    tokio::time::timeout(Duration::from_secs(5), runtime.submit(&second.program))
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(cwd.join("new2")).unwrap(), b"second");
    socket2::SockRef::from(&peer)
        .set_recv_buffer_size(1024 * 1024)
        .unwrap();
    let receiver = tokio::spawn(async move {
        let mut bytes = Vec::new();
        peer.read_to_end(&mut bytes).await.unwrap();
        bytes
    });
    tokio::time::timeout(Duration::from_secs(15), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(receiver.await.unwrap(), payload);
    runtime.shutdown().await.unwrap();
}

#[test]
#[ignore = "child fixture launched by real-program workspace journeys"]
fn sparse_view_child_fixture() {
    assert_eq!(fs::read("seed").unwrap(), b"seed");
    for root in
        std::env::args().filter_map(|argument| argument.strip_prefix("root:").map(str::to_owned))
    {
        let root = Path::new(&root);
        assert_eq!(fs::read(root.join("input")).unwrap(), b"managed");
        assert_eq!(fs::read(root.join("output")).unwrap(), b"managed");
        assert_eq!(
            fs::read(root.join("extra")).unwrap(),
            b"previously unobserved"
        );
        assert_eq!(
            fs::read(root.join(".temp/user-visible")).unwrap(),
            b"user data stays visible"
        );
    }
    fs::write("native-file", b"external program write").unwrap();
}

#[tokio::test]
async fn program_observes_prior_file_publication_in_its_real_working_directory() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
    let mut compiler = Session::new();
    run_live(&mut runtime, &mut compiler, &cwd, "\"seed\" -> seed\n").await;
    let executable = std::env::current_exe()
        .unwrap()
        .to_string_lossy()
        .replace('\\', "\\\\")
        .replace('"', "\\\"");
    run_live(&mut runtime, &mut compiler, &cwd,
        &format!("child => $local::\"{executable}\"(\"--exact\", \"sparse_view_child_fixture\", \"--ignored\", \"--nocapture\")\nchild::exit_code -> code\n")).await;
    assert_eq!(fs::read(cwd.join("seed")).unwrap(), b"seed");
    assert_eq!(
        fs::read(cwd.join("native-file")).unwrap(),
        b"external program write"
    );
    assert_eq!(fs::read(cwd.join("code")).unwrap(), b"0\n");
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
async fn program_observes_prior_publications_and_user_scratch_in_other_directories() {
    let workspace = workspace();
    let a1 = workspace.path().join("a1");
    let a2 = workspace.path().join("a2");
    let b = workspace.path().join("b");
    for directory in [&a1, &a2, &b] {
        fs::create_dir(directory).unwrap();
    }
    for directory in [&a1, &a2] {
        fs::write(directory.join("input"), b"managed").unwrap();
        fs::write(directory.join("extra"), b"previously unobserved").unwrap();
    }
    fs::write(b.join("seed"), b"seed").unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(a1.join(".temp"))).unwrap();
    let mut compiler = Session::new();
    let quoted = |path: &Path| {
        path.to_string_lossy()
            .replace('\\', "\\\\")
            .replace('"', "\\\"")
    };
    run_live(
        &mut runtime,
        &mut compiler,
        &a1,
        &format!(
            "input -> output\n$local::\"{}/input\" -> $local::\"{}/output\"\n",
            quoted(&a2),
            quoted(&a2)
        ),
    )
    .await;
    assert_scratch_contents(&a1.join(".temp"), &[], Some(&runtime));
    for directory in [&a1, &a2] {
        fs::create_dir_all(directory.join(".temp")).unwrap();
        fs::write(
            directory.join(".temp/user-visible"),
            b"user data stays visible",
        )
        .unwrap();
    }
    let executable = std::env::current_exe()
        .unwrap()
        .to_string_lossy()
        .replace('\\', "\\\\")
        .replace('"', "\\\"");
    let context = CompileContext {
        pwd: b.clone(),
        source_file: None,
    };
    let launch = pipeline::compile(&mut compiler,
        &format!("child => $local::\"{executable}\"(\"--exact\", \"sparse_view_child_fixture\", \"--ignored\", \"--nocapture\", \"--skip\", \"root:{}\", \"--skip\", \"root:{}\")\n", quoted(&a1), quoted(&a2)), &context, &[]).unwrap();
    runtime.execute(&launch.program).await.unwrap();
    let feed = pipeline::compile(
        &mut compiler,
        "child::stderr -> child-errors\nchild::exit_code -> code\n",
        &context,
        &runtime.live_processes(),
    )
    .unwrap();
    runtime.execute(&feed.program).await.unwrap();
    assert_eq!(
        fs::read(a1.join(".temp/user-visible")).unwrap(),
        b"user data stays visible"
    );
    for directory in [&a1, &a2] {
        assert_eq!(fs::read(directory.join("output")).unwrap(), b"managed");
        assert_eq!(
            fs::read(directory.join("extra")).unwrap(),
            b"previously unobserved"
        );
    }
    assert_eq!(
        fs::read(b.join("code")).unwrap(),
        b"0\n",
        "{}",
        fs::read_to_string(b.join("child-errors")).unwrap()
    );
    assert_eq!(
        fs::read(b.join("native-file")).unwrap(),
        b"external program write"
    );
    runtime.shutdown().await.unwrap();
}

#[cfg(unix)]
#[tokio::test]
async fn same_batch_nested_finite_sources_do_not_capture_the_outer_fifo_namespace() {
    use std::os::unix::ffi::OsStrExt;
    use std::os::unix::fs::{FileTypeExt, MetadataExt};

    let workspace = workspace();
    let common = workspace.path().join("work");
    let child = common.join("a1");
    fs::create_dir_all(child.join("nested")).unwrap();
    fs::write(child.join("nested/input"), b"nested payload").unwrap();
    fs::write(common.join("unobserved"), b"unrelated outer sibling").unwrap();
    let fifo_path = common.join("fifo");
    let fifo = std::ffi::CString::new(fifo_path.as_os_str().as_bytes()).unwrap();
    assert_eq!(unsafe { libc::mkfifo(fifo.as_ptr(), 0o600) }, 0);
    let before = fs::symlink_metadata(&fifo_path).unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(workspace.path().join("state"))).unwrap();
    run_live(
        &mut runtime,
        &mut Session::new(),
        &common,
        "a1/nested/input -> a1/copy\n\"parent payload\" -> parent-file\n",
    )
    .await;
    assert_eq!(fs::read(child.join("copy")).unwrap(), b"nested payload");
    assert_eq!(
        fs::read(common.join("parent-file")).unwrap(),
        b"parent payload"
    );
    assert_eq!(
        fs::read(common.join("unobserved")).unwrap(),
        b"unrelated outer sibling"
    );
    runtime.shutdown().await.unwrap();
    let after = fs::symlink_metadata(&fifo_path).unwrap();
    assert!(after.file_type().is_fifo());
    assert_eq!((after.dev(), after.ino()), (before.dev(), before.ino()));
}

#[cfg(unix)]
#[tokio::test]
async fn physical_parent_namespace_preserves_an_unobserved_fifo_above_sparse_children() {
    use std::os::unix::ffi::OsStrExt;
    use std::os::unix::fs::{FileTypeExt, MetadataExt};
    use tokio::io::AsyncWriteExt;

    let workspace = workspace();
    let common = workspace.path().join("work");
    let first = common.join("a1");
    let second = common.join("a2");
    fs::create_dir_all(&first).unwrap();
    fs::create_dir(&second).unwrap();
    fs::write(first.join("seed"), b"first").unwrap();
    fs::write(second.join("seed"), b"second").unwrap();
    fs::write(first.join("unobserved"), b"retained first sibling").unwrap();
    fs::write(second.join("unobserved"), b"retained second sibling").unwrap();
    let fifo_path = common.join("fifo");
    let fifo = std::ffi::CString::new(fifo_path.as_os_str().as_bytes()).unwrap();
    assert_eq!(unsafe { libc::mkfifo(fifo.as_ptr(), 0o600) }, 0);
    let before = fs::symlink_metadata(&fifo_path).unwrap();
    let scratch = workspace.path().join("state");
    let mut runtime = RuntimeSession::new(&Options::new(scratch.clone())).unwrap();
    let mut compiler = Session::new();
    run_live(&mut runtime, &mut compiler, &first, "seed -> copy\n").await;
    run_live(&mut runtime, &mut compiler, &second, "seed -> copy\n").await;
    assert_scratch_contents(&scratch, &[], Some(&runtime));

    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let context = CompileContext {
        pwd: common.clone(),
        source_file: None,
    };
    let stream = pipeline::compile(
        &mut compiler,
        &format!(
            "wire => tcp://{}\nwire::/ -> new\n",
            listener.local_addr().unwrap()
        ),
        &context,
        &[],
    )
    .unwrap();
    tokio::time::timeout(Duration::from_secs(5), runtime.submit(&stream.program))
        .await
        .unwrap()
        .unwrap();
    let mut peer = tokio::time::timeout(Duration::from_secs(5), listener.accept())
        .await
        .unwrap()
        .unwrap()
        .0;
    assert!(!runtime.work_ids().is_empty());
    assert!(!common.join("new").exists());
    peer.write_all(b"physical payload").await.unwrap();
    peer.shutdown().await.unwrap();
    tokio::time::timeout(Duration::from_secs(5), runtime.wait())
        .await
        .unwrap()
        .unwrap();
    assert_eq!(fs::read(common.join("new")).unwrap(), b"physical payload");
    assert_eq!(fs::read(first.join("copy")).unwrap(), b"first");
    assert_eq!(fs::read(second.join("copy")).unwrap(), b"second");
    assert_eq!(
        fs::read(first.join("unobserved")).unwrap(),
        b"retained first sibling"
    );
    assert_eq!(
        fs::read(second.join("unobserved")).unwrap(),
        b"retained second sibling"
    );
    runtime.shutdown().await.unwrap();
    let after = fs::symlink_metadata(&fifo_path).unwrap();
    assert!(after.file_type().is_fifo());
    assert_eq!((after.dev(), after.ino()), (before.dev(), before.ino()));
}

#[cfg(unix)]
#[tokio::test]
async fn finite_missing_file_preserves_unobserved_fifo_and_socket_siblings() {
    use std::os::unix::ffi::OsStrExt;
    use std::os::unix::fs::{FileTypeExt, MetadataExt};
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    fs::write(cwd.join("input"), b"finite").unwrap();
    let fifo = std::ffi::CString::new(cwd.join("fifo").as_os_str().as_bytes()).unwrap();
    assert_eq!(unsafe { libc::mkfifo(fifo.as_ptr(), 0o600) }, 0);
    let listener = std::os::unix::net::UnixListener::bind(cwd.join("socket")).unwrap();
    listener.set_nonblocking(true).unwrap();
    let pipe_before = fs::symlink_metadata(cwd.join("fifo")).unwrap();
    let socket_before = fs::symlink_metadata(cwd.join("socket")).unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
    run_live(&mut runtime, &mut Session::new(), &cwd, "input -> output\n").await;
    assert_eq!(fs::read(cwd.join("output")).unwrap(), b"finite");
    assert_scratch_contents(&cwd.join(".temp"), &[], Some(&runtime));
    let pipe_after = fs::symlink_metadata(cwd.join("fifo")).unwrap();
    let socket_after = fs::symlink_metadata(cwd.join("socket")).unwrap();
    assert!(pipe_after.file_type().is_fifo() && socket_after.file_type().is_socket());
    assert_eq!(
        (pipe_after.dev(), pipe_after.ino()),
        (pipe_before.dev(), pipe_before.ino())
    );
    assert_eq!(
        (socket_after.dev(), socket_after.ino()),
        (socket_before.dev(), socket_before.ino())
    );
    assert_eq!(
        listener.accept().unwrap_err().kind(),
        std::io::ErrorKind::WouldBlock
    );
    runtime.shutdown().await.unwrap();
}

#[test]
fn capture_omits_only_the_current_owned_invocation_and_retains_user_temp_data() {
    let workspace = workspace();
    let source = workspace.path().join("work");
    let scratch = source.join(".temp");
    fs::create_dir_all(&scratch).unwrap();
    fs::write(source.join("source"), b"source").unwrap();
    fs::write(scratch.join("user-sentinel"), b"preserve user temp").unwrap();
    set_modified(&scratch);
    let before = fs::metadata(&scratch).unwrap().modified().unwrap();
    let invocation = InvocationRoot::create(&scratch).unwrap();
    assert!(invocation.scaffold_directory(&scratch).unwrap().is_none());
    let mut store = SnapshotStore::new_in(&invocation.path().join("assets"), &invocation).unwrap();
    assert!(
        store.capture(&source).is_err(),
        "unqualified capture retains its strict self-capture guard"
    );
    let snapshot = store.capture_in(&source, &invocation).unwrap();
    assert!(
        snapshot
            .entries()
            .contains_key(Path::new(".temp/user-sentinel"))
    );
    assert!(snapshot.excludes_invocation(&invocation).unwrap());
    assert!(
        snapshot
            .excludes_private_path(&invocation.path().join("future-output"))
            .unwrap()
    );
    let relative = Path::new(".temp").join(invocation.path().file_name().unwrap());
    assert!(
        !snapshot
            .entries()
            .keys()
            .any(|path| path.starts_with(&relative))
    );
    assert!(store.capture_in(invocation.path(), &invocation).is_err());
    let mut forged = snapshot.entries().clone();
    forged.insert(relative.clone(), snapshot.entries()[Path::new("")].clone());
    assert!(store.from_entries_in(forged, &source, &invocation).is_err());
    #[cfg(windows)]
    {
        let alias = Path::new(".temp").join(
            invocation
                .path()
                .file_name()
                .unwrap()
                .to_string_lossy()
                .to_uppercase(),
        );
        let mut forged = snapshot.entries().clone();
        forged.insert(alias, snapshot.entries()[Path::new("")].clone());
        assert!(store.from_entries_in(forged, &source, &invocation).is_err());
    }
    let entries = snapshot.entries().clone();
    let rebuilt = store
        .from_entries_in(entries, &source, &invocation)
        .unwrap();
    assert!(rebuilt.excludes_invocation(&invocation).unwrap());
    drop(rebuilt);
    drop(snapshot);
    store.cleanup().unwrap();
    invocation.cleanup().unwrap();
    assert_eq!(
        fs::read(scratch.join("user-sentinel")).unwrap(),
        b"preserve user temp"
    );
    assert_eq!(fs::metadata(&scratch).unwrap().modified().unwrap(), before);
    assert_eq!(fs::read_dir(&scratch).unwrap().count(), 1);
}

#[test]
fn fresh_internal_scratch_is_not_invented_as_logical_source_data() {
    let workspace = workspace();
    let source = workspace.path().join("work");
    fs::create_dir(&source).unwrap();
    fs::write(source.join("source"), b"source").unwrap();
    set_modified(&source);
    let before = fs::metadata(&source).unwrap().modified().unwrap();
    let scratch = source.join(".temp");
    let invocation = InvocationRoot::create(&scratch).unwrap();
    assert!(invocation.scaffold_directory(&scratch).unwrap().is_some());
    assert!(invocation.scaffold_directory(&source).unwrap().is_none());
    let mut store = SnapshotStore::new_in(&invocation.path().join("assets"), &invocation).unwrap();
    let snapshot = store.capture_in(&source, &invocation).unwrap();
    assert!(!snapshot.entries().contains_key(Path::new(".temp")));
    assert_eq!(
        snapshot.entries()[Path::new("")].metadata().modified,
        before
    );
    drop(snapshot);
    store.cleanup().unwrap();
    invocation.cleanup().unwrap();
    assert_eq!(fs::metadata(&source).unwrap().modified().unwrap(), before);
    assert!(
        scratch.is_dir(),
        "configured scratch remains available to callers"
    );
    assert_eq!(fs::read_dir(&scratch).unwrap().count(), 0);
}

#[test]
fn replacing_the_owned_identity_does_not_authorize_cleaning_the_foreign_replacement() {
    let workspace = workspace();
    let invocation = InvocationRoot::create(workspace.path()).unwrap();
    let moved = workspace.path().join("retained-original");
    fs::rename(invocation.path(), &moved).unwrap();
    fs::create_dir(invocation.path()).unwrap();
    fs::write(invocation.path().join("foreign"), b"not owned").unwrap();
    assert!(invocation.verify().is_err());
    assert!(invocation.cleanup().is_err());
    assert_eq!(
        fs::read(invocation.path().join("foreign")).unwrap(),
        b"not owned"
    );
}

#[test]
fn live_stores_and_snapshots_prevent_early_invocation_cleanup() {
    let workspace = workspace();
    fs::write(workspace.path().join("source"), b"retained bytes").unwrap();
    let invocation = InvocationRoot::create(workspace.path()).unwrap();
    let mut store = SnapshotStore::new_in(&invocation.path().join("assets"), &invocation).unwrap();
    let snapshot = store.capture(&workspace.path().join("source")).unwrap();
    assert!(invocation.cleanup().is_err());
    assert!(store.cleanup().is_err());
    assert!(invocation.path().exists());
    drop(snapshot);
    store.cleanup().unwrap();
    invocation.cleanup().unwrap();
    assert!(!invocation.path().exists());
    invocation.cleanup().unwrap();
}

fn run(cwd: &Path, source: &str) -> execution::ExecutionReport {
    let context = CompileContext {
        pwd: cwd.to_owned(),
        source_file: None,
    };
    let compilation = pipeline::compile(&mut Session::new(), source, &context, &[]).unwrap();
    execution::execute(&compilation.program, &Options::new(cwd.join(".temp"))).unwrap()
}

#[test]
fn copying_the_cwd_with_default_scratch_preserves_user_temp_without_recursive_runtime_data() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir_all(cwd.join(".temp")).unwrap();
    fs::write(cwd.join("source"), b"copied source").unwrap();
    fs::write(cwd.join(".temp/user-sentinel"), b"copied user temp").unwrap();
    let report = run(&cwd, "$pwd::pwd -> $pwd::pwd/../out/.\n");
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
    assert_eq!(
        fs::read(workspace.path().join("out/source")).unwrap(),
        b"copied source"
    );
    assert_eq!(
        fs::read(workspace.path().join("out/.temp/user-sentinel")).unwrap(),
        b"copied user temp"
    );
    assert_eq!(
        fs::read_dir(workspace.path().join("out/.temp"))
            .unwrap()
            .count(),
        1
    );
    assert_scratch_contents(&cwd.join(".temp"), &["user-sentinel"], None);
}

#[test]
fn cleanup_preserves_committed_user_entries_and_namespace_time_in_configured_scratch() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    let scratch = cwd.join(".temp");
    fs::create_dir_all(&scratch).unwrap();
    fs::write(scratch.join("sentinel"), b"existing user data").unwrap();
    set_modified(&scratch);
    let before = fs::metadata(&scratch).unwrap().modified().unwrap();
    let report = run(&cwd, "\"committed\" -> .temp/result\n");
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
    assert_eq!(fs::read(scratch.join("result")).unwrap(), b"committed");
    assert_eq!(
        fs::read(scratch.join("sentinel")).unwrap(),
        b"existing user data"
    );
    assert!(fs::metadata(&scratch).unwrap().modified().unwrap() > before);
    assert_scratch_contents(&scratch, &["result", "sentinel"], None);
}

#[test]
fn fresh_configured_scratch_accepts_an_ordinary_user_target() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    let report = run(&cwd, "\"committed\" -> $pwd::pwd/.temp/user-file\n");
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
    assert_eq!(fs::read(cwd.join(".temp/user-file")).unwrap(), b"committed");
    assert_scratch_contents(&cwd.join(".temp"), &["user-file"], None);
}

#[tokio::test]
async fn finite_runtime_accepts_and_copies_fresh_scratch_user_entries() {
    for existing_scratch in [false, true] {
        let workspace = workspace();
        let cwd = workspace.path().join("work");
        fs::create_dir(&cwd).unwrap();
        if existing_scratch {
            fs::create_dir(cwd.join(".temp")).unwrap();
            fs::write(cwd.join(".temp/sentinel"), b"retain user data").unwrap();
        }
        let context = CompileContext {
            pwd: cwd.clone(),
            source_file: None,
        };
        let mut compiler = Session::new();
        let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
        for script in [
            "\"committed\" -> $pwd::pwd/.temp/user-file\n",
            "$pwd::pwd/.temp -> $pwd::pwd/../copied/.\n",
        ] {
            let compilation = pipeline::compile(&mut compiler, script, &context, &[]).unwrap();
            runtime.execute(&compilation.program).await.unwrap();
            if script.starts_with('"') {
                assert_eq!(fs::read(cwd.join(".temp/user-file")).unwrap(), b"committed");
            }
            if script.starts_with("$pwd") {
                assert_eq!(
                    fs::read(workspace.path().join("copied/user-file")).unwrap(),
                    b"committed"
                );
                assert_eq!(
                    fs::read_dir(workspace.path().join("copied"))
                        .unwrap()
                        .count(),
                    if existing_scratch { 2 } else { 1 }
                );
            }
        }
        runtime.shutdown().await.unwrap();
        assert_eq!(fs::read(cwd.join(".temp/user-file")).unwrap(), b"committed");
        let expected = if existing_scratch {
            vec!["sentinel", "user-file"]
        } else {
            vec!["user-file"]
        };
        assert_scratch_contents(&cwd.join(".temp"), &expected, None);
        assert_eq!(
            fs::read(workspace.path().join("copied/user-file")).unwrap(),
            b"committed"
        );
        if existing_scratch {
            assert_eq!(
                fs::read(cwd.join(".temp/sentinel")).unwrap(),
                b"retain user data"
            );
        }
    }
}

#[test]
#[ignore = "child fixture launched by the real-program workspace journey"]
fn scratch_namespace_child_fixture() {
    fs::create_dir_all(".temp").unwrap();
    fs::write(".temp/user-file", b"native child bytes").unwrap();
}

#[test]
#[ignore = "child fixture launched by the real-program workspace journey"]
fn deferred_ancestor_child_fixture() {
    fs::remove_file("node").unwrap();
    fs::create_dir("node").unwrap();
    fs::write("node/x", b"producer bytes").unwrap();
    println!("producer stdout");
}

#[tokio::test]
async fn program_completion_gates_future_source_and_destination_traversal() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    fs::write(cwd.join("node"), b"original ordinary file").unwrap();
    let executable = std::env::current_exe()
        .unwrap()
        .to_string_lossy()
        .replace('\\', "\\\\")
        .replace('"', "\\\"");
    let script = format!(
        "child => $local::\"{executable}\"(\"--exact\", \"deferred_ancestor_child_fixture\", \"--ignored\", \"--nocapture\")\n$pwd::pwd/node/../node/x -> observed\n\"literal bytes\" -> $pwd::pwd/node/../node/literal\n$pwd::pwd/node/../node/literal -> observed-literal\nchild::stdout -> $pwd::pwd/node/../node/stdout\n"
    );
    let context = CompileContext {
        pwd: cwd.clone(),
        source_file: None,
    };
    let mut compiler = Session::new();
    let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
    let compilation = pipeline::compile(&mut compiler, &script, &context, &[]).unwrap();
    tokio::time::timeout(
        std::time::Duration::from_secs(30),
        runtime.execute(&compilation.program),
    )
    .await
    .unwrap()
    .unwrap();
    assert_eq!(fs::read(cwd.join("observed")).unwrap(), b"producer bytes");
    assert_eq!(
        fs::read(cwd.join("node/literal")).unwrap(),
        b"literal bytes"
    );
    assert_eq!(
        fs::read(cwd.join("observed-literal")).unwrap(),
        b"literal bytes"
    );
    assert!(
        fs::read_to_string(cwd.join("node/stdout"))
            .unwrap()
            .contains("producer stdout")
    );
    runtime.shutdown().await.unwrap();
    assert!(cwd.join("node").is_dir());
    assert_eq!(fs::read(cwd.join("observed")).unwrap(), b"producer bytes");
    assert_eq!(
        fs::read(cwd.join("observed-literal")).unwrap(),
        b"literal bytes"
    );
    assert_scratch_contents(&cwd.join(".temp"), &[], None);
}

#[tokio::test]
async fn program_creates_user_scratch_then_following_block_copies_it() {
    for existing_scratch in [false, true] {
        let workspace = workspace();
        let cwd = workspace.path().join("work");
        fs::create_dir(&cwd).unwrap();
        if existing_scratch {
            fs::create_dir(cwd.join(".temp")).unwrap();
            fs::write(cwd.join(".temp/sentinel"), b"retain user data").unwrap();
        }
        let executable = std::env::current_exe()
            .unwrap()
            .to_string_lossy()
            .replace('\\', "\\\\")
            .replace('"', "\\\"");
        let launch = format!(
            "child => $local::\"{executable}\"(\"--exact\", \"scratch_namespace_child_fixture\", \"--ignored\", \"--nocapture\")\n"
        );
        let context = CompileContext {
            pwd: cwd.clone(),
            source_file: None,
        };
        let mut compiler = Session::new();
        let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
        for script in [
            launch.as_str(),
            "$pwd::pwd/.temp -> $pwd::pwd/../copied/.\n",
        ] {
            let compilation = pipeline::compile(&mut compiler, script, &context, &[]).unwrap();
            tokio::time::timeout(
                std::time::Duration::from_secs(30),
                runtime.execute(&compilation.program),
            )
            .await
            .unwrap()
            .unwrap();
        }
        runtime.shutdown().await.unwrap();
        assert_eq!(
            fs::read(cwd.join(".temp/user-file")).unwrap(),
            b"native child bytes"
        );
        assert_eq!(
            fs::read(workspace.path().join("copied/user-file")).unwrap(),
            b"native child bytes"
        );
        let expected = if existing_scratch {
            vec!["sentinel", "user-file"]
        } else {
            vec!["user-file"]
        };
        assert_scratch_contents(&cwd.join(".temp"), &expected, None);
        assert_eq!(
            fs::read_dir(workspace.path().join("copied"))
                .unwrap()
                .count(),
            if existing_scratch { 2 } else { 1 }
        );
        if existing_scratch {
            assert_eq!(
                fs::read(cwd.join(".temp/sentinel")).unwrap(),
                b"retain user data"
            );
        }
    }
}

#[cfg(windows)]
#[test]
fn ordinary_and_verbatim_aliases_cannot_bypass_the_owned_exclusion() {
    let workspace = workspace();
    let invocation = InvocationRoot::create(workspace.path()).unwrap();
    let mut store = SnapshotStore::new_in(&invocation.path().join("assets"), &invocation).unwrap();
    let text = invocation.path().to_string_lossy();
    let ordinary = PathBuf::from(text.strip_prefix(r"\\?\").unwrap_or(&text));
    assert!(store.capture_in(&ordinary, &invocation).is_err());
    let root_text = workspace.path().to_string_lossy();
    let ordinary_root = PathBuf::from(root_text.strip_prefix(r"\\?\").unwrap_or(&root_text));
    let snapshot = store.capture_in(&ordinary_root, &invocation).unwrap();
    assert!(snapshot.excludes_invocation(&invocation).unwrap());
    assert!(
        !snapshot
            .entries()
            .keys()
            .any(|path| path.starts_with(invocation.path().file_name().unwrap()))
    );
    drop(snapshot);
    store.cleanup().unwrap();
    invocation.cleanup().unwrap();
}
