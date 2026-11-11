# Field memory, static layouts and lifetime inference in 0.8

The subsequent [object spatial-layout work](object-spatial-layout.md) extends
the reference-array baseline below with numeric payload pages, per-row managed
reference ownership and raw VM field operands. Logical fields and instance/Cell
identities remain the same; physical field placement is not a source guarantee.
[Affine scan scheduling](affine-layout-optimization.md) separately coordinates
readonly numeric record accesses without reassociating floating reductions.
The earlier acceptance and layout measurements in this note remain historical
records of their named implementations, not measurements of those new changes.

## Workload and ownership

The November 9 extension targets ordinary Scope construction, aliases, branch
updates, loop-carried fields and helper functions. The previous scalar replacement
pass stops at a basic-block boundary. This work extends the reasoning across the
CFG and makes the remaining physical objects cheaper. It does not add a second
source evaluator or assume that every object has a compile-time instance count.

The [mutable-state contract](mutable-state-and-effects-v080.md) remains
authoritative: binding and field types are fixed, values are mutable, closures
capture storage locations, and aliases refer to one Scope instance. The
interpreter owns mutable source execution; independent I/O runs only after
operand admission. Filesystem observations and authentication are never reusable
memory-analysis proofs.

Three identities must not be conflated:

| Identity | What can be inferred | What it does not imply |
| --- | --- | --- |
| Lexical storage location | Fixed type and captured-cell relationships | That its current Scope reference never changes |
| Allocation site and shape | Ordered field schema and possible instances | That repeated execution creates one shared object |
| Scope instance | Exact aliases and field versions along an edge | That another equal-looking instance is interchangeable |

An allocation executed once before a loop can have loop-carried fields. An
allocation inside a loop may produce multiple simultaneously live instances.
Keeping the former in scalar field slots does not justify merging the latter.
Recursion, deferred ranges and externally selected cardinalities likewise keep
their runtime instances and ownership while benefiting from a shared layout.

## Cross-block memory model

Memory locations are rooted at a proved object/cell identity and a field path.
The CFG state carries reaching values and memory versions. A field store kills
only overlapping locations; a store to another object's independent field does
not erase useful facts. At a join, incoming fields can agree, need executable
edge stores into a scalar join location, or remain unknown. An old value must
not be forwarded merely because it was seen on one predecessor.

The implementation direction follows [LLVM MemorySSA](https://llvm.org/docs/MemorySSA.html):
give reads, writes and joins explicit relationships instead of repeatedly
searching every preceding memory operation. The bytecode uses ordinary local
slots and predecessor stores as executable joins; no new Phi opcode or source
syntax is required. Its abstract memory representation is not a claim that the
entire program has been translated to LLVM SSA.

[GCC's alias-analysis interfaces](https://gcc.gnu.org/onlinedocs/gccint/Alias-analysis.html)
separate statement uses and clobbers. Our sparse function summaries similarly
describe paths rooted at actual parameter/capture identities, including a
captured-cell rebind separately from a member store. Subtree effects absorb
recursive path growth instead of imposing a source nesting quota. Unknown
callbacks retain unknown memory effects; known filesystem operations do not
automatically mean that every lexical Scope field was modified. Existing
`EffectAnalysis` still owns failures, divergence and task-lifetime effects.

Deletion has a stronger condition than forwarding. A physical store observed by
an escaped object, captured cell or called function remains observable even when
the current function does not read it. Missing-member and duplicate-construction
errors also remain selected effects. Acquire/release and function return retain
their existing task ownership, not a new global memory drain.

## Compact physical layouts

A Scope's named fields are fixed; the source does not specify their slot order.
Physical instances can share an immutable name-to-slot layout while owning
fresh mutable storage. Shared layout metadata is not shared field storage.
A slot containing a CellValue must keep that exact cell when an alias or method
captures the field.

This is a logical Value-slot layout, not the CLR's private object-header layout
or a promise of unmanaged addresses. Path/String/Function/Connection fields are
predominantly references; removing each instance's dictionary and member-view
wrapper addresses the ordinary workload without introducing unsafe GC reference
storage. Scalar replacement can remove the entire physical object when escape
and lifetime proofs permit it.

Compatible field-index hints can use the previously unused A operand of
GetMember/SetMember: zero means named access, a positive number is a one-based
logical slot hint, and Text remains the authoritative member name. The runtime
uses the hint only when that slot has the expected name in the actual layout;
otherwise ordinary named lookup applies. Older readers ignore A and preserve
the same operation. Connection and Process members remain their existing typed
operations, not Scope array accesses. This mechanism does not require a new
artifact format or a new language-semantic version.

## Linking and regions

Static schema information enables relocatable layout descriptors and symbolic
field references. A future linker can merge tables, align regions and relocate
references, but also needs symbol identity, layout compatibility, callable
contracts, memory-effect summaries and initialization/ownership information.
Concatenating bytes alone cannot establish those semantic agreements.

The useful connection to [MLIR's ownership-based deallocation](https://mlir.llvm.org/docs/BufferDeallocationInternals/)
is to infer which region owns an instance and where ownership escapes. A local
non-escaping instance can collapse into its invocation frame; an escaping
closure or deferred callback must retain its actual storage lifetime. The
current managed runtime remains responsible for physical allocation and GC;
this work does not introduce an unmanaged arena or a generic linker merely to
express static layout facts.

## Acceptance and optimizer cost

Each transformation is exercised through actual source, relocatable bytecode
and bound bytecode, using independent O0 controls and checking both selected
branches. Structural checks require the targeted member accesses/allocation to
disappear, not just identical final values. Capture aliases, differing branch
values, outside-loop objects, repeated constructor instances, escaped stores
and selected errors are discriminators.

Analysis runs only on relevant functions. States are published at block entries,
worklist updates follow changed successors, and summaries are sparse by access
path. Constant pools retain indexed interning and exact floating representation.
Benchmarks compare compiler and prepared-VM time/allocation, with no-Scope
scripts as a negative control; a smaller artifact alone is not a latency result.

## Integrated implementation

The O2/O3 pipeline now uses the following concrete mechanisms:

* `CrossBlockMemoryOptimization` promotes private, noncyclic allocations across
  blocks before block-local SRA. Field slots carry branch and loop joins; nested
  containment propagates escape. A cyclic allocation site retains its physical
  generations rather than being assigned one concrete object identity.
* After preparation, relevant functions share one `MemoryEffects.Analyze`
  result. Physical field caches use must-valid joins and actual closure origins;
  parameter/capture access paths invalidate only the fields they can modify.
  A capture rebind and raw shared field cells remain distinct alias events.
  Creation of a lazy adapter is not its callback execution: actual enumeration
  applies latent effects, while proved callback-free Path work does not become
  a lexical-memory clobber.
* `AggregateArgumentSpecialization` handles known direct, readonly Scope helper
  inputs even when I/O ownership prevents ordinary inlining. It stages every
  original argument before extracting proved fields, then calls a private
  scalar-argument clone with the original capture/return ownership. Unknown
  shapes, aggregate escapes, writes and potentially effectful enumeration keep
  the original path. Existing escaping callable signatures are not rewritten.
* Physical Scope objects use shared `ScopeLayout` metadata and fresh Value
  arrays. The compiler emits guarded member-slot hints using existing operands;
  constant cloning and binding share the layout but create fresh owned payloads.

The preparation cache does not cache the module-dependent physical forwarding
result under only a coarse effect flag. That analysis runs after preparation
against the current precise summaries. Functions without lexical field clients
do not request the new module analysis. Ordinary transfer-only functions retain
their existing linear normal-form path.

Physical escaped-object stores remain in this implementation: a called function
can fail after an earlier store, exposing the prefix. Private scalarized field
stores instead use the existing graph's cross-block demand/DSE. General
escaped-memory SSA store deletion, arbitrary aggregate ABI splitting and an
unmanaged region allocator are not claimed.

Accepted local mechanisms are recorded in `tests/CrossBlockMemory` (728 focused
contracts), `tests/MemoryEffects` (69, including recursive/loop widening and actual
bytes), `tests/AggregateArguments` (173), and `tests/ScopeLayout`. Those are separate
focused records, not a fabricated single cross-platform test total. The native
delivery lane owns platform acceptance; the [performance record](performance-v080.md)
owns actual before/after measurements and workload applicability.
