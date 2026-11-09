# Typed, source-backed standard library in 0.8

## Two layers, one semantic implementation

The November 9, 2026 expansion follows two complementary requirements:

1. Keep the host/compiler primitive layer small and semantically precise.
2. Provide a broad expression vocabulary above it, including substantial source
   libraries built from ordinary `scope` and generic `fn` values.

`Range` is the sequence abstraction. There is no parallel Array API or new
integer, pointer, iterator or file-handle language type just to imitate another
language's standard library. Materialization, random access and host resources
remain implementation mechanisms where an operation genuinely requires them.

Executable definitions live in `stdlib/*.msync`. Completion, signatures, hover,
definition navigation and `msync skill library MODULE` use those same embedded
bytes. This note records operation families and their contracts rather than
maintaining another authoritative signature table.

## Namespace ownership and compatibility

| Public entry | Responsibility | Implementation boundary |
| --- | --- | --- |
| `$std::cast(value, Type)` | Explicit, target-dependent conversion | A compiler-known Type value determines the inferred result; selected runtime conversions still validate their data |
| `$std::ranges` | Sequence adapters, searches, folds and aggregate tools | Source composition for expressible algorithms; native iteration/materialization capabilities where source cannot implement a lazy producer |
| `$std::functional` | Function and predicate composition | Ordinary generic source functions and closures |
| `$std::math` | Binary64 arithmetic, classification and scalar composition | Host floating-point intrinsics and ordinary source utilities; no integer/fenv subsystem |
| `$std::string` | Unicode text, searches, slices and Range producers | Unicode scalar host primitives plus source conveniences |
| `$std::path` | Lexical path composition and decomposition | Source wrappers over existing path/filesystem primitives; endpoint provenance remains attached |
| `$std::io` | File content and link-oriented workflows | Selected host effects, with Range-based lines/bytes instead of an Array type |
| `$std::fs` | Existing filesystem inspection and mutation API | Retained compatibility surface, not silently removed in favor of `io` |
| `$std::process` | Completed values and live process operations | Runtime-owned process lifetime/control, never a fabricated portable PID |
| `$std::bool` | Boolean expression tools | Existing source composition remains valid |

`stdlib/_core.msync` is a private source prelude for root exports such as cast,
not a public `$std::_core` namespace. `$std::ranges` remains the only predefined
range namespace; the removed `$view`, `$views`, `$ranges` and bare aliases are
not restored. User-declared lexical names remain ordinary values.

The namespace is a mutable lexical Scope, not a hardcoded spelling prefix.
Selective module loading must preserve shared module locations: a source
function capturing a standard module and later code selecting it through the
root must observe the same module object and permitted field assignments.
Parsing may be cached; a first caller's live Scope, captured binding slots,
working directory or home directory must not become process-global cache data.

Direct module selection instantiates only that module and its executable source
dependencies. A function captures the internal module location, not a detached
module snapshot. If later code materializes the whole `$std` root, its fields
load those same module Cells; changing a root module member therefore remains
visible to the earlier closure. A user-declared `std` still follows ordinary
lexical shadowing. The focused loading owner accepted 56 O0/O2/O3 and binary
contracts, including root/member replacement and shadowing. A tiny unoptimized
Bool use contains only that module's six source functions, not the enlarged
Range/String library's declarations.

## C++ operation-family mapping

The [C++ draft Ranges library](https://eel.is/c++draft/ranges) separates sequence
access, factories, adapters and conversions. msync adopts that separation while
keeping one public Range namespace and its own asynchronous source semantics.
The [algorithms clauses](https://eel.is/c++draft/algorithms) supply the coverage
map, not an ABI or a requirement to reproduce pointer categories.

| C++ family | msync representation and adoption criterion |
| --- | --- |
| Sequence access, count, search, predicates and folds | Range-consuming functions; early-return operations stop demand immediately |
| Transform, filter, take/drop and related adapters | Lazy Range expressions; constructing an adapter does not run its callback |
| Zip, enumerate, chunk, sliding and flattening | Range values with inferred element or Scope structure; no second tuple/Array subsystem |
| Reverse, sorting and reusable snapshots | Explicit buffering/materialization where forward iteration is insufficient |
| Mutating algorithms over iterator references | Return transformed Ranges or perform explicit callbacks/IO; do not pretend that immutable scalar elements are C++ writable references |
| Parallel execution policies | Not a name-only wrapper: source callbacks retain single-VM mutation and task-owner semantics; independent IO is scheduled by the runtime |
| Allocators, pointer operations and integer bit manipulation | No corresponding source-level representation is introduced in this expansion |

The floating-point portion of `<cmath>` fits the existing Number representation:
arithmetic, classification, rounding and elementary functions are actual binary64
operations. Their exceptional results and signed-zero behavior are part of the
operation contract. Integer-only algorithms and floating-point environment
control are different families and are not implied by providing `math`.

The [C++ string clauses](https://eel.is/c++draft/string.classes) suggest useful
text operations, but msync String has Unicode scalar semantics rather than a
choice of C++ code-unit template specializations. The
[filesystem operation clauses](https://eel.is/c++draft/fs.op.funcs) distinguish
lexical path manipulation from observations and mutations; msync additionally
retains selected Connection identity and captured local projection.

Process control is a host extension, not a nonexistent C++ `std::process`
family. It uses the actual
[.NET Process facilities](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process?view=net-10.0)
and established native SSH/WSL transport ownership.

## Required semantic distinctions

### Type-dependent casts

An explicit cast is not an implicit truthiness rule. `Null` still participates
only in admitted nullable storage and `is null` / `is not null` tests; postfix
`value?` still means **is null**. A target Type value determines the expression's
inferred type. Invalid selected data conversions report the source operation
and actual reason rather than returning an unrelated default.

A positive Type-value guard such as `if target is Number` establishes Number
as the denoted target, not Type as its metatype. For a target selected between
Number and String, use the corresponding positive guard on each dynamic branch.
Merely excluding Number does not establish that an otherwise unknown Type value
denotes String. Deferred Range casts also retain their inferred result contract:
reassigning the target binding to a different element type is diagnosed at the
cast origin rather than silently changing a previously inferred Range schema.

Path/String conversion changes the runtime value as well as its inferred type.
Converting a Path for lexical inspection does not authenticate its endpoint.
Converting textual content to Path is explicit, since `String -> destination`
writes text while `Path -> destination` copies file content.

### Range demand and replay

Adapters retain their source and callback; callback execution occurs on demand.
A terminal may finish early. Materializing operations document the finite-input
requirement and own their buffers; no arbitrary item-count quota is introduced.
Returned Scope elements preserve instance identity and shared captured Cells.

Each API must state whether another enumeration re-evaluates its source or
reuses an owned materialization. Filesystem-backed Ranges are observations,
not eternal snapshots. Count/index arguments use Number but require the exact
integral values implied by that operation; this does not create an integer type
or permit floating-point reassociation.

### IO and process ownership

Reads, writes and link operations are selected runtime effects. Ordinary file
replacement keeps the established owned same-directory stage and atomic
publication contract; an append operation does not secretly promise rollback.
Link entry operations must not modify a link target by accident.

Live Process operations operate on the actual selected process owner. Waiting
for one process must not join unrelated jobs; cancellation and failure retain
the native cause and source origin. A remote process identifier must not be
confused with the local SSH/WSL proxy process identifier.

### Optimization and tooling

Native signatures and effects are one contract shared by inference, runtime and
optimization. Deferred callback effects belong to consumption, not adapter
construction. Unknown callbacks cannot be treated as pure merely because their
adapter object is cheap to create. Filesystem observations never become stale
reusable compile-time proofs.

Adding expression tools must not make a small `$std::bool` use instantiate every
unrelated source module. Compiler/module-load cost, prepared execution and actual
IO latency are different measurements and are recorded separately.

## Delivery evidence

The experience journey follows executable APIs through source checking,
relocatable `.mso`, bound `.mexe`, actual bytes, and editor navigation into
`msync-stdlib:///0.8.0/…` documents. Domain owners retain their focused semantic
and platform tests. This integration record does not relabel a source module's
successful parsing as proof that its lazy callbacks or native operations ran.

Implementation inventories and accepted command/result references are added
here as the independent module lanes freeze their interfaces.

### Executable source inventory

The expansion's source snapshot has 357 named exports, comprising 166 ordinary
source functions and 191 native declarations. The latter include the existing
filesystem/native compatibility surface; they are not 191 newly introduced OS
operations. Counts describe executable declarations, not overload padding or a
second Array library.

| Source module | Native declarations | Source functions | Public exports |
| --- | ---: | ---: | ---: |
| Root `_core` prelude | 1 | 0 | 1 (`cast`, not a `_core` module) |
| `bool` | 0 | 6 | 6 |
| `fs` | 66 | 5 | 71 |
| `functional` | 0 | 14 | 14 |
| `io` | 8 | 6 | 14 |
| `math` | 43 | 13 | 56 |
| `path` | 0 | 40 | 40 |
| `process` | 12 | 4 | 16 |
| `ranges` | 37 | 51 | 88 |
| `string` | 24 | 27 | 51 |

The focused domain contracts remain with their implementations:
[Range demand, ordering and buffering](ranges-v080.md),
[binary64 mathematics](../../tests/StdlibMath/README.md), and the IO/Process and
text/path owner journeys. Source-only Path, Bool and functional modules illustrate
the intended layer boundary; elementary mathematics and stream iteration are
host capabilities rather than reimplementations in user source.

### Cross-module experience acceptance

`tests/Msync.EndToEnd/stdlib_experience_journey.py --binary PATH` runs eight composed
programs at O0/O2/O3 through source, relocatable and context-bound execution:

* UTF-8 file lines → composed trim/case function → lazy filter/map → String join
  → actual file bytes.
* Target-dependent cast → inferred Number/Path/Connection → Unicode scalar
  access and lexical filename inspection, without authenticating the Connection.
* Zip/enumerate records → inferred Scope fields → projected String results.
* `take(0)` and a mutable callback counter → source fold → exact demand counts.
* A discarded String join and byte writer → latent callback writes retained.
* File size → Number division/rounding and source mathematical composition.
* Runtime Type values selected on controlled filesystem branches → positive
  Number/String guards → statically inferred conversion results.

All 72 stage executions compare actual output bytes; source checking must leave
the targets absent, and executable bytecode launches from another directory
after deleting source. The same entry checks three inferred editor hovers,
three definition jumps into exact embedded sources (including the private root
prelude), structural Range member completion, eight source-backed skill pages
and six color modes. The local acceptance used the fresh managed CLI; the
integration owner separately records final Native AOT platform/image evidence.
A separate negative case checks the deferred cast's changed-target diagnostic
and source origin. The added Type guard cases were run incrementally after the
first 54 accepted executions, without repeating unrelated domain matrices.

An early O2 composition failure made `$std::string` become Null after captured
module use. O0 and actual source modules were correct. The optimizer owner fixed
the captured-Cell epoch case rather than rewriting the user pipeline or disabling
selective loading. The cross-module program remains a regression discriminator.

## Native library-load observations

The Linux component comparison uses exact scheduler baseline
`a8c71875e5f7bd2b5a628a8bcdece3f8b72ec4d1` and stdlib candidate
`b3c0f9f875e3d0f022f5f853a9f8f6b5e197ac65`. Both embed the identical
`tests/Performance/StdlibLoad/Program.cs` measurement source
(`40a1ba5f70b782266cf679daeb2ef3150fe4a0249f6aac37ee01ea65ea26575d`),
built using .NET SDK 10.0.403. Native executable SHA-256 values are:

* Baseline: `c0decade49374bd7232f3e7e53d4f394fb3de563a3cf4d924ddfb78bb99510c2`.
* Candidate: `6be44d30bdb34dcafdcf32da41fca4fe3a33191c724705ab3f08d2572e41e973`.

`tests/Performance/stdlib_load.py` alternates AB/BA across seven fresh processes
per common case/image. Each process records one cold compilation, performs three
untimed warmups, then records three warm O0 compilations. O0 is intentional:
unrelated source declarations must not disappear through DCE before their load
cost is observed. Times are compiler component milliseconds, excluding process
startup and IO; allocated bytes are managed allocation traffic, not live heap.
No measurement is a CI threshold.

| Shared source | Cold p50 / p95, before → after (ms) | Warm p50 / p95, before → after (ms) | Warm bytes, before → after | Raw functions, before → after |
| --- | --- | --- | ---: | ---: |
| No standard-library reference | 0.381 / 0.442 → 0.415 / 0.496 | 0.021 / 0.036 → 0.026 / 0.045 | 7,336 → 7,328 | 1 → 1 |
| `bool::both(true, false)` | 3.449 / 4.069 → 1.550 / 1.804 | 1.411 / 1.897 → 0.266 / 0.326 | 442,256 → 72,104 | 23 → 7 |
| `ranges::first(ranges::take([1, 2, 3], 2))` | 3.863 / 5.828 → 4.946 / 5.328 | 1.596 / 2.043 → 2.247 / 3.161 | 442,360 → 616,384 | 24 → 53 |

Selective loading substantially reduces unrelated-module work for Bool. The
no-library control has unchanged raw shape and approximately unchanged allocation;
its few-microsecond timing difference does not establish a meaningful product
regression or improvement. The expanded Range module itself costs about 0.65 ms
more per warm unoptimized compilation in this workload: its source-function
surface has grown, so it cannot be described as free or as a universal speedup.

Candidate-only capabilities are costs, not comparisons against nonexistent old
APIs:

| Source | Cold p50 / p95 (ms) | Warm p50 / p95 (ms) | Warm bytes |
| --- | ---: | ---: | ---: |
| Source `ranges::size(take(...))` | 5.442 / 6.236 | 2.220 / 3.063 | 641,024 |
| Root `cast("42", Number)` | 1.372 / 3.543 | 0.086 / 0.123 | 22,760 |
| Composed String trim/case closure | 4.536 / 7.435 | 1.891 / 2.886 | 505,960 |

Complete raw results are under `.temp/stdlib-load/paired-final.json`, with both
exact source archives, probe hashes and SDK in the adjacent baseline/candidate
`build.json` manifests. The reusable experiment implementation is committed
under `tests/Performance/StdlibLoad` and `tests/Performance/stdlib_load.py`.
