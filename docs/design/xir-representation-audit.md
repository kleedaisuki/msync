# Representation audit: keep known facts through execution

2026-11-22. Evidence review of the [XIR draft](executable-machine-ir.md), against
`9bd388d8` and packaged 0.9.3 Linux x64. This is not an implementation sequence.
It extends the analysis of concrete layouts and typed pending values to
closures, ranges, calls and contextual binding.

Use three categories:

- **Established fact:** the producer already knows it; a representation can
  carry it across a call, pass or artifact boundary.
- **Backend choice:** a placement, encoding or code-cache decision that depends
  on the final target. Late selection can be legitimate; recovering a
  language-level contract from the chosen encoding is a separate problem.
- **Actual runtime input:** a new value, effect, identity or environmental
  observation that must be supplied or decided at the relevant boundary.

## Closures: code identity is not environment identity

[HIR](../../src/Msync.Hir/Model/HirModule.cs#L24) has function/template IDs,
capture mode and source identity, while
[LLVM lowering](../../src/Msync.Hir/Lowering/LlvmHirLowerer.Calls.cs#L114)
builds an environment record and a callable aggregate (code, environment,
native name). For a known source target it emits individual capture
arguments **and** saves the evaluated callable in an operand bundle. The
[Native emitter](../../src/Msync.Hir/Lowering/LlvmNativeLowerer.Memory.cs#L191)
switches back to passing the descriptor;
[PlanCallCapturePreparation](../../src/Msync.Hir/Lowering/LlvmNativeLowerer.CallCaptures.cs#L13)
walks uses to discard loads, offsets and extracts that served the other ABI.
The [machine linker](../../src/Msync.Runtime/NativeModel/Tiering/NativeCompiledFunction.cs#L304)
then scans definitions and reads so it can fuse selected `ComposeFunction`
or field loads back into calls. This is a verified representation round trip,
not evidence that every closure requires runtime reconstruction.

**Draft correction.** The retained `closure_poly` program chooses between
`mk(3)` and `mk(5)`, where both calls create a closure of the same source
`f`. Its O2 portable disassembly has `Call ... b=5` to the same guest
specialization. It extracts three components from each result before the
loop, then emits three `Select`s and a `ComposeFunction` at each call in the
loop. The earlier 4,347 ns point measures this whole shape, not selection
between two code entrypoints; it does not isolate the cost of composition.
A separate control selecting between distinct `triple` and `quint` functions
has `Call ... b=-1 ... symbol="2:5;3:6"` and returns 116 for eight inputs.
That control was checked for behavior, not timed.

The closure still captures a live location when required; a fresh escaping
capture, a rebound callable, or a captured field changed through another
alias remains observable. The callee expression is evaluated before
arguments, even when its code target is known. Retain the evaluated
environment at that point, rather than rereading a mutable callee after
argument effects. A known code target plus a varying environment is a direct
call contract, not an indirect one. Non-escaping or stable captures can be
carried in a more direct representation where the semantics admit it. Shao
and Appel's [closure-conversion paper](https://collaborate.princeton.edu/en/publications/efficient-and-safe-for-space-closure-conversion/)
likewise separates installing the environment from transferring control, and
addresses closure allocation and lifetime rather than removing either by fiat.

## Range: producer and consumer carry more than an item type

Item ABI and producer mode are separate. A ready numeric sequence, a snapshot,
a lazy callback pipeline and a live file stream have different iteration and
ownership contracts. The type `Range<Number>` does not imply either an async
iterator or an unboxed number cursor for every producer.

In the packaged O2 numeric window probe, `iota(0,12) |> drop(2) |> take(3)`
is emitted as the `iota` guest wrapper, named calls to
`ranges.sequence/drop/take`, then generic iterator operations. In the
[runtime](../../src/Msync.Runtime/NativeModel/NativeInterpreter.RangePlans.cs#L9),
`TryPlanRange` maps names to opcodes, inspects carrier, readiness, arena and
callback form, creates plan nodes and fuses the window. Diagnostics confirm
three nodes in total, one window fusion and one window kernel yielding three
items; the result is 9. The fast cursor already works. The redundancy here is
forgetting a known producer/operator chain and recognizing it again at
execution, not a claim that numeric ranges all use async enumeration.

The [datum range path](../../src/Msync.Runtime/NativeModel/NativeInterpreter.RangePrimitives.cs#L50)
also re-derives transform/scan result metadata from the callable even when
the compiler supplied a physical result ABI; it uses a datum iterator, an
async value-yielding adapter and side tables for retained roots/deferral
where needed by that interface. For a fixed callback signature, carry its
item ABI and result mode in the producer contract. For a truly varying
callback or stream, the unknown portion stays at iteration. These paths
were inspected, not separately benchmarked.

Retain evaluation of literal elements at construction; lazy callbacks
execute on consumption, not construction. Keep short-circuiting, order,
iterator disposal, pending identities, and effects on each traversal. In
the effectful probe, `take(0)` calls the callback zero times, materializing
`take(2)` calls it twice, and reading the front of the original Range
calls it once more; the output is `0,0,2,2,0,3`. The useful lesson from
[Stream Fusion](https://www.cs.tufts.edu/~nr/cs257/archive/duncan-coutts/stream-fusion.pdf)
is to retain producer/consumer structure until specializing the loop, not
to import the pure-list rewrite rules into an effectful Range.

## Calls: separate target, frame ABI, and work ownership

`NativeOp.Call` carries a direct target when known, or a finite target map
for some indirect calls. [FunctionDispatch](../../src/Msync.Core/Artifacts/FunctionDispatch.cs#L9)
caches a typed operand for the string wire form; the dynamic call performs a
lookup but does not reparse the string on every call. REPL replacement and
actual function-valued selection still require a late target decision.

[Native lowering](../../src/Msync.Hir/Lowering/LlvmNativeLowerer.Memory.cs#L191)
records the known target and argument slots. The
[backend](../../src/Msync.Runtime/NativeModel/NativeBackendCompiler.cs#L30)
then classifies work ownership from a call graph and builtin behavior,
discovers unread parameters and plans call windows; the
[execution linker](../../src/Msync.Runtime/NativeModel/Tiering/NativeExecutionFunction.Calls.cs#L43)
consumes the serialized call-site plan and constructs physical moves from
actual frame offsets. Calculating a move from two physical offsets is
ordinary linking. Making each tier infer whether a fixed guest call is a
closure, whether its arguments need an alias snapshot, and whether it owns
work is not inherent to the language.

There is a second, distinct loss: the machine path consumes eligible
[leaf entries](../../src/Msync.Runtime/NativeModel/Tiering/NativeCompiledFunction.cs#L238),
and [callable SSA](../../src/Msync.Core/NativeModel/NativeSsaBody.cs#L12)
disallows bodies with a call or a backedge. The fixed target of `mix -> clip`
is present in the portable program. Yet that fact alone cannot make
`mix` a callable machine leaf. Stack capacity, recursion, cancellation,
effect ownership, and correctly ordered argument snapshots are real
constraints. Turning every syntactic non-leaf into a generic bridge is a
tier policy, not a missing source-language fact. Do not conflate that policy
with runtime target lookup, or fix it by blindly relaxing recursion guards.

## Binding: new observations, not a second source compiler

[Preparation](../../src/Msync.Runtime/NativeModel/NativePreparation.cs#L22)
does legitimate partial evaluation over context and selected metadata. It
residualizes at unknown control, cycles or opaque effects instead of doing
user work early. A detached selection probe bound a directory containing
`first.txt`, then changed its bytes and added `later.txt`. Running from
another cwd copied the new bytes of `first.txt` under the bound destination,
did not copy `later.txt`, and recorded no contextualization/backend
compilation spans. This distinguishes captured context and membership from
live payloads. It does not suggest execution invokes the planner again.

After contextual planning, [the optional binding inliner](../../src/Msync.Optimization/NativeModel/NativeOptimizer.Inlining.cs#L8)
does a second general guest rewrite, reconstructs callable origin from split
descriptors for admission, relocates plan intervals, and invalidates affected
facts. Rebuilding information actually invalidated by a rewrite is valid
engineering; discarding stable storage contracts wholesale makes the rewrite
costly and can make its result worse. The sharp case is the
[frame-wide veto](../benchmarks/0.9.3-runtime-attribution.md#1-binding-inlines-a-call-that-disables-the-whole-functions-jit):
address replanning invents an eligibility loss where the bound context
provided no new alias information. The same flag also makes
[numeric representation selection](../../src/Msync.Runtime/NativeModel/NativeNumberLowering.cs#L27)
treat every slot as address-exposed and forget its bounds on modeled memory
writes. This is another code-proven consequence of the flag, not a measured
share of the signal regression.

The executable does carry backend decisions. At executable link there are
still narrower representations rebuilt from the portable view; for example
[`NativeExecutionFunction`](../../src/Msync.Runtime/NativeModel/Tiering/NativeExecutionImage.cs#L120)
calls `NativeExecutionOperand.Link`, which computes a layout-check
decision for Narrow/Convert, then overwrites that decision with the saved
backend bit. `FuseScopeCopies` separately counts uses and entry edges to find
forwardable linked copies. The former is directly redundant; the latter is
a real optimization currently performed after the saved plan, not another
whole-program alias analysis. Neither is equivalent to replanning filesystem
effects.

## Implication for the draft

An executable IR earns its place by carrying the relation between code and
environment, producer and cursor, call site and ABI, and prepared effect and
its actual resource. More opcodes or another JIT do not by themselves achieve
this. Select only the unknown component at runtime. Preserve what existing
paths already do well: fixed call plans, typed dispatch operands, numeric
window kernels, frozen selection membership, and residual effect execution.
Do not move program effects, iterator consumption or identity transitions
to an earlier phase to get simpler numbers.

Probe scope: packaged 0.9.3 Linux x64, O2 source / portable / prepared with
JIT off and auto for the new closure and Range controls (18 executions).
The context probe explicitly binds and then runs detached with diagnostics.
Retained fixtures, text disassembly and receipts are in
`.temp/xir-draft-review/representation-audit/`; reproductions use `compile -O2`,
`compile -O2 --prepare`, `run` and `disassemble`. No new performance run,
product source change, or artifact format change.
