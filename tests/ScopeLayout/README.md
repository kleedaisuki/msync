# Immutable Scope shapes and compact field storage

Run `dotnet run --project tests/ScopeLayout`. The source/object/executable
journeys compare O0, O2 and O3 with actual results, aliases, nullable fields,
closure-shared cells, loop-created instances and constructor-local storage.
Manual format-6 bytecode checks finite layout projections, opaque named
access, exact member diagnostics and shared captured locations. The reader
rejects invalid layout metadata before execution. Connection qualification
and Process members use their own operations rather than Scope fields.
`tests/Msync.Runtime.Tests --mutable-state` independently exercises current live
captures, obsolete-format rejection and fresh nested Scope constant instances.

`--field-places-only` checks common and differing ordinals at branch and loop
joins, legal opaque fields, captured layout-changing assignment versus copied
receiver identity, and rejection of an omitted incoming layout. Observations
must retain the original `LoadField`/`StoreField` PCs. `--namespace-only`
checks unused namespace erasure and live callable environments through source,
detached HIR, portable object and bound artifact.

`--member-dispatch-only` exercises the executable LoadLocal/LoadField pair.
The fixture enters at either original PC, rebinds a captured receiver to a
different field layout, verifies the exact missing-field span and verifies
that a Process member remains deferred until consumption. Instruction
profiling follows the canonical PCs rather than exposing the synthetic tag.

`--guarded-scope-only` tests the short Scope comparison region.
It covers an incoming interior PC, exact signed-zero and NaN payloads,
raw and boxed stores, a different runtime layout, a writable Cell alias,
a missing-field span and an unresolved Process result. Aggregate counters
record region hits and misses without changing the logical opcode counts;
detailed observation sees the original PCs.
Both conditional opcodes run taken and fallthrough cases where one
numeric destination is overwritten and the other is live. Each
aggregate run commits exactly one slot and retains the lower operand;
detailed traces still report the original conditional and successor PC.

`--duplicate-only` first checks that the reader rejects duplicate field names
in a layout whether or not its constructor is selected. It then uses legal
typed code containing an opaque receiver lookup to test actual runtime effect
ordering. Across 18 direct, portable and prepared executable journeys, the
selected missing-field failure retains its exact source span and concrete
diagnostic. Preparation does no writes; selected failure preserves earlier
admitted work and suppresses later work, while an unselected failure leaves
both writes intact.

`--call-preparation-only` verifies that execution selects the actual static or
table-dispatched target exactly once. It covers opaque native I/O, mutable
captured counters and metadata reads after preceding publications, through
direct bytecode and portable/prepared executable round trips. Preparation is
outside the VM; residual calls execute normally.
An additional raw-runtime fixture holds a public `ProcessValue.Completion`
producer while its discarded stdout transfer owns the metadata target. With no
sleep or timing threshold, the callee's captured-counter prefix must be visible
before release while its metadata result remains pending. Releasing the producer
must yield the published text without replaying that prefix. The live process is
deliberately not serialized into an artifact.
The matching process-flow discriminator calls a helper that schedules a second
copy and returns the same held Process. The caller must advance after discarding
that result while both process-dependent transfers remain pending; releasing the
producer must finish both copies. Preparation may defer unknown source metadata
to actual VM admission, but must not wait for the live producer before the caller
can supply its completion.
Two additional held-producer controls require both the callee and caller scalar
prefixes to advance when the held path is in an unselected branch or the selected
metadata belongs to an already-ready sibling file. They reject endpoint-global
or parent-directory-wide waits for unrelated work.

## Spatial-storage discriminators

`--spatial-only` runs reference-compatible Core semantics without assuming a
physical representation. `--packed-spatial-only` uses one explicit
`ScopeAllocator` and additionally requires the numeric storage mechanism:
multiple rows share fewer GC-reference-free double arrays, and warmed raw
numeric reads/writes allocate no per-operation Number wrappers. These are
mechanism checks, not a promise that Scope handles have adjacent addresses.
Wide sparse numeric fields include offsets 0, 64 and 129.

The checks retain IEEE bit patterns, declaration-order enumeration, instance
identity and hash membership, visible-value cloning, exact captured-cell
identity, and numeric/reference/Null transitions. Promotion of one row must not
change a neighbor. Weak-reference checks keep one mixed row and one numeric row
alive while 514 unrelated row handles and 257 reference payloads become
collectable. This distinguishes a numeric-only shared page from a slab retaining
unrelated managed data.

`--packed-isolation-only` keeps an early numeric row after dropping its allocator
and later rows. The unrelated final page and previous input Number boxes must
be collectable. This discriminator fails the first packed prototype, in which
row storage retained a mutable shape/pool and its unrelated current page; the
split immutable-mapping/current-pool implementation passes.

`--spatial-vm-only` constructs actual rows through compiler/VM dense-vector programs,
rather than only calling an allocator in a microbenchmark. Computed rows must
use numeric storage; shared-literal and small mixed/reference-tail rows report
their representation without forcing a policy that could make them slower.
The reference suffix is longer than the numeric operand sidecar, and must not
be truncated or demand nonexistent numeric slots.

The real CLI companions are
`tests/Msync.EndToEnd/object_locality_journey.py` and
`affine_scan_journey.py`. They exercise runtime-derived values through source,
source-deleted `.mso` and `.mexe` execution. The affine positive explicitly
requires fewer portable iterator sites; dependent, lazy-effectful and irregular
controls derive their expected values independently. Floating reductions retain
their own per-accumulator order even when interleaving two independent scans is
legal.

The frozen common4 producer passed the object journey at O2 through all three
routes (12 exact outputs each). The existing HIR semantic journey passed its
three O2 routes (28 outputs each, six rejection diagnostics, no query effects),
and the selected mixed-observation batch-failure journey retained successful
work and the concrete failure with `Jobs=1`. The unchanged numeric mechanism
checks were accepted on common3: 94 packed Core checks and 32 actual VM checks.

## Ownership and layout

`ScopeLayout` owns immutable declaration-order names and a single ordinal
name-to-slot dictionary. Ordinary `ScopeValue` instances own a fresh `Value[]`;
the layout/array constructor transfers that array to the new instance. Packed
instances instead own a row descriptor and private reference payload while
sharing only a numeric page with other rows. The
public dictionary constructor snapshots member values. `Members` retains its
`IReadOnlyDictionary<string, Value>` interface and now references the Scope
itself, without allocating a per-instance member-view object. A field containing
`CellValue` still loads and stores through that exact shared location.

Portable code carries typed layout, field projection and static-slot tables.
An executable resolves its layout IDs to immutable descriptors; descriptors
contain no live cells, source bindings, endpoint observations or caller path
context. Runtime constant cloning shares the descriptor, transforms visible
field values into fresh slots and retains allocation-site provenance.

Scope equality and hashing use stable instance identity. Neither a shared
layout nor a shared empty storage array makes two independently constructed
Scopes equal. Native `ranges.unique`/`contains` and `HashSet<Value>` therefore
retain distinct objects while recognizing aliases, and field mutation does not
change collection membership. The direct native regression first reproduced
the record-derived equality failure with two instances using `Array.Empty`;
actual source factories are also checked through source/object/executable
routes. Current `PopMany(0)` happens to allocate distinct arrays, but semantic
identity must not depend on that incidental allocation policy.

The layout describes reference slots and member identity, not unboxed payload
types. Two source shapes with the same names can share it even if their scalar
payload types differ. The compiler separately owns fixed type/nullability
schemas. Known slots therefore do not imply a compile-time count of live
instances: recursion, dynamic ranges and external input still determine that
count. A future linker can relocate descriptor references independently of
individual heap addresses; managed allocation and variable-sized String/Path
payloads do not become static memory segments merely because field positions
are known.

The approach follows the shared descriptor/property-array separation used by
[V8 fast properties](https://v8.dev/blog/fast-properties). Unlike JavaScript,
Scope membership does not dynamically change, so no HiddenClass transition
tree is needed. Finite portable projections carry a layout ID and ordinal for
every reachable receiver layout; this is verified bytecode, not a field-name
hint or adaptive inline cache. A genuinely opaque Scope still needs a named
lookup because its layout is not known to the producer.

## Focused representation measurement

`DOTNET_TieredCompilation=0 dotnet run -c Release --project tests/ScopeLayout
-- --performance` compares the previous dictionary/view representation with
shared-layout storage in one process. It performs three warm-ups and reports
nine-sample medians on the development Linux host, .NET 10.0.403 SDK. Construction
reuses the same three scalar payloads and one prepared shape; it does not time
parsing, first-time shape construction, Native AOT startup or filesystem I/O.
The baseline directly reproduces the previous field-dictionary/member-view
ownership and does not charge its old temporary PopMany/name-split arrays.

| Case | Median time | Thread allocation |
| --- | ---: | ---: |
| 50,000 dictionary/view instances | 15.929 ms | 14,000,000 B |
| 50,000 shared-layout instances | 4.338 ms | 4,400,000 B |
| 500,000 previous named reads | 18.899 ms | 0 B |
| 500,000 compact named reads | 17.746 ms | 0 B |
| 500,000 direct indexed reads | 4.397 ms | 0 B |
| 500,000 guarded indexed reads | 14.227 ms | 0 B |

The measured per-instance storage fell from 280 to 88 bytes. This historical
component table predates the format-6 place representation. Direct indexing
isolates the layout benefit; it is not a whole-product throughput claim.

## Guarded crossing continuation

`dotnet run --project tests/ScopeLayout -c Release -- --scope-crossing-only`
checks the whole crossing arm through format-6 object and bound images.
Each route executes unprofiled, with aggregate counters, and with
instruction-site observations. Both directions and their nonempty Bool
join retain the canonical opcode counts. The fixtures include signed zero,
NaN, permuted layouts, a live-out local, a real Cell alias and alternate entry.
Run the same check with
`MSYNC_JIT=off` to verify this worker does not rely on the guest JIT.
