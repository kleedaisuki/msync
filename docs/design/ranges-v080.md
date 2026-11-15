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

## Source initialization and traversal are separate

Range is an ordinary builtin object. Its LLVM-facing representation is `ptr`;
literal construction is an ordinary constructor call with already evaluated
element operands. It does not introduce a Range instruction, implicit element
functions, a distinct capture model, or a prerequisite heap/alias proof. LLVM's
`ConstantRange` and `ConstantFPRange` are compiler numeric-analysis structures,
not representations or evaluation policies for the language Range object.

A literal `[a, b, c]` evaluates its initializers once in source order and stores
the resulting elements. Scopes retain their allocation identity across repeated
enumeration. This is the source-container side of the Stream/C++ ranges model,
not an implicit generator of constructor closures. Explicit callbacks are the
lazy boundary: `map(source, callback)` creates a pipeline now and invokes the
callback during traversal; `generate(count, factory)` produces elements on demand.
A function returning either object is still called normally. No function-result
projection chooses whether an ordinary source call should execute.

Native/HIR opcode numbers formerly used for implicit literal/call thunks are
retired without renumbering other instructions. HIR format 4 retains an empty
reserved wire section, not a range-mode arena. Artifacts containing the retired
evaluation modes/opcodes require source recompilation. There is no compatibility
execution fallback.

## Effects, reuse and ownership

Constructing an adapter does not enumerate its source or invoke its callbacks.
The signature's `Deferred` behavior carries that distinction into memory/effect
analysis. Parameter-domain failures such as zero window width are separate from
effects encountered during enumeration. In particular `take(adapter, 0)` does
not enumerate the adapter or invoke its traversal callbacks. Ordinary calls that
construct the source and adapter have already executed; their arguments are not
implicitly captured for later evaluation.

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

## Execution-only costs of Range composition and singleton payloads

The 0.8.6 Linux x86-64 release CLI compiled at O2 and bound each input before
measurement. A source-linked Native AOT runner at `025b1da` (.NET SDK 10.0.403,
logical CPU 12) decoded all `.mexe` images before timing. It warmed each
variant once, then ran six rounds, alternating forward and reverse variant
order in one process. Time covers `Interpreter.ExecuteAsync` to completion;
every result was checked outside the timer. Allocation is the difference in
`GC.GetTotalAllocatedBytes(true)` around each invocation, not live memory.

The first control sums the even values of `x + 1` for integers
`0 <= x < 262144` in increasing order (expected `17180000256`). Start
`result` at zero. The scalar loop breaks when `x >= 262144`, assigns
`y = x + 1`, adds `y` when `y % 2 == 0`, then assigns `x = y`.
The inline `for` consumes `iota(0, 262144)` and keeps the same conditional
body. The pipeline uses `transform(values, fn(x: Number) { x + 1 })` then
`filter(added, fn(x: Number) { x % 2 == 0 })`; the `for` body just adds each
selected value.

| Range control | Median ms | Median allocated bytes |
| --- | ---: | ---: |
| Scalar loop | 215.20 | 9,438,720 |
| `for iota`, inline body | 239.07 | 15,733,104 |
| `iota → transform → filter → for` | 467.05 | 103,816,880 |

The inline range adds about 24 allocation bytes per input, consistent with
the [boxed sequence yield](../../src/Msync.Runtime/Native/Ranges/NativeRanges.Algorithms.cs#L78-L89).
The composed range adds about 336 bytes per input over the inline range and
has a +226.40 ms paired-median elapsed difference. Each input crosses two
[callback invocations](../../src/Msync.Runtime/Native/Ranges/NativeRanges.cs#L68-L82),
each transporting a one-element argument array and crossing a producer
boundary. The measurement does not apportion the cost among iterator steps,
argument transport, frames, boxes and GC. In particular it does not show
that each synchronous completion performs OS scheduling.

The second control repeats the 8,192-key
[red-black Set fixture](../../tests/Performance/LibraryMatrix/tree.msync)
with score `73728` and four source variants. For the unchanged local copy,
prepend the current library's `construction_range`, `NaturalOrder` and
`Set` definitions to the fixture without its first import line. In the
inline comparison copy, replace `self::order::less(key, value)` and its
inverse with the corresponding Number `<` expressions and constrain
`key` to Number in `find_index`, `contains`, `allocate`, and `insert`.
The scalar payload copy additionally changes the NIL initializer's payload
to zero, stores `key` instead of `[key]` in allocation/reuse, and reads
`node::payload` directly in the two search loops.

| Set control | Median ms | Median allocated bytes |
| --- | ---: | ---: |
| Standard Set | 2,004.98 | 212,724,192 |
| Unchanged local Set | 1,915.60 | 212,723,064 |
| Local Set, inline finite Number comparison | 1,386.23 | 166,774,480 |
| Inline comparison, direct Number payload | 1,131.29 | 129,089,948 |

Use the local variant as the baseline for the source edits. The paired
median differences are −533.67 ms for inlining the comparison and −248.23 ms
for the subsequent payload change. Owned-array `front` already bypasses
enumeration; the second change removes the remaining singleton creation,
lookup/call path and intermediate representation. Neither change explains
the whole gap. This is a finite-number diagnostic overlay, not a valid
library replacement: the normal order handles Null, NaN and custom
comparators, and the observable Set shape is read by the
[red-black journey](../../tests/Msync.EndToEnd/rb_set_journey.py#L55-L59).

### Constant-time indexing still crosses an expensive boundary

For a point-access control, sum `x % 64` for `x` in `iota(0, 262144)`
(expected `8257536`). The two alternatives replace the value used in the
sum with `ranges::at(values, x % 64)` or `Buffer::get(values, x % 64)`.
Build `values` once from `iota(0, 64)` with `materialize` or
`Buffer::new`, respectively. Disassembly retains the direct `NativeCall`
at each access. Both operations already index their backing storage
without enumeration.

| Indexed access control | Median ms | Median allocated bytes |
| --- | ---: | ---: |
| Scalar, sum the index itself | 188.20 | 18,878,832 |
| Owned snapshot, `ranges.at` | 359.17 | 65,021,584 |
| Live Buffer, `storage.buffer_get` | 356.18 | 46,153,864 |

The Buffer adds about 104 bytes per index; the snapshot lookup adds
about 176. The SDK 10.0.403 Native AOT machine code allocates a display
class on entry to [native invocation](../../src/Msync.Runtime/Execution/Interpreter.Invocation.cs#L98-L135),
before it tests for a deferred argument: the captured local async
function is only used in the deferred branch. Both
[Range dispatch](../../src/Msync.Runtime/Native/Ranges/NativeRanges.cs#L8-L29)
and [extended dispatch](../../src/Msync.Runtime/Native/Ranges/NativeRanges.Algorithms.cs#L17-L53)
also allocate display classes before their operation tests because
some unrelated adaptors use capturing lambdas. Those allocations are
distinct from a `ValueTask` actually suspending, or from the index
implementation and layout of the 64-element storage.

A diagnostic build moves deferred native completion and the capturing Range
adaptors behind separate helpers, invoked only in the branches that need
them. The two runners execute identical `.mexe` bytes in A/B/B/A fork
order, with the same per-fork warmup and six observations. Results below
are pooled descriptive medians (12 observations per runner).

| Indexed access control | Median ms, baseline → diagnostic | Median allocated bytes, baseline → diagnostic |
| --- | ---: | ---: |
| Scalar | 196.56 → 183.43 | 18,878,832 → 18,878,696 |
| Owned snapshot, `ranges.at` | 373.94 → 339.11 | 65,021,584 → 29,369,592 |
| Live Buffer, `storage.buffer_get` | 376.52 → 349.86 | 46,153,864 → 29,376,312 |

The diagnostic removes about 64 bytes per Buffer read and 136 bytes per
snapshot read, leaving about 40 incremental bytes per access in both cases,
consistent with the two-argument
[`Value[]`](../../src/Msync.Runtime/Execution/OperandStack.cs#L62-L72).
This identifies the common closure and the additional Range dispatch
closures without changing index behavior or eliminating any VM instructions.
The same binary overlay and frozen standard Set image, run in one B-then-A
pair of warmed six-observation forks, changed median execution from
1,930.54 to 1,819.05 ms and allocation from 212,722,248 to 152,320,316 bytes
(28.4% less). The elapsed observations are descriptive; the scalar control
also varied. These controls do not validate effectful or deferred branches
for shipping a production patch.

A compact owned-single representation and a specialized source-to-source
loop for a closed pure pipeline are distinct follow-ups. Compare them
separately against the existing generic path. The
[stream fusion paper](https://www.cs.tufts.edu/~nr/cs257/archive/duncan-coutts/stream-fusion.pdf)
shows why removing intermediate iteration state and inlining callbacks can
matter; a general msync Range must still preserve lazy demand, selected
callback effects, iteration/IEEE order, cancellation and disposal. The
probe, inputs and raw per-round receipts from this run are in
`.temp/range-vm-diagnosis/`.

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
