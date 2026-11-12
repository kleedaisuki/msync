# Ordered Number Scope fold acceptance

Run from the repository root:

```sh
dotnet run --project tests/OrderedScopeFold -c Release -p:RestoreLockedMode=true
MSYNC_JIT=off dotnet run --project tests/OrderedScopeFold -c Release --no-build
```

The fixtures are explicit format-6 modules and never need source or HIR.
Both portable and bound images are encoded and read through the actual
artifact reader. Each route executes with telemetry disabled, aggregate
profiling, and canonical instruction-site observations.

Two equal multisets put `1` and `-2^54` on opposite sides of a chunk
boundary. Their expected results differ, so tree reduction or row
reordering cannot pass. An affine recurrence has a separate fused
multiply-add counterexample. Other long cases exercise a right-hand
accumulator, repeated field use, live invariant input, constants, exact
signed zero and infinity, and IEEE NaN classification. Short and empty
ranges retain ordered semantics without promoting native work.

The admitted mixed-layout fixture alternates rows with different physical
ordinals for the same field across native chunks. A portable place proves both
ordinals explicitly. The negative fixtures distinguish a failed runtime guard
from a static liveness rejection: a Cell reaches the accumulator through
another local, two factories in a mixed producer print in demand order, and an
escaped row must still be a real Scope. Aggregated per-function opcode counts
match the canonical path, including row admission.
Logical folded rows are counted separately from actual row factories.

The called fixtures receive the seed as a real argument and execute with
their own iterator, accumulator and row slots. The admitted call has a
private frame; selected effects and a live accumulator Cell still fall
back in the called frame. A direct, unvalidated Module with an
undersized declaration cannot use undeclared locals as evidence for a
fold plan.

On a supported host with JIT enabled, a long admitted batch enters more
than one native chunk; with JIT off it keeps the same aggregate semantic
counts and uses the precompiled worker. Add `--expect-native` on Linux
AArch64 when executable page publication is available. Use
`--semantic-only` to check results without requiring tier counters, or
`--emit .temp/scope-fold-fixtures`
to write the standalone `.mso` and bound `.mexe` test inputs.
