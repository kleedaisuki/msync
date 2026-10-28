//! Release-only v0.4 frontend/preparation/verification scaling probe.

use std::alloc::{GlobalAlloc, Layout, System};
use std::fs;
use std::hint::black_box;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::{Duration, Instant};

use msync::compiler::{CompileContext, structured::Session};
use msync::{hir, language, preparation};

struct Allocator;

static TRACK: AtomicBool = AtomicBool::new(false);
static LIVE: AtomicUsize = AtomicUsize::new(0);
static PEAK: AtomicUsize = AtomicUsize::new(0);
static ALLOCATIONS: AtomicUsize = AtomicUsize::new(0);

#[global_allocator]
static ALLOCATOR: Allocator = Allocator;

unsafe impl GlobalAlloc for Allocator {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        let pointer = unsafe { System.alloc(layout) };
        if !pointer.is_null() && TRACK.load(Ordering::Relaxed) {
            let live = LIVE.fetch_add(layout.size(), Ordering::Relaxed) + layout.size();
            PEAK.fetch_max(live, Ordering::Relaxed);
            ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
        }
        pointer
    }

    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        if TRACK.load(Ordering::Relaxed) {
            LIVE.fetch_sub(layout.size(), Ordering::Relaxed);
        }
        unsafe { System.dealloc(pointer, layout) };
    }

    unsafe fn realloc(&self, pointer: *mut u8, old: Layout, size: usize) -> *mut u8 {
        let new = unsafe { System.realloc(pointer, old, size) };
        if !new.is_null() && TRACK.load(Ordering::Relaxed) {
            let live = if size >= old.size() {
                LIVE.fetch_add(size - old.size(), Ordering::Relaxed) + size - old.size()
            } else {
                LIVE.fetch_sub(old.size() - size, Ordering::Relaxed) - (old.size() - size)
            };
            PEAK.fetch_max(live, Ordering::Relaxed);
            ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
        }
        new
    }
}

struct Fixture {
    root: PathBuf,
}

impl Fixture {
    fn new(base: &Path) -> Self {
        let stamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        let root = base.join(format!("planner-v4-{:010}-{stamp:032}", std::process::id()));
        fs::create_dir_all(base).unwrap();
        fs::create_dir(&root).unwrap();
        Self { root }
    }

    fn script(&self, case: &str, count: usize) -> String {
        let directory = self.root.join(format!("{case}-{count}"));
        fs::create_dir(&directory).unwrap();
        for index in 0..=count {
            fs::write(
                directory.join(format!("s{index}")),
                format!("payload {index}\n"),
            )
            .unwrap();
            if case == "independent" {
                fs::write(directory.join(format!("d{index}")), b"before").unwrap();
            }
        }
        if case == "overwritten" {
            fs::write(directory.join("out"), b"before").unwrap();
        }
        let mut script = String::new();
        for index in 0..count {
            let (source, target) = match case {
                "independent" => (format!("s{index}"), format!("d{index}")),
                "forward-chain" => (format!("s{index}"), format!("s{}", index + 1)),
                "overwritten" | "missing-overwritten" => (format!("s{index}"), "out".into()),
                _ => panic!("unknown case {case}"),
            };
            script.push_str(&format!(
                "{case}-{count}/{source} -> {case}-{count}/{target}\n"
            ));
        }
        script
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        fs::remove_dir_all(&self.root).unwrap();
    }
}

fn compile(text: &str, root: &Path) -> hir::Program {
    Session::new()
        .compile(
            &language::parse(text).unwrap(),
            &CompileContext {
                pwd: root.to_owned(),
                source_file: None,
            },
        )
        .unwrap()
}

fn elapsed(mut work: impl FnMut(), samples: usize) -> (f64, f64) {
    work();
    let mut values = Vec::with_capacity(samples);
    for _ in 0..samples {
        let start = Instant::now();
        work();
        values.push(start.elapsed());
    }
    values.sort();
    let milliseconds = |duration: Duration| duration.as_secs_f64() * 1000.0;
    (
        milliseconds(values[values.len() / 2]),
        milliseconds(values[(values.len() * 9 / 10).min(values.len() - 1)]),
    )
}

fn sizes() -> Vec<usize> {
    std::env::var("MSYNC_V4_PLANNER_SIZES")
        .unwrap_or_else(|_| "10,50,100,200".into())
        .split(',')
        .map(|part| part.parse::<usize>().unwrap())
        .filter(|count| *count > 0)
        .collect()
}

fn main() {
    if cfg!(debug_assertions) {
        eprintln!("planner_v4: use a release build for timing");
        return;
    }
    let base = PathBuf::from(std::env::var_os("MSYNC_PERF_ROOT").expect("set MSYNC_PERF_ROOT"));
    assert!(
        base.is_absolute(),
        "performance root must be explicit and absolute"
    );
    let fixture = Fixture::new(&base);
    let context_root = match std::env::var_os("MSYNC_PERF_CONTEXT_ROOT") {
        Some(alias) => {
            let alias = PathBuf::from(alias).join(fixture.root.file_name().unwrap());
            assert!(alias.is_absolute());
            #[cfg(unix)]
            {
                use std::os::unix::fs::MetadataExt;
                let original = fs::metadata(&fixture.root).unwrap();
                let other = fs::metadata(&alias).unwrap();
                assert_eq!((original.dev(), original.ino()), (other.dev(), other.ino()));
            }
            #[cfg(not(unix))]
            assert_eq!(
                fs::canonicalize(&fixture.root).unwrap(),
                fs::canonicalize(&alias).unwrap()
            );
            alias
        }
        None => fixture.root.clone(),
    };
    let samples = std::env::var("MSYNC_V4_PLANNER_SAMPLES")
        .unwrap_or_else(|_| "7".into())
        .parse::<usize>()
        .unwrap();
    assert!(samples > 0);
    let filter = std::env::var("MSYNC_V4_PLANNER_CASE").ok();
    for case in [
        "independent",
        "forward-chain",
        "overwritten",
        "missing-overwritten",
    ] {
        if filter.as_ref().is_some_and(|filter| filter != case) {
            continue;
        }
        for count in sizes() {
            let source = fixture.script(case, count);
            let program = compile(&source, &context_root);
            let (compile_ms, compile_p90_ms) = elapsed(
                || {
                    black_box(compile(&source, &context_root));
                },
                samples,
            );
            let (prepare_ms, prepare_p90_ms) = elapsed(
                || {
                    black_box(preparation::prepare(&program, true).unwrap());
                },
                samples,
            );
            let prepared = preparation::prepare(&program, true).unwrap();
            let (verify_ms, verify_p90_ms) = elapsed(
                || {
                    prepared.verify().unwrap();
                },
                samples,
            );
            LIVE.store(0, Ordering::Relaxed);
            PEAK.store(0, Ordering::Relaxed);
            ALLOCATIONS.store(0, Ordering::Relaxed);
            TRACK.store(true, Ordering::SeqCst);
            let profiled = preparation::prepare(&program, true).unwrap();
            let retained = LIVE.load(Ordering::Relaxed);
            let peak = PEAK.load(Ordering::Relaxed);
            let allocations = ALLOCATIONS.load(Ordering::Relaxed);
            drop(profiled);
            TRACK.store(false, Ordering::SeqCst);
            println!(
                "{{\"case\":\"{case}\",\"n\":{count},\"samples\":{samples},\"compile_ms\":{compile_ms:.6},\"compile_p90_ms\":{compile_p90_ms:.6},\"prepare_ms\":{prepare_ms:.6},\"prepare_p90_ms\":{prepare_p90_ms:.6},\"verify_ms\":{verify_ms:.6},\"verify_p90_ms\":{verify_p90_ms:.6},\"allocations\":{allocations},\"peak_bytes\":{peak},\"retained_bytes\":{retained},\"nodes\":{},\"dependencies\":{},\"commutations\":{},\"rewrites\":{}}}",
                prepared.program.body.instructions.len(),
                prepared.graph.dependencies.len(),
                prepared.graph.commutations.len(),
                prepared.rewrites.len()
            );
        }
    }
}
