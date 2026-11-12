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
first, last : Scope | Null
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
