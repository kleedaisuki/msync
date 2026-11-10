# Object spatial layout and affine schedules

## Objective and starting point

The user requests language-specific spatial layout optimization, coordinated
with traversal and scan schedules inspired by GCC Graphite. The starting source
is `8c95198245aa37a0a1b0c0496855fe7968668702`. The accepted previous Native
baseline is `7d1d2856e75631f162bdded1f7258dc7b1d71620afcd2ae28ae42939a08865f4`.
Implementation work is separate from the already completed contextual I/O
backend; it must retain its execution-only VM and selected-resource ownership.

Current Scope instances share name/offset metadata but own arrays of Value
references. This improves dictionary overhead, not payload placement: a numeric
field still normally points to a NumberValue object. The VM operand stack
already uses a lazy double sidecar for arithmetic, while reference consumers
materialize ordinary values. Existing cross-block scalar replacement can remove
some physical Scopes entirely. Those mechanisms are the baseline, not new work
to count again as spatial optimization.

## Semantic and ownership boundaries

Source programs observe object identity, stable declared fields, logical member
enumeration, fixed storage types, aliases, capture cells and returned values.
They do not observe a physical contiguous Scope representation. Physical field
order, payload partitions, grouping and internal handles may therefore change
without new source types or layout syntax. Logical field names/indices remain
independent of physical addresses and must survive artifact/REPL boundaries.

Every anonymous construction remains a fresh instance. Named Scope declarations
retain their program/session singleton identity. Snapshot membership freezes
references, not the objects behind them. A captured field/location retains its
shared Cell identity even when a numeric value needs promotion or materialization.
Null admission and union stores must not be restricted merely because the first
stored value was numeric. Internal storage fallback is not source type widening.

GC-visible references must stay in managed reference storage. An untyped arena
containing hidden pointers is not an acceptable shortcut. Grouping also needs a
lifetime model: one escaped row must not retain unrelated rows' managed payloads
or cause an allocator to retain every historical page. Reference-free numeric
pages and separately owned row references are one candidate; the implementation
choice must be measured against small mixed/reference-heavy objects.

Allocation removal, spatial locality and lifetime retention are different
outcomes. Avoid replacing repeated NumberValue allocation with a new object at
every field read: construction, field access/store and OperandStack boundaries
must be considered together. A universal fat tagged slot can penalize the
dominant Path/String case, as the earlier operand-stack prototype demonstrated.

## Affine model and legality

A useful loop model records an integer iteration domain, affine access maps,
object/resource identities, dependences and an execution schedule. It must
actually drive a transformation; finite literal expansion alone is not a
polyhedral optimizer. The backend may choose layout-aware interchange, fusion
or tiling only where the selected transformation is legal and profitable.

Number remains strict IEEE binary64. Modeling an index expression as integer
arithmetic requires exactness of every intermediate operation, not just an
in-range final result. Floating reductions cannot be reassociated, and scalar
grouping, NaN and signed-zero behavior remain unchanged. Internal Buffer sizes
and proved integral iteration bounds provide useful positive proofs.

`i * N + j` is bilinear if N is still an unknown parameter. It can become an
affine access after contextual preparation fixes N, or after a proved
multidimensional delinearization. This is a reason to use the staged language
pipeline, not to declare arbitrary indexing affine. Pointer-chasing tree walks,
effectful callbacks and unresolved I/O do not automatically become affine.

Disjoint indices do not prove disjoint Scope objects: repeated Range elements
may alias, and a named Scope may occur in many iterations. Field dependences
must use actual/proved object identities. Bounds and selected failures are also
part of legality; do not reorder a possible error past an observable mutation
without a valid proof. Opaque calls, delayed producers and directional I/O
constraints keep their existing semantics.

## Engineering and research connections

* [GCC Graphite options](https://gcc.gnu.org/onlinedocs/gcc/Optimize-Options.html)
  describe SCoP extraction and an ISL/Pluto-based scheduler. Reuse the separation
  of model, dependence legality and schedule choice; evaluate native-library
  integration cost against msync's compilation latency and platform packaging.
* [Polly's architecture](https://polly.llvm.org/docs/Architecture.html) separates
  kernel detection/modeling from transforms and code generation. Its discussion
  of pass placement highlights the trade-off between useful inlining and losing
  recognizable loop structure. Unrelated functions should not pay for the model.
* [Marmoset, ECOOP 2024](https://drops.dagstuhl.de/entities/document/10.4230/LIPIcs.ECOOP.2024.38)
  connects cross-function traversal patterns with dense recursive layouts. The
  applicable idea is access-directed placement, not assuming mutable shared
  Scopes have the same representation freedom as packed mostly-serial ADTs.
* [Cache-conscious coallocation, PLDI 2006](https://www.microsoft.com/en-us/research/publication/cache-conscious-coallocation-hot-data-streams/)
  groups contemporaneously accessed allocation streams and fields. It motivates
  shape/access/lifetime grouping rather than indiscriminately pooling objects.

## Delivery evidence

Leaders own the concrete layout and affine interfaces and record their decisions
and measurements in the relevant implementation notes. Acceptance must include
actual traversal/scan output, transformation/placement evidence, captured aliases
and retention, strict Number semantics, reference-heavy controls, optimizer cost
and affected real source/artifact/REPL/platform journeys. A reduced allocation
count alone is not proof of locality or a Native latency improvement.
