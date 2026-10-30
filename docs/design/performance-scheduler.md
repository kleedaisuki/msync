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
