# Lexical semantic contexts for editing incomplete source

## Typed HIR migration contract (0.8.1)

The Typed HIR migration separates editor analysis from executable lowering.
The language server already consumes `EditorAnalysis`, not bytecode `Module`;
its protocol does not need to expose backend slots or change for this boundary.
The new producer must supply the same views from a read-only semantic generation:

* Occurrences retain their source-owned range, type, signature, semantic role,
  member shape and known Path/Connection provenance.
* `At(offset)` selects lexical declaration boundaries and receivers, including
  incomplete generic bodies. Unknown parameters remain bindings rather than
  reverting to unbound Paths. Context tables retain scope identities rather
  than eagerly expanding every completion list.
* Definition locations pair the correct source document with each range.
  Standard declarations navigate to the same embedded source bytes through
  versioned `msync-stdlib` documents.
* Diagnostics and error nodes preserve useful neighboring declarations.
  Queries cannot mutate specialization/storage domains or emit bytecode to
  discover a signature. Builtin and standard metadata come from semantic
  catalogs, not executable module construction.
* A document's text, version and server generation own one analysis result.
  Background completion of an older generation cannot overwrite a newer one;
  close/reopen creates a new identity even when the LSP version restarts at one.

Selected physical Path completion remains a separate effectful operation.
Analyzing source, producing semantic tokens, hovering, navigating or formatting
must not enumerate remote directories, authenticate, execute a Process or
perform a transfer.

`tests/Msync.EndToEnd/hir_editor_journey.py` exercises these protocol views using
the real language server. Its published-0.8.0 baseline passed generic instance
types, uncalled/incomplete contexts, latest-generation selection, nullable flow,
error recovery, UTF-16 definition ranges, standard source navigation, exact
qualified edits, close/reopen and absence of source effects. The baseline image
is `10d27794ccf2dd4c5638fd6d77d3510de5b6f1dd61ac0e0557382e3cf36c32ad`.
The same journey will consume the coherent new compiler and final Native images;
the semantic-only API boundary is owned by the compiler rather than inferred
from the protocol test's absence of effects.

The coherent Typed HIR managed snapshot passed the same eleven protocol checks,
and the unchanged `StandardConceptTooling` host passed all 47 contracts against
that snapshot's Compiler/Core/HIR assemblies. This includes native aliases with
source parameter names, variadic tails, nullable concept field completion,
inline associated-type definitions and qualified standard concept composition.
The accepted Compiler image was
`824b4b9b8a779bbc3d3a40db76dffe1f5d02b9e361ca3ab76fac574d26da6a83`.
These are managed integration results; final Native and installed extension
acceptance remain separate workflows.

Frozen-assembly test hosts must resolve explicit HintPaths before candidate
assemblies discovered under their project directory. SDK default candidate
search can select an older DLL in a previous local build directory even when
the new HintPath and artifacts path are correct. For such isolated hosts, use
`AssemblySearchPaths={HintPathFromItem};{RawFileName}` and compare the resolved
Compiler hash with the snapshot receipt before interpreting a failure. An
apphost hash alone does not identify its managed dependency generation.

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
the former `where::(object::item)` syntax. Its current spelling is
`object::item @ where`: the inner selection remains lexical; `where` is the
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

A physical qualifier applies to the value produced by its left operand, not
merely to adjacent source text. Grouping can place a slash receiver inside that
operand: `(folder/) @ remote` still completes against the remote endpoint. Quoted
and whitespace-separated leaf values likewise retain their physical owner even
when the caret is separated from the trailing `@ remote` expression.

The collector records path-leaf and slash-expression identities during the real
inference invocation. On qualification unwind it follows only transparent value
flow: path joins, nested physical qualification, block tails, conditional result
branches and match-arm values. It does not enter calls, callable bodies,
conditions, discriminants, discarded statements or lexical Scope member-name
lookup. Physical ownership is attached to these value-flow leaves, not to a wide
outer source span that could make a call argument authenticate to the wrong host.

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

## Closed constraints and endpoint selection

Named `concept` declarations live in a separate lexical type environment, not
in the runtime binding table. Editor snapshots retain that immutable environment
alongside ordinary bindings. Only a type-completion position merges its concept
names into the completion list; a concept must not appear as an executable
variable. The same declaration identity resolves annotation navigation. Concept
outlines include associated types, field requirements and callable signatures.
Uncalled constrained functions receive a structural probe shape, so their field
completion remains useful before the first concrete call.

Cast targets in `value as Type` are type positions. `Number` observations are
numeric rather than Path observations; the editor never executes conversion or
constraint checking effects. `$std` and its named module scopes, including
unloaded module placeholders, retain named-Scope highlighting instead of
changing colour after their first load.

`path @ endpoint` associates the selected endpoint with value-flow leaves on
its left. Unlike the former prefix qualifier, it does not require a preceding
member-receiver selection. Path-join receivers and branch result paths inherit
the physical endpoint; the endpoint expression remains an ordinary lexical
completion position. This metadata is editor-only and does not authenticate.

`tests/Msync.Compiler.Tests --upgrade-editor` exercises concept highlighting,
type completion, declaration navigation and outlines, constrained generic member
completion, cast target completion, named standard modules and SSH-owned path
completion in one recovered-analysis workflow.

Persistent REPL display metadata uses `CompilerSession.DescribeConcepts()`
separately from `DescribeBindings()`. A `SemanticMember.IsConstraint` marker lets
the prompt retain concept highlighting and hints without offering those names
as executable values or treating them as bound filesystem operands.

Parameter identity is distinct from the identity of its argument value.
`Binding.IsParameter` preserves variable presentation for a Scope parameter even
when its actual argument is a named Scope. The editor records parameter origins
and reference spans without copying or weakening the inferred Scope shape.
Origin matching uses source-string reference identity plus the source span, not
just a filename and offset: accepted REPL submissions share a filename but can
reuse the same offsets for unrelated declarations. Function-valued parameters
retain function presentation. Associated type names are offered only in the
owning concept's type positions; they are not executable bindings.

The complete focused editor workflow passed against managed snapshot 4 on
November 9, 2026, with zero build warnings. The selected lexical/member lookup
check additionally matched all 16 exposed values after the parameter-role
change. These checks use shared product DLLs and compile only the test project;
actual protocol and native host journeys remain separate delivery evidence.
