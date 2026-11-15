# LLVM scalar cohort source-correspondence review

Scope: the default compiler's ADCE, LoopRotation, modern MemorySSA DSE, and the
source/Native boundaries they exercise. Reference LLVM commit:
`a55e64432a516fd6507d3ecfba7407cfe425f940`. This is a bounded independent review,
not a claim that the complete LLVM pipeline or target is implemented.

No production changes or MSBuild runs were made by this reviewer. Findings below
come from tracing the current source and the pinned upstream implementation.
Focused execution belongs to the root's serialized integration work.

### Integration disposition

Both P1 findings below describe the reviewed pre-fix snapshot. Root corrected
the producer boundaries, preserving the upstream algorithms. The integrated
public compiler/Native2 ADCE journey now passes four source executions and four
detached artifact executions, including exact `profile-name.txt = "written"`
output. Four independent public-session cases (O0/O2, `if`/`match`) retain the
original pending conversion failure and source span. The executed receipt is
`artifacts/diagnostics/llvm-scalar-cohort/adce.log`; detailed traces, source and
output files are in its adjacent `adce/adce/` directory. These fixes do not imply
that the complete LLVM pipeline or target has been ported.

## P1: profiler-runtime symbol recognition can erase a user function's I/O

Resolved at the producer; focused real execution passed as recorded above.

`LlvmAdce.Liveness.cs::AlwaysLive` preserves upstream ADCE's name-based exception
for constant value-profile instrumentation. A call to
`__llvm_profile_instrument_target` with a constant first argument is not a live
root even if it has side effects. Upstream source:
[ADCE.cpp::isInstrumentsConstant](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/ADCE.cpp).

`LlvmHirLowerer.cs::Reserve` currently protects only the `llvm.` prefix when
assigning a user function's IR symbol. Thus a noncapturing msync function with
the profiling-runtime name receives the exact recognized symbol. Its explicit
constant argument is also the first LLVM argument; no hidden captured-location
argument prevents the exception.

Concrete source shape:

```msync
fn __llvm_profile_instrument_target(n: Number) {
    $std::io::write_text(./profile-output.txt, "written");
    n
};
__llvm_profile_instrument_target(1);
7
```

The unused call can disappear at O2, losing the file write. Keep ADCE's upstream
rule unchanged and isolate ordinary source symbols from LLVM-reserved runtime
symbols at the producer, preserving the original source/debug/Native name. This
is the same representation-boundary responsibility as existing `llvm.` mangling,
not a new ADCE heuristic or a ban on source identifiers.

## P1: discarded match still loses its pending-Bool demand

Resolved by shared producer demand insertion; focused execution passed above.

The Bool-demand correction in `LlvmHirLowerer.Scalar.cs::Conditional` covers
`if` and short-circuit boolean expressions. `LlvmHirLowerer.Operations.cs::Match`
still emits `LlvmCondBrInstruction(accepted, ...)` directly after `MatchPattern`.
For a literal Bool pattern, `MatchPattern` creates an ordinary runtime equality
call. That call produces a pending Bool rather than consuming one.

Reuse the existing `AdceJourney.PendingBool` gate-held invalid conversion and
change its second-submission function to:

```msync
fn discarded(flag: Bool) {
    match flag { true => { 1; }, _ => { 2; } };
    7
};
discarded(pendingBool)
```

ADCE can retain the equality call while erasing the dead branch. Native
`PendingBinary`/`Combine` creates a pure future (`NativeInterpreter.Operators.cs`),
and `NativeWorkScope.FinishBatchAsync` intentionally does not join pure futures
without a submission or transfer. Consequently retaining the equality call does
not retain the source branch's wait/error propagation. The O0 branch consumes
the pending conversion while optimized code can return 7.

Use one producer helper for the Bool-demand-plus-branch boundary and apply it to
all source control consumers that can receive a pending datum. Iterator Next and
static-initialization predicates already have distinct runtime operations which
return a ready Bool; do not invent an analysis or runtime gate for those paths.
The existing ordinary runtime-demand call and Native branch fusion suffice.

## Checked boundaries without additional findings

- The root's correction of both LoopRotation `MergeBlockIntoPredecessor` callers
  now matches upstream's DTU-only invocation; the earlier DT+DTU double update
  must not be restored by an older staging overlay.
- DSE's represented ordinary-store paths retain upstream walk/scan/partial
  budgets, postorder traversal, overwrite interval construction, visibility
  queries, delayed deletion, and source phase order in the inspected paths.
  The apparently function-wide 5000-definition collection limit matches this
  exact upstream implementation despite the option's per-block wording.
- Other raw function-name checks found in the new optimization directory are
  the generic TTI external-library cost heuristic. Source functions have local
  linkage, so the source local-linkage rule returns before that name list.
  This does not add library effects or purity. TLI remains independently disabled
  for C-library symbols.
- Runtime operations and ordinary builtin bindings dispatch by declaration
  identity retained in lowering maps, not by interpreting a source spelling of
  `msync.operation.*`. Normal symbol-table uniquing avoids an identity collision.
- No Range-specific opcode, deferred element evaluator, capture mode, or heap
  admission proof was introduced by these three passes in the inspected paths.

These checks are source review evidence, not a substitute for the root's actual
O0/O2, Native2, pending-value, and Native AOT journeys.
