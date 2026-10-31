//! Release-only logical effect-graph scaling with complete public pair proofs.
use std::alloc::{GlobalAlloc, Layout, System};
use std::hint::black_box;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::{Duration, Instant};

use msync::endpoint::{Backend, Location};
use msync::hir::effects::OperationFacts;
use msync::hir::graph::{self, PlanningContext};
use msync::hir::{Input, Instruction, InstructionId, InstructionKind, Output, Program, Region};
use msync::language::DestinationMode;
use msync::plan::FileClass;
use msync::syntax::Span;

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

fn fixture(case: &str, count: usize) -> (Program, PlanningContext) {
    let location = |path| Location {
        backend: Backend::Local,
        path,
    };
    let mut context = PlanningContext::default();
    let mut body = Region::default();
    for index in 0..count {
        let source = if case == "shared-source" {
            "s/shared".to_owned()
        } else {
            format!("s/{index}")
        };
        let destination = if case == "dense" {
            "d/shared".to_owned()
        } else {
            format!("d/{index}")
        };
        let id = InstructionId(index);
        body.instructions.push(Instruction {
            id,
            kind: InstructionKind::Transfer {
                source: Input::File {
                    location: location(source),
                    pattern: None,
                },
                destination: Output::File {
                    location: location(destination),
                    mode: DestinationMode::Exact,
                },
            },
            span: Span::default(),
            expansion: Vec::new(),
        });
        context.operation_facts.insert(
            id,
            OperationFacts {
                source: FileClass::Regular,
                destination: FileClass::Regular,
            },
        );
    }
    (Program { body }, context)
}

fn millis(duration: Duration) -> f64 {
    duration.as_secs_f64() * 1000.0
}
fn measure(case: &str, count: usize, samples: usize) {
    let (program, context) = fixture(case, count);
    black_box(graph::build_with_context(&program, &context));
    let mut times = Vec::with_capacity(samples);
    for _ in 0..samples {
        let start = Instant::now();
        black_box(graph::build_with_context(
            black_box(&program),
            black_box(&context),
        ));
        times.push(start.elapsed());
    }
    times.sort_unstable();
    LIVE.store(0, Ordering::Relaxed);
    PEAK.store(0, Ordering::Relaxed);
    ALLOCATIONS.store(0, Ordering::Relaxed);
    TRACK.store(true, Ordering::SeqCst);
    let plan = graph::build_with_context(&program, &context);
    let retained = LIVE.load(Ordering::Relaxed);
    let peak = PEAK.load(Ordering::Relaxed);
    let allocations = ALLOCATIONS.load(Ordering::Relaxed);
    TRACK.store(false, Ordering::SeqCst);
    assert!(plan.issues.is_empty());
    let pairs = count.saturating_mul(count.saturating_sub(1)) / 2;
    if case == "dense" {
        assert_eq!(plan.dependencies.len(), pairs);
    } else {
        assert_eq!(plan.commutations.len(), pairs);
        assert!(plan.dependencies.is_empty());
    }
    println!(
        "{{\"case\":\"{case}\",\"n\":{count},\"samples\":{samples},\"median_ms\":{:.6},\"p90_ms\":{:.6},\"allocations\":{allocations},\"peak_bytes\":{peak},\"retained_bytes\":{retained},\"dependencies\":{},\"commutations\":{}}}",
        millis(times[samples / 2]),
        millis(times[(samples * 9 / 10).min(samples - 1)]),
        plan.dependencies.len(),
        plan.commutations.len()
    );
}

fn main() {
    if cfg!(debug_assertions) {
        eprintln!("graph_v6: use a release build for timing");
        return;
    }
    let sizes = std::env::var("MSYNC_V6_GRAPH_SIZES")
        .unwrap_or_else(|_| "10,100,500,1000".into())
        .split(',')
        .map(|count| count.parse::<usize>().unwrap())
        .collect::<Vec<_>>();
    let samples = std::env::var("MSYNC_V6_GRAPH_SAMPLES")
        .unwrap_or_else(|_| "7".into())
        .parse::<usize>()
        .unwrap();
    assert!(samples > 0);
    let filter = std::env::var("MSYNC_V6_GRAPH_CASE").ok();
    for case in ["independent", "shared-source", "dense"] {
        if filter.as_ref().is_some_and(|filter| filter != case) {
            continue;
        }
        for &count in &sizes {
            assert!(count > 0);
            measure(case, count, samples);
        }
    }
}
