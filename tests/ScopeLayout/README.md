# Immutable Scope shapes and compact field storage

Run `dotnet run --project tests/ScopeLayout`. The source/object/executable
journeys compare O0, O2 and O3 with actual results, aliases, nullable fields,
closure-shared cells, loop-created instances and constructor-local storage.
Manual typed bytecode also checks name-only access, matching/mismatching
offset hints, Connection/Process dispatch and selected duplicate-field errors.
`tests/Msync.Runtime.Tests --mutable-state` independently exercises current live
captures, obsolete-format rejection and fresh nested Scope constant instances.

`--duplicate-only` selects 18 actual VM/filesystem journeys for hand-authored O0
typed IR, including source-span ownership and pure initial entry. Duplicate
fields are selected or skipped directly and inside a called function, with a
preceding small text transfer when applicable. Direct modules, portable artifact
round trips and explicitly prepared executable round trips must retain the
concrete `ArgumentException` inside the instruction-owned `MsyncException`.
Preparation must perform no writes; selected failure preserves earlier admitted
work and suppresses later work, while an unselected failure leaves both writes
intact. Source parsing is deliberately not involved: its earlier rejection of a
duplicate declaration would not exercise backend/VM effect ordering.

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

`--spatial-vm-only` constructs actual rows through compiler/VM Buffer programs,
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

The interpreter lazily caches successfully constructed layouts by their exact
member spelling. It never builds a selected-error table on an unselected path.
The cache remains valid when a REPL image changes: shapes contain no live cells,
source bindings, endpoint observations or caller path context. Programs without
Scope construction do not allocate the cache. Runtime constant cloning shares
the layout, transforms visible field values into fresh slots and retains
allocation-site provenance.

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
tree is needed. The guarded field hint uses the lookup-avoidance principle of
[Self inline caches](https://bibliography.selflanguage.org/pics.html), but is a
compile-time offset plus a name guard, not a runtime PIC or adaptive compiler.
The original member name remains authoritative. Hint zero is the old encoding;
a wrong offset falls back to ordinary named access, and older readers can
ignore the hint without changing behavior.

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

The measured per-instance storage fell from 280 to 88 bytes. Direct indexing
isolates available layout benefit; actual bytecode uses the guard to preserve
named semantics when mutable bindings hold a differently ordered shape. These
are component observations, not a whole-product throughput claim.
