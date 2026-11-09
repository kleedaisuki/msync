# Source-backed ordered collections in 0.8

`$std::collections` provides `List`, `Map`, `MapWith`, `Set`, `SetWith`,
`Stack`, and `Queue`. They are normal generic source functions returning fresh
anonymous Scope objects. Methods close over shared mutable state. Copying a
collection Scope therefore shares its mutations; calling a factory again creates
an independent collection. No additional runtime collection type is introduced.

## Ownership and storage schema

Every constructor consumes an initial Range to determine its element schema.
Prototype factories hide the empty Range/record seed and do not insert a sample:

```msync
let numbers = $std::collections::EmptyList(0);
numbers::add(1);
let words = $std::collections::EmptySet("");
let counts = $std::collections::EmptyMap("", 0);
counts::put("readme", 1);
let optional = $std::collections::EmptyNullableMap("", 0);
optional::put("readme", null);
```

The prototype arguments are ordinary values evaluated once, not unevaluated Type
descriptors. Their inferred types fix the new collection's schema. Passing Null
alone therefore establishes Null-only storage, not a future arbitrary type.
`EmptyNullableMap` explicitly admits Null in the mapped value while preserving
the concrete value prototype's type; it does not make keys nullable.

`EmptySetWith(prototype, equal)` and
`EmptyMapWith(key_prototype, value_prototype, equal)` retain a custom equivalence
relation. `EmptyStack(prototype)` and `EmptyQueue(prototype)` follow the same
storage policy. These are source compositions, not additional native types.

For direct Range composition, `List([] as Range<Number>)` is also supported.
`take(existing, 0)` preserves an existing richer element schema. A concept such
as Entry is a constraint, not a constructor for a new Scope row layout; casting
an empty Range to `Range<Entry>` does not invent key/value locations.

Inferred storage types remain fixed across
method calls. Collections are not untyped bags merely because methods are
first-class Function values. A constructor infers storage from the values it
consumes, not from permission to assign a different value to a caller's original
location. For example, reading a currently nonnull nullable Scope field can
produce a Number-only Range. To construct nullable mapped storage, supply an
actual nullable element union (or an explicitly typed Range seed), rather than
assuming a copied scalar retains the admission rules of its former location.

Mutation publishes a materialized replacement Range, not a growing chain of
lazy transforms. `materialize` is an explicit eager operation, including when
returned from a source function: the compiler carries its EagerRange result
policy through the callable. A normal source function returning a lazy Range
remains deferred. This distinction is essential: storing a deferred
`concat(state::items, ...)` back into `state::items` makes its later read observe
itself. Stable snapshot methods therefore return explicit `materialize(...)`,
not a bare captured field. `snapshot()` and Map's `entries()` return a stable shallow
snapshot; a later collection mutation cannot change its membership. Elements
that are Scope objects preserve their normal identity. Map owns separate key and value snapshots. Its `entries()` method constructs
fresh entry records instead of mutating caller-owned entries. Separating the
columns also keeps a stable scalar storage schema; repeated row-transform
replacements needlessly grow compiler shape joins.

## API families

| Factory | Operations |
| --- | --- |
| `List(initial)` | snapshot, size, is_empty, clear, get, get_or, add, add_all, insert, set, remove_at, remove_where, contains, index_of, slice, reverse, sort_with |
| `Map(initial)` / `MapWith(initial, equal)` | entries, keys, values, size, is_empty, clear, contains_key, get_or, put, try_add, remove |
| `Set(initial)` / `SetWith(initial, equal)` | snapshot, size, is_empty, clear, contains, add, remove, union_with, intersect_with, except_with, is_subset_of, overlaps |
| `Stack(initial)` | snapshot, size, is_empty, clear, push, peek, pop |
| `Queue(initial)` | snapshot, size, is_empty, clear, enqueue, peek, dequeue |
| Empty factories | EmptyList, EmptySet, EmptySetWith, EmptyMap, EmptyMapWith, EmptyNullableMap, EmptyStack, EmptyQueue |

Map input entries have fields `key` and `value`. Duplicate keys keep their first
position and receive the last value. `put` reports whether a new key was added;
`try_add` does not overwrite. `get_or` distinguishes a missing entry from a
present nullable value by inspecting entry membership, not testing its value.
Set keeps the first occurrence and preserves insertion order. `With` comparers
must behave as an equivalence relation. Default equality is Range `matches`,
including its explicit handling of Null. There is no global comparer registry.

List `set`, `insert`, and `remove_at` report false for out-of-bounds indices.
Count/index operations retain the Range library's integral Number requirements.
`get`, `peek`, `pop`, and `dequeue` return Null on absence. `get_or` accepts an
explicit fallback. `remove_where` returns the number removed.

## Cost model and adoption

These are ordered, Range-backed collections, not hidden hash tables. Lookup is
linear; mutations generally copy a finite snapshot. Map/Set construction and set
algebra with arbitrary source comparers can be quadratic. They target script
configuration, manifests, small mutable worklists and expressive source
composition. Large sequential work should continue to use lazy Range adapters;
no-collection programs do not instantiate this module. A future host hash index
would require an explicit consistent equality/hash contract, not merely a
renaming of this implementation.

The operation families are informed by .NET
[List<T>](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1?view=net-10.0),
[Dictionary<TKey,TValue>](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0),
and [HashSet<T>](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.hashset-1?view=net-10.0).
Their host iterator invalidation and hash-table complexity are not claimed here.

`tests/Msync.EndToEnd/collections_journey.py` exercises alias mutation, factory
isolation, stable snapshots, duplicate keys, ordered set algebra, stack/queue
behavior, custom equality, nullable mapped values, empty stack/queue operations,
and O0/O2/O3 source/relocatable/executable CLI routes. Three rejected programs
exercise fixed List, Set and Map storage schemas.

`tests/Msync.EndToEnd/collections_empty_journey.py` independently exercises all
eight empty factories, empty reads/removals, populated mutations, custom
comparers and explicit nullable mapped values through O2 source, relocatable and
executable CLI paths. It also rejects concrete element-type changes, an
unadmitted Null value and a String comparator used with Number keys.
