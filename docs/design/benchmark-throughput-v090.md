# Concurrent benchmark journeys and detailed SQLite export

## Latest measured runtime (2026-11-15)

Latest frozen image: `artifacts/native/runtime-shared-plans/msync`, SHA-256
`42e527631aa0fb67df4eb78097d95c7adacb9566ce8b987e2b92ac634952c9c1`.
This includes synchronous execution, compiler-owned storage plans, segmented
managed byte/root frames, handshake-free submission, inline storage ownership,
compact argument descriptors and shared call plans. It does **not** implement
the subsequently discussed unmanaged guest heap/stack.

The final Native AOT comparison uses twelve alternating pairs pinned to CPU 0,
one warmup per image/workload, independently compiled/bound artifacts and exact
stdout checks. Timing covers CLI `run-ir`, excluding compile/bind. Calls use
`-O0`, B+ uses `-O2`. This is serial latency, **not** a new concurrent throughput
sweep. November 13 acceptance and Batch 4C figures are historical snapshots.

| Workload | Original median | Latest median | Median paired change | Faster pairs |
| --- | ---: | ---: | ---: | ---: |
| calls-100000 | 897.11 ms | 694.29 ms | -20.63% | 12/12 |
| B+ inventory | 2117.08 ms | 1844.33 ms | -13.10% | 11/12 |

Original: `artifacts/native/evaluator-before/msync`, SHA-256
`49f47587ad5bf9f58c5e98c16498d9ab0507176d039265428a159cfe79538f6b`.
Receipt: `artifacts/benchmarks/synchronous-evaluator/final-original-pinned.json`.
Changes are medians of per-pair ratios, not ratios of aggregate medians. They
measure the combined implementation, not handshake removal in isolation.
Against the recent `runtime-direct-step` image, the last storage/loading batch
approximately preserves throughput: paired -1.94% calls and -0.25% B+, seven
faster pairs each (`shared-plans-pinned.json`).

Reproduce with retained frozen images and workload payloads:

```sh
taskset -c 0 python3 tests/CallLowering/benchmark_evaluator.py --candidate artifacts/native/runtime-shared-plans/msync --baseline artifacts/native/evaluator-before/msync --repetitions 12
```

Final-image acceptance covers stream demand/error ordering, 24 filesystem Future
journeys, four source/bound O0/O2 descendant-acquire routes and Force-mode
recursive live strings (output 6000). The [execution handoff](native-execution-lowering-v090.md)
records mechanisms and receipt locations. This documentation update does not
claim a new benchmark run. Payloads remain under ignored `artifacts/` and are
not shipped in the source commit.

## Measurement contract

The opt-in `--group throughput` diagnostic measures a population of
independent native CLI processes, not parallel execution inside one VM and not
an open-loop server. It was formerly in the default suite; it is not a product
score. Its default concurrency sweep is 1/4/12/24 workers. The six default
groups remain serial.
Default throughput workloads are numeric records, the bounded sensor pipeline,
and manifest-driven local copies; select other existing corpus cases explicitly.

A Linux systemd user scope applies `MemoryMax=24 GiB`, `MemorySwapMax=0` and
`CPUQuota=1200%` to the measurement controller, preparation, workers, and recorder.
The process and descendants inherit at most twelve CPUs from the available
CPU affinity. The actual scope limits, CPU set, lifetime memory peak, OOM events
and CPU accounting are retained in JSON and the SQLite `run.metadata_json`.
Resource enforcement requires Linux cgroup v2 and an available systemd user
manager; an unsupported host fails rather than claiming a nonexistent limit.
`--cpu` still means one pinned CPU if explicitly supplied at the suite entry.
Concurrency is not CPU count: twenty-four processes can compete for twelve CPUs.

Each worker has its own input/output tree. Portable objects can be copied, but
**bound artifacts capture local Path projections**. Each bound worker therefore
binds the portable object inside its actual working directory, before warmup
and measurement. This is essential for copy workloads: relocating a template
`.mexe` would send output back to the template while the worker oracle checked
another directory. Per-worker bind cost and artifact hashes are retained.

Worker threads are started before the measurement window and released together.
Each worker issues another request after its previous reset/run/oracle cycle.
A repetition completes a fixed request population, including final drain;
requested count is raised to concurrency when necessary. There is no arrival
queue, scheduling-delay estimate, or inference about latency under external
load. The window starts at worker release and ends at the last completed oracle.

| Quantity | Boundary |
| --- | --- |
| Successful requests/s | Verified successful requests / whole concurrent window |
| Invocation latency | Launch, native CLI work, output capture and process exit; excludes reset and oracle |
| Reset / verification time | Separate per-request monotonic intervals; included in throughput window |
| P50 / P95 / P99 | Linear interpolation of successful invocation samples within one repetition; raw samples retained |
| Child CPU | Process-tree child user/system CPU deltas for the repetition, not a sum of inclusive telemetry spans |
| Memory peak | Whole bounded scope lifetime, including fixture preparation and separate recording; not a per-request allocation metric |

Failures remain samples, contribute to failure rate and elapsed window, and do
not count as successful throughput. The current batch drains and persists all
its results before stopping the run. Startup/preparation/warmup failures retain
the active combination and concrete invocation details in `result.json`.
Small populations do not establish a stable tail or saturation curve.

## Storage and analysis

`result.json` indexes configuration, frozen-image payload hashes, fixture
manifests, preparation, per-repetition summaries and separate recordings.
`benchmark.sqlite` is a self-contained analysis store:

- `run`: status and complete metadata JSON.
- `batches` / `throughput`: image/workload/route/seed/concurrency/repetition,
  request and failure counts, elapsed/child CPU, throughput and percentiles.
- `samples`: request and worker identity, relative start/end, invocation/reset/
  verification costs, command, working directory and structured concrete errors.
- `profiles`: recording ownership, original database path and all original
  session fields (trace, process, timestamps, completion and exporter error).
- `records`, `attributes`, `metrics`, `error_causes`: lossless schema-v1 content,
  with an added `profile_id`. `spans` and `events` are convenience views.

A single controller owns the benchmark database. It writes each completed
batch transactionally, outside the measured window; WAL supports analysis readers.
Telemetry profiles run separately and serially after all timed routes for one
fixture. Each profile has its own product database, avoiding competing product
writers in a shared SQLite database. `ATTACH` and `INSERT ... SELECT` copy the
original tables within SQLite instead of building millions of Python objects.
Composite `(profile_id,id)` identities preserve original record IDs and causal
relationships. Unsigned metric text and integer projections are both retained.
Incomplete recordings are imported with their actual status and then fail the
benchmark run; a successful guest with a missing recorder is not a complete
profile. Original recordings remain usable by existing schema-v1 readers.

This storage design follows SQLite's [single-writer isolation contract](https://www.sqlite.org/isolation.html)
and [WAL behavior](https://www.sqlite.org/wal.html). Separating validated useful
work from instrumentation follows the application-oriented approach in
[Renaissance](https://renaissance.dev/resources/docs/renaissance-suite.pdf),
without treating its JVM workloads as evidence about msync performance.

```sql
SELECT image,workload,route,concurrency,repetition,
       successful_requests_per_second,p50_ms,p95_ms,p99_ms,failure_rate
FROM throughput ORDER BY workload,route,image,concurrency,repetition;

SELECT b.workload,b.concurrency,s.request,s.worker,s.command_json,s.error_json
FROM samples s JOIN batches b ON b.id=s.batch_id WHERE s.status='failed';

SELECT p.workload,p.route,r.name,COUNT(*) AS observations,SUM(r.duration_ns) AS inclusive_ns
FROM profiles p JOIN spans r ON r.profile_id=p.id
WHERE r.name IN ('compile','analyze','lower','optimize','execute')
GROUP BY p.id,r.name;

SELECT p.workload,p.route,r.name,m.key,m.value,m.value_int
FROM profiles p JOIN records r ON r.profile_id=p.id
JOIN metrics m ON m.profile_id=r.profile_id AND m.record_id=r.id
WHERE r.name='vm.function';
```

Do not join imported records by `id` alone, add nested inclusive spans, subtract
route medians to invent compilation cost, or pool concurrency/seed/repetition
windows into one tail claim. Baseline and current are separate frozen-image
observations, not fabricated paired speedups.

## Focused acceptance, 2026-11-13

Used the existing published Native AOT `04465c4` image, SHA-256
`009a035ff4c623f12ba05b0547f404c2c10cb84a7f5a5ca14e202f20990f53a3`,
not the concurrently edited compiler/runtime in the worktree. Smoke scale,
seed 17, concurrency 1/12/24, all three routes, 24 requests per repetition:

- Numeric-record stream: all 216 timed requests passed. That run deliberately
  remains marked **failed**: its first post-timing export exposed a read-only
  SQLite URI connection bug. The completed timings were retained, not rerun or
  relabeled as a completed export. `uri=True` fixes the actual connection issue.
- Manifest copy after correcting binding-directory ownership: one warmup per
  worker, all 216 timed requests passed across nine batches. Three separate
  instruction-level profiles completed, with 3,935 records. The merged four
  detail tables and session fields match originals bidirectionally, field for
  field. Scope lifetime memory peak: 537,059,328 bytes; no OOM events.
- Real native compilation of invalid source: two failed requests remained
  queryable with zero successful throughput, failure rate 1, command/cwd,
  exit code, stdout and stderr. Guest failure is not reclassified as success.
- Receipt checker verifies sample identity/count, isolated directories,
  timing-window bounds, throughput and percentile arithmetic, foreign keys,
  database integrity and lossless detail export. Focused entry-point contracts
  also pass. There is no timing gate or full-product regression run.

These are small functional acceptance windows, not production throughput
estimates or optimization speedup claims. Durable databases and receipts are
under `artifacts/benchmarks/throughput-v090/`; numeric partial completion and
failure acceptance are labeled separately. Original profile paths in the merged
database describe their capture location; merged detail does not depend on
those files remaining available.

```sh
python3 tests/Performance/run.py --binary PATH_TO_NATIVE_MSYNC \
  --group throughput --workload local-manifest-copy --route all --scale smoke \
  --samples 1 --warmups 1 --requests 24 \
  --concurrency 1 --concurrency 12 --concurrency 24 --profile instructions \
  --work .temp/benchmarks/NEW_DIRECTORY
python3 tests/Performance/Corpus/check_throughput.py \
  .temp/benchmarks/NEW_DIRECTORY/throughput
```

The checker compares imported content with original recording files while the
capture directory is retained. `--partial` explicitly permits checking completed
batches from a failed run without changing its status.
