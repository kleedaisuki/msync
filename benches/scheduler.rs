//! Paired real-file CLI journeys, including process startup and live planning.
use std::fs;
use std::path::Path;
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

static GENERATION: AtomicU64 = AtomicU64::new(0);

fn run(executable: &Path, root: &Path, count: usize) -> Duration {
    let generation = GENERATION.fetch_add(1, Ordering::Relaxed);
    let payload = format!("scheduler fixture {generation:016x}\n");
    fs::write(root.join("source"), payload.as_bytes()).unwrap();
    let start = Instant::now();
    let output = Command::new(executable)
        .args(["run", "copy.msync", "--jobs", "4", "--color", "never"])
        .current_dir(root)
        .env("HOME", root.join("home"))
        .env("XDG_CONFIG_HOME", root.join("config"))
        .env("APPDATA", root.join("config"))
        .env("NO_COLOR", "1")
        .env_remove("MSYNC_CONFIG")
        .env_remove("MSYNC_STATE_DIR")
        .env_remove("MSYNC_TRACE")
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .output()
        .unwrap();
    let elapsed = start.elapsed();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let summary = format!(
        "done: {count} assignment(s), {count} file(s), {} bytes;",
        count * payload.len()
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    assert!(
        stdout
            .lines()
            .last()
            .is_some_and(|line| line.starts_with(&summary)),
        "unexpected execution counts: {stdout}"
    );
    for id in 0..count {
        assert_eq!(
            fs::read(root.join(format!("out/{id}"))).unwrap(),
            payload.as_bytes()
        );
    }
    elapsed
}

fn median(values: &mut [Duration]) -> f64 {
    values.sort_unstable();
    values[values.len() / 2].as_secs_f64() * 1000.0
}

fn main() {
    if cfg!(debug_assertions) {
        println!(
            "scheduler timings skipped in debug builds; use the release benchmark with two executable paths"
        );
        return;
    }
    let args: Vec<_> = std::env::args_os()
        .skip(1)
        .filter(|argument| argument != "--bench")
        .collect();
    if args.len() < 2 {
        eprintln!(
            "usage: scheduler BASELINE CURRENT [SAMPLES]; MSYNC_SCHEDULER_SIZES=1,100,1000,10000"
        );
        return;
    }
    let before = fs::canonicalize(&args[0]).unwrap();
    let after = fs::canonicalize(&args[1]).unwrap();
    let samples = args
        .get(2)
        .map(|value| value.to_str().unwrap().parse::<usize>().unwrap())
        .unwrap_or(7);
    assert!(samples > 0);
    let sizes =
        std::env::var("MSYNC_SCHEDULER_SIZES").unwrap_or_else(|_| "1,100,1000,10000".into());
    let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&temporary).unwrap();
    println!(
        "# jobs=4; 2 warmups per executable; {samples} alternating AB/BA samples; warm overwrite; fresh generation and summary counts verified outside timing"
    );
    println!(
        "copies,baseline_median_ms,current_median_ms,baseline_min_ms,baseline_max_ms,current_min_ms,current_max_ms"
    );
    for count in sizes
        .split(',')
        .map(|value| value.parse::<usize>().unwrap())
    {
        let root = tempfile::Builder::new()
            .prefix("scheduler-bench-")
            .tempdir_in(&temporary)
            .unwrap();
        fs::create_dir(root.path().join("out")).unwrap();
        fs::write(
            root.path().join("copy.msync"),
            (0..count)
                .map(|id| format!("source -> out/{id}\n"))
                .collect::<String>(),
        )
        .unwrap();
        for _ in 0..2 {
            run(&before, root.path(), count);
            run(&after, root.path(), count);
        }
        let mut baseline = Vec::new();
        let mut current = Vec::new();
        for sample in 0..samples {
            if sample % 2 == 0 {
                baseline.push(run(&before, root.path(), count));
                current.push(run(&after, root.path(), count));
            } else {
                current.push(run(&after, root.path(), count));
                baseline.push(run(&before, root.path(), count));
            }
        }
        println!(
            "{count},{:.3},{:.3},{:.3},{:.3},{:.3},{:.3}",
            median(&mut baseline),
            median(&mut current),
            baseline.first().unwrap().as_secs_f64() * 1000.0,
            baseline.last().unwrap().as_secs_f64() * 1000.0,
            current.first().unwrap().as_secs_f64() * 1000.0,
            current.last().unwrap().as_secs_f64() * 1000.0
        );
    }
}
