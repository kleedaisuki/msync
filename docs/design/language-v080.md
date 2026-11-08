# v0.8 typed expression language

The authoritative source grammar is implemented in `src/Msync.Compiler/Syntax`.
The preserved Rust frontend and runtime live under `legacy/`; their old artifacts
are not silently reinterpreted by the C# bytecode interpreter.

## Values, bindings and control flow

Braces, not indentation, delimit a block. An expression without a final `;` is
the block result; `;` discards that value and produces `Null`. Empty blocks and
declarations also produce `Null`. A terminating `return` or `break` has internal
`Never` type, so an unreachable block tail does not widen a function result with
an artificial Null case.

`let` introduces an immutable lexical binding, and a later declaration may shadow
it. `_` is a discard pattern, not a nullable variable declaration. Destructuring
patterns bind range elements. Paths preserve raw spelling until an endpoint or
runtime context projects them. Strings preserve literal backslashes and newlines;
a doubled quote represents one quote. Path/String conversions are executable
bytecode conversions where an annotation or parameter demands the other type,
not merely changes to inferred metadata. Consequently an unconstrained String
transfer writes literal bytes, whereas a String admitted to a Path parameter is
read as a file. Both annotated block tails and explicit returns convert values.

`Bool` and `Null` are separate types. Null does not coerce to Bool or participate
in ordinary boolean comparisons. `is null`, `is not null`, and postfix `?` test
null identity. In particular **`value?` means `value is null`**, not its negation.
A binding must explicitly admit null with `?` or a nullable annotation. Branch
refinement removes null from the other branch, and a null arm in `match` removes
that case from the following catch-all. Conditions remain strictly Bool.

The finite semantic universe includes Bool, String, Path, Function, Scope,
Connection, Process, Null, Range with an inferred item type, and Type values.
Number is retained as a count primitive for the established range APIs and
numeric process exit results. Unions use the Core `TypeKind` bitset, with an
optional range item type, rather than allocating a vector for ordinary scalars.

`match` requires exhaustive coverage. True/false cover Bool, null covers Null,
and type patterns cover their finite type domains. A regex or an individual
String literal cannot exhaust the String/Path domain; a type arm or `_` is needed.
Regex arms are partial predicates over Path/String: Null, Bool and other
nontext values fall through rather than being stringified. A matched binding
narrows to its text domain, so nullable or heterogeneous subjects remain useful
with a fallback arm. Literal patterns are validated by the host regex engine
during compilation. Regex matching projects Path values into native strings.
`decltype(expr)` checks
and infers the expression in a nonexecuted compilation frame and emits a Type
constant; the operand's effects never enter the caller's instruction stream.

## Functions and namespaces

Named/anonymous `fn` is the one callable source construct. Parameters may state
capability constraints such as `Path | String`; omitted types specialize from
arguments. A template captures only referenced outer bindings, by value, at its
definition. Capture ownership is retained as slots, not names: local shadowing
must not change what a recursive call sees. Specialized recursive calls reuse a
function-table identity instead of recursively expanding source. Recursive
inference starts with Never and joins concrete return branches; explicit return
annotations provide a direct expected type where needed.

First-class closures retain captured values when returned from a function or
registered with a native range callback. Static call sites select a monomorphic
function-table entry while retaining the closure's capture vector. A function
selected by runtime control flow retains a finite set of statically checked
callable alternatives and a precomputed template-to-specialization dispatch map.
There is no runtime source parsing or generic type inference.

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
Scope `::` selects a member; Connection `::` qualifies an inferred Path/String
value. A scope selected by control flow exposes the intersection of member names
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

## Lazy ranges and artifacts

Literal ranges keep a pure-value fast path. Effectful items become typed,
zero-argument closure thunks; the range evaluates only demanded items. Calls
returning Range defer the entire call and its argument evaluation until demand.
`take(0)` therefore does not execute filesystem queries, mutations, or programs
inside the source expression. Thunks capture only referenced existing slots;
their bytecode owns any internal local slots. A finite range transfer remains
one selected operation, not an implicit source-level `for`.

The compiler emits source-independent, typed stack bytecode with explicit
function tables, local slots, branches, capture layouts and native identities.
It passes the relocatable Module through the Core machine-independent optimizer
at the requested level before export. Runtime binding performs contextual and
machine-dependent lowering; native AOT compiles the host interpreter, not a
reflection-based dynamic code generator for the DSL.

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
