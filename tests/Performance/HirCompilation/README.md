# Typed HIR compiler-cost observations

This probe separates two questions:

1. What does a user pay for a fresh native `check` process?
2. What time and allocation traffic does the compiler itself require after its
   code and embedded standard-source caches are warm?

`measure.py` runs six small, real source shapes: a scalar binding, Boolean
composition, shell cwd selection, List construction/count, a type relationship,
and a B-tree with 24 inserts followed by lookup. O0 exposes construction work;
default O2 includes the optimizer. Compile/check does not execute the source IO
or native storage operations. The returned program and encoded object are
retained, and output sizes/hashes are recorded outside timing.

## Frozen images

The published 0.8.0 baseline is commit
`637bd091e62e013f4003a1fa7e2c8f0b5e917140`. Its Linux archive SHA-256 is
`bd5310545263db3abb2fe25c6c3c2fcf4ebe1e23de22a8ce288a1a4611cc81f2`;
the extracted executable is
`10d27794ccf2dd4c5638fd6d77d3510de5b6f1dd61ac0e0557382e3cf36c32ad`.
The immutable local copy and provenance are under
`.temp/typed-hir-baseline/{native,receipt.json}`. Never use a moving checkout's
output as the before image.

The managed component baseline is built from that exact Git commit under
`.temp/typed-hir-baseline/source`, using isolated outputs and the local pinned
SDK. Its measured runtime/assembly identities are in every report. Managed
results are not Native AOT timings, and whole-process numbers are not summed
with component numbers.

```sh
dotnet build tests/Performance/HirCompilation/Msync.HirCompilation.csproj \
  -c Release -p:FrozenAssemblies=/absolute/frozen/assemblies \
  --artifacts-path .temp/hir-cost/probe-build -o .temp/hir-cost/probe
python3 tests/Performance/HirCompilation/measure.py \
  --binary /absolute/frozen/native/msync \
  --component .temp/hir-cost/probe/msync-hir-compilation \
  --source-revision COMMIT --samples 7 \
  --work .temp/hir-cost/inputs --output .temp/hir-cost/report.json
```

The managed probe references frozen assemblies directly and never builds the
shared product projects. Its ordinary compile-total API works for both released
and candidate compilers. Separate semantic analysis, HIR lowering and encoding
measurements will use the actual new phase APIs once those contracts exist;
the baseline implementation has no independent analysis-only timing to report.

One untimed launch precedes native observations. Each component process records
its first compile, performs three warmups, and retains seven default warm
observations. Tiered compilation is disabled identically for component runs.
Allocated bytes are current-thread managed allocation traffic, not retained
heap size or RSS. Reports keep every sample and p50/interpolated p95; no timing
threshold is a CI gate. Coordinate measurement windows with build owners before
interpreting small differences.

## Initial baseline

The first native observations used five fresh processes per case/level on the
development host with warm OS caches. Current source development was active,
so these are saved product controls, not a claim about an isolated machine's
portable latency limit. Raw data is `.temp/typed-hir-baseline/native.json`.

| Native check, p50 milliseconds | O0 | O2 |
| --- | ---: | ---: |
| Scalar binding | 14.23 | 15.78 |
| Bool | 18.33 | 15.63 |
| Shell cwd | 18.30 | 17.79 |
| List construction/count | 32.00 | 46.17 |
| Type relationship | 15.85 | 19.48 |
| B-tree construction/lookup | 1094.39 | 1562.91 |

Use a matched alternating before/candidate observation for final attribution.
The HIR migration must also measure its own analysis/lowering allocation and
time; a better residual program does not excuse unbounded semantic analysis.

The same-host managed Release component baseline uses SDK 10.0.403, seven warm
observations per source/level, and the exact published source archive. Raw data
and the three product assembly hashes are in
`.temp/typed-hir-baseline/component.json`.

| Warm compile-total | O0 p50 ms / allocated bytes | O2 p50 ms / allocated bytes |
| --- | ---: | ---: |
| Scalar binding | 0.0215 / 9,080 | 0.0682 / 22,064 |
| Bool | 0.2153 / 50,864 | 0.6432 / 159,816 |
| Shell cwd | 0.3350 / 73,960 | 1.0483 / 227,536 |
| List construction/count | 3.9702 / 927,712 | 14.5070 / 4,432,800 |
| Type relationship | 0.0900 / 25,456 | 0.2395 / 51,312 |
| B-tree construction/lookup | 1475.9988 / 146,409,824 | 1991.6555 / 379,472,456 |

The B-tree case retains 577 functions and 33,127 O0 instructions (27,850 at O2).
This is a useful expensive semantic-construction case, not a requirement to run
the historical large mutation matrix during every compiler iteration. Allocation
is especially discriminating here; the concurrent development host's wall-clock
measurements require a fresh matched comparison before attributing a speedup.

## Retention and observed heap peak

Allocation traffic alone does not answer whether typed bodies stay live longer
than needed. Run a separate instrumented process, not the timing samples:

```sh
python3 tests/Performance/HirCompilation/measure.py \
  --component /absolute/frozen/probe/msync-hir-compilation \
  --source-revision COMMIT --retention-only \
  --case none --case list --case tree \
  --work .temp/hir-retention/inputs --output .temp/hir-retention/report.json
```

After three cache-warming compiles, this mode records collected managed-heap,
GC committed/heap-size, current process RSS and process-lifetime peak RSS at
three boundaries: before compilation, with the returned Module deliberately
held, and after releasing it. A non-inlined helper owns the strong result
reference; a weak reference reports whether the Module is alive at the held and
released boundaries. The snapshots themselves have a small, explicit diagnostic
footprint, so differences are observations rather than exact transitive object
sizes. A remaining cache delta is not mislabeled as a retained Module.

A background observer requests a 1 ms interval and retains the largest
`GC.GetTotalMemory(false)` observation during compile/held-result collection.
This is a sampled lower bound on managed-heap occupancy, including uncollected
garbage, not an exact live-object peak. Scheduler resolution and short phases
can miss higher values. Process peak RSS is a separate lifetime high-water mark
that includes warmups, JIT code and native memory; it cannot be reset or equated
with managed retained size. No observer or forced collection runs inside the
ordinary timing path.

The ownership motivation follows the Rust compiler's
[THIR body lifecycle](https://rustc-dev-guide.rust-lang.org/thir.html): typed
body arenas need not survive the whole compilation. When candidate APIs settle,
the probe will observe these modes separately:

| Candidate mode | Deliberately held output | Release question |
| --- | --- | --- |
| Normal compile | Bytecode Module | Can transient typed body arenas be reclaimed after lowering? |
| Analyze/editor | Semantic result | Which declaration/type/source facts intentionally remain available? |
| Export | HIR semantic result and export operation | Are body and serialization buffers consumed without retaining duplicate whole-program images? |

This is an explicit integration TODO, not a claim that the old compiler exposes
those phase APIs. The baseline has only the compile-total/held-Module path.

The initial retention observation is
`.temp/typed-hir-baseline/retention.json`. Every tested Module was alive while
deliberately held and collectible after the helper released it. These separate
instrumented runs do not replace the timing observations above.

| Managed baseline | Collected before / held / released bytes | Observed heap peak bytes |
| --- | ---: | ---: |
| List O0 | 1,499,000 / 1,557,640 / 1,499,000 | 2,378,328 |
| List O2 | 1,525,760 / 1,581,696 / 1,525,760 | 5,925,808 |
| B-tree O0 | 2,023,624 / 6,053,736 / 2,021,064 | 28,522,432 |
| B-tree O2 | 2,084,232 / 5,710,456 / 2,081,672 | 32,562,456 |

The B-tree peak observations contain 1,054 and 1,636 samples respectively. Its
146/379 MB allocation traffic is much larger than its collected held Module or
sampled heap occupancy: these metrics answer different questions. A Typed HIR
design should not convert short-lived inference traffic into permanently
retained whole-program bodies merely to reduce its reported allocation count.

## Unrelated-world scaling control

`--world-scaling` replaces the ordinary cases with a 2 × 2 grid: 32 or 1,024
unrelated private bindings, and 8 or 64 calls of one `wrap(value: Scope)`
definition. Each argument is a fresh `Scope{item:Number}`; each result is a
fresh `Scope{value:Scope{item:Number}}`. The final Scope consumes the first,
middle and last result's numeric field. These are source-level anonymous
objects with one structural shape, not 64 distinct code identities. All private
bindings precede the definition and calls inside one block, making accidental
copies of the caller's unrelated semantic world visible without importing the
standard library or running IO. Each report records the grid dimensions,
structural shapes, consumed indices and exact source bytes/hash.

```sh
python3 tests/Performance/HirCompilation/measure.py \
  --world-scaling --component /absolute/frozen/probe/msync-hir-compilation \
  --source-revision COMMIT --samples 7 \
  --work .temp/hir-world/inputs --output .temp/hir-world/timing.json
python3 tests/Performance/HirCompilation/measure.py \
  --world-scaling --component /absolute/frozen/probe/msync-hir-compilation \
  --source-revision COMMIT --retention-only \
  --work .temp/hir-world/inputs --output .temp/hir-world/retention.json
```

Omit both binary options to generate and identify inputs without measuring a
moving compiler. All four inputs were accepted by the frozen published 0.8.0
Native compiler at O0; no candidate WIP was treated as a passing implementation.
This addition does not repeat the accepted six-case baseline measurements.

The discriminating question is whether analysis/lowering costs grow roughly
with the unrelated world plus call sites, or multiply them by copying the whole
world per call. Compare the call-count delta at each world size, not only the
largest case's elapsed time. Report allocation traffic, held HIR, held bytecode
and observed transient heap independently. The existing `functions` count is a
final bytecode observation, not the number of semantic specializations. When
the real candidate APIs are ready, record finalized HIR functions and distinct
code instances before optimization alongside it; inlining and dead-code
elimination may legitimately remove the residual wrapper. A source definition
count of one is input metadata, not a fabricated candidate sharing result.
