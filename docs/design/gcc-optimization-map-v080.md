# GCC 16.2 optimization mapping for msync 0.8

## Baseline and interpretation

The requested reference is the latest stable major line, GCC 16.2, released on
August 7, 2026, not GCC 17 development. See the [release table][releases], the
[versioned option manual][options], and the actual [16.2 pass pipeline][pipeline].
The internals [Tree SSA][trees] and [RTL][rtl] explanations describe mechanisms;
the tagged pipeline, rather than a moving manual, establishes pass ordering.

This is an implementation audit of the shared working tree on November 9, 2026,
starting at `f57e4d1` plus the optimizer/state/scheduler work in progress. Status
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
| Constant propagation; `-ftree-ccp` | Meet reaching definitions, evaluate scalar constants, fold branches; genuine SCCP also tracks executable edges so impossible predecessors do not pollute joins | `GraphOptimization.Resolve`, `PureEvaluation.Native`: implemented CCP/folding. `Discover` initially explores both conditional successors; executable-edge SCCP is not yet a distinct implemented analysis |
| Copy/forward propagation; `-ftree-copy-prop`, `-ftree-forwprop` | Follow definition identity, not source names; bypass dead stores and copies while retaining captured locations | Reaching-definition graph resolves constant copies; general nonconstant copy substitution remains partial |
| DCE; `-ftree-dce`, conditional DCE | Backward demand from return, observable effects/errors/divergence and live REPL exports | `GraphOptimization.IsRoot/Keep`; entry `PreserveLocals` bypass. Implemented demand elimination; calls and much arithmetic are retained conservatively unless folded/inlined |
| DSE; `-ftree-dse` | Remove overwritten local values and nonescaping object construction/stores only when no read, alias, capture or escape observes them | `DiscoverWrites`, `AllocationOrigins`, `DiscoverEscapes`: implemented dead-object/store subset; not full per-field memory SSA or store-to-load forwarding |
| FRE/GVN; `-ftree-fre` | Value-number pure expressions with operand definition versions; reuse a dominating computation without changing evaluation order or error behavior | No value-number table/dominator-based availability pass found in initial audit; applicable missing work, graph optimizer owner |
| PRE / code hoisting; `-ftree-pre`, `-fcode-hoisting` | Reuse expressions already available on some predecessors by edge insertion; never introduce errors/effects on previously unselected paths | Applicable for pure total scalar/string/path operations. Not implemented in initial audit; requires dominance/availability and critical-edge handling, not constant folding renamed PRE |
| Predicate simplification, jump threading; `pass_thread_jumps`, `-ftree-vrp` | Track `is null`, Bool/type tests, equality and finite alternatives per edge; invalidate facts on writes and modifying calls | Frontend has typing/narrowing; bytecode optimizer has constant branch folding but no general edge-predicate implication or range-domain pass found |
| IPA effects; `-fipa-pure-const`, `-fipa-modref`, `-fipa-reference` | Fixed-point call summaries must distinguish reads/writes/captures, errors, divergence, enumeration and invocation ownership | `EffectAnalysis.FunctionEffects` implements effect flags and recursive divergence propagation; initial graph roots do not consume the summaries. Per-location mod/ref precision is missing |
| Direct-call discovery / IPA CP / clones; `-fipa-cp`, `-fipa-cp-clone` | Callable identity plus argument types specializes generics; constant arguments need value specialization or inlining, not type specialization alone | `Compiler.Functions.Specialize`, `FunctionInlining.Targets`: implemented type specializations and local target discovery. General cross-call constant-value cloning is not established |
| Early/single-use/indirect inlining | Splice fresh invocation slots, preserve capture-cell references, relocate branch targets; do not erase scheduler ownership boundaries | `FunctionInlining`: implemented nonrecursive capture-aware inlining with size/use policy. Recursive SCCs and invocation-owning bodies excluded; equivalence pending |
| Escape / SRA / IPA SRA; `-ftree-sra`, `-fipa-sra` | Replace a nonescaping Scope's fields with scalar definitions, including intervening field stores; preserve instance identity when observable | Allocation origins and immutable-field folding implemented. `ResolveMember` rejects an allocation if any reachable SetMember may target it: mutable-field scalar replacement and unused-parameter rewriting remain absent |
| Reachability / section GC / constant merging | Prune unreachable functions, constants and dispatch references; merge only immutable payloads | `ModuleOptimization.Compact` implements closure/call/dispatch relocation and table pruning. Never intern mutable Scope instances by equal contents |
| Loop invariant motion; `-ftree-loop-im` | Hoist pure total computations whose operands and memory versions are invariant; preserve zero-iteration and lazy callback semantics | No loop analysis/preheader/dominator pass found. Applicable to actual bytecode loops; I/O metadata and process calls are not invariant just because arguments are |
| Loop canonicalization / induction / strength reduction; `-fivopts`, `-ftree-scev-cprop` | Recognize finite numeric/literal ranges and exact induction recurrences under Number semantics; remove abstraction overhead without changing arithmetic results | Not implemented in initial audit. msync numbers currently use IEEE floating arithmetic; do not import signed-integer no-overflow assumptions |
| Unswitch / peeling / unrolling; `-funswitch-loops`, `-fpeel-loops`, `-funroll-loops` | Split invariant conditions or expand proven finite small iterations using a code-size cost model; preserve per-iteration stores, callback order and empty-range behavior | Applicable selectively; not implemented. Filesystem/deferred ranges are not static arrays, and expanding arbitrary ranges is not compile-time evaluation |
| Tail/sibling-call optimization; `-foptimize-sibling-calls` | Replace actual tail recursion with frame reuse only when closure locations and invocation-owned jobs have correct lifetimes | Applicable but absent in initial audit; Native AOT cannot eliminate interpreter recursion on behalf of bytecode |
| Tail merging / identical code folding | Merge bytecode tails or bodies only with equal capture layout, effects, callable identity behavior and diagnostic provenance | Not established. Prefer CFG cleanup first; callable identity and source spans prevent blind structural function merging |
| Profile-guided inlining/layout | Feed real bytecode hotness into code-size and dispatch decisions | Not implemented; platform AOT profiling can improve the interpreter itself, not substitute user-program profiles |

The tagged GCC pipeline repeats CCP, FRE, DSE, DCE and copy propagation around
IPA and loop transformations. msync should similarly converge interacting
passes, invalidate summaries after rewriting, and compact artifacts last. A
single adjacency peephole pass cannot implement this interaction.

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
