# Source-backed collection families in 0.8

## Data, operations and policy ownership

`$std::containers::{List, Map, EqualitySet, Stack, Queue}` are named Scope
families. Their reusable associated functions take the instance explicitly:

```msync
let values = $std::containers::List::new([1, 2]);
let alias = values;
$std::containers::List::push(alias, 3);
let snapshot = $std::containers::List::snapshot(values);
```

Instances contain typed Range storage and policy values, not captured getter
or method tables. Family code and named default policies are shared, while
anonymous instance records are fresh. Aliases observe the same mutable Scope;
their storage schemas remain fixed. No additional collection TypeKind is needed.

## Construction and fixed element schemas

`new(initial)` determines element schemas from the supplied Range. Empty
constructors accept ordinary prototypes without inserting them:

```msync
let numbers = $std::containers::List::empty(0);
$std::containers::List::push(numbers, 1);
let words = $std::containers::EqualitySet::empty("");
let counts = $std::containers::Map::empty("", 0);
$std::containers::Map::insert(counts, "readme", 1);
let optional = $std::containers::Map::empty_nullable("", 0);
$std::containers::Map::insert(optional, "readme", null);
```

Prototype arguments evaluate once. Their inferred types determine storage;
passing Null alone does not create a future arbitrary type. Explicit nullable
storage admits Null in addition to its concrete mapped element. For direct
Range composition, `List::new([] as Range<Number>)` is supported. A concept is a
constraint, not a constructor for a new row: `Range<Entry>` does not manufacture
missing entry locations.

Mutation publishes a materialized replacement Range, not a growing chain of
lazy transforms over the location being replaced. Source functions returning
ordinary Ranges retain demand-driven execution; explicit `materialize` preserves
its eager semantics through wrappers. `snapshot` and Map's `entries`, `keys`
and `values` freeze membership but preserve element object identity. Map owns
separate typed key/value columns and creates fresh entry records instead of
mutating caller-owned entries.

## Equality and storage policies

`RangeStorage` requires `snapshot(Range) -> Range`; default `SnapshotStorage`
materializes the selected input. Custom storage can trace or change snapshot
representation while preserving ordered contents. Mutations eagerly admit the
policy's snapshot so a source Range policy cannot accidentally install a
self-referential lazy update.

`Map::with_equality` and `EqualitySet::with_equality` accept an `Equality` policy.
Its associated `Self` and first-order `equal(Self, Self) -> Bool` signature are
compiler checked. `with_policy` selects equality and storage together. Actual
policy callable identities specialize normally, with no global equality
registry or runtime mode switch. Null admission and comparison remain explicit.

Map retains first-key position and the latest mapped value for equivalent keys.
`insert` reports a new key; `try_insert` does not overwrite. Membership, rather
than a nullable value check, distinguishes an absent key from a present Null.
`EqualitySet` preserves first insertion order and uses its explicit equivalence
relation. These snapshot-based families do not claim logarithmic operations.

## Associated operations

| Family | Principal operations |
| --- | --- |
| `List` | new, empty, with_storage, push, extend, get, get_or, set, insert, remove, remove_where, contains, index_of, slice, reverse, sort_with, snapshot, size, is_empty, clear |
| `Map` | new, empty, empty_nullable, with_equality, with_policy, entries, keys, values, contains_key, get, get_or, insert, try_insert, remove, size, is_empty, clear |
| `EqualitySet` | new, empty, with_equality, with_policy, contains, insert, remove, union_with, intersect_with, except_with, is_subset_of, overlaps, snapshot, size, is_empty, clear |
| `Stack` / `Queue` | new, empty, with_storage, push, peek, pop, snapshot, size, is_empty, clear |

Index/count inputs follow the Range library's finite integral Number rules.
Absent get/peek/pop results are Null. List indexed mutation returns false for an
out-of-range index; `remove_where` returns a count. Empty stack peek does not
form a negative drop count.

## Ordered families

Canonical `Set` is a red-black tree using `StrictOrder`, not the linear
equality-based family. BTree, BPlusTree and Heap are distinct ordered structures,
not aliases for sorting a Range. `NaturalOrder` gives Null and NaN an explicit
container order without changing language-level IEEE comparisons. Their source
algorithms and indexed-storage boundary are recorded with the ordered-structure
implementation; `EqualitySet` remains available for arbitrary equivalence
relations that are not orderings.
See [the ordered-container contract](ordered-containers-v080.md) for node
ownership, indexed storage, complexity and algorithm-specific observations.

```msync
let ordered = $std::containers::Set::new([3, 1, 2]);
$std::containers::Set::insert(ordered, 4);
$std::containers::Set::remove(ordered, 2);
let ascending = $std::containers::Set::snapshot(ordered);
```

The unpublished callable factory module has been deleted. There is one current
Scope-family API, without a second compatibility vocabulary.

## Focused observations

The snapshot-based family owner accepted nine actual source/object/executable
routes at O0/O2/O3 with fixed-schema, alias, policy and mutation checks. The
final unpublished-adapter removal accepted three O2 routes with 39 exact byte
observations and three useful signature/storage rejections. Its fixture is
`tests/Msync.EndToEnd/collections_policy_journey.py`; final Native cross-platform
acceptance belongs to the delivery record. Ordered-family evidence is distinct
from these snapshot-based observations.
