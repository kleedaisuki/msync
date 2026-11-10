# Lexical semantic contexts for editing incomplete source

## Typed HIR migration contract (0.8.1)

The Typed HIR migration separates editor analysis from executable lowering.
The language server already consumes `EditorAnalysis`, not bytecode `Module`;
its protocol does not need to expose backend slots or change for this boundary.
The producer supplies these views from a read-only semantic generation:

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
The same journey consumes the coherent new compiler; final Native acceptance
uses its published image. The semantic-only API boundary is owned by the compiler rather than inferred
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

The final affected managed source-workflow slice retained the committed
assertions and advanced only failing groups after causal fixes:

| Workflow | Accepted producer | Coverage |
| --- | --- | --- |
| `hir_editor_journey.py` | frozen17 | Eleven real LSP generation, type, source and no-execution checks |
| `nullable_scope_journey.py --lsp-only` | frozen18 | Six original source checks and three document versions, including exact returned-Scope field definitions |
| `standard_concepts_tooling_journey.py` | frozen19 | Real protocol, persistent REPL, progressive skill pages and Unix PTY qualified-type completion |

The nullable-member fix preserves the returned field's declaration identity
through concrete call arguments. Standard namespace and native query roles
come from semantic observations. A failed concept-policy argument points to the
actual argument expression (`scope` at character 52 in the EqualitySet fixture),
not the entire call; its related declaration remains in embedded standard source.
The final standard-tooling Compiler image was
`fa761090f8c015a9c1be2159e7e91cf06065ec5e4302d1f8f1be9547d2e402dd`.
Per-producer receipts and raw workflow reports are under `.temp/hir-final-ux`;
these managed checks do not substitute for the release lane's Native and
installed-extension workflows.

Frozen-assembly test hosts must resolve explicit HintPaths before candidate
assemblies discovered under their project directory. SDK default candidate
search can select an older DLL in a previous local build directory even when
the new HintPath and artifacts path are correct. For such isolated hosts, use
`AssemblySearchPaths={HintPathFromItem};{RawFileName}` and compare the resolved
Compiler hash with the snapshot receipt before interpreting a failure. An
apphost hash alone does not identify its managed dependency generation.

### Incremental observation projection and query ownership

A visibility point shares the binder's immutable lexical name-to-location map.
The observer separately maintains persistent projected location facts and capture
remappings. Opt-in `SemanticFacts` notifications record changed locations and
object regions; unchanged bindings are not walked at every statement boundary.
A region-to-root dependency index refreshes nested Scope aliases after writes,
while earlier source points retain their previous projected value IDs. Normal
headless analysis leaves the notification callback unset and allocates no
observation state. Freezing detaches callbacks and publishes only immutable maps,
IDs and presentation metadata, never a semantic context or inference dictionary.

Completion and item resolution can run concurrently over one document result.
`EditorSemanticIndex` therefore creates one query-local `SemanticValuePresenter`
per `At`, `Resolve` or occurrence projection. Its caches and recursion sets are
reused within that request, not shared between worker and protocol threads.

The matched managed probe in `tests/Performance/HirEditor` analyzes a partial,
uncalled generic function after ordinary Path bindings, then requests its lexical
context and known receiver member. Both images ran on .NET 10.0.14 with
`DOTNET_TieredCompilation=0`, three warmups and nine warm samples. Explicit frozen
HintPaths and the loaded Compiler hashes identify the actual inputs: baseline
`637bd09` used `cc80ad51f6bbc983e7133f0765c75bd9f15d26aa174bc3de4cd89b7fde835122`;
the Typed HIR candidate used
`46868919dc98d58f95b7d5b2c144ea25f643df57d8cc055f24deac27e47c35e6`.
Source hashes and visible binding counts matched, and no build/run/publish job
was active when the paired measurement began.

| Preceding bindings | 0.8.0 warm median | Typed HIR warm median | 0.8.0 allocated bytes | Typed HIR allocated bytes |
| ---: | ---: | ---: | ---: | ---: |
| 0 | 0.3614 ms | 0.3052 ms | 70,096 | 73,112 |
| 80 | 4.0297 ms | 2.0765 ms | 880,248 | 1,324,464 |
| 400 | 19.5824 ms | 23.2747 ms | 4,192,336 | 6,205,856 |

Before this change, the same candidate workload at 400 bindings allocated
13,373,072 bytes and had a 139.8788 ms exploratory median. That initial run
included concurrent builds, so its time is not a controlled end-to-end speedup
claim. Replacing the eager binding/point cross product materially reduced both
work and allocation. In the quiet paired run, allocation grew 4.69 times for
five times as many bindings, rather than quadratically. The remaining candidate
400-binding median is 3.69 ms higher and allocates about 2 MB more than 0.8.0;
the raw timing distributions overlap widely. This probe includes the new
semantic pipeline's metadata as well as presentation snapshots, so it does not
attribute all remaining allocation to the editor. Native LSP latency remains a
separate measurement. Raw samples live in
`.temp/hir-editor-component/comparison4.json` and `comparison5.json`.

The changed representation passed the original eleven real-LSP checks and all
47 existing standard-concept tooling contracts. Eighteen direct editor checks
include incomplete generic bodies, nested Scope aliases before/after mutation,
shadowed lexical identities and concurrent completion/resolution. The linked
immutable projection checks remain at 42, including release of HIR body arenas.

Indexed resources are a separate dependency family from object regions. A Buffer
write intentionally updates `SemanticContext.Indexed`, not every alias's Range
value or every containing Scope row. Observation therefore subscribes to indexed
fact changes and follows the singleton `IndexedRegion` or joined `IndexedAliases`
through fields, elements and captures. It refreshes only dependent roots. The
current element is read from each live resource description; an old embedded
`SemanticValue.Element` is not a fallback when resource identities are present.

### Declaration provenance and skipped pure-query operands

A Scope field's declaration origin is not its mutable storage location.
`SemanticScope.FieldDefinitions` is an optional immutable map from field names
to source-owned declaration ranges. Scope construction caches that map once per
bound construction descriptor and shares it between instances. Formal input
abstraction and dynamic value projection can clear private `Locations` without
losing navigation; actual argument substitution replaces the whole Scope
metadata, including its own origins. Same-shape instances never add source
origins to code identity or restore a callee-private cell alias. Structural joins
union the finite origins of common fields. The editor prefers these ranges and
uses the older location-derived origin only when no separate facet is present.

Pure metadata queries may resolve their native callee without analyzing that
callee expression as executable input. Their highlighting and hover therefore
also use the binder's real standard-module/member sidecars. `BoundStandardUse`
retains the parsed `std` name's exact span independently from the selected module
span; no source-text name guessing is needed. Module export descriptors and
native declaration identities provide missing query-operand observations
without inference re-entry, bytecode emission or execution. Program-owned
standard modules retain named-Scope presentation; `fn` and `scope` remain syntax
keywords.

A focused strict-frozen host passed 17 origin/role checks against Compiler
`659ab7fda953ab8bd2adfe4b6aea3df7eef9617a4382862f420a7640af2abaac`.
They include the user's direct/rebuilt nullable constructor example, distinct
actual field origins through one reused same-shape identity function, and
standard root/module/native-query roles with useful signatures and source
documentation. The original nullable workflow additionally passed all six
source checks and its three real language-server cases.

### Declared constraints in callable hover

Runtime kinds are not a complete callable signature: both a bare `Scope` and
`$std::concepts::Equality` have the Scope runtime kind, but only the latter
expresses the required policy operations. `SemanticTypeTables` therefore keeps
compact concept display names and associated `typename` binder names beside
its existing type/signature tables. The shared display formatter preserves
concept identity, nullable admission, true unions and nested Range elements.
Standard concept names are qualified using their actual bound module ownership;
local concepts retain their lexical names. No HIR expression/body arena is kept
for this presentation metadata.

Source callable help is projected from its owning bound declaration and actual
parameter constraints. Declaration origins are indexed once with the existing
initializer scan; template documentation is cached within that analysis.
Constraint help includes the concept's own source documentation and required
members rather than replacing it with a generic Function explanation. Native
aliases use the same type-name rules while retaining their declaration's
parameter names, result and documentation. This path never analyzes a tested
operation, emits bytecode or executes a query to obtain hover text.

The actual failed VS Code source fixture remained unchanged at 1,015 UTF-8 bytes
with SHA256
`ffac7323348ab1f3875b02c24ee044652cbeffd10e0b0ebc0528244afe091a3d`.
Against managed Compiler
`cebc3525bc12635af4be873a842319bc1c2a87f4f18e9e5cd8ac2201ccbbdc68`,
the real LSP hover at its `with_equality(` now contains
`equality: $std::concepts::Equality`, the equivalence-relation documentation and
`equal: fn(left: typename Self, right: Self) -> Bool`. The focused direct host
passed eight checks covering that policy constraint, owning-field documentation,
nullable/local concept names, nullable associated types inside Range signatures
and native aliases. Its loaded Compiler hash matched the frozen receipt. The
fixture witness is `.temp/hir-equality-hover24/report.json`; installed extension
acceptance remains a separate workflow.

Semantic joins retain their real finite facets rather than a historical binary
`first`/`second` graph. The observer reads the joined Scope fields, Range element,
callable candidates, indexed resources and their captures directly. Dependency
tracking likewise follows only those reachable facts. Multiple callable
signatures are projected from the finite `Callables` set into presentation-only
`SemanticValueView.Alternatives`; those UI variants do not retain semantic join
history. Native variants keep their own source parameter names and result types.

Actual type alternatives remain in `HirType.Alternatives`. When no explicit
merged Scope fields are present, the presenter intersects the finite type rows
and retains each common field's alternative type keys, including nested rows.
Null alternatives do not remove a nullable Scope's capabilities, while a
Function-or-Scope union does not gain Scope-only fields. Recursive presentation
still terminates by type/value identity rather than by discarding variants or
imposing a depth limit.

After semantic history removal, managed Compiler
`bb4456050510f8d81411f5c925bb5cf7c4cddd0aaff732b2cf9f1d07b0ecdfc0`
passed 35 source-editor checks: the previous 29 plus distinct source/native
callable signatures and nullable callable narrowing. The strict frozen host's
loaded Compiler hash matched the producer receipt. The linked projection checks
passed 45 assertions, adding nested true-union common fields, nullable Scope
capabilities and rejection of Scope-only fields on a Function-or-Scope union.

The projected `CurrentType` is read-only presentation metadata. It can express a
fresh Range element or the union of several resource elements without interning
a type into a sealed HIR module or re-entering semantic analysis. Stable HIR type
IDs and fixed storage constraints are unchanged. Membership snapshots have no
live indexed identity, so they retain their original element metadata, and old
source points keep their earlier immutable projected IDs.

The published 0.8.0 Native baseline displays `Range<Number>` for a Buffer and its
ordinary alias after the first Number write, and `Range<Never>` for an earlier
empty membership snapshot. Its nested Scope hover incorrectly retained
`Range<Never>` for the same live Buffer; the new dependency projection corrects
that stale member too. A focused actual baseline report is kept under
`.temp/hir-editor-indexed-baseline/report.json`. Against managed Compiler
`1dcbaba83cf2b92fd144a8c813c224f2a2f87784f44efcf490a7c30a658ba5c2`,
the direct editor checks now pass 29 assertions: the previous 18 plus live
carrier inference, aliases, nested Scopes, old source points, snapshots, joined
resource identities and a Buffer returned from a source function. The strict
frozen host's loaded Compiler hash matched that image.

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
offset, a shared immutable lexical name-to-location map and an independent
projected-fact snapshot. The innermost owner
containing the caret wins; within it, the last applicable declaration boundary
wins. Before a binding's initializer, the old binding is still visible. After
the declaration, its replacement shadows that name. Leaving a block restores
the outer root. Future declarations and sibling function locals are not visible.

Keep roots, not expanded completion lists, in this table. Materialize and render
the selected environment only when queried, sharing descriptions within the
query-local presenter. Eagerly rendering every visible binding at every
statement makes an ordinary sequence of declarations quadratic.

`HirTemplate.Captures` is deliberately insufficient for completion: runtime closure
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

The binder has already recorded generic bodies, type variables, constraints and
lexical declarations. The observer projects those bound facts without inventing
a call, checking a speculative body or emitting bytecode. Unknown-dependent
results stay unknown; unrelated known declarations remain useful. If a binding
initializer cannot be inferred yet, its pattern still owns the names it declares,
so recovery cannot leak older same-named values.

No artificial unknown arguments enter the real generic-specialization cache or
appear as actual call signatures. Partial editor generations publish semantic
views only; they never become executable `.mso` or `.mexe` programs.

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
must not acquire later mutations by sharing a mutable inference payload. See
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
Uncalled constrained functions project their bound requirement descriptors, so
field completion remains useful before the first concrete call without probing
a speculative invocation.

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
`SemanticBindingView.IsParameter` preserves variable presentation for a Scope parameter even
when its actual argument is a named Scope. The editor records parameter origins
and reference spans without copying or weakening the inferred Scope shape.
Origin matching uses source and logical declaration IDs, not just a filename
and offset: accepted REPL submissions share a filename but can reuse the same
offsets for unrelated declarations. Function-valued parameters
retain function presentation. Associated type names are offered only in the
owning concept's type positions; they are not executable bindings.

The complete focused editor workflow passed against managed snapshot 4 on
November 9, 2026, with zero build warnings. The selected lexical/member lookup
check additionally matched all 16 exposed values after the parameter-role
change. These checks use shared product DLLs and compile only the test project;
actual protocol and native host journeys remain separate delivery evidence.
