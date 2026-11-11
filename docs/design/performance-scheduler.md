# Scheduler admission: bounded demand classes

## Workload and ownership

This follow-up starts at `c5696a8` (0.3.1). The target is admission overhead
for many independent ordinary copies under a small finite-copy limit, not a new
file transport or a claim about bulk disk/network throughput. The existing
indexed planner and native I/O fast paths are unchanged.

The previous `Scheduler::launch_ready` rotated every queued ready node after
each completion, including nodes that could not fit an already saturated finite
lane. With N homogeneous ready copies and a fixed small job count this repeats
roughly N + (N - 1) + ... admission visits. Multi-member co-start groups also
recomputed their stream count and buffer sum for each visited member.

The scheduler now owns one immutable `Demand { copies, streams, bytes }` per
execution cohort and a single unresolved-dependency count for that cohort.
Only fully dependency-ready cohorts enter admission. Ready queues are indexed by
distinct demand, with FIFO order within a class and enqueue sequence numbers
across classes. Admission selects the oldest fitting class head, removes empty
classes, and reserves every member before joining any completion.

This is a normal-form change: ordinary copies are singleton cohorts rather than
a separate execution algorithm. It removes duplicate member readiness/started
state and per-attempt cohort cloning and recounting. A saturated finite lane
cannot prevent a stream-only cohort from starting; a large buffered cohort
cannot prevent a later fitting cohort from using otherwise idle capacity.

With K distinct ready demand classes, a selection scans K class heads, not every
queued member. Homogeneous local copies have K = 1; arbitrary public plans can
have many different cohort sizes, so there is no universal linear-time claim.
FIFO applies to fully dependency-ready cohorts. The previous implementation's
rotation of partially-ready member placeholders is not retained as a scheduling
contract; dependency edges, indivisible co-start, resource bounds and report
ordering remain unchanged.

## Engineering and research context

[Tokio JoinSet](https://docs.rs/tokio/latest/tokio/task/struct.JoinSet.html)
already provides completion-order joining and ownership of spawned tasks. The
change retains it instead of adding a custom executor or polling worker pool.
Finite filesystem work still crosses the existing blocking-worker boundary;
stream I/O remains asynchronous, and cancellation drains owned native work.

[Dominant Resource Fairness](https://www.usenix.org/conference/nsdi11/dominant-resource-fairness-fair-allocation-multiple-resource-types)
(NSDI 2011) motivates representing admission as a vector rather than treating
worker slots as the only resource. Its multi-tenant fairness algorithm is not
adopted: msync executes one finite dependency graph, does not predict durations,
and has no tenant shares to allocate. The useful local conclusion is narrower:
check copy slots, stream slots and shared payload buffers together, preserving
atomic admission for complementary channel endpoints.

## Correctness gates

`src/scheduler.rs` contains a differential property test comparing demand-class
selection with a simple oldest-fitting FIFO over generated enqueue/capacity
sequences. It checks empty-class retirement and final draining as well as the
returned group IDs. A deterministic mixed-demand test covers an oversized cohort
followed by smaller finite and stream-only work. A shared-predecessor regression
checks a cohort with three incoming edges from two external predecessors: one
completion releases two edges but cannot enqueue the cohort early; the final
edge enqueues it exactly once.

Existing real-file, localhost TCP, FIFO pipeline, cancellation and memory-budget
journeys remain the integration guard, rather than treating the queue model as
proof of correct transfer behavior. The private `runtime::inspection` extraction
is behavior-preserving: metadata/cache lifetime and transfer execution are now
separate code owners, while the crate entry points and filesystem checks remain
unchanged. It has no independent speedup claim.

## Release measurements (2026-10-30)

Native Linux x86-64 guest, kernel `6.12.113+deb13-amd64`, Rust 1.99.1,
QEMU Virtual CPU version 2.5+ (16 logical CPUs visible), repository and fixtures
on ext4 `/dev/vda4`. Both executables use the normal Cargo release profile and
the same lockfile. Telemetry is disabled. No concurrent build/profiling was run
during the final samples; guest/host scheduling noise remains possible.

The saved baseline `.temp/scheduler-perf/msync-before` was built before any
scheduler edits. The metadata-only module extraction overlapped its dependency
build, so this is a **pre-admission-change worktree executable**, not a claim of
a byte-exact executable built from untouched `c5696a8`. The measured change is
the scheduler admission implementation; metadata behavior is unchanged. SHA-256:

- Before: `f993f01754178b7fd1b17a1fbfe3247cf0c891e723ac854aafbe8452a859e196`
- After: `915440aaf88e8e2f7fd22486c7777d9addbd8bd75f9321a4540875a675fe3104`

`benches/scheduler.rs` runs actual CLI `run` processes with `--jobs 4`, one
shared ordinary source and N distinct exact destinations. It includes process
startup, parsing, live planning, filesystem traversal, staged overwrites, action
formatting, stdout/stderr capture and exit. Fixture creation is outside timing.
Before **each** invocation the source receives a new fixed-width generation token;
after timing, the harness checks the operation/file/byte summary and every output
against that generation. Thus a successful no-op cannot pass by reusing outputs
from a prior sample. Warmups establish existing targets; these are warm overwrite
results, not first-write or cold-cache results. Each executable has two warmups,
followed by serial alternating AB/BA samples, with no affinity pinning.

All times below are milliseconds; ranges are observed min–max, not confidence
intervals. The first final run used seven samples per executable:

| Copies | Before median | After median | Before range | After range |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 5.234 | 5.539 | 4.596–7.274 | 4.386–7.185 |
| 100 | 23.396 | 24.087 | 20.629–29.559 | 20.504–26.963 |
| 1,000 | 225.368 | 203.997 | 190.641–248.400 | 186.614–230.950 |
| 10,000 | 4,353.247 | 2,198.687 | 4,052.832–4,818.498 | 2,079.472–2,344.924 |

A separate 31-sample small-control run checks whether the small increases above
persist rather than treating one noisy median as decisive:

| Copies | Before median | After median | Before range | After range |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 4.529 | 4.497 | 3.909–6.051 | 3.758–6.337 |
| 100 | 20.902 | 20.983 | 17.304–30.836 | 18.269–26.534 |

The useful gain is the 10,000-copy journey: about 2.15 seconds / 49.5% lower
median end-to-end latency on this host. At 1,000 copies the median is 9.5% lower
with overlapping sample ranges. The small controls do not establish a meaningful
speedup or regression. These results do not predict large-file throughput,
remote-client latency or million-node graphs.

There is a state-size trade-off: one cached three-word demand and a second word
per ready entry replace repeated computation. For singleton cohorts on a 64-bit
build this adds approximately 31 bytes of logical element payload per cohort
(after removing the old `started` boolean), about 0.31 MB at 10,000 cohorts,
plus container allocation/capacity overhead. This is a layout estimate, not a
measured process-RSS bound. It is accepted for the demonstrated reduction in
admission work, not advertised as a memory optimization.

### Reproduce

Preserve the old release executable **before** changing admission code. Do not
copy an already modified executable and call it a baseline.

```sh
cargo build --release --locked
mkdir -p .temp/scheduler-perf
cp target/release/msync .temp/scheduler-perf/msync-before
# Apply the admission change, then rebuild the current executable.
cargo build --release --locked
cargo bench --locked --bench scheduler -- \
  .temp/scheduler-perf/msync-before target/release/msync 7
MSYNC_SCHEDULER_SIZES=1,100 cargo bench --locked --bench scheduler -- \
  .temp/scheduler-perf/msync-before target/release/msync 31
```

Disposable final logs are `.temp/scheduler-perf/final-first.csv` and
`.temp/scheduler-perf/small-controls.csv`. Earlier exploratory timings used a
constant payload verifier; they are not the accepted results above because
unchanged existing outputs could have hidden an accidental no-op. The durable
tables retain the strengthened measurements even if `.temp` is cleaned.

## C# completed-prefix retirement (0.8, 2026-11-09)

The current product is the Native AOT C# runtime, not the Rust scheduler
described above. A real `copy; acquire; fs.exists(output-directory)` sequence
exposed a different historical-state cost: successful tasks left empty path
nodes in `ResourceIndex.Children`. Looking up a directory therefore revisited
every previously completed descendant. The earlier ordinary-independent-copy
counter only established cheap same-depth leaf queries, not cheap directory
observations after many successive frontiers.

An initial managed structural discriminator copied actual files and checked every
64-byte output. At fixed path depth, 100/1,000 independent copies visited
3,684/36,984 nodes, while the copy/acquire/directory-observation sequence visited
9,634/546,484. Those setup-specific counts are respectively `37N - 16` and
`N²/2 + 46.5N - 16`. Their difference exposes history traversal, not extra
observable dependencies. They are not Native latency measurements.

### Frozen Native comparison

The accepted component comparison uses exact product snapshots and an identical
probe, without compiling or binding in the execution interval:

| Input | Before | Candidate |
| --- | --- | --- |
| Product source | `ec37545` | `c0b21ab` |
| Measurement source | `98fffa2` | `98fffa2` |
| Native executable SHA256 | `227eda9f08fb2e53e18dc465eccacd318ff2a54f3353839b81a00589e01765d5` | `b52a6158baddd330be10a4777fbb4f5440370f2a59ab9121647a2107de1be12e` |

Both were published from separate repository-root `.temp` source archives using
the pinned SDK, Release, linux-x64, project-owned Native AOT, locked RID package
resolution, and the corresponding `SourceRevisionId`. Only the runtime
retirement change differs in the product code. Source-archive hashes and actual
embedded runtime/core build identities are saved beside the reports.

The harness alternates AB/BA process batches for three rounds. Each process has
one warmup and three accepted samples, giving nine observations per image/case.
Every sample writes a random fixed-size generation and checks every output
outside timing. Fixtures are ordinary warm-overwrite local files; no page-cache
eviction or artificial transport is used. Constructor cost, execution cost,
global allocated bytes and public resource statistics are recorded separately.

The raw module is already executable and uses the ordinary interpreter,
dependency index, copy slots, staged publication, metadata and fsync behavior.
The `gated_literal` control launches the same native component executable as a
real child; its stdout is unavailable until the host observes all admissions
and writes a gate. `gated_shared` first publishes a shared source through that
child, then admits Path readers which depend on it. Its original source is
deliberately stale. These cases distinguish admission from later I/O completion
and detect a removed RAW edge without replacing actual work with a Task mock.

All values below are milliseconds, p50 / descriptive p95:

| Prepared execution workload | Before | Candidate |
| --- | ---: | ---: |
| 100 independent copies | 47.347 / 48.632 | 49.408 / 55.734 |
| 1,000 independent copies | 468.299 / 522.342 | 484.725 / 534.490 |
| 100 copy/acquire/directory observations | 144.673 / 192.605 | 140.314 / 160.744 |
| 1,000 copy/acquire/directory observations | 1,630.907 / 1,905.470 | 1,440.278 / 1,477.722 |
| 1,000 same-file dependency-chain copies | 1,261.727 / 1,363.509 | 1,278.506 / 1,469.326 |
| 1,000 gated String transfers | 491.570 / 599.383 | 491.094 / 588.010 |
| 1,000 gated shared-source Path transfers | 499.442 / 717.611 | 525.315 / 555.074 |

The directory-observation workload improves in every alternating round; at
1,000 operations its median drops by 190.629 ms / 11.7%. Its resource visits
fall from 551,482 to 24,000. Execution allocations rise from 12,443,968 to
16,100,296 bytes because serial frontiers repeatedly retire and recreate common
prefix nodes. This 3.66 MB churn is an explicit cost, not a memory-optimization
claim. A node pool is not introduced merely to recover it: late completion
leases, comparers and lifetime ownership would become more complicated.

The ordinary and dependency-chain controls do not establish a general copy
speedup. Their median drain costs vary with real filesystem completion. Their
admission medians stay approximately unchanged: independent 52.667 to 51.359 ms,
chain 50.440 to 50.090 ms. Ordinary independent allocations remain approximately
9.32 MB, and resource visits remain 40,982. Gated shared-source admission drops
from 49.165 to 43.559 ms even though its execution median is higher; the latter
must not be reported as a scheduler speedup. All byte-generation oracles pass,
and the chain/shared controls preserve exactly 999/1,000 dependency edges.

The adoption is narrow: retire completed frontier state so native directory
observations do not pay for historical descendants. It is not evidence that
fsync, kernel I/O, SSH, or every task launch became faster. An auxiliary
`strace -f -c` run confirms exactly 100 fsyncs and 100 renames for 100 copies;
its setup/verification and tracing distortion exclude it from latency results.

### Reproduction and scope

```sh
python3 tests/Performance/scheduler_components.py \
  --before .temp/scheduler-performance/before-native/msync-scheduling-components \
  --after .temp/scheduler-performance/after-native/msync-scheduling-components \
  --sizes 100,1000 --cases independent,observe_directory \
  --rounds 3 --samples 3 --warmups 1 \
  --output .temp/scheduler-performance/final-core.json
```

The companion controls use `--sizes 1,1000 --cases
chain,gated_literal,gated_shared`. Reports, all raw observations and build/source
manifests are under `.temp/scheduler-performance/`. These are same-host Linux
component measurements. Whole-CLI source/object/executable journeys are a
separate question (`tests/Performance/scheduler.py --stage ...`); native Windows
behavior is accepted by the delivery owner, not inferred from this Linux run.
No benchmark threshold is a CI quality gate.

## C# selected native operands and sparse runtime frontiers (0.8.6)

The runtime now consumes the native signature's per-operand Path access
rather than assigning the operation's strongest effect to every Path.
`fs.copy_file` and `fs.copy_symlink` read their source and write their
destination; `fs.copy` does not treat its String payload as a source path.
The source can share an outstanding read, but not a write; the destination
still conflicts with both reads and writes. Ordering fences, namespace
publication and existing physical path projection remain in place.

A real gated child differentiates these cases. Its stdout publication blocks
another transfer from a common source to the same held destination. An
independent native copy from that source now publishes before gate release
in all three source/object/executable routes. Before the change the frozen
Native CLI failed this discriminator. Reading the held destination or
writing it remains blocked in all three routes. The gate is a causal
control, not a timing threshold; every expected byte is checked after
release. The executed bound fixtures have no contextual plan nodes.

The ResourceIndex retains the same prefix overlap and task-completion
semantics. A trie node represents zero or one child directly and promotes
to a dictionary at a real branch. The path scanner uses span keys and
allocates segment text only on insertion. An access set represents zero or
one task inline and promotes only for genuinely shared active tasks. Both
representations demote on removal. Faulted and canceled tasks remain visible;
retiring an old registration cannot erase a newly allocated same-name node.
Prepared batches union dependencies directly into their existing target set.
If historic pending bookkeeping remains but the index has no registrations,
there is no external frontier to query. Serialized predecessors and the
ordering fence are independent of this decision.

This is an application of adaptive trie representation, not the byte-level
layout of [Leis et al.'s ART](https://db.in.tum.de/~leis/papers/ART.pdf).
The high-fan-out representation uses the .NET 10
[span alternate dictionary lookup](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2.getalternatelookup?view=net-10.0);
it preserves ordinal local/remote matching and Windows ordinal-ignore-case
matching for local paths.

Linux x86-64 Native AOT, CPU 9, AB/BA, 18 samples per image, exact footprint
vectors from the accepted contextual I/O graph receipts. This component
replays one held task per transfer; timing includes dependency queries,
registration and retirement, not decoding, filesystem I/O or full batch
submission. All dependency counts match, including 4,262 overlapping task
instances in the directory trace.

| Exact graph | Baseline → candidate median | Allocated bytes per replay |
| --- | ---: | ---: |
| One ordinary copy | 8.29 → 2.27 μs | 7,752 → 1,856 |
| 1,024 independent paths | 7.86 → 3.76 ms | 3,386,856 → 566,288 |
| 1,024 selected paths | 8.20 → 2.96 ms | 3,354,072 → 566,280 |
| Directory, 1,092 nodes | 11.41 → 3.93 ms | 4,257,984 → 769,384 |

The real-transfer controls separately check schedule behavior, aliases, mixed
observed/discarded failures and cancellation. Raw observations, fixture hashes
and the component source are in
`artifacts/benchmarks/library-matrix-v086/scheduler-deep/`.

In the separate Native component that copies real bytes after all admissions,
the final isolated scheduler build uses 5.75 → 4.01 MB for 1,024 gated
literal writes and 9.79 → 7.02 MB for 1,024 gated shared-source copies.
Their median admission times are 36.57 → 29.98 ms and 60.88 → 51.17 ms;
whole execution is 956.98 → 957.46 ms and 792.42 → 668.80 ms respectively.
Each arm has nine checked observations across three alternating batches.
Small and tail timings are mixed; an earlier intermediate-build run also
had slower literal admissions despite lower allocation. These receipts do
not establish a stable P95 improvement or a storage-throughput claim.

### Residual native observations scan unrelated namespace producers (2026-11-11)

The adaptive resource trie does not cover the separate namespace-publication
lookup. [`NamespaceDependencies`](../../src/Msync.Runtime/Execution/Interpreter.Plans.cs#L259-L283)
projects every ancestor of the selected local operand, resolving each parent,
then enumerates the whole task-to-roots map. The test for completion is inside
that enumeration; completed entries retained until their owner joins are
still visited. The map records even ordinary residual Path copies because
their concrete node kind is not known at this admission boundary.

A [process-gated CLI probe](../../tests/Performance/namespace_publication.py)
isolates this path without replacing transfers with mock tasks. A child
publishes stdout to `held.txt` only after a host gate opens. The script admits
P Path copies from `held.txt` to distinct destinations and then executes 4,000
verified `fs.exists(input.txt)` calls. The selected input is unrelated to all
publishers. The measured interval is between two flushed stdout markers in
the same process, after copy admission and before opening the gate. The host
checks that publication has not happened in the interval, then releases the
child and verifies every destination byte. P=0 still holds the child's String
transfer, retaining ordinary native dependency checks but not the Path
namespace-producer scan. All input executables contain zero contextual plan
nodes: parsing, binding, artifact decoding, worker slots, copy and fsync are
outside this interval.

Linux x86-64 Native AOT 0.8.6 release candidate, package run `43021811925`,
binary SHA-256 `278024641a9f62c3dff32b669a26d791a79cd3278073ec8233ad5b60e4c9a506`,
Linux 6.12.113 on CPU 14, three forward/reverse/forward rounds. Process CPU is
the `/proc/<pid>/stat` user-plus-system delta (10 ms resolution), excluding
the waiting child. The shared host had concurrent work; the ranges below
describe this window, not a population tail or a throughput forecast.

| Held Path copies P | Nonmatching producer checks (source model) | Wall median (range), ms | Process CPU median (range), ms |
| ---: | ---: | ---: | ---: |
| 0 | 0 | 307 (284–311) | 270 (260–270) |
| 1 | 4,000 | 577 (569–582) | 490 (490–500) |
| 1,024 | 4,096,000 | 1,115 (1,070–1,129) | 950 (890–970) |
| 4,096 | 16,384,000 | 3,191 (2,991–4,022) | 2,670 (2,530–3,340) |

The check count is derived from the code, not an instrumented counter.
The 0-to-1 change also switches on namespace ancestor projection. Comparing
the nonzero frontiers keeps that projection active and increases the number
of disjoint candidates while holding the query body, selected path and
dependency set fixed. CPU grows before there are any transfer bytes or fsyncs
to charge. This isolates a scheduler admission cost from bytecode dispatch
and storage throughput; it is not a claim that a mixed 4,096-copy frontier
is typical. Separately,
[`AwaitNativeDependenciesAsync`](../../src/Msync.Runtime/Scheduling/Interpreter.Scheduling.cs#L168-L203)
creates a new physical path fact owner for every effectful native Path call
with registered work; the [C02 syscall probe](safety-cost-audit-v086.md#c02-temporary-proof-owners-repeat-path-walks)
shows repeated leaf and parent resolution even with a small frontier.

Prefer a live, exact entry-to-producers lookup queried at the selected
ancestor entries, rather than scanning tasks or descendants; attach its
registrations and retirement to the existing work owner. That change can
remove the P term independently of pathname caching. Then measure whether
scoped namespace facts or one shared path projection reduce the remaining
ancestor walks. Do not reuse an old alias identity after a publication or
an uncontrolled external effect. In particular, keep entry/referent
distinctions, link/../directory alias cases, and actual predecessor waits.
On the same Native image, the [gated dependency journey](../../tests/Msync.EndToEnd/cohort_dependencies_journey.py)
checks independent shared readers, native link/.. dependencies and merge
through a descendant directory alias in all three source/object/executable
routes (nine journeys): independent publication precedes the gate; both
dependent cases wait and preserve exact bytes. Linux
[`namei` path lookup](https://docs.kernel.org/7.1/filesystems/path-lookup.html)
likewise distinguishes cached traversal from rename-sensitive validation;
it is not evidence for an indefinitely valid user-space canonical-path
cache.

Reproduce on Linux with a matching Native image and a fresh scratch path:

```sh
taskset -c 14 python3 tests/Performance/namespace_publication.py \
  --binary /path/to/native/msync --workspace .temp/namespace-publication
```

The runner retains the source, object, executable and per-round samples in
the selected scratch directory. This run's receipt is
`.temp/vm-io-investigation/pinned/result.json`.

### Exact-entry namespace producer index

`NamespaceProducerIndex` now keeps both task-to-root ownership and exact
local entry-to-task postings. Admissions and retirement update both views
together; the REPL successor retains the same index as the live scheduler
state. Each query still resolves the current parent of each selected lexical
ancestor before combining it with the leaf. It looks up those exact entries,
filters out completed tasks and deduplicates shared producers only on a hit.
The resource trie remains responsible for prefix overlaps and unknown remote
work. No remembered canonical pathname outlives a query.

One ordinary producer is stored inline in each entry posting, reusing the
resource trie's `TaskSet` storage. Only concurrent writers to the same entry
need a set, and duplicate roots in a batch do not create duplicate task
references. The reverse view remains necessary for alias reprojection and
removal. A local or remote workload with no namespace registrations creates
neither dictionary.

On Linux x86-64, SDK 10.0.403 Native AOT, a checkout through `887eced`
plus just the namespace index was compared with the `025b1da` Native CLI.
Each arm ran the same pre-bound files in three alternating rounds on logical
CPU 14. The host gated the same 4,000-query window and checked all output bytes.

| Held Path copies | Median process CPU, before → after | Median wall, before → after |
| ---: | ---: | ---: |
| 0 | 290 → 250 ms | 347 → 299 ms |
| 1 | 580 → 530 ms | 674 → 676 ms |
| 1,024 | 1,040 → 640 ms | 1,287 → 767 ms |
| 4,096 | 3,160 → 640 ms | 4,127 → 784 ms |

Small-frontier changes are not attributed to the index; host wall time
varied. The large-frontier CPU slope disappeared in this selected disjoint
case. This measures neither file-copy throughput nor the cost of remaining
path-resolution walks in isolation. The matching receipts, per-arm binary
and executable hashes, and driver are in
`.temp/namespace-index-isolated/{paired/result.json,compare.py}`.

The nine independent reader/alias/dependency journeys above passed with
the candidate. Three additional CLI journeys gate a process-produced Path
that publishes a directory symlink; a selected native `exists` below it
waits and sees the new file through source, object and executable routes.
`tests/SchedulingGraph --namespace-only` also passed its existing retry and
namespace lifecycle cases. A randomized source-linked comparison with the
old scan checked 3,833 queries over duplicate postings, shared writers,
completion, retirement and local/remote entries.
