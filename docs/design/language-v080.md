# v0.8 typed expression language

The authoritative source grammar is implemented in `src/Msync.Compiler/Syntax`.
The retired Rust frontend and runtime remain in Git history at `v0.7.3`, not
the current checkout. Their artifacts are rejected by the C# bytecode interpreter.
Current artifacts use format 4 only; formats 1/2/3 are rejected. Product version
remains 0.8.0 and the default optimization level is O2. The
[delivery matrix](language-upgrade-delivery-v080.md) records acceptance separately
from this language contract.

## Values, bindings and control flow

Braces, not indentation, delimit a block. An expression without a final `;` is
the block result; `;` discards that value and produces `Null`. Empty blocks and
declarations also produce `Null`. A terminating `return` or `break` has internal
`Never` type, so an unreachable block tail does not widen a function result with
an artificial Null case.

`let` introduces a mutable lexical location with an immutable inferred or
declared type, and a later declaration may shadow it with a different identity.
Assignment updates the existing value and checks, never widens, its storage type. Scope
aliases share an instance and its mutable field locations. The November 9
requirement supersedes the original immutable model; the full inference,
aliasing and optimization contract is in
[mutable state and effects](mutable-state-and-effects-v080.md).
`_` is a discard pattern, not a nullable variable declaration. Destructuring
patterns bind range elements. Binding names use the language's atom spelling,
not an ASCII identifier subset: `.codex`, `copy-config` and Unicode names are
legal. Nonliteral atoms consult the lexical environment before becoming an
unbound Path; the same rule applies to closure captures and qualified operands.
Completion replaces the whole atom name, including a leading dot, rather than
relying on the editor's default word range. Paths preserve raw spelling until an endpoint or
runtime context projects them. Strings preserve literal backslashes and newlines;
a doubled quote represents one quote. Path/String conversions are executable
bytecode conversions where an annotation or parameter demands the other type,
not merely changes to inferred metadata. Consequently an unconstrained String
transfer writes literal bytes, whereas a String admitted to a Path parameter is
read as a file. Both annotated block tails and explicit returns convert values.

`Bool` and `Null` are separate types. Null does not coerce to Bool or participate
in ordinary boolean comparisons. `is null`, `is not null`, and postfix `?` test
null identity. In particular **`value?` means `value is null`**, not its negation.
A binding must explicitly admit null with `?` or a nullable annotation. Scope
fields likewise require `?`, for example `scope { filename?: null }`. A nullable
Null initializer leaves a compile-time payload inference hole: store constraints
supply its non-null type evidence, then the solved storage type is fixed. Branch
refinement removes null from the other branch, and a null arm in `match` removes
that case from the following catch-all. Conditions remain strictly Bool.

`for` retains its Null result and accepts bare `break`; value-producing exits
belong to `loop { ... break value; }`. Nested exits select the nearest loop.
Break edges carry their own flow facts, rather than borrowing a surviving
fallthrough refinement. Abrupt exits close the active iterators they leave,
then close acquire regions; ordinary exhaustion needs no extra operation.
Iterator close is an internal native capability, not a new source type or
bytecode format.

Destructuring a Range snapshots only the requested finite prefix once, then
indexes that snapshot. Extra input is still ignored and a short input still
fails at the missing index. This neither enumerates an unbounded suffix nor
reruns a lazy callback separately for each binding.

A value of type Type is not itself the type it denotes. Unknown denotations
cannot be folded to false by comparing the metatype's bit with Number or String.
Successful singleton type tests retain Type as the value type and record the
proved denotation for branch-local casts; ordinary payload narrowing is separate.

The finite semantic universe includes Bool, String, Path, Function, Scope,
Connection, Process, Null, Range with an inferred item type, and Type values.
Number is the numeric carrier for arithmetic, range/count APIs and numeric
process exit results. Unions use the Core `TypeKind` bitset, with an
optional range item type, rather than allocating a vector for ordinary scalars.

`match` requires exhaustive coverage. True/false cover Bool, null covers Null,
and type patterns cover their finite type domains. A regex or an individual
String literal cannot exhaust the String/Path domain; a type arm or `_` is needed.
Regex arms are partial predicates over Path/String: Null, Bool and other
nontext values fall through rather than being stringified. A matched binding
narrows to its text domain, so nullable or heterogeneous subjects remain useful
with a fallback arm. Source regex patterns are validated by the host regex engine
during compilation. Regex matching projects Path values into native strings.
`decltype(expr)` checks
and infers the expression in a nonexecuted compilation frame and emits a Type
constant; the operand's effects never enter the caller's instruction stream.

## Numbers and explicit conversions

Number is IEEE binary64, not a public integer-width hierarchy. Decimal fractions
(`1.5`, `.5`, `1.`), exponent notation (`1e3`), separators (`1_000`), and binary,
octal and hexadecimal integer literals (`0b10`, `0o10`, `0x10`) are accepted.
Adjacent numeric operators such as `1+2` are arithmetic; explicit path spelling
such as `./1.5` or `~/1+2` remains path data. Unary `+`/`-`, `+`, `-`, `*`, `/`,
`%` and `**` use numeric operands. Multiplication/division/remainder associate
left to right; exponentiation associates right to left and binds more tightly
than unary negation: `-2**2` is `-4`.

Division follows IEEE/JavaScript Number semantics, including infinity and NaN
for zero divisors. `%` is truncating remainder with the dividend's sign, not
Python's modulo; `$std::math::remainder` is the separate IEEE remainder function.
NaN is unequal to itself and makes ordered comparisons false; positive and
negative zero compare equal while their representation remains distinct. Static
folding and runtime evaluation preserve those rules; an algebraic constraint
does not license reassociation of floating-point operations.

Lazy Range counts/indices require finite integral nonnegative Numbers below
2^63; stride/chunk/slide require positive counts. They are not restricted to
Int32 or silently parsed from String. Materialized containers retain their real
storage limits. The [numeric contract and rationale](number-semantics-v080.md)
explains count policy, safe integers and container ordering separately from
source numeric equality.

`value as TypeSyntax` requests an explicit conversion; the target is static type
syntax, not an arbitrary runtime Type-valued expression. The source checker
rejects impossible conversions before optimization. Value-dependent parse
failures still produce an actual conversion diagnostic during execution.

| Target | Admitted source representations beyond compatible identity |
| --- | --- |
| String | Path (native text), Bool, Number, Connection, Type |
| Path | String, projected in the current context |
| Number | Bool (`false`/`true` to 0/1), String/Path containing invariant numeric text |
| Bool | Number (zero/NaN false, otherwise true), String/Path containing `true`/`false` |
| Connection | String naming a builtin endpoint or an explicit `ssh://`/`wsl://` endpoint |
| Range with an item constraint | Range whose elements admit the requested conversion, applied lazily |
| Scope, Function, Process, Type | Compatible identity only; concept constraints are checked structurally |

Nullable targets admit Null; nonnullable targets require prior null handling.
A conversion of a union must admit every possible source case. Text-to-Bool is
not nonempty-string truthiness. **Only Path/String conversion is implicit**,
including assignments, parameters and annotated returns; no Number/Bool coercion
is introduced. The old public `cast` function is replaced by `as`; its internal
native lowering is not an additional source API.

## Functions and namespaces

### Canonical range namespace

`$std::ranges` is the only predefined source entrypoint for range operations.
The former `$view`, `$views`, `$ranges`, and unbound bare `view::`, `views::`,
or `ranges::` conveniences have been removed. Using one reports source-owned
diagnostic `E0210` with the canonical migration spelling, rather than silently
interpreting the removed builtin as a filesystem Path. A user may still declare
an ordinary lexical binding with any of those names; it has no implicit
namespace status.

The editor's root catalog, TextMate range-function fallback, snippets and
embedded skill examples use the same canonical namespace. Removed builtin
identities are not retained in the current format-4 reader/runtime. An ordinary
user declaration with the same spelling remains a lexical value, not a builtin.

Named/anonymous `fn` is the one callable source construct. Parameters may state
capability constraints such as `Path | String`; omitted types specialize from
arguments. A template captures only referenced outer lexical locations, by
identity, at its definition. Capture ownership is retained as slots, not names: local shadowing
must not change what a recursive call sees. Specialized recursive calls reuse a
function-table identity instead of recursively expanding source. Recursive
inference starts with Never and joins concrete return branches; explicit return
annotations provide a direct expected type where needed.

Return constraints use the same type catalog as parameters and bindings:

```msync
fn select(file: Path) -> Path { file };
let optional = fn(file: Path, present: Bool) -> Path? {
    if present { file } else { null }
};
```

Both named and anonymous functions enforce the annotation on block tails and
explicit returns, including executable Path/String conversions. A mismatch
points to the returned expression and relates it to the declared return type.
`LanguageTypes` is the single source for parsing, resolution, editor type
completion and printed type discovery; `Number` is supported, not an unimplemented
`Int` alias. Type completions are selected from parsed type positions after a
parameter/binding colon or a function's `->`, not from arbitrary transfer arrows.

First-class closures retain captured locations when returned from a function or
registered with a native range callback. Static call sites select a monomorphic
function-table entry while retaining the closure's capture vector. A function
selected by runtime control flow retains a finite set of statically checked
callable alternatives and a precomputed template-to-specialization dispatch map.
There is no runtime source parsing or generic type inference.

### Named structural concepts

Concepts express requirements over existing Scope values while keeping the
runtime type universe closed:

```msync
concept Semigroup {
    type Self;
    combine: fn(left: Self, right: Self) -> Self,
};
concept Monoid {
    Semigroup,
    identity: Self,
};
fn use_identity(algebra: Monoid) -> Monoid {
    algebra::combine(algebra::identity, algebra::identity);
    algebra
};
```

A body contains required members, operation signatures, associated type
declarations and previously declared base concepts. Associated types are local
or inherited; matching Type-valued members (for example `Self: String`), value
members and concrete operation parameter annotations supply their carrier.
Associated Scope carriers retain deep field shape rather than discarding it
to the broad Scope kind. Implementing functions may remain generic.

Constraints apply at specialization, annotated returns/bindings and static `as`
checks. Concept names resolve in their definition-time lexical environment,
including closures and REPL submissions. Mutable constrained locations and
required operation members preserve their checked contracts on replacement.
Checking does not execute operations or insert implicit field conversions.
Missing members, unresolved associated types and wrong signatures relate the
diagnostic to the concept declaration (`E0214`).

A Null-only nullable field may still have an unsolved storage carrier. Concrete
associated-type evidence can establish that carrier when the object satisfies a
concept; the current field value remains Null. The obligation belongs to the
actual cell and its structural view, not a global substitution for every fresh
instance of the constructor. Already fixed String/Any/union domains are checked
rather than narrowed because their current value happens to be Null. Metadata
queries establish such obligations only in their isolated semantic workspace.

There is no `requires`, `trait`, `impl`, global instance search, operator
overloading or additional runtime TypeKind. Different Scope dictionaries may
express different algebras over the same carrier. Structural checking cannot
prove associativity, purity or commutativity and supplies no parallel-reduction
or floating-point-reassociation proof. See
[algebraic constraints and rationale](algebra-constraints-v080.md).

### Scope identity and source class composition

`scope Name { ... }` is a named declaration singleton: the first executed
initializer establishes one shared instance per declaration within the current
program or REPL session. Repeated calls/loop visits reach that same instance;
specializations must agree on its fixed field schema. Reentrant initialization
uses the first published canonical instance. Session reset creates an independent
registry; this is not cross-program/process shared state.

A named Scope reserves its lexical location before constructing its fields.
Member functions can therefore select later members through the Scope name,
including recursive calls such as `Family::operation(...)`. They capture the
same mutable location as ordinary closures, rather than a copied namespace or
an independently initialized singleton. A named Scope expression supplies its
name inside that expression; a declaration also binds it in the enclosing scope.

`scope { ... }` constructs a fresh instance each time it is evaluated. Thus a
named namespace can expose an anonymous factory for independent objects:

```msync
scope Counter {
    new: fn { scope { value: 0 } },
};
let first = Counter::new();
let second = Counter::new();
first::value = 1;
```

`second::value` stays 0. Aliasing `first` shares its identity and mutable fields;
the inferred Number storage type remains fixed. Factory methods, captured
locations and concept constraints provide source-level object composition
without opening the runtime type system. The
[standard-library inventory](stdlib-v080.md) and focused collections/services
notes describe the built-in compositions rather than a second host class model.

### Explicit `global` imports and the Rust compatibility boundary

`global name, other;` explicitly imports existing definition-time lexical
ancestors into an `fn`; it is not a declaration in a process-wide name table.
Resolve the nearest prior binding at the function's definition, never an
unrelated caller local or a later declaration. A nested function can import a
grandparent binding even when its parent did not explicitly import it. Prior
Scope fields are legitimate ancestors for functions defined inside that Scope.
An import retains the binding's type, Null admission, declaration origin and
identity. Ordinary 0.8 implicit capture remains supported.

In 0.8 the imported location is the same shared cell used by ordinary captures.
Assignment through the import updates that location, subject to its fixed type;
later assignment to the same outer location is visible inside the function.
A later `let` shadows with a different location and does not retarget the import.
The import outlives the defining frame when its closure does. Top-level `global`
is invalid because top-level bindings already share their lexical environment.
A parameter or a local `let` with the same name conflicts with the import;
repeating an import of the same still-visible location is harmless. Reimporting
after shadowing with a new local is also a conflict, rather than silently
retargeting that local. Scope syntax remains fields plus a value expression;
functions inside a Scope may import preceding fields, but `global` does not
create exported fields.

The Rust 0.7.3 baseline owns a deliberately different value model. Its parser
emits `StatementKind::Global`, and `import_globals` imports from frozen ancestor
maps into a fresh invocation environment. Frozen maps can contain a live
`Value::Dynamic(Slot)`, so an imported runtime Bool/String slot observes later
slot updates, whereas a captured literal remains the old literal. Importing does
not import the owner's `cells` map: assigning the imported name changes the
invocation's local value, not the outer binding. Prior macro/symbol/boolean
definitions can also be imported by their frozen identity. A root Scope has the
same top-level restriction; a Scope nested in a macro inherits that invocation's
import context. One old imported-name-set quirk allowed reimport after a local
rebind to reset the name to the captured value; 0.8 instead keeps the explicit
lexical-identity conflict rule above.

These historical distinctions are grounded in the `v0.7.3` sources:
[`structured.rs`](https://github.com/kleedaisuki/msync/blob/v0.7.3/src/compiler/structured.rs)
(`import_globals`, macro frame construction),
[`structured/symbols.rs`](https://github.com/kleedaisuki/msync/blob/v0.7.3/src/compiler/structured/symbols.rs)
(`captured`, `instantiate`) and
[`structured/v6.rs`](https://github.com/kleedaisuki/msync/blob/v0.7.3/src/compiler/structured/v6.rs)
(slot assignment). The discriminating
baseline tests are `lowering_v4::macro_global_uses_definition_snapshot_and_locals_do_not_leak`,
`symbol_v052::lexical_globals_capture_prior_values_and_callers_are_not_captured`
and the transitive/imported-template cases in `compiler_functions_v060`.
Restoring the keyword in 0.8 reuses existing lexical captures and typed location
effects; it does not require a global registry, a new opcode, or a new artifact
format. Historical artifacts are not accepted by the current C# reader.

A type-derived condition such as `if decltype(value) is Path` selects its
statically possible branch during specialization. `match decltype(value)`
similarly infers only the selected type arm after checking exhaustive coverage
of that known type value. An impossible branch cannot reject the current
specialization for an operator unsupported by that specialization's argument.
Condition evaluation remains exactly once when it has effects; the decltype
operand itself remains unevaluated. Ordinary Bool parameters are not value
specializations: calls with true and false share one type specialization and
retain their runtime branch.

Scopes are lexical member values; Connections are physical endpoint values.
`$wsl`, `$local`, native endpoint contexts and `ssh://...` produce Connections.
Scope/Process `::` selects a member. `path @ connection` qualifies a Path/String
value, evaluates the left operand before the right, and preserves raw path
provenance. For example `(~/.codex @ ssh://forge) -> ./backup;` selects a remote
source without introducing lexical endpoint members. The old physical
`connection::path` spelling is rejected with migration guidance. A scope selected
by control flow exposes the intersection of member names
that exist in every alternative, with each member's joined semantic type.
Identical dictionary allocation identity is not required for member projection.

Standard-library module files are executable compiler inputs embedded from
`stdlib/*.msync`. Parsed documents are cached; semantic values are constructed
with the current lexical/runtime ownership. `$std::fs`, `$std::ranges` and other
module names are actual Scope members. Native declarations resolve registered
host identities with declared signatures; known dependent range capabilities
specialize item and callback results at each use. The compiler does not require
a Runtime assembly reference. An unqualified native name may select a unique
registered suffix, such as `exists` resolving `fs.exists`.

## Submission completeness and regex boundaries

`SyntaxException.IsIncomplete` distinguishes an unfinished submission from a
complete malformed one. Parser EOF expectations and lexer-owned unterminated
String/regex states supply the flag; the CLI does not count raw braces or guess
from error text. Comments, escaped delimiters, character classes and named-group
headers therefore retain the same meaning in files, piped sessions and the
interactive editor. The original exception constructor remains available.

Regex delimiters also recognize adjacent `=>` and `->` operators. A named capture
`(?<name>...)` or named backreference `\k<name>` owns its internal closing angle;
that angle is not the source regex delimiter. The host regex engine still owns
regex validity and matching, rather than a second regex parser in the CLI.

`tests/Msync.EndToEnd/regex_boundary_journey.py` checks compact match arms,
compact selected transfers, empty named captures and backreferences from source
and source-free `.mso`/`.mexe`. The REPL continuation/recovery journey additionally
checks comment and regex braces, actual multiline strings and incomplete EOF.

## Lazy ranges and artifacts

Literal ranges keep a pure-value fast path. Effectful items become typed,
zero-argument closure thunks; the range evaluates only demanded items. Calls
returning Range ordinarily defer the call and its arguments until demand.
`take(0)` therefore does not execute filesystem queries, mutations, or programs
inside the source expression. Thunks capture only referenced existing slots;
their bytecode owns any internal local slots. A finite range transfer remains
one selected operation, not an implicit source-level `for`.
Explicit eager snapshot operations (`materialize`, `environment.variables`)
consume their input at the call and propagate that effect through source
functions. They must not become deferred observations of later mutable state.

The compiler emits source-independent, typed stack bytecode with explicit
function tables, local slots, branches, capture layouts and native identities.
It passes the relocatable Module through the Core machine-independent optimizer
at the requested level before export. Runtime binding performs contextual and
machine-dependent lowering; native AOT compiles the host interpreter, not a
reflection-based dynamic code generator for the DSL.
Binding captures context once, lowers, optimizes once at the requested level,
then performs final contextual lowering. Executing `.mexe` neither rebinds nor
reoptimizes it. Format 4 is the sole supported binary contract; see
[runtime ownership](runtime-v080.md).

`CompilerSession` retains lexical bindings and stable function/local identities
between REPL submissions. Each returned Module contains only the new main-entry
instructions and the complete referenced function/constant tables. Runtime
sessions retain main locals; previously accepted effects are not replayed.
A failed compilation restores the preceding compiler tables and environment.

## Engineering and research connections

- [Bidirectional typing](https://research.cs.queensu.ca/home/jana/papers/bidir-survey/)
  motivates synthesizing expression types and checking operator/parameter
  capabilities at their source locations; this implementation needs no
  higher-rank type engine.
- [C# null-state analysis](https://learn.microsoft.com/en-us/dotnet/csharp/nullable-references)
  illustrates the useful separation of declared nullability from branch-local
  knowledge. msync makes violated admission a compile error, not a warning.
- [Native AOT limitations](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
  rule out runtime code emission and unbounded reflection. Explicit records,
  bytecode interpretation and a host-native registry fit that execution model.
- [MLIR bytecode versioning](https://mlir.llvm.org/docs/BytecodeFormat/)
  separates container evolution from semantic compatibility. Core artifacts
  carry an explicit stage and version rather than persisting process handles,
  authentication state, filesystem observations or compiler source templates.

Focused compiler invariants live in `tests/Msync.Compiler.Tests`; actual source,
source-free .mso/.mexe, callback, nullable, filesystem and recursive journeys live
in `tests/Acceptance` and `tests/Review`. Formatting and strict local compilation
are developer checks, not remote publication quality gates.

## Persistent execution-prefix state

A CompilerSession can reuse a parsed submission and expose its visible binding
names for native editing without parsing or inferring on every keystroke.
The original Compile(string) entrypoint remains available.

Persistent entry slots use a private uninitialized sentinel distinct from Null.
After a runtime error or cancellation, the compiler reconciles source-ordered
root declarations against stores that actually completed. Completed prefix
bindings survive; an unfinished shadow reveals the preceding initialized
binding, including an earlier declaration in the same submission. Actual Null
values remain initialized values. Function, constant and local identities stay
stable; completed filesystem operations are not reversed.

Only interactive CompilerSession instances retain declaration metadata. Ordinary
source compilation allocates no binding journal, and execution adds no per-load
or per-store validation. Reconciliation scans declarations only after failure.
The real-process submission journey covers prefix values, repeated shadows,
legitimate Null values, multiline recovery and captured paths across /cd.

## Source-owned diagnostics and editor inference

Compiler and parser errors carry one structured `SourceDiagnostic`: a stable
category code, unprefixed message, primary source/range/label, explanatory help,
optional source edit, and related source locations. CLI rendering and LSP share
that data; neither extracts coordinates from exception text. The original
exception message still includes `source:line:column` for existing callers.
Coordinates handle LF, CRLF and lone CR consistently. Lexer comments terminate
at either newline form instead of swallowing the remainder of a CR-only file.

Common failures identify the operation the user must change: Null admission or
comparison (`E0201`), unknown types (`E0202`), incompatible values (`E0203`),
call arity/signatures (`E0204`), missing members (`E0205`), invalid `::` receivers
(`E0206`), loop context (`E0207`), regex syntax (`E0208`) and incomplete match
coverage (`E0209`). Parser categories distinguish missing separators (`E0102`),
open delimiters (`E0103`) and unclosed literals (`E0104`). Missing punctuation
and nullable bindings supply exact edits; delimiter errors retain the opening
location, rather than only pointing at end-of-file.

Specialization failures retain the function-body primary range and add the
actual call with parameter/argument types as a related location. An incompatible
annotated argument highlights that argument, with the parameter declaration as
a related location. In particular, passing `Constructor()::path` to a generic
function expecting lexical member access explains that the Path member is not
the Scope itself; it does not misdescribe every `::` as Connection qualification.
The representation follows the source-language/actionable guidance in
[GCC's diagnostic guidelines](https://gcc.gnu.org/onlinedocs/gccint/Guidelines-for-Diagnostics.html)
and the primary/related labels, help and source-edit separation used by
[Rust's diagnostic system](https://rustc-dev-guide.rust-lang.org/diagnostics/diagnostic-structs.html).

`EditorCompiler.Analyze` parses once and runs the same pure static compiler with
an optional observation callback. Ordinary source compilation supplies no
observer and does not allocate editor signatures or member lists. Analysis
retains semantic Info identities first, then renders signatures after all calls
have specialized: an earlier function declaration or alias therefore reports
its later-inferred parameter/result types. Multiple call types aggregate rather
than making a shared source occurrence depend on the last call checked.

Parser-owned identifier spans distinguish declaration names from keywords and
quoted member spellings. Scope-member occurrences, constructor-returned Scopes
and Function/Scope aliases retain their semantic roles. Syntactic declaration
roles are seeded inside uncalled generic bodies without inventing argument
types or executing/specializing those bodies. Scope member presentation is
cached by semantic Scope identity only within the current analysis, so repeated
standard-library references share their member descriptors without retaining
a previous document's lexical context.

Focused executable checks are `tests/Msync.Compiler.Tests --diagnostics`:
source-edit recompilation, delimiter provenance, all newline coordinates,
Null/Bool/match explanations, the supplied Scope constructor/call example,
polymorphic hover aggregation, exact semantic identifier spans and uncalled
nested declarations. Actual CLI/LSP and extension-host journeys exercise the
same structures through the installed user interfaces.

Editor snapshots may end inside a function, Scope initializer, binding,
qualified selection or call argument. `Parser.ParseForEditor` retains the enclosing
syntax with grammar-owned missing expressions and missing delimiter tokens,
while preserving the first original lexer/parser diagnostic and source ranges.
The ordinary `Parse` entrypoint remains strict; recovered trees never enter the
normal source/artifact compilation path.

`EditorAnalysis.At(caret)` queries immutable lexical snapshots recorded by the
same inference helpers used for ordinary compilation. It returns actual visible
bindings and the inferred receiver of `::`, including returned-Scope chains.
Snapshots retain persistent environment roots plus lexical owner/point offsets;
member/signature rendering occurs only for the requested caret, with a local
cache, not for every environment at every expression. Completed lexical owners
use half-open bounds, so closing a function cannot leak its parameters outside.
Incomplete owners include the end-of-input caret.

Uncalled generic bodies are explored only in isolated editor frames: annotations
provide known parameter capabilities, unannotated parameters stay explicitly
unknown, and local bindings still shadow outer names. The editor definition
environment retains all visible outer names, not only names already selected as
runtime captures. Called generic contexts retain actual argument types; when
several instantiations share a source receiver, completion exposes only members
available in every applicable alternative. Speculative observations never replace
concrete observations from actual calls. Statically unselected branches can be
explored for editing without changing the selected executable branch.

`CompilerSession.AnalyzeEditor` forks compiler-owned tables and immutable binding
roots. Its probe specialization cache is separate from live Template caches;
editing a new argument type cannot install a function-table identity into the
running session. It does not interpret user source or replay prior effects.
Focused tests compare later real module functions, constants and instructions
against a session that performed no probes. Tolerant diagnostics retain the
active generic call chain, so source recovery does not erase call-site guidance.

Presentation origin is independent of structural type. A named `scope Name {}`
and its aliases have the type presentation role; an anonymous Scope value and a
constructor-returned instance have the ordinary variable role. Both still have
semantic type Scope and permit heterogeneous, independently inferred fields.
Functions use the function role regardless of named/anonymous spelling.
The literal `scope` and `fn` tokens remain keywords, not declaration-role
identifiers. Named concepts and `$std` module namespaces use the type role;
`if`/`else`/`match`, `?`, `acquire`/`release` and `as` use control-flow coloring,
while `@` is an operator. Formatter layout uses a soft 80-column target and
readable multiline Boolean chains, without changing literal data or semantics;
see [tooling details](tooling-v080.md).

### Static endpoint hints and live prompt bindings

Editor descriptors may retain an `Endpoint` Connection identity from a literal
SSH/WSL URI or a builtin endpoint. Aliases and lexical Scope members preserve
that identity. A control-flow join retains it only when both alternatives name
the same Connection; aggregation across generic source occurrences likewise
requires every observed choice to have that same non-null identity. This is
static metadata, not a filesystem observation or permission to execute source.
Neither inference nor binding description starts a process or authenticates.

Endpoint identity does not enter the specialization key or emitted bytecode.
A generic parameter therefore forgets its scalar endpoint identity, including
identities nested in Range item metadata, before body inference. Otherwise a
type-shared `identity(firstHost)` specialization could incorrectly advertise the
first host for `identity(secondHost)`, or carry it into a returned closure.
Captured location identities participate in specialization, but a mutable
location's current Scope payload, callable or endpoint is not a permanent
constant. Reuse its current-value facts only while the reaching write and
alias/effect dependencies remain valid; see
[mutable state and effects](mutable-state-and-effects-v080.md). A function that
statically returns a literal Connection can retain that literal result normally.

`CompilerSession.DescribeBindings()` renders the current accepted/reconciled
lexical environment into an independent semantic snapshot: names, types,
callable signatures, nested members and known endpoint identities. It performs
no recompilation and excludes compiler-internal slots such as `$std` (an
explicit user alias of the standard Scope remains visible). The prompt requests
a snapshot once before editing, not for every keystroke. Execution-prefix
reconciliation and lexical shadowing therefore govern both execution and hints.
`tests/Msync.Compiler.Tests --editor-endpoints` covers literal/builtin aliases,
nested members, ambiguous joins, generic/captured/Range endpoint isolation,
unchanged type specialization counts and accepted/reconciled prompt snapshots.

A REPL can pass `ExecutionSession.GetLocalValue` to
`CompilerSession.AnalyzeEditor(submission, resolveAcceptedValue)`. The fork then
hydrates accepted Path/Connection/String facts from initialized root values and
Scope fields. A captured Path retains its actual working directory, home and
native projection after `/cd`; completion does not rebind its spelling against
the current process directory. Function captures are hydrated by matching the
actual FunctionValue and zipping its capture vector in Template capture order,
never by interpreting function-frame slot numbers as root-local indexes.
Unobserved captured Paths remain unknown instead of acquiring a guessed cwd.

The fork owns snapshots of accepted Template signature dictionaries and capture
metadata as well as its own specialization cache. Previously returned analyses
therefore remain stable when a later real submission adds a specialization;
concurrent caret queries read no mutable live Template state. Value observation
never enumerates a Range or invokes a callable, and it does not enlarge static
Scope capabilities or specialize code on runtime Bool values. The focused
`tests/PathProjection --editor-session` journey executes accepted declarations,
changes cwd/home, checks direct/Scope/captured/returned-closure projections, and
then executes new real generic calls after editor-only probes.

Caret contexts retain the grammar-owned receiver kind: lexical `Member` selection
versus `PathJoin`. An opaque Path parameter can therefore suppress an unproved
filesystem query while still offering actual visible bindings, without treating
an invalid `Path::member` as a filesystem or namespace lookup. Process receivers
expose the four working runtime capabilities `stdout`, `stderr`, `exit_code` and
`success`; their types come from the canonical native signatures. The compiler
no longer advertises `pid`, which the runtime has never exposed, and accepts the
already-supported Bool `success` property.
