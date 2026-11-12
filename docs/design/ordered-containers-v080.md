# Source-defined ordered containers (v0.8.0)

## Representation and policy boundary

The canonical families are `containers::BTree`, `BPlusTree`, `Set`, and `Heap`
under `$std`. They are named Scope declarations containing shared associated
functions. Each instance is an anonymous data Scope. Operations receive `self`
explicitly; nodes and instances do not carry a repeated collection of method
closures. `Set` is the red-black tree, while `EqualitySet` is the separate
insertion-ordered, equality-policy collection with linear search.

`StrictOrder` relates a carrier exemplar to a comparison operation:

```msync
concept StrictOrder {
    sample?: typename Self,
    less: fn(left: Self, right: Self) -> Bool,
};
```

The exemplar retains the structural row for record keys; a bare Type value
denoting `Scope` cannot describe its fields. A Type-valued `Self` can additionally
supply the carrier of an empty scalar collection. These are compiler-checked
capability and schema constraints, not proofs of transitivity or comparator
stability. Functions specialize normally for the supplied policy. There is no
global instance registry; the internal `Storage` kind describes an owned
collection allocation, not an additional user-declared type or a Range.

The default `NaturalOrder` is Null-first. For Number it places all NaNs in one
final equivalence class and treats the two zeros as equivalent. This is an
explicit container policy, not a change to IEEE language comparisons. Text and
Path follow the established Unicode-scalar lexical comparison of their native
text; Path values retain their endpoint provenance, but that provenance is not
part of the language's lexical equality or this default ordering. A custom
policy can choose a different equivalence relation.

The normal numeric path uses raw `<` and `>=` operations. Only an unordered
comparison reaches the NaN fallback; an ordinary key comparison does not pay
for two native `is_nan` calls and their argument arrays.

Keys and values retain their normal identity. Snapshot operations freeze
membership, not the contents of referenced Scope objects. Comparator-visible
key fields and policy behavior must remain stable while keys are indexed;
remove and reinsert a key when changing its ordering. The implementation does
not deep-copy keys or repeatedly validate this contract on ordinary operations.

## Indexed storage without an open type universe

`Storage` is a noniterable implementation capability. The owning collection
chooses a representation instead of making mutable memory look like a Range:
Heap and bounded page sequences use dense vectors, red-black Set uses its
specialized fixed-stride rows, and B/B+ allocate pages from stable slots. The
ordinary collection API remains a named Scope with explicit instance
arguments. An owner-relative page index never denotes a Range position,
process pointer, or entry in a global handle table.

Dense indexed access and replacement are O(1), and append is amortized O(1).
Insertion/removal shifts the suffix of the same vector. Tree pages contain at
most fifteen keys, so shifts are bounded independently of collection size.
Numeric vectors store binary64 directly and reuse the platform's
[`List<T>` indexing and growth](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1?view=net-10.0).
Slots preserve live page indices across growth and reuse an intrusive free
chain; they do not shift later pages on release.

The compiler keeps a fixed element carrier separately from the dynamic
elements observed in an owner. A Type witness establishes the carrier, not a
hypothetical resource identity for the value's fields. The first allocation
of a page supplies the actual nested vector aliases. A retrieved Scope or
key/value retains its possible aliases; potentially aliasing writes invalidate
affected flow facts rather than treating each retrieval as a fresh object.

Native effect summaries model indexed contents as a memory resource, including
reads, writes, stored references, and returned aliases. These operations are not
pure compile-time constants. Local memory operations still execute in dry-run
mode and do not join unrelated filesystem or process work. A snapshot copies
membership immediately and retains ordinary element identity.

## Algorithms and costs

| Family | Actual structure | Point operations | Traversal and construction |
| --- | --- | --- | --- |
| `BTree` | Degree-eight B-tree; keys and values in internal and leaf pages | Find, insert, remove O(log n) | Entries O(n); half-open range O(log n + k) |
| `BPlusTree` | Degree-eight B+ tree; values only in leaves, linked leaf chain | Find, insert, remove O(log n) | Entries O(n); seek and leaf-chain range O(log n + k) |
| `Set` | Red-black tree with a shared black NIL node | Contains, insert, remove, bounds and endpoints O(log n) | Snapshot O(n); range O(log n + k) |
| `Heap` | Array-backed binary min-heap | Peek O(1); push, pop, replace O(log n) | Floyd heapify O(n); drain/sorted O(n log n) |

Here `k` is the number of selected entries and comparison cost is separate.
The B/B+ tree and Heap `validate` operations are explicit inspections, not a
cost paid by every query. The Set journeys independently inspect its arena and
red-black invariants rather than relying on a product validator.

### B-tree and B+ tree

Both trees use owner-relative numeric page slots. This keeps recursive graph
identity separate from recursively expanding structural Scope schemas. The
slot arena owns allocation, live count and recycling; each occupied slot
holds a page Scope with separate noniterable vectors for keys, values and
child indices. The fixed minimum degree eight is a layout choice, not an
input quota. Node-local search is binary; moving page contents remains
bounded. [SQLite's numbered B-tree pages and free list][sqlite-pages] use the
same separation between an index and its current allocation, without implying
any disk format or pager policy for msync.

The B-tree uses preemptive splitting and top-down deletion, including sibling
borrowing, merging, and predecessor/successor replacement. The B+ tree maintains
the minimum of the right subtree as each separator. Leaf overflow/underflow is
repaired upward; a changed minimum propagates only through first-child
ancestors. Its range iterator seeks once and follows leaf links rather than
searching from the root for every record. The mechanisms follow the standard
[B-tree occupancy and rebalancing analysis](https://opendatastructures.org/ods-cpp/14_2_B_Trees.html);
the [Rust BTreeMap contract](https://doc.rust-lang.org/std/collections/struct.BTreeMap.html)
provides a practical ordered-map and key-stability comparison.

Retiring a page releases the arena's reference to it and links its index into
the arena's free list. Reuse installs a fresh page at that index; retaining a
stale page identity is not part of the map contract. Capacity follows the
high-water number of live pages, not the total historical operation count.
`clear` resets the same arena to one root, so aliases of the collection still
observe the cleared map. Sibling merges read occupied vectors directly.
Entry and range collection build one output vector and transfer its membership
to the published Range, without exposing the mutable owner or copying element
objects.

[sqlite-pages]: https://www.sqlite.org/fileformat.html#the_freelist

`new(key_prototype, value_prototype)` establishes the fixed key/value schemas;
`with_policy` additionally selects ordering. `get` returns `Option?`, preserving
the distinction between a missing entry and an entry containing Null. Entries
are raw `{ key?: ..., value?: ... }` records. `range(low, high)` is half-open in
the selected comparator order. `height`, `page_count`, and `validate` expose
useful structural observations without changing ordinary query behavior.
The [nullable B+ journey](../../tests/Msync.EndToEnd/bplus_nullable_journey.py)
checks a present Null key and value through leaf splitting, removal, and a
published half-open snapshot on source, portable, and bound execution paths.

### Red-black Set

The Set uses stable numeric arena indices, parent/left/right edges, red/black
state, and one black NIL node. Rotations and insertion/deletion repair share a
direction parameter instead of duplicating the mirror cases. Removed slots are
recycled, their payload references are released immediately, and their owned
Scope records can be reused. The design shares the balancing invariant of the
[Linux rbtree interface](https://docs.kernel.org/core-api/rbtree.html), but the
implementation is source msync code rather than copied C or an opaque host Set.

`insert` reports whether a new comparator-equivalence class was added. `get`,
`lower_bound`, `first`, `last`, `pop_first`, and `pop_last` return `Option?` so
Null keys can still be present values. `snapshot` is ordered. A different
comparison policy, such as case-folded String order, changes equivalence without
changing the tree algorithm.

### Heap

The comparator defines priority: `less(a, b)` means `a` is removed before `b`.
A reverse comparison policy gives a max-heap. Equal-priority elements are not
stable. Sifting keeps one value in a hole, moves ancestors or children, and
writes the saved value once, reducing storage writes compared with repeated
swapping. Bottom-up construction is Floyd heapification, not repeated sorting;
this is the same practical mechanism used by the platform
[PriorityQueue implementation](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Collections/src/System/Collections/Generic/PriorityQueue.cs)
and the [Rust BinaryHeap family](https://doc.rust-lang.org/std/collections/struct.BinaryHeap.html).

Heap owns a dense vector with a fixed element carrier; there is no mutable
Range or parallel boxed mirror. A Number vector starts with a contiguous
`List<double>`, even when empty. An explicit Type witness reserves that
owner directly and neither runs nor specializes bottom-up heapification.
A real Range is consumed once before selected comparisons; a primitive
Number producer copies its unboxed elements into the vector. Other
carriers preserve original value references. Push and pop mutate the
tail, and growth never changes the owner object seen by aliases.
If a Range already retains an unresolved Number from a live Process,
construction retains that reference instead of joining its producer
early. Normal vector writes do not widen a primitive Number lane. The
[gated child journey](../../tests/ReturnedProcesses/HeapDeferredChecks.cs)
requires the Heap and its snapshot to return before the caller supplies
the child's input, then checks both selected Numbers and the child output
across source and both artifact stages.

The native writer borrows the vector's current primitive extent after
proving indices and selecting a callback-free method. No resizing or
suspending call may execute inside that borrow; this is also the contract
of [`CollectionsMarshal.AsSpan`](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.collectionsmarshal.asspan).
When child selection calls a user comparator, the source reads the
selected child's live value afterward. There is no library-level
leaf-callable probe and no fork between two different sift algorithms.
The executable optimizer can inline proved immediate comparison
policies. Typed basic-block parameters distinguish Boolean short-circuit
joins of an inlined comparator from numeric child/value joins; neither
shares a global register indexed by operand stack depth.

`snapshot` is the first conversion from the owner to a Range and copies
membership, not key objects. `drain` allocates a same-carrier private
vector, fills it in removal order, and transfers its backing extent
directly to an immutable Range. It does not take an intermediate snapshot
to recover the carrier, or copy the completed output a second time.
The [dense-owner journey](../../tests/Msync.EndToEnd/heap_storage_journey.py)
checks each pop and saved member over source, detached object, detached
executable, and JIT-off execution. Its disassembly rejects a Buffer call
in the new image, and the optional native assertion requires actual
worker stores.

On the frozen Linux x64 `arena-vector` stage, that test observed 2,016
native worker entries and 18,917 machine stores. Replacing the policy
after warm-up still produced every expected key, the unchanged heap-order
snapshot and exactly 8,806 selected comparisons; its JIT-off output was
byte-identical. An [8,192-key paired replay](../../artifacts/benchmarks/container-representation-v086/heap-dense-owner-timings.json)
used nine alternating fresh processes and exact independent output
oracles, with each host compiling the same public-API source against
its own standard module. The older Buffer host and the combined
dense-owner stage measured median bound wall time 226 → 203 ms and
direct-source wall time 345 → 294 ms. These are end-to-end differences,
not an attribution to the allocator alone.
After the shared structural Scope projection fix, the full managed Heap
model passed six O0/O2 staged journeys with 96 mutations and 18 exact
outputs each, including a retained reference key, nullable extension,
custom comparison, and three selected negative source diagnostics.

`snapshot` preserves heap order. `sorted` drains a copy; `drain` consumes the
instance. Empty `peek`/`pop` return Null; with nullable payloads, inspect
`is_empty` to distinguish an absent element from a present Null. `replace_top`
on an empty heap inserts and returns Null. `push_pop` on an empty heap returns
its argument without changing the heap; its ordinary fast path can return in
O(1), otherwise it sifts in O(log n).

## Focused product journeys

`tests/Msync.EndToEnd/ordered_structures_journey.py --binary /absolute/msync`
selects only these changed contracts at O2. It composes the B/B+ map-model
journey, red-black Set invariant journey, and heapq-model journey. All execute
the real CLI, serialized `.mso`, and bound `.mexe`; binary stages remove the
source and the executable stage uses a different launch directory.

The owner tests additionally inspect occupancy, leaf depth and leaf links;
black height, parent edges, NIL and free-slot ownership; stable snapshots,
custom record/String policies and NaNs; and heap construction/comparison counts.
Targeted churn checks observe arena high-water capacity, immediate payload
release, and reuse of owned node records. These are incremental container
checks, not a repeated full language or installer regression.

### Slot-owner B/B+ acceptance

The [B/B+ journey](../../tests/Msync.EndToEnd/btree_bplus_journey.py) drives
745 independent model mutations per family through source, detached `.mso`,
and detached `.mexe`. In addition to the map and page invariants it checks
retired slot reuse and reset directly, and verifies that the artifacts call
the slot allocator and vector primitives rather than the removed Buffer
operations. All [six managed routes](../../artifacts/benchmarks/direct-indexed-rows-v086/slot-owner-btree-bplus-managed.json)
passed. The [nullable B+ journey](../../tests/Msync.EndToEnd/bplus_nullable_journey.py)
separately distinguishes a present Null from a missing key and checks that a
range snapshot outlives the deletion of its Null key.

The [3,072-transaction B+ inventory pairs](../../artifacts/benchmarks/direct-indexed-rows-v086/slot-owner-bplus-inventory.json)
compile and bind the same source independently for two Linux x64 Native AOT
hosts. Nine alternating pairs on one logical CPU ran the checked workload
with wall medians 292.0 → 260.1 ms and process CPU medians 205.8 → 192.1 ms.
This measures the combined slot/vector checkpoint, not an isolated allocator
microbenchmark. The comparison used the numeric workload, not the subsequent
non-null structural witness correction.

### Historical managed integration observations

The implementation owners reused frozen adjacent Compiler/Core/Runtime images
from before the slot-owner migration; these observations are not Native AOT
timing claims or requirements to retain the earlier objects.

| Changed mechanism | Accepted observation |
| --- | --- |
| B/B+ algorithms | Each family passed a 745-mutation independent map model, with occupancy/order/leaf-depth checks after every mutation, ordered/range results, case-folded and record policies, NaN/zero behavior, and root collapse |
| B/B+ typed artifacts | Four O2 `.mso`/`.mexe` routes compiled the exact updated class declarations as ordinary source, deleted that source, and verified eleven outputs per route; canonical module source entrypoints were checked separately |
| Reused B/B+ pages | 64 inserts, deletion of every key, and 64 further inserts retained arena capacity and repopulated the same owned Scope/Buffer pages |
| Red-black Set | 602,685 model and structural assertions across O0/O2/O3 and source/`.mso`/`.mexe`; the independent walker checks black height, parent edges, ordering, cycles and free-slot partitioning |
| Set payload lifetime | A separate three-stage O2 churn check passed 165 assertions over five cycles of sixteen record keys: capacity seventeen including NIL, immediate payload release, owned Scope reuse, input demand once, and unchanged external key objects |
| Heap | Nine O0/O2/O3 staged routes, 96 heapq-model mutations and validation after each mutation, sixteen exact outputs per route; three additional O2 routes checked nullable payload behavior |
| Default order | 81 pair comparisons covering Null, infinities, finite values, signed zeros and NaNs matched an independent model at O0 and O2 |
| Combined example | The real O2 CLI produced `10`, `23,24,25,26`, `Ada,Klee`, and `config,notes,archive` from `examples/ordered_containers.msync` |

The full platform delivery consumes the canonical embedded module through
`ordered_structures_journey.py`; it does not infer installed-module acceptance
from source-prefix probes or repeat every local assertion matrix. The detailed
image identities and final platform results belong to the delivery record.

### Cross-runtime cost observation (0.8.6)

The [library matrix](../../tests/Performance/LibraryMatrix/README.md) separates
compile time from checked insertion, search and heap-drain work. With 8,192
Number keys the source-language Set has tens of seconds of execution cost even
from a pre-bound image. This is not a balance-invariant failure or evidence of
a different asymptotic complexity. The current search path obtains every key
through an indexed Scope and a single-element payload Range and invokes a
comparison policy. Profile those individual steps before redesigning the tree.
A typed fast path must still preserve comparator equivalence, nullable keys and
ownership of arbitrary payloads. The simple benchmark-only linear-probing
table is not an API replacement: it has no resizing, deletion or general key
semantics.

The subsequent [continuation ablation](contextual-io-backend.md#compute-only-continuation-admission-086-investigation)
locates the dominant cost above the container representation. With that
planning cost experimentally bypassed, a checked 1,024-key Set control still
takes about 0.51 s. A local copy of the stock implementation takes 0.54 s,
substituting direct finite Number comparisons takes 0.37 s, and also storing
the key as a scalar instead of a singleton Range takes 0.30 s. Those source
overlays isolate two further costs on this workload; their simpler comparison
and storage are not semantically interchangeable with the generic library.
Keep NaN/null/custom comparator behavior and general payload ownership in the
design of any typed fast path.
