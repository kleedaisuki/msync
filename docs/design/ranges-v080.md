# Range algorithms and iterator capabilities in 0.8

## Source tools over a small iterator boundary

`$std::ranges` remains the only predefined range namespace. A Range represents a
sequence, not a new array storage type or an implicit parallel loop. Public tools
in `stdlib/ranges.msync` are executable compiler input. Search, counting, folds,
prefix tests and projections are ordinary generic functions; only operations
requiring an iterator-producing capability live in `NativeRanges.Extended.cs`.

The operation families follow the [C++ range-adaptor draft](https://eel.is/c++draft/range.adaptors)
and [fold draft](https://eel.is/c++draft/alg.fold), while retaining msync's explicit
Null rules, reference-sharing Scope values and asynchronous source evaluation.
This is not a claim that an arbitrary msync Range is a C++ sized, random-access
or multipass range.

## Typed construction and explicit cursors (0.8.6)

`empty_of(Number)` produces an empty, eager `Range<Number>`. The compiler uses
the existing statically denoted Type, including the row from
`decltype(scope { name: "" })`, and the runtime constructs zero items with
the same closed carrier. No exemplar, deferred generator, conversion of
`Never` storage or language syntax extension is needed. This is the existing
Type-witness model applied to the C++ [empty view](https://eel.is/c++draft/range.empty)
contract.

`Cursor::begin(range)` eagerly freezes membership once. `has_value(cursor)`
distinguishes an item (including Null) from exhaustion; `value(cursor)`
uses indexed access, with the same exhaustion error as `at`. `next` moves to
the next item or the end and reports whether that position is readable.
`previous` moves back if possible, including from the end. `seek` and
`advance` accept positions in the closed interval from zero through the end
and leave the cursor unchanged for out-of-range or non-integral requests.
An ordinary Scope alias shares the position; `clone` creates an independent
position sharing the same snapshot. Neither operation copies the objects
in that snapshot. A live `for` loop remains the lightweight single-pass
alternative when random access is unnecessary.

`size` uses the stored snapshot length and `at` indexes snapshots directly;
both consume general ranges as required. `Cursor::{lower_bound,upper_bound}` return
absolute positions in the remainder of an already frozen sorted range,
without moving the cursor. These searches take O(log n) indexed steps.
The range-level `lower_bound`, `upper_bound`, `equal_range` and
`binary_search` first snapshot their input; `equal_range` returns the
half-open interval `{first,last}`, and a bound can equal the size.
The `_with` variants use an explicit strict comparator. Both follow the
[partitioned-input contract](https://eel.is/c++draft/alg.binary.search);
the algorithms do not sort or validate sortedness implicitly.
`is_sorted` and `is_sorted_with` are separate checks.

`merge`, `set_union`, `set_intersection`, `set_difference` and
`set_symmetric_difference` share a streaming two-input iterator. They require
both inputs sorted by the selected order and use the C++
[sorted multiset conventions](https://eel.is/c++draft/alg.set.operations):
union uses maximum multiplicity, intersection minimum, difference truncated
subtraction, and symmetric difference absolute subtraction. Merge retains
all values and chooses the left source first for equivalent keys.
Intersection/difference retain the left carrier; the other operations
combine both carriers. The natural variants use the same ordering as
`sort` (including the existing NaN placement), not a new meaning of language
comparison. The `_with` variants accept a typed Bool comparator.

Each consumption advances left before right and retains at most one current
item from each. Constructing the result does not enumerate either source
or invoke the comparison, and breaking early disposes both iterators.
Comparisons and each source's effects occur in iterator order; the functions
do not promise repeated evaluation is effect-free. Repeated enumeration of
a replayable source starts with fresh iterator state.

The full native result inference and lightweight flow/return projections share
the same structural range rules for nested windows, typed empties and sorted
combinations. A loop over `slide` retains the inner Range and its item type
instead of introducing an unrelated nullable Any element.

`tests/Msync.EndToEnd/stdlib_iterators_journey.py` checks the actual CLI
through source and source-deleted HIR lowered to both bytecode execution routes.
Its cases distinguish typed empty construction and policy selection, cursor
cloning and Null items, stable ties and shared Scope elements, lazy early exit
and repeat consumption, and typed B+ page splits and erasure. Negative cases
reject a non-static witness or an invalid fixed-carrier insertion.

## Range adaptors and terminal families

| Capability | Result and evaluation |
| --- | --- |
| `concat(a, b)` | Lazy left-to-right concatenation; preserves original element references |
| `flatten(values)` | Lazy one-level flattening of `Range<Range<T>>` |
| `zip(a, b)` | Lazy fresh Scope records `{ first, second }`, stops at the shorter source |
| `enumerate_from(values, start)` | Lazy fresh records `{ index: Number, value }`; `enumerate` starts at zero |
| `chunk(values, count)` | Lazy nonoverlapping chunks, including the final partial chunk |
| `slide(values, count)` | Lazy complete overlapping windows; no incomplete final window |
| `materialize(values)` | Eager snapshot of the sequence; later enumeration reuses it |
| `sort(values)` | Stable, buffered natural ordering on each enumeration |
| `sort_with(values, less)` | Stable, buffered ordering with asynchronous `less(a, b) -> Bool` |
| `scan(values, initial, operation)` | Lazy inclusive accumulation, with a fresh accumulator per enumeration |
| `sequence(start, stop, step)` | Lazy Number progression with exclusive end; both step directions work |
| `repeat(value, count)` | Lazy repeated references, not clones or repeated factory invocations |

The source layer adds `map`, `reject`, `flat_map`, `join`, `indices`, `keys`,
`values`, `iota`, `generate`, `slice`, `tail`, `init`, `cache`, `sorted`,
`sorted_with`, `sort_by`, `size`, `distance`, `count`, `count_if`, `matches`,
`for_each`, `copy_if`, `fold`, `reduce`, `fold_right`, `sum`, `product`,
`find_or`, `first_or`, `last_or`, `at`, `at_or`, `index_if`, `index_of`,
`min`, `max`, `min_by`, `max_by`, `partition`, `equal`, `starts_with`,
`ends_with`, `lexicographical_compare` and `adjacent_find_or`.

## Effects, reuse and ownership

Constructing an adapter does not enumerate its source or invoke its callbacks.
The signature's `Deferred` behavior carries that distinction into memory/effect
analysis. Parameter-domain failures such as zero window width are separate from
effects encountered during enumeration. In particular `take(adapter, 0)` does
not resolve the adapter's deferred source call.

Adapters enumerate again when the consumer enumerates again, subject to the
source's own capabilities. They do not silently promise that an external or
single-pass source is repeatable. `materialize` explicitly snapshots the first
successful enumeration and does not retain its own failed/cancelled read attempt;
the source's own reuse and failure behavior still applies. It copies
the sequence of Values, not the mutable contents of Scope instances or captured
Cells. This both preserves aliases and avoids a hidden deep-copy operation.

`zip` advances sources sequentially, left before right. If the right source ends
first, the left source has already advanced once for that attempted pair. The
API does not promise symmetric callback counts for unsized sources. Both
enumerators are disposed when consumption stops, fails or is cancelled.

Each chunk/window owns its captured sequence storage. Later window advancement
does not mutate an already returned chunk, although Scope elements remain
references to their original mutable instances. A positive width larger than a
short source is valid: `slide` is empty and `chunk` returns its one partial chunk.
No buffer is preallocated according to the requested width.

`scan` initializes its state inside the enumerator, not inside a reusable source
closure. A second consumption starts from the seed rather than continuing a
previous partial accumulation. The compiler specializes its two-argument
callback against the fixed accumulator domain and actual item type.

Natural ordering is limited to Bool, Number and compatible Path/String values.
Number ordering matches the existing interpreter's `Double.CompareTo`; text
uses Unicode scalar ordinal comparison of native spelling. No hash-code order
is invented for Scope, Function, Connection or Null. `sort_with` supports other
domains through an explicit strict-weak-order Bool callback. Stable merge sort
preserves equal elements' original order and awaits each comparison in the same
VM invocation rather than blocking on an asynchronous callback.

## Terminal semantics and cost

`size`/`distance` consume a non-snapshot Range; only the snapshot case uses
constant-time size. `count` counts matching elements and `count_if` counts
predicate matches. `matches` handles Null through `is null`/`is not null` and
delegates non-Null membership equality to the existing collection relation;
it does not introduce Null truthiness or relational ordering.

`fold` uses an ordinary local Scope accumulator with explicit Null admission.
The compiler can infer its non-Null payload and specialize the operation while
preserving the fixed storage constraint. `sum` and `product` retain the exact
left-to-right Number operation order; they do not substitute arithmetic formulas
or reassociate floating-point expressions.

Some source tools deliberately request a reusable snapshot: `min`/`max`, their
projection variants, `partition`, `equal` and lexicographical comparison. This
avoids silently evaluating a one-shot source twice. `partition` shares one
source snapshot, but its two predicate views execute their callbacks separately
when consumed. Prefix tests buffer only the prefix and short-circuit values;
suffix tests need reverse buffering. Sorting and snapshots use O(n) storage;
chunk/slide use O(width), bounded by values actually observed.

Counts are finite, integral Numbers in the platform iterator's index domain;
there is no user-language Integer type or arbitrary library element quota.
Number progressions use exact sequential floating-point addition. If a step
eventually ceases to change its value, enumeration reports that non-progress
instead of looping forever. A finite fractional progression can use an explicit
Number cast because decimal-looking tokens otherwise retain path-name syntax.

## Focused verification

`tests/StdlibRanges` executes source and binary round trips at O0/O2/O3 through
relocatable and context-bound bytecode, including Scope record projections,
nullable accumulators, stable sorting, lazy callbacks, reusable snapshots,
shared object identity, large-width short-source windows and cancellation.
The test controls live beneath repository-root `.temp` and never inspect user
home-directory data.

The `--nullable` test selection isolates public `single`, membership/search and
item retrieval with Null and Type values: 102 serialized portable/executable
checks at O0/O2/O3. Nullable membership uses native value identity/equality;
it does not introduce a source-language `Null ==` operation. Source declarations
admit these values while dependent result inference retains the actual item type.
