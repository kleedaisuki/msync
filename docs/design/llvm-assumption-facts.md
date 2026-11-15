# Scalar LLVM assumption facts

The optimizer consumes the real `LlvmAssumptionCache`, rather than accepting an
analysis parameter and continuing with the null-cache algorithms. This slice
uses LLVM commit `a55e64432a516fd6507d3ecfba7407cfe425f940`; inspected source is in
`vendor/llvm-reference/llvm/`. It does not add runtime checks, a heap solver,
LLVM backend ownership or a new language feature.

## Query transport

KnownBits, sign-bit/significant-bit, masked-zero, nonzero, non-equality,
power-of-two, integer range/overflow, poison/undef, speculation and local load
queries carry trailing optional `assumptions` and `allowEphemerals` arguments.
Recursive queries retain the actual cache and use the source incoming-edge
context when traversing PHIs. `BitsQuery` is a value carrier, not a global cache
or thread-local hidden state. Old callers keep the null-cache behavior. Null AC
never creates an assumption cache, scans a function or allocates recording state.

InstructionSimplify's query carrier and KnownFPClass transport are coordinated
with their owners. Source sites that intentionally create a fresh query with
only DataLayout or omit a context retain that behavior; AC is not mechanically
injected into source-independent subqueries.

## Source mechanisms

| Source | Represented implementation |
| --- | --- |
| `ValueTracking.cpp::computeKnownBitsFromContext` | Actual affected-value cache entries; align/offset and range bundles; direct/not boolean conditions; truncated boolean conditions including raw NUW; ICmp/bit-mask/shift/range patterns; source conflict reset |
| `computeConstantRange` final AC loop | Direct range-bundle intersections and ICmp regions; comparison RHS recursively uses context=assumption, depth+1 and the same source query options |
| `isKnownNonZeroFromAssume` | Bundle nonnull implications, truncated conditions and commuted ICmp with the value or its ptrtoint; actual context validity |
| `isKnownNonEqualFromContext` | Actual assumed comparison conditions, using the shared source implication matcher and source recursion depth |
| `isKnownToBeAPowerOfTwo` | The represented ctpop==1 / ctpop u<2 assumption patterns, in addition to recursive cache transport |
| `BundleAttributes.cpp` | Constant optional alignment/offset/count facts, range extraction, and source nonnull implications for align/dereferenceable/nonnull/range bundles |
| `AssumeBundleQueries.cpp::getKnowledgeFromBundle/getKnowledgeForValue` | The retained alignment/dereferenceable subset used by local load queries; source alignment/offset defaults and MinAlign combination |
| `Loads.cpp::isDereferenceableAndAlignedPointerViaAssumption` | Alignment and byte facts accumulated across actual valid assumptions; the source free-between condition, not guessed storage summaries |
| `ValueTracking.cpp::willNotFreeBetween/hasNoFreeInRange` | Source bounded instruction scan and predecessor walk, including context-block backedges; NoFree call descriptors and represented atomic load/store ordering |
| `isGuaranteedNotToBeUndefOrPoison` | Actual retained NoUndef knowledge after context/DT/reachability checks, and assume's actual noundef condition argument in direct-UB-use queries |
| `computeOverflowForSignedAdd` | Its context-only result-sign refinement now uses actual AC facts rather than the former unavailable-cache branch |

The retained helper is a source subset for these requested attributes, not a
claim that the complete retained-knowledge map library or attribute system has
been ported. There are no guessed facts for unrelated bundle tags. Fence scope
and atomic RMW/CmpXchg ordering state are not represented by the current core;
those source synchronization cases remain explicitly unsupported rather than
being interpreted as non-synchronizing.

## Context and ephemeral values

`IsValidAssumeForContext` uses source instruction order, the fifteen-instruction
execution-transfer scan, ephemeral users and DT or predecessor/entry fallback.
KnownBits/nonzero/FP context queries use the SimplifyQuery ephemeral policy.
The NoUndef retained-knowledge path and Loads retained alignment/dereferenceable
filter call the source context/DT overload with its default **false** ephemeral
policy; a query's broader flag does not widen those source checks. A noninteger
value skips the UndefOnly dominator-condition walk, not the final NoUndef
knowledge lookup.

The local no-free scan retains the source `NumChecked++ > 32` cutoff and starts
with an empty visited set so loop edges back to the context block are checked.
This is a source query for assumption-derived facts, not an added global
allocation/lifetime analysis. Project HeapAlloc live results retain the already
established non-freeable object contract; no new heap facts are inferred.

## Focused use and observability

`AssumptionFactProperties.Run()` creates actual declared assume instructions,
operand bundles and a real cache. It exercises comparison/range facts,
recursive integer queries, ephemeral contexts, nonzero/non-equality, alignment
with offset, retained load byte/alignment facts, pointer and integer NoUndef,
ctpop power-of-two facts, result-sign overflow refinement and a call without
NoFree between an assumption and load. Pass owners separately exercise the
real SimplifyCFG/LoopRotation journeys that supply these query inputs.

Caller-owned counters record visited assumption entries, bundle/expression
matches, conflict resets, retained fact outcomes, ephemeral-user work, no-free
scan/block work, cutoffs and concrete call/synchronization causes. No disabled
telemetry path creates a dictionary, scope or event string. Root serializes
builds and execution; this component does not start a competing MSBuild run.

## Injected condition context

`LlvmCondContext` represents `SimplifyQuery.h::CondContext` at the pinned
revision: a condition, its `Invert` flag, and the caller-populated identity set
`AffectedValues`. `BinaryQuery.WithCondContext` and `WithoutCondContext` copy the
query, changing only this field. The context is owned by the caller's local
simplification attempt; it is not an assumption inserted into the function,
a persistent cache, an alias identity or a runtime check.

KnownBits consumes an injected condition **before** the context-instruction
null check, exactly as `computeKnownBitsFromContext`. Only affected values use
`computeKnownBitsFromCond`'s existing logical/ICmp/truncation transfer. FP-class
queries likewise call the existing condition-class transfer before their null
context return. `Invert` selects the false condition rather than rewriting the
source graph. Metrics `known_bits.injected_condition.applied` and
`known_fp.injected_condition.applied` record actual consumption; disabled
recording creates no state.

Ordinary operand recursion retains the same condition context, including
integer/FP bitcasts and the existing sign-bit, nonzero, power-of-two, range and
overflow queries that call KnownBits. Public InstructionSimplify entry points
forward it through the actual query carrier rather than accepting an unused
parameter. Existing explicit AC/CtxI/DT poison/undef APIs retain their source
signatures: they do not gain an invented condition interpretation.

PHI incoming and recurrence queries intentionally clear the injected context
when evaluating incoming-edge values, matching the source
`getWithoutCondContext()` sites in ValueTracking.cpp. A fact established for a
PHI's result at the query point must not be projected backward onto its incoming
operands. This applies to KnownBits, nonzero, non-equality, power-of-two,
sign-bit and FP-class recursion. The original injected context still applies
to the queried PHI itself when it is an affected value.

Focused properties are staged in `.temp/llvm-condcontext/tests/CondContextChecks.cs`.
They cover no-CtxI injection, the affected-value filter, polarity, expression
recursion, actual SimplifyICmp consumption, independent null-context queries,
PHI boundary clearing and KnownBits/FP-class transport. Execution is pending
root's serialized integration and the real InstCombine Select consumer journey.
