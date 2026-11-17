# Finite contracts, not copied heaps

2026-11-12. Proposed compiler representation and a conditional recursive-summary
argument; **not implemented or a claim of language-wide convergence**. This note
extends the concrete failures in [the recursive-query investigation](performance-components-v080.md#native-09-recursive-query-investigation-2026-11-12)
and the independent `.temp/semantic-fingerprints/review.md`. It concerns compiler
facts only. Native objects remain static builtin layouts and addresses; no new
VM object, allocator, permission state or runtime validation layer is proposed.

## The result to pursue

An append-only accumulator need not create a new code specialization for every
length. Its *current heap* grows; its source storage contracts and the code's
field accesses do not. A recursive summary must describe changes to those
contracts, rather than contain a copied post-call heap.

For the existing BTree example, a promising finite interface is the `_Chain`
root's storage/logical contract:

```text
first, last : Scope or Null
count       : Number
seed        : Range<Item>       // includes the actual associated Item obligation
```

The created link has fields `value`, `previous`, `next`; its shape/type
obligations are separate schema facts. `visit_range` returns Null on each normal
exit. The `output.last.previous...` contents do not determine either ABI.
`_Chain.node` performs an explicit checked conversion before dependent accesses;
an opaque Scope carrier is not proof that every referent has Node layout.

This does **not** justify the old active-input matcher. Code checked for one
precise heap B cannot simply be reused for an invariant I containing B. Analyze
the body under I and close its return/effect obligations, then apply that summary
to B. In particular, replacing the recursive call by `effects=[]` leaves facts
that the real call may change and is unsound.

## One authority for each identity

The current `SemanticScope` contains Layout, Locations and recursive Fields
snapshots; `SemanticContext.Objects` and `Values` additionally hold current data.
That duplication explains why keys, FormalInput, projection and publication must
reconstruct graphs and choose between old versus current field values.

Replace the cause, not each traversal:

| Identity | Owned immutable information | Program-point information |
| --- | --- | --- |
| SchemaId | Fields, source declarations, storage contracts, checked views, callable/member obligations, associated Type equations; recursive edges are IDs | None of a caller's heap contents |
| PlaceId | A cell, or `(base, FieldId)`; capture environment role and concrete/symbolic base are explicit | Alias/binding relation and current fact |
| FactId | Builtin alternatives, schema constraints, callable templates/environment references, Type denotation and optional known payload | The environment maps each place to one fact |

The desired Scope fact is `(SchemaId, BasePlace)`, not another tree of field
values. A field read derives its place from the schema and reads the single
environment. Logical SchemaId is not physical native-layout identity: mutable
Scope referents can change while the address carrier's storage remains fixed.

Retain source DefinitionId separately from PlaceId. A lexical declaration is not
the cell allocated by every invocation of that declaration. In particular,
`(environment root, capture declaration)` must not collapse all closure instances
into one cell. Use typed IDs, not several untagged integer universes.

### Values without allocated locations are not a second heap

* A Scope *type/prototype* is a schema and a constructor-fact recipe, not a fake
  mutable region or a collection of freshly allocated HIR cells.
* A returned fresh Scope records its constructor/allocation site and generative
  result root. Its field facts come from the same transfer's stores.
* A host `zip` record uses the existing host schema and an element constructor
  mapping `first`/`second` to input element facts. No Any placeholder is needed.
* Function-valued fields retain callable templates and symbolic environment
  references. Range facts retain item schema and constructor/selector facts, not
  recursively copied entire element heaps.
* Editor definitions/literals are immutable payloads or symbolic entry/exit place
  projections in the same results. They are not part of a type-only cache key.

These recipes describe facts, not another executor. They must be consumed by the
existing canonical abstract transfer. Checker, partial/editor query and residual
code publication consume facts/obligations separately.

### Capture modes must keep their actual meaning

| Capture | Frozen at capture | Still shared/live |
| --- | --- | --- |
| Location | The identity of the captured cell | Its current value, and any reached heap |
| Value of a primitive | The primitive value | Nothing in that primitive |
| Value of a Scope reference | The address/reference value | Fields of the referenced allocation |
| Value of a function | Function/environment descriptor | Any location captures in that environment |

A Value capture of a Scope does **not** snapshot its fields. A Location capture
does not become a nested frozen value graph merely because a summary is cached.
The existing borrowed-return transport fix illustrates the required symbolic
place substitution, but does not solve accumulator recursion by itself.

Transport is not summary application. Alpha-transport of an already chosen input
graph must preserve its node/alias bijection, frozen fallback payloads and capture
modes; it must not follow a newly mapped live cell or apply effects while copying.
Only summary application intentionally reads/substitutes actual caller cells.
Sharing the ID-renaming primitive is useful; reusing an effect/result projector
as the input transport changes the mathematical operation.

## Three distinct objects: code key, summary, caller state

1. **Code key.** Template, physical parameter/capture ABI, scheduling/capture
   modes and static evidence actually consumed by code generation: member views,
   associated Types and callable alternatives. Do not include the current linked
   list's length or fresh concrete region numbers. A known input fact can be
   omitted only when code is checked under the broader retained contract; an
   optimization that bakes it into code must retain the corresponding premise.
   Static language observations additionally need an observational-equivalence
   premise, not merely successful type checking under that broader contract.
2. **Summary.** Formal places, complete read/write/effect footprint, normal-return
   facts and symbolic result origins/constructors. It is parametric in actual
   alias identities. Never cache an actual caller environment in it.
3. **Caller environment.** Actual places and their current facts. Applying the
   summary substitutes formal places and updates exactly the may-written cells;
   unrelated facts remain unchanged.

One symbolic root per formal does not assert that formals are disjoint. A caller
substitution may map two roots to the same actual place unless a proved premise
requires otherwise. Conversely, a normalizer's fresh IDs are not evidence that
actual roots are distinct. Unknown referents mean possible compatible aliases,
not an empty set.

Write effects that depend on entry versus exit reads need those versions. The
example `x=1; return y` cannot cache the old y as its result when x and y may be
the same cell. A symbolic write to x followed by an exit read of y has the right
meaning under either alias substitution. A type-only result fact alone cannot
replace required result provenance.

## A conditional finite-summary theorem

Fix a program and one admitted family of code keys K. Assume:

1. K is finite. Its schema/type evidence is a finite graph or parameterized
   contract, not a fresh expansion for each recursive actual heap.
2. Each key has finitely many abstract place families P: distinguished formal
   cells/regions, source allocation/constructor families and their declared
   fields. Repeated concrete allocation does not mint another analysis variable.
3. Facts, alias information, return control and effects form a finite-height
   lattice L. Transfers and summary application are monotone and sound. Exact
   known primitive constants can use the usual flat constant lattice; infinitely
   many possible constants do not themselves imply infinite height.
4. The footprint includes transitive callees, callbacks, captures, indexed writes
   and deferred effects. Absence is proved, never inferred from missing data.
5. Mutable storage and member/associated-Type obligations remain invariant;
   retained logical facts follow the same explicit checked-view rules as source.
6. Every static observation that the generated body uses is retained or is
   parametric in formal evidence. In particular, replacing a precise input B by
   I preserves the observation, not just the ability to typecheck the body.

Define F on the product of per-key summaries by interpreting each body's CFG
with the current summaries of its callees. Starting at bottom, iterate

```text
S[k] := S[k] join AnalyzeBody(k, S)
```

and re-enqueue dependents only when a summary changes. Because the product is
finite-height and updates increase monotonically, this terminates. At the fixed
point `F(S) <= S`. Sound local transfers and call substitution then prove, by
induction on the depth of every *finite* concrete call tree, that each terminating
execution's result/effects are included. Divergent executions need not be
invented as returning. Resource/effect prefixes still have to be represented;
normal-return summaries alone do not authorize deleting effects before divergence.

**Important operational consequence:** intermediate bottom return information
cannot permanently prune following code. Record the dependency and revisit it
when the callee changes. Publish/prune residual code only after closing its SCC,
or use an independently proved closed return contract without claiming that its
effects are empty. This is precisely what current incomplete `FunctionInstance`
handling does not establish.

If each summary has height h, at most `|K|*(h-1)` strict per-key increases occur;
this is a termination bound, not an acceptable implementation complexity bound.
Use sparse actual dependencies/places, not dense whole-heap copies per iteration.

### What the theorem does not assert

Finite per-summary height does not bound infinitely many *keys*. Interning copied
heaps merely assigns IDs to infinitely many different inputs. Allocation sites
alone also do not prove singleton locations. Arbitrary logical Type construction
or genuinely polymorphic recursive specialization can generate infinitely many
distinct type arguments even with finite physical ABIs. The proposed schema
parameterization/observation discipline needs an implementation proof for each
such feature; this note does not claim a complete finite precise analyzer for
the entire language. Losing associated-Type evidence, returning Any, rejecting
previously accepted programs or adding a runtime generic-object path is not a
valid way to satisfy a finiteness assumption.

## The smaller constructive BTree step

A first implementation can close the actual accumulator recurrence without a
general shape verifier or a library-name special case:

1. Compute recursive call SCCs and a complete typed write footprint from bound
   operations and known callable/callback choices. Existing BoundCallableWrites
   is useful evidence, but its declaration IDs must be instantiated as formal
   places and its Unknown cases cannot silently become no writes.
2. Form an input invariant from existing root schemas and writable contracts,
   retaining seed/Item and all logical/member obligations. Do not import current
   first/last descendant snapshots into that contract.
3. Analyze one SCC under this invariant. Every store must satisfy its fixed
   storage and checked-view obligations; every recursive edge must admit the
   same invariant. Resolve normal result/effects to a post-fixpoint.
4. On a call with more precise facts B, preserve each fact outside the substituted
   may-write footprint. For written places, apply the proved post fact/constructor
   or project to the corresponding invariant, preserving symbolic aliases and
   entry/exit origins. This is typed local forgetting, not whole-context havoc.
5. Reuse the same code key for empty, one-link and arbitrarily long histories.
   `_Chain.take` still executes the actual runtime links; this optimization does
   not replace the data structure or truncate its contents.

For `push`, one successful call writes root first/last/count, the prior tail's
next when non-null, and fresh link fields. Repeated pushes compose into a fresh
segment whose interior can be represented by a recursive schema/constructor
family. The entry tail remains a distinguished root; old non-tail cells are
framed unless another callback's footprint reaches them. Tail values and exact
counts are runtime data, not new code keys. A footprint that merely says
“all Node fields” is weaker and must not be sold as an exact frame proof.

`visit_range`'s unannotated normal exits are syntactically Null (including early
return and the final loop statement). Prove that with the same bound transfer;
do not invent a source annotation or let unfinished Never erase the append after
the first recursive edge. Complete callbacks can extend the footprint; an
unknown callback is a missing proof condition, not permission to assume purity.

### A checkable post-fixpoint certificate, not another shape-analysis engine

For this class the cheaper route is to propose a candidate summary from existing
contracts, then verify it, rather than enumerate increasingly precise heaps:

```text
candidate = (input invariant I, typed may-writes W, normal result Null)
check each body in the call SCC under the candidate callee contracts
require: all recursive inputs satisfy I; actual writes are included in W;
         successful stores preserve I; all normal returns satisfy Null;
         static observations are uniform or explicitly parameterized
```

The recursive Hoare rule justifies this simultaneous post-fixpoint check: for
each finite call tree, replace assumed child contracts by the induction hypothesis
and use the checked body. This proves partial correctness, not that recursion
terminates. Applying the candidate contract at a recursive edge still updates W;
it is not an optimistic identity transformer.

Both root schemas and write-target families must come from the same bound source
authority. Syntactic call/closure/callback edges select the SCC; existing checked
member/storage constraints type the write selectors. A target may denote several
concrete cells, so updates are weak unless singleton identity is proved. Closure
and alias paths may add compatible targets; an Unknown target cannot disappear
because it has no normalized graph node.

Static observations require particular care in this implementation:
`SemanticAnalyzer.Operations.TypeOf` calls Analyze/FlowType and emits a TypeValue
constant. A body observing `decltype(output.last)` can distinguish an input whose
current last fact is Null from the broader Scope|Null invariant. Analyzing it
under I and baking a different Type constant is not a valid optimization even if
both bodies typecheck. Record dependencies of TypeOf, member/associated metadata,
static callable selection and layout decisions in the same transfer. Retain
their exact evidence in the code key, or pass/project existing formal evidence
without changing the observation. Do not invent a runtime TypeOf check as a
substitute: actual runtime kind need not equal the source's static union type.
For the proposed BTree certificate, the testable hypothesis is that these static
dependencies come from root seed/Item and local declared Node contracts, not the
accumulator's current previous-chain history. This remains to be checked on the
actual bound program.

The BTree typechecker does not need to prove that links form an acyclic list, or
that count equals its length. Those are runtime application invariants checked
by the independent journey. It needs the fixed writable carriers, Item evidence,
effect frame and existing checked Node access. Therefore adopting a general
list-segment/TVLA engine before trying this certificate would add machinery for
a property the compiler is not being asked to establish.

The first discriminating implementation experiment is one generic certificate
builder plus one ordinary transfer under I, recording the proposed versus
observed footprint, schema/key count and unfinished-summary dependencies on the
minimal empty-range reproducer and original 745-mutation input. It must not test
template names, recognize `_Chain`/BTree, add annotations, or bypass failed
obligations. Acceptance/precision for other source patterns still has to be
established; this proposed certificate is not a claim that all logical evidence
can already be parameterized by the current compiler.

### Destructive examples that the implementation must distinguish

* An input with `last = Null` is more precise than the invariant. After a
  recursive append, retaining that Null fact is wrong even though the invariant
  is preserved.
* `x=1; return y` with x=y differs from x!=y. Formal parameter names do not prove
  separation, and a cached entry snapshot does not represent an exit read.
* Two allocations at one source site are not the same object. Strong-updating a
  summary node's `next` to non-null loses an older node's still-null next. Weak
  update or a proved distinguished singleton is required.
* Distinct closure environments may own cells from the same lexical declaration.
  Folding them by that declaration can change aliasing. Environment role belongs
  to place identity, not to source-definition metadata.
* Different associated Item types, location/value capture modes, writable
  carriers, checked views, Range item contracts or future callable domains must
  not share a summary unless parametric substitution proves the obligation.
* Two inputs can typecheck under the same invariant yet have different static
  TypeOf results. Static-observation preservation must be checked independently
  of storage safety and runtime effect soundness.

The small executable witness `tests/RecursiveScopesV090/summary_model.py` exercises
these principles on ordinary finite heaps. It checks frame preservation across
append histories and counterexamples to identity effects, forced-disjoint
formals and strong updates of allocation-site summaries. It is not a replacement
for the compiler or the actual BTree journey.

The separate actual CLI control
`python3 tests/RecursiveScopesV090/static_observations.py PATH_TO_NATIVE_AOT_CLI`
checks this static-observation obligation through O0/O2 source/object/bound routes.
For `observe(x) { decltype(x::last) as String }`, a precise Null field produces
`Null`; an input-dependent `Scope?` field produces `Scope?` even on its runtime
Null branch. The non-null branch also produces `Scope?`, not dynamic `Scope`.
All 18 routes pass on the preserved 580-input Native AOT image; this is an actual
language counterexample to observation-erasing widening, not proof that a proposed
recursive summary preserves observations. Source/object inputs are deleted before
bound execution. Receipt: `.temp/static-observations/4d1457bf012c45bc9f4f99d318cbe352/result.json`.

Run `python3 tests/RecursiveScopesV090/summary_model.py`. Its 8,748 cases enumerate
all previous/next links over `{Null, node1, node2}`, all first/last roots over the
same set, two initial count values and zero through five appends. Thus old cycles
and disconnected roots are included, not silently ruled out by a list invariant.
Root versus Link identities are separated by this toy model's constructors. That
is a model precondition, not evidence that an actual source root/descendant or
seed field is disjoint: real framing must follow fresh-constructor provenance,
compatible aliases, static observations and complete normal/failure/deferred
write prefixes, never parameter names, schema names or normalized IDs.
The typed summary key stays constant through twenty append histories while the
exact previous-chain key grows. A two-procedure recursive footprint model reaches
its post-fixpoint in three rounds (two strict increases). Eight explicit negative
witnesses check the alias/capture/incomplete-effect conditions above. Results go
to `.temp/semantic-summary-model/result.json`; the test is fully typed development
infrastructure, not a product dependency or a second language implementation.

## Deletion and acceptance boundary

Start with Scope schema/base-place ownership and one environment. Preserve one
formal-place substitution, the actual checker and one abstract transfer.
Delete recursive field snapshots and then the import/key/publication traversals
whose only purpose was reconciling those snapshots. Do not add the proposed IDs
beside all old fields and call the resulting adapter stack the completed model.

Acceptance is the unchanged original 745-mutation source plus source-free HIR,
strict object/bound routes and independent output/tree invariants, together with
closure/alias/member/associated-Type/editor controls. Measure compile time,
allocated bytes and published schema/function counts on one causal frozen image.
The original 180-second limit remains. No current experiment in this note proves
that acceptance or the project's hard 30% net deletion target.

## Relevant external mechanisms, not imported guarantees

* [Cousot and Cousot, POPL 1977](https://www.di.ens.fr/~cousot/COUSOTpapers/POPL77.shtml)
  supplies the abstract-interpretation/post-fixpoint basis; the conditional proof
  above still depends on this compiler's transfers and finite key space.
* [Frielinghaus, Seidl and Vogler, SAS 2016](https://arxiv.org/abs/1606.07687)
  explicitly separates unbounded summary variables from ascending abstract
  values. Better iteration cannot repair a key that keeps creating variables.
* [Sagiv, Reps and Wilhelm's shape-analysis report](https://research.cs.wisc.edu/techreports/viewreport.php?report=1383)
  explains finite descriptors of unbounded mutable heaps. Its precision and
  singleton/multiplicity distinctions matter; adopting a full TVLA engine here
  is not the recommendation.
* [Infer's production account of local reasoning](https://fbinfer.com/docs/separation-logic-and-bi-abduction/)
  motivates preserving the frame and composing local procedure footprints.
  It does not justify inferring disjointness from missing aliases.
* [Stein, Chang and Sridharan, TOPLAS 2024](https://www.bennostein.org/toplas24.pdf)
  demonstrates sound demanded recursive summarization and incremental result
  consistency. This is relevant after one fact authority exists, not a reason
  to put another dependency cache around copied SemanticValue heaps.
* [Rust's 2026 polymorphic-code experiment](https://github.com/rust-lang/goals/blob/main/src/2026/polymorphization.md)
  separates fixed data layout from duplicated code while retaining associated
  types and identity observations. It is an experiment, not a result to claim
  for msync; importing its hidden-vtable mechanism is neither necessary nor
  recommended for the already statically linked layouts here.

## Alias-key and untouched-frame discriminators (2026-11-12)

The unmerged canonical-input prototype exposed an alpha-invariance defect in the
key encoder: sorting `AliasViews` by source region numbers before assigning
canonical numbers produced `V2V4` versus `V4V2` for the same rooted graph. The
candidate now emits scope alias relations after ordinary graph paths anchor
identities. External-only targets are numbered by their complete incidence to
anchored sources, retaining edge kind and multiplicity of distinct targets.
Equal-incidence external nodes are interchangeable because this relation has no
edges with an external-only source. This is **not** a general graph-isomorphism
algorithm, nor permission to use sorted local neighbourhoods for arbitrary
recursive graphs. [Nauty/Traces' canonical-labelling discussion](https://pallini.di.uniroma1.it/Introduction.html)
provides the broader distinction between partition refinement and canonical
labelling. The candidate property probe covers allocation permutations, shared
versus disjoint external targets, edge kind, capture mode, detached environment
cell partitions, and static Type denotations. It does not solve recursive SCC
admission or the outstanding allocation regression.

A separate, source-reachable frame defect has a bounded main-line correction:
`InvalidateScopeAliases` used to clear literal, Type-denotation and observation
facts from *every* field after a write to one field. For a range containing two
Scopes `{n: Number, Item: Number-as-Type}`, writing the selected view's `n`
therefore made an annotation using the untouched original `Item` fail. Update
only the written field's fact/location, retaining all other fields and source
metadata. Source Scope construction derives field names from each actual HIR
location name; record construction allocates a fresh location per field. Thus
source field names cannot disguise different names for one field cell within a
Scope. The regression journeys are `alias-untouched-Type-frame` and the real
`nested-captured-scope-write` execution oracle in `ShapeRegressionChecks.cs`.
The latter replaces an earlier misleading analyze-only claim that an unknown
alias target implied an empty write domain; it never established that claim.

A graph-import probe also demonstrates that the older normalizer loses an
existing caller `AliasViews` relation unless it happens to recreate it from
source-set overlap. The candidate transports the admitted reachable relation
with one source-to-formal region substitution, including external family nodes.
That invariant counterexample alone is not proof of a source-visible main-line
failure. In particular, simple single-element range write/read controls still
execute correctly on the frozen main control. Missing alias evidence must not
be interpreted as `NoAlias`; [LLVM's alias-analysis contract](https://llvm.org/docs/AliasAnalysis.html)
similarly distinguishes `MayAlias` from demonstrated non-aliasing. Empty-source
summary handling and complete unknown/TOP semantics remain separate obligations.

The source counterexample was subsequently sharpened: pass the selected view
and its actual original Scope to `alter(view, original)`, write
`view::Item = String`, then use `original::Item` as a Type annotation. Frozen main
accepted and executed the annotated Number result, retaining the stale Number
witness after a possibly aliasing write. A separate same-shaped third Scope must
remain a valid static Number witness. The correction registers **all** admitted
Scopes, including ordinary concrete ones, in the actual-source→formal-view
incidence relation, rather than relating only two Dynamic views. A reverse
incidence index constructs the same explicit overlap relation without a quadratic
pairwise scan. Transport existing caller `AliasViews` through the same region
bijection, admitting reachable external-family nodes; forwarding through another
function must not erase that relation.

Body admission and key construction share this premise. Main's existing
`CodeSchema` fallback and capture/type policies are retained, but the alias
relation is included in the key using the deferred canonical encoder described
above. Otherwise a specialization first checked for a known-disjoint third
Scope could bypass the new check when called with the aliased original. This
bounded alias/frame correction is independent of the still-unmerged one-pass
input-graph representation. Session regressions exercise both call orders,
failed-submission reconciliation and subsequent legal calls. Regular source/HIR
journeys additionally check forwarding. A persisted forwarding function calling
a future-rebindable session target has a separate deferred-call contract; its
broadened Type witness must not be mistaken for a proof about concrete aliases.

### Candidate next discriminator: code premises versus external alias arguments

The faithful-alias correction exposed a severe associated-BTree recurrence
regression (the old two-insert sample exceeds the unchanged300-second gate).
Borrowing alias-set views instead of allocating every alias edge as another
HashSet removes a concrete extra graph, and representing external families by
IDs avoids their field-heap copies, but neither establishes convergence.

A narrower *conditional* interface may separate these two ownership classes:

1. `R` is the set of places reached through actual arguments, captured values,
   callable environments, field/element values, deferred-write recipes and
   lexical Type-witness reads. It includes every fact from which the checked
   body can compute an executable/static observation; it is not a syntactic
   whitelist of field names.
2. Code validity depends on current facts on `R` and the may-alias relation
   **between members of R**. A write to x weakly updates each readable y in R
   that may alias x; static Type facts and untouched frames retain their usual
   obligations.
3. External-only family identities are substitution arguments for replaying
   `Field(formalBase, field)` against the actual caller alias expansion. If no
   body read/result/capture can turn one into an additional readable value,
   their cardinality and external-to-external graph topology need not be code
   premises. Their relation/identity is still retained by transport and effects,
   not silently deleted or treated as disjoint.
4. Nested calls and deferred callables must satisfy the same closure property.
   A demanded external fact extends the readable interface before reuse, using
   entry-owned immutable facts, never a retained live caller context.

This is not implemented or an acceptance result. The obstacle is explicit:
`AliasViews` currently mixes concrete source incidence, view overlap and
may-alias edges in one untyped integer relation. Shared external incidence must
still induce the correct overlap among admitted formals; plain transitive
closure of arbitrary MayAlias edges is not a proof. Existing negative
external-topology properties remain transport-identity obligations and must not
be weakened merely to obtain code reuse. A code-key projection needs a checked
non-observability/substitution argument before excluding any such premise.

### Established cause: replaying alias facts as effects

The bounded interface probe supplied a stronger explanation than a hypothetical
key projection. Its234 observed key rows contain20 groups with identical
ordinary fact hashes **and** formal-only alias-relation hashes but changing
external-family cardinality. For template25, the ordinary prefix stays4291
characters with15 anchored Scope identities, while the external families grow
10 →60 →120 →240 →… →2,488,320. The complete key grows4361 →18,799,866 characters.
Raw evidence and reproducible aggregation are in
`.temp/compiler-alias-interface-probe/{keys.jsonl,summary.json,summarize.py}`.
This is diagnostic-only data, not a benchmark: hashing/instrumentation adds work.

The cause is `InvocationProjection.ApplyEffects` replaying **all poststate alias
facts**, including unchanged entry relations. An external input-family endpoint
has no ordinary Scope binding, so `ScopeTargets` fell through to generative
`MapRegion`; every invocation cloned the caller's own input metadata into fresh
family identities. Even a read-only function could grow the caller alias graph.

The correction keeps owned alias facts for checking/key/graph consumers, but
publishes a separate `AliasEffects` relation containing only edges introduced
after the immutable entry relation. Application replays that delta, not its
premises. New body-created/returned-view edges still project; borrowed input
relations remain the caller's relations. No key premise or external-topology
property was removed. The `AliasEffectChecks` property compares zero, one, two
and eight read-only calls and requires identical caller alias-family counts.

On one coherent frozen image, the unchanged associated two-insert source changed
from a correctly bounded300.843-second timeout (maximumRSS10,315,856KiB) to
7.892 seconds /1,091,575,816 allocated bytes /376 functions. The older main
control was7.523 seconds /1,078,513,120 bytes /366 functions, but lacked the alias
correctness premise; this single comparison does not establish an acceptable
performance margin or the30% deletion requirement. Receipts are
`.temp/compiler-frame-control/{symbolic-view-timeout.json,alias-effect-measure.json}`.
The original745-mutation program and full coherent user journeys remain required
before any recurrence-completion claim.

### Entry-reference recipes: a value is not an exit field lookup (2026-11-13)

The next source-SCC footprint obstruction is narrower than a whole new summary
engine. `_Chain.node` returns an entry reference through a checked Scope view;
the caller later writes the old tail's `next`. Current origin transport can lose
that entry value or recompute its path after effects have rebound the owning
field. Neither a type-only SourceReturnShape nor the returned runtime Scope kind
identifies the required write receiver.

The isolated entry-reference candidate replaces the existing root-only origin
bindings with whole producer-place bindings captured during the same input Bind
traversal. A recipe `EntryValue(root, fields)` denotes the value already read at
invocation entry. If it is a Scope address, that address is frozen but the fields
of the addressed allocation remain live. A checked view refines the logical
schema; it does not allocate a new object or change the reference's producer.
Formal-to-actual substitution composes the caller's recipe rather than retaining
a live caller environment in the cached summary.

`SemanticOrigin.Fields` is an ImmutableArray: its default record equality does
not compare independently constructed paths structurally. The candidate defines
path equality/hash with ordinal field names and treats default/empty paths as
the same empty projection. A plain Dictionary keyed by the old record would
silently miss reconstructed equivalent paths.

Independent review fixtures in `.temp/entry-reference-review/` compare frozen
main Compiler `8cc7a53b...` with candidate `8646aacf...`, using identical frozen
Core/HIR/Optimization/Runtime dependencies. The actual private canonical
InvocationProjection is exercised, not another expression evaluator. Candidate
properties preserve an opaque recipe without generating a fresh region, retain
a known borrowed base/cell, and see current cell contents after a change between
Bind and Project. Structural-path and default/empty equality also pass. The old
projection loses those identities.

Two real source/native-binary journeys establish why the entry distinction is
observable, at both O0 and O2:

```text
prior(box) = { saved = box.flag; box.flag = false; return saved; }
box.flag = true; if prior(box) { 11 } else { 22 }       // must return 11

priorType(box) = { saved = box.Item; box.Item = String; return saved; }
box.Item = Number; Old = priorType(box); answer: Old = 7 // must remain legal
```

The frozen old Compiler produces a runtime narrowing failure in the Bool case
and incorrectly rejects the Type case as expecting String. The candidate returns
11 and 7 respectively. Separate controls preserve a Scope address through source
rebinding while keeping its heap live, distinct fresh wrappers over one borrowed
value, and a borrowed field inside a fresh wrapper. Raw results, assembly hashes,
fixtures and initial harness setup failures are kept separately in `results.json`
and the build/transport logs. These observations are not performance acceptance.

**The candidate does not yet close typed W.** A source helper that checks an
opaque `owner.last`, then rebinds `owner.last` and writes the saved reference's
`next`, still leaves stale Null facts in both the old and reviewed candidate
Compiler. A second control writes String through the saved reference's Item;
both versions incorrectly retain the original Number annotation witness. An
unrelated same-shaped object's Item remains a valid witness and must stay so.

The missing boundary is visible in the existing semantic rules:

1. `MemberAssign` receives a checked view with negative structural Region and a
   meaningful value Origin.
2. `DynamicElement` creates a dynamic view region whose Aliases are derived from
   the structural Scope. With no concrete aliases, that array is empty; it does
   not prove the reference is fresh or disjoint.
3. `RecordWrite` retains Region/Field but not the receiver's entry-reference
   recipe. `InvocationProjection.ProjectWrite` cannot bind this new region and
   drops a dynamic-region write with no aliases.

Thus recording the write as `Field(EntryValue(owner,last), next)` is essential;
`Field(ReadExit(owner.last), next)` targets the replacement, and a synthetic
region with no aliases loses the write entirely. The appropriate repair belongs
in the canonical field-place/write-target representation, using the existing
entry substitution. A known actual entry value resolves to its old base; a
still-opaque entry value retains a symbolic/TOP may-write, not an empty effect or
a new allocation. Do not append entry origins indiscriminately to MemberRead:
a read after a body write is a current/exit read unless an entry producer proves
otherwise. Fresh constructor *outer* identity remains generative even when its
field values have borrowed recipes.

A constructive property must record MemberAssign on the opaque borrowed view
*inside the generic body*, collect its canonical write event/footprint, then
substitute the actual entry owner and apply effects. First projecting the receiver
to a known base and only then assigning a field bypasses this missing boundary.
Return-recipe properties alone therefore cannot certify the SCC write footprint.
The complete typed write and static-observation certificate, original 745 trace
and unchanged deadline remain required; no recursive admission was enabled by
this review.

## Existing collection workload attribution (2026-11-16)

On `artifacts/native/native-set-stdin-repl/msync`, the standard seed-17
collection sources were compiled only to HIR. Five phases-only processes per
fixture gave analyze medians of 1,193 ms (B+), 792 ms (Set), 230 ms (Heap)
and 2.5 ms (numeric record stream control). Median allocation on the compiler
thread was 298, 200, 65 and 0.54 MB respectively. HIR from the phases and
profile runs passed the existing independent stdout oracles.

One separate, lossless profile per workload gives the following exclusive
span accounting (direct child durations subtracted; not telemetry-off time):

| Workload | Analyze ms | Instantiate ms excluding children | Flow solve ms excluding children | Published instance misses / calls |
| --- | ---: | ---: | ---: | ---: |
| B+ | 1,925 | 963 | 413 | 156 / 413 |
| Set | 1,045 | 758 | 34 | 137 / 178 |
| Heap | 322 | 103 | 71 | 39 / 55 |

Set's `_set_ref`, `_set_child` and `_set_link` alone account for 37, 18
and 20 published instances. Each family uses one builtin carrier signature,
despite different semantic graph keys. This is evidence to inspect
specialization boundaries, not proof that all emitted bodies are interchangeable.
The instance span includes key construction and uninstrumented miss work:
formalization, body checking, type resolution and summary construction. It is
not a measurement of hash-map lookup. In B+, 315 of 354 flow preparations
occur inside another flow solve: return-shape projection can run a callee
flow analysis while supplying a caller transfer. One outer solve performs
28 transfers over 270 ms elapsed, 55 ms after child spans. The solver spans
therefore include interprocedural projection as well as local CFG work.
Uninstrumented debugger stacks also show key construction, graph reconstruction
and GC. Treat those stack samples as qualitative corroboration.

First separate instance key, input formalization, summary resolution/freezing
and invocation projection in the instrumented path. Then test stable schema
IDs and symbolic place/effect summaries on Set; instrument return-shape cache
work separately for B+ before changing the worklist. Rust's
[query evaluation model](https://rustc-dev-guide.rust-lang.org/queries/query-evaluation-model-in-detail.html)
is an engineering comparison for inexpensive cache reuse;
[demand interprocedural dataflow](https://research.cs.wisc.edu/wpis/papers/fse95a.pdf)
is a research comparison, not an assumption that this language fits its domain.
Receipts: `.temp/semantic-cost/`, including the lossless profile databases,
`inventory.log`, phase repeats and stack samples.

After the layout/path specialization change, three Native AOT phase-only
compiles on CPU 12 gave analyze medians of 630 ms for standard Set and 802 ms
for B+ inventory (seed 17). Three separate instrumented runs gave exclusive
Set medians of 199 ms resolving summary types, 126 ms replaying effects,
67 ms constructing formal inputs, 24 ms attaching origins, 36 ms freezing
summaries, and 1.34 ms building keys. Formal construction visited roughly
13,500 fields per compile. B+ spent 123 ms resolving summary types, 102 ms
freezing, and 73 ms constructing formal inputs. Both profiled HIR outputs
passed their existing stdout oracles.

The expensive reconstruction is now downstream of key selection.
`ResolveSummaryTypes` scans all context values and object fields; aggregate
projection calls `FlowType`, which starts a new scope map and serializes a
reachable shape before interning it again. Carry closed type IDs through
publication and resolve only exported dependent types. Build and bind
symbolic effects from accessed paths rather than freezing a complete formal
object snapshot. Receipts: `.temp/semantic-attribution/`.

The following implementation removed whole-context export resolution.
Exported values retain closed type IDs; parameterized types are resolved
from their HIR definitions when exported. Formal Scope rows borrow a source
during analysis and materialize only requested fields. Queries borrow a
semantic overlay instead of importing the reachable caller graph. Effects
on input objects use formal paths; input objects themselves do not go into
the frozen result graph. Fresh exported allocations still carry their own
descriptions.

Paired Native AOT measurements (CPU 12, standard seed 17, three fresh
processes per image) showed phase-only analyze medians of 630 to 83 ms for
Set and 749 to 304 ms for B+ inventory. Managed allocations fell from 168
to 22 MB and from 207 to 95 MB. In separate detailed runs, the formal-field
visits fell from 13,516 to 183 and from 10,869 to 857; the old full summary
type reconstruction was replaced by under 0.1 ms of result-type export in
both workloads. The exact compiler stage receipts and workload output
oracles are under `.temp/demand-formal/`.

### Alias-independent instance admission

Instance selection no longer classifies templates for alias observation,
encodes caller alias edges, or imports those edges into the formal body.
The caller binds and applies the ordered effect plan to its actual objects;
runtime object identity and field storage are unchanged. A Type member load
used by a static annotation records the required denotation at the load's
position, not after all function writes. The caller's effect replay checks that
requirement against the actual object. A failed requirement is a compile-time
diagnostic, not a different specialization or a VM branch. The schema walker
does not serialize graph-local backreferences. An executable or return-scheme
key records interned types, physical layouts, and static choices used by the
bound program, including denoted Types and callable contracts reached through
source field paths. Recursive type edges already refer to interned IDs.
Formal writes and Type reads retain the path of the expression that accessed
the object. Instantiation binds each relevant path to the actual input before
applying the ordered events. In particular, a recursive symbolic row can stand
for both `p` and `p::next::next` without conflating their effects in a caller
where those paths reach different objects.

Anonymous construction remains one interned storage layout per declared field
row. The LLVM member lowering uses the actual allocation layout before
consulting the candidate catalogue and keeps the checked field ordinal in
the direct path. Binding the native executable resolves the field's constant
offset; execution does not search layouts for fixed-offset access. This is the
typed address computation described by [LLVM GEP](https://llvm.org/docs/GetElementPtr.html).

In the earlier intermediate implementation, the standard Set workload
(seed 17) Native AOT comparison
reduced phases-only analyze P50 from 718 to 570 ms and allocation from 198 to
153 MB (five processes per image). The detailed run had 130 alias-independent
and 51 alias-sensitive selections. All 21 `_set_link` selections were
independent; none of its 20 published keys encoded an alias graph (19 did
before). `_set_ref` fell from 37 to 20 instances. Five telemetry-off
source-route pairs all improved, with median paired elapsed -3.8%; bound
execution was mixed at +0.8% paired. All timed invocations passed the existing
output oracle. Receipts: `.temp/scope-layout-symbolic/` and the baseline
phase profiles in `.temp/scope-layout-keys/`.

### Finite Type witnesses and correlated scope alternatives

Carry a finite set of denoted type IDs through control-flow joins; the carrier
of each value is still `Type`. A correlated Scope alternative keeps its whole
row rather than joining its witness and payload independently. This follows
the correlation principle in [TypeScript's narrowing of discriminated unions](https://www.typescriptlang.org/docs/handbook/2/narrowing.html#discriminated-unions),
but here selecting a row also selects concrete native storage.

A dependent call in an existing `if` or `match` arm uses the corresponding
direct specialization. If a result has already been stored in a local, HIR
carries the original arm ordinal separately from the object. Matching the
associated Type member can narrow that row before the call; otherwise only
the dependent call consumes the ordinal. One finite scalar Type argument can
instead dispatch on its existing encoded value. Neither representation
queries the runtime object for its type. A common field with equal native
offset and carrier across layouts binds to one fixed offset; the per-row
payload is likewise addressed at its known offset.

Equal allocation byte counts do not imply equal native layouts: String has a
reference lane where Number has a byte payload. LLVM heap allocation
equivalence must retain native layout and allocation-site identity, so a
control-flow hoist cannot replace one allocation with the other.
