use std::ffi::OsString;
use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

struct Case {
    name: &'static str,
    args: Vec<OsString>,
    input: Option<&'static str>,
}

struct Workspace(PathBuf);

impl std::ops::Deref for Workspace {
    type Target = Path;

    fn deref(&self) -> &Path {
        &self.0
    }
}

impl Drop for Workspace {
    fn drop(&mut self) {
        if let Err(error) = fs::remove_dir_all(&self.0) {
            eprintln!(
                "cannot remove startup fixture {}: {error}",
                self.0.display()
            );
        }
    }
}

fn run(executable: &Path, case: &Case, workspace: &Path) -> Duration {
    let start = Instant::now();
    let mut child = Command::new(executable)
        .args(&case.args)
        .current_dir(workspace)
        .env("NO_COLOR", "1")
        .stdin(if case.input.is_some() {
            Stdio::piped()
        } else {
            Stdio::null()
        })
        .stdout(Stdio::null())
        .stderr(Stdio::piped())
        .spawn()
        .expect("start measured executable");
    if let Some(input) = case.input {
        child
            .stdin
            .take()
            .unwrap()
            .write_all(input.as_bytes())
            .unwrap();
    }
    let output = child.wait_with_output().unwrap();
    assert!(
        output.status.success(),
        "{}: {}",
        case.name,
        String::from_utf8_lossy(&output.stderr)
    );
    start.elapsed()
}

fn percentile(samples: &mut [Duration], numerator: usize) -> f64 {
    samples.sort_unstable();
    samples[(samples.len() - 1) * numerator / 100].as_secs_f64() * 1000.0
}

fn fixture(root: &Path) -> Workspace {
    let id = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    let directory = root
        .join(".temp")
        .join(format!("startup-{}-{id}", std::process::id()));
    fs::create_dir_all(&directory).unwrap();
    let directory = Workspace(directory);
    fs::write(directory.join("source.bin"), b"startup timing fixture").unwrap();
    fs::write(directory.join("small.msync"), "source.bin -> output.bin\n").unwrap();
    fs::write(
        directory.join("hundred.msync"),
        (0..100)
            .map(|index| format!("source.bin -> output-{index}.bin\n"))
            .collect::<String>(),
    )
    .unwrap();
    directory
}

#[cfg(windows)]
fn remote_case(workspace: &Path) -> Case {
    let output = Command::new("wsl")
        .args(["--exec", "wslpath", "-a", "-u"])
        .arg(workspace)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let directory = String::from_utf8(output.stdout).unwrap();
    let mut script = "remote => $wsl\n".to_owned();
    for index in 0..16 {
        fs::write(
            workspace.join(format!("remote-{index}.bin")),
            b"metadata benchmark fixture",
        )
        .unwrap();
        script.push_str(&format!(
            "{:?} -> remote-output-{index}.bin\n",
            format!("remote::{}/remote-{index}.bin", directory.trim())
        ));
    }
    fs::write(workspace.join("remote.msync"), script).unwrap();
    Case {
        name: "plan/wsl-16-unique",
        args: vec!["plan".into(), "remote.msync".into()],
        input: None,
    }
}

fn main() {
    if cfg!(debug_assertions) {
        println!(
            "startup timings skipped in debug builds; use the release benchmark with two executable paths"
        );
        return;
    }
    let mut args = std::env::args_os().skip(1).filter(|arg| arg != "--bench");
    let Some(baseline) = args.next() else {
        println!("usage: startup BASELINE_EXECUTABLE CURRENT_EXECUTABLE [SAMPLES=30]");
        return;
    };
    if baseline.to_string_lossy().starts_with('-') {
        return;
    }
    let Some(current) = args.next() else {
        panic!("provide the current executable");
    };
    let samples: usize = args
        .next()
        .map(|value| value.to_string_lossy().parse().unwrap())
        .unwrap_or(30);
    let remote = args.any(|argument| argument == "--wsl");
    assert!(samples >= 5);
    let baseline = std::path::absolute(baseline).unwrap();
    let current = std::path::absolute(current).unwrap();
    let root = std::env::current_dir().unwrap();
    let workspace = fixture(&root);
    let mut cases = vec![
        Case {
            name: "version",
            args: vec!["--version".into()],
            input: None,
        },
        Case {
            name: "help",
            args: vec!["--help".into()],
            input: None,
        },
        Case {
            name: "check/1",
            args: vec!["check".into(), "small.msync".into()],
            input: None,
        },
        Case {
            name: "plan/1",
            args: vec!["plan".into(), "small.msync".into()],
            input: None,
        },
        Case {
            name: "plan/100-shared-source",
            args: vec!["plan".into(), "hundred.msync".into()],
            input: None,
        },
        Case {
            name: "repl/help-exit",
            args: vec!["repl".into()],
            input: Some("/help\n/quit\n"),
        },
    ];
    #[cfg(windows)]
    if remote {
        cases.push(remote_case(&workspace));
    }
    #[cfg(not(windows))]
    let _ = (&mut cases, remote);
    println!(
        "# paired alternating AB/BA, 5 warmups per executable, {samples} samples; wall-clock ms"
    );
    println!("case,baseline_p50_ms,current_p50_ms,baseline_p95_ms,current_p95_ms");
    for case in &cases {
        let slow = case.name == "plan/wsl-16-unique";
        let iterations = if slow { samples.min(5) } else { samples };
        for _ in 0..if slow { 1 } else { 5 } {
            run(&baseline, case, &workspace);
            run(&current, case, &workspace);
        }
        let mut before = Vec::with_capacity(iterations);
        let mut after = Vec::with_capacity(iterations);
        for sample in 0..iterations {
            if sample % 2 == 0 {
                before.push(run(&baseline, case, &workspace));
                after.push(run(&current, case, &workspace));
            } else {
                after.push(run(&current, case, &workspace));
                before.push(run(&baseline, case, &workspace));
            }
        }
        println!(
            "{},{:.3},{:.3},{:.3},{:.3}",
            case.name,
            percentile(&mut before, 50),
            percentile(&mut after, 50),
            percentile(&mut before, 95),
            percentile(&mut after, 95)
        );
    }
}
