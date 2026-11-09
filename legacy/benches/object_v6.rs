//! Object-only allocation/latency probe; source compilation and fixture cloning are untimed.

use std::alloc::{GlobalAlloc, Layout, System};
use std::hint::black_box;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::Instant;

use msync::compiler::{CompileContext, structured::Session};
use msync::context::{Context, contextualize};
use msync::object::ObjectModule;
use msync::pipeline;

struct Allocator;
static TRACK: AtomicBool = AtomicBool::new(false);
static ALLOCATIONS: AtomicUsize = AtomicUsize::new(0);
static BYTES: AtomicUsize = AtomicUsize::new(0);

#[global_allocator]
static ALLOCATOR: Allocator = Allocator;

unsafe impl GlobalAlloc for Allocator {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        let pointer = unsafe { System.alloc(layout) };
        if !pointer.is_null() && TRACK.load(Ordering::Relaxed) {
            ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
            BYTES.fetch_add(layout.size(), Ordering::Relaxed);
        }
        pointer
    }

    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        unsafe { System.dealloc(pointer, layout) };
    }

    unsafe fn realloc(&self, pointer: *mut u8, old: Layout, size: usize) -> *mut u8 {
        let pointer = unsafe { System.realloc(pointer, old, size) };
        if !pointer.is_null() && TRACK.load(Ordering::Relaxed) {
            ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
            BYTES.fetch_add(size, Ordering::Relaxed);
        }
        pointer
    }
}

fn fixture(kind: &str, count: usize, context: &CompileContext) -> ObjectModule {
    let mut source = String::new();
    if kind == "dynamic" {
        source.push_str("macro step(path):\n    ready = exists path\n    if ready:\n        \"payload\" -> path\n");
    }
    for index in 0..count {
        if kind == "dynamic" {
            source.push_str(&format!("step($pwd::pwd / \"output-{index}\")\n"));
        } else {
            source.push_str(&format!("\"payload\" -> $pwd::pwd / \"output-{index}\"\n"));
        }
    }
    pipeline::compile_object_named(&mut Session::new(), &source, "object-v6.msync", context)
        .unwrap_or_else(|errors| panic!("fixture compilation failed: {errors:?}"))
}

fn measure<T, U>(
    label: &str,
    kind: &str,
    count: usize,
    phase: &str,
    samples: usize,
    mut setup: impl FnMut() -> T,
    mut action: impl FnMut(T) -> U,
) {
    drop(black_box(action(setup())));
    let mut times = Vec::with_capacity(samples);
    for _ in 0..samples {
        let input = setup();
        let start = Instant::now();
        let output = black_box(action(input));
        times.push(start.elapsed().as_secs_f64() * 1_000_000.0);
        drop(output);
    }
    times.sort_by(f64::total_cmp);
    let input = setup();
    ALLOCATIONS.store(0, Ordering::Relaxed);
    BYTES.store(0, Ordering::Relaxed);
    TRACK.store(true, Ordering::Relaxed);
    let output = black_box(action(input));
    TRACK.store(false, Ordering::Relaxed);
    let allocations = ALLOCATIONS.load(Ordering::Relaxed);
    let bytes = BYTES.load(Ordering::Relaxed);
    drop(output);
    println!(
        "{{\"label\":{label:?},\"kind\":{kind:?},\"entries\":{count},\"phase\":{phase:?},\"median_us\":{:.3},\"allocations\":{allocations},\"allocated_bytes\":{bytes}}}",
        times[times.len() / 2]
    );
}

fn options(name: &str, default: &str) -> Vec<String> {
    std::env::var(name)
        .unwrap_or_else(|_| default.to_owned())
        .split(',')
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(str::to_owned)
        .collect()
}

fn main() {
    let arguments = std::env::args()
        .filter(|value| value != "--bench")
        .collect::<Vec<_>>();
    let label = arguments.get(1).map_or("candidate", String::as_str);
    let samples = arguments
        .get(2)
        .map_or(7, |value| value.parse::<usize>().expect("sample count"));
    assert!(samples >= 3);
    let pwd = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(".temp/object-v6");
    let compile_context = CompileContext {
        pwd: pwd.clone(),
        source_file: Some(pwd.join("object-v6.msync")),
    };
    let context = Context::new(&compile_context);
    let kinds = options("MSYNC_OBJECT_KINDS", "static,dynamic");
    let phases = options(
        "MSYNC_OBJECT_PHASES",
        "validate,source_modules,contextualize",
    );
    let sizes = options("MSYNC_OBJECT_SIZES", "100,1000,5000")
        .into_iter()
        .map(|value| {
            value
                .parse::<usize>()
                .expect("MSYNC_OBJECT_SIZES must contain integers")
        })
        .collect::<Vec<_>>();
    assert!(
        kinds
            .iter()
            .all(|kind| matches!(kind.as_str(), "static" | "dynamic"))
    );
    assert!(phases.iter().all(|phase| matches!(
        phase.as_str(),
        "validate" | "source_modules" | "contextualize"
    )));
    for kind in &kinds {
        for &count in &sizes {
            let module = fixture(kind, count, &compile_context);
            if phases.iter().any(|phase| phase == "validate") {
                measure(
                    label,
                    kind,
                    count,
                    "validate",
                    samples,
                    || (),
                    |()| module.validate().unwrap(),
                );
            }
            if phases.iter().any(|phase| phase == "source_modules") {
                measure(
                    label,
                    kind,
                    count,
                    "source_modules",
                    samples,
                    || module.clone(),
                    |module| module.with_source_modules(Vec::new()).unwrap(),
                );
            }
            if phases.iter().any(|phase| phase == "contextualize") {
                measure(
                    label,
                    kind,
                    count,
                    "contextualize",
                    samples,
                    Session::new,
                    |mut session| contextualize(&module, &context, &mut session).unwrap(),
                );
            }
        }
    }
}
