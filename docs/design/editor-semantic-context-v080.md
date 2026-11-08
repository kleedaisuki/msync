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

The editor parse result also retains regex token spans from that same lexer
stream, including an unfinished regex's original lexical diagnostic span.
Semantic coloring consumes these ranges directly; regex content such as `fn`,
`scope` or `?` must not be reclassified as surrounding language syntax. Strings,
comments and ordinary comparison operators do not produce regex ranges.

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

## Static path values

Typed descriptors retain a concrete-or-unknown `KnownPath` or `KnownText` fact
for literals, aliases and Scope fields. These facts describe values, not
filesystem observations. Pure joins and physical qualification reuse
`PathSemantics`; no source is executed and no cwd, home directory, environment
variable or search-path lookup is frozen by inference. Contextual endpoint
projections therefore remain unknown when their result depends on execution.

Implicit Path/String conversions update both the inferred type and the literal
fact. A branch join or a polymorphic source occurrence retains a value only when
all alternatives agree. Type-shared generic parameters forget scalar and Range
item literal facts, so the first invocation cannot choose a later invocation's
completion directory. Exact immutable Scope identities retain their member
facts under the existing specialization contract.

Materializing derived Path values is editor-only; ordinary compilation does not
allocate extra joined path strings for presentation. Facts are not serialized
into artifacts or included in specialization keys. A slash selection records its
typed left receiver, allowing completion after a known path-producing function
without a second expression evaluator. Accepted REPL values require their actual
captured path context rather than projecting an old binding through a new cwd.

`tests/Msync.Compiler.Tests --static-paths` covers literal and nested member
aliases, pure joins and qualification, annotated and explicit returns, ambiguous
branches, generic isolation, incomplete slash receivers and byte-for-byte
artifact stability around editor analysis.

## Research connection

[Pelsmaeker et al., Language-parametric static semantic code completion
(OOPSLA 2022)](https://doi.org/10.1145/3527329) derives completion proposals
from syntax and static semantics, including scope-graph name resolution. Its
relevant engineering principle here is to reuse the language's binding and
typing rules instead of maintaining a second name-guessing implementation.
msync uses its existing persistent environments and compiler operations rather
than adopting the paper's general Statix/SDF3 framework. The paper's formal
soundness and completeness results do not automatically transfer to this
implementation; the concrete contracts above and real editing journeys are
the acceptance criteria.
