# Affine scan scheduling and object locality

## Workload and ownership

This pass targets repeated scans of one explicitly materialized numeric Range,
including immutable membership of numeric Scope records. The object layout
work is specified in [the spatial-layout note](object-spatial-layout.md).
Traversal through data-dependent tree pointers is not affine merely because
the language has a closed type system.

`AffineLoopOptimization` belongs to the machine-independent optimizer. It runs
before literal-loop expansion so the iteration structure remains available.
The VM executes the resulting ordinary bytecode; neither a VM optimizer nor a
new source-language collection type is introduced.

## Domain, access and schedule

For two complete scans of the same snapshot, the iteration domain is

```text
D0 = { S0(i) : 0 <= i < N }
D1 = { S1(i) : 0 <= i < N }
```

The snapshot ordinal is an internal integer, not a reassociation of arbitrary
binary64 source index arithmetic. Primitive accesses have map `A[i]`; a record
read has map `A[i].field`. The original schedule is `(statement, i)`. Scan fusion
selects `(i, statement)`, visiting one record for each body before advancing.
Each body's own arithmetic instruction sequence and each reduction's element
order remain unchanged. Floating addition is not associative and is never
reassociated or replaced by a parallel reduction.

Private scalar states may carry dependence from `S0(i)` to `S0(i+1)` and from
`S1(i)` to `S1(i+1)`. These remain lexicographically forward in the fused
schedule. Cross-body scalar reads/writes would introduce a dependence that
fusion can reverse, so those bodies are not fused. Read-only row accesses do
not conflict even when two snapshot positions reference the same Scope.
Iteration-private locations are alpha-renamed when needed; a compacted physical
slot reused by the second original loop is not a shared logical accumulator.

## Provenance and legality

The input must have an eager snapshot origin, not merely type `Range`. Native
snapshot/materialization operations establish membership after their producers
execute; the analysis itself never enumerates a runtime producer. Deferred
factories are inspected only as typed bytecode shape summaries. A constructor
must prove the requested field exists and is numeric; `Range<Scope>` alone does
not prove a field read nontrapping. Field mutation and unknown callback effects
invalidate those row facts.

The initial body class consists of local loads/stores, constant operands,
numeric arithmetic and proven numeric row reads. It excludes external work,
unknown native calls, member mutation, capture writes, early returns and
noncanonical control flow. Current item values are staged independently of
body-local slot reuse. An empty snapshot executes no body and retains the
original initial accumulator values.

## Engineering and research connections

[GCC Graphite](https://gcc.gnu.org/onlinedocs/gcc/Optimize-Options.html)
uses ISL and Pluto-style scheduling for general affine loop nests.
[Polly's architecture](https://polly.llvm.org/docs/Architecture.html)
separates detection, domain/access/dependence representation, scheduling and
code generation. This pass follows that separation rather than renaming finite
unrolling as polyhedral optimization.

For this one-dimensional uniform-access schedule, exact dependence conditions
reduce to range identity, field totality, private-state independence and
forward per-body recurrences. A general Presburger solver does not add a legal
schedule to this class. Bundling ISL/GMP in both Native AOT platforms would add
startup, packaging and ownership costs without benefit for this transformation.
The model is explicit so a later multidimensional region can justify a solver
when it actually needs nontrivial integer-set projection or scheduling.

Legality and profitability remain separate. The current transformation reduces
iterator dispatch and repeated record fetches without growing the arithmetic
body or changing the reference-heavy data representation. Functions with fewer
than two iteration headers do not request this analysis. Acceptance must check
the changed iteration structure as well as actual source/object/executable
results, strict IEEE controls, selected errors and producer effects.

## Integrated source and artifact evidence

The first integrated positive probe exposed two missing provenance bridges, not
a reason to weaken fusion assertions: `cast(..., TypeValue(Number))` had retained
the registry's generic return, and an iota wrapper's deferred Range had lost its
checked Number element type. The analysis now carries the explicit cast target
and the specialized function's Range element contract without executing that
producer. Numeric Scope rows still require constructor/member proof; the broad
Scope kind cannot supply it. Invalidating schemas also reaches retained range
items and closure captures, so materializing a previously bound range cannot
restore pre-mutation field facts.

Coherent managed producer `spatial-common-3` (Optimization SHA256 prefix
`ff23fd8ec22`) passes the actual O2 pipeline for runtime-derived numeric scans
and readonly numeric Scope-field scans through source, source-deleted `.mso`
and contextual `.mexe`: each portable program has one `IteratorNext` instead of
two and the exact results remain `2464/116704` and `40/44`. The changed-boundary
IEEE and shared-row controls pass six routes, retaining `0/2` and `14/16`.
The preceding same-contract negative controls pass fifteen routes for captured
recurrences, intervening I/O, live callbacks and irregular traversal. These
records are under `.temp/spatial-validation/common3-affine-positive.json`,
`common3-affine-new-controls.json` and `common2-affine-controls.json`.

The isolated new-pass oracle additionally checks 46 runtime/provenance cases,
including empty ranges, physical slot reuse and repeated Scope identity. It is
not a replacement for the integrated default-path evidence above. This section
establishes transformation and semantics, not a Native speedup measurement.
