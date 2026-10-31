//! Release-only typed frontend lowering and lexical-environment scaling probe.
use std::alloc::{GlobalAlloc, Layout, System};
use std::hint::black_box;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::Instant;

use msync::compiler::{CompileContext, structured::Session};
use msync::{language, source::Source};

struct Allocator;
static TRACK: AtomicBool = AtomicBool::new(false);
static COUNT: AtomicUsize = AtomicUsize::new(0);
static BYTES: AtomicUsize = AtomicUsize::new(0);
#[global_allocator]
static ALLOCATOR: Allocator = Allocator;
unsafe impl GlobalAlloc for Allocator {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        let pointer = unsafe { System.alloc(layout) };
        if !pointer.is_null() && TRACK.load(Ordering::Relaxed) {
            COUNT.fetch_add(1, Ordering::Relaxed);
            BYTES.fetch_add(layout.size(), Ordering::Relaxed);
        }
        pointer
    }
    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        unsafe { System.dealloc(pointer, layout) };
    }
    unsafe fn realloc(&self, pointer: *mut u8, layout: Layout, size: usize) -> *mut u8 {
        let pointer = unsafe { System.realloc(pointer, layout, size) };
        if !pointer.is_null() && TRACK.load(Ordering::Relaxed) {
            COUNT.fetch_add(1, Ordering::Relaxed);
            BYTES.fetch_add(size, Ordering::Relaxed);
        }
        pointer
    }
}

fn script(case: &str, size: usize) -> String {
    let mut source = String::new();
    if case == "symbols" {
        source.push_str("symbol child(base):\n    return base / leaf\n");
    }
    for index in 0..size {
        match case {
            "transfers" => source.push_str(&format!("input{index} -> output{index}\n")),
            "symbols" => source.push_str(&format!("child(root) -> output{index}\n")),
            "conditions" => source.push_str(&format!("if exists input{index}:\n    \"payload\" -> output{index}\nelse:\n    \"fallback\" -> output{index}\n")),
            "captures" => source.push_str(&format!("value{index} := root{index}\nsymbol symbol{index}():\n    global value{index}\n    return value{index}\n")),
            "macro-captures" => source.push_str(&format!("value{index} := root{index}\nmacro macro{index}():\n    global value{index}\n    value{index} -> output{index}\n")),
            "bindings" => source.push_str(&format!("value{index} := root{index}\n")),
            _ => panic!("unknown compiler case {case}"),
        }
    }
    source
}

fn main() {
    if cfg!(debug_assertions) {
        eprintln!("compiler_v6 requires release mode");
        return;
    }
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let context = CompileContext {
        pwd: root.clone(),
        source_file: Some(root.join("compiler-v6.msync")),
    };
    let sizes = std::env::var("MSYNC_COMPILER_SIZES").unwrap_or_else(|_| "10,100,1000".into());
    let cases = std::env::var("MSYNC_COMPILER_CASES")
        .unwrap_or_else(|_| "transfers,symbols,conditions,captures,macro-captures,bindings".into());
    let samples: usize = std::env::var("MSYNC_COMPILER_SAMPLES")
        .unwrap_or_else(|_| "7".into())
        .parse()
        .unwrap();
    assert!(samples > 0);
    println!(
        "compiler_v6 cwd={} profile=release samples={samples} allocations=separate-untimed-build",
        root.display()
    );
    for size in sizes.split(',').map(|part| part.parse::<usize>().unwrap()) {
        for case in cases.split(',') {
            let text = script(case, size);
            let document = language::parse(&text).unwrap();
            let source = Source::new("compiler-v6.msync", text);
            let mut initial = Session::new();
            if case == "bindings" {
                initial
                    .compile_named(&document, &context, source.clone())
                    .unwrap();
            }
            let repl_document = language::parse("value0/leaf -> output\n").unwrap();
            let repl_source = Source::new("repl.msync", "value0/leaf -> output\n");
            let run = || {
                let lowered = if case == "bindings" {
                    initial
                        .clone()
                        .compile_named(black_box(&repl_document), &context, repl_source.clone())
                        .unwrap()
                } else {
                    Session::new()
                        .compile_named(black_box(&document), &context, source.clone())
                        .unwrap()
                };
                black_box(lowered);
            };
            run();
            let mut timings = Vec::with_capacity(samples);
            for _ in 0..samples {
                let start = Instant::now();
                run();
                timings.push(start.elapsed());
            }
            timings.sort_unstable();
            COUNT.store(0, Ordering::Relaxed);
            BYTES.store(0, Ordering::Relaxed);
            TRACK.store(true, Ordering::Relaxed);
            run();
            TRACK.store(false, Ordering::Relaxed);
            println!(
                "lower/{case} n={size} median_ms={:.6} min_ms={:.6} max_ms={:.6} allocations={} allocated_bytes={}",
                timings[samples / 2].as_secs_f64() * 1000.0,
                timings[0].as_secs_f64() * 1000.0,
                timings[samples - 1].as_secs_f64() * 1000.0,
                COUNT.load(Ordering::Relaxed),
                BYTES.load(Ordering::Relaxed)
            );
        }
    }
}
