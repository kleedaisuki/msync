# v0.6 preparation: indexed epoch history

## Scope and baseline contract

The baseline is the complete, uncommitted v0.6 worktree frozen by the coordinating
performance run, not an older release. The second candidate's preparation
component is accepted on the declared native Linux workloads: fresh 500-input
controls improved 11.2× for independent assignments and 16.2× for repeated
writes. Exact provenance, measurements, remaining costs and trade-offs are below.
This component acceptance does not certify the integrating owner's entire final
release, runtime work still in progress, or native Windows/WSL performance.

Preparation may inspect only its admitted basic block and never reuse filesystem
observations across dispatches. `resolve_instruction` must remain fresh after
preceding effects. Remote discovery remains acquisition-owned under ordinary
block preparation; this work does not restore eager transport probes, weaken
path failures, or cache metadata globally. Existing evidence is in
[block specialization](block-specialization.md),
[planner proof obligations](planner-proof.md), and
[the accepted v0.5 observation change](performance-v5.md#accepted-optimization-acquisition-owned-remote-facts).

## Source-level bottleneck hypothesis

`src/preparation/optimize.rs` still performs repeated full-history scans in its
literal-aware typed optimizer:

- `forward` searches backwards for the latest overlapping write, then scans the
  intervening history for changes to the forwarded origin.
- `remove_redundant` searches for a reaching write and then checks intervening
  reads/writes.
- `eliminate` walks all subsequent nodes until an overlapping read or write.

For independent N-file assignments these are quadratic even though no alias
candidate exists. `PreparedProgram::verify` repeats this optimizer to replay the
certificate, so verification pays the same work. Calling the legacy indexed
planner after the typed candidate does not erase the typed scan cost.

The initial candidate is an indexed-history replacement, not weaker proofs or a
new rewrite. Sweep direction, fixed-point termination, eligible epochs, permission
checks and the order/content of rewrite records remain unchanged.

## Index contract

Reuse the existing `plan::index::ResourceIndex` rather than building another
path trie. Query its conservative candidates and recheck `Resource::overlaps`
before selecting an extremum. Preparation adds a critical conflict domain not
needed by the original physical planner index: an unresolved native/WSL alias
may overlap regardless of path prefix. Supplement candidates with opposite-domain
Local/WSL IDs; keep explicit disjoint proofs in the final overlap predicate.
Identity/projection hints remain negative alias constraints and never justify
positive path equality.

Forward and redundancy use prefix indexes; elimination uses suffix indexes.
Elimination queries the first subsequent read and write and preserves the
`read_id <= write_id` blocking rule. Removed operations are never inserted into
later queries. Rewrites never cross a non-transfer instruction or an ineligible
file/stream operation.

## Discriminating workload and acceptance plan

Use the maintained release `planner_v4` harness with identical owned real-file
fixtures, toolchain, lockfile, tracing mode and hardware. Its `independent`,
`overwritten`, `missing-overwritten` and `forward-chain` cases separate sparse
alias search, repeated writes, creation metadata and forwarding. Include small
scripts and larger sizes (100, 1000, 5000 if runtime permits). Baseline and candidate
runs must not overlap with builds or other benchmark runs. Report preparation
and verification separately; compiler timing is a control, not an attributed win.

Acceptance requires differential rewrite results and unchanged facts/graph
verification, plus targeted actual-copy journeys. Existing readonly, creation
mtime/mode, hardlink, path traversal, stream and cross-endpoint regressions remain
applicable. Linux results do not certify native Windows/WSL helper behavior.

## Prior work

LLVM [MemorySSA](https://llvm.org/docs/MemorySSA.html) explicitly motivates
indexed/cached clobber queries by the risk of quadratic dependence scans. We use
the same reaching-modification question on a linear file-assignment epoch; this
is not an import of MemorySSA CFG machinery. The production implementation's
[caching walker](https://github.com/llvm/llvm-project/blob/main/llvm/lib/Analysis/MemorySSA.cpp)
provides the relevant engineering precedent: reuse a stable analysis structure
rather than rescanning every earlier definition for every query.

MLIR's [side-effects and speculation rationale](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/)
keeps effect classification separate from permission to execute/hoist an
operation. Our index accelerates proof queries only; it cannot speculate `exists`,
program effects, remote metadata or publication.

## Candidate implementation

Baseline executable SHA-256:
`ede9b2b020a21f96cb6149866d3f9ac98e1d6189481fe2bcccea470734ec06ca`.
The coordinating owner preserved its exact source archive as well; benchmark
comparison must use that source/library rather than relinking an old executable
against current code.

The candidate replaces three scan-based optimizer passes with prefix/suffix
`History` indexes. The native/WSL supplement is used only when the query has
opposite-domain retained entries; otherwise the mature index returns an extremum
directly. Removed redundant/dead nodes are never inserted into later queries.
The creation-coalescing rule, permissions, fixed point and legacy cross-check are
unchanged.

`src/preparation/optimize/tests.rs` retains the old scans under `cfg(test)` as a
slow differential oracle. Generated tests compare both overlap extrema and full
retained-program/rewrite traces. Inputs include literal/file sources, creation
facts, unstable source premises, real hardlink identities, and native/WSL conflict
and disjoint-proof cases. Their targeted execution and the subsequent serialized
measurements are recorded below.

## Frozen baseline measurements

The parent ran `target/performance-v6/baselines/planner_v4-before`, linked against
`libmsync-v060-before.rlib`, using the unchanged maintained
`benches/planner_v4.rs`. `MSYNC_V4_PLANNER_SIZES=100,500`,
`MSYNC_V4_PLANNER_SAMPLES=3`, and an explicit absolute `MSYNC_PERF_ROOT` under
repository `.temp/performance-v6/preparation-fixtures` selected owned fixtures.
The harness warms each measured stage before three samples and profiles allocation
separately from timing. Raw output: `.temp/performance-v6/preparation-before.txt`.
Environment: Rust/Cargo 1.99.1, release profile, x86_64 Linux kernel
6.12.113+deb13-amd64 on the same host as the frozen baseline. No native Windows/WSL
execution is included.

| Case | N | Prepare median ms | Verify median ms | Peak Rust heap bytes | Retained nodes | Commutation proofs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| independent | 100 | 170.723 | 12.826 | 2,550,492 | 100 | 4,950 |
| independent | 500 | 4,154.765 | 390.168 | 22,556,884 | 500 | 124,750 |
| forward-chain | 100 | 97.416 | 14.846 | 2,530,188 | 100 | 4,950 |
| forward-chain | 500 | 2,553.153 | 474.640 | 22,228,740 | 500 | 124,750 |
| overwritten | 100 | 74.207 | 1.311 | 919,385 | 1 | 0 |
| overwritten | 500 | 1,819.469 | 23.356 | 4,677,755 | 1 | 0 |
| missing-overwritten | 100 | 80.268 | 1.222 | 938,915 | 1 | 0 |
| missing-overwritten | 500 | 1,869.574 | 24.715 | 4,775,613 | 1 | 0 |

These are baselines, not a speedup claim. Three samples establish a local scaling
probe, not tail latency. Independent/forward cases retain a quadratic-size public
commutation proof set, so not all of their cost is avoidable analysis overhead.
The repeated-overwrite cases retain one node and no commutation proofs yet still
increase preparation time approximately 23–25 times for 5 times as many inputs.
That discriminates a preprocessing/history cost from unavoidable output size.
Preparation time also substantially exceeds standalone certificate verification,
so indexed typed rewrites alone should not be assumed to remove the dominant
residual cost. Candidate comparison will determine the next step.

The academic antecedent is Novillo's
[Memory SSA — A Unified Approach for Sparsely Representing Memory Operations](https://www.airs.com/dnovillo/Papers/mem-ssa.pdf)
(2007 GCC Developers' Summit). Its practical connection here is sparse
representation of reaching modifications, not stronger alias assumptions. The
2006 [GCC integration discussion](https://gcc.gnu.org/legacy-ml/gcc-patches/2006-12/msg00436.html)
also documents the compile-time/representation trade-off. We reuse the repository's
mature path index; adopting an entire external compiler framework for these linear
epoch queries would add machinery without removing the required file-specific
permission and metadata-version checks.

## Targeted correctness evidence

The coordinating owner executed
`cargo test --locked --lib preparation::optimize::tests` against the candidate.
Both 128-case generated tests passed (1.87 seconds total execution): indexed
extrema equal the full-overlap scan and complete rewrite programs/traces equal
the preserved baseline sweep oracle. Raw gate output is
`.temp/performance-v6/unit-gates.log`.

The affected legacy preparation target initially passed 36 tests and exposed one
older selector expectation (`file/<.*> -> target/`), reproduced by the parent
against the frozen v0.6 baseline: selectors are now lazy control ranges rather
than eagerly admitted preparation operands. Its narrow expectation update is
owned by integration, not a weakening of the optimizer's rewrite proof. The new
candidate does not turn selector evaluation into a metadata optimization premise.

## First paired candidate: useful verification gain, incomplete preparation result

The first integrated candidate was frozen with SHA-256
`7fc6ff13304151ff22b1822d0b0da80a339d1a10009c6f7bf7a1998df642e220`.
The parent compared frozen baseline/candidate harnesses serially on the same
maintained fixtures, three samples per stage. Raw paired rows are
`.temp/performance-v6/preparation-paired-before.txt` and
`.temp/performance-v6/preparation-paired-optimized.txt`.

| Case, N=500 | Prepare before ms | Prepare candidate ms | Verify before ms | Verify candidate ms | Candidate allocation delta |
| --- | ---: | ---: | ---: | ---: | ---: |
| independent | 3,826.704 | 3,644.181 | 386.594 | 119.504 | +9.1% |
| forward-chain | 2,404.213 | 1,825.335 | 413.410 | 118.115 | +16.3% |
| overwritten | 1,680.707 | 1,758.355 | 40.593 | 5.200 | +7.4% |
| missing-overwritten | 1,550.898 | 1,694.295 | 26.774 | 6.245 | +6.2% |

Peak and retained Rust heap reported by this harness were unchanged for these
rows. This is an integrated candidate comparison, not isolation of only the
typed rewrite index; graph/other compiler changes are present too. Standalone
verification improved substantially, but full preparation retained seconds of
work for 500 inputs and the repeated-overwrite medians worsened. Those results
were not accepted as completing preparation optimization. Small N=100 controls
were retained in the raw data; three samples do not establish a tail distribution
or excuse the larger workload's residual.

## Second candidate: epoch-local state representation

Source investigation found two remaining history scans before rewrite/graph
construction: pending-write invalidation checks and postcondition class queries.
Every successful destination also appends directory postconditions for every
ancestor. Indexing those events without addressing repeated ancestor records
would still return many identical parent candidates.

The second candidate preserves every classification rule but changes the query
representation:

- Pending writes use the shared logical-effects resource index, followed by the
  original `Resource::may_overlap` and managed/independent checks. Repeated
  identical managed writes share one pending footprint; unmanaged effects remain
  independently represented.
- Postconditions group only identical `(Location, tree, FileClass)` events. Each
  group retains its latest sequence position, and the shared file-resource index
  points to groups. A query orders its conservative candidates by that sequence
  and applies the old exact-location, raw descendant, independence and alias
  predicates without strengthening them.
- Grouping is sound because two identical events either return the same answer
  for a query or both skip that query. Different kinds or tree flags are **not**
  merged: Regular(parent) followed by non-tree Directory(parent) still implies
  that an unproduced child is missing. Normalized Windows/case aliases must still
  reach the conservative Unknown result when raw descendant checks cannot prove
  the relation.
- Index snapshots are copy-on-write and preparation-local. `unknown` resets
  postcondition history exactly as before. No filesystem observation is skipped,
  no namespace truth is shared across blocks/dispatches, and remote probe policy
  is unchanged.

The test-only frozen `ReferenceState` retains the old scans. Generated action
sequences compare class, host stability and WSL separation sets after writes,
parent-entry/metadata effects, managed deferrals, FIFO-only byte effects, opaque
invalidation, explicit separation proofs, resets and cloned snapshots. Inputs
include Windows/case/UNC spellings even on Linux. A focused 1000-duplicate-parent
case checks both bounded groups and the preceding regular-parent missing-child
inference. The repaired gate and measurements are recorded below.

### Aggregate phase observability

Preparation now exposes `prepare.observe`, `prepare.optimize`, `prepare.graph`
and `prepare.verify`. Observation has three coarse child spans:
`prepare.observe.resolve`, `prepare.observe.types` and
`prepare.observe.resources`. Existing leaf/ancestor probe counters are emitted
once, alongside location/resource and postcondition-update/group counts. There
are no per-lookup trace records or new per-candidate counters. Disabled telemetry
retains the existing cheap span fast path.

These spans distinguish native metadata resolution, typed epoch inference,
physical alias capture, rewrite construction and certificate replay. Nested span
durations must not be summed as independent costs. The measured candidate and
phase profiles below distinguish the removed history cost from remaining work.

The first state differential run immediately found an important extra query
domain: after a missing postcondition for `/state/a`, classifying raw
`/state/a/../b` must retain that missing ancestor. The logical alias key normalizes
to `/state/b` and therefore cannot by itself select all raw traversal premises.
The candidate now conservatively considers every event group for queries with a
raw `..` component, then applies the original classification predicates. Ordinary
paths keep indexed queries. The frozen scan oracle was not weakened; the shrunk
case is retained both as a deterministic test and in
`proptest-regressions/preparation/state/tests.txt`. This is a classification-only
completeness condition; it does not change the shared graph index's own normalized
alias relation.

After the raw-traversal fix, the coordinating owner ran the repaired state gate:
all five `preparation::state` tests passed, including the generated reference
comparison, snapshot sharing, native/opaque-writer separation and both focused
postcondition regressions. The complete legacy preparation target passed 37/37;
block/runtime/telemetry-related affected gates also passed. Shared-index candidate
completeness and Arc path-sharing tests passed. Commands/results are retained in
`.temp/performance-v6/wave2-tests-repair.log`. Strict all-target Clippy and the
release build then passed (`wave2-clippy.log`, `wave2-build.log`). These are
correctness/build results; the second-candidate measurements below supply the
separate performance evidence.

## Accepted preparation component: second measured candidate

The second frozen component executable is
`target/performance-v6/baselines/msync-v060-optimized-2`, SHA-256
`015645dc073986de9743c082ade38c6361cbd385ce754a8fed261065358a6b97`.
Its frozen library is `libmsync-v060-optimized-2.rlib`, SHA-256
`da8504f53bbccf6e030193cac42f6df8d2a9a25f70af3b086284692c4dc9b7ab`;
`planner_v4-optimized-2` is
`3bfbd7571908c1c0e637148d22c0e2e152aa8d63d19622336ada5d3068075888`.
The parent owns these artifacts and the serialized measurements. No benchmark or
Cargo invocation was started independently by the preparation workstream.

Setup remained the maintained release `planner_v4` harness, owned real files,
N=100/500, one untimed warmup per measured stage and three timed samples. Rust
heap profiling runs separately. Host/toolchain/filesystem remained the QEMU
x86_64 Linux host and ext4 fixture storage documented above. The fresh baseline
controls were rerun in this second-candidate measurement window, not borrowed
from the earlier discovery pass. Raw rows:
`.temp/performance-v6/preparation-wave2-before.txt` and
`.temp/performance-v6/preparation-wave2.txt`.

| Fresh same-window control, N=500 | Baseline prepare median ms | Candidate prepare median ms | Ratio | Baseline verify median ms | Candidate verify median ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| independent | 3,697.343 | 330.495 | 11.2× | 379.863 | 126.272 |
| overwritten | 1,691.392 | 104.515 | 16.2× | 24.471 | 5.229 |

Both retain identical semantic output counts: independent has 500 retained nodes,
zero dependencies, 124,750 public commutation proofs and no rewrites; overwritten
has one retained node, zero pair proofs and 499 rewrites. The large improvement
in the one-node case rejects the explanation that preparation was merely paying
an unavoidable public-proof output cost. The representation change removed
repeated history and duplicate-parent work without removing filesystem admission
premises.

The full second-candidate matrix, including small controls and discovery-only
cases, is:

| Case | N | Prepare median ms | Verify median ms | Peak Rust heap bytes | Retained Rust heap bytes | Allocations |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| independent | 100 | 32.427 | 7.443 | 2,304,677 | 977,816 | 73,228 |
| independent | 500 | 330.495 | 126.272 | 21,323,837 | 8,293,568 | 377,887 |
| forward-chain | 100 | 32.208 | 8.125 | 2,283,147 | 960,613 | 72,146 |
| forward-chain | 500 | 282.318 | 139.025 | 20,991,203 | 8,096,701 | 369,864 |
| overwritten | 100 | 13.022 | 1.313 | 836,541 | 356,609 | 36,820 |
| overwritten | 500 | 104.515 | 5.229 | 4,255,711 | 1,853,900 | 176,929 |
| missing-overwritten | 100 | 13.525 | 0.885 | 854,503 | 357,777 | 36,084 |
| missing-overwritten | 500 | 75.826 | 6.108 | 4,345,601 | 1,860,572 | 173,350 |

The earlier missing-overwritten baseline was 1,550.898 ms versus 75.826 ms here
(about 20×), and forward-chain was 2,404.213 versus 282.318 ms (about 8.5×).
Those are useful discovery comparisons, **not fresh same-window controls**; the
headline accepted ratios use only the two newly rerun baselines. Three samples
are not a tail-latency claim. The integrated candidate also contains private Arc
sharing of normalized logical paths and prior graph/compiler changes; these
numbers establish subsystem benefit, not isolation of one line of code.

### Memory and allocation trade-offs

Against the fresh N=500 controls, independent peak heap fell 5.5% and retained
heap 8.7%, while allocation count was approximately flat (+0.5%). Overwritten
peak heap fell 9.0%, retained heap was approximately unchanged (-0.02%), and
allocations fell 6.3%. The forward-chain discovery comparison still allocates
about 9.0% more often than its earlier baseline, despite lower peak/retained heap
and much lower latency. Index construction is not free; this is an explicit
allocation-frequency trade-off, not a universal allocation reduction. Complete
pair proofs still dominate retained state for independent large plans.

### Phase profile: what was learned

The parent also ran one warmed traced diagnostic per case with 500 assignments.
The profile fixture writes `s0..s500`; independent seeds `d0..d500`, overwritten
seeds one `out`. It compiles 500 source-to-target statements with a fresh Session
and the fixture as logical cwd, prepares once without tracing, enables JSONL
telemetry, then calls `prepare` once under a `prepare.profile` span and shuts the
sink down. It does **not** execute transfers. This flat diagnostic fixture and
single traced observation are separate from the maintained harness's medians;
their elapsed times must not be substituted for those medians or used as a
trace-on/off comparison.

| Profile phase | independent ms | overwritten ms |
| --- | ---: | ---: |
| observe (parent) | 97.108 | 26.735 |
| ↳ resolve native observations | 14.905 | 5.867 |
| ↳ infer typed postconditions | 37.822 | 7.977 |
| ↳ capture physical resources | 42.391 | 12.721 |
| optimize | 74.625 | 20.637 |
| graph | 14.586 | 0.222 |
| verify | 99.845 | 4.474 |
| complete preparation profile | 288.248 | 52.696 |

The observation children are included in their parent; they are not additive
extra work. Profile provenance is
`.temp/performance-v6/profile-wave2-independent.jsonl` and
`profile-wave2-overwritten.jsonl`.

The aggregate counters make the mechanism concrete:

| Counter, N=500 profile | independent | overwritten |
| --- | ---: | ---: |
| postcondition updates | 4,000 | 4,000 |
| retained postcondition groups | 507 | 8 |
| unique managed pending footprints | 500 | 1 |
| observed native leaf probes | 1,001 | 502 |
| ancestor probes / cached hits | 7 / 1,000 | 7 / 501 |
| captured physical resources | 1,000 | 501 |

The independent plan retains 500 destination events plus seven shared ancestors;
the repeated-write plan retains one destination event plus those ancestors.
This demonstrates bounded representation of repeated parent postconditions while
native leaf/ancestor observations and physical proof capture remain visible.
The profile now attributes costs to actual observation, inference, physical alias
capture, optimization and verification rather than hiding seconds under a
single preparation label.

The remaining independent verification cost includes checking the required
124,750 public pair proofs and replaying rewrite/context certificates; it is not
all an avoidable history scan, nor is every millisecond asserted unavoidable.
At this accepted point, preparation no longer has the unexplained multi-second
500-input residual. Further changes are deliberately frozen for integration.
Acceptance here covers native Linux preparation and its affected correctness
journeys, not cross-platform transport speed, disk throughput, or the integrating
owner's still-changing whole runtime release.
