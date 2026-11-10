# Affine scan scheduling and object locality

## Workload and ownership

This pass targets repeated scans of one explicitly materialized numeric Range,
including immutable membership of numeric Scope records. The object layout
work is specified in [the spatial-layout note](object-spatial-layout.md).
Traversal through data-dependent tree pointers is not affine merely because
the language has a closed type system.

`AffineLoopOptimization` belongs to the machine-independent optimizer. It runs
after predicate simplification and before per-block memory/scalar analyses, then
can retry when those later passes actually change code and expose more provenance.
Both attempts precede literal-loop expansion so iteration structure remains available.
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

## Phase placement and optimizer cost

The power-user tail probe retains 128 independent Number reductions over a
materialized runtime-derived range. The original pipeline correctly fused all
128 iterators into one, but only after CrossBlockMemoryOptimization and
ScopeScalarization had analyzed the much larger original CFG. A separate warmed
sampled-thread-time trace attributes 40.1% of optimizer time to scalarization's
HashSet-based liveness, 18.5% to MemoryEffects, and 11.6% to affine fusion itself.
Three instrumented warm optimizer observations take 508.86, 527.05 and 537.56 ms,
allocating approximately 364.24 MB each; each triggers 44–48 Gen0, 10–12 Gen1
and 3–4 Gen2 collections. They are attribution observations, not a P99 distribution
or a Native latency claim.

The schedule proof does not require those expensive whole-CFG analyses. The
pipeline therefore attempts the same affine proof immediately after predicate
simplification, before memory/scalar analysis pays for the old iteration graph.
It retains a later opportunity only if intervening memory, scalar or tail-call
transformation actually changes instructions or local count. Existing
`Unchanged` canonicalization detects that condition; no persistent negative cache
or new invalidation scheme is introduced. The retained late proof handles facts
made visible by those transformations, while unchanged functions do not pay for
an identical second attempt. Captures, effects, schema totality and IEEE order
use the same legality rules at either point.

The frozen producer for the diagnostic trace is the predicate-lazy image
`6354688ac7167ac7c342c8145adb0c2adcb4b3d7370480283a4e57070dd74e78`.
The exact 128-scan source hash is
`60f500a2a0816babeecdc989e7ce045df4b7ec37e270964fc8e9addf23e319ec`;
the maintained compiler-tail fixture generator and phase host reproduce it.
Raw stage observations, opcode histograms and the separate trace live under
`.temp/tail-compiler/scans-profile-capture.log`, `scans128.nettrace` and
`scans-profile-summary.json`.

The first controlled unprofiled comparison keeps that producer and changes only
phase placement. Three warm optimizer observations change from
462.76/489.27/492.09 ms to 397.48/403.05/393.09 ms, and median allocation changes
from 364,239,472 to 277,756,944 bytes. This is a useful partial reduction, not a
complete repair: both arms still collect Gen2 four times per observation. An
earlier arm launched before the coordinator's quiet acknowledgement is retained
as `late-number_scans-128-warm-prequiet.json` and is not mixed into this comparison.
The controlled arms are `late-number_scans-128-warm.json` and
`early-number_scans-128-warm.json`. Three observations are calibration, not P95/P99.

The added `scope-carried-snapshot` journey stores an eager numeric Range inside
a Scope field, then scans the selected member. Its original late-only producer
and changed producer both fuse the portable scans and return exact `34/34`
outputs through source, source-deleted `.mso` and `.mexe` execution. The complete
changed journey passes nine cases through all three routes, including dependent
captures, lazy callbacks, intervening I/O, repeated rows and strict floating-point
order. The same changed Optimization image passes 112 invalidation and 87
predicate contracts. This retains the late provenance opportunity instead of
trading it away for earlier analysis.
