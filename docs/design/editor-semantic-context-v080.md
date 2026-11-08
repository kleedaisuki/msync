# Lexical semantic contexts for editing incomplete source

This is the implementation contract for completing names and members inside
functions and Scopes in 0.8. It extends the original top-level prefix recovery;
the focused acceptance results belong beside the implementation as they land.
Endpoint query ownership remains in
[the remote completion contract](editor-remote-completion-v080.md).

## One syntax model, two admissions

Ordinary compilation accepts complete syntax and reports the existing precise
error otherwise. Editor parsing retains the same node shapes around incomplete
input: a missing expression or selector has an explicit zero-width missing node,
and a missing delimiter is recorded at its grammatical owner. The original text
and diagnostic remain unchanged. Recovery does not append guessed source text,
split on semicolons in Strings, or turn an unfinished function into an unrelated
top-level prefix.

In particular, `fn f(arg) { let local = scope { x: ./ }; local::` retains the
function, parameter, completed local binding, member receiver, and missing member.
The strict compiler still rejects that text. The editor has enough structure to
show `local`'s actual `x` member without executing the function.

This follows the useful part of
[Roslyn's immutable syntax model](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-syntax):
missing and skipped tokens represent incomplete input instead of discarding its
surrounding syntax. [Tree-sitter's upstream design](https://tree-sitter.github.io/tree-sitter/)
also makes useful results during syntax errors a primary editor workload. Neither
source requires adopting another grammar or parser runtime for msync.

## Contexts belong to lexical owners and declaration boundaries

The semantic snapshot answers `At(caret)` with two distinct things:

- Visible lexical bindings, including parameters, captures, local declarations,
  recursive function identity, and earlier Scope members.
- The inferred receiver of a member or endpoint selection at that exact syntax
  location. This may be a Scope, a Connection, a Process, or unknown.

An internal context entry stores a lexical owner span, a declaration boundary
offset, and the existing immutable `Frame.Environment` root. The innermost owner
containing the caret wins; within it, the last applicable declaration boundary
wins. Before a binding's initializer, the old binding is still visible. After
the declaration, its replacement shadows that name. Leaving a block restores
the outer root. Future declarations and sibling function locals are not visible.

Keep roots, not expanded completion lists, in this table. Materialize and render
the selected environment only when queried, sharing the existing per-snapshot
Scope-description cache. Eagerly rendering every visible binding at every
statement makes an ordinary sequence of declarations quadratic.

`Template.Captures` is deliberately insufficient for completion: runtime closure
capture stores only names already used by the body. An editor-only declaration
environment root also retains outer names the user has not typed yet. This does
not enlarge runtime closures or alter their captured slots.

Selection inference records the actual receiver at `::`, including a missing or
partially written member. `factory()::nested::` therefore uses the inferred
returned Scope and its nested value, not a textual search for the last matching
identifier. Remote completion obtains a known Connection from this same result;
an ambiguous endpoint is not replaced with an older same-named binding.

## Uncalled and generic functions

Already instantiated generic functions reuse the real argument and result
information from ordinary specialization. Multiple real contexts at the same
source point combine their alternatives: a reliable member is available in
every applicable Scope shape, and endpoint identity is present only when those
contexts agree.

An uncalled body still exposes its parameters, declaration environment, and
complete local declarations. Explicit parameter annotations provide actual
constraints. An unconstrained generic parameter is an unknown **binding**, not
an unbound Path. It shadows an outer binding of the same name even before its
eventual call determines its type.

Editor probes reuse the existing expression and binding semantics. They do not
execute user code, authenticate endpoints, or introduce a second interpreter.
Unknown-dependent results stay unknown; unrelated known declarations remain
useful. If a binding initializer cannot be inferred yet, its pattern still owns
the names it declares, so recovery cannot leak older same-named values.

Artificial unknown arguments must not enter the real generic-specialization
cache or appear as actual call signatures. Probe state is separate from actual
specializations, and no recovered tree or editor frame can become a published
`.mso` or `.mexe`. Runtime and artifact compatibility are unchanged.

## Focused user journeys

The discriminating cases are completion in an unfinished named or anonymous
function; an annotated parameter and a called generic Scope parameter; an unused
outer binding; a local shadow and later restoration; nested Scope members;
constructor-returned Scope chains; and incomplete selector, argument list,
initializer, or closing delimiter. Tests apply the resulting source edits and
check ordinary compilation where the completed program is valid. Diagnostics,
hover, member completion, and endpoint selection must agree on the same source
generation and lexical context.
