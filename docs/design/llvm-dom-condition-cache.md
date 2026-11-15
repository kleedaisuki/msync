# LLVM dominating-condition cache

## Source and ownership

`LlvmDomConditionCache` translates `llvm/Analysis/DomConditionCache.h` and
`lib/Analysis/DomConditionCache.cpp` at LLVM commit
[`a55e64432a516fd6507d3ecfba7407cfe425f940`](https://github.com/llvm/llvm-project/tree/a55e64432a516fd6507d3ecfba7407cfe425f940).
The cache is a map from affected values to conditional branches. Registration
uses the existing source `findValuesAffectedByCondition` translation, and each
value's branch list is deduplicated. The cache neither scans an entire function
implicitly nor performs automatic invalidation: as upstream, the transforming
client registers branches, re-registers modified conditions and removes erased
values. It owns no runtime state or source-language resource lifetime.

The InstCombine driver's source `SimplifyQuery` carrier (`BinaryQuery`) retains
both the real condition cache (`DC`) and `TargetLibraryInfo` (`TLI`). TLI remains
the explicitly disabled C-library configuration of the existing Native target;
no user function name acquires C-library behavior. Source query copies preserve
these objects, rather than recreating a cache-less query in each transform.

## Actual consumers

The translated ValueTracking paths consume the cache together with a context
instruction and the existing dominator tree:

- `computeKnownBitsFromContext` tests both successor edges. True/false outcome
  facts are combined with operator facts; a contradiction resets the result
  before assumption-cache processing, in the same source order.
- `isKnownToBeAPowerOfTwo` recognizes the source `ctpop(V)` comparisons on either
  dominating outcome, including the `OrZero` alternative.
- `isKnownNonEqualFromContext` visits conditions affecting either operand,
  applies true/false edge dominance, and calls the actual implication algorithm
  with the source context-free subquery and recursion depth.
- `computeKnownFPClassFromContext` processes true and false dominating outcomes
  before assumption-cache entries, using the existing floating condition/class
  transfer functions.

The existing APIs split a query into explicit arguments. Their optional trailing
`conditions` argument is threaded through recursive known-bits, integer facts,
floating classes and the supporting ValueTracking calls. Existing callers
retain their signatures' behavior when the argument is omitted. Instruction
simplification forwards `BinaryQuery.DC` to those consumers, so the field is not
an unused annotation and facts survive recursive operand analysis.

The independent `isImpliedByDomCondition` helper remains the upstream
single-predecessor algorithm; it is not replaced by a new global search. The
source exponent-range consumer depends on represented `fabs`/`frexp` intrinsics;
this tranche does not invent those intrinsic identities or approximate them with
ordinary user calls. The represented scalar consumers above are wired directly.

## Observation and focused execution

Optional counters distinguish branch registration, affected values, duplicate
entries, lookup hit/miss and value removal. Consumer counters identify tested
conditions and the particular dominating outcome, including conflict reset.
Actual analysis/pass spans retain function/source ownership; disabled counters
do not construct diagnostic keys or a second trace sink.

`DomConditionCacheProperties` builds real SSA/CFG graphs covering registration,
deduplication/removal, a nested true edge, false-edge non-equality, recursive
arithmetic facts, instruction-simplifier use of the same query, both outcomes of
`ctpop` power-of-two guards, ordered floating classes and parallel CFG edges
that must not supply an outcome fact. Root owns the coordinated build and
execution; these properties supplement full source-language pass journeys.
