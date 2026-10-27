# V3: measured effect-planner scaling

## Target and decision

The prior planner's work scaled with the complete history even for unrelated
copies. This round targets actual `Plan::build`, not just parser/compiler timing:
kind propagation, source-version forwarding, backward dead-store elimination,
dependency discovery, transitive reduction, resource normalization, and cohort
validation. File-transfer throughput and process startup are separate experiments.

The replacement keeps one planning algorithm. It uses indexed may-overlap queries,
typed live effect frontiers, and sparse reachability instead of unconditional
all-pairs history scans and an `N²`-bit ancestor matrix. It also caches canonical
parent prefixes during one plan without skipping new-child probes or ordinary
file identity handles. Full semantic invariants are in `planning.md` and
`planner-proof.md`.

The measured large-plan gain justifies the design. Native Linux planning for
5,000 real local files fell from seconds to about 0.15 seconds. An initial index
prototype increased local peak heap by 17–21%; sharing immutable proof components
removed that peak-memory regression before delivery. A small Windows script
still pays about 0.17 ms extra planning latency, so this is not a blanket startup
speedup claim. There is no million-node or universal linear-time claim.

## Workload and reproducibility

`benches/planner.rs` is a release-only, custom harness:

```powershell
cargo bench --bench planner
$env:MSYNC_PLANNER_SIZES = '100,1000,5000'
$env:MSYNC_PLANNER_CASE = 'local-independent' # optional case filter
$env:MSYNC_PLANNER_SAMPLES = '3'             # optional sample count
```

Cases:

| Case | Input and expected shape |
| --- | --- |
| `seed` | Supplied five-copy script, with known file-kind rows supplied to isolate planning |
| `independent` | Distinct source/destination siblings on one synthetic SSH endpoint; no network requests |
| `local-independent` | Real, distinct ordinary source files and missing destination siblings |
| `alias-paths` | Same real local paths produced through DSL aliases; frontend work is outside the timer |
| `overwritten` | Distinct sources repeatedly assign one destination; one surviving operation |
| `forward-chain` | File `i` produces file `i+1`; stable-source forwarding preserves all final destinations |
| `dense` | Unknown source `tree/input` writes ancestor `tree`; no DSE, reduced completion chain |

The timer includes plan construction and destruction. Each case has one untimed
warmup, seven timed samples below 1,000 operations and three at/above 1,000;
reported latency is the median. `black_box` prevents unused-result elimination.
An additional, untimed build records Rust allocation count, additional peak heap,
retained-plan heap, and graph/rewrite counts. Input program construction is not
part of that heap profile. The allocation wrapper does not update memory counters
during timed samples; it only reads its tracking flag. Native kernel handle/cache
memory and process RSS are not included in the Rust heap figure.

The immutable baseline is commit `d96724d`. Baseline libraries/executables were
preserved under `target/perf-baseline/`; disposable harnesses and raw output are
under `.temp/perf-v3/`. A standalone harness was necessary while other production
modules were being edited. It links the same baseline compiler/telemetry library
on both sides and includes the new `plan.rs` on the optimized side, retaining the
same public `Backend`, `Location`, `CopyOp`, and `Program` types. The normal Cargo
benchmark is the maintained portable entrypoint; temporary archive paths are not
dependencies of the repository's tests or benchmark target.

Environment: 2026-10-27, Intel i9-12900H, Windows x86_64 MSVC Rust 1.99.0;
WSL2 Linux 6.18.33.2, x86_64 GNU Rust 1.97.1. Compare within a platform, not across
the two compilers. Linux native measurements use a private mount namespace with
a tmpfs mounted on the project's `.temp`, preserving project-local fixture
paths. Timed native processes are pinned with `taskset -c 0`. The final Windows
pair used PowerShell/child processor affinity mask `1`.

```sh
# Run inside WSL as root; the .temp mount exists only in this namespace.
unshare --mount --fork /bin/bash -c '
  set -eu
  cd /mnt/d/Code/msync
  mount --make-rprivate /
  mount -t tmpfs -o size=256m,mode=1777 msync-planner-perf .temp
  MSYNC_PLANNER_SIZES=100,1000 taskset -c 0 target/perf-baseline/planner-d96724d-linux
  MSYNC_PLANNER_SIZES=100,1000 taskset -c 0 target/perf-probe/planner-current-linux
'
```

## Native Linux latency and graph counts

Milliseconds, warm sample medians. This is actual planning, not copy I/O.

| Workload | N | Baseline ms | Indexed ms | Retained nodes / edges |
| --- | ---: | ---: | ---: | ---: |
| Seed | 5 | 0.048 | 0.041 | 5 / 4 |
| Independent | 100 | 1.009 | 0.536 | 100 / 0 |
| Local independent | 100 | 4.858 | 2.300 | 100 / 0 |
| Alias paths | 100 | 4.610 | 2.233 | 100 / 0 |
| Overwritten | 100 | 0.430 | 0.286 | 1 / 0 |
| Forward chain | 100 | 1.325 | 0.771 | 100 / 0 |
| Dense | 100 | 0.635 | 0.220 | 100 / 99 |
| Independent | 1,000 | 86.634 | 7.184 | 1,000 / 0 |
| Local independent | 1,000 | 267.926 | 24.126 | 1,000 / 0 |
| Alias paths | 1,000 | 224.340 | 23.745 | 1,000 / 0 |
| Overwritten | 1,000 | 17.071 | 2.903 | 1 / 0 |
| Forward chain | 1,000 | 77.574 | 7.920 | 1,000 / 0 |
| Dense | 1,000 | 42.995 | 1.975 | 1,000 / 999 |
| Local independent | 5,000 | 9,756.666 | 145.855 | 5,000 / 0 |

The 5,000-file baseline ranged 7,371–10,558 ms; final optimized samples were
140–147 ms.
One other agent reported an incremental Windows build during the broader Linux
window. These are not a confidence interval or an exact guaranteed speedup.
Even using the baseline minimum against the optimized maximum, the large-plan
difference remains substantial. Work counters and preserved DAG shapes identify
the mechanism independently of host timing noise.

## Windows paired latency

The final Windows before/after harness uses the same allocation wrapper, baseline
IR library, CPU affinity and fixture paths. Early exploratory timings used an
always-updating allocation counter; they are not used for these paired latency
claims. Milliseconds:

| Workload | N | Baseline ms | Indexed ms |
| --- | ---: | ---: | ---: |
| Seed | 5 | 0.514 | 0.683 |
| Independent | 100 | 1.672 | 1.014 |
| Local independent | 100 | 37.199 | 26.819 |
| Alias paths | 100 | 38.279 | 33.679 |
| Overwritten | 100 | 0.769 | 0.472 |
| Forward chain | 100 | 2.064 | 1.128 |
| Dense | 100 | 0.997 | 0.438 |
| Independent | 1,000 | 113.393 | 12.479 |
| Local independent | 1,000 | 914.895 | 293.728 |
| Alias paths | 1,000 | 887.606 | 253.135 |
| Overwritten | 1,000 | 26.268 | 5.104 |
| Forward chain | 1,000 | 123.898 | 12.783 |
| Dense | 1,000 | 57.723 | 4.031 |

A short independent incremental build overlapped the early part of this window;
small-case ranges include outliers. The 1,000-local-file baseline ranged
896–1,019 ms, optimized 291–352 ms. The tiny seed did regress by about 0.17 ms;
an adaptive second planner was not added to conceal that constant cost. Judge
small-script user latency using the separate whole-CLI startup/run benchmark.

For scaling inspection, final indexed 5,000-operation Windows medians were
60.3 ms independent, 1,518.8 ms local, 1,652.2 ms aliases, 28.5 ms overwritten,
70.3 ms forwarded chain, and 20.0 ms dense. Only the optimized 5,000-operation
latencies are quoted here: the original Windows 5,000-case baseline had different
profiling overhead and concurrent-build noise. Its heap profile and deterministic
graph counts remain useful, but its latency is not a clean paired speedup ratio.

## Resource work and memory

`Plan::build_profiled` exposes additive `PlanningStats`; the existing `build`
signature and `Plan` public fields remain unchanged. Counts also appear in plan
telemetry. `canonicalize_probes` counts calls to the standard library function,
not the internal number of filesystem syscalls.

| Local workload | Baseline canonicalize probes | Indexed probes | Baseline / indexed identity handles |
| --- | ---: | ---: | ---: |
| 100 distinct files | 400 | 202 | 100 / 100 |
| 1,000 distinct files | 4,000 | 2,002 | 1,000 / 1,000 |
| 5,000 distinct files | Not separately instrumented | 10,002 | Indexed: 5,000 |

Baseline probe counts above came from a separate untimed instrumented copy of
the archived planner. File-kind inspection and file identity opens were not
removed to manufacture gains. Existing FIFO/socket/named-pipe gates remain in
force. A regression with 64 destinations sharing one ordinary source checks
65 resource misses, at most 68 canonicalize probes, one identity handle, and no
dependency candidates.

The indexed independent 5,000-file graph considers zero hazard candidates rather
than every historical pair. The dense 5,000-node unknown-resource graph considers
4,999 candidates and retains 4,999 edges; it does not allocate every historical
edge and then erase them. Actual 5,000-operation shapes remain unchanged:
overwritten = 1 node / 4,999 removals / 2 rounds, forward chain = 5,000 nodes /
4,999 forwardings / 2 rounds, dense = 5,000 nodes / 4,999 edges / 1 round.

Peak additional Rust heap, decimal MB:

| Platform / 5,000-operation case | Baseline MB | Indexed MB |
| --- | ---: | ---: |
| Linux native local files | 24.86 | 20.21 |
| Windows local files | 30.15 | 28.75 |
| Windows independent remote paths | 16.94 | 13.89 |
| Windows forward chain | 14.98 | 12.57 |
| Windows dense unknown effects | 12.22 | 9.15 |

The first indexed prototype increased local peak heap by 17–21%. Allocation
profiles exposed deeply cloned immutable path vectors at cache/effect boundaries.
Final proof-key components, comparison components, and Windows projections use
private `Arc<[String]>` arrays; non-Windows comparisons share the exact component
array. Public locations and per-access scopes remain separately owned, and
equality remains structural rather than pointer-based. This fixes ownership cost
for every size instead of introducing a special small-script algorithm.

At 5,000 local operations, Windows allocation count fell from 1,417,880 to 868,899;
Linux fell from 1,007,880 to 517,526. Retained-plan heap is Linux 10.94 → 8.47 MB,
Windows 13.05 → 13.37 MB: the latter still has a small retained-object/header cost
even though working peak is lower. Singleton ID sets are inline to avoid a B-tree
allocation per unique leaf/identity. Removing the always-allocated `N²` ancestor
matrix improves asymptotic storage, not every constant in every script.

## Correctness, scope, and precedents

Validation includes all 22 pre-existing planner tests (192 ordinary-file random
programs and reverse-ready schedules), added rooted/relative negative-proof and
external-hardlink retirement guards, probe-count tests, a 128-case indexed-overlap
property against brute force, and 10,000 mixed-class/endpoints differential
programs against `d96724d` comparing full rendered plans and explicit hazard
vectors. Independent V3 tests additionally check every raw retained-node hazard
has completion reachability over hierarchical paths, wildcards, hardlinks and
Windows/WSL projections, and check 24 co-start waves without a global fence.

The one deliberate baseline semantic correction separates remote absolute and
relative paths in positive proof keys. Conservative conflict detection is kept;
different-root paths do not suddenly become safe independent operations.

The indexed passes still alternate to the actual fixed point. A bounded planner
walk or an arbitrary rewrite-round cutoff was not substituted for correctness.
Paired stream channels retain co-start exceptions and complete-frontier
retirement is disabled for their modifier resources. Hash collisions and WSL
projections can add hazards or block optimization, never establish file equality.

Relevant primary precedents:

- [LLVM MemorySSA](https://llvm.org/docs/MemorySSA.html) models reaching memory
  definitions and clobber queries to avoid repeatedly scanning unrelated writes.
  Here the linear DSL needs indexed nearest effects rather than a full CFG/SSA.
- The current [LLVM DSE implementation](https://llvm.org/docs/doxygen/DeadStoreElimination_8cpp_source.html)
  checks observations between killing and killed stores. It also illustrates why
  uncertain clobbers must block elimination. This planner does not copy LLVM's
  arbitrary exploration budgets into a language contract requiring a fixed point.
- Mokhov, Mitchell and Peyton Jones,
  [Build Systems à la Carte](https://www.microsoft.com/en-us/research/publication/build-systems-la-carte/)
  separates determining required work from scheduling it. The optimized plan
  remains a reusable dependency value; runtime admission/resource policy is not
  embedded in conflict lookup.

Next performance work should use real CLI traces: planning improvements do not
promise faster disks, downloads, FIFO peers, or process creation. Startup,
metadata batching, native copy and cancellation paths require their own paired
end-to-end measurements.
