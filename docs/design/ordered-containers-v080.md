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
global instance registry or additional runtime type kind.

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

`storage::Buffer` supplies the small host-only capability needed by source
algorithms: indexed get/set, append/pop, bounded page insertion/removal, swap,
size, clear, and an eager membership snapshot. The object is an ordinary Scope
whose `items` field is a Range backed by an owned `List<Value>`. It has no global
handle table, manual lifetime protocol, or additional user-visible type kind.
Ordinary materialized Ranges do not become mutable.

Indexed access and replacement are O(1); append is amortized O(1). Insertion or
removal inside a Buffer shifts its suffix. Tree pages contain at most fifteen
keys, so those shifts are bounded independently of collection size. Heap
operations use only indexing, replacement, append, and final-element removal.
The host implementation reuses the platform's
[List indexing](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.item?view=net-10.0)
and [capacity growth](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.add?view=net-10.0),
not another custom allocator.

Compiler metadata distinguishes an owned buffer from an ordinary Range and
retains the inferred element schema. Buffer elements do not borrow a prototype
parameter's storage location merely because a prototype established their type.
Returned Scope values retain possible aliasing: two indices can contain the
same object, and an object can also be referenced outside the buffer. Flow
refinements are invalidated by potentially aliasing writes rather than treating
each retrieval as a fresh allocation.

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

Both trees use numeric arena edges. This keeps recursive graph identity separate
from recursively expanding structural Scope schemas. A node owns its indexed
key/value/child pages. The fixed minimum degree eight is a layout choice, not an
input quota. Node-local search is binary; moving page contents remains bounded.

The B-tree uses preemptive splitting and top-down deletion, including sibling
borrowing, merging, and predecessor/successor replacement. The B+ tree maintains
the minimum of the right subtree as each separator. Leaf overflow/underflow is
repaired upward; a changed minimum propagates only through first-child
ancestors. Its range iterator seeks once and follows leaf links rather than
searching from the root for every record. The mechanisms follow the standard
[B-tree occupancy and rebalancing analysis](https://opendatastructures.org/ods-cpp/14_2_B_Trees.html);
the [Rust BTreeMap contract](https://doc.rust-lang.org/std/collections/struct.BTreeMap.html)
provides a practical ordered-map and key-stability comparison.

Retired pages clear their key/value/child references before entering the free
list. Reuse preserves the owned node and its cleared Buffers rather than
allocating replacement objects on every delete/insert cycle. Capacity follows
the high-water number of live pages, not the total historical operation count.

`new(key_prototype, value_prototype)` establishes the fixed key/value schemas;
`with_policy` additionally selects ordering. `get` returns `Option?`, preserving
the distinction between a missing entry and an entry containing Null. Entries
are raw `{ key?: ..., value?: ... }` records. `range(low, high)` is half-open in
the selected comparator order. `height`, `page_count`, and `validate` expose
useful structural observations without changing ordinary query behavior.

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

### Managed integration observations

The implementation owners reused frozen adjacent Compiler/Core/Runtime images;
these observations are not Native AOT timing claims.

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
