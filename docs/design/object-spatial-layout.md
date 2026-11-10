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

The implementation and records below distinguish actual placement, legal schedule
changes, allocation traffic, retained heap and Native latency. These are separate
claims; fewer allocations alone do not establish locality or an overall speedup.

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

## Exact Native source-run comparison

The final Linux Native comparison uses the accepted contextual-backend image
(`7d1d2856e75631f162bdded1f7258dc7b1d71620afcd2ae28ae42939a08865f4`)
and spatial candidate source `a0cebdf6c5f31dd0b3650fd534cbbee7ac1d7ddd`
(image SHA256
`54d591a87ab831c07459333d3e06acc39a1164b37beaae0eb83cccfd9df21bfc`).
Each case has seven alternating AB/BA fresh-process pairs after one untimed
functional launch per image. Commands use `run SOURCE -O2 --no-config
--no-telemetry` and `NO_COLOR=1`. Source and runtime seed files are prepared
outside timing; each pair reads the same seed, and every output byte is checked
after timing. There is no global cache flush or concurrent build/profile work.
The measured interval includes startup, source compilation, contextual
preparation and execution, not just a physical field scan.

| Source workload | Baseline median ms | Candidate median ms | Median paired delta ms | Faster pairs |
| --- | ---: | ---: | ---: | ---: |
| Computed numeric Scope build/scan, 512 rows | 61.84 | 55.39 | -7.20 | 6/7 |
| Reference-heavy Scope control, 512 rows | 67.49 | 64.15 | -4.14 | 4/7 |
| Two numeric Scope scans, 256 rows | 143.44 | 140.48 | +1.78 | 3/7 |
| Scalar output, no Scope | 20.73 | 24.08 | +3.04 | 1/7 |

Paired deltas are candidate minus baseline; the median of paired differences
need not equal the difference of independent medians. The computed case shows
a combined Native implementation benefit, not isolated numeric-page attribution.
The reference control is mixed: candidate p95 rises from 72.17 to 75.40 ms.
The scalar control retains a visible +3.04 ms paired-median regression, with six
of seven pairs slower; this comparison does not establish its cause.

Disassembly confirms real affine fusion: the two-scan portable artifact changes
from two `IteratorNext` instructions to one. Nevertheless, its source-run pairs
do not establish an end-to-end latency improvement. This mechanism result, the
same-Core packing component's 1.70x scan result, and the managed common-path
preparation improvement above answer different questions and are not
interchangeable speedup claims.

Exact commands, native/receipt/harness/source identities, all raw samples,
stdout/stderr, checked results and both affine disassemblies are retained under
`artifacts/candidates/spatial-layout/a0cebdf/performance/`; `index.json` links the
records. No outliers were removed and no replacement timing batch was run.

## Completed integration and acceptance

The accepted source is `a0cebdf6c5f31dd0b3650fd534cbbee7ac1d7ddd`.
[Native candidate 42826959366](https://github.com/kleedaisuki/msync/actions/runs/42826959366)
passes all eleven selected phases on both Linux and Windows, including the
object-locality journey and required affine-fusion check. Three actual VS Code
host suites and a separate real CLI-installed VSIX/native pair also pass. The
installed source LSP, source-deleted HIR and format-5 plan views retain hover,
outline, highlighting, read-only behavior and absence of execution effects.

| Requested direction | Implemented mechanism and evidence |
| --- | --- |
| Actual spatial object layout, not contiguous references alone | Numeric Scope payloads share managed double pages grouped by logical layout, allocation site and numeric shape; raw VM operands access the page. Same-Core scans and held-heap observations isolate this representation from unrelated runtime changes. |
| No physical-layout promise in the language | Logical field names/order, fresh and singleton identities, aliasing, Cell captures and snapshots remain unchanged. Incompatible payload writes promote the one affected object, not its neighbors. |
| Correct lifetime and GC behavior | Managed references belong to individual rows; pages contain no such payloads, immutable descriptors contain no allocator/current-page ownership, and full old pages have no allocator history root. Weak-reference discriminators prove sibling payload and allocator isolation. |
| Traversal/scan schedule informed by layout | The default machine-independent pass fuses proved eager Number/readonly numeric-Scope scans, changing the schedule from `(statement,i)` to `(i,statement)` and reusing the row before advancing. Actual source/object/executable positives require two iterators to become one. |
| Graphite-style model rather than a renamed unroller | Explicit integer ordinal domain, access/schema facts, scalar dependence legality and schedule selection run before literal expansion. The implemented subset is uniform one-dimensional scan fusion, not a general ISL scheduler or a claim that pointer traversal is affine. |
| Numeric and side-effect semantics | Each reduction retains its original IEEE order/grouping. Captured/cross-scan dependencies, lazy callbacks, possible field failures and opaque effects retain the original schedule; readonly repeated Scope references are allowed. |
| Language-native representation and composition | No new source type, layout annotation, dynamic CLR type generation, unmanaged GC-hidden arena or external solver deployment is required. Existing `fn`, Scope, Range and Buffer programs use the default path. |
| Negative controls and optimizer overhead | Computed/shared-literal/mixed/reference cases, unchanged headers, raw wrapper counts and retained-heap tests expose costs. A causal common-path repair removes repeated block/index work instead of disguising reference-only regressions as a layout trade-off. |
| Usable cross-platform product path | Both Native platform matrices, real REPL/terminal use, artifact lowering/execution and actual installed extension acceptance complete on the exact package bytes; strict local build and changed-source formatting pass. |

These results justify retaining the representation and bounded affine pass,
without claiming every constructor or source-run case improves. The known
preboxed-construction cost, scalar startup/control regression and inconclusive
small affine source-run result stay explicit above. They are not replaced by
the independent scan component's result or a pooled speedup.

All four validated packages, source/build hashes and installed evidence are
linked by `artifacts/candidates/spatial-layout/a0cebdf/delivery.json`; the
matching checksum list covers the Linux archive, Windows portable archive,
Windows installer and VSIX. This task does not publish or replace any release.
The public 0.8.1 tag/assets remain unchanged, bytecode format remains 5, HIR
format remains 1 and the source language remains 0.8.0. Documentation-only
closure commits do not change the accepted production source or payloads.

## Tail-latency follow-up

The subsequent [tail investigation](performance-tail-v081.md) retains the original
seven-pair observations above, but separates them from larger process controls
and power-user execution/session measurements. A 1,000-pair diagnostic comparison
does not reproduce the original scalar +3.04 ms magnitude; its smaller common
startup cost is not attributed to object packing. Reference-only slow-end changes
also do not establish a stable regression in that diagnostic window.

The useful fixes remove work at its owning phase rather than tuning the collector:

| Owner | Avoided work |
| --- | --- |
| Predicate analysis | Eagerly walking every possible alias and allocating an identity at every opaque call; invalidation is now realized on demand. |
| Scan optimizer | Expensive per-block memory analysis before proved fusion, and forward-seeded worklists for backward liveness. Late provenance-dependent fusion remains available. |
| Persistent sessions | Submission-history lookup chains and synthetic empty compilation/full executable-HIR freezing to describe accepted bindings. |
| VM/backend boundary | Structurally empty preparation, per-instruction emitter arrays, and whole-caller rewriting for exact source calls with neither callee nor remaining-region preparation opportunities. Actual function execution remains intact. |
| Selected filesystem observations | Reading before an earlier submitted publication is admitted; the runtime now yields at selected namespace, exact-entry and current-referent frontiers without revalidating prepared plans. |

The isolated same-bytecode 8,192-row/four-scan runtime control retains all 1,000
samples per producer: P95 changes from 252.00 to 162.06 ms, P99 from 276.59 to
187.49 ms, and allocation from about 99.04 to 4.66 MB per execution. These are
execution-component observations for the loop-header repair, not Native launch
latencies or an attribution to cache locality alone. The initial-block protocol
caveat, full raw samples, block sensitivity and non-uniform GC-count changes are
retained in the tail note. The distinct-filename manifest, colored terminal and
final exact-Native comparisons have their own clocks and producer identities.

Compiler allocation is not universally linear: the 4,096-binding alias-havoc
family improves from 1.85 GB to 138 MB with approximately linear growth, whereas
128 independent reductions still allocate about 190 MB after the bounded phase
ordering/worklist repairs. Likewise, persistent-root publication has a measurable
dense-update cost even though complete prompt preparation improves. Those costs
remain visible in the linked component/session records.
