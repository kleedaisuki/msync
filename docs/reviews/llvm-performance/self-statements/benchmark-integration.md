# Benchmark integration: personal implementation account

## Ownership and assignment

I joined after the LLVM IR translation, SROA port, machine scalar carriers,
Function descriptor splitting and default-pipeline wiring already existed.
I did not choose or author those architectures. My initial assignment was to
prepare the existing application corpus comparison without building, timing,
changing the harness or touching production code. A later bounded assignment
was to inspect storage/liveness and nullable environment transport while another
agent investigated the closure failure. Root subsequently assigned me the PHI
copy fix after identifying the actual execution path.

## What I actually did

- Inspected `BENCHMARK.md`, `tests/Performance/Corpus/README.md`, the existing
  runner and three fixture implementations. Confirmed source execution defaults
  to O2 in `CommandOptions.cs`; artifact preparation explicitly passes O2.
- Appended the application comparison section to `docs/design/llvm-sroa-port.md`.
  Selected changing closure dispatch, materialized numeric records and Unicode
  path processing; described their independent Python oracles and measurement
  boundaries. I did not launch a benchmark or obtain performance numbers.
- Inspected shared instruction schemas, runtime liveness, storage copying,
  preparation and backend code. The new Function operations do expose their
  actual operands to shared liveness. I found no missing operand declaration.
- Implemented the root-directed PHI transport fix in
  `LlvmNativeLowerer.Control.cs`, `LlvmNativeLowerer.AggregatePhis.cs`, and
  `LlvmNativeLowerer.cs`. The shared `PhiCopy` helper compares the actual source
  and destination slot `PhysicalType`, retains typed raw copying for equal
  carriers, and otherwise emits the existing untyped, kind-adapting Copy.
  Both scalar and aggregate PHI copies use it. Added the actual
  `phi.carrier_adaptations` metric. I did not introduce another runtime operation,
  optimizer, fallback or null check.

## Rationale and concrete assumptions

The benchmark choice was intended to observe distinct applications rather than
count optimization firings. Closure dispatch exercises shared captured locations
and stored Function objects; numeric records combine local scalar state with
materialized object references. Unicode paths are an unrelated control, not a
claim of zero optimization impact. Keeping source and bound timings separate
avoids mistaking execution improvements for compiler improvements. The proposed
three small-scale pairs are an initial development comparison, not a claim about
production distributions or precise tails.

The copy fix addresses a representation boundary, not language semantics or
alias eligibility. A typed Native Copy means a common physical encoding. An
incoming monomorphic Address and a nullable Address do not have that encoding:
the nullable carrier includes a kind tag and payload offset. Generic Copy
already performs the required tag/root adaptation. Comparing actual slot types
rather than aggregate shape metadata matters because a leaf can retain an
existing HeapAlloc slot's narrower carrier. This keeps the normal equal-carrier
path unchanged and uses existing machinery for the genuinely different encoding.
The assumption is the established Native Copy contract, not a new safety proof.

## My incorrect conclusion and its correction

I initially reported that the runtime backend resets Copy operand types and
reinstates typed copying only for equal carriers, based on
`NativeExecutionFunction.Lower`. That code exists, but I had not traced which
backend emitted the current source pipeline's image. My conclusion was too broad.
Root identified the actual path, `NativeStandardBackendEmitter`, which exports
`instruction.Type` directly. Thus the target's invalid typed PHI Copy contract
was observable and was not repaired by the code I had inspected. I corrected the
report and applied the target fix. This was a call-path ownership mistake in my
investigation, not evidence that extra runtime validation was needed.

## Results and remaining limits

Root reports that the existing managed midend journey now passes O0/O2 source,
detached HIR/object and detached bound routes plus its failure cases, recorded in
`artifacts/diagnostics/llvm-sroa-source/cli-receipt.json`. I did not personally
execute that build or journey. When I delivered the copy fix, I had not yet
received the subsequent AOT comparison results. Root later ran the comparison
and committed and pushed the results in `baaa0310`; I did not run it myself.
The versioned receipt is
[`benchmark-tier-standard.json`](../results/benchmark-tier-standard.json).
Those results do not support an overall speedup. My earlier present-tense
statement that the project had no new AOT benchmark result was stale and wrong;
the absence was in my handoff knowledge, not in the project's delivered work.

I now judge my most important shortcoming to be inspecting a plausible backend
implementation before establishing the active emission path. The proposed
benchmark sample is also deliberately limited: its three workloads cannot
establish universal compiler/runtime performance. No new measurement harness,
blanket regression or CI gate is warranted by those limits.
