# Candidate: interprocedural value and range provenance

2026-11-13. **Implemented candidate in the worktree; integrated acceptance is
not complete.** The 580-input performance snapshot remains untouched. This
extends the existing Core value/address graph, not another VM analysis, artifact
annotation, runtime cache or language type.

The [independent soundness review](native-value-provenance-review-v090.md)
records actual-VM correlation/index controls, higher-order finite-identity and
CopyMemory counterexamples, and the required final combined rerun.

## Measured boundary and actual workload

The ValueID/reaching-definition batch separates colored storage slots from values.
Its controls cover joins, backedges and binding identity. The remaining record
probe (`.temp/native-backend-proof/proof-at.log`) has five unknown ProjectAddress
inputs in main (PCs 215, 230, 238, 252, 260), while `make_row` no longer inherits an
old capture pointer's unknown origin at its later field-address definitions.

The real producer is:

```
iota -> native ranges.transform(source, make_row)
     -> native ranges.materialize -> OpenIterator -> NextIterator -> row members
```

See `tests/Performance/Corpus/compute.py::_NUMERIC_RECORDS`,
`src/Msync.Runtime/NativeModel/NativeInterpreter.RangePrimitives.cs` and
`stdlib/ranges.msync`. Supporting only direct Call and MakeRange would miss this
workload. `make_row` actually allocates layout 4 in the inspected artifact; its
return-slot annotation is not the evidence.

## Current candidate and remaining acceptance

The actual frozen record artifact now proves all five main projections as
non-frame origins with physical layout 4, while keeping exact target identity
incomplete and importing no callee PC as a caller-local allocation. The receipt
is `.temp/core-hir-code-reduction/final-record-proof.log`. This is a static proof
receipt, not an end-to-end speedup claim.

The closed candidate passes 3,183 optimization observations, 2,400 artifact
contracts, 28 source/standalone-HIR O0/O2 routes and 666 middle-end contracts.
They include the two-call child-layout correlation, two indices sharing the same
function code object, named recursion, binding lifetimes and concurrent linking.
Independent real-VM controls additionally pass exact child-B layout correlation,
ordered calls to two indices sharing one code record, CopyMemory's replacement
referent, higher-order `rec(f(f), c)` closure and recursive `walk(x.next, c)`
transport. Receipt: `.temp/native-provenance-review/cyclic/acceptance.json`.
Four fresh processes completed those independent groups in 0.39–0.47 seconds.
These bounded controls establish the tested contracts, not a general analysis
soundness/termination theorem or end-to-end profitability.

Frozen Core source identities for that cohort:

* `NativeAddressAnalysis.cs`: `859dbbab66dd4ad1d63dbb15eaed620905c320ca1c65951c7d6c960e390b8385`
* `NativeAddressAnalysis.Calls.cs`: `703bcd291e3636a86166c97cf52fa853db27354fc78f3674f267fced92f9cf36`

Application nodes use the original IR producer/use, finite caller call-site and
virtual callback role. Their actual operands are joined into owned nodes; an
import evaluator object is not a new symbolic producer. This replaces the
observed quadratic application growth in the original higher-order counterexample.
Reads/projections retain actual binding identity. CopyMemory and ClearMemory
invalidate their actual destination's memory facts; a previous referent layout
cannot survive a bulk overwrite or clear.

Unresolved alias/layout uses now occupy **one tagged symbolic bank**. The typed
Formal/Application/Adaptation/Projection/Read/Element/Range collections are
non-owning views, not parallel sets. Concrete local/foreign regions, callable and
container retention remain distinct facts. Empty concrete sets are allocated
lazily; ordinary scalar definitions do not manufacture reference-provenance nodes.
`NativeKnownCalls`' 207-line duplicate scanners are removed; inlining consumes
`CallablesAt` from this authority. Replacement code is counted and the required
30% net deletion has not been achieved. The affected-path accounting at this
checkpoint is **614 physical lines net added**, including replacement code and
shared consumer changes; the repository-wide frozen `cloc` budget remains the
acceptance authority. Scanner deletion is not a completed budget reduction.

Operand interning preserves actual binding identity. Only a strict pure formal,
without attached alias/container/layout/unknown facts, can normalize to its own
entry ValueID; both the key and stored operand use that same value. Function
indices come from the program owner, not record/code-object equality. Recursive
imports enqueue fixed-point dependencies instead of recursively replaying calls.
The owner serializes demand/link queries and publishes completed function views;
new callers instantiate into their own nodes and never feed actuals back into a
previously published callee summary.

### Analysis setup observation and open work

A Core-only cold managed probe, excluding artifact read and guest execution,
allocated 2,757,176 bytes / 135.231 ms for a fresh record owner and 7,319,608 bytes /
194.128 ms for polygon. The measurements include first-use .NET JIT compilation;
they are neither Native AOT setup cost nor workload timings. The complete probe
and setup outputs are under `.temp/core-hir-code-reduction/summary-probe/` and
`setup-{record,polygon}.log`. Do not quote them as an optimization gain.

Further reduction should reuse the original ValueID/IR definition graph for
symbolic edges and consolidate evaluator bookkeeping, not add kind-specific fact
banks again. Flow-insensitive writes still lose precision across an overwrite
following an unknown effect. Some native producer schemas remain genuinely
unproved and keep actual variant selection. Public parameter/capture domains are
not closed-world allocation proofs. The combined Runtime and Native AOT workload
measurements remain outside this Core correctness checkpoint.

### Shared-DAG import correction after the 602 checkpoint

The 602 integration exposed a real end-to-end failure outside the earlier record
and polygon controls: compiling the Set-dedup smoke source consumed the runner's
natural 300-second deadline with almost flat 58 MB RSS. Its captured native stack
repeated `Invocation.Import` through shared Read/Projection/Retained edges. The
image and timeout remain negative evidence; earlier passing controls did not
establish functional acceptance of that checkpoint.

Import had removed `(source, destination)` from its path-active set on return.
That prevents recursive-stack cycles but visits a shared diamond once per path.
The corrected traversal retains each exact pair until that root import frontier
finishes. It is not a cross-pass cache, canonical-source collapse, depth cap or
conversion to Top. Later facts are processed by the existing monotone owner
iteration. Cached Input joins now report growth through their function Step;
Store effect/escape joins and newly admitted foreign-field facts also contribute
to `changed`, so a skipped late pair cannot silently end the fixed point.

The focused managed journey uses exactly
`.temp/native-program-provenance-functional-smoke/applications/set-dedup/smoke/17/`
source, stdin and independent oracle. After the correction: Analyze 3,005.842 ms,
O0 lowering 352.625 ms, O2 compilation 7,636.451 ms. Produced and strict-detached
O0/O2 executions all match the exact oracle (0.53–2.51 seconds per execution).
These are a bounded managed reproduction, not a matched AOT speedup claim. Logs:
`.temp/core-hir-code-reduction/set-closed{,-build}.log`.

Local closure controls pass 3,191 optimization observations, including reversed
function registration for formal/capture/element joins and an eleven-level
shared-diamond Range graph. The joined middle-end controls remain 666, source/HIR
routes 28 and codec contracts 2,400. The same five independent VM counterexamples pass again on the visited/changed
cohort: `.temp/native-provenance-review/dag-final/acceptance.json`. Core source
hashes `0196e8107d293d4a6265c9385f147966d6fcaa1cdb63f9e76d9b35d8bf9d5f4e`
and `8232626c05adc3e3c17a5e8db22e97e7ccf7e13777c81f7ccaf0fe0c27fbced2`
match the independently frozen payload before and after execution. No repeated
AOT publication or guest-runtime fallback was used to obtain this correction.

## Independent facts, not a larger meaning for Unknown

| Fact | Meaning | Does not imply |
| --- | --- | --- |
| Local location targets | Known locations in this activation's graph | Every runtime object is uniquely identified by its allocation site |
| Target completeness (`Unknown` today) | Whether the local target set includes every possible target | Unknown target must alias a current frame cell |
| May alias current frame cells | Direct address may reach this invocation's register/location storage | A retained child reference aliases the address of its container |
| Actual layout alternatives | Physical layouts established by producers or checked admission | Freshness, noalias, readiness or absence of frame aliases |

A returned callee allocation can have unknown exact identity but known physical
layout and no alias to caller-private frame cells. A checked Narrow can establish
a layout without establishing no-frame-alias. A Scope may retain a closure that
captures a frame cell without its own region overlapping that frame cell.

"Current frame" means logical register/location cells, not equality of managed
allocation owners. Coallocated Scope regions remain distinct logical regions;
region bounds must not be erased. Keep actual null/readiness/cancellation and
union-selection obligations separate from these facts.

## One graph and its ownership

Make the existing `NativeAddressAnalysis` program-scoped, with function views.
There is one ValueID/Points/memory authority; each view uses the existing CFG
reaching edges. A program owner manages function dependencies and summary work,
not a second set of parallel per-function points tables.

Conceptual internal API (names may follow the implementation):

```csharp
NativeAddressAnalysis(NativeProgram immutableProgram);
FunctionFacts Function(int function);

// Existing use-oriented consumers remain use-oriented.
Points FunctionFacts.At(int pc, int slot);
IReadOnlySet<int>? FunctionFacts.ProvenLayoutsAt(int pc, int slot);
IReadOnlyList<CallableOrigin> FunctionFacts.CallablesAt(int pc, int slot);

// On Points, separate target incompleteness from this specific alias query.
bool Points.MayAliasCurrentFrame;
```

Range-element references and symbolic return expressions are internal edges of
that owner. Runtime needs no Elements cache: a successful NextIterator result
already has the ordinary At/ProvenLayoutsAt facts. Link immutable operands once.

The Runtime owner is an execution-image generation. An optimization owner belongs
to an explicit immutable analysis phase/snapshot; never cache summaries globally
by function index while an optimizer mutates instruction arrays. Compilation,
artifact input and new session images retain their existing boundaries.

## Finite return summaries and call-site substitution

Summarize the actual values reaching Return, not Signature.Result.Layouts:

| Atom | Derived from | Caller instantiation |
| --- | --- | --- |
| Actual allocation producer `(function, pc)` | Allocate | Foreign-activation region with that concrete layout |
| Formal value `arg(i)` | Entry ValueID propagated unchanged | Actual argument facts at this call |
| Formal captured value `capture(i)` | A value-capture snapshot | Saved capture ValueID from actual MakeFunction |
| Formal captured location | Location-capture address | Saved location identity; reading mutable contents is a separate memory operation |
| Checked-layout value | A real Narrow | Keep source aliases; refine physical alternatives only |
| Unknown | Unresolved producer/load/call | Remains unknown on joins; never becomes empty/bottom |

Copies, moves, Return joins and known call targets compose these expressions.
Keep container/retention edges alongside direct-address facts. Reading a mutable
capture is not the same as returning its immutable construction-time value.
Mutation before Return must affect its memory facts.

Do not import a callee's allocation PC into caller `Target.Allocation`. Qualify
producer identity and activation relationship. Even recursive calls to the same
function create a different activation. A foreign allocation-site summary is a
may-origin, not a must-alias object or a license for strong memory updates across
different calls. Preserve interior offsets and physical pointees: returning an
allocation's field pointer does not turn it into that allocation's region-base
address or justify the base's layout proof. It may still prove no caller-frame
alias. A callee's own escaping FrameAddress is foreign frame storage; a captured
pointer to the caller's frame is a substituted caller alias.

This is caller-side substitution, not closed-world parameter narrowing. For
`id(x) { return x; }`, calls with two different inputs get two corresponding
results; public Invoke may still supply any value admitted by its established ABI.
Do not union all callers into one nominal parameter domain or add hot-path guards.
A public externally supplied callable with unresolved origin remains unknown.

Use finite, interned expression/ValueID nodes and SCC dependencies. Distinguish
uncomputed bottom from genuine unknown. Recursive dependencies converge over
finite producer/formal/capture alternatives; do not unroll call strings or grow
unbounded access-path expressions. Do not give consumers provisional facts before
the demanded dependency component converges. Unknown native callbacks remain a
real boundary, not an excuse to infer their result from a logical return type.

## Callable and element edges

A callable origin records its actual target and capture edges at MakeFunction.
AdaptFunction/session dispatch applies its actual target mapping, preserving the
appropriate environment retargeting. A location capture is an address snapshot,
not the current value of the source register after coloring/rebinding. Unknown
or mutable callable origins must not be resolved merely from a source name.

Extend the canonical builtin contract, currently `NativeSignature.ResultRule` /
`ElementCallback` / `NativeBehavior`, rather than adding a parallel name switch.
Those fields presently describe logical specialization/behavior, not automatically
alias guarantees: audit each implementation before making a provenance transfer
part of that same contract.

| Producer/operation | Element-origin transfer |
| --- | --- |
| MakeRange | Actual input values; a Function element is not implicitly invoked |
| MakeLazyRange | Mask-0 inputs are values; mask-1 inputs use actual zero-argument callback return summaries; accepted null-mask behavior invokes all items |
| DeferRange | Invoke the thunk summary, then use the returned Range's elements; do not make the thunk itself an item |
| `ranges.transform` | Substitute actual source-element facts into the actual callback summary |
| materialize/filter/take/drop/reverse/sort | Preserve selected element identities, not container identity; materialize is not a deep copy |
| concat/merge | Union relevant source element origins |
| zip/enumerate | Concrete native factory ABI for the new outer record; retain actual input element edges separately |
| chunk/slide/flatten | Preserve explicit nested-element edges and the correct nesting level |
| RangeFromMemory | Actual selected field memory; a Scope element's carrier annotation does not freeze its referents |
| scan | Seed plus callback-result recurrence in the same finite graph |

A fresh callback result can be known even if its input element is unknown. A
callback returning its argument instead substitutes that unknown. Deferred work
is never executed by analysis; effects still happen once at the existing demand
point, with captured directory, cancellation and pending identity unchanged.

An iterator ID is storage too. Extend the existing reaching-definition topology
with OpenIterator/CloseIterator definitions, not a whole-function union of every
producer that ever occupied that ID. NextIterator's successful edge defines its
item; the failure edge retains the old destination. The current conservative
union on both edges must become edge-specific before claiming precise item facts:
otherwise coloring can reintroduce the old Unknown through an impossible edge.
Reopening a handle on a backedge must join the real producer alternatives.

## Constructive controls required before implementation acceptance

1. `id(framePointer)` and `id(freshScope)` at different call sites retain different
   aliases; the first cannot be labeled no-frame merely because the callee is known.
2. Two calls to one allocation site produce two objects. Neither equal producer
   IDs nor equal layouts permits forwarding a store between them.
3. Recursive fresh returns are foreign to the caller activation even when the
   function IDs and slot numbers match. Captured caller pointers remain aliases.
4. A fresh returned Scope contains a captured frame pointer: its outer field
   address is non-frame, but loading/dereferencing that field restores the alias.
5. A value-captured Scope survives source-slot rebinding unchanged. A location-
   captured Scope observes a later compatible write; do not freeze the old referent.
6. Literal Function items versus deferred Function items produce different element
   facts. A deferred thunk returning Range must not add a nesting level.
7. Snapshot/materialize preserves object aliases. Mutating an original row is
   visible through the materialized Range, without changing its allocation layout.
8. An externally supplied Range annotated `Range<Scope layouts=[L]>` is not proof
   that every emitted address actually came from allocation L.
9. Unknown-plus-known branches stay unknown. Iterator failure, reopening and
   backedges cannot inherit a success-only result or a previous handle's provenance.
10. Reproduce all five record projections with actual producer evidence and their
    no-frame-alias facts; compare the same artifact in Off/Force. Do not count the
    five-gate disappearance as an end-to-end speedup without the measured journey.

## Deletion and integration boundary

`Optimization.NativeKnownCalls` currently has an immutable-cell scanner plus a
separate block-local callable/address/binding/memory scanner. Replace those with
`CallablesAt` from the same Core graph, then delete the old scanners; do not keep
both as a compatibility fallback. The inliner consumes the shared target/capture
facts. Iterator and return propagation extend existing ValueID edges, not a new
range verifier. Retire redundant producer walks when their admitted-boundary
obligations have genuinely moved to this authority.

The first implementation should trace the actual record chain, but the transfer
rules must be semantic (producer/callback/selection), not special cases for
`make_row`, layout 4 or the five measured PCs. Existing untrusted formats keep
entering the same pipeline. No new wire hints, Assume opcode, nominal Scope class,
VM object state or source-language restriction is required.

## External design checks

LLVM distinguishes allocator-like `noalias` returns from `returned` parameter
propagation and from capture restrictions. That separation supports keeping
fresh origin, substituted alias and retention distinct here; these are derived
facts, not unchecked LLVM-style promises imported into msync's untrusted IR.
[LLVM Language Reference](https://llvm.org/docs/LangRef.html#parameter-attributes).

MLIR's dataflow guidance distinguishes uninitialized facts from overdefined
unknown and requires monotone joins. Its call-transfer interfaces separately
handle external calls. Reuse that discipline, not its infrastructure or a second
analysis engine. [MLIR dataflow tutorial](https://mlir.llvm.org/docs/Tutorials/DataFlowAnalysis/),
[call-transfer interface](https://mlir.llvm.org/doxygen/classmlir_1_1dataflow_1_1AbstractSparseForwardDataFlowAnalysis.html).

Reps, Horwitz and Sagiv's interprocedural graph-reachability work motivates finite
summaries and matching a return to its own call-site inputs rather than mixing
all callers. Its precise IFDS guarantees assume finite distributive transfer
functions; this design does not claim those guarantees for every heap/effect rule
or propose a general IFDS engine.
[Primary paper](https://research.cs.wisc.edu/wpis/papers/diku-tr94-14.pdf).
