# Typed, source-backed standard library in 0.8

## Native families, records and policies

The current canonical source API separates three things that the earlier
factory-object API combined:

1. A named Scope owns a family of reusable constructors and operations.
2. An anonymous Scope owns each instance's data and selected policy values.
3. A concept constrains the fields or operations actually needed by an
   algorithm. It does not create a new runtime kind or a global instance search.

For example, `containers::List::new` constructs a data record, while
`containers::List::push(value, item)` mutates that record through an explicit
first argument. The operations are not copied into the record, and a field
read does not allocate or call a getter closure. Named family and default-policy
Scopes follow the ordinary declaration-site singleton rule. Instance records
are fresh anonymous Scopes; aliases retain the same mutable instance identity.

| Family | Instance data | Explicit policy boundary |
| --- | --- | --- |
| `$std::containers::List`, `Stack`, `Queue` | Typed materialized items and storage policy | `RangeStorage::snapshot` |
| `$std::containers::Map`, `Set` | Typed item/key/value snapshots, equality and storage policies | Associated-carrier `Equality`, plus `RangeStorage` |
| `$std::io::File` | Path and text-IO policy | First-order `TextIOPolicy`; Path provenance remains attached |
| `$std::io::Directory` | Path | Existing filesystem/Range operations |
| `$std::process::Command` | Executable Path and materialized argument snapshot | Actual Process operations, not a fabricated host resource |
| `$std::process::Completed` | Completed Process, output/error text and status | No hidden last-result slot |
| `$std::text::StringBuilder` | Chunks, Unicode-scalar length and chunk policy | Typed `TextChunks` append/build operations |
| `$std::regex::Pattern` | Pattern expression | Existing native regular-expression primitives |

Generic `fn` operations specialize using the actual instance row and policy
callable identities. A policy is an ordinary Scope value selected explicitly by
the caller, not a runtime string switch, reflection registry or implicit global
implementation. Named family functions receive `self` explicitly, so their
data dependencies remain visible to compiler dataflow and Scope scalarization.
The model borrows the organization and explicit receiver of
[Rust associated functions and methods](https://doc.rust-lang.org/book/ch05-03-method-syntax.html),
without importing `impl`, ownership syntax or another open type universe.
Concept bounds play the capability role described by
[Rust trait bounds](https://doc.rust-lang.org/book/ch10-02-traits.html), but msync
uses its existing structural checking and native Scope/Fn specialization.

The expression-problem motivation is closely related to
[object algebras](https://www.cs.utexas.edu/~wcook/projects/oa/oa.pdf): an
operation family and its carrier can vary independently. Here ordinary Scope
records and explicit policy arguments provide that separation, while existing
specialization removes known callable indirection. This does not introduce a
second runtime object model.

Policy signatures are checked at specialization. Their behavioral contracts
remain explicit: storage snapshots preserve ordered contents; an equality
policy supplies a stable equivalence relation on its stated carrier; a text-IO
policy owns the selected read/write behavior; a chunk policy preserves ordered
text concatenation. Signature satisfaction alone is not proof of these laws,
purity, or permission to reorder effects.

The earlier Pascal-named factory functions (`List`, `File`, `Command`, `Builder`
and their peers) remain source-compatible adapters. They are not the canonical
model for new code. In particular, a named Scope is not made implicitly callable
as a constructor: construction is an explicit `family::new(...)` operation.
Existing low-level `Path`, `Process`, `Range`, `io` and `fs` operations remain
useful directly; no wrapper record is required merely to use a native value.

Class-like family Scopes use PascalCase and functions use lowercase snake_case.
This convention applies to new associated operations and the existing scalar,
Range, filesystem and process primitives, without adding a second spelling API.
`$std::data::{Option, Result, Either, Pair}` supplies native algebraic composition
families, described in [the data representation contract](standard-data-v080.md).

An equality policy is a reusable source declaration, rather than a constructor
option interpreted by a runtime registry:

```msync
scope FoldCase {
    Self: String,
    equal: fn(left: String, right: String) -> Bool {
        $std::string::to_lowercase(left) == $std::string::to_lowercase(right)
    },
};
let names = $std::containers::Set::with_equality(["Klee", "klee"], FoldCase);
$std::containers::Set::insert(names, "Alice");
let unique = $std::containers::Set::snapshot(names);
```

The constructor checks the standard `Equality` contract. `Self` and the function
annotations constrain actual operation arguments and results; a callback that
returns Number instead of Bool is rejected before execution. The selected
record stores `FoldCase` as its policy, while the compiler retains its concrete
callable identity. Changing the policy is an explicit value choice, not an
integer mode flag or a hidden global equality setting.

### Focused integration observations

The canonical collection owner accepted nine real source/object/executable
routes at O0/O2/O3, 126 exact output observations and three policy/storage type
rejections. The final naming-only correction reused those semantic results and
accepted the three O2 routes, 42 observations and the same three diagnostics.
The callable `collections` compatibility module remained byte-identical.

The service owner accepted 180 actual filesystem, custom-policy and child-process
assertions through source and serialized `.mso`/`.mexe` at O0/O2/O3. The text and
pattern owner accepted 153 canonical record/policy assertions and 207 existing
text/regex contracts; the final snake_case correction reran the affected 153
checks rather than the unchanged historical matrix. These are focused owner
observations, not a sum of independent end-to-end scenarios.

`tests/Msync.EndToEnd/stdlib_policy_journey.py` combines the canonical collection
journey with actual File reads/rename, a selected in-memory uppercase IO policy,
a custom materialized chunk policy, regex, a real child msync process and the
existing Builder compatibility path. The cross-module slice passed all nine
managed staged routes before the final spelling correction; the final snake_case
O2 slice passed three routes, 39 byte observations and three useful diagnostics.
Its `--services-only` selection avoids repeating the collection owner's local
acceptance. Native platform delivery runs the full combined selection; its exact
image identities and results belong to the delivery record, not these managed
component observations.

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
| `value as Type` | Explicit, target-dependent conversion | Language syntax selects a finite conversion matrix; Path/String remain the only implicit conversion pair |
| `$std::ranges` | Sequence adapters, searches, folds and aggregate tools | Source composition for expressible algorithms; native iteration/materialization capabilities where source cannot implement a lazy producer |
| `$std::functional` | Function and predicate composition | Ordinary generic source functions and closures |
| `$std::math` | Binary64 arithmetic, classification and scalar composition | Host floating-point intrinsics and ordinary source utilities; no integer/fenv subsystem |
| `$std::string` | Unicode text, searches, slices and Range producers | Unicode scalar host primitives plus source conveniences |
| `$std::path` | Lexical path composition and decomposition | Source wrappers over existing path/filesystem primitives; endpoint provenance remains attached |
| `$std::io` | File content and link-oriented workflows | Selected host effects, with Range-based lines/bytes instead of an Array type |
| `$std::fs` | Existing filesystem inspection and mutation API | Retained compatibility surface, not silently removed in favor of `io` |
| `$std::process` | Completed values and live process operations | Runtime-owned process lifetime/control, never a fabricated portable PID |
| `$std::bool` | Boolean expression tools | Existing source composition remains valid |
| `$std::containers` | Named collection families and explicit equality/storage policies | Typed data records; shared associated functions, Range snapshots and native indexed storage where required |
| `$std::data` | Option, Result, Either and Pair composition | Closed runtime kinds; native null sum, Scope products and explicit eliminators |
| `$std::concepts` | Reusable structural and policy contracts | Compile-time declarations; no runtime concept values or implicit instance registry |
| `$std::meta` | Closed-type and callable metaprogramming | Unevaluated compiler queries and finite Type operations, not a user-extensible runtime type hierarchy |
| `$std::collections` | Compatibility callable collection factories | Established source behavior retained; new code uses `containers` |
| `$std::text` | StringBuilder and text layout | Source data records use one native persistent-chunk primitive instead of repeatedly copying prior text |
| `$std::regex` | Pattern family, matching and match/group records | Native regular-expression engine; Unicode scalar indexes and raw pattern specification records |
| `$std::files` | Compatibility callable File/Directory services | Existing source behavior retained; new code uses `io::File` and `io::Directory` |
| `$std::commands` | Compatibility callable command/result services | Existing source behavior retained; new code uses `process::Command` and `process::Completed` |
| `$std::environment` | Process variables and captured execution directories | Narrow host primitives plus source nullable/default/path-list composition |

`stdlib/_core.msync` has no public root cast export: conversions are language
expressions, not a dynamic universal conversion function. `_core` is not a
public namespace. `$std::ranges` remains the only predefined
range namespace; the removed `$view`, `$views`, `$ranges` and bare aliases are
not restored. User-declared lexical names remain ordinary values.

The namespace is a mutable lexical Scope, not a hardcoded spelling prefix.
Selective module loading must preserve shared module locations: a source
function capturing a standard module and later code selecting it through the
root must observe the same module object and permitted field assignments.
Parsing may be cached; a first caller's live Scope, captured binding slots,
working directory or home directory must not become process-global cache data.

The object services are ordinary structural values. For example, the compiler
checks a File service against a user-defined concept without a trait registry,
an implementation declaration, or an additional host object kind:

```msync
concept WritableText {
    write: fn(value: String) -> Null,
};
fn save(destination: WritableText, text: String) {
    destination::write(text)
};
let report = $std::files::File(./report.txt);
save(report, "completed");
```

File and Command factories capture specifications; they do not open files,
start processes or authenticate remote endpoints until a selected method does
that work. Qualify a physical path with `path @ connection`; `::` selects the
object's lexical methods and fields.

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

### Statically selected conversions

An explicit cast is not an implicit truthiness rule. `Null` still participates
only in admitted nullable storage and `is null` / `is not null` tests; postfix
`value?` still means **is null**. The type expression after `as` determines the
inferred result without a dynamic universal-cast API. Invalid data conversions report the source operation
and actual reason rather than returning an unrelated default.

The compiler rejects conversion pairs outside its finite matrix. Converting
text to Number parses the selected text at runtime; it does not change every
String parameter into an implicit numeric input. `decltype(expression)` is
unevaluated and can supply a statically inferred target.

Path/String conversion changes the runtime value as well as its inferred type.
Converting a Path for lexical inspection does not authenticate its endpoint.
Transfer dispatch preserves the operand's actual type: `String -> destination`
writes text while `Path -> destination` copies file content. Use `text as Path`
when that source expression must denote a file, rather than relying on another
operation's Path parameter to introduce the implicit conversion.

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

`ranges.materialize` is an explicit eager boundary: the selected call consumes
its input once before returning an owned, replayable Range. The EagerRange
effect propagates through source functions, so a collection's `snapshot()`
method can freeze membership before a later mutation. Ordinary Range-producing
source calls remain on-demand. `environment.variables()` similarly snapshots
the process environment at the selected call, not at a later enumeration.

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

The current 15 public modules have 429 top-level exports: 214 ordinary source
functions, 208 native declarations and seven source values. Object methods are
not separately counted as top-level exports. Native declarations include the
established filesystem surface, not 208 newly introduced OS operations. The
private, comment-only `_core` source contributes no public export.

| Source module | Native declarations | Source functions | Source values | Top-level exports |
| --- | ---: | ---: | ---: | ---: |
| `bool` | 0 | 6 | 0 | 6 |
| `collections` | 0 | 15 | 0 | 15 |
| `commands` | 0 | 2 | 0 | 2 |
| `environment` | 9 | 4 | 0 | 13 |
| `files` | 0 | 2 | 0 | 2 |
| `fs` | 66 | 5 | 0 | 71 |
| `functional` | 0 | 14 | 0 | 14 |
| `io` | 8 | 23 | 0 | 31 |
| `math` | 45 | 13 | 6 | 64 |
| `path` | 0 | 40 | 0 | 40 |
| `process` | 12 | 4 | 0 | 16 |
| `ranges` | 37 | 51 | 0 | 88 |
| `regex` | 6 | 4 | 0 | 10 |
| `string` | 24 | 27 | 0 | 51 |
| `text` | 1 | 4 | 1 | 6 |

The focused domain contracts remain with their implementations:
[Range demand, ordering and buffering](ranges-v080.md),
[binary64 mathematics](../../tests/StdlibMath/README.md), and the IO/Process and
text/path owner journeys. Source-only Path, Bool and functional modules illustrate
the intended layer boundary; elementary mathematics and stream iteration are
host capabilities rather than reimplementations in user source.

Source object composition also supplied a compiler convergence discriminator:
the first row-based Map exposed a loop analysis comparing freshly joined Scope
metadata by reference instead of semantic state. That defect is fixed; the
retained column-based source representation is independently simpler and
cheaper, not a substitute for compiler convergence. The measurements and
original-row regression are recorded in
[performance-components-v080.md](performance-components-v080.md).

### Earlier library-only expansion evidence

The following acceptance record belongs to the earlier `71828ca` library
expansion, before the language upgrade replaced public cast calls and Connection
qualification. It is retained as provenance, not as a claim that its old source
syntax remains current. Current upgrade journeys and the exact tested image
identities are recorded in [stdlib-upgrade-validation-v080.md](stdlib-upgrade-validation-v080.md).

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

## Earlier native library-load observations

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
