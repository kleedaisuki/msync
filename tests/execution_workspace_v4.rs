use std::fs;
use std::path::Path;
#[cfg(windows)]
use std::path::PathBuf;
use std::time::{Duration, UNIX_EPOCH};

use msync::compiler::CompileContext;
use msync::compiler::structured::Session;
use msync::execution::{self, Options, RuntimeSession};
use msync::pipeline;
use msync::transaction::filesystem::{InvocationRoot, SnapshotStore};

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

fn private_files(path: &Path) -> (usize, u64) {
    let mut files = 0;
    let mut bytes = 0;
    for entry in fs::read_dir(path).unwrap() {
        let entry = entry.unwrap();
        let metadata = entry.metadata().unwrap();
        if metadata.is_dir() {
            let (nested_files, nested_bytes) = private_files(&entry.path());
            files += nested_files;
            bytes += nested_bytes;
        } else {
            files += 1;
            bytes += metadata.len();
        }
    }
    (files, bytes)
}

#[tokio::test]
async fn missing_file_enrolls_payload_and_parent_metadata_not_unrelated_children() {
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
    assert_eq!(private_files(&cwd.join(".temp")), (1, 65536));
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
async fn sparse_named_origin_restores_parent_time_and_unknown_siblings() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    fs::write(cwd.join("kept"), b"unobserved").unwrap();
    set_modified(&cwd);
    let before = fs::metadata(&cwd).unwrap().modified().unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
    let mut compiler = Session::new();
    run_live(
        &mut runtime,
        &mut compiler,
        &cwd,
        "transaction A:\n    \"new\" -> result\n    commit\n",
    )
    .await;
    run_live(&mut runtime, &mut compiler, &cwd, "rollback A\n").await;
    assert!(!cwd.join("result").exists());
    assert_eq!(fs::read(cwd.join("kept")).unwrap(), b"unobserved");
    assert_eq!(fs::metadata(&cwd).unwrap().modified().unwrap(), before);
    runtime.shutdown().await.unwrap();
    assert_eq!(fs::metadata(&cwd).unwrap().modified().unwrap(), before);
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
#[ignore = "native child fixture, launched only by the sparse-to-complete view test"]
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
        assert!(
            !root.join(".temp").exists(),
            "runtime backing leaked into the native view"
        );
    }
    fs::write("native-file", b"private native write").unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn sparse_file_coverage_upgrades_to_a_complete_native_program_view() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
    runtime.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
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
        b"private native write"
    );
    assert_eq!(fs::read(cwd.join("code")).unwrap(), b"0\n");
    runtime.shutdown().await.unwrap();
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn sparse_parents_in_other_directories_upgrade_before_a_new_native_view() {
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
    runtime.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
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
    assert_eq!(
        private_files(&a1.join(".temp")),
        (2, 14),
        "both parents must initially remain sparse"
    );
    let executable = std::env::current_exe()
        .unwrap()
        .to_string_lossy()
        .replace('\\', "\\\\")
        .replace('"', "\\\"");
    run_live(&mut runtime, &mut compiler, &b,
        &format!("child => $local::\"{executable}\"(\"--exact\", \"sparse_view_child_fixture\", \"--ignored\", \"--nocapture\", \"--skip\", \"root:{}\", \"--skip\", \"root:{}\")\nchild::exit_code -> code\n", quoted(&a1), quoted(&a2))).await;
    for directory in [&a1, &a2] {
        assert_eq!(fs::read(directory.join("output")).unwrap(), b"managed");
        assert_eq!(
            fs::read(directory.join("extra")).unwrap(),
            b"previously unobserved"
        );
    }
    assert_eq!(
        fs::read(b.join("native-file")).unwrap(),
        b"private native write"
    );
    assert_eq!(fs::read(b.join("code")).unwrap(), b"0\n");
    runtime.shutdown().await.unwrap();
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
    assert_eq!(private_files(&cwd.join(".temp")), (1, 6));
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
    assert_eq!(fs::read_dir(cwd.join(".temp")).unwrap().count(), 1);
}

#[test]
fn origin_undo_restores_the_containing_namespace_with_default_internal_scratch() {
    for existing_scratch in [false, true] {
        let workspace = workspace();
        let cwd = workspace.path().join("work");
        fs::create_dir(&cwd).unwrap();
        if existing_scratch {
            fs::create_dir(cwd.join(".temp")).unwrap();
        }
        fs::write(cwd.join("kept"), b"unchanged").unwrap();
        set_modified(&cwd);
        let before = fs::metadata(&cwd).unwrap().modified().unwrap();
        let report = run(
            &cwd,
            "transaction A:\n    \"new\" -> result\n    commit\n    \"later\" -> other\nrollback A\n",
        );
        assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
        assert!(!cwd.join("result").exists());
        assert!(!cwd.join("other").exists());
        assert_eq!(fs::read(cwd.join("kept")).unwrap(), b"unchanged");
        assert_eq!(fs::metadata(&cwd).unwrap().modified().unwrap(), before);
        assert_eq!(fs::read_dir(cwd.join(".temp")).unwrap().count(), 0);
    }
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
    assert_eq!(fs::read_dir(&scratch).unwrap().count(), 2);
}

#[test]
fn fresh_configured_scratch_accepts_an_ordinary_user_target() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    let report = run(&cwd, "\"committed\" -> $pwd::pwd/.temp/user-file\n");
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
    assert_eq!(fs::read(cwd.join(".temp/user-file")).unwrap(), b"committed");
    assert_eq!(fs::read_dir(cwd.join(".temp")).unwrap().count(), 1);
}

#[test]
fn fresh_scratch_user_namespace_can_be_copied_and_rolled_back() {
    let workspace = workspace();
    let cwd = workspace.path().join("work");
    fs::create_dir(&cwd).unwrap();
    set_modified(&cwd);
    let before = fs::metadata(&cwd).unwrap().modified().unwrap();
    let report = run(
        &cwd,
        "transaction A:\n    \"committed\" -> $pwd::pwd/.temp/user-file\n    commit\n    $pwd::pwd/.temp -> $pwd::pwd/copied/.\nrollback A\n",
    );
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
    assert!(!cwd.join(".temp/user-file").exists());
    assert!(!cwd.join("copied").exists());
    assert_eq!(fs::read_dir(cwd.join(".temp")).unwrap().count(), 0);
    assert_eq!(fs::metadata(&cwd).unwrap().modified().unwrap(), before);
}

#[tokio::test]
async fn finite_runtime_accepts_fresh_scratch_user_entries_then_named_undo() {
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
            "transaction A:\n    \"committed\" -> $pwd::pwd/.temp/user-file\n",
            "$pwd::pwd/.temp -> $pwd::pwd/../copied/.\n",
            "rollback A\n",
        ] {
            let compilation = pipeline::compile(&mut compiler, script, &context, &[]).unwrap();
            runtime.execute(&compilation.program).await.unwrap();
            if script.starts_with("transaction") {
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
        assert!(!cwd.join(".temp/user-file").exists());
        assert_eq!(
            fs::read_dir(cwd.join(".temp")).unwrap().count(),
            usize::from(existing_scratch)
        );
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
#[ignore = "native child fixture, launched only by the mediated process test"]
fn scratch_namespace_child_fixture() {
    fs::create_dir_all(".temp").unwrap();
    fs::write(".temp/user-file", b"native child bytes").unwrap();
}

#[test]
#[ignore = "native child fixture, launched only by the mediated process test"]
fn deferred_ancestor_child_fixture() {
    fs::remove_file("node").unwrap();
    fs::create_dir("node").unwrap();
    fs::write("node/x", b"producer bytes").unwrap();
    println!("producer stdout");
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn native_import_gates_future_source_and_destination_traversal() {
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
        "transaction A:\n    child => $local::\"{executable}\"(\"--exact\", \"deferred_ancestor_child_fixture\", \"--ignored\", \"--nocapture\")\n    $pwd::pwd/node/../node/x -> observed\n    \"literal bytes\" -> $pwd::pwd/node/../node/literal\n    $pwd::pwd/node/../node/literal -> observed-literal\n    child::stdout -> $pwd::pwd/node/../node/stdout\n"
    );
    let context = CompileContext {
        pwd: cwd.clone(),
        source_file: None,
    };
    let mut compiler = Session::new();
    let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
    runtime.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
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
    let rollback = pipeline::compile(&mut compiler, "rollback A\n", &context, &[]).unwrap();
    runtime.execute(&rollback.program).await.unwrap();
    runtime.shutdown().await.unwrap();
    assert_eq!(
        fs::read(cwd.join("node")).unwrap(),
        b"original ordinary file"
    );
    assert!(!cwd.join("observed").exists());
    assert!(!cwd.join("observed-literal").exists());
    assert_eq!(fs::read_dir(cwd.join(".temp")).unwrap().count(), 0);
}

#[tokio::test]
#[ignore = "requires a provisioned native process-resource backend"]
async fn native_program_creates_user_scratch_then_copy_and_origin_undo() {
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
            "transaction A:\n    child => $local::\"{executable}\"(\"--exact\", \"scratch_namespace_child_fixture\", \"--ignored\", \"--nocapture\")\n"
        );
        let context = CompileContext {
            pwd: cwd.clone(),
            source_file: None,
        };
        let mut compiler = Session::new();
        let mut runtime = RuntimeSession::new(&Options::new(cwd.join(".temp"))).unwrap();
        runtime.set_worker(env!("CARGO_BIN_EXE_msync")).unwrap();
        for script in [
            launch.as_str(),
            "$pwd::pwd/.temp -> $pwd::pwd/../copied/.\n",
            "rollback A\n",
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
        assert!(!cwd.join(".temp/user-file").exists());
        assert_eq!(
            fs::read(workspace.path().join("copied/user-file")).unwrap(),
            b"native child bytes"
        );
        assert_eq!(
            fs::read_dir(cwd.join(".temp")).unwrap().count(),
            usize::from(existing_scratch)
        );
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
