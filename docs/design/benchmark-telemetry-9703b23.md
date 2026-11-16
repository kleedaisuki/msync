# Product performance and telemetry: 9703b23

2026-11-15 · Linux x64 Native AOT · current-image snapshot, with one compiler failure.

## Findings and recommended order

1. **Fix recursive type resolution before treating this as a complete application baseline.**
   Standard `set-dedup` crashes during O2 compilation, before any timed execution.
   GDB shows repeated `ResolveHirType` / `NeedsResolution` / `ResolveHirTypeShallow`
   re-entry. This is a type-graph traversal problem, not a slow optimizer or VM.
2. **Remove per-line scheduling overhead from the input path.** The bounded sensor
   pipeline consumes approximately 0.97 child CPU-seconds/request at concurrency 1,
   versus 0.39 for numeric records, despite similar 0.38-second invocation latency.
   It saturates the twelve-CPU scope near 10.5 requests/s. Source inspection finds
   an unconditional `Task.Run` for each line. A reader-owned I/O driver is the
   concrete experiment; preserve lazy demand, cancellation, reader ordering and
   custom readers that can block in their Async implementation.
3. **Make REPL compilation/image publication proportional to changed work.**
   Growing exports increase compilation and execution-host costs. Do not optimize
   terminal repaint or the arithmetic increment first: the separate phase replay
   places most submission time in compilation, particularly optimization.
4. **Continue compiler work across semantic analysis and multiple passes.** B+
   spends 1.85 s analyzing and 3.74 s optimizing in its source profile. Escape
   analysis is 1.01 s, not the whole explanation. Existing program-facts sharing
   is already implemented; the old recommendation to introduce sharing is stale.
5. **Retain Auto tiering.** Five same-artifact alternating pairs show lower
   latency in polygon, signal and numeric-record workloads. More VM redesign
   needs a workload-specific benefit, not just native-entry or opcode counts.

## Image and measurement boundaries

- Commit: `9703b23073fbb2f11946d57f0a9e8804aec92185`; worktree clean when built.
- Image: `artifacts/native/benchmark-9703b23/msync`, SHA-256
  `b965ee619f44852691877e47e2dd520f1643c2d6cbecd74270a90aee03a8cbe0`.
- Adjacent `libe_sqlite3.so`: SHA-256
  `b0f5c48026fedcf9f05cf31c6fadf9e1e001ca59cd85aa68b8b6814e52c18a07`.
- SDK 10.0.403, Native AOT/runtime 10.0.14; Debian 13, kernel
  `6.12.113+deb13-amd64`, QEMU Virtual CPU 2.5+, 16 visible vCPUs.
- Serial groups pinned to CPU 12. Three measured observations and one warmup
  where supported; editor uses three fresh sessions. Session journeys contain
  256 verified submissions each, with separate 64-submission age windows.
- Applications: standard scale, seed 17, O2, Auto, source and bound routes.
  Ten workloads × two routes × three samples = **60 successful timed invocations**,
  plus separate warmups and twenty instruction-level profiles. Set has no valid
  timing. The initial multi-case receipt remains failed; seven independent
  follow-up runs complete the other workloads without relabeling that failure.
- CLI, I/O, interactive, editor and both PTY journeys completed. Every timed
  application invocation passed its stdout/file oracle. Files were reset and
  checked outside serial product timing; caches were warm.
- Throughput is a separate closed-loop 12 CPU / 24 GiB scope on CPUs 0–11,
  standard scale, bound route, two repetitions of 48 requests at C=1/4/12/24.
  All **1,152 requests / 24 batches** passed. Scope lifetime memory peak was
  **776,163,328 bytes (740.2 MiB)**, including preparation and profiling, not
  per-request memory. The focused receipt/export checker completed successfully.
- Twenty application profiles, three independent throughput profiles and two
  phase-only PTY diagnostic replays completed: **25 sessions, 86,302 records**.
  The additional Set crash recording remains `active`, explicitly excluded from
  that complete-session total. Replays use the same binary through an exec-only
  launcher; their telemetry-on timings are not primary benchmark observations.
- No historical image was timed. Older tables are context, not a paired speedup.
  Three serial samples and two throughput windows do not establish a stable P99.
  Linux `perf` is unavailable under host `perf_event_paranoid=3`; the following
  diagnosis uses internal phase/counter evidence, not sampled host CPU hotspots.

## Telemetry-off application latency

Each cell is a median in milliseconds. Source includes compile/preparation;
Bound includes process launch, artifact load and exit but excludes compile/bind.
Do not subtract route medians to manufacture a compile measurement.

| Application | Size | Source P50, ms | Bound P50, ms |
| --- | ---: | ---: | ---: |
| polygon-queries | 16,384 | 786.01 | 664.90 |
| signal-hysteresis | 65,536 | 1260.33 | 1178.18 |
| numeric-record-stream | 32,768 | 441.97 | 389.66 |
| set-dedup | 8,192 | Compilation SIGSEGV | Not prepared |
| bplus-inventory | 3,072 | 6616.24 | 1732.73 |
| heap-scheduler | 4,096 | 2100.62 | 764.69 |
| bounded-sensor-pipeline | 12,000 | 556.82 | 304.91 |
| live-policy-dispatch | 28,000 | 890.91 | 632.14 |
| unicode-path-manifest | 65,000 | 2845.11 | 2496.23 |
| local-manifest-copy | 144 | 398.99 | 220.72 |
| local-range-copy | 144 | 588.09 | 226.79 |

Unicode paths are the largest successful bound-route latency in this selection.
The profile has 65,000 calls each to `fs.extension`, `fs.filename`, `fs.stem`,
`string.is_ascii`, `string.length`, `string.starts_with`, `string.to_lowercase`
and `string.utf8_size`, plus stdin line enumeration. This motivates investigating
reader scheduling and repeated path/text transformations, not another frame pool.
Native-call frequencies alone do not assign elapsed time to any one builtin.

## Compiler phases and the Set failure

Independent telemetry-on inclusive intervals, milliseconds. Compile contains
Analyze/Lower/Optimize; explicit optimizer pass totals are nested inside Optimize.
They must not be added to their containers or to aggregate optimizer events.

| Application | Compile | Analyze | Optimize | Explicit escape passes |
| --- | ---: | ---: | ---: | ---: |
| polygon-queries | 284.49 | 8.41 | 265.78 | 43.14 |
| signal-hysteresis | 256.38 | 10.67 | 239.33 | 44.35 |
| numeric-record-stream | 100.80 | 6.60 | 87.60 | 23.97 |
| bounded-sensor-pipeline | 365.06 | 11.60 | 338.59 | 70.89 |
| bplus-inventory | 5880.65 | 1849.45 | 3739.54 | 1011.62 |
| heap-scheduler | 1648.79 | 455.08 | 1111.69 | 268.13 |
| live-policy-dispatch | 493.38 | 13.25 | 459.39 | 120.12 |
| local-manifest-copy | 212.15 | 3.84 | 198.77 | 52.64 |
| local-range-copy | 417.64 | 12.49 | 379.07 | 84.14 |
| unicode-path-manifest | 576.26 | 18.68 | 533.46 | 111.88 |

B+ optimization is 63.6% of compilation, semantic analysis 31.4%; explicit escape
passes are 17.2% of compilation. Its next optimizer totals are inlining 644.44 ms,
local values 529.17 ms and dead results 330.44 ms. The largest explicit pass is
428.70 ms inlining `<main>`; its largest escape invocation is 246.26 ms on `<main>`.
There are 314 escape invocations, not the 1,917 in the older 04465c4 snapshot.
This is descriptive context, not a controlled revision comparison.

B+ analysis allocates 394,484,248 bytes and optimization 911,546,048 bytes in the
recorded compiler intervals. Semantic instantiation reports 200 cache hits and
154 misses. Nested instantiation time must not be summed into an independent
3.44-second semantic phase: Analyze itself is 1.85 seconds.

`NativeOptimizer` already threads shared `ProgramFacts` through functions,
inlining, storage planning and register allocation, with explicit invalidations.
The next experiment should identify unchanged-query recomputation and large-main
pass work, retaining those mutation/version boundaries. This matches the
[LLVM New Pass Manager's analysis preservation/invalidation discipline](https://llvm.org/docs/NewPassManager.html),
not a recommendation to add a general incremental solver before identifying reuse.

### Crash mechanism and reproduction

The first telemetry-off Set preparation exits `-11` after 1,575.51 ms with empty
stdout/stderr. A telemetry-enabled reproduction also exits 139. GDB reproduces
SIGSEGV, with repeated frames at:

- `SemanticAnalyzer.RecursiveTypes.cs:46,65–73`: `NeedsResolution` calls
  `ResolveHirTypeShallow` while tracking a traversal-local `seen` set.
- `SemanticAnalyzer.Types.cs:171`: resolving a witness calls `ResolveHirType`
  again, which creates fresh `active` and `seen` state.

This strongly identifies stack-exhausting type-resolution re-entry; the fault
surfaces inside a GC allocation helper, not evidence by itself of GC corruption.
The crash recording contains completed binding/semantic work but no completed
Analyze or Optimize container. Increasing stack size, reducing corpus size, or
silently running O0 would not fix the type-graph contract.

The appropriate repair is one graph-resolution context across witness and shape
edges, preserving recursive type identity and availability. Do not introduce an
arbitrary recursion-depth cutoff. Keep the current benchmark image unchanged
while implementing and measuring that repair separately.

The retained fixture is under the evidence tree described below; reproduce with:

```sh
artifacts/native/benchmark-9703b23/msync --no-config --no-telemetry \
  compile -O2 \
  .temp/benchmarks/analysis-9703b23-latency/applications/set-dedup/standard/17/workload.msync \
  -o .temp/benchmarks/set-reproduction.mso
```

## Runtime representation and Auto tiering

Bound instruction-level profiles. Managed allocation is process-wide within the
execute interval, including observer work; logical storage is a different metric.
IR attempts are not hardware instructions or a host CPU-time breakdown.

| Application | IR attempts | Function entries | Descriptor allocations / reuse | Managed allocation, MiB |
| --- | ---: | ---: | ---: | ---: |
| polygon-queries | 8,868,629 | 196,614 | 17 / 196,597 | 99.90 |
| signal-hysteresis | 14,707,162 | 65,545 | 7 / 65,538 | 22.60 |
| numeric-record-stream | 2,228,446 | 32,773 | 6 / 32,767 | 36.79 |
| bplus-inventory | 12,775,401 | 252,386 | 74 / 252,312 | 117.01 |
| heap-scheduler | 5,316,061 | 115,112 | 23 / 115,089 | 48.45 |
| bounded-sensor-pipeline | 612,289 | 30,073 | 7 / 30,066 | 41.50 |
| live-policy-dispatch | 1,718,074 | 27,754 | 5 / 27,749 | 54.29 |
| unicode-path-manifest | 3,590,556 | 2 | 2 / 0 | 218.63 |

The descriptor reuse path is actually exercised. Nine of ten application bound
profiles have zero argument-snapshot allocations; B+ has thirteen. Do not infer
that all remaining managed allocation is activation construction or that moving
the whole heap unmanaged is the highest-return change.

Auto actually enters native guest code in this image. For example, signal records
two compilations, 94,637 entries and 9,261,024 raw operations; polygon records one
compilation and 14,210 entries. The old statement that Auto remains disabled does
not describe this image. Compilation time is 6.99 ms for signal and 2.86 ms for
polygon in those profiles; this alone does not establish profitability.

A follow-up holds each already-bound artifact and image fixed, uses one warmup
per policy and five alternating Auto/Off pairs on CPU 12, with exact stdout:

| Workload | Auto median, ms | Off median, ms | Median paired Auto change | Faster Auto pairs |
| --- | ---: | ---: | ---: | ---: |
| polygon-queries | 724.37 | 1020.82 | -30.50% | 5/5 |
| signal-hysteresis | 1191.09 | 1343.98 | -11.38% | 5/5 |
| numeric-record-stream | 402.90 | 439.54 | -8.34% | 4/5 |

These are policy effects for three workloads, not a universal JIT speedup or
ratios derived from unrelated suite windows. Keep Auto as the default here.
The [2025 R copy-and-patch study](https://d3s.mff.cuni.cz/publications/kocourek_copyandpatch_2025/)
is relevant to low-cost native-tier design, but its R gains are not msync evidence.
The [ECOOP 2024 instruction-pointer study](https://drops.dagstuhl.de/entities/document/10.4230/LIPIcs.ECOOP.2024.14)
also shows why actual dependency chains and operation costs matter more than
opcode counts; this host does not supply the hardware sampling needed to claim
that a particular chain is the present bottleneck.

## Concurrent throughput and its distinct limits

Cells are medians of two repetition-level values, **requests/s / invocation P50 ms**.
They do not pool all requests into one percentile. Throughput includes reset and
oracle work; invocation latency does not. All routes are bound and standard scale.

| Workload | C=1 | C=4 | C=12 | C=24 |
| --- | ---: | ---: | ---: | ---: |
| numeric-record-stream | 2.62 / 377.72 | 9.51 / 410.24 | 24.04 / 456.54 | 23.04 / 844.46 |
| bounded-sensor-pipeline | 2.61 / 384.19 | 9.18 / 431.47 | 10.55 / 1073.13 | 10.45 / 2073.61 |
| local-manifest-copy | 6.16 / 101.78 | 14.60 / 120.94 | 11.02 / 239.58 | 9.96 / 595.86 |

- Numeric records scale about 9.17x from C=1 to C=12. At C=24 throughput falls
  4.2% and invocation P50 rises 85%. Twelve workers are the better measured
  high-throughput setting under this budget.
- Sensor processing uses about 11.1 child CPUs at C=12 and 11.6 at C=24.
  Its separate bound profile uses 1,013.04 ms process CPU in 373.49 ms execute
  wall time, with 11,291 dispatch pauses and 30,073 function entries. The source
  path `NativeHostIO.StandardIO.cs:51–68` schedules a Task for every read,
  even a readily available line. These observations support a scheduling/CPU
  overhead hypothesis, not a disk-bandwidth explanation. A reader-driver change
  should be judged by child CPU/request and preserved demand/error ordering.
- Copy throughput peaks at C=4 in this harness, but that is **not** proof that
  product copy execution intrinsically saturates there. Mean reset/verification
  per request are 11.40/47.55 ms at C=1 and 358.17/1377.62 ms at C=24. Verification
  and fixture contention are substantial in the measured whole journey. Child
  CPU averages only 1.84 cores at C=24; the controller/oracle and filesystem wait
  are not captured by child CPU alone. Investigate the native invocation and
  harness work separately before redesigning the transfer scheduler.
- Copy transfer spans overlap: their summed 290.03 ms in the throughput profile
  exceeds the 117.56 ms command interval. This is normal parallel selected work,
  not a duration accounting error or an exclusive-time decomposition.

## Interactive and actual session age

Telemetry-off medians, milliseconds. Fresh LSP editor initialization excludes
spawn; interactive `lsp-initialize` includes it. File replacement is warm local
I/O, not a storage-bandwidth score.

| Journey | P50, ms |
| --- | ---: |
| CLI startup | 17.97 |
| check-small / check-batch | 18.15 / 87.07 |
| 4 KiB source / object replacement | 27.45 / 24.90 |
| 256-file source / object tree | 455.66 / 417.26 |
| 64 MiB source / object replacement | 224.76 / 168.80 |
| Persistent scalar / copy request | 54.45 / 54.00 |
| LSP initialize / hover | 17.47 / 0.22 |
| LSP actual edit / token delta | 12.45 / 8.63 |
| Realistic cold library completion | 3.73 |
| Realistic ordinary completion | 1.03 |
| flat400 open-to-hover | 63.17 |
| flat400 completion after edit burst / whole burst | 98.35 / 107.13 |

The historical one-second single-CPU LSP startup stall does not appear here.
400-binding edit bursts and REPL submission costs are more relevant remaining
interactive targets than warm hover controls.

| Submission window | Fixed-state P50, ms | Growing-export P50, ms |
| --- | ---: | ---: |
| 1–64 | 48.09 | 66.55 |
| 65–128 | 49.41 | 72.12 |
| 129–192 | 48.87 | 87.19 |
| 193–256 | 56.93 | 89.18 |

A separate phases-level replay preserves the colored PTY journey and 256 result
checks. Its source/index extraction excludes shell prelude and setup, associating
submission metric indexes 2–257 with user submissions 1–256. In the growing replay,
first/last-window median Compile is **48.36/65.64 ms**, Optimize **41.02/54.73 ms**,
Analyze **1.22/1.67 ms**, and Execute **20.24/27.02 ms**. Fixed-state compile stays
around 30–34 ms and execute 16–17 ms in the same diagnostic replay.

Thus the observed age effect is not explained primarily by parse or semantic
analysis. The REPL compiles/publishes each submission, and `execute` includes
`NativeExecutionSession.ExecuteAsync` -> `UpdateProgram`, relocatable backend
preparation, display and `AcceptSubmission`, not just guest arithmetic.
Investigate reuse at those ownership boundaries and dependency-based invalidation;
do not merely tune prompt polling or remove language diagnostics.

## Retained evidence and reproduction

- Durable summary: `artifacts/benchmarks/analysis-9703b23/summary.json`.
- Self-contained throughput SQLite and original recordings, all group receipts,
  raw samples, generated application source/stdin/oracles, portable/bound artifacts,
  build/diagnostic logs, GDB stack and fully typed analysis scripts are retained
  under that directory's `evidence/` tree.
- `evidence-manifest.json` maps capture paths to retained paths with byte sizes and
  SHA-256. SQLite files are copied through the SQLite backup API, including WAL
  content; the Set crash session is not rewritten as complete.
- Frozen executable, debug symbols and SQLite sidecar remain in
  `artifacts/native/benchmark-9703b23/`.
- Large generated transfer payloads are reproducible from the frozen harness
  hashes, seed, size and input manifests; they are not duplicated into the report.
- The initial partial suite receipt remains `failed`. Follow-up application
  receipts, successful throughput and policy ablation are distinct experiments.
  Product source was not modified to obtain these measurements.

Representative serial invocation:

```sh
python3 tests/Performance/run.py \
  --binary artifacts/native/benchmark-9703b23/msync \
  --group cli --group io --group interactive --group editor --group session \
  --group applications --samples 3 --warmups 1 --seed 17 --scale standard \
  --route both --profile instructions --jit auto --cpu 12 \
  --submissions 256 --window 64 --work .temp/benchmarks/NEW_EMPTY_DIRECTORY
```

For independent application continuation, select `--group applications --workload
CASE` with the same other application settings and a new work directory. Throughput:

```sh
python3 tests/Performance/run.py \
  --binary artifacts/native/benchmark-9703b23/msync --group throughput \
  --samples 2 --warmups 1 --seed 17 --scale standard --route bound \
  --requests 48 --concurrency 1 --concurrency 4 --concurrency 12 --concurrency 24 \
  --profile instructions --jit auto --work .temp/benchmarks/NEW_EMPTY_DIRECTORY
```

## Follow-up: telemetry-off Auto JIT end-to-end acceptance

A subsequent functional run uses this exact image with explicit `MSYNC_JIT=auto`
and isolated HOME/configuration, without product recording destinations. Fourteen
real-process task groups run under the shared 12 CPU / 24 GiB runner: nine pass
and five fail in the initial receipt. One failure is an obsolete language fixture
using `remote::path`; correcting only that input to `connection @ path` and
replaying that single case succeeds, including artifact routes. The original
54/55 language receipt remains failed, not overwritten.

Four unresolved failures remain: O2 returned-closure capture reads an unready
`EnvironmentAddress`; Set source execution SIGSEGV; colored-PTY program-input
handoff stalls; BTree's O2 source invocation reaches its 180-second timeout.
BPlusTree and Heap pass their full selected source/object/executable properties,
alongside native builtin/alias/artifact, CLI/LSP and both eighteen-case scheduling
routes. These functional outcomes are not new latency or JIT-causality evidence.

The [acceptance record](../../artifacts/validation/auto-jit-no-telemetry-9703b23/README.md)
contains the task/environment plan, exact failures, all byte logs, self-contained
receipt databases and retained fixtures. Product code and the frozen image are
unchanged; only the obsolete language-test source spelling was corrected.

### Follow-up command-boundary cost attribution

A further telemetry-off Auto replay records the existing B+/Heap journey commands.
Heap spends 25.633 s in three checks and 17.284 s in two explicit compilations;
these alone are 80.4% of its 53.355 s command total. Source run adds another
compilation-bearing 9.488 s; portable/bound execution cost only 0.310/0.209 s.
Source inspection confirms `check` currently performs full native compilation,
including optimization, before printing `valid`. The Heap journey therefore
enters the complete compiler six times for one fixture/level.

B+ spends 36.446 s in two explicit compilations, 24.786 s in source run (including
another compilation), 5.552/5.642 s in object/bound execution, and 0.739 s in
binding/disassembly: 73.165 s total command time. These execution workloads check
tree structure after 745 mutations, unlike a bare-operation microbenchmark.

BTree's non-executing `check -O2` itself times out at 180.464 s, consuming 180.840
child CPU-seconds. Even the source-to-typed-HIR route, which bypasses native
lowering/optimization, exceeds a separate 30-second diagnostic cap. This confirms
serious compiler/frontend-HIR work, rather than attributing the source timeout to
VM execution or JIT. The 22.34 s TTY failure instead includes a fifteen-second
terminal-oracle wait for an output that never arrives.

Exact per-command wall/CPU intervals, unchanged B+/Heap oracle results, source
provenance and probe plans are in the acceptance record's
`phase-diagnosis/` directory. These are new diagnostic runs, not subtraction-based
reconstructions of the original concurrent timings.
