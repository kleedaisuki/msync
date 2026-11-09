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

## Actual phase API, first candidate slice

Build `-p:HirPhaseProbe=true` to select `CandidateProgram.cs` instead of the
released compiler-total entry point. This opt-in references `Msync.Hir.dll` and
calls these actual APIs; it never calls the old `Compiler.Compile` facade:

1. `Compiler.Analyze(source, name)` returns the immutable `HirModule`.
2. `HirLowerer.Lower(hir, options).Module` consumes that known HIR, with backend
   layout retention disabled.
3. `HirArtifact.Write(hir, stream)` validates/serializes that same known HIR to a
   fresh MemoryStream, including the output-buffer allocation.

Each phase has its own first invocation, three warmups and warm observations.
Lowering and serialization do not include reanalysis. The first lowering API
does not run the optimizer: its level option sets bytecode metadata, not a
performed O2 pass. Reports explicitly say `optimizer_used: false`; two level
labels are not an optimization comparison. These are managed component
observations, not Native AOT CLI latency or the final compile pipeline's cost.

```sh
dotnet build tests/Performance/HirCompilation/Msync.HirCompilation.csproj \
  -c Release -p:HirPhaseProbe=true \
  -p:FrozenAssemblies=/absolute/frozen/candidate \
  --artifacts-path .temp/hir-phase/probe-build -o .temp/hir-phase/probe
python3 tests/Performance/HirCompilation/measure.py \
  --component .temp/hir-phase/probe/msync-hir-compilation \
  --producer-receipt /absolute/frozen/candidate/snapshot.json \
  --source-revision COMMIT --case none --case arithmetic --samples 7 \
  --work .temp/hir-phase/inputs --output .temp/hir-phase/timing.json
```

`--producer-receipt` verifies every producer DLL hash against the frozen image,
verifies the probe's copied product dependencies against that receipt, and
embeds the original receipt with its source/build qualifications. The first
slice used `.temp/hir-artifacts/snapshot-1/snapshot.json`: source collated at
`21acb297597c0d3ee403dd6f770fe6255f30c6bc`, Compiler
`0782c696ff02297a0e91c13e3d22362249eaf6df0a139ccbd0461244cedab49f`, HIR
`4c5a58178505f14435684f1c547d10d803cf98f31b8da65e419830bcf3859769`.
The producer reused immediately preceding dependency assemblies rather than
claiming a clean rebuild of its transient worktree. All six exact DLL identities
and this limitation remain in each raw report.

Only the no-standard-library scalar and `1 + 2` inputs were measured in that
slice; the incomplete standard-module linker was not benchmarked as a working
candidate. Raw observations are `.temp/hir-first-phase/{timing,retention}.json`.

| First managed slice, level 0 metadata | Analyze p50 ms / allocated B | Lower p50 ms / allocated B | HIR write p50 ms / allocated B |
| --- | ---: | ---: | ---: |
| Scalar binding | 0.0248 / 8,816 | 0.0013 / 2,904 | 0.0040 / 3,880 |
| `1 + 2` | 0.0257 / 9,136 | 0.0024 / 2,568 | 0.0126 / 3,944 |

These tiny observations establish that the actual stage boundaries are
measurable; they are not evidence of overall migration speedup. In particular,
serialization is not part of the ordinary compile-total control. The shared
host had an active compiler server and parallel MSBuild work during this first
slice. Treat its timings as API/measurement smoke observations, not causal
before/after evidence. Per-thread allocated-byte counts describe these real
phase calls independently of host scheduling, although they do not establish
retained size. Final matched warm timing needs a coordinated quiet window;
other owners' builds must not be terminated to manufacture one.

Add `--retention-only` for separately instrumented candidate lifetimes. The
analysis mode deliberately holds the complete returned HIR graph. The lowering
mode keeps its input HIR alive and measures the additional returned bytecode;
serialization keeps that same input HIR and measures its additional output
stream/buffer. The scalar analysis observation was collected heap
77,496 → 78,728 → 77,592 bytes; lowering 83,864 → 84,592 → 83,896;
serialization 87,632 → 88,704 → 87,664. Each returned result was alive while
held and collectible after release, in both inputs and both level labels.
The input HIR intentionally remains live for the latter two operations. This
does **not** demonstrate streaming compilation, body-at-a-time arena disposal,
editor-only retained projections, or their future peak-memory benefits. Those
ownership modes remain to be measured when their product APIs exist.

### Connected candidate snapshot

The same probe ran against the subsequent connected-CLI producer at
`.temp/hir-editor-cli/frozen/snapshot-receipt.json`, source head
`7a6230e09ef23ec6dfe0dbbdd2d849b9b263062b`, Compiler
`1f6af0107fecce36009bec27d2d00069aa0472a7df9f2924b0356b6c65a52de7`, HIR
`85875220b77cd104428b5d4cee63d916b3c4b359ae8fd0e81e87af0151863d0b`.
All six producer hashes were verified; only the test probe was built, with zero
warnings. No product rebuild or released-baseline remeasurement was performed.
Use `--level 0` to select just one lowering metadata level; repeated `--level`
options select other explicit levels, while the established default stays O0/O2.
The two no-standard-library source strings are unchanged and their hashes
remain in `.temp/hir-second-phase/{timing,retention}.json`.

| Connected candidate, level 0 metadata | Analyze allocated B | Lower allocated B | HIR write allocated B |
| --- | ---: | ---: | ---: |
| Scalar binding | 12,416 | 2,944 | 2,896 |
| `1 + 2` | 12,448 | 2,608 | 2,960 |

Both inputs finalize one HIR function and four nodes. Independent retention
observations again found every phase result alive while deliberately held and
collectible afterward. For scalar analysis the collected heap was
77,784 → 78,928 → 77,880 bytes; lowering with HIR input retained was
84,064 → 84,792 → 84,096; serialization with HIR input retained was
87,832 → 88,392 → 87,864. These boundaries retain the same limited ownership
meaning described above; none proves body-streaming disposal.

Raw timings were recorded for reproducibility, but concurrent shared build work
still prevents a causal timing trend claim. This continuation establishes actual
phase-call allocation and held/released stability after semantic model changes,
not complete workload acceptance or a final migration performance comparison.

### Default Compile cutover: supported workloads and construction cost

The next image is the genuine default Analyze → Lower → Optimize cutover,
`.temp/hir-editor-cli/frozen4/snapshot-receipt.json`, Compiler
`3296ff6984adfbb10360c836568b90c9ac7c24bd48b503cdf5b87385d3592b42`, HIR
`99e291ccd03c3390350513abd430bac58207a0537000e580c3b25f54542b1118`.
The complete six-DLL receipt is embedded in the raw observations under
`.temp/hir-fourth-phase/`. Again only the test probe was built.

`--include-compile` opts into measuring the actual default Compile facade as an
additional phase. This phase includes the optimizer at the selected level;
`lower_unoptimized` remains an independent backend-only observation. In
retention mode, normal Compile runs **before** the probe deliberately retains
any analyzed HIR, so its before/held/released observations do not include a
second semantic graph held by the caller. Separate Analyze/Lower/Export
observations keep their previously documented ownership boundaries.

Run `--preflight-only` first: one real default Compile per source/level records
acceptance and final bytecode counts without repeated warm observations. Pass
its JSON as `--accepted-preflight FILE` to later observations; the driver
verifies the same product image and source hashes and skips rejected cases,
retaining their diagnostics rather than benchmarking a failure. For example:

```sh
python3 tests/Performance/HirCompilation/measure.py \
  --component /absolute/frozen/probe/msync-hir-compilation \
  --producer-receipt /absolute/frozen/snapshot-receipt.json \
  --source-revision COMMIT --preflight-only \
  --work .temp/hir-default/inputs --output .temp/hir-default/preflight.json
python3 tests/Performance/HirCompilation/measure.py \
  --component /absolute/frozen/probe/msync-hir-compilation \
  --producer-receipt /absolute/frozen/snapshot-receipt.json \
  --source-revision COMMIT --include-compile --samples 3 \
  --accepted-preflight .temp/hir-default/preflight.json \
  --work .temp/hir-default/inputs --output .temp/hir-default/timing.json
```

Five regular inputs were accepted at O0/O2. The B-tree was rejected at
`msync-stdlib:///0.8.0/containers.msync:682:24` with `Scope has no member 'node'`
during MemberRead → Loop → generic instance analysis. It was skipped in all
subsequent timing and retention runs, not reported as faster compilation.

| Default cutover allocation traffic, B | Compile O0 | Compile O2 | Analyze | Lower only | HIR write |
| --- | ---: | ---: | ---: | ---: | ---: |
| Scalar | 17,040 | 30,464 | 12,968 | 2,944 | 2,960 |
| Bool | 68,816 | 183,120 | 58,888 | 6,144 | 7,240 |
| Shell | 93,288 | 249,664 | 78,872 | 9,800 | 26,312 |
| List | 1,875,760 | 4,530,224 | 1,737,944–1,738,312 | 72,288 | 508,328 |
| Traits | 36,728 | 60,688 | 31,576 | 4,024 | 13,544 |

These are three warm samples per stage/level, not an isolated latency
comparison. Allocation identifies a real construction-cost question: the
released List O0 control allocates 927,712 B, versus 1,875,760 B here (about
2.02×); O2 changes from 4,432,800 B to 4,530,224 B (about +2.2%). Meanwhile the
new O2 residual has 27 functions/228 instructions versus 43/415 in the released
control. A smaller optimized program does not by itself establish a cheaper
compiler. Most current List construction traffic occurs in semantic analysis.
The candidate still has active semantic fixes and is not the final performance
acceptance image.

| Input | HIR functions / templates | Nodes | Types | Scope layouts |
| --- | ---: | ---: | ---: | ---: |
| Scalar | 1 / 0 | 4 | 3 | 0 |
| Bool | 2 / 1 | 19 | 9 | 1 |
| Shell | 2 / 1 | 28 | 21 | 4 |
| List | 6 / 34 | 623 | 287 | 8 |
| Traits | 1 / 0 | 7 | 12 | 1 |

The unrelated-world grid was also accepted and observed at O0, with the same
one specialized `wrap` function plus entry (two finalized functions and one
generic template) in all four cases:

| Private bindings / calls | Analyze allocated B | Nodes | Types / layouts | Collected Analyze before / held / released B |
| --- | ---: | ---: | ---: | ---: |
| 32 / 8 | 366,256 | 137 | 32 / 12 | 110,496 / 130,152 / 110,592 |
| 32 / 64 | 1,546,768 | 473 | 144 / 68 | 117,224 / 188,848 / 117,320 |
| 1,024 / 8 | 4,520,240 | 2,121 | 32 / 12 | 180,144 / 430,136 / 180,240 |
| 1,024 / 64 | 5,576,936 | 2,457 | 144 / 68 | 174,584 / 476,544 / 174,680 |

The extra 56 calls allocate about 1.18 MB with 32 unrelated bindings and
1.06 MB with 1,024: this control does not show a caller-world × call-count
allocation amplification. Code sharing is visible, but type/layout counts still
grow with source call sites; sharing code must not be mislabeled as interning
every structural row or merging object identities. Semantic freshness/aliasing
remains the separate end-to-end journey's contract.

All 56 independent phase-result retention observations found the returned
result alive while held and collectible afterward. For List O2, normal Compile
collected heap was 1,526,728 → 1,556,680 → 1,526,760 B; deliberately retained
full Analyze output was 1,530,080 → 1,620,080 → 1,530,176 B. Their sampled
heap-occupancy peaks were about 5.75 MB and 3.30 MB respectively, not retained
object sizes. Some tiny phases have zero background samples; their reported
initial observation cannot establish an interval peak. Remaining cache/runtime
deltas after release are not charged to a result whose weak reference is dead.
These measurements do not prove per-body streaming disposal or final editor
retention behavior. No old baseline was remeasured and no timing speedup or
regression is attributed under concurrent shared builds.
