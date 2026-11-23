# O2 compile-cost follow-up

Source revision: `90857bf3`; workload SHA-256:
`7331a08a68bd76c9f90affe5214de630ea0cd693468607e78860a215881ceafd`.
This explains the previously retained +37.39 ms paired compiler observation.
The initial causal investigation did not change product source or optimization
policy. The subsequent repairs below preserve that policy while removing
container allocation and repeated structural work.

## Result

The regression is a pipeline interaction, not only the time spent inside
InstCombine. Aggregate-store expansion creates considerably more memory
definitions, and DSE repeatedly walks those definitions. Nearly all additional
work belongs to `<main>`, especially standard-library namespace initialization.

The retained detailed traces give these exact main-function work counts:

| Work | InstSimplify control | InstCombine final |
| --- | ---: | ---: |
| DSE input instructions | 1,095 | 1,395 |
| DSE collected definitions | 261 | 507 |
| DSE definition-walk visits | 1,986 | 11,637 |
| DSE overwrite queries | 1,723 | 11,128 |
| Pointer-offset decompositions | 3,070 | 21,290 |
| Pointer-cast stripping queries | 3,446 | 22,256 |
| DSE input/output instructions | 1,095 / 1,095 | 1,395 / 1,395 |

InstCombine expands 123 aggregate stores into 369 component stores. Source
attribution identifies **120** in `ranges.msync`, one in `fs.msync`, one in
`string.msync`, and one in the user program. These are predominantly initial
Function descriptor stores, not hot arithmetic in user loops. DSE mostly
rediscovers that the writes address distinct fields; the main function is
unchanged by this DSE invocation. Other functions still have profitable DSE,
so disabling DSE globally is not the repair.

## Low-perturbation Native AOT experiment

The existing detailed trace instruments each InstCombine visit and copies its
growing metrics dictionary before/after the visit. Its 75.37 ms InstCombine
interval therefore cannot be presented as the telemetry-off cost.

`scripts/compiler/profile_o2_cost.py` creates a disposable snapshot of the
recorded source revision, not the current HPIR implementation. It
inserts per-function/pass clocks and allocation counters, using no ordinary
compiler recorder. The pass calls and their order are the production ones.
One Native AOT binary supports a diagnostic-only four-cell experiment:

1. full InstCombine, DSE on;
2. previous InstSimplify, DSE on;
3. full InstCombine, memory-cleanup off;
4. previous InstSimplify, memory-cleanup off.

The switches exist **only in the disposable overlay**. They are not shipped
compiler options or fallback implementations. Memory-cleanup ablation omits
its cycle/AA/MemorySSA setup as well as DSE, so the experiment attributes that
whole downstream stage, not just its inner loop.

The final balanced nine-block run has **36 successful measured compilations**.
Each mode gets one untimed warmup; a four-treatment Latin-square order balances
positions. Separate phase profiles repeat each mode three times. Ordinary
telemetry is disabled throughout. Child process CPU is also retained to help
separate host scheduling from computation.

The additive **mean paired CPU** decomposition is:

| Contrast | Increment |
| --- | ---: |
| Full versus previous pipeline | +33.04 ms |
| Extra memory-cleanup cost induced by InstCombine | +23.55 ms |
| Difference with memory-cleanup absent | +9.49 ms |

The corresponding paired wall-time means are +34.29, +24.69 and +9.59 ms.
The contrasts are computed within each complete four-cell block; medians of
independent routes are not subtracted. The original +37.39 ms is a separate
control/final binary comparison and is not expected to equal an overlay
experiment to a fraction of a millisecond.

The source-copy regressions have the same amplification, and are larger rather
than being unexplained file-I/O changes. Three separate pass-only observations
per mode on the existing standard/29 copy sources give these stage medians:

| Source | Combine, previous / full | Memory cleanup, previous / full |
| --- | ---: | ---: |
| local-manifest-copy | 0.41 / 19.11 ms | 9.31 / 47.65 ms |
| local-range-copy | 0.47 / 14.05 ms | 9.37 / 68.68 ms |

All twelve copy-source compilations succeeded. These are direct compiler-stage
observations, not source-run minus bound-run subtraction. They support compiler
work as the explanation for the meaningful source-copy regressions. They do
not explain or relabel the small bound-only differences in the earlier matrix.

Three separate low-detail profiles place InstCombine at approximately
21–24 ms, versus 0.7–0.9 ms for the old simplifier. Memory cleanup is roughly
28 ms with the expanded IR, versus 5 ms without it. These individual phase
medians must not be added to reconstruct the paired process increment.
Other stages and collection placement vary between fresh processes.

## Allocation and implementation mechanism

The low-detail profiles consistently allocate approximately:

| Stage | Previous | Full |
| --- | ---: | ---: |
| Combine | 0.24 MiB | 3.90 MiB |
| Memory cleanup | 2.58 MiB | 11.98 MiB |

InstCombine performs 1,556 main-function GEP visits and about 700 store visits.
Visitor timing alone is not a stable CPU hotspot attribution: one run places
about 12 ms in GEP visits, another puts it in Store visits, with nearly identical
visit counts and allocations. Collection/preemption placement is a likely
explanation; these measurements do not establish an exclusive GC pause total.
The stable findings are IR expansion, query counts and allocated bytes.

There is a concrete production-port cost to investigate next:
`LlvmValueTracking.PointerOffsets.cs` and `.PointerCasts.cs` each allocate a
`HashSet<LlvmValue>` at query entry, including the common one-/two-node pointer
chain. The two DSE query families together grow from 6,516 to 43,546 calls:
**37,030 additional small-set constructions** in the main function. Upstream
`Value.cpp` uses inline `SmallPtrSet<const Value *, 4>` at both sites. This is a
container/allocation mismatch, not evidence that the upstream algorithm performs
no alias or overwrite reasoning. Its exact latency contribution is not yet
isolated and must not be equated to all 23.55 ms.

Recommended implementation order:

1. Preserve the upstream small-set storage behavior for short pointer walks,
   without adding another analysis or dropping cycle semantics.
2. Measure the resulting memory-cleanup allocations/cost on this same source.
3. Investigate repeated immutable base/constant-offset work and the missing
   surrounding memory-optimization stages only if the remaining cost warrants
   it. Keep invalidation local to actual IR mutation; do not introduce a global
   heap solver or an arbitrary optimization cutoff.

The pinned upstream pipeline really does place InstCombine before DSE, with
other intervening passes. Moving or disabling passes merely to improve this
one timing is not established as a compatible solution.

## Evidence and reproduction

Portable observations are in `results/o2-cost-90857bf3/`: final balanced
`ablation-balanced.json`, an independent initial `ablation-first.json`, overlay
input hashes, and `summary.json` with the original detailed work counters.
All raw mode samples, allocations, failures and phase rows are retained.
Earlier diagnostic attempts occasionally reached a PointerTyID target-lowering
error; those attempts are not relabeled successful or used as a clean matrix.
Five unmodified final/control compilation checks each passed; a production
reproduction of that separate diagnostic failure is not established.

```sh
python3 scripts/compiler/profile_o2_cost.py prepare --work .temp/o2-cost-probe
cd .temp/o2-cost-probe/checkout
DOTNET_PROCESSOR_COUNT=12 dotnet publish src/Msync.Cli/Msync.Cli.csproj \
  -c Release -r linux-x64 -m:12 -p:UseSharedCompilation=false -o ../image
cd ../../..
python3 scripts/compiler/profile_o2_cost.py measure --work .temp/o2-cost-probe \
  --source .temp/llvm-instcombine-compile/midend.msync --pairs 9
```

The source fixture is `SOURCE` in `tests/Msync.EndToEnd/native_midend_journey.py`;
the recorded hash above fixes its exact contents. No product dependency on this
Python infrastructure is introduced.

Both full/on and previous-simplifier/on diagnostic variants additionally passed
the existing real midend source, detached-HIR/object and bound O2 journey,
including all 29 expected output files and selected-failure behavior. The
receipts are `journey-full.json` and `journey-simplify.json`. This is focused
experiment validation, not a substitute for the production acceptance receipts.

Primary references: pinned LLVM
[DSE](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/DeadStoreElimination.cpp),
[pointer walks](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/IR/Value.cpp),
[default pass order](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Passes/PassBuilderPipelines.cpp),
and the [InstCombine contribution guide](https://llvm.org/docs/InstCombineContributorGuide.html).

## Repair: inline pointer-walk visited sets

`LlvmPointerVisitedSet` restores the pinned `Value.cpp` choice of four inline
reference entries for both pointer-cast and pointer-offset walks. The .NET
representation is a value-type `InlineArray(4)`, not a newly allocated array.
Only insertion of a fifth distinct reference allocates a reference-identity
`HashSet`; the four existing entries migrate once. Long walks remain unbounded,
and cycles still stop at the first repeated reference. The walkers' transfer
rules, stopping conditions and callback order are unchanged.

The shared sink-neutral `value_tracking.pointer_visited.*` counters distinguish
inline inserts, identity probes and hits from overflow promotions, migrated
entries, subsequent hash queries, inserts and hits. A null metrics sink performs
no counter updates or telemetry allocations. The new fixture
`PointerWalkSmallSetProperties` covers depths zero through eight, four/five-entry
boundaries, short and overflow cycles, GEP displacement accumulation,
address-space rules and ConstantExpr chains. Build and matched-workload timing
are owned by the integrating task; this implementation note does not claim a
measured speedup before that comparison.

## Repair: invocation-local DSE address facts

DSE now shares cast stripping, underlying-object and constant-offset results
within its existing per-function `State`. This caches structural pointer facts,
not alias answers, memory contents or whole-program escape conclusions. Query
modes and data layout are fixed by that invocation; exact pointer identities
and APInt index widths remain part of the computation. Actual operand rewrites
invalidate the table before recursive deletion or store merging. The
[design note](../../design/llvm-dse-address-facts.md) records the boundaries.

Request/hit/miss telemetry uses full static metric keys: the recording path
must not allocate a pair of prefixed strings for every cache access. The old
DSE metric helper is otherwise unchanged. No scan, walk or definition budget,
pass order, enabled optimization or overwrite decision was changed.

The integrated build also repairs the pre-existing folded-pointer-load target
selection gap exposed by an initial benchmark warmup. Its physical storage
metadata was already retained on the Use; storage/result-type selection now
consumes it consistently with address emission. The identical exception stack
was present in the pre-repair diagnostic experiment. A deterministic nested
Scope journey exercises the missing state directly and after Native2 roundtrip;
there is no new inference pass or fallback. See the
[target-layout follow-up](../../design/llvm-performance-regression-review.md#folded-pointer-load-physical-storage-follow-up)
and [independent review](o2-cost-fix-review.md).

### Matched comparison method

All compiler images use the same source hash at the top of this document and
ordinary Native AOT release compilation, not the diagnostic ablation switches.
The original control and pre-repair image are preserved unchanged. Each command
is a fresh process compiling source with `--no-config --no-telemetry -O2`, with
the same output path overwritten and artifact hashes computed outside the timer.
Source compilation, not detached-HIR compilation or `check`, is measured.
No build, trace collection or functional journey runs alongside these timings.

The initial nine-pair comparisons and the first 12-block comparison are retained,
not selected away. Their variability motivated a larger balanced comparison:
all six permutations of original/pre-repair/repaired images, repeated ten times.
Each image occupies every position equally. `measure_o2_repair.py` records all
60 blocks (180 measured compilations), wall time and child user+system CPU time,
plus one untimed warmup per image. Nonzero exits retain stderr and fail the
experiment rather than being treated as fast compilations. Contrasts are paired
within each block; additive decomposition uses means, not sums of independent
medians. The paired medians remain available for comparison with the historical
37.39 ms observation.

### Address-decomposition repair result

Address-decomposition Native AOT image SHA-256:
`6c49d967008f6efd251c1c50d4ccb0046f71dd42041ad016e70160a536f18ef2`.
The original image is `3c821875…dceb410`; pre-repair is
`ba275ad3…c4b0324`. The exact hashes, full orders and individual observations
are in [the delivery comparison](results/o2-address-repair/compile-delivery-balanced-60.json),
with a compact [summary](results/o2-address-repair/summary.json).
All 180 measured compilations and three warmups succeeded.

| Same-round contrast | Mean wall (ms) | Paired median wall (ms) | Mean child CPU (ms) |
| --- | ---: | ---: | ---: |
| Pre-repair − original | +43.77 | +43.15 | +43.68 |
| Repaired − original | **+22.75** | **+22.20** | **+22.71** |
| Repaired − pre-repair | **−21.02** | −19.16 | **−20.97** |

Mean total compile wall times are 99.56 ms original, 143.34 ms pre-repair and
122.31 ms repaired. The paired-mean standard errors are 2.22, 1.89 and 2.57 ms
respectively for the three contrasts. This repairs about half of the extra cost
in the current balanced run; it does **not** eliminate the whole regression.
The earlier +37.39 ms was a nine-pair median under earlier conditions. The
remaining cost is measured against the original image directly, not computed
as `37.39 − 21.02`. Earlier nine-pair and 12/60-block exploratory results are
retained beside the delivery result; only this comparison uses the exact
address-decomposition image after converting the new storage-selection flags into real
cumulative counters.

The main-function DSE work explains the mechanism:

| Work | Pre-repair | Repaired |
| --- | ---: | ---: |
| Input / output instructions | 1,395 / 1,395 | 1,395 / 1,395 |
| Collected memory definitions | 507 | 507 |
| Definition-walk visits | 11,637 | 11,637 |
| Overwrite queries | 11,128 | 11,128 |
| Walk-budget stops | 72 | 72 |
| Cast requests / actual helper calls | 22,256 / 22,256 | 22,256 / **491** |
| Offset requests / actual helper calls | 21,290 / 21,290 | 21,290 / **460** |
| Underlying-object requests / cache misses | — | 23,268 / 505 |
| Short visited-set overflow promotions within DSE | eager HashSets | **0** |
| Detailed-recording managed allocation (bytes) | 15,510,976 | 4,889,392 |

The allocation row includes detailed observer work, unlike the off-recording
latency experiment. Main deletes no instructions before or after; other
functions retain useful DSE. The real overwrite/clobber/escaped-alias/loop
journey still removes both overwritten diamond stores, reduces component
stores 5→1 and actual overwritten field-store execution hits 4→0, while
preserving callback and IO ordering. It executes all four cases at O0/O2 and
again after Native2 roundtrip. Its old raw object-graph JSON dump encountered
recursive source-type metadata; it now uses the product's canonical Native
disassembler rather than serializing internal objects.

The final Native AOT midend journey passes source, detached-HIR/object and
bound routes, all 29 outputs and the selected failure at O0/O2. Execution counts
match pre-repair exactly: O0 3,061 instructions / 58 calls / 89 logical
allocations; O2 2,566 / 58 / 88. Long pointer walks and cycles, 32-bit index
arithmetic, negative offsets, deletion/merge invalidation and the deterministic
folded pointer-load journey also pass. Receipts are retained in the same result
directory. The remaining ~23 ms is still real compiler work: the expensive
aggregate-store expansion and 11,637 DSE definition visits remain enabled.
These changes remove repeated decomposition and small-container heap work;
they do not establish a new exclusive phase breakdown for the residual.

Reproduce the final balanced comparison with the preserved images:

```sh
MSYNC_JIT=auto python3 scripts/compiler/measure_o2_repair.py \
  --original artifacts/native/llvm-sroa/msync \
  --before artifacts/native/llvm-reviewed-final/msync \
  --after artifacts/native/o2-cost-delivery/msync \
  --source .temp/llvm-instcombine-compile/midend.msync \
  --receipt artifacts/diagnostics/o2-cost-fix/aot-delivery-receipt.json \
  --work .temp/o2-cost-delivery-balanced --rounds 10 \
  --output artifacts/diagnostics/o2-cost-fix/compile-delivery-balanced-60.json
```

## Candidate selection repair

Keeping 11,128 overwrite queries was not a requirement. Preserving DSE means
retaining profitable deletion/merge opportunities and observable results, not
preserving needless comparisons. The address cache repaired repeated
computation but left repeated candidate discovery. The subsequent repair
changes how candidate memory regions are represented.

An online same-block store run now partitions known independent allocation and
static roots, with a sorted union of written byte intervals per root. A new
range that intersects no prior write jumps to the run boundary. It does not
walk each unrelated field/object again. Unknown roots stay within one exact
base; genuinely observing reads and real effects end the run. Ordinary reads
of an independent, unwritten root are unrelated to every write already in the
run and do not force a restart. This matters for the 50 literal loads between
Function descriptor stores in the actual main function. There is no global
heap solver, CFG repartitioning, budget reduction or disabled optimization.

The first single-root attempt was insufficient on the real sample: 11,128
queries only fell to 10,601. Multi-root partitioning plus unrelated-read
handling is what removes the dominant repeated traversal. The implemented
model reuses existing BasicAA independent-object facts; it does not introduce
new source alias/lifetime constraints. LLVM MemorySSA deliberately has a single
memory-state chain; its optimized edge cannot be reused for arbitrary other
locations. The representation trade-off is consistent with Novillo's original
[Memory SSA paper](https://www.airs.com/dnovillo/Papers/mem-ssa.pdf), sections
2.2/3, but this repair is deliberately local to straight-line store runs.

Final same-source Native AOT detailed counters:

| Main-function work | Before candidate selection | Repaired |
| --- | ---: | ---: |
| Overwrite queries | 11,128 | **94** |
| Definition visits | 11,637 | **603** |
| Walk-limit stops | 72 | **1** |
| Collected definitions | 507 | 507 |
| Input / output instructions | 1,395 / 1,395 | 1,395 / 1,395 |
| Detailed-recording allocated bytes | 4,889,392 | 1,076,808 |

The 13,519 `disjoint_definitions_skipped` counter counts indexed segment
lengths, not a subtraction from the old walk visits: indexed jumps can extend
past where the old budget stopped. Overlapping candidates still use the
ordinary DSE deletion and partial-store merging paths. The actual four-case
DSE journey retains its 5→1 component stores and 4→0 overwritten store hits.

The unchanged pre-index control failed during the first comparison, exposing
a second part of the physical-layout omission: aggregate Function load/store
scalarization and narrowing discarded a retained memory-Use declaration.
The producer now carries that existing declaration into derived addresses;
the Function-component target consumer accepts the actual Use. This is
information preservation, not another type inference pass. Six deterministic
executions cover ordinary pointer fields and aggregate Function fields, both
zero-GEP folding orders and Native2 roundtrip. The failed control comparison
is retained as `aborted-single-root-comparison.json`, not counted as a successful
or fast compile.

The final image is
`5dbc0aa1e08ab06527289cc38edba50de4db0b2e85d716e229fdb9a69c288401`.
The final comparison uses the preserved pre-InstCombine original image and
this repaired image: 60 alternating-order pairs, 120 successful measured
source compilations, fresh processes, ordinary telemetry off, no concurrent
build/profile/journey. The original control does not contain the aggregate
scalarization responsible for the failed intermediate control.

- Original median compile: **105.23 ms**; repaired median: **124.66 ms**.
- Direct paired residual: **+19.11 ms median**, **+18.70 ms mean**.
- Compare against the original image directly; do not subtract unrelated
  historical medians to claim the amount saved by this stage alone.

The final Native AOT midend journey again passes all 29 outputs, source,
detached-HIR/object, bound execution and selected failure at O0/O2. Execution
counts remain exactly 3,061 / 58 / 89 at O0 and 2,566 / 58 / 88 at O2.
Seven focused address/index properties also pass, including independent global
writes interleaved with unrelated reads, genuinely observing reads and two
unknown argument roots that must not be partitioned as independent.

Separate low-perturbation sampling of the **pre-index** address-cache build
keeps DSE enabled in both full and previous-simplifier modes. Across six paired
profiles, all-function InstCombine averages 25.04 ms versus simplification
0.80 ms, and memory cleanup 11.13 ms versus 3.29 ms. The whole optimization
pipeline increment is 24.10 ms, not the sum of just these two positive
contrasts: other phases also change. Target lowering adds 4.02 ms in that
experiment. This points the next substantial work toward InstCombine's
transformation/IR processing cost, rather than maintaining the old 11,128 DSE
comparisons. These are pre-index phase measurements, not an exclusive
post-repair attribution of every remaining millisecond.

All final receipts, raw pairs, the aborted comparison and the phase-sampling
source overlay hashes are retained under
[results/o2-store-partitions](results/o2-store-partitions/summary.json).
Reproduce the final latency comparison with `native_list.py --pairs 60`,
`--baseline artifacts/native/llvm-sroa/msync`,
`--candidate artifacts/native/o2-store-partitions/msync`, the unchanged source
and final `aot-receipt.json`. No timing gate or product fallback was added.
