# Interpreter block frontier: event-driven readiness

## Scope

The active structured interpreter uses `execution::blocks::BlockSchedule`, not
the older `src/scheduler.rs` finite-plan scheduler. The demand-class optimization
documented in [performance-scheduler.md](performance-scheduler.md) therefore does
not remove the active interpreter's repeated whole-block readiness scans.

A useful workload is N independent ordinary copies with four finite worker slots.
The old frontier scans every instruction and its incoming edges whenever a worker
completes, then allocates a vector containing every remaining ready instruction;
the interpreter uses only the prefix that fits available capacity. Even an
edgeless block consequently repeats work proportional to N squared. Separate
`all_admitted` and `is_finished` scans add similar repeated work. This is scheduler
overhead, not a prediction about payload throughput or filesystem durability cost.

## Edge-indexed model

The frontier keeps graph identity separate from state: stable source positions determine ready
order; an instruction-ID index maps external handles to those positions. Each
edge owns a satisfaction bit. Each node owns its unresolved-edge count, state,
incoming and outgoing edge indices. An ordered ready frontier contains exactly
pending nodes with zero unresolved edges. Admission, completion and captured-input
notifications touch only the corresponding outgoing edges. State counters answer
whole-block completion/admission queries without scanning all nodes.

Three events remain distinct:

- Admission satisfies admission/start edges, not completion edges.
- Completion satisfies all remaining outgoing edges.
- Immutable input capture upgrades only edges whose complete reason set consists
  of captured file-read hazards; mixed read/write edges stay completion-gated.

Speculative group retry must restore edge satisfaction and readiness, not merely
change `Running` back to `Pending`. Co-admission ignores prerequisites inside the
cohort but still requires every outside prerequisite. A speculative contraction
must not release an external admission-gated consumer before its guard is known
safe. Duplicate incoming edges are counted individually.

An edge index alone is insufficient: retaining `ready() -> Vec<InstructionId>` in
the live hot loop still copies an arbitrarily large ready tail after every worker
completion. The interpreter needs ordered, incremental candidate traversal while
retaining reservation priority and allowing blocked rendezvous candidates to be
skipped. A bounded prefix without a continuation cursor can starve later eligible
work when its first members are blocked.

## Validation and measurements

Use the original scan implementation as an independent differential model.
Generated DAGs should contain sparse/nonmonotonic instruction IDs, duplicate edges,
both admission and completion gates, and legal admission/completion/capture/retry
sequences. Compare ordered ready membership, group eligibility, rejected
transitions, running count and final completion state after every event.

Focused examples retain captured mixed hazards, physical channel co-admission,
contraction retries, and malformed cycles. A frontier-only benchmark should
separate graph construction from admission/completion work and cover independent
nodes, chains and shared-predecessor fanout. A deterministic four-worker completion
simulation is enough to expose the repeated scan; it is not an executor benchmark.

The existing `benches/scheduler.rs` paired real-file CLI harness remains the
end-to-end gate. It runs actual copy programs with four jobs, changes the source
generation before every invocation and verifies every destination outside timing.
Run alternating before/after samples on 1, 100, 1,000 and 10,000 copies; retain
small-control measurements rather than accepting a regression for common scripts.
Preserve the baseline executable before production edits and serialize builds and
measurements. Do not transfer a frontier microbenchmark speedup claim directly to
whole-program latency.

## Prior art

The unresolved-predecessor frontier is the established topological-scheduling
construction associated with [Kahn, *Topological sorting of large networks*
(1962)](https://doi.org/10.1145/368996.369025). The adaptation here is not a new
sorting algorithm: execution has admission and completion events, capture can
upgrade a gate, and safe retry can revoke a previously satisfied gate. Explicit
per-edge satisfaction extends the familiar invariant to these non-monotonic
runtime events without requiring whole-graph rediscovery.

[Ninja's Plan implementation](https://github.com/ninja-build/ninja/blob/master/src/build.cc)
separates a ready work queue from dependency completion and resource-pool
admission. The transferable production lesson is to schedule newly eligible work
on graph events rather than rediscover it by scanning unrelated nodes. We retain
Tokio's existing task ownership and completion mechanism instead of introducing
a second executor or a work-stealing dependency.

## Frontier-only harness

`scripts/bench-block-frontier.sh` compiles the actual before/after frontier source
into one optimized standalone executable. Minimal HIR record stubs keep this
probe independent of unrelated compiler and filesystem work; the scheduling
implementation itself is included unchanged. The harness uses sparse instruction
IDs, four simulated worker slots, independent/chain/fanout graphs, two warmups per
implementation and alternating AB/BA samples. Construction and frontier handling
are timed separately; completion count and identity checksum must match.

The preserved pre-index source is exactly
`478cd9a476b50c1b5d9b19b61ed41f78f4248e0a:src/execution/blocks.rs`, SHA-256
`bf9f67d3114f15a940a8083e5113588b566b4dfdac2a3918cc0656a1fb1dc81c`.
It can be recovered after disposable files are removed:

```sh
mkdir -p .temp/block-frontier-perf
git show 478cd9a476b50c1b5d9b19b61ed41f78f4248e0a:src/execution/blocks.rs \
  > .temp/block-frontier-perf/blocks-before.rs
scripts/bench-block-frontier.sh .temp/block-frontier-perf/blocks-before.rs \
  100,1000,10000 7
```

This probe measures candidate traversal and state transitions, not filesystem
throughput, cancellation behavior, channel liveness or execution semantics. Those
remain responsibilities of the differential tests and actual CLI journeys.

## Accepted measurement

The actual-source before/after frontier probe completed in the exclusive runtime
window with seven alternating pairs and unchanged probe/source hashes. At 10,000
nodes, independent/chain/fanout median frontier administration fell from
6,663.798/12,957.403/10,793.410 ms to 1.981/2.064/2.917 ms. Initialization is
separate. This is not physical file-copy throughput; the 13 frontier model and
regression tests passed independently of these timings.

The scheduler source did not change in subsequent async-frame repairs, so this
probe was not unnecessarily rerun. Full results, limitations and accepted-image
context are in [runtime performance](performance-runtime-v6.md); the nine
size/shape summaries are retained in `runtime-frontier-v6.csv`.
