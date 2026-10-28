//! Actual scoped-frontier IO overlap, commit/rewind and ordinary REPL completion.

use std::collections::HashSet;
use std::fs;
use std::io::Write;
use std::path::Path;
use std::process::{Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant, UNIX_EPOCH};

fn workspace() -> tempfile::TempDir {
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    tempfile::Builder::new()
        .prefix("named-frontier-v4-")
        .tempdir_in(temporary)
        .unwrap()
}

fn run(work: &Path, source: &str, jobs: usize, repl: bool) -> Output {
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"));
    child
        .current_dir(work)
        .args(if repl { vec!["repl"] } else { vec!["run", "-"] })
        .args([
            "--color",
            "never",
            "--trace",
            "trace.jsonl",
            "--buffer-size",
            "4096",
            "--memory-limit",
            "8192",
            "--jobs",
            &jobs.to_string(),
        ])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped());
    let mut child = child.spawn().unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(source.as_bytes())
        .unwrap();
    let deadline = Instant::now() + Duration::from_secs(20);
    while child.try_wait().unwrap().is_none() {
        if Instant::now() >= deadline {
            let _ = child.kill();
            let output = child.wait_with_output().unwrap();
            panic!(
                "scoped frontier did not finish: {}",
                String::from_utf8_lossy(&output.stderr)
            );
        }
        thread::sleep(Duration::from_millis(5));
    }
    child.wait_with_output().unwrap()
}

fn success(output: &Output) {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

fn number(record: &str, name: &str) -> usize {
    record
        .split_once(&format!("\"{name}\":"))
        .unwrap()
        .1
        .chars()
        .take_while(char::is_ascii_digit)
        .collect::<String>()
        .parse()
        .unwrap()
}

fn peak_writes(trace: &str, threshold: usize) -> usize {
    let mut active = HashSet::new();
    let mut peak = 0;
    let mut started = 0;
    for record in trace
        .lines()
        .filter(|record| record.contains("\"name\":\"literal.capture\""))
    {
        if number(record, "bytes.total") < threshold {
            continue;
        }
        let task = record
            .split_once("\"span_id\":\"")
            .unwrap()
            .1
            .split('"')
            .next()
            .unwrap();
        if record.contains("\"phase\":\"write-start\"") {
            assert!(active.insert(task), "duplicate capture start: {record}");
            started += 1;
            peak = peak.max(active.len());
        } else if record.contains("\"phase\":\"write-complete\"") {
            assert!(
                active.remove(task),
                "capture completed before its real file-open event: {record}"
            );
        }
    }
    assert_eq!(
        started, 2,
        "both actual large literal writes must be observed"
    );
    assert!(active.is_empty(), "unfinished actual capture intervals");
    peak
}

fn scopes_commute(work: &Path, source: &str) -> bool {
    use msync::compiler::{CompileContext, structured::Session};
    let program = Session::new()
        .compile(
            &msync::language::parse(source).unwrap(),
            &CompileContext {
                pwd: work.to_owned(),
                source_file: None,
            },
        )
        .unwrap();
    let prepared = msync::preparation::prepare(&program, true).unwrap();
    let left = program.body.instructions[0].id;
    let right = program.body.instructions[1].id;
    prepared
        .graph
        .commutations
        .iter()
        .any(|proof| proof.left == left && proof.right == right)
}

#[test]
fn disjoint_named_literal_jobs_overlap_and_keep_checkpoint_and_origin_semantics() {
    let left = "A-可莉 ".repeat(512 * 1024);
    let right = "B-世界 ".repeat(512 * 1024);
    for (isolation, jobs, shared_parent) in [
        ("serializable", 2, false),
        ("serializable", 1, false),
        ("read committed", 2, true),
    ] {
        let root = workspace();
        let work = root.path();
        fs::create_dir(work.join("a")).unwrap();
        fs::create_dir(work.join("b")).unwrap();
        fs::create_dir(work.join("witness")).unwrap();
        let a = "a/result-a";
        let b = if shared_parent {
            "a/result-b"
        } else {
            "b/result-b"
        };
        fs::write(work.join(a), b"old A").unwrap();
        fs::write(work.join(b), b"old B").unwrap();
        fs::write(work.join("a/checkpoint-temp"), b"keep checkpoint").unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(work.join(a), fs::Permissions::from_mode(0o640)).unwrap();
        }
        let old_time = UNIX_EPOCH + Duration::from_secs(978307200);
        fs::OpenOptions::new()
            .write(true)
            .open(work.join(a))
            .unwrap()
            .set_times(fs::FileTimes::new().set_modified(old_time))
            .unwrap();
        let observed_time = fs::metadata(work.join(a)).unwrap().modified().unwrap();
        let permissions = fs::metadata(work.join(a)).unwrap().permissions();
        let source = format!(
            "transaction A({isolation}):\n    {left:?} -> {a}\n    commit\n    \"discard pending\" -> a/checkpoint-temp\n    rollback\ntransaction B({isolation}):\n    {right:?} -> {b}\n{a} -> witness/a\n{b} -> witness/b\nrollback A\nrollback B\n"
        );
        let commute = scopes_commute(work, &source);
        assert!(
            commute,
            "the overlap profile needs a checked parent-region commutation proof"
        );
        success(&run(work, &source, jobs, false));
        assert_eq!(fs::read(work.join("witness/a")).unwrap(), left.as_bytes());
        assert_eq!(fs::read(work.join("witness/b")).unwrap(), right.as_bytes());
        assert_eq!(
            fs::metadata(work.join("witness/a")).unwrap().permissions(),
            permissions
        );
        assert_eq!(fs::read(work.join(a)).unwrap(), b"old A");
        assert_eq!(fs::read(work.join(b)).unwrap(), b"old B");
        assert_eq!(
            fs::metadata(work.join(a)).unwrap().modified().unwrap(),
            observed_time
        );
        assert_eq!(
            fs::read(work.join("a/checkpoint-temp")).unwrap(),
            b"keep checkpoint"
        );
        let trace = fs::read_to_string(work.join("trace.jsonl")).unwrap();
        assert_eq!(
            peak_writes(&trace, left.len()),
            jobs.min(2),
            "isolation={isolation} shared_parent={shared_parent} proved_commutation={commute} events={}",
            trace
                .lines()
                .filter(|line| line.contains("literal.capture")
                    || line.contains("\"name\":\"transaction.commit\""))
                .collect::<Vec<_>>()
                .join("\n")
        );
        assert!(!trace.contains("A-可莉 A-可莉"));
    }
}

#[test]
fn named_sibling_creations_keep_the_existing_strict_parent_gate() {
    let root = workspace();
    fs::create_dir(root.path().join("shared")).unwrap();
    let left = "A".repeat(1024 * 1024);
    let right = "B".repeat(1024 * 1024);
    let source = format!(
        "transaction A:\n    {left:?} -> shared/a\ntransaction B:\n    {right:?} -> shared/b\n"
    );
    assert!(
        !scopes_commute(root.path(), &source),
        "named sibling insertion must retain the strict parent hazard"
    );
    success(&run(root.path(), &source, 2, false));
    assert_eq!(
        fs::read(root.path().join("shared/a")).unwrap(),
        left.as_bytes()
    );
    assert_eq!(
        fs::read(root.path().join("shared/b")).unwrap(),
        right.as_bytes()
    );
    let trace = fs::read_to_string(root.path().join("trace.jsonl")).unwrap();
    assert_eq!(
        peak_writes(&trace, left.len()),
        1,
        "named insertions have no namespace-merge exemption"
    );
}

#[test]
fn ordinary_repl_literal_is_committed_before_the_next_statement_reads_it() {
    let root = workspace();
    let payload = "ordinary-UTF8-可莉 ".repeat(64 * 1024);
    let source = format!("{payload:?} -> first\nfirst -> second\n/quit\n");
    success(&run(root.path(), &source, 2, true));
    assert_eq!(
        fs::read(root.path().join("first")).unwrap(),
        payload.as_bytes()
    );
    assert_eq!(
        fs::read(root.path().join("second")).unwrap(),
        payload.as_bytes()
    );
}

#[test]
fn finishing_a_fast_scope_does_not_unbind_another_scopes_pending_producer() {
    let root = workspace();
    fs::create_dir(root.path().join("a")).unwrap();
    fs::create_dir(root.path().join("b")).unwrap();
    fs::write(root.path().join("a/result"), b"old A").unwrap();
    fs::write(root.path().join("b/result"), b"old B").unwrap();
    fs::write(root.path().join("b/copied"), b"old copy").unwrap();
    let payload = "large B bytes ".repeat(256 * 1024);
    let source = format!(
        "transaction A:\n    \"fast A\" -> a/result\ntransaction B:\n    {payload:?} -> b/result\n    b/result -> b/copied\n"
    );
    success(&run(root.path(), &source, 2, false));
    assert_eq!(fs::read(root.path().join("a/result")).unwrap(), b"fast A");
    assert_eq!(
        fs::read(root.path().join("b/result")).unwrap(),
        payload.as_bytes()
    );
    assert_eq!(
        fs::read(root.path().join("b/copied")).unwrap(),
        payload.as_bytes()
    );
}

#[test]
fn future_stdin_cannot_be_hoisted_across_a_checkpoint_to_complete_its_process() {
    use msync::compiler::{CompileContext, structured::Session};
    let root = workspace();
    let source = "transaction A:\n    child => $path::cat()\n    commit\n    \"future input\" -> child::stdin\n";
    let program = Session::new()
        .compile(
            &msync::language::parse(source).unwrap(),
            &CompileContext {
                pwd: root.path().to_owned(),
                source_file: None,
            },
        )
        .unwrap();
    let graph = msync::hir::graph::build(&program);
    assert!(
        graph
            .issues
            .iter()
            .any(|error| error.message.contains("crosses a checkpoint")),
        "{graph:?}"
    );
    assert!(!root.path().join(".temp").exists());
}
