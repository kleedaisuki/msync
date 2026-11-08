#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
rlib=${1:?usage: build-control-alloc-probe.sh MSYNC_RLIB OUTPUT}
output=${2:?usage: build-control-alloc-probe.sh MSYNC_RLIB OUTPUT}
mkdir -p "$root/.temp/runtime-allocation-probe" "$(dirname "$output")"
source="$root/.temp/runtime-allocation-probe/probe.rs"
# Reuse the exact timed workload constructors; only the diagnostic entry point differs.
sed -n '2,/^fn main() {/p' "$root/benches/control_runtime_v6.rs" | sed '$d' > "$source"
cat >> "$source" <<'RUST'

use std::alloc::{GlobalAlloc, Layout, System};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};

struct Counting;
static TRACK: AtomicBool = AtomicBool::new(false);
static ALLOCATIONS: AtomicU64 = AtomicU64::new(0);
static REQUESTED: AtomicU64 = AtomicU64::new(0);
static LARGEST: AtomicU64 = AtomicU64::new(0);
static BINS: [AtomicU64; 6] = [const { AtomicU64::new(0) }; 6];

fn record(size: usize) {
    if !TRACK.load(Ordering::Relaxed) { return; }
    ALLOCATIONS.fetch_add(1, Ordering::Relaxed);
    REQUESTED.fetch_add(size as u64, Ordering::Relaxed);
    LARGEST.fetch_max(size as u64, Ordering::Relaxed);
    let bin = match size { 0..=64 => 0, 65..=256 => 1, 257..=1024 => 2, 1025..=4096 => 3, 4097..=16384 => 4, _ => 5 };
    BINS[bin].fetch_add(1, Ordering::Relaxed);
}

// This probe counts allocator requests, not timing or process RSS. Delegation
// retains the system allocator's ownership, layout and failure semantics.
unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        record(layout.size());
        unsafe { System.alloc(layout) }
    }
    unsafe fn alloc_zeroed(&self, layout: Layout) -> *mut u8 {
        record(layout.size());
        unsafe { System.alloc_zeroed(layout) }
    }
    unsafe fn dealloc(&self, ptr: *mut u8, layout: Layout) {
        unsafe { System.dealloc(ptr, layout) }
    }
    unsafe fn realloc(&self, ptr: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        record(new_size);
        unsafe { System.realloc(ptr, layout, new_size) }
    }
}
#[global_allocator]
static ALLOCATOR: Counting = Counting;

fn main() {
    let count: usize = std::env::args().nth(1).unwrap_or_else(|| "2000".into()).parse().unwrap();
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/runtime-allocation-probe/fixture");
    std::fs::create_dir_all(root.join("input")).unwrap();
    std::fs::write(root.join("input/config.toml"), "verified input").unwrap();
    let options = Options::new(&root);
    println!("case,iterations,linked_span_bytes,allocations,requested_bytes,largest_request,le64,le256,le1024,le4096,le16384,larger");
    for filesystem in [false, true] {
        let program = program(&root, count, filesystem);
        ALLOCATIONS.store(0, Ordering::Relaxed);
        REQUESTED.store(0, Ordering::Relaxed);
        LARGEST.store(0, Ordering::Relaxed);
        for bin in &BINS { bin.store(0, Ordering::Relaxed); }
        TRACK.store(true, Ordering::Relaxed);
        let result = execute(&program, &options);
        TRACK.store(false, Ordering::Relaxed);
        result.unwrap();
        println!("{},{count},{},{},{},{},{},{},{},{},{},{}",
            if filesystem { "repeat-range" } else { "pure-control" },
            std::mem::size_of::<msync::telemetry::LinkedSpan>(),
            ALLOCATIONS.load(Ordering::Relaxed), REQUESTED.load(Ordering::Relaxed),
            LARGEST.load(Ordering::Relaxed), BINS[0].load(Ordering::Relaxed),
            BINS[1].load(Ordering::Relaxed), BINS[2].load(Ordering::Relaxed),
            BINS[3].load(Ordering::Relaxed), BINS[4].load(Ordering::Relaxed), BINS[5].load(Ordering::Relaxed));
    }
}
RUST
CARGO_MANIFEST_DIR="$root" rustc --edition 2024 -C opt-level=3 -A unused-imports "$source" \
  --extern "msync=$rlib" -L "dependency=$root/target/release/deps" -o "$output"
