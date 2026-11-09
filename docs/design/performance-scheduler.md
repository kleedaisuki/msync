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
