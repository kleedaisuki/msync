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

### Nullable constructor results retain their actual member shape

`fn -> Scope { scope { item: path } }` retains its inferred member metadata;
the return annotation checks the value, not an erasure of its shape. A parameter
annotated `Scope` likewise keeps the actual argument's members during generic
specialization. An `if` without `else` joins that shape with Null, and
`if not object?` narrows it back to Scope because postfix `?` means is-null.
The Null predecessor must not contribute an empty conflicting shape. This was
the cause of the reported `object::item` failure in older server binaries, not
the return/parameter annotations or the parentheses in
`where::(object::item)`. The inner selection remains lexical; `where` is the
physical Connection.

`nullable_scope_journey.py` keeps both reported source variants verbatim for
CLI checks and actual LSP diagnostics, hover, completion and definition. It
changes an uninstantiated generic declaration into the direct-return and rebuilt
Scope variants in one document, checking that field origins follow the current
variant. Controlled execution substitutes an owned temporary Path and local
Connection, then checks both exists edges at O0/O2/O3 through source, `.mso` and
`.mexe`; it never executes the user's home-directory or WSL transfer.

The VS Code client launches `msync.serverPath` (default `msync` resolved from
PATH), rather than embedding another compiler implementation. Installing an
updated VSIX alone does not replace a separately installed old server; acceptance
must use the intended Native executable and restart the language server.

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
completion directory. Scope identities retain their declared member types, but
mutable member values require reaching-write facts: an alias store or relevant
callee write invalidates old literal/path facts. A persistent lexical snapshot
must not acquire later mutations by sharing a mutable Info payload. See
[mutable state and effects](mutable-state-and-effects-v080.md).

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

## Physical path-flow ownership

A physical qualifier applies to the value produced by its right operand, not
merely to adjacent source text. Grouping can place a slash receiver inside that
operand: `remote::(folder/)` still completes against the remote endpoint. Quoted
and whitespace-separated leaf values likewise retain their physical owner even
when a lexical completion fragment no longer includes `remote::`.

The collector records path-leaf and slash-expression identities during the real
inference invocation. On qualification unwind it follows only transparent value
flow: path joins, nested physical qualification, block tails, conditional result
branches and match-arm values. It does not enter calls, callable bodies,
conditions, discriminants, discarded statements or lexical Scope member-name
lookup. Physical receiver ranges are clipped to these value domains, so a wide
outer source span cannot make a call argument authenticate to the wrong host.

Invocation checkpoints keep independent generic contexts separate; outer known
qualifiers replace inner known endpoint ownership only within that invocation.
At the caret, `HasPhysicalOwner` distinguishes ordinary local values from an
opaque physical operand, and `PhysicalEndpoint` is present only when all
applicable concrete contexts agree. Unknown/contextual inner projections cannot
be turned back into a literal directory by an outer known endpoint: `$env` and
other contextual projections may change the actual path spelling. Known path
receivers reuse Core `PathSemantics.Qualify`, including its cwd/home/projection
ownership rules. Ordinary compilation records none of this editor-only state.

`tests/Msync.Compiler.Tests --qualified-contexts` covers grouped/quoted values,
aliases, nested local/SSH/WSL qualification, result branches, non-value-flow
boundaries, lexical member lookup, opaque and polymorphic owners, and unchanged
relocatable artifact bytes. The real SSH journey uses distinct local and remote
cwd entries so a successful query to the wrong filesystem cannot pass.

## Finite editor specialization

Malformed or incomplete syntax can create an unknown-dependent recursive
specialization chain with new inferred argument shapes. Editor inference tracks
active FunctionExpr identities from the finite parsed document. An already
reserved specialization key is always reused first, preserving ordinary
same-key recursion; only a new specialization re-entering an active source
function returns its declared result type or an opaque unknown result. There is
no arbitrary depth limit, and normal source compilation maintains no active
editor set. Editor call frames reuse the full declaration environment for
visible names, while executable capture slots retain their original ownership.

`tests/Msync.EndToEnd/lsp_probe_recovery.py` opens the actual malformed keyword
parameter case in a separate LSP process, checks its diagnostic and unrelated
Scope hover, then applies a valid change and checks a Bool hover in the same
surviving process. This tests user editing recovery, not only a caught exception
inside a unit-test process.

## Semantic definitions and structural outlines

Navigation uses declaration identity from the same recovered syntax tree and
lexical environments as inference. An editor Binding retains its own declaration
location, separate from its value's initializer. A later alias therefore points
to the alias declaration, while a captured name still points to the declaration
that the function captured before a later shadow. Parameter slots retain the
parameter's name span. Scope field origins belong to the field key rather than
the function or Path used to initialize it; joined Scope shapes retain all
applicable field locations instead of guessing one declaration.

Editor reference locations are recorded when actual name and member resolution
succeeds. Unbound atoms remain Paths and do not acquire links to later same-named
variables. Native standard-library declarations keep their versioned
`msync-stdlib` source locations. Origin objects and Scope origin maps are created
only for editor analysis; normal runtime IR and artifact encoding are unchanged.

Document symbols are collected from parser nodes, not a declaration-shaped
regular expression. Strings and comments cannot manufacture outline entries.
Each named declaration has its full node range and a separate name selection
range; nested parameters, local bindings and Scope fields belong to its children.
The server emits this hierarchy for capable clients and flattens the same tree
into SymbolInformation locations for older clients. Absent optional details are
omitted. Pending definition/outline requests follow the same current-generation
analysis lifecycle as hover, without reparsing on the protocol reader loop.

The actual stdio acceptance runner is
`tests/Msync.EndToEnd/navigation_journey.py`: parameter/global shadowing, lawful
`.codex` names, old captures, aliases, Scope fields, future unbound Paths, Unicode
coordinates, virtual standard-library definitions and complete/incomplete
outlines in both protocol capability modes. Compiler `--navigation` checks the
same origin and structural range model independently of JSON conversion.
