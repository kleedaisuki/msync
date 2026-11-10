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

## Managed spatial Scope storage

`ScopeValue` keeps one private storage union, not an additional payload reference
in every ordinary object. Existing dictionary/array constructors remain ordinary
Value storage. The VM's explicit `ScopeAllocator` selects numeric placement when
constructing a Scope from its operand spans; reference-only objects retain their
original header and reference-array representation.

A numeric placement groups by logical layout, semantic allocation site and
numeric-field shape. An immutable row descriptor maps logical member offsets to
compact numeric or reference slots. Numeric fields occupy row-major doubles in
managed pages; each row owns only its own reference payload. A page contains no
GC references. Logical names, enumeration order and guarded field hints remain
independent of this physical arrangement.

The allocator owns mutable pool state separately from the immutable descriptor.
It retains only a current page, starting at one row and growing geometrically
toward an 8 KiB page target. This is allocation granularity, not an input-size
limit: wide rows still have their full field count. Full pages are not retained
in an allocator history. A live Scope owns its row/page and descriptor, never a
back-reference to the allocator or another row's managed payload. The GC can
therefore collect dead sibling Strings, functions, Scopes and Cells even while
some numeric rows remain live.

Raw Number get/store methods connect the page to `OperandStack`'s existing double
sidecar. `MakeScope` consumes the reference/numeric spans before clearing its
operands, avoiding a temporary array and initial wrappers for computed doubles.
Public Value consumers still materialize `NumberValue` at their actual boundary.
A numeric slot receiving a String, Null or Cell promotes only that Scope into
ordinary Value storage; the exact captured Cell is installed and retained.
Promotion does not change the source storage type or admit an invalid source
assignment. Binary64 payloads, signed zero and NaN bits survive unchanged.

Repeated immutable Number references already share payload. Each pool compares
the current numeric reference pattern with its preceding row during the existing
shape/construction passes. A wholly repeated reference pattern remains ordinary
storage instead of duplicating literals into a page. New references and raw
doubles remain eligible for numeric placement. Only immutable Number references
are retained by this one-pattern policy; no global constant set, per-field boxed
cache or nonnumeric managed payload is kept.

## Preparation applicability versus physical scans

The first matched source control showed why raw-VM field access alone is not an
end-to-end allocation result. A private diagnostic Core mirror of immutable
common1 counted 64 numeric materializations in one eight-row/four-field scan.
The first stacks all entered `PackedScopeStorage.GetField` through
`ReadKnownMember -> KnownLoop.Step -> TryExpandLoop`: the contextual backend had
attempted the scalar scan twice before publishing no I/O expansion. The mirror
records functional attribution, not a timing or allocation benchmark, under
`.temp/spatial-box-attribution/`.

Known I/O-loop expansion now checks whether the body can expose a transfer or
source call before evaluating its elements. Scalar-only field scans remain in
the affine/VM path. The rule does not skip source execution, prohibit a loop or
disable contextual preparation; ordinary I/O and unknown-call wrappers retain
their existing applicability. It also avoids adding a read memo table to a scan
that reads each field only once.

The same locality principle applies to VM plan bookkeeping. Its private
`PlanNodeResult` is a readonly value record containing completion and offset, so
the result array stores those hot records inline rather than one reference and
one separate object per node. Public result/Value APIs are unchanged.

## Causal common-path cost repair

The first common1 source comparison retained the intended physical Scope sites
but added allocation traffic for numeric/mixed records. The no-I/O-loop guard
and repeated-reference placement policy removed that traffic regression in
common3. Its reference-only control nevertheless had a large wall-time increase,
which cannot be explained by numeric placement because those rows remain plain.
Both diagnostic comparisons remain recorded rather than discarded.

A private operation-count mirror then executes one identical reference fixture
against frozen baseline/common3: every VM opcode and direct preparation count
is identical (including 2,052 member reads, 514 Scope constructions, 1,026
iterator steps and 517 preparations). The candidate removes two failed
KnownLoop trials and their 2,048 additional field reads. It does not perform more
source work. A single gated sampled-thread attribution pair, after compilation
and warmup, locates 93.1%/94.6% of sampled stacks under repeated `IndexBlocks`.
Its subpaths repeatedly partition instructions, split large Scope member text,
create duplicate-name HashSets and copy boundary arrays. True GC pause is about
62.8/67.5 ms; sampling-related suspension is not reported as GC or exact CPU.
These are scoped method attribution results, not a benchmark.

Large-constructor uniqueness is now a weak, immutable operand-text fact. Small
Scopes retain their existing allocation-free span comparison. Block formation
uses one linear marker/effect pass, and direct PC indexing avoids an intermediate
block array and per-block LINQ rescans. A rewritten function retains its original
identity only when every instruction, source span, jump and PC mapping is
unchanged; the VM then reuses its block index. These caches contain no mutable
field values, filesystem observations or plan-validity flags. Generated CFG and
duplicate-field controls compare 1,300 cases with the original partitioner.

The common4 matched managed source control (512 rows, compile/bind outside the
timer, same SDK/runtime and TieredCompilation=0, three warmups/five samples)
reduces approximately 10.7–10.9 MB execution allocation to 1.73–1.81 MB. Its
descriptive medians improve from roughly 46–53 ms to 4.5–6.0 ms across computed,
literal, mixed and reference-only records. Because the reference control benefits
as well, the approximately 9x common-path improvement is attributed to removing
redundant preparation/block work, not exclusively to spatial layout. The full
raw samples, source and producer identities and earlier results are retained in
`tests/Performance/ScopeSpatial/README.md` and `.temp/scope-spatial/`.

## Independent spatial contribution

A separate bounded component compares plain construction with `ScopeAllocator`
on the same common4 Core, removing compiler/backend-version differences. It
constructs 32,768 fresh four-Number rows with identical NumberValue/Value-array
inputs, verifies every identity and Number bit pattern, and scans using raw
numeric access. Five recorded samples of 32 passes give descriptive medians
29.91 ms plain versus 17.57 ms packed (about 1.70x); both scan paths allocate
zero bytes. The packed rows occupy 136 reference-free pages, with 1,048,576 bytes
logical numeric payload and 1,056,736 bytes page capacity.

Full-GC held-heap observations are approximately 6,554,824 versus 4,206,072 bytes
(35.8% lower); they describe retained live object graphs, not allocation traffic
or a cache-miss census. Supplying preboxed inputs deliberately exposes the other
side of the boundary: construction traffic rises from 6,553,648 to 9,187,640 bytes
(40.2%) because packed placement copies those inputs into pages and discards
them. The VM span entry can avoid initial wrappers for raw computed operands,
but the contextual backend may already have materialized a computed Number.
Neither this component nor the common-path result establishes an unconditional
constructor or whole-program speedup. The component source, exact Core hash,
methods, samples and reproduction commands are in
`tests/Performance/ScopeSpatial/Component/` and its parent README.
