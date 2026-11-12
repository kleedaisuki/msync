# Typed indexed storage and dynamic aggregate facts

Set, Heap, BTree, and BPlusTree now have noniterable storage owners. Set owns
typed red-black rows; Heap owns a dense vector; BTree and BPlusTree share a
stable page allocator and store dense page lanes in vectors. Their snapshots
publish a Range only at sequence boundaries. The mutable Range `Buffer` API,
its native operations and its executable projection/mirror path were removed.
Sections below that discuss Buffer record the previous implementation and
measurements, not a compatibility requirement.

The ordered containers need constant-time indexed reads and writes. Repeatedly
materializing or enumerating an ordinary Range would hide a linear factor in
heap operations and tree arena access. The old `storage::Buffer` therefore
owned an explicit mutable Range backing store. A Buffer instance was that
owned Range itself, not a Scope wrapping an `items` field. The named Buffer
family exposed native operations as function values, avoiding per-access
source wrapper frames. It did not introduce another language value kind or
a process-wide handle registry.

A snapshot copies membership, not the objects referenced by that membership.
Aliases of a stored Scope still refer to one Scope instance. Ordinary Range
materialization is unchanged. The compiler retains the inferred element type,
required Scope fields and callable signatures across indexed reads, and checks
replacement/insertion against that fixed schema. An empty untyped Buffer solves
its carrier on the first write; `Buffer::empty(prototype)` supplies it explicitly.
Only an actual top-level Path/String store conversion changes representation;
structural record checks do not silently rewrite nested field representations.

## Collection-owned storage: target architecture

A Range is a producer and its iteration context, not the mutable memory from
which a producer might read. An owned stream snapshot may use an array or a
typed vector as its private representation. It does not confer the right to
allocate rows, replace indexed values, or resize that vector. Collection code
does not obtain storage rights by constructing and probing a Range.

### Red-black Set owner and places

Each Set owns one arena with four scalar state slots: `root`, `count`,
`next` (the next unused index), and `free` (the head of the recycled chain).
Use one row-major vector of `(left, right, parent, color, key)` specialized
for the established key carrier. A node place is the arena plus a private
integer index. A Number row contains four consecutive Int32 lanes and one
binary64 key (24 bytes); managed keys are fields in a GC-visible typed row
array, not pointers hidden in raw bytes. A genuine nullable/union
key uses the corresponding value representation, not a second membership
tag. A present Null is an ordinary key. No node boxes, key Ranges, per-row
generation counters, or mirrored object vectors are needed.

Reserve index zero for black NIL; initialize `next` to one. Allocation pops
`free` using the retired row's `left` slot as its next link, or takes
`next++` and grows the vector geometrically. It overwrites all four
structural fields and stores the key before linking the row. Erasure unlinks
the row and completes balancing, drops the key reference if that lane carries
references, and writes the old `free` into the retired row's `left` before
publishing the new head. A non-NIL initialized row belongs to the tree or the
free chain; no separate occupancy array is required. Clear resets the root,
count, free chain and frontier, and drops the previously held key roots.
Growth and recycling do not change live indices. The executable lowers a
node field to base plus index times stride plus a fixed offset, with no
Range lookup, Scope handle, field-name dispatch, or guest Number-to-index
conversion for a rotation. Do not bit-pack the parent and color or add an
allocator size class unless measurements justify the added work.

This borrows the removal of an object indirection from
[Linux's intrusive rbtree](https://docs.kernel.org/core-api/rbtree.html) and
stable owner-relative numbering and free-slot reuse from
[SQLite's page freelist](https://www.sqlite.org/fileformat.html#the_freelist).
Neither raw pointers nor disk page semantics are required. The relevant
research comparison is
[Chilimbi, Hill and Larus's cache-conscious structure layout](https://research.cs.wisc.edu/arch/uwarch-wiki2/index.php/PubsUWArch/B2hd-DBLPconfpldiChilimbiHL99):
co-locate fields used together, and measure the actual dereference sequence
rather than assuming denser storage alone fixes traversal latency.

### Public values and effect boundaries

- `Set::node`, `tree::nodes`, `tree::free`, mutable internal links, and
  the NIL object's identity were observations of the unpublished
  representation, not collection contracts. The old
  [churn check](../../tests/OrderedRBSet/ChurnChecks.cs) and
  [escape journey](../../artifacts/benchmarks/direct-indexed-rows-v086/escape.msync)
  record what those objects did; they do not require view adapters, arena
  demotion, or a particular recycled node identity in the new artifact model.
  Inspect node structure in tests, not through a guest-visible Buffer.
  The Set itself still has identity: two aliases refer to one owner. A user
  key also keeps its original identity through insertion, lookup, removal,
  and snapshots; retiring an internal row never mutates the user key.
- The source algorithm and comparison policy remain authoritative. An
  arbitrary comparator may invoke effects or change reachable state. Do not
  pin a relocatable arena across an opaque callback; end the typed segment,
  run the selected callback once, then reacquire the live owner and any
  invalidated facts. A fixed, proven immediate comparator and private links
  need neither callback transport nor repeated admission. Preserve the
  evaluation order of selected policy effects and errors.
- `Set::snapshot` and `Set::range` still choose membership at the call.
  The half-open range may execute the selected comparator during that
  selection. Publish one privately owned result vector as a Range; do not
  defer selection to first iteration or copy a temporary Buffer's membership.
  Key object identities survive publication, while subsequent tree membership
  edits do not modify the saved sequence. The binary Heap needs a dense typed
  vector, not a row allocator or a free list.

### Integration order

1. In Typed HIR, distinguish the owner from a Range producer, node places
   from guest Scope values, and a typed key load from stream iteration.
   Reuse the same binding and effect facts for source and REPL. Public Set
   operations expose the owner and its keys, not row indices; the low-level
   arena operations can only address an index relative to its owner.
2. Portable code must express enough owner, layout, index and effect identity
   for binding without HIR/source. The executable chooses stride and offsets
   and records a typed owner lifetime for the whole admitted segment. Neither
   wire form contains a process pointer. The new artifact model does not
   recognize obsolete Set/Buffer producer patterns for compatibility.
3. Move allocation, insert/delete repair and search together onto this
   owner. Delete the `nodes`/`free` Buffer pair, per-node `payload` Range,
   public node views, and `Buffer::get -> Scope::member` call path. The old
   append-then-project worker admission, index-lane reprojection, and
   Set-specific handle materialization are unnecessary once the new owner
   takes over; do not delete shared machinery while other collections use
   it. `TypedRows` and `IndexedMethodPlan` demonstrate raw rows and typed
   workers, not a reason to retain the producer pattern.
4. Verify source and source-free `.mso`/`.mexe`: sorted
   membership and independent red-black invariants; warmed insert/delete
   churn and high-water retention; Number, String/Path and reference/Null
   keys; policy effects and order; Set and user-key aliases, clear, and
   membership snapshots; normal Windows behavior. Profile raw accesses and
   stores, actual Range publication and real process timings. The typed
   journey cannot publish node handles or per-key Ranges.

Process ownership is a separate consumer from iteration or snapshotting. The
ownership walker must visit managed references already present in an owner
(including a published row handle that can retain mutable fields), not call
an index accessor that materializes an otherwise unobserved scalar row.
The production walker now scans existing reference storage for ordinary
Buffers, packed Scopes and typed rows. A raw numeric extent has no ownership
roots; a typed row contributes only its existing published reference cache.
The [indexed root journey](../../tests/ReturnedProcesses/IndexedRootChecks.cs)
checks real Process escape through a Buffer and discriminates handle/Range
publication from numeric storage using the runtime's actual counters.

The source implementation now creates an opaque `Storage` owner in
`stdlib/arena.msync` and the runtime uses the typed row owner in
`SetArenaOperations.cs`. The Set algorithms retain their source comparison
and balancing order; they call direct row primitives, not Buffer or Scope
member operations for nodes. A separate typed vector receives the selected
keys and transfers its storage to a Range at the public output boundary.
The Number row ABI is defined by `ArenaNumberRowLayout`. At a canonical
write, root/parent/child values are admitted against the live frontier.
An executable that borrows the Number rows may rely on that invariant,
provided it stores only proven row indices and ends its borrow before a
selected callback or a resize.

## Runtime carrier resolution

An initially untyped empty Buffer starts with a `Never` item carrier. Its first
successful write resolves the carrier on the existing Range object; aliases,
including those retained between REPL submissions, observe the same solution.
Clearing the contents does not reopen that solution. A snapshot made before the
write remains an independent empty `Range<Never>`; subsequent snapshots publish
the solved carrier.

`RangeValue.ItemType` remains an ordinary backing-field getter. No dynamic type
provider, virtual call or additional field is added to ordinary Range reads.
Only Buffer writes may perform the one-time carrier solution. An owned buffer
retains its immutable initial carrier for equality and hashing through
`IRangeTypeOrigin`; that interface is consulted only by Range equality/hash,
never by iteration or item-type access. Existing ordinary Range descriptor
comparisons are preserved, ownership-retention bookkeeping is excluded, and a
live Buffer stays in its original hash bucket after inference.

The compiler also records eager Buffer snapshot calls with point-in-time semantic
argument metadata. Later solving the source Buffer's inference cell must not
retroactively change the result contract of an earlier immutable snapshot.

## Typed HIR resource domains (0.8.1)

A flow join of independently allocated Buffer values retains a finite set of
possible resource identities. It does not allocate a merged backing store or
erase the indexed-storage capability. All non-null alternatives must be owned
Buffers; joining an ordinary Range does not grant that capability.

A derived Range preserves its element schema and referenced objects, not the
input Buffer's backing-store identity. `NativeBehavior.PreservesIdentity`
records the stronger claim only for actual identity operations. Similarly,
consuming an outer Range must not discard the pending effects of an inner Range
returned as an element; those effects belong to the later inner enumeration.

Element reads join the possible values. Writes satisfy every possible target's
fixed carrier, including constraints established on different control-flow
edges. Indexed effect summaries retain these carrier constraints separately
from their resulting element type: a read union is not a writable constraint.
Reifying a live resource's current element type does not rewrite the binding's
fixed storage type or an earlier membership snapshot.

When a resource view can select Path-backed or String-backed storage, the
runtime performs the top-level implicit conversion against the actual target.
Path-to-String uses `NativeText`; String-to-Path retains the runtime directory
and home context. Normal singleton-target compile-time conversions remain in
place. Neither conversion changes the resource's equality/hash identity.

## Values are not prototype locations

A prototype is used to infer an element schema, not to identify the location of
an eventual stored element. In particular, a Number copied from a nullable
parameter is a Number value, not an alias of that nullable parameter's storage.
Keeping its `Info.Storage` caused a real B-tree range traversal to acquire a
spurious `Number?` child index after other generic calls. Indexed element
construction removes that top-level value-flow location. Scope field locations
and the Buffer's own callable element domain remain explicit.

Nor can one prototype Scope identity stand for every concrete stored object.
After an object escapes into indexed storage, the compiler uses a dynamic-memory
view: its field types remain available, but its initial scalar values are no
longer permanent facts. Joined element schemas retain provenance to their input
Scopes, so mutation through a selected element also invalidates stale facts on
an original source alias. Each selected aggregate read has its own abstract read
identity; that is not a claim that the runtime objects are disjoint.

## Flow proofs and callable contracts

Memory versions are immutable tokens in `Frame.Facts`, rather than one mutable
global counter. Control-flow forks keep their own tokens. A possible indexed or
escaped-field write changes the token and invalidates earlier member-value
refinements. An unmodified alternative branch retains its refinement. A cached
function specialization's memory effect produces a fresh token on each call,
so a second invocation cannot accidentally reuse the first invocation's proof.
These are compiler facts only; no version checks are added to executing bytecode.

Actual function values retain their runtime identities and captures. A mutable
callable element uses the established callable-storage domain and dispatch
machinery. A signature's declared callable parameter determines whether a native
operation invokes a callback; passing a Function as a Buffer element does not
invoke it merely because the first argument is a Range.

Possible Scope field locations connected by indexed selection also retain
concept contracts. These memory-alias links are separate from ordinary callable
value-flow links. Checking a selected function field against a concept cannot
subsequently be invalidated by assigning an incompatible function through an
original object alias. The ordinary non-indexed path does not traverse an alias
graph when there are no such links.

An already checked concept also retains its associated-type proof when a
dynamic read can no longer treat an ordinary Type-valued witness field as a
constant. The required member storage contracts still enforce that proof; an
unrequired mutable witness is not a runtime type declaration. This keeps, for
example, a present-Null Option distinct and well typed after indexed storage.

Failed submissions restore newly exposed views, first-write element solutions
and alias links. Editor and metaprogramming-query snapshots copy the mutable
metadata graph, including shared storage domains and alias links. A failed or
successful `is_invocable` probe cannot constrain a later real Buffer write.

## Focused integration evidence

`tests/IndexedStorage` executes the production compiler, binary artifact codec,
context binder and interpreter. Its source/serialized `.mso`/serialized `.mexe`
cases at O0/O2/O3 distinguish independent elements, repeated-index aliases,
original aliases, nested objects, callable replacement, nullable member proofs,
snapshot membership and actual Path/String conversion. Negative cases cover
fixed carrier/field schemas, invalidated null proofs and concept-violating alias
writes. A selected metadata-query case verifies both rejection and isolation of
the indexed callable-alias contract; a session case checks failed first-write
rollback. Ordered-container owners additionally exercise the complete source
algorithms against their independent models.

## Experimental packed Number storage (0.8.6)

A scratch runtime used contiguous binary64 storage for an exact Number Buffer
and passed raw values through ready `buffer_get`/`buffer_set` operations. The
same live owner widened to ordinary Value storage on a mismatching dynamic
write. Its root view stayed stable across widening because Range adapters can
retain that view before a future reference appears. Membership snapshots and
IEEE payloads remained exact. This was a runtime representation experiment,
not a new source type or artifact format.

A live one-million-number Buffer retained about 8.39 MB instead of 32.38 MB
in a managed component probe. On frozen B+ inventory and Heap scheduler
artifacts, however, native layout savings coexisted with increased transient
boxing and allocation at generic locals and argument boundaries. The Heap
artifact materialized every ready packed read. Small and noisy latency changes
do not justify enabling this representation by default. Reassess with typed
local/argument transport, and require an actual retained-size workload before
adding an explicit dense storage API. See the
[raw evidence and reproduction](../../artifacts/benchmarks/packed-number-buffer-v086/README.md).

The [typed-local follow-up](../../artifacts/benchmarks/packed-number-buffer-v086/typed-locals/README.md)
reduced Heap's additional packed boxes to about 8,200 on the same artifact.
However, admitting BufferGet-driven locals shifted B+ materialization into
`LoadStorage -> MakeScope` and return; it added about 1 MB of transient
allocation without meaningful box savings. A short Heap frame's 104-byte
Number sidecar also costs more than its four saved 24-byte wrappers. The
next targets are proven non-Cell raw scope-operand transport and cheaper
short-lived sidecars. Keep packing and the broader admission disabled.

A [compact numeric suffix follow-up](../../artifacts/benchmarks/packed-number-buffer-v086/compact-suffix/README.md)
on the Scope transport source verifies that the B+ scope path can now
consume about 9,900 unboxed storage loads. Compact sidecars save about
0.61 MB in B+ and 0.36 MB in Heap versus the full sidecar under packing,
but the composed path still allocates more than the ordinary Buffer
control. Without packing, compaction independently saves about 0.46 MB
and 0.20 MB respectively with unchanged logical counts; wall timing is
mixed. The layout must retain any raw borrowed argument preceding the
writable suffix when detaching. This is an allocation lever to evaluate
separately, not a reason to enable packed Buffer.

Production now uses the compact writable suffix for already admitted private
numeric frames, preserving any live lower raw argument during detachment.
It does not broaden admission or change Buffer backing storage. Profile
metrics expose allocated sidecar arrays and slots. On the latest Scope
runtime comparison, B+ and Heap allocated 455,432 and 196,232 fewer bytes
respectively in both tested seeds with unchanged boxing and logical paths.
The separate offsets increase managed frame-state size; non-hit timings
are noisy and are reported separately from the allocation result. See the
[production compaction receipt](../../artifacts/benchmarks/packed-number-buffer-v086/compact-suffix/product/README.md).


The [direct packed local-sink experiment](../../artifacts/benchmarks/packed-number-buffer-v086/local-sink/README.md)
passes a ready packed read straight to an admitted numeric local instead of
spilling through operand stack scratch. Despite roughly 9,900 B+ and 69,900
Heap hits per seed, allocations and box/sidecar counts are unchanged versus
plain packing, and the sink is not faster. Direct measurement shows the same
operand-stack array counts with or without the sink; the extra stack arrays
from packing account for about 75% of B+'s transient allocation penalty.
Packing with the prior admission
remains workload-dependent: Heap CPU moves down modestly on repeat, but adds
about 0.39 MB of transient allocation; B+ adds about 1.62 MB with mixed CPU.
The million-number control still retains 74.1% less managed memory. Do not
enable packing or revisit a single local sink on these artifacts. Reopen for
an actual retained-memory constraint or a typed collection region that
avoids multiple generic transport allocations together.


The [activation-window follow-up](../../artifacts/benchmarks/packed-number-buffer-v086/activation-window/README.md)
removes four-double operand arrays across several consumers, not just one
sink. The inline window saves up to 2.87 MB in packed B+ and 1.84 MB in
packed Heap, but widens the disabled async frame from 336 to 432 bytes
and usually raises CPU time. An eight-entry inactive-array cache saves
the same allocation without growing the frame, including about 14.68 MB
in the non-Buffer Signal workload; its repeated CPU results are mixed.
Both changes save allocation independently of packing, which still adds
about 0.40 MB in B+ and 0.16 MB in Heap against the same scratch layout.
Keep default storage unchanged. A separate lease experiment is warranted
when a long-running workload shows GC or tail-latency pressure, not as a
prerequisite for packing.


A [checkpointed Signal GC-pressure experiment](../../artifacts/benchmarks/packed-number-buffer-v086/activation-window/gc-tail/README.md)
verifies that leasing has an actual GC benefit, not just a byte-count
benefit. With 262,144 samples under a verified 16 MiB managed heap cap,
Gen0 collections drop from 116 to 71 and total pause from 70–87 ms to
45–49 ms. Across ten capped pairs, delivered p99 is essentially unchanged
for one seed and improves 7.8% for the other. The default-heap latency
results remain mixed. Keep the lease separate from the product default
and from packed Buffer; revisit only with a measured deployment GC/tail
constraint and after checking overlap with the chosen typed batch.
