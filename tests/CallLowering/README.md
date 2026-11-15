# Guest calls and Future ownership

`dotnet run --project tests/CallLowering -c Release` checks compiled argument
borrowing/snapshots, independent escaping locations, frame reuse, tail handoff,
20,000 ordinary guest calls, and delayed input through function return. Forced
native execution must distinguish operand readiness from type invalidation;
an unrelated pending input must not divert ready scalar stores to host helpers.
A queued process returned inside a scope must be cancellable before its input
arrives, without launching the host process.

The real-process scheduler runner includes these cases:

```
python3 tests/Msync.EndToEnd/scheduling_journey.py --binary PATH --executable \
  --case deferred-process-argument --case returned-process-submission \
  --case selected --case after-region --case acquire-break
python3 tests/CallLowering/submission_failures.py --binary PATH
```

They cover producer gates, process arguments derived from pending stdout,
function-return ownership, argument mutation after submission, selected/global
acquire, directional ordering and source-attributed launch errors. Test processes
and scripts are fixtures, not runtime dependencies.

`benchmark_evaluator.py` records alternating paired native-image timings with
exact stdout checks. Its frozen image paths and workload definitions are explicit
in the script; receipts live under `artifacts/benchmarks/synchronous-evaluator`.
The current architecture work is not considered complete by these checks alone;
see the execution-lowering design note for outstanding ownership/performance work.

The current benchmark driver accepts `--candidate`, `--baseline`, `--call-count`
and `--repetitions`; it records source/binary hashes and both wall and child CPU
time. The default call workload has 100,000 iterations and three guest calls per
iteration. Compare paired receipts, not medians from different runs. Compiled
work-scope tests also cover pure recursion, transitive I/O ownership, the leaf
contract and round-tripping the bounded DirectCall handler through `.mexe`.

Private frame data now shares per-thread nonmoving stack segments. The deep-call
journey checks segment crossings and telemetry; repeated 3,000-frame string-root
recursion checks roots survive both crossings and segment reuse in Off/Force.
These supplement the escaping-location and tail-argument lifetime journeys.

The scheduler runner's callback-acquire case exercises a lazy transform running
inside a transfer without joining its own Future. returned-callback-owner checks
that a returned job can publish a released process through a mutable capture and
that outer acquire includes the promoted process. Transfer workers own their
callback work scopes and cancellation, rather than inheriting a dead activation.

The benchmark optionally accepts --reference for a third frozen image. With
three images, six rounds rotate all execution orders. Reports include CPU
affinity, including affinity inherited from an external taskset invocation.
callback-inherited-acquire also checks that a worker's isolated scope preserves
the earlier released-process frontier rather than simply ignoring caller work.

Direct-entry journeys also exercise caller-owned Number/Bool/union result slots,
delayed String/Number returns, and conversion diagnostics with profiling both on
and off. Deep calls can mix direct native-stack admission and explicit guest
records; the test checks completed transitions, balanced explicit returns,
segmented storage and workspace reuse rather than forcing one calling policy.

descendant_frontier.py gates a producer callback on stdin until two transfers
have been admitted. The first callback publishes a released process; the second
callback's acquire must wait for that process even though it was absent from
the original frontier snapshot. A deliberately discarded second input tests
that handled cancellation is not re-observed through the descendant graph.
Run with --binary PATH; --trace enables diagnostic ownership decisions.
