use std::alloc::{GlobalAlloc, Layout, System};
use std::hint::black_box;
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::{Duration, Instant};

use msync::compiler::{Backend, Compiler, CopyOp, Location, Program};
use msync::plan::{FileClass, OperationKinds, Plan};
use msync::syntax::{self, Span};

struct TrackedAllocator;

static TRACKING: AtomicBool = AtomicBool::new(false);
static LIVE: AtomicUsize = AtomicUsize::new(0);
static PEAK: AtomicUsize = AtomicUsize::new(0);
static ALLOCATIONS: AtomicUsize = AtomicUsize::new(0);

#[global_allocator]
static ALLOCATOR: TrackedAllocator = TrackedAllocator;

unsafe impl GlobalAlloc for TrackedAllocator {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        let pointer = unsafe { System.alloc(layout) };
        if !pointer.is_null() && TRACKING.load(Ordering::Relaxed) {
            let live = LIVE.fetch_add(layout.size(), Ordering::Relaxed) + layout.size();
            ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
            PEAK.fetch_max(live, Ordering::Relaxed);
        }
        pointer
    }

    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        if TRACKING.load(Ordering::Relaxed) {
            LIVE.fetch_sub(layout.size(), Ordering::Relaxed);
        }
        unsafe { System.dealloc(pointer, layout) };
    }
}

fn operation(backend: &Backend, source: String, destination: String) -> CopyOp {
    CopyOp {
        source: Location {
            backend: backend.clone(),
            path: source,
        },
        destination: Location {
            backend: backend.clone(),
            path: destination,
        },
        pattern: None,
        span: Span::default(),
    }
}

fn case(name: &str, count: usize, root: &Path) -> Program {
    let backend = Backend::Ssh {
        host: "planner.invalid".into(),
        user: None,
        port: None,
    };
    let source = root.join("sources").to_string_lossy().replace('\\', "/");
    let target = root.join("outputs").to_string_lossy().replace('\\', "/");
    if name == "alias-paths" {
        let text = format!(
            "sources := \"{source}\"\noutputs := \"{target}\"\n{}",
            (0..count)
                .map(|index| format!("sources/file{index} -> outputs/file{index}\n"))
                .collect::<String>()
        );
        return Compiler::new()
            .compile(&syntax::parse(&text).unwrap())
            .unwrap();
    }
    Program {
        operations: (0..count)
            .map(|index| match name {
                "local-independent" => operation(
                    &Backend::Local,
                    format!("{source}/file{index}"),
                    format!("{target}/file{index}"),
                ),
                "independent" => operation(
                    &backend,
                    format!("sources/file{index}"),
                    format!("outputs/file{index}"),
                ),
                "overwritten" => operation(
                    &backend,
                    format!("sources/file{index}"),
                    "outputs/value".into(),
                ),
                "forward-chain" => operation(
                    &backend,
                    format!("chain/file{index}"),
                    format!("chain/file{}", index + 1),
                ),
                "dense" => operation(&backend, "tree/input".into(), "tree".into()),
                _ => panic!("unknown planner case {name}"),
            })
            .collect(),
    }
}

fn measure(name: &str, program: &Program, kinds: &[OperationKinds], samples: usize) {
    black_box(Plan::build(black_box(program), black_box(kinds)));
    let mut timings = Vec::with_capacity(samples);
    for _ in 0..samples {
        let start = Instant::now();
        black_box(Plan::build(black_box(program), black_box(kinds)));
        timings.push(start.elapsed());
    }
    timings.sort_unstable();
    LIVE.store(0, Ordering::Relaxed);
    PEAK.store(0, Ordering::Relaxed);
    ALLOCATIONS.store(0, Ordering::Relaxed);
    TRACKING.store(true, Ordering::Relaxed);
    let (plan, stats) = Plan::build_profiled(program, kinds);
    TRACKING.store(false, Ordering::Relaxed);
    let heap_peak = PEAK.load(Ordering::Relaxed);
    let allocations = ALLOCATIONS.load(Ordering::Relaxed);
    let retained = LIVE.load(Ordering::Relaxed);
    println!(
        "{name} n={} median_ms={:.3} min_ms={:.3} max_ms={:.3} heap_peak_bytes={heap_peak} retained_bytes={retained} allocations={allocations} nodes={} edges={} removed={} forwarded={} rounds={} resource_cache_misses={} canonicalize_probes={} identity_handles={} dependency_candidates={}",
        program.operations.len(),
        millis(timings[timings.len() / 2]),
        millis(timings[0]),
        millis(timings[timings.len() - 1]),
        plan.nodes.len(),
        plan.edges.len(),
        plan.removed.len(),
        plan.forwarded.len(),
        plan.optimization_rounds,
        stats.resource_cache_misses,
        stats.canonicalize_probes,
        stats.identity_handles,
        stats.dependency_candidates
    );
}

fn millis(duration: Duration) -> f64 {
    duration.as_secs_f64() * 1000.0
}

fn main() {
    if cfg!(debug_assertions) {
        println!("planner timing skipped in debug builds; use cargo bench --bench planner");
        return;
    }
    let sizes: Vec<usize> = std::env::var("MSYNC_PLANNER_SIZES")
        .unwrap_or_else(|_| "100,1000,5000".into())
        .split(',')
        .map(|value| value.parse().unwrap())
        .collect();
    let selected = std::env::var("MSYNC_PLANNER_CASE").ok();
    let root = std::env::current_dir()
        .unwrap()
        .join(".temp/planner-bench-fixtures");
    std::fs::create_dir_all(root.join("sources")).unwrap();
    for index in 0..sizes.iter().copied().max().unwrap_or(0) {
        std::fs::write(
            root.join(format!("sources/file{index}")),
            b"planner benchmark\n",
        )
        .unwrap();
    }
    if selected.as_deref().is_none_or(|name| name == "seed") {
        let program = Compiler::new()
            .compile(&syntax::parse(include_str!("../docs/dsl/spread.msync")).unwrap())
            .unwrap();
        measure(
            "seed",
            &program,
            &vec![
                OperationKinds {
                    source: FileClass::Regular,
                    destination: FileClass::Missing
                };
                program.operations.len()
            ],
            7,
        );
    }
    for count in sizes {
        for name in [
            "independent",
            "local-independent",
            "alias-paths",
            "overwritten",
            "forward-chain",
            "dense",
        ] {
            if selected.as_deref().is_some_and(|selected| selected != name) {
                continue;
            }
            let program = case(name, count, &root);
            let kinds = vec![
                OperationKinds {
                    source: if name == "dense" {
                        FileClass::Unknown
                    } else {
                        FileClass::Regular
                    },
                    destination: if name == "dense" {
                        FileClass::Unknown
                    } else {
                        FileClass::Missing
                    }
                };
                count
            ];
            let samples = std::env::var("MSYNC_PLANNER_SAMPLES")
                .ok()
                .map(|value| value.parse().unwrap())
                .unwrap_or(if count >= 1000 { 3 } else { 7 });
            measure(name, &program, &kinds, samples);
        }
    }
}
