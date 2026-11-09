# Immutable Scope shapes and compact field storage

Run `dotnet run --project tests/ScopeLayout`. The source/object/executable
journeys compare O0, O2 and O3 with actual results, aliases, nullable fields,
closure-shared cells, loop-created instances and constructor-local storage.
Manual typed bytecode also checks old name-only access, matching/mismatching
offset hints, Connection/Process dispatch and selected duplicate-field errors.
`tests/Msync.Runtime.Tests --mutable-compat` independently exercises format 2
snapshot captures and fresh nested Scope constant instances.

## Ownership and layout

`ScopeLayout` owns immutable declaration-order names and a single ordinal
name-to-slot dictionary. `ScopeValue` owns a fresh `Value[]` for each instance;
the layout/array constructor transfers that array to the new instance. The
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
