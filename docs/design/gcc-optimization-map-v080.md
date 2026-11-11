# GCC 16.2 optimization mapping for msync 0.8

## Baseline and interpretation

The requested reference is the latest stable major line, GCC 16.2, released on
August 7, 2026, not GCC 17 development. See the [release table][releases], the
[versioned option manual][options], and the actual [16.2 pass pipeline][pipeline].
The internals [Tree SSA][trees] and [RTL][rtl] explanations describe mechanisms;
the tagged pipeline, rather than a moving manual, establishes pass ordering.

This is a source audit of the integrated `e7be48a` implementation, updated
through `97f149c` after finite evaluation, IPA and constant-pool integration on
November 9, 2026. Earlier `f57e4d1` / `c1efa32` findings are superseded below. The production
callsites and direct rewrite mechanisms were inspected independently. Test
counts are the implementation owners' current local acceptance records, not a
new cross-platform performance claim. The separate [mutable-state contract][state]
owns semantics; [optimizer-graph-v080.md](optimizer-graph-v080.md) owns detailed
rewrite/scaling evidence. Historical Rust results do not certify these C# passes.

GCC is a reference for algorithms, not a backend dependency or code transplant.
Its assumptions about C undefined behavior, aliasing and integer overflow are
not msync semantics. Scope allocation identity, captured cells, selected errors,
deferred enumeration, and possible divergence are observable here.

## Stage ownership

| Stage | Available knowledge | Optimization responsibility |
| --- | --- | --- |
| Typed source lowering | Binding identities, fixed storage types, generic callable identities, lexical shapes | Monomorphization, conversion insertion, lowering sugar; reject invalid programs before DCE |
| Relocatable `.mso` | Typed bytecode, CFG, def-use, effects; no selected machine context | All applicable context-independent transformations below |
| Executable `.mexe` | Frozen cwd/HOME/source context and endpoint grammar | Relocation, contextual folding and executable layout; no replay of the portable optimizer |
| Selected runtime work | Actual paths, selected endpoints, pending processes, dependencies | Resource DAG, directional barriers, independent I/O overlap; never freeze filesystem observations as reusable facts |
| Native AOT backend | Host ISA, ABI, registers, CPU costs | Native instruction selection, register allocation and machine instruction scheduling for the C# implementation |

An executable artifact is still bytecode, not a CPU executable. Native AOT does
not automatically optimize the user's bytecode loop or inline its functions:
those remain msync optimizer responsibilities. Conversely a bytecode rewrite
does not provide a new hardware register allocator.

## Machine-independent coverage and remaining work

Options name the corresponding GCC family; mechanisms and applicability in the
last columns are msync design conclusions from the inspected source.

| Family / GCC entry | msync mechanism and semantic conditions | Actual source and status / owner |
| --- | --- | --- |
| CFG cleanup; `pass_fixup_cfg` | Remove unreachable blocks, remap branches, collapse empty control paths after each rewrite | `GraphOptimization.Discover/Emit` and `Optimizer.FormBlocks`: integrated CFG reachability, branch folding, target relocation and repeated cleanup. Slot/table pruning follows rewrites |
| Constant propagation; `-ftree-ccp` | Meet reaching definitions, evaluate scalar constants, fold branches; genuine SCCP also tracks executable edges so impossible predecessors do not pollute joins | `PredicateOptimization.Discover` is integrated before graph optimization: selected-edge constant propagation, executable-predecessor joins and edge assumptions. `GraphOptimization.Resolve` / `PureEvaluation.Native` then propagate reaching constants and fold approved native operations |
| Copy/forward propagation; `-ftree-copy-prop`, `-ftree-forwprop` | Follow definition identity, not source names; bypass dead stores and copies while retaining captured locations | Integrated definition-identity propagation, scalar field forwarding and value-number reuse eliminate supported copies without turning captured storage into copied values. No general SSA renaming pass is claimed |
| DCE; `-ftree-dce`, conditional DCE | Backward demand from return, observable effects/errors/divergence and live REPL exports | `GraphOptimization.IsRoot/Keep` consumes actual target effect summaries. Known terminating effect-free calls and dead nonescaping work can disappear; unknown calls, divergence, selected failures, externally owned writes and live REPL exports remain roots |
| DSE; `-ftree-dse` | Remove overwritten local values and nonescaping object construction/stores only when no read, alias, capture or escape observes them | Integrated backward-demand local/cell/object-store deletion plus `ScopeScalarization` per-field scalar slots. Actual alias identities and escaped stores remain observable. No arbitrary cross-block memory-SSA DSE claim |
| FRE/GVN; `-ftree-fre` | Value-number pure expressions with operand definition versions; reuse a dominating computation without changing evaluation order or error behavior | `GlobalRedundancy` is integrated at O2/O3 for branched functions: operand-definition value identities, CFG availability and existing result locations implement scoped global FRE. Cyclic SCCs are excluded as motion regions. `ValueNumbering` handles basic-block-local CSE at O1+ |
| PRE / code hoisting; `-ftree-pre`, `-fcode-hoisting` | Reuse expressions already available on some predecessors by edge insertion; never introduce errors/effects on previously unselected paths | `GlobalRedundancy` inserts materializable total expressions on missing predecessor edges, explicitly splitting taken critical edges. It preserves existing operand stacks and applies a result-slot cost model: cheap arithmetic usually reuses existing locations; repeated or string expressions can justify new slots. Not arbitrary speculative native/path/field hoisting |
| Predicate simplification, jump threading; `pass_thread_jumps`, `-ftree-vrp` | Track `is null`, Bool/type tests, equality and finite alternatives per edge; invalidate facts on writes and modifying calls | Integrated Bool/Null/type/equality edge refinement and constant branch elimination with alias-write invalidation. Numeric intervals, range-based relational implication and general path-duplicating jump threading are not implemented |
| IPA effects; `-fipa-pure-const`, `-fipa-modref`, `-fipa-reference` | Fixed-point call summaries must distinguish reads/writes/captures, errors, divergence, enumeration and invocation ownership | `EffectAnalysis.FunctionEffects` fixed-point flags are consumed by graph DCE; recursive SCCs and backward edges preserve divergence, and alias/capture mutation is observable. These are useful coarse summaries, not GCC-equivalent field/location-sensitive mod/ref sets |
| Direct-call discovery / IPA CP / clones; `-fipa-cp`, `-fipa-cp-clone` | Callable identity plus argument types specializes generics; constant arguments need value specialization or inlining, not type specialization alone | Generic type specialization, actual callable discovery and graph native-call lowering are integrated. O2/O3 `InterproceduralSpecialization` clones known direct callees for constant control arguments and removes unused cloned inputs; it preserves argument evaluation, captures, escaping callback signatures and invocation ownership. Actual product oracle: 120 contracts |
| Early/single-use/indirect inlining | Splice fresh invocation slots, preserve capture-cell references, relocate branch targets; do not erase scheduler ownership boundaries | `FunctionInlining` is integrated as one wave between outer graph fixed-point rounds. It stages arguments/captures in fresh invocation slots, excludes recursive SCCs and protects scheduler ownership boundaries. Graph lowering identifies native callees from actual values, not an untrusted Call.B hint |
| Escape / SRA / IPA SRA; `-ftree-sra`, `-fipa-sra` | Replace a nonescaping Scope's fields with scalar definitions, including intervening field stores; preserve instance identity when observable | `ScopeScalarization` is integrated: per-field scalar slots and versions, raw-cell identity, read/write forwarding, alias/unknown-call invalidation and local live-out escape checks. Region is one basic block. Escaped Scope reads may forward while actual writes remain. Arbitrary CFG field phi nodes and IPA aggregate parameter splitting are not implemented |
| Reachability / section GC / constant merging | Prune unreachable functions, constants and dispatch references; merge only immutable payloads | `LocalSlotCompaction` removes unused non-prefix frame slots and remaps every slot opcode; captured/parameter prefix and live REPL storage remain stable. `ModuleOptimization.Compact` prunes and relocates function/constant/dispatch reachability. `ValueRepresentation` separates bit-exact Number literal identity from source equality: signed zero/NaN representation must not be collapsed |
| Loop invariant motion; `-ftree-loop-im` | Hoist pure total computations whose operands and memory versions are invariant; preserve zero-iteration and lazy callback semantics | `LoopOptimization.Hoist` is integrated for canonical iterator loops with sole preheader entry. It hoists proven total expression trees over stable typed nonshared locals; excludes potentially failing field/native operations and mutable captured aliases. This is not general memory LICM |
| Loop canonicalization / induction / strength reduction; `-fivopts`, `-ftree-scev-cprop` | Recognize finite numeric/literal ranges and exact induction recurrences under Number semantics; remove abstraction overhead without changing arithmetic results | Canonical iterator recognition and dispatch removal are integrated. General scalar-evolution recurrences, induction replacement and final-value formulas are not implemented; IEEE Number semantics require a separate exact-integral proof before arithmetic substitutions |
| Unswitch / peeling / unrolling; `-funswitch-loops`, `-fpeel-loops`, `-funroll-loops` | Split invariant conditions or expand proven finite small iterations using a code-size cost model; preserve per-iteration stores, callback order and empty-range behavior | `LoopOptimization.Expand` fully expands a statically sized adjacent MakeRange under an O-level code-growth budget. Already-evaluated elements are staged once and retain raw storage identity; they need not be literals. This is full finite expansion, not partial unrolling. General invariant-condition unswitching and peeling remain missing |
| Tail/sibling-call optimization; `-foptimize-sibling-calls` | Replace actual tail recursion with frame reuse only when closure locations and invocation-owned jobs have correct lifetimes | `TailCallOptimization` is integrated for self-tail calls with return/jumps-to-return continuation. It stages actual callee and arguments, creates fresh logical invocation locations, restores captures, then jumps to entry. Invocation-owning work excludes the rewrite. Mutual/sibling tail calls remain unsupported |
| Tail merging / identical code folding | Merge bytecode tails or bodies only with equal capture layout, effects, callable identity behavior and diagnostic provenance | Not established. Prefer CFG cleanup first; callable identity and source spans prevent blind structural function merging |
| Profile-guided inlining/layout | Feed real bytecode hotness into code-size and dispatch decisions | Not implemented; platform AOT profiling can improve the interpreter itself, not substitute user-program profiles |

The tagged GCC pipeline repeats CCP, FRE, DSE, DCE and copy propagation around
IPA and loop transformations. msync should similarly converge interacting
passes, invalidate summaries after rewriting, and compact artifacts last. A
single adjacency peephole pass cannot implement this interaction.

### Audited production pass order

`Optimizer.Optimize` now actually wires the following stages. Feature summaries
skip passes with no corresponding input constructs; they do not weaken their
proofs. Immutable body identities cache unchanged preparation, and graph caches
include an effect-summary epoch so a changed callee invalidates dependent facts.

```text
O0: FormBlocks only
O1/O2/O3:
  exact literal-body fast path → slot/table compaction → FormBlocks
  otherwise repeat:
    PredicateOptimization          [predicate-bearing bodies]
    ScopeScalarization             [Scope construction]
    TailCallOptimization           [calls]
    LoopOptimization               [iteration]
    recompute FunctionEffects
    GraphOptimization / DCE        [with effect summaries]
    CompileTimeEvaluation          [O2/O3, certified finite pure bodies]
    LocalSlotCompaction
    InterproceduralSpecialization  [O2/O3, direct constant-control/dead inputs]
    one FunctionInlining wave
  until structural stability
  DirectionalNormalization         [plain directional markers]
  GlobalRedundancy                  [O2/O3 + branched body]
  ValueNumbering                    [O1+]
  recompute FunctionEffects
  GraphOptimization cleanup        [reuse valid identity/effect-epoch results]
  LocalSlotCompaction
  ModuleOptimization.Compact
  FormBlocks
```

The audited baseline performed `lower → optimize → lower` during binding;
current binding no longer replays portable optimization. Exact literal bodies avoid rediscovering the graph after a
previous pass has reduced a body to a literal return. This shortcut applies to
literal value bodies, not to a guessed no-work lifecycle bypass.

The production FRE/PRE oracles in `tests/GlobalRedundancy` use the public
`Optimizer.Optimize` at O2 and O3 in addition to direct-pass checks. They compare
actual runtime/serialized results and count residual computations on both
critical-edge directions and bypass paths; the owner's integrated record is
130 contracts. Scope147, Loop74, Tail42 and Predicate85 are focused direct-pass
records; graph semantic756, mutable61 and directional16236 are the owner's
separate acceptance records. These counts describe different contracts and are
not one complete cross-platform run or a universal performance result.

The GCC 16.2 `passes.def` comparison is concrete: early CCP/SRA/FRE/DSE occur
before major IPA; IPA CP/SRA/inlining precede pure-const/modref/reference; later
scalar passes repeat FRE/threading/VRP/DSE/DCE/copy propagation, then LIM/PRE,
loop transformations and further cleanup, before tail calls and RTL scheduling /
register allocation. This is not an instruction to copy its exact ordering:
msync has different memory and scheduling semantics, but the requisite facts
must precede transformations and be recomputed after invalidating rewrites.

### Resolved audit discriminator and oracle setup

The earlier raw-cell alias wrong-code was incorporated into
`tests/PredicateOptimization` and `tests/OptimizationGraph`: two local slots own
one Cell, a store through one must invalidate the other's constant predicate.
The integrated implementation tracks raw LoadCell/LoadStorage/GetCapture
identities and invalidates/retains writes across their storage aliases. This is
no longer listed as an open blocker; keep the discriminator when extending IPA,
PRE or captured-field rewrites.

For raw differential modules, explicitly set `OptimizationLevel: 0` or provide
an already executable-stage baseline. Interpreter binding invokes optimization
at the stored level, whose record default is 1. Otherwise the supposed oracle
may already execute the same optimizer under investigation. Source compiled
explicitly at level 0 and the current global redundancy probes avoid that setup
error. Use `ValueRepresentation.Same`, not public numeric equality, when deciding
that reaching constants or value-number keys have identical representations.

## Context- and machine-dependent work

| Family | Actual msync boundary and status |
| --- | --- |
| Contextual partial evaluation | Binding relocates and folds context-dependent operands without replaying portable optimization. Selected filesystem observations are not reusable validity proofs; the VM does not rerun binding or planning |
| Operation selection | `ExecutionLowering` replaces proven local Transfer with LocalTransfer and materializes builtin endpoint values. Endpoint grammar belongs to the selected endpoint, not the host alone |
| Resource dependence scheduling | `Interpreter.Scheduling` uses endpoint/path hierarchy and read/write dependencies. This is machine/context-dependent I/O scheduling, not GCC RTL scheduling; runtime owner is validating actual bytes and process/barrier lifetimes |
| Directional acquire/release | `Barrier`/`Acquire` and invocation-owned pending work are observable scheduling constraints. No DCE, CSE or loop motion may silently turn them into no-ops or global unrelated drains |
| Kernel I/O and transport selection | Select platform mechanisms through the native runtime, respecting atomic publication and normal retry semantics. Copy throughput needs real file/tree/SSH measurements; no scalar-pass result proves it |
| Instruction selection, addressing modes, register allocation, CPU scheduling, branch relaxation | GCC RTL families (`pass_sched`, `pass_ira`, `pass_reload`) map to the Native AOT toolchain, not new msync bytecodes; no msync implementation claim |
| SIMD/SLP, cache tiling, array loop interchange, hardware prefetch, CRC recognition | No matching user-language packed numeric-array kernel was established. Not applicable to path/range objects merely because both are collections. Native library string/copy kernels may use these internally |
| Unsafe arithmetic/alias options | Do not adopt fast-math reassociation, null-pointer deletion, strict C alias assumptions or UB-based loop bounds as language-neutral improvements |

## Priorities, acceptance and latency

The integrated graph, predicate, field forwarding, redundancy and loop passes
address ordinary nested Scope/function/standard-library composition. Known-finite
pure compile-time evaluation and IPA constant-control cloning/dead-parameter
removal are now integrated at O2/O3. Product-level tests require 200-item pure
loops to become literal returns, and effectful constant-control callees to lose
their unused branch and unused input without erasing their owner-return join.
See [finite evaluation](../../tests/CompileTimeEvaluation/README.md) and
[IPA specialization](../../tests/InterproceduralSpecialization/README.md).
Do not replace missing applicable passes with a statement that all GCC options
or every possible optimization has been implemented.

Semantic applicability and adoption benefit are different questions:

| Remaining family | Fit and current decision |
| --- | --- |
| Constant-control IPA clones / dead parameters | Adopted for known direct calls under a code-growth budget. Original callable definitions remain when escaping; runtime effects in unused actual arguments are retained |
| Finite pure compile-time evaluation | Adopted for certified finite eager-range loops and supported scalar operations. Certification precedes evaluation; no arbitrary iteration cap, recursive interpreter, filesystem read or authentication |
| Dynamic loop unswitching | Semantically suitable for a Bool/type/null predicate proven invariant across cell/field/callback writes. Not adopted in this wave: no measured branch-dispatch bottleneck in dominant path/range I/O loops justifies cloning and analysis cost. Finite MakeRange expansion does not cover this family |
| Numeric induction / strength reduction | Generic IEEE Number recurrence replacement is unsound under rounding, signed zero and NaN behavior. A proven exact bounded integral subdomain could enable a future subset; no such domain is claimed today |
| General sibling/mutual tail calls | Semantically possible with frame/cell and invocation-owner lifetime proof. No production recursive family requiring that generalization was established, so current implementation intentionally supports self-tail reuse only |
| Precise IPA mod/ref / aggregate argument splitting | Potentially useful once actual escaped-field call costs dominate. Existing coarse flags and basic-block SRA are not equivalent; cross-call location identities and ABI-preserving wrappers are prerequisites |
| Array distribution/interchange/tiling/SIMD | No corresponding packed numeric-array workload exists in the current source language. Lazy path ranges and transfer ordering are not interchangeable with tensor loops; native runtime/library kernels remain a separate backend concern |
| User-program profile-directed cloning/layout | No bytecode hotness profile is currently an input. Native AOT profiling concerns interpreter implementation code, not a substitute for source-program profiles |

The adoption decision follows workload, correctness and user-visible latency,
not whether a GCC option name can be attached to a rewrite. In particular,
adding an analysis that slows every small submission to optimize an unobserved
case would conflict with the stated millisecond-latency objective.

The existing `tests/OptimizationGraph` is an appropriate differential entry:
level 0 versus levels 1/2/3; `.mso` and `.mexe` serialization; independently fresh
output directories; real bytes; post-binding filesystem mutation; selected
diagnostic comparison. Extend it with discriminators for each new mechanism:

* Unknown condition with identical incoming constants, infeasible predecessor,
  repeated nullable test, and a store between two otherwise identical tests.
* Repeated pure expression versus repeated `exists`, invalid regex and process
  call; preserve effects/errors and avoid authenticating unselected endpoints.
* Scope aliases, overwritten fields, escaped constructors and shared captured
  cells; REPL exports remain roots even if the current submission never reads them.
* Recursive nontermination and zero-iteration loops; optimization must not invent
  a return or execute a formerly skipped trapping expression.
* For every structural assertion, require the relevant residual operation to
  disappear, not merely unchanged final values. Re-optimizing accepted output
  should be stable, and serialization must not restore removed tables.

For performance, freeze source/native artifact identities and alternate baseline
versus candidate on startup, check, source/object submission, nested composition,
small file and directory transfers. Report millisecond p50/p95, compile/bind/run
phase costs, allocation and residual bytecode counts. Compare legacy Rust and
the current C# native baseline using the established
[performance journeys](performance-v080.md). Scheduling wins additionally need
actual independent/overlapping path jobs and barrier-controlled processes on
Linux and Windows; microbenchmarks alone do not establish user-visible latency.

## Source-container follow-up: GCC and LLVM (2026-11-09)

The new indexed containers supply a concrete workload for another optimization
selection, rather than a reason to copy the remaining GCC option list. Upstream
references were checked against GCC **16.2** and LLVM **23.1.5**; the latter is
advertised by the [LLVM project](https://llvm.org/). The implemented additions
are machine-independent and run before `.mso` publication. Current contextual
binding consumes the result without replaying the optimizer; none caches
filesystem or remote proofs.

| Selected mechanism | Actual implementation and semantic boundary |
| --- | --- |
| Dependency-local analysis invalidation | `OptimizationDependencies` records actual direct/dispatch callee bodies and effect summaries, replacing the graph cache's module-global effect epoch. Transitive effects already come from the existing fixed point. Unrelated functions keep their valid graphs; body/layout changes invalidate dependencies. This follows LLVM's [analysis-preservation mechanism](https://llvm.org/docs/NewPassManager.html#implementing-analysis-invalidation), not a new pass-manager framework |
| Indexed-memory read availability | `IndexedMemoryOptimization` reuses a dominating successful Buffer read, intersects the same producer at CFG joins, and resets on backedges. Exact indices distinguish disjoint writes; value-only stores preserve length, while resize/callback/barrier clobbers invalidate facts. The first potentially failing read and its span remain. This adopts the useful [MemorySSA clobber-query principle](https://llvm.org/docs/MemorySSA.html) without introducing general memory SSA or speculative DSE |
| Closed native call-domain specialization | The compiler's existing monotone callable domain can prove one native target. O2/O3 standalone lowering then removes dynamic dispatch while retaining callee-before-argument evaluation. Multiple/unknown domains, sessions and editor probes remain relinkable. Compiler-only boundaries disappear before artifact output; there is no new bytecode format |
| Remove representation-only wrappers | Buffer values are the owned Range itself; the named family contains native function values. Removing the Scope/fields-array wrapper and source method frames makes indexed optimization reach the public container API instead of an artificial direct-FFI-only probe |

The [dependency probe](../../tests/OptimizationDependencies/README.md) measured
the same source graph in ABBA order: graph analyses **227 → 130**, optimizer
allocation **1,939,544 → 1,557,904 bytes**, and pooled p50 **4.2068 → 3.0959 ms**.
Optimized artifact bytes are identical, and the native-only unchanged control
adds no allocation. This is optimizer cost reduction, not a new user-program
transformation or an established callee-literal folding feature.

The [indexed probe](../../tests/IndexedMemoryOptimization/README.md) consumes
1024 loop iterations. Sixteen repeated read sites become one: prepared VM p50
**18.3638 → 2.8472 ms**, allocation **3,590,224 → 272,472 bytes**. The optimizer
spends another roughly **0.21 ms / 104 KB** on that case; it is not a free pass.
No-Buffer optimizer allocation remains exactly unchanged. A cheap literal-index
screen avoids CFG work for the distinct-index control. These are managed causal
component observations, not universal Native AOT startup or tree-throughput claims.

Public-source coverage closes the mechanism: 77 native-domain contracts, 206
indexed contracts, 191 owned-carrier checks, nine actual CLI routes, and the
four-container source/object/executable aggregate passed. The final native
platform wave owns its own immutable-image evidence. See the native-friendly
[CLI entrypoint](../../tests/Msync.EndToEnd/static_optimization_journey.py).

Two real residual opportunities were deliberately not added as unconditional
passes. A frozen probe confirms ordinary natural loops are outside the current
iterator-only LICM recognizer, and `x >= 0` does not eliminate a later `x < 0`.
However, actual tree/heap arithmetic is mostly variant, Heap already hoists its
size, and moving policy/field loads needs must-execute and alias proofs. A fresh
CFG pass or general numeric lattice was not justified on every small script.
Future demand-driven IEEE-aware relations must retain NaN and signed-zero facts;
general store deletion also needs the exception/must-execute conditions in
LLVM's [tagged DSE implementation](https://github.com/llvm/llvm-project/blob/llvmorg-23.1.5/llvm/lib/Transforms/Scalar/DeadStoreElimination.cpp).
SIMD, CPU register allocation, fast-math and broad loop cloning remain unsuitable
for the established workload, not silently missing promised features.

## General-computation middle-end architecture (2026-11-11)

The [current VM diagnosis](performance-assumption-audit-v086.md#current-vm-cost-fewer-interpreter-actions-before-a-different-dispatcher)
locates two different opportunities: remove work from the user program and
reduce the implementation cost of the remaining bytecodes. The former belongs
to the compiler. Do not make the latter pay for optimizer-only facts, or
mistake an interpreter implementation speedup for a good middle-end rewrite.

The [GCC 16.2 pipeline][pipeline] and the [LLVM 23.1.5 default pipelines][llvm-pipeline]
are both ordered around reusable analyses and cleanup after transformations:
early simplification exposes IPA and loops; IPA and loop transforms create
further scalar opportunities. [LLVM's analysis manager][llvm-npm] gives cached
results an IR-unit owner and makes preservation explicit. Adopt the ownership,
not the whole pass-manager/plugin surface. Current msync already has a
dependency-local graph cache (`OptimizationDependencies`), but graph DCE,
predicate propagation, indexed availability and call-target discovery each
decode overlapping facts. A shared structure must remove that work, not run a
second implementation alongside every unchanged pass.

### One geometry, several fact owners

| Scope | Reusable facts | Invalidated by |
| --- | --- | --- |
| Function body | Block/edge identity, reachability, reverse postorder, dominators, stack shape at edges | Changed block boundaries or terminators; start with immutable body identity |
| Value flow | Reaching definitions for private slots and stack columns, edge-specific phi inputs, exact constants, immediate values, callable/receiver alternatives | Changed definitions or contributing feasible edges; capture/alias effects invalidate cells, not unrelated private slots |
| Logical memory | May-clobber dependencies for cells, field locations, indexed contents and lengths; callee parameter/capture-relative effects | Overlapping writes or unknown aliases, relevant callee summaries, explicit enumeration/callback effects |
| Profitability | Loop/call/edge counts, residual dynamic work, code growth, compile cost | New artifact/site identity, changed workload or instrumentation mode; never an input to legality |

`ControlFlowGraph` consumes the existing Core `BlockBuilder` projection, maps
PCs to dense block IDs and retains edge roles independently of unique neighbor
lists. Two edges can have the same destination: `IteratorNext` success pushes
the item and exhaustion does not. An explicit virtual exit and unreachable
predecessors remain distinguishable. Typed edges, reverse postorder and
dominators are independently lazy; global redundancy's adjacency-only work
does not allocate the other analyses. [Cooper–Harvey–Kennedy][dominators]
reverse-postorder intersection computes immediate dominators; tree intervals
make dominance queries constant time. The late optimizer's
`FunctionAnalysisContext` can share this geometry between global redundancy,
indexed memory and natural loop clients. It retains only the current
instruction stream's geometry, not a cache of historical body versions.
Metadata-only copies can reuse the graph; a new instruction stream replaces
it. The linked-source oracle under `.temp/middleend-research/graph-test/`
checks 1,500 generated CFGs against reachability after deleting each
candidate dominator, plus duplicate iterator-edge, empty and owner controls.
New consumers should migrate rather than copy this geometry.

An isolated managed Release component compared the old global-redundancy
partition and per-client builds with the shared owner, keeping the same
transformations on both sides. It loaded the same seven O0 objects before
timing, ran seven alternating pairs on CPU 11 with tiering off, and compared
each output artifact's SHA-256. All optimized artifacts were byte-identical.
Geometry builds fell from **15→14** (Hash), **82→79** (Heap), **7→5**
(scalar) and **2→1** (indexed diamond). Short, SHA and Set stayed at 1, 20
and 267 respectively; repeated function-body rewrites can still require
new graphs. Median cumulative allocation was near-neutral or slightly lower
on each sample group (e.g. −11,648 B on Set's ~28.2 MB and −5,496 B on
Heap's ~330.8 MB). Elapsed observations on the shared host were mixed; this
is duplicate-analysis evidence, not a compile-latency speedup.
Receipts: `.temp/middleend-research/compile/compare-edges-lazy.csv` and
`compile/snapshot/`.

Represent private values by definition identity and attach joins to actual CFG
edges. Represent aliased stores by location identity and memory definition, not
as reassignment of a private virtual value. Do not put distinct semantic
identities in the same lattice merely because they have the same type. Begin
with sparse demand for facts (use sites, candidate loops and candidates for
clobber queries); do not allocate a full value or memory SSA state for every
instruction. Keep stack bytecode as the portable, inspectable output. Analysis
objects are owned by the optimizer for the precise body and dependency facts,
never encoded as imported authority in format 5. A rewrite emits an immutable
body and reuses old identities only if the relevant facts are still valid.

### First algorithm slices and proof boundaries

**Loops.** Current `LoopOptimization` only recognizes a lexical
`IteratorNext/GetIterator/StoreLocal` shape. Even a constant seven-iteration
scalar recurrence retains an O2 backedge in the Native CLI control. A
dominator-based census of the previously checked, freshly bound library matrix
has the following static coverage; duplicate specializations are counted and
hotness is not inferred.

| Frozen O2 artifact | Natural-loop headers | Non-iterator headers | Non-iterator, one entry/latch/exit |
| --- | ---: | ---: | ---: |
| Set | 8 | 5 | 2 |
| Heap | 6 | 4 | 1 |
| Hash | 4 | 2 | 2 |
| SHA-256 | 5 | 5 | 5 |
| Stdin scalar recurrence | 1 | 1 | 1 |

See the typed census/reproduction in `.temp/middleend-research/` and the
seven-iteration source/disassembly in `.temp/midend-loop-research/`.
[LLVM Loop Simplify/LCSSA][llvm-loops] isolates loop entry, latch, exits and
loop-defined values live beyond an exit. [GCC scalar evolution][gcc-scev]
represents recurrences relative to a loop and [iteration analysis][gcc-niter]
returns the assumptions and zero-iteration condition with its answer. For
msync, recognize a natural loop by a reachable dominance backedge; a numerically
smaller PC or SCC alone is not a canonical loop. Rotation preserves one
pre-entry test (including zero iterations) and the exact selected comparison.
Unroll with preserved guards when that has a measured benefit, or group guards
with an exact counted proof and the original residual loop. For `+0,+1`,
`n <= 2^53` plus `i + (k - 1) < n` certifies the next k tests and exact
updates, including fractional bounds. An ordered comparison rejects NaN
and positive infinity; the residual retains original comparison semantics
and any rounding-induced nontermination. Do not replace `!(i >= n)` by
`i < n` on an unconstrained Number. Preserve each arithmetic reduction's
order and check selected early exits, aliased captures, and zero-trip effects.
Separate constant finite evaluation from dynamic unrolling in measurements.

**Memory.** Start with edge-translated *full* redundancy, not speculative
loads. For each feasible incoming edge of a join, translate the selected
receiver and index to that edge and require a successful read witness for
the same unchanged resource. Reuse a joined private local when its edge
values are exactly those reads; otherwise materialize one result phi with
copies on the selected edges, preserving source effects, first failure,
and raw reference identity. In the dynamic stdin diamond at
`.temp/memory-edge-audit/selected.msync`, O2 retains three reads; the
`before` local already carries the right per-edge witness for the third.
Current Heap has a source-level pilot for this effect; measure a pre-pilot
source control and the unmodified library separately. A second candidate,
BTree child selection after a possible split, needs a different witness
on the split edge. Never assume different value numbers cannot alias.

[GCC's virtual operands and alias oracle][gcc-alias] and [LLVM MemorySSA][llvm-memory]
both separate an ordered memory graph from location-specific alias queries.
Model stable definition IDs at program points and incoming-edge memory phis;
do not assign new versions on every fixed-point visit or eagerly create
variables for every object × index. Walk past proven-disjoint clobbers only
for the selected query. Keep length/shape separate from contents, and local
memory from external scheduling. [GCC FRE/PRE][gcc-pre] and [LLVM GVN][llvm-gvn]
motivate per-edge translation; insertion of a potentially failing load is a
separate PRE decision. [LLVM DSE][llvm-dse] also requires must-execute and
exception checks; msync Buffer writes add index failure and target-dependent
conversion, and must not be deleted just because their final value is dead.

**Calls and predicates.** Share selected call-target and capture facts across
inlining, constant arguments, effects and memory analysis. The current
`FunctionInlining.Targets` loses facts at branches/calls, while
`InterproceduralSpecialization.Calls` makes a second approximation.
A positive Call.B is not proof of a source-valued receiver: native/process
alternatives and open session dispatch have different execution contracts.
Use the HIR callable domain or reconstructed proven value flow, and keep
capture cells and invocation ownership intact. Use true strongly connected
components (the current inliner DFS only marks some backedge paths) before
widening recursion-sensitive eligibility. Separate legal sites from cost,
and charge argument/capture transport, body growth and cleanup. See [GCC
IPA summaries][gcc-ipa] and [LLVM InlineCost][llvm-inline].

In the exact checked Set artifact, `NaturalOrder::less` accounts for
478,589 and `Set::node` for 309,586 of 1,008,026 observed direct source calls;
Heap's numeric comparator accounts for 191,943 of 273,867. This is a measured
target ordering, not authority to bypass a callback. Also independently
remove a same-successor branch when its condition is known to be an already
available Bool; the Set comparator has two `LoadLocal; IsNull; JumpIfFalse`
triples of this shape (about 2.87 million dynamically executed instructions).
Preserve effectful producer evaluation and don't remove a deferred wait or
condition error. Counts and exact image identities are in
`.temp/ipa-inline-research/`.

### Convergence, budgets and self-profiling

Use one initial feature scan and demand expensive analysis only for actual
clients. A local CFG fact survives a callee-only change; a call-dependent fact
survives only if its body/selected summary dependencies match. A finite
value lattice needs canonical definition/edge IDs, and an allocation or
loop versioning transform needs a bounded, monotonic code-growth budget:
stable results should not clone their own copies in the next outer round.
Compute prerequisite facts once per body, use reverse-dependency worklists
for changed IPA summaries, and run affected simplification after mutation.
Retain current literal and linear fast paths; profile no-hit and sparse
branches before broadening analysis to all functions.

For a proposed transform, record compile analysis time/allocation and static
growth separately from dynamic opcode, call and native operation counts.
Estimate gain from the removed dynamic units minus added work, with separate
weights for frame, memory and branch operations; an isolated opcode count is
not a hardware-cycle prediction. Tag counters by exact image identity,
phase, function ID and PC or edge. Never join PCs across rewritten images
as if they were the same instruction. Use phase correlation for work
accounting and stable source/semantic origins only for cross-image attribution.
Classify input distributions by actual branch/iteration counts, not by static
candidate counts or invented population weights. Profiles select profitable
legal rewrites; they cannot establish a proof.

Use source, relocatable and bound routes with checked outputs/error order,
and a genuinely unoptimized control. For paired Native AOT performance,
separate same-image runtime ablations from different-image compiler
transformations. Alternate before/after after warmup; retain sizes, artifact
identities and samples. Time ordinary runs with telemetry off to estimate
user cost; use the opt-in SQLite instruction mode to account for changed
work, and report instrumentation overhead independently. Include a
no-opportunity short script, a runtime-sized arithmetic loop, the selected
indexed read diamond/alias controls, and the checked library matrix;
none alone certifies mixed transfer scheduling. These tests discriminate
shape, compile cost and execution benefit instead of treating pass count as
progress.

[llvm-pipeline]: https://github.com/llvm/llvm-project/blob/llvmorg-23.1.5/llvm/lib/Passes/PassBuilderPipelines.cpp
[llvm-npm]: https://llvm.org/docs/NewPassManager.html
[llvm-loops]: https://llvm.org/docs/LoopTerminology.html
[dominators]: https://www.cs.tufts.edu/~nr/cs257/archive/keith-cooper/dom06.pdf
[gcc-scev]: https://gcc.gnu.org/onlinedocs/gccint/Scalar-evolutions.html
[gcc-niter]: https://gcc.gnu.org/onlinedocs/gccint/Number-of-iterations.html
[gcc-alias]: https://gcc.gnu.org/onlinedocs/gccint/Alias-analysis.html
[llvm-memory]: https://llvm.org/docs/MemorySSA.html
[gcc-pre]: https://github.com/gcc-mirror/gcc/blob/releases/gcc-16.2.0/gcc/tree-ssa-pre.cc#L52-L109
[llvm-gvn]: https://github.com/llvm/llvm-project/blob/llvmorg-23.1.5/llvm/lib/Transforms/Scalar/GVN.cpp#L1735-L1790
[llvm-dse]: https://github.com/llvm/llvm-project/blob/llvmorg-23.1.5/llvm/lib/Transforms/Scalar/DeadStoreElimination.cpp#L8-L25
[gcc-ipa]: https://gcc.gnu.org/onlinedocs/gccint/IPA.html
[llvm-inline]: https://github.com/llvm/llvm-project/blob/llvmorg-23.1.5/llvm/lib/Analysis/InlineCost.cpp
[releases]: https://gcc.gnu.org/releases.html
[options]: https://gcc.gnu.org/onlinedocs/gcc-16.2.0/gcc/Optimize-Options.html
[pipeline]: https://raw.githubusercontent.com/gcc-mirror/gcc/releases/gcc-16.2.0/gcc/passes.def
[trees]: https://gcc.gnu.org/onlinedocs/gccint/Tree-SSA-passes.html
[rtl]: https://gcc.gnu.org/onlinedocs/gccint/RTL-passes.html
[state]: mutable-state-and-effects-v080.md
