# GCC 16.2 optimization mapping for msync 0.8

## Baseline and interpretation

The requested reference is the latest stable major line, GCC 16.2, released on
August 7, 2026, not GCC 17 development. See the [release table][releases], the
[versioned option manual][options], and the actual [16.2 pass pipeline][pipeline].
The internals [Tree SSA][trees] and [RTL][rtl] explanations describe mechanisms;
the tagged pipeline, rather than a moving manual, establishes pass ordering.

This is an implementation audit of the shared working tree on November 9, 2026,
initially at `f57e4d1`, independently re-audited at `c1efa32` plus the
optimizer/state/scheduler working changes. Status
below means code was inspected, **not** that cross-platform equivalence or a
performance result has been accepted. Implementation owners must update the
status when their changes land. The separate [mutable-state contract][state]
owns language semantics. Historical Rust results in `optimization-roadmap.md`
do not certify these new C# passes.

GCC is a reference for algorithms, not a backend dependency or code transplant.
Its assumptions about C undefined behavior, aliasing and integer overflow are
not msync semantics. Scope allocation identity, captured cells, selected errors,
deferred enumeration, and possible divergence are observable here.

## Stage ownership

| Stage | Available knowledge | Optimization responsibility |
| --- | --- | --- |
| Typed source lowering | Binding identities, fixed storage types, generic callable identities, lexical shapes | Monomorphization, conversion insertion, lowering sugar; reject invalid programs before DCE |
| Relocatable `.mso` | Typed bytecode, CFG, def-use, effects; no selected machine context | All applicable context-independent transformations below |
| Executable `.mexe` | Frozen cwd/HOME/source context and endpoint grammar | Contextual partial evaluation and native operation selection, then the same graph optimizer again |
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
| CFG cleanup; `pass_fixup_cfg` | Remove unreachable blocks, remap branches, collapse empty control paths after each rewrite | `GraphOptimization.Discover/Emit`, `Optimizer.FormBlocks`: implemented subset; graph optimizer owner; equivalence pending |
| Constant propagation; `-ftree-ccp` | Meet reaching definitions, evaluate scalar constants, fold branches; genuine SCCP also tracks executable edges so impossible predecessors do not pollute joins | `GraphOptimization.Resolve` / `PureEvaluation.Native`: active CCP/folding. New `PredicateOptimization.Discover` separately admits only the selected edge for known conditions and merges executable predecessor facts. That pass exists with standalone reflection tests but is not yet called by the production optimizer at this audit point |
| Copy/forward propagation; `-ftree-copy-prop`, `-ftree-forwprop` | Follow definition identity, not source names; bypass dead stores and copies while retaining captured locations | Reaching-definition graph resolves constant copies; general nonconstant copy substitution remains partial |
| DCE; `-ftree-dce`, conditional DCE | Backward demand from return, observable effects/errors/divergence and live REPL exports | `GraphOptimization.IsRoot/Keep`; entry `PreserveLocals` bypass. Implemented demand elimination. Known direct Call with B target now consumes `FunctionEffects` to remove effect-free, nondiverging calls; unknown calls remain roots. Effect summaries are recomputed in the outer fixed point |
| DSE; `-ftree-dse` | Remove overwritten local values and nonescaping object construction/stores only when no read, alias, capture or escape observes them | `DiscoverWrites`, `AllocationOrigins`, `DiscoverEscapes`: implemented dead-object/store subset; not full per-field memory SSA or store-to-load forwarding |
| FRE/GVN; `-ftree-fre` | Value-number pure expressions with operand definition versions; reuse a dominating computation without changing evaluation order or error behavior | `ValueNumbering.Eliminate` is now active: expression keys use op/text/arity and operand value numbers, materialize shared results in fresh slots, and invalidate on modifying operations. It resets at all branch targets and branches: this is local value numbering, not global dominance-based FRE/GVN |
| PRE / code hoisting; `-ftree-pre`, `-fcode-hoisting` | Reuse expressions already available on some predecessors by edge insertion; never introduce errors/effects on previously unselected paths | Applicable for pure total scalar/string/path operations. Not implemented in initial audit; requires dominance/availability and critical-edge handling, not constant folding renamed PRE |
| Predicate simplification, jump threading; `pass_thread_jumps`, `-ftree-vrp` | Track `is null`, Bool/type tests, equality and finite alternatives per edge; invalidate facts on writes and modifying calls | `PredicateOptimization` implements Bool/Null/type/equality edge assumptions, selected-edge constant propagation and fact joins. It has no numeric interval/ranger domain or path duplication. Implementation exists; production callsite absent at this audit point; captured-cell invalidation requires the alias discriminator below |
| IPA effects; `-fipa-pure-const`, `-fipa-modref`, `-fipa-reference` | Fixed-point call summaries must distinguish reads/writes/captures, errors, divergence, enumeration and invocation ownership | `EffectAnalysis.FunctionEffects` implements flags, callgraph worklist propagation, recursive divergence and captured-slot writes. `Optimizer` now passes summaries to graph root selection for direct calls. Per-location read/write summaries and transitive alias effects remain missing; flags alone are not GCC-style precise mod/ref |
| Direct-call discovery / IPA CP / clones; `-fipa-cp`, `-fipa-cp-clone` | Callable identity plus argument types specializes generics; constant arguments need value specialization or inlining, not type specialization alone | `Compiler.Functions.Specialize`, `FunctionInlining.Targets`: implemented type specializations and local target discovery. General cross-call constant-value cloning is not established |
| Early/single-use/indirect inlining | Splice fresh invocation slots, preserve capture-cell references, relocate branch targets; do not erase scheduler ownership boundaries | `FunctionInlining`: implemented nonrecursive capture-aware inlining with size/use policy. Recursive SCCs and invocation-owning bodies excluded; equivalence pending |
| Escape / SRA / IPA SRA; `-ftree-sra`, `-fipa-sra` | Replace a nonescaping Scope's fields with scalar definitions, including intervening field stores; preserve instance identity when observable | `ScopeScalarization` now implements per-basic-block field slots, get/set forwarding, escape marking and live-out escape checks. Escaped objects are reconstructed while tracked fields forward until modifying calls invalidate them; nonescaping allocations become scalar slots. Cross-block memory SSA and IPA unused-parameter rewriting remain absent. Implementation exists; production callsite absent at audit |
| Reachability / section GC / constant merging | Prune unreachable functions, constants and dispatch references; merge only immutable payloads | `ModuleOptimization.Compact` implements closure/call/dispatch relocation and table pruning. Never intern mutable Scope instances by equal contents |
| Loop invariant motion; `-ftree-loop-im` | Hoist pure total computations whose operands and memory versions are invariant; preserve zero-iteration and lazy callback semantics | `LoopOptimization.Hoist` recognizes canonical iterator header/latch/preheader, stable nonshared locals and total typed expression trees. It rejects external body entries, shared locations and potentially failing field/native operations; moves total invariants before first iteration. Implementation exists; production callsite absent at audit |
| Loop canonicalization / induction / strength reduction; `-fivopts`, `-ftree-scev-cprop` | Recognize finite numeric/literal ranges and exact induction recurrences under Number semantics; remove abstraction overhead without changing arithmetic results | Not implemented in initial audit. msync numbers currently use IEEE floating arithmetic; do not import signed-integer no-overflow assumptions |
| Unswitch / peeling / unrolling; `-funswitch-loops`, `-fpeel-loops`, `-funroll-loops` | Split invariant conditions or expand proven finite small iterations using a code-size cost model; preserve per-iteration stores, callback order and empty-range behavior | `LoopOptimization.Expand` implements complete expansion of adjacent finite literal MakeRange loops, with optimization-level code-growth budget and branch relocation. It preserves return/branch exits; no general unswitching or partial peeling pass found. Filesystem/deferred ranges are not expanded. Implementation exists; production callsite absent at audit |
| Tail/sibling-call optimization; `-foptimize-sibling-calls` | Replace actual tail recursion with frame reuse only when closure locations and invocation-owned jobs have correct lifetimes | `TailCallOptimization` implements self-tail calls followed by return/jumps-to-return. It saves arguments, installs fresh invocation locations/captures and jumps to entry; rejects bodies owning read/invoke/control work. Not mutual/sibling tail-call elimination. Implementation exists; production callsite absent at audit |
| Tail merging / identical code folding | Merge bytecode tails or bodies only with equal capture layout, effects, callable identity behavior and diagnostic provenance | Not established. Prefer CFG cleanup first; callable identity and source spans prevent blind structural function merging |
| Profile-guided inlining/layout | Feed real bytecode hotness into code-size and dispatch decisions | Not implemented; platform AOT profiling can improve the interpreter itself, not substitute user-program profiles |

The tagged GCC pipeline repeats CCP, FRE, DSE, DCE and copy propagation around
IPA and loop transformations. msync should similarly converge interacting
passes, invalidate summaries after rewriting, and compact artifacts last. A
single adjacency peephole pass cannot implement this interaction.

### Audited production pass order and integration boundary

At the second audit, `Optimizer.Optimize` actually executes:

```text
O0: FormBlocks only
O1/O2/O3:
  repeat:
    recompute FunctionEffects(module)
    GraphOptimization on each non-preserved function to its own fixed point
    FunctionInlining to its own fixed point
  until instruction sequences stabilize
  ValueNumbering once
  recompute FunctionEffects
  GraphOptimization cleanup
  ModuleOptimization.Compact
  FormBlocks
```

`PredicateOptimization`, `ScopeScalarization`, `LoopOptimization` and
`TailCallOptimization` have real rewrite implementations and standalone test
programs, but no production callsite was present at the audited snapshot. Their
tests invoke internal methods through reflection. Passing those tests does not
mean `msync compile` uses the transformations. This distinction must be removed
only after inspecting actual pipeline wiring and a production-path discriminator.

The GCC 16.2 `passes.def` comparison is concrete: early CCP/SRA/FRE/DSE occur
before major IPA; IPA CP/SRA/inlining precede pure-const/modref/reference; later
scalar passes repeat FRE/threading/VRP/DSE/DCE/copy propagation, then LIM/PRE,
loop transformations and further cleanup, before tail calls and RTL scheduling /
register allocation. This is not an instruction to copy its exact ordering:
msync has different memory and scheduling semantics, but the requisite facts
must precede transformations and be recomputed after invalidating rewrites.

### Reproduced alias invalidation blocker

A scoped local probe under `.temp/gcc-audit` constructed a current-semantics
module accepted by `Artifact.Validate`:

```text
0 True; 1 InitializeLocal 0; 2 LoadCell 0; 3 InitializeLocal 1;
4 LoadLocal 0; 5 JumpIfFalse 10;
6 False; 7 StoreLocal 1; 8 LoadLocal 0; 9 Return;
10 False; 11 Return
```

Slots 0 and 1 own the same Cell after instruction 3. The original O0 interpreter
returns false; direct PredicateOptimization returns true. Production O1 also
returned true on this discriminator at the audit snapshot. Invalidation cannot
be restricted to writes to a function's capture-prefix slots: an ordinary local
can hold a Cell obtained through LoadCell/GetCapture/LoadStorage. Reaching writes,
constant facts, value numbers and effect summaries must share that alias model.
This is a reported implementation blocker, not an accepted transformation.

For a trustworthy probe, explicitly set `OptimizationLevel: 0` on raw baseline
modules: `Interpreter` binds relocatable modules and binding invokes optimization
at the stored level. The record's default is 1, so an omitted level can silently
optimize the purported oracle before execution. Source compiled with explicit
level 0 does not have this particular setup error.

## Context- and machine-dependent work

| Family | Actual msync boundary and status |
| --- | --- |
| Contextual partial evaluation | `Binding.Bind` attaches context, invokes `ExecutionLowering`, reruns `Optimizer`, then repeats lowering/optimization. Implemented in working code; verify idempotence and residual IR rather than assume two repetitions suffice for every future pass |
| Operation selection | `ExecutionLowering` replaces proven local Transfer with LocalTransfer and materializes builtin endpoint values. Endpoint grammar belongs to the selected endpoint, not the host alone |
| Resource dependence scheduling | `Interpreter.Scheduling` uses endpoint/path hierarchy and read/write dependencies. This is machine/context-dependent I/O scheduling, not GCC RTL scheduling; runtime owner is validating actual bytes and process/barrier lifetimes |
| Directional acquire/release | `Barrier`/`Acquire` and invocation-owned pending work are observable scheduling constraints. No DCE, CSE or loop motion may silently turn them into no-ops or global unrelated drains |
| Kernel I/O and transport selection | Select platform mechanisms through the native runtime, respecting atomic publication and normal retry semantics. Copy throughput needs real file/tree/SSH measurements; no scalar-pass result proves it |
| Instruction selection, addressing modes, register allocation, CPU scheduling, branch relaxation | GCC RTL families (`pass_sched`, `pass_ira`, `pass_reload`) map to the Native AOT toolchain, not new msync bytecodes; no msync implementation claim |
| SIMD/SLP, cache tiling, array loop interchange, hardware prefetch, CRC recognition | No matching user-language packed numeric-array kernel was established. Not applicable to path/range objects merely because both are collections. Native library string/copy kernels may use these internally |
| Unsafe arithmetic/alias options | Do not adopt fast-math reassociation, null-pointer deletion, strict C alias assumptions or UB-based loop bounds as language-neutral improvements |

## Priorities, acceptance and latency

Highest-value next mechanisms are executable-edge/predicate propagation,
versioned pure value numbering, mutable nonescaping field forwarding, and usable
interprocedural mod/ref summaries. They directly reduce ordinary nested Scope,
function and standard-library composition overhead. PRE, LICM, cloning and loop
transforms build on those analyses rather than adding independent pattern lists.
Do not replace missing applicable passes with a marketing statement that all GCC
optimizations were adopted.

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

[releases]: https://gcc.gnu.org/releases.html
[options]: https://gcc.gnu.org/onlinedocs/gcc-16.2.0/gcc/Optimize-Options.html
[pipeline]: https://raw.githubusercontent.com/gcc-mirror/gcc/releases/gcc-16.2.0/gcc/passes.def
[trees]: https://gcc.gnu.org/onlinedocs/gccint/Tree-SSA-passes.html
[rtl]: https://gcc.gnu.org/onlinedocs/gccint/RTL-passes.html
[state]: mutable-state-and-effects-v080.md
