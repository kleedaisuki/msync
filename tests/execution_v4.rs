use std::fs;
use std::path::Path;

use msync::compiler::CompileContext;
use msync::compiler::structured::Session;
use msync::execution::{self, ExecutionError, ExecutionReport, Options};
use msync::pipeline;
use proptest::prelude::*;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("execution-v4-")
        .tempdir_in(root)
        .unwrap()
}

fn run(root: &Path, source: &str) -> Result<ExecutionReport, ExecutionError> {
    let context = CompileContext {
        pwd: root.to_owned(),
        source_file: None,
    };
    let compilation = pipeline::compile(&mut Session::new(), source, &context, &[]).unwrap();
    execution::execute(&compilation.program, &Options::new(root.join("scratch")))
}

fn text(root: &Path, path: &str) -> String {
    fs::read_to_string(root.join(path)).unwrap()
}

#[test]
fn typed_macro_copies_a_real_tree_into_explicit_replacement_without_nesting() {
    let root = workspace();
    fs::create_dir_all(root.path().join(".codex/agents/empty")).unwrap();
    fs::write(root.path().join(".codex/agents/new.txt"), "new agent").unwrap();
    fs::create_dir_all(root.path().join("out/agents/old-directory")).unwrap();
    fs::write(
        root.path().join("out/agents/old-directory/old"),
        "remove this",
    )
    .unwrap();
    let report = run(root.path(), "macro copy_codex(.codex, des):\n    .codex/agents -> $pwd::des/agents/.\ncopy_codex($local::.codex, out)\n").unwrap();
    assert_eq!(text(root.path(), "out/agents/new.txt"), "new agent");
    assert!(root.path().join("out/agents/empty").is_dir());
    assert!(!root.path().join("out/agents/old-directory").exists());
    assert!(!root.path().join("out/agents/agents").exists());
    assert_eq!(text(root.path(), ".codex/agents/new.txt"), "new agent");
    assert_eq!(report.assignments, 1);
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
    assert_eq!(
        fs::read_dir(root.path().join("scratch")).unwrap().count(),
        0
    );
}

#[test]
fn utf8_and_nul_literal_bytes_are_exact_and_parents_publish_with_the_statement() {
    let root = workspace();
    let source = "\"可莉🌟\\n\0done\" -> deep/tree/result.txt\n";
    let report = run(root.path(), source).unwrap();
    assert_eq!(
        fs::read(root.path().join("deep/tree/result.txt")).unwrap(),
        "可莉🌟\n\0done".as_bytes()
    );
    assert_eq!(report.assigned_bytes, "可莉🌟\n\0done".len() as u64);
    assert!(report.cleanup.is_empty());
}

#[test]
fn checkpoints_local_rewind_and_named_origin_restore_the_requested_lifecycle() {
    let root = workspace();
    fs::write(root.path().join("output.txt"), "origin").unwrap();
    let source = "\"before\" -> before.txt\ntransaction A1(read uncommitted):\n    \"first\" -> output.txt\n    commit\n    \"0\" -> exit.txt\n    commit\n    \"pending\" -> wsl.txt\n    rollback\nrollback A1\n\"success\" -> result.txt\n";
    let report = run(root.path(), source).unwrap();
    assert_eq!(text(root.path(), "output.txt"), "origin");
    assert!(!root.path().join("exit.txt").exists());
    assert!(!root.path().join("wsl.txt").exists());
    assert_eq!(text(root.path(), "before.txt"), "before");
    assert_eq!(text(root.path(), "result.txt"), "success");
    assert_eq!(report.rollbacks, 2);
    assert!(report.cleanup.is_empty());
}

#[test]
fn local_rollback_keeps_the_latest_checkpoint_at_implicit_block_commit() {
    let root = workspace();
    run(root.path(), "transaction A:\n    \"one\" -> a\n    commit\n    \"two\" -> a\n    \"discard\" -> b\n    rollback\n").unwrap();
    assert_eq!(text(root.path(), "a"), "one");
    assert!(!root.path().join("b").exists());
}

#[test]
fn a_failed_named_region_restores_its_origin_but_not_prior_top_level_statements() {
    let root = workspace();
    fs::write(root.path().join("out"), "old").unwrap();
    let error = run(root.path(), "\"earlier\" -> before\ntransaction A:\n    \"changed\" -> out\n    commit\n    missing -> broken\n").unwrap_err();
    assert!(
        error
            .report
            .diagnostic
            .message
            .contains("source does not exist")
    );
    assert_eq!(text(root.path(), "out"), "old");
    assert_eq!(text(root.path(), "before"), "earlier");
    assert!(!root.path().join("broken").exists());
    assert!(error.recovery.is_empty(), "{error}");
    assert!(error.retained_paths.is_empty(), "{error}");
    assert_eq!(
        fs::read_dir(root.path().join("scratch")).unwrap().count(),
        0
    );
}

#[test]
fn a_selector_collision_does_not_publish_an_earlier_selected_file() {
    let root = workspace();
    fs::create_dir(root.path().join("source")).unwrap();
    fs::write(root.path().join("source/a"), "a").unwrap();
    fs::write(root.path().join("source/b"), "b").unwrap();
    fs::create_dir(root.path().join("dest")).unwrap();
    fs::write(root.path().join("dest/b"), "keep").unwrap();
    let error = run(root.path(), "source/<.*> -> dest/\n").unwrap_err();
    assert!(error.to_string().contains("already exists"));
    assert!(!root.path().join("dest/a").exists());
    assert_eq!(text(root.path(), "dest/b"), "keep");
}

#[test]
fn container_merge_retains_existing_names_and_does_not_overwrite_nested_files() {
    let root = workspace();
    fs::create_dir_all(root.path().join("source/nested")).unwrap();
    fs::write(root.path().join("source/a"), "a").unwrap();
    fs::write(root.path().join("source/nested/new"), "new").unwrap();
    fs::create_dir_all(root.path().join("dest/source/nested")).unwrap();
    fs::write(root.path().join("dest/source/retained"), "retained").unwrap();
    fs::write(root.path().join("dest/source/nested/other"), "other").unwrap();
    run(root.path(), "source -> dest/\n").unwrap();
    assert_eq!(text(root.path(), "dest/source/a"), "a");
    assert_eq!(text(root.path(), "dest/source/nested/new"), "new");
    assert_eq!(text(root.path(), "dest/source/nested/other"), "other");
    assert_eq!(text(root.path(), "dest/source/retained"), "retained");
}

#[test]
fn exact_directory_rejection_and_empty_selection_never_create_a_destination() {
    let root = workspace();
    fs::create_dir(root.path().join("source")).unwrap();
    fs::create_dir(root.path().join("existing")).unwrap();
    let error = run(root.path(), "source -> existing\n").unwrap_err();
    assert!(error.to_string().contains("existing directory"));
    run(root.path(), "source/<never> -> absent/\n").unwrap();
    assert!(!root.path().join("absent").exists());
}

#[test]
fn transaction_reads_its_own_pending_producers_and_prior_versions_remain_immutable() {
    let root = workspace();
    run(root.path(), "transaction A:\n    \"first\" -> a\n    a -> b\n    commit\n    \"second\" -> a\n    b -> c\n").unwrap();
    assert_eq!(text(root.path(), "a"), "second");
    assert_eq!(text(root.path(), "b"), "first");
    assert_eq!(text(root.path(), "c"), "first");
}

#[test]
fn same_file_and_overlapping_tree_assignments_are_refused_without_mutation() {
    let root = workspace();
    fs::write(root.path().join("source"), "original").unwrap();
    fs::hard_link(root.path().join("source"), root.path().join("alias")).unwrap();
    assert!(run(root.path(), "source -> alias\n").is_err());
    assert_eq!(text(root.path(), "source"), "original");
    assert_eq!(text(root.path(), "alias"), "original");
    fs::create_dir(root.path().join("tree")).unwrap();
    fs::write(root.path().join("tree/file"), "tree").unwrap();
    assert!(run(root.path(), "tree -> tree/child/.\n").is_err());
    assert!(!root.path().join("tree/child").exists());
}

#[test]
fn unsupported_process_effects_are_rejected_before_an_earlier_file_assignment_starts() {
    let root = workspace();
    let error = run(
        root.path(),
        "\"never\" -> out\nchild => $path::missing_executable()\n",
    )
    .unwrap_err();
    assert!(error.to_string().contains("verified process-resource view"));
    assert!(!root.path().join("out").exists());
    assert!(!root.path().join("scratch").exists());
}

#[test]
fn pre_cancelled_execution_does_not_capture_or_publish_anything() {
    let root = workspace();
    let context = CompileContext {
        pwd: root.path().to_owned(),
        source_file: None,
    };
    let compilation =
        pipeline::compile(&mut Session::new(), "\"never\" -> out\n", &context, &[]).unwrap();
    assert!(!root.path().join("out").exists());
    assert!(!root.path().join("scratch").exists());
    let options = Options::new(root.path().join("scratch"));
    options.cancel.cancel();
    assert!(execution::execute(&compilation.program, &options).is_err());
    assert!(!root.path().join("out").exists());
    assert!(!root.path().join("scratch").exists());
}

#[test]
fn finite_http_sources_are_captured_at_execution_then_published_atomically() {
    use std::io::{Read, Write};
    use std::net::TcpListener;
    use std::thread;

    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let server = thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        let mut request = [0; 4096];
        let count = stream.read(&mut request).unwrap();
        assert!(
            std::str::from_utf8(&request[..count])
                .unwrap()
                .starts_with("GET /source.txt HTTP/1.1\r\n")
        );
        stream
            .write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\nremote")
            .unwrap();
    });
    let source = format!("web => http://{address}\nweb::/source.txt -> out\n");
    let report = run(root.path(), &source).unwrap();
    server.join().unwrap();
    assert_eq!(text(root.path(), "out"), "remote");
    assert!(report.cleanup.is_empty());
}

#[test]
fn remote_transaction_capability_failure_precedes_any_literal_publication() {
    let root = workspace();
    let source = "\"never\" -> before\ntransaction A:\n    $wsl::source -> out\n";
    let error = run(root.path(), source).unwrap_err();
    assert!(error.to_string().contains("versioned endpoint view"));
    assert!(!root.path().join("before").exists());
    assert!(!root.path().join("scratch").exists());
}

#[test]
fn a_literal_container_error_preserves_prior_atomic_statements_and_creates_no_container() {
    let root = workspace();
    let error = run(
        root.path(),
        "\"before\" -> before\n\"unnamed\" -> missing/\n",
    )
    .unwrap_err();
    assert!(error.to_string().contains("no basename"));
    assert_eq!(text(root.path(), "before"), "before");
    assert!(!root.path().join("missing").exists());
}

#[test]
fn a_read_only_file_is_not_bypassed_by_immutable_publication() {
    let root = workspace();
    let target = root.path().join("out");
    fs::write(&target, "protected").unwrap();
    let mut permissions = fs::metadata(&target).unwrap().permissions();
    permissions.set_readonly(true);
    fs::set_permissions(&target, permissions).unwrap();
    let error = run(root.path(), "\"must not write\" -> out\n").unwrap_err();
    assert_eq!(text(root.path(), "out"), "protected");
    assert!(fs::metadata(&target).unwrap().permissions().readonly());
    assert!(error.to_string().contains("read-only"));
    let mut permissions = fs::metadata(&target).unwrap().permissions();
    #[cfg(windows)]
    {
        // Windows restores its read-only attribute, not Unix permission bits.
        #[allow(clippy::permissions_set_readonly_false)]
        permissions.set_readonly(false);
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        permissions.set_mode(0o600);
    }
    fs::set_permissions(&target, permissions).unwrap();
}

#[cfg(windows)]
#[test]
fn missing_windows_case_aliases_share_a_domain_without_changing_visible_names() {
    let root = workspace();
    run(
        root.path(),
        "\"first\" -> Output\nOutput -> copy\n\"second\" -> output\n",
    )
    .unwrap();
    assert_eq!(text(root.path(), "Output"), "second");
    assert_eq!(text(root.path(), "copy"), "first");
    let names = fs::read_dir(root.path())
        .unwrap()
        .map(|entry| entry.unwrap().file_name())
        .collect::<Vec<_>>();
    assert!(names.iter().any(|name| name == "Output"));
    assert!(!names.iter().any(|name| name == "output"));
}

#[cfg(windows)]
#[test]
fn case_sensitive_windows_names_are_refused_instead_of_silently_merged() {
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_ADD_FILE, FILE_ADD_SUBDIRECTORY, FILE_CASE_SENSITIVE_INFO, FILE_DELETE_CHILD,
        FILE_FLAG_BACKUP_SEMANTICS, FILE_READ_ATTRIBUTES, FILE_WRITE_ATTRIBUTES,
        FileCaseSensitiveInfo, SetFileInformationByHandle,
    };

    let root = workspace();
    let directory = root.path().join("sensitive");
    fs::create_dir(&directory).unwrap();
    let account = std::process::Command::new("whoami.exe").output().unwrap();
    assert!(account.status.success());
    let account = String::from_utf8(account.stdout).unwrap();
    // The native flag requires CreateFiles/CreateDirectories/DeleteChild in
    // addition to WriteAttributes. Grant only this test's owned empty directory,
    // never an ancestor, user setting, service or SSH configuration.
    let grant = std::process::Command::new("icacls.exe")
        .arg(&directory)
        .arg("/grant")
        .arg(format!("{}:(OI)(CI)F", account.trim()))
        .output()
        .unwrap();
    assert!(
        grant.status.success(),
        "{}",
        String::from_utf8_lossy(&grant.stderr)
    );
    let handle = fs::OpenOptions::new()
        .access_mode(
            FILE_READ_ATTRIBUTES
                | FILE_WRITE_ATTRIBUTES
                | FILE_ADD_FILE
                | FILE_ADD_SUBDIRECTORY
                | FILE_DELETE_CHILD,
        )
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
        .open(&directory)
        .unwrap();
    let info = FILE_CASE_SENSITIVE_INFO { Flags: 1 };
    // SAFETY: Owned fixture directory, live handle, typed input of the documented size.
    assert_ne!(
        unsafe {
            SetFileInformationByHandle(
                handle.as_raw_handle(),
                FileCaseSensitiveInfo,
                (&info as *const FILE_CASE_SENSITIVE_INFO).cast(),
                std::mem::size_of::<FILE_CASE_SENSITIVE_INFO>() as u32,
            )
        },
        0,
        "{}",
        std::io::Error::last_os_error()
    );
    drop(handle);
    fs::write(directory.join("A"), "preserve uppercase").unwrap();
    let error = run(root.path(), "\"lowercase\" -> sensitive/a\n").unwrap_err();
    assert!(
        error
            .to_string()
            .contains("case-sensitive resource adapter")
    );
    assert_eq!(text(root.path(), "sensitive/A"), "preserve uppercase");
    assert!(!directory.join("a").exists());
    assert!(!root.path().join("scratch").exists());
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(24))]
    #[test]
    fn ordered_file_execution_matches_an_independent_value_model(operations in prop::collection::vec((any::<bool>(), 0usize..3, 0usize..2), 1..8)) {
        let root = workspace();
        let mut values = ["initial-a".to_owned(), "initial-b".to_owned(), "initial-c".to_owned()];
        let names = ["a", "b", "c"];
        for (name, value) in names.iter().zip(&values) { fs::write(root.path().join(name), value).unwrap(); }
        let mut source = String::new();
        for (literal, destination, offset) in operations {
            if literal {
                let value = ["literal", "可莉🌟"][offset];
                source.push_str(&format!("\"{value}\" -> {}\n", names[destination]));
                values[destination] = value.to_owned();
            } else {
                let from = (destination + 1 + offset) % 3;
                source.push_str(&format!("{} -> {}\n", names[from], names[destination]));
                values[destination] = values[from].clone();
            }
        }
        let report = run(root.path(), &source).unwrap();
        prop_assert!(report.cleanup.is_empty());
        for (name, value) in names.iter().zip(&values) { prop_assert_eq!(text(root.path(), name), value.as_str()); }
    }
}

#[cfg(windows)]
#[test]
#[ignore = "requires an installed WSL distribution; uses only an owned project fixture, not user configuration"]
fn real_wsl_macro_source_replaces_a_native_existing_directory() {
    use std::process::Command;

    let root = workspace();
    let source = root.path().join("source ' 可莉");
    fs::create_dir(&source).unwrap();
    fs::write(source.join("new.txt"), "from real WSL").unwrap();
    fs::create_dir(root.path().join("target")).unwrap();
    fs::write(root.path().join("target/old"), "remove old").unwrap();
    let output = Command::new("wsl.exe")
        .env("WSL_UTF8", "1")
        .args(["--exec", "wslpath", "-a", "-u"])
        .arg(&source)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let linux = String::from_utf8(output.stdout).unwrap();
    let source = format!(
        "macro copy(source, target):\n    source -> target/.\ncopy($wsl::\"{}\", $pwd::target)\n",
        linux.trim()
    );
    let report = run(root.path(), &source).unwrap();
    assert_eq!(text(root.path(), "target/new.txt"), "from real WSL");
    assert!(!root.path().join("target/old").exists());
    assert!(report.cleanup.is_empty(), "{:?}", report.cleanup);
}
