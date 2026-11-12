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

Source programs observe object identity, stable named fields, fixed storage
types, aliases, capture cells and returned values, not a field enumeration order.
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

## Indexed allocation is not a Range capability

The target model separates the iterator from the memory owner. Range exposes
iteration and stream transformations; a lazy producer, a singleton, and a
materialized vector are alternative representations. Random mutation and
allocation are not operations on the iterator. Replace the unpublished
Buffer-backed collection ABI rather than carrying a second storage path.
Preserve guest alias and snapshot semantics where observable, not the
internal operations that happened to implement them.

A collection-owned indexed heap reserves a row (or an extent when a collection
needs one), returns an owner-relative integer handle, and recycles the slot
when the owner releases it. Start with a fixed row stride from the specialized
layout and a free list; do not build a general variable-size allocator for
red-black-tree nodes. A binary Heap instead needs a dense vector, not a
separate allocation or a free-list entry per element. The native analogues are
[Bonwick's fixed-object slabs](https://www.usenix.org/conference/usenix-summer-1994-technical-conference/slab-allocator-object-caching-kernel)
and [SQLite's numbered pages and freelist](https://www.sqlite.org/fileformat.html#the_freelist);
neither their OS policy nor their disk format belongs in the guest heap.

An indexed row is a product of its declared field representations. Typed HIR
owns the field and capture facts, portable bytecode carries the logical facts,
and the executable chooses physical offsets, strides and operations. A proven
field access is base plus row stride plus field offset, with no lookup by name
or per-field type test. A reference field remains visible to the host GC;
only actual variant fields need a discriminator. An alias to a Scope does not
make every field a Cell: it can share the row. A mutable binding whose location
is shared by a closure or a lexical import must retain that location, but
neither independent fields nor uncaptured locals pay for it. A private,
nonescaping Scope can stay in locals/registers without a handle.

Handle admission belongs at the boundary where an owner and a dynamic index
are selected. Traversals with a proven live owner and in-bounds links should
not recheck those facts at every load. Retiring a row invalidates its use by
the collection; an ordinary guest Scope already published from it still has
its original identity and payload. Neither a row number nor a guest alias
silently becomes the next object to reuse the storage. A private free list does
not require generation tags on tree links; a future public raw-handle contract
must decide stale-handle semantics independently.

Set's collection owner has no node Scope or parallel object list. The source
rotations and the native worker both address the same owner-relative rows,
and their stores update the same memory. The earlier typed Range projection,
lazy Scope handle cache, and membership remapping path were removed with the
mutable Buffer carrier.

## Named Scope static section

[CPython's dataclass slots](https://docs.python.org/3/library/dataclasses.html)
are the useful analogy for a fixed field layout, not for field boxing:
[the member descriptor](https://github.com/python/cpython/blob/main/Include/descrobject.h)
records a name, type, and offset. Msync's stable shape and field types can
take that farther, choosing a machine representation for each field instead
of treating every slot as an object reference.

A named declaration's storage class is static, but its initialized state
belongs to an execution or REPL session, not the host process. The runtime
relocates portable declaration IDs to compact indices in the execution-owned
static section. Get/SetSingleton then index that section directly. A slot
starts empty, the first selected initialization publishes it, and aliases and
successive session images retain the same object. An unselected initializer
does not execute just because a slot has been reserved. Instruction positions
and portable operands on disk do not change. Anonymous constructions do not
use this section, even when they share the same layout as a named Scope.

The section currently stores a Scope reference per named declaration.
Separately optimizing the fields themselves means relocating known field
access to typed offsets and retaining a guest Scope handle only where its
identity is actually observable. The reference section is not evidence that
the whole payload has moved to static storage.

## Scope namespace erasure and typed places

The target model does not have a generic object slot for every binding.
Separate three questions: which declaration a name denotes; whether a value
has observable instance identity; whether a particular storage location is
shared. None answers the others. Lexical namespace selection can disappear
even when the selected field is mutable or its initializer is effectful:
translate the selection to the actual field location, not necessarily a
constant. Conversely, a Scope passed to an opaque identity-observing consumer
can require one handle without requiring generic field dispatch or a Cell per
field. Sharing the row and sharing a mutable binding that *contains* the row
are different alias relationships.

| HIR use | Lowered value or place | What disappears |
| --- | --- | --- |
| A known callable or constant selected through an inert namespace expression | Symbol, literal, or direct call with its actual capture operands | Receiver loads, namespace handles, member loads and generic dispatch |
| A field of a named declaration reached only by statically resolved uses | Program/session static field, or SSA value if immutable and dominated by initialization | Namespace wrapper, singleton-reference transport, field-name lookup |
| A field of a fresh, nonescaping construction | SSA value; private frame place only when needed across control or lifetime boundaries | Scope allocation, fields array, boxing and Cell transport |
| A field whose location is shared | Environment or static field place; capture the location, not a copy of its payload | Per-field boxes and unrelated Cells |
| A field of a published Scope or collection row | Typed projection from one admitted owner and identity; publish/reuse the handle only at the observation boundary | Generic layout tests on known accesses; per-row wrapper allocation on internal traversal |

These are classifications by use and ownership, not new guest types.
Returning a Scope from a known callee need not publish a handle when callers
only project fields. Returning one at the user/host boundary can publish it.
Two layout-identical anonymous constructions remain distinct if observed;
two aliases of a single row share one instance. A named declaration's stable
symbol survives code generations even if the current alias is re-bound.
Similarly, a known function shape is not a proof that a mutable function
field still contains the same callable. An open session may add later uses;
accepted export and capture locations keep their identities and must not be
erased by a closed-script projection rule.

The existing compiler supplies the starting facts. `HirScopeLayout` and
`HirLocation` in
[`HirModule.cs`](../../src/Msync.Hir/Model/HirModule.cs) carry field types,
null admission and location identity; `HirCaptureMode` distinguishes a value
from a location. `HirLowerer.Scope` already separates initializer evaluation
order from physical field order. `IsNativeCalleeProjection` in
[`SemanticAnalyzer.Native.cs`](../../src/Msync.Compiler/Semantics/SemanticAnalyzer.Native.cs)
already erases known inert native receivers, but
[`HirLowerer.Call`](../../src/Msync.Hir/Lowering/HirLowerer.cs) still
transports a callable for a known source target. Generalize the existing
callee-erasure condition to a proven source symbol and explicit capture
operands. An effectful receiver, a mutable selection or an open dispatch slot
is not a direct symbol: preserve its evaluation once, in the original order,
even when its eventual result permits a direct call.

### One address model across lowering

**Typed HIR** keeps logical field identities, value-versus-location capture,
initializer/effect order, source diagnostics and the reachability of exported
values. It neither chooses a host pointer nor invents a heap object for a
lexical namespace. Existing def-use, memory effects and escape facts decide
where aggregate identity is needed. An immutable shape determines which fields
exist; it does not prove which row a mutable binding currently names.

**Portable typed code** represents a surviving place by a typed logical
coordinate: `Frame(slot)`, `Static(declaration, field)`,
`Environment(capture, field)` or `Row(owner, index, field)`. A place's
contract includes its payload representation, owner/provenance and writable
or readonly status. The address calculation is ordinary IR; the absence of
pointer arithmetic in source is irrelevant. It is not a Number or an
arbitrary machine address, and computing it is not a memory access.
An index is a proved integral row identity or the result of explicit dynamic
admission, not a floating guest value revalidated on each field access.
Layouts and logical selectors can be interned and shared across functions.
Persist the minimal facts needed to resolve those places, not the typed HIR,
source text, target-native offsets or a second escape-analysis result.

**Bound execution** relocates a place once to a lane and offset in its actual
owner. Static, frame and environment accesses use the known base and offset;
row access adds the admitted index times the selected stride. Raw lanes keep
Number, index and Bool bits; reference lanes keep host-visible GC roots and
use the host's reference access/barrier, not an untracked native pointer.
Unions need a tag only where their representation genuinely differs. The
managed executor and native emitter consume the same normalized operands,
with direct typed loads/stores. Keep the portable origin PC separately for
diagnostics, effects and continuation recipes. Do not reintroduce
`ScopeValue`, `Cell` or string lookup as the operand abstraction at this stage.

Readiness, lifetime, bounds and shape are facts with different origins. The
producer verifies local/field indices and fixed layout once. Only an
unresolved dynamic owner, index, variant or mutable policy needs a guard.
An admitted base can be used throughout a non-suspending region until an
actual invalidating effect; reacquire the owner after suspension if needed
instead of retaining an unrooted interior address. A side exit carries live
values, owner/row identities and the original continuation, and never repeats
already performed initializer or I/O effects. Use narrow typed places for
the interpreter as well as the JIT; fixing only emitted machine code leaves
the same object protocol as the canonical model.

### Portable schema

Separate address formation from the load/store itself, as in
[LLVM's GetElementPtr](https://llvm.org/docs/GetElementPtr.html). A logical
place contains the owner and field ordinal, not a serialized machine address.
For a fixed physical lane the bound address is base plus byte offset; for an
indexed owner it is base plus row stride times index plus byte offset.
The source language needs no address arithmetic to express either operation.

The incoming parameter value domain is distinct from the type of its mutable
local slot: entering with one layout does not forbid a later legal structural
rebind. A by-value capture can be checked against its selected value domain.
A by-location capture instead carries an invariant storage contract (the same
reason Rust's [interior-mutable place is invariant](https://doc.rust-lang.org/reference/subtyping.html)).
Code-type table IDs are image-local; a promoted Cell retains its structural
contract and does not hold the old executable image alive. A changed opaque
call target admits that contract once at the selected boundary. Neither
comparing the current payload nor rechecking every field load proves the
invariant. Checking a Range-valued argument or field never enumerates it.

Replace the unpublished Scope wire encoding in one revision, not an old and
new ABI in parallel. Bump the artifact format discriminator to reject old
bytes unambiguously; do not add an old-version reader, a runtime compatibility
mode or a second importer. Keep source-language semantics and the separate
language version unchanged. Both artifact stages carry the same logical
records; only `.mexe` additionally carries its contextual plans.

Extend [`Module`](../../src/Msync.Core/Bytecode/Module.cs) with four
module-local tables, serialized after the header/context and before constants
and functions. Use the current length-prefixed arrays and signed 32-bit
indices (`-1` only for an absent optional index), so there is no pointer-sized
field on disk:

| Table | Entry and invariant |
| --- | --- |
| `CodeTypes` | `(TypeKindMask:u16, ElementType:i32, ScopeLayouts:i32[])`. `ElementType` is absent unless the value can be a Range. A nonempty, sorted, unique layout set enumerates every possible Scope alternative; an empty set with the Scope bit is *opaque*, not proof of any layout. Nullability remains a kind alternative. Nesting uses type IDs, not embedded descriptors. |
| `ScopeLayouts` | `Fields[]` in deterministic logical ordinal order, each `(Name:string, StorageType:i32)`. Names are unique; each fixed storage contract includes its Null admission. An ordinal is not a byte offset. This table describes values, not object identity or which fields happen to be captured by a particular construction. |
| `StaticScopes` | `(Layout:i32)` per declaration, never deduplicated by layout. Array index is the standalone static ID. A persistent compiler image additionally owns the append-only declaration-to-slot assignment across generations, without serializing live session roots or values. |
| `FieldProjections` | `(Name:string, Cases:(Layout:i32, Ordinal:i32)[])` with finite cases sorted by unique layout ID. Every case must identify the named member. A one-case entry is monomorphic; an empty case list instead declares an explicitly checked structural projection. For a finite projection, the result type is the union of admitted fields' storage types and a store's value must fit *each* admitted field. |

Carry `CodeTypeId` for every parameter, result and captured payload in
[`FunctionCode`](../../src/Msync.Core/Bytecode/FunctionCode.cs); each capture
also says value versus location. The current public `TypeRef` views, where
still needed by analyses, are projections of this one signature, not a
duplicate type table on disk. For selected static field access a function
also declares the sorted set of `RequiredStatics` it assumes initialized at
entry. Locals and stack values are inferred by the bytecode verifier; no
second table per instruction is needed. Encode Scope constants by layout ID
and values in ordinal order, so constants and constructors establish the
same provenance; for a constant Range its element type must include the
layouts of the actual serialized elements. Stable IDs follow function
specialization, closure captures, Range element flow and image rebasing.
An inferred `TypeRef.Scope` alone is deliberately insufficient for access.
Neither is every current HIR `Shape` an allocation layout:
[`SemanticAnalyzer.Join`](../../src/Msync.Compiler/Semantics/SemanticAnalyzer.Types.cs)
creates an intersection view of the fields of two Scopes. Carry the finite
constructor-layout alternatives independently of that semantic view, including
through returned values, captured storage contracts and Range elements. Do
not register the view as a new physical layout or derive a receiver set from
its field names after lowering. Use the open state only for a producer that
genuinely lacks the layout fact.

Use the existing `Instruction(Op, A, B, Text, Span)` encoding. The new Scope
instructions use `Text=null`, with diagnostic names supplied by the tables:

| Instruction | Operand and stack contract | Replaces |
| --- | --- | --- |
| `NewScope A=layout, B=site` | Consume one initializer value per field in logical order, produce a fresh `Scope<A>`. Allocation site is provenance, not layout or identity. | `MakeScope` name list parsing and shape inference from payload. |
| `LoadField A=projection` | Consume a non-null Scope with an admitted layout; produce the selected field's typed payload. | Scope `GetMember`, hint comparison and name lookup. |
| `StoreField A=projection` | Consume receiver then assigned value in existing push order; update the selected typed field and produce Null. Conversions are explicit producers, not side effects of address resolution. | Scope `SetMember`, hint comparison and name lookup. |
| `AdmitScope A=code-type` | Consume a genuinely opaque Scope and produce the stated finite Scope layout domain or its selected diagnostic. Admission checks the actual owner's typed layout once; it does not expose arbitrary byte-address arithmetic or implicitly copy a mutable object. | Only needed at an actual open producer boundary, not a second member operation. |
| `JumpIfStaticReady A=target, B=static` | Jump when the declaration has a published owner, establishing readiness on the taken edge. Stack is unchanged; the target uses the ordinary branch operand. | `GetSingleton; Dup; IsNull; JumpIfFalse; Pop` guard protocol. |
| `PublishStatic A=static` | Consume one candidate field value per declared field, commit only if no owner has won, and establish readiness. Does not return or allocate a namespace wrapper. | The named `MakeScope; SetSingleton` pair. |
| `LoadStaticField / StoreStaticField A=static, B=ordinal` | Require readiness and lexical provenance for that declaration. Read pushes its typed payload; write consumes the assigned value and pushes Null. Neither loads a receiver. | Named field receiver load, member resolution and singleton reference transit. |
| `LoadStaticScope A=static` | Require readiness; return the winning declaration's identity handle only for an actual whole-Scope value use. It views the authoritative static fields. | Loading a singleton object as the prerequisite to every field access. |

A fixed place lowers to base and typed lane/offset without a general
address object at runtime. For a finite layout join the binder resolves all
cases once. If their physical location/representation agrees, emit a direct
load/store; otherwise choose the actual typed place by layout identity and
reuse that selection while its receiver is live. Different layouts are a
real dispatch requirement. The explicitly structural form checks the actual
selected field and, for stores, that field's fixed payload contract.
Inlining may reveal a finite receiver without proving that the assigned value
fits its storage; do not reject the checked form or replace it with an
unchecked finite projection on receiver evidence alone. Open admission checks
both ownership and typed storage validity; matching field names cannot
authorize unchecked loads from an independently mutable foreign owner.

There is no generic Scope/Process/Connection member opcode in the new
format. Current source already emits a registered Process member
capability and rejects Connection `::` in favor of `@`; lower Process
members to their explicit typed native operation and Connection qualification
to its existing operation. Mutable lexical alias rebinding still selects the
actual Scope; the static field form is only valid when reaching definitions
prove that the receiver denotes its declaration.

**Validation is source-free.** The reader validates table indices, unique
fields and case/domain consistency once, then extends
[`BytecodeValidation`](../../src/Msync.Core/Artifacts/BytecodeValidation.cs)'s
CFG stack/local proof. Each typed load requires a definitely non-null
receiver whose *entire* layout domain is covered by the projection; an
intersection test on `TypeKind` is not sufficient. A control-flow join
unions possible layout IDs and intersects definite-static-ready sets.
Verified constructor operands, constant fields, call arguments/returns,
capture modes and typed native result contracts establish values; a
declaration in a table is not evidence that an opaque producer actually
returned the declared shape. A store value must satisfy every possible
field's fixed contract. A raw location is not a Scope operand; if a
mutable captured location is loaded, use its fixed storage domain, not
the shape of its last observed value. Verify static readiness on both
direct field operations and callees' entry requirements; a selected open
call can admit unresolved requirements once at the call edge. A failed
proof is a format error for a typed operation, not a request to retry it
by name. The binder never opens HIR and the executor never revalidates
these facts.

Format 6 carries typed places directly. The former `ExecutionScopePlaces`
inference, name-based layout parsing and wrapper-only singleton transport
are no longer part of the bound path. The
[bound place table](../../src/Msync.Runtime/Execution/BoundScopePlaces.cs)
translates verified layout/ordinal pairs to executable field access. The
[portable place tests](../../tests/PortableScopeCode/Program.cs) reject
invalid wire contracts; the
[typed place journey](../../tests/Msync.EndToEnd/typed_place_journey.py)
retains source, detached artifact, identity and alias coverage.

This follows [LLVM's address computation versus load/store
distinction](https://llvm.org/docs/GetElementPtr.html) and
[Cranelift's verified stack places and explicit offsets](https://github.com/bytecodealliance/wasmtime/blob/main/cranelift/docs/ir.md).
[LLVM SROA](https://llvm.org/docs/Passes.html#sroa-scalar-replacement-of-aggregates)
supports splitting private aggregates into values, while [Stadler et al.'s
partial escape analysis](https://ssw.jku.at/Research/Papers/Stadler14/Stadler2014-CGO-PEA.pdf)
shows why materializing at an observed escape is different from allocating
on all paths. Use these mechanisms, not their source-language object
contracts or their target machine layouts.

### Implementation order and discriminating tests

1. Change the artifact schema and compiler, validator and executor together:
   replace `MakeScope`, Scope `GetMember/SetMember`, and Scope constant name
   lists with layout IDs and field projections. Delete the name guard and
   the bound graph's recovery pass in the same switch. This removes checks
   across constructors, local fields, argument/return projections and typed
   rows; optimizing one pattern or library is not a prerequisite.
2. Replace `GetSingleton/SetSingleton` with selected initialization in the
   execution/session static
   layout. Reserve field storage without evaluating anything; run the
   original initializer only on a selected first entry. Commit the winning
   candidate once, preserving the existing reentrant first-publication
   behavior and the binding initialized by that declaration. Stage candidate
   values in the initializer activation; do not expose partially initialized
   fields. If an initializer publishes a closure that captures a candidate
   field before commit, that location stays with its candidate (including
   a losing one), while winning captures and any published handle view the
   authoritative fields. Do not retarget escaped locations or keep mirrored
   fields.
3. Use the same place model for private anonymous fields and for typed
   collection rows. Eliminate a closed standard or source namespace
   receiver when a known call needs only its explicit captures. Port Set
   mutation and rotations to owner-relative places with typed link/color
   operations. Remove the superseded Buffer/object protocols after the
   typed route covers construction, reads, writes, captures and live exits.
4. Require source, source-free `.mso` and source-free `.mexe` execution
   through the same field semantics, with native on/off where applicable.
   The [object-locality journey](../../tests/Msync.EndToEnd/object_locality_journey.py)
   already detects eager or repeated named initialization and anonymous
   identity conflation. The [literal projection fixture](../../tests/LiteralScopeProducers/literal-demand.msync)
   distinguishes mutable aliases, fresh views and a genuinely lazy tail;
   it must not be optimized into an eager snapshot. Add variants for
   reentrant named initialization, escaped closures that mutate a shared
   static field, field-layout joins and selected mutable callable replacement
   across REPL submissions. Reject malformed layout IDs, projection offsets,
   incomplete join cases, nullable or opaque receivers at typed loads,
   incompatible stores and uninitialized static loads at artifact read.
   Prove the representation too: for a closed field loop there is no
   `ScopeValue` allocation, name lookup or per-field guard in the lowered hot
   body; for its escaping counterpart the handle count matches actual
   observed instances. Check end-to-end time and cold binding cost rather
   than declaring success from an opcode census alone.

For the format cutover, the collaborating edits form one dependency chain:

| Owner | Required change |
| --- | --- |
| Compiler + HIR | Preserve actual layout alternatives at semantic joins independently of structural views. Update HIR model/codec, session layout interning and `HirLowerer` to emit the complete portable table and typed operands, including nested Scope constants and explicit Process members. |
| Core artifact and bytecode | Add the table records, update `Module`, `FunctionCode`, `OpCode`, `InstructionEffects`, `BlockBuilder`, artifact encode/decode and the verifier. Handle nested Scope/Range/closure constants with the same table context. Bump the wire revision at the same point the old opcodes disappear. |
| Portable optimizer | Replace textual operands in scalar replacement, memory/effect analysis, CFG/def-use, inlining and aggregate argument specialization. The logical identity for a field analysis is layout plus ordinal; a projection may name a finite set of these. Rewriters preserve or explicitly remap IDs, not reconstruct a name-based instruction for the executor. |
| Contextual binding + Runtime | Consume the typed table in `ExecutionTargets`, region/native worker importers and the contextual planner. Layout-known Scope construction uses its compiled descriptor. Replace `Interpreter.State`'s field guards and `StaticScopeSection`'s wrapper protocol with direct owner access and selected initialization. |
| Tooling + acceptance | Render typed operands in the CLI artifact disassembler, update hand-assembled test modules, and rebuild unpublished fixture artifacts from their original source. Test source-free bind/run and malformed-image rejection in a single native build. |

Adding an optional table while emitting the current name operands would leave
two authorities and would not make the validator or executor simpler. The
first cutover does not need to move every field into native register or
row storage on day one; it does need every Scope producer and consumer in
the chosen format to agree on one typed layout contract.

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
GC references. Logical names and guarded field hints remain
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

### Page-level descriptors for homogeneous numeric rows

Previously each packed row allocated a storage object with references to
its shape, page and reference payload, even if its reference payload was
empty. The row offset now belongs to the Scope handle; numeric-only rows
on the same page share one storage descriptor. Mixed rows still own their
references, and ordinary reference storage keeps its existing type
dispatch. The shared descriptor contains no sibling rows or allocator
state. A logical field index is still checked against the shape before
adding the physical row offset; a neighboring row is not a valid field
of this Scope. Promotion detaches only the promoted handle, not the page.

A managed .NET 10 component test on Linux x86-64 (CPU 12,
`DOTNET_TieredCompilation=0`, three AB/BA pairs) built 32,768 distinct
four-Number rows on 136 pages, verified each field and identity, and
scanned them in declaration order. The candidate removes 32,632 separate
storage objects and reduces construction traffic from 9,187,640 to 7,620,224
bytes. Approximate live heap after a forced collection falls from
4,206,072 to 2,638,648 bytes (-37.3%). The ordinary nonpacked control
retains the same 6,553,648 allocated bytes and 6,554,824 live bytes.
All six scans allocate zero bytes. Raw scan elapsed time improved in the
three packed pairs, but the controls also moved with host load; this is
not a standalone cache-miss or latency result.

A separate paired VM comparison held the exact four format-5 executable
artifacts, Runtime, compiler and optimizer fixed, changing only Core.
For 8,192 records each, the computed numeric case falls from 3,559,176 to
3,167,568 bytes per execution, and mixed records from 3,362,208 to
3,296,680 bytes. Shared literals and reference-only allocation are
unchanged. Four alternating fork pairs with eight checked observations
per fork give pooled descriptive elapsed medians of 42.19 to 39.13 ms
(computed), 30.75 to 34.32 ms (shared literals), 37.08 to 39.24 ms (mixed),
and 30.59 to 32.11 ms (reference-only). Elapsed differences across the
control forks are mixed; prefer the allocation and retention results
over a broad VM throughput claim. An earlier `double[]` type test in
the accessor materially regressed even the plain control and was not
retained. The accepted representation uses the existing storage-class
discriminator and leaves the public artifact ABI unchanged.

Receipts (build manifests, component and exact VM samples, focused test
output and artifact identities) are in
`artifacts/benchmarks/library-matrix-v086/locality-deep/`.

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

### Native power-user tail results

The standalone CLI comparison uses the exact `9e5e997` Native candidate; the
final session comparison uses `604b558`, whose only subsequent product-source
change preserves assigned fact identity and IEEE payloads during publication.
These are separate frozen producers, not a claim that both tables measured one
binary. No complete CLI timing batch was repeated for the session-only change.

| Native workload | Previous P95/P99 ms | Changed P95/P99 ms | Boundary |
| --- | ---: | ---: | --- |
| Fixed-state colored REPL, 1,024 requests | 52.08 / 63.07 | 11.15 / 14.22 | Enter through result and next rendered prompt |
| Growing colored REPL, requests 769–1,024 | 434.21 / 753.50 | 100.24 / 117.00 | Same boundary; 770–1,025 user exports, nonstationary age window |

For CLI power workloads, the 8,192-row reference `.mexe` P99 is 147.98 versus
100.68 ms (400 pairs), and the distinct-filename manifest `.mexe` P99 is
1,349.37 versus 354.14 ms (200 pairs). The latter is an empirical slow-end
observation with an unbounded upper population-quantile reference interval, not
a precise P99 guarantee. Light startup cases do not establish a stable P99
improvement. The full tables, block intervals and raw observations belong to the
[tail record](performance-tail-v081.md), not a pooled speedup.

Each 256-request growing-session window has only three observations at or above
its empirical P99. Typing/render response is measured separately; burst enqueue
through completion is queue latency, not prompt readiness or independent service
time. All 6,144 REPL replies were checked and all six processes exited normally.

[CLI candidate 42863498904](https://github.com/kleedaisuki/msync/actions/runs/42863498904)
passes the eight selected Native contracts on Linux and Windows.
[Session candidate 42869625070](https://github.com/kleedaisuki/msync/actions/runs/42869625070)
passes the three changed session/HIR contracts on both platforms. Both include
the actual source editor host and Windows installer upgrade acceptance; neither
publishes or replaces the existing release. Exact package receipts remain under
`artifacts/candidates/tail-latency/9e5e997/` and `604b558/`.

## Lazy scalar Scope templates

A capture-free, straight-line lazy item producer that only constructs a
Scope from immutable scalar literals can share the immutable field array.
It still creates a fresh mutable Scope handle on every pull. This is not
memoization of the yielded object and does not force range enumeration.
The exact bound Module owns the recipe, and retained session images keep
the original path.

Shared literal rows use the same direct read as ordinary Scope rows.
A write-side marker detaches the field array under the first-write lock;
ordinary field reads do not pay a new dispatch test. Host record copies
still share the original instance's field storage, so their source first
detaches from other template-backed objects. Capture installation uses
the same write boundary. Malformed layouts, context-dependent literals,
callable state and actual effects remain on canonical demand.

On the same frozen standard polygon artifact, final Native AOT p50
changed 595.11 -> 399.26 ms (7/7 faster), managed allocation
35.22 -> 13.20 MB, and physical frame activations 196,614 -> 6.
The 196,608 logical producers and their original PCs remain observable.
See the [acceptance receipt](../../artifacts/benchmarks/literal-scope-v086/README.md).
