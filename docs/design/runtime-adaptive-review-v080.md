# Independent review of adaptive runtime experiments

## Scope and result

On November 9, 2026 the runtime research and prototype implementations were
reviewed by different owners. This note records mechanism corrections and
independent semantic evidence, not a production implementation or speedup claim.
The [experimental protocol](runtime-adaptive-validation-v080.md) owns causal
performance comparisons. The [initial research](managed-runtime-optimization-v080.md)
owns the candidate priorities.

Separate primary-source reviews are retained for
[HotSpot](runtime-hotspot-review-v080.md) and
[CoreCLR](runtime-coreclr-review-v080.md). Both support small guarded resolution
changes rather than importing a JIT/OSR/GC subsystem into Native AOT.

## Mechanism corrections that changed an experiment

HotSpot interpreter quickening includes link/address predecoding; it is not
necessarily a response to execution heat. Its compiled inline-cache state
machine is distinct from an interpreter PIC. Some resolved field operations
still retain per-execution runtime work, so fusion cannot simply discard a
Cell read, cancellation point or selected error.

CoreCLR's tagged GDV/PGO design documents retain historical proposal language.
The independent review cross-checked actual .NET 10 JIT transformation source:
guard construction preserves effectful argument evaluation, and an observed
target histogram is not an exhaustive-candidate proof. Native AOT's MIBC inputs
are publish-time host optimization, not CLR tiering of msync bytecode.

A local implementation fact directly changed P2. `Optimizer.FormBlocks` treats
`GetMember` as MayFail, placing it at its own block boundary. The initially
proposed same-block `LoadLocal; GetMember` pattern therefore has no eligible
ordinary O2 sites. P2 was changed to a real retained, same-block
`LoadLocal; LoadLocal; Add` sequence in a Range-parameter accumulator.
This is a pattern-selection result, not a reason to weaken block boundaries.

## Guard and state obligations

| Candidate | Required identity/state | Miss and invalidation behavior |
| --- | --- | --- |
| P0 selected target without a FunctionValue copy | The already evaluated actual callee supplies its NativeName and captures; the selected index is separate data | Native callees bypass nonnative dispatch maps. Invalid overrides/missing mappings preserve selected failures. Function entry retains normal owner and session-image refresh |
| P1 two-entry field PIC | Immutable ScopeLayout reference and field offset at one function/PC; each hit reads the actual Scope and current Cell payload | A correct existing field hint bypasses the new cache. New layouts fall back without a language shape limit; image refresh clears site tables |
| P2 numeric fusion | Same-block sequence, two live local reads in original order, current operand kinds | Three cancellation checks remain; fallback consumes already evaluated values rather than rereading Cells; the original Add instruction supplies failure attribution. Plan rebuild follows module/image identity |

Cacheable resolution is not a cacheable field value or closure environment.
Filesystem metadata, credentials and IO results are not facts stored in these
plans. No experiment requires new serialized opcodes or changing original IR.

## Independent execution evidence

`tests/Performance/AdaptiveReview` contains a separate 14-assertion harness. Its
explicit executable-stage modules prevent automatic binding from secretly
optimizing away the chosen sites. It covers actual captures, argument mutation,
native direct/table calls, a process-launch sentinel, three layouts, live field
Cells, missing-field span, IEEE bits and nonnumeric Add fallback. It also runs
selected executable artifacts through serialization.

The actual control and all three frozen P0/P1/P2 snapshots passed all 14 assertions.
P0's patch was also inspected for NativeName and actual-capture preservation.
P2's RunBlock hook was inspected for its three cancellation points, consumption
of evaluated operands, original Add span and correct next PC. P1's implementation
was inspected for layout-reference guards, live field reads, existing-hint
bypass and generation invalidation. These checks are
separate from the prototype owners' source/IR and timing matrices.

## Existing source-order fault found during the review

This discriminator deliberately changes the binding while evaluating an
argument:

```msync
let action = fn(value: Bool) { "A" };
let replacement = fn(value: Bool) { "B" };
fn change() { action = replacement; true };
let first = action(change());
let second = action(false);
$std::string::concat(first, second)
```

The independently executed control returns AB at O0, and AA at O1/O2/O3, in
relocatable, bound and executable-codec routes. The corresponding raw executable
sequence returns AB, including its codec roundtrip. Optimized IR retains an
inlined captured-cell store but returns a folded AA constant. Therefore the
original lowering/runtime evaluation order is correct and the fault belongs to
the static optimizer path; it must not be reported as a new P0 regression or
used as a favorable equivalence oracle. The language/optimizer owners received
the exact source and IR dumps for a focused fix.

## Decision boundary

The inspected prototypes have useful bounded guard structure. Semantic checks
alone do not establish profitability: P0 must beat the real retained call ABI,
P1 must beat the existing field-hint path, and P2 must amortize plan construction
and its always-on dispatch check. Actual Native AOT cold/prepared costs and
negative controls decide adoption. No production adoption or universal
equivalence claim follows from this review note.
