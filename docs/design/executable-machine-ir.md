# Executable machine IR: evidence audit and open design (XIR)

2026-11-22. **Not ready to implement.** An audit of the first draft found one design
error that would hang existing programs, and several causal claims the evidence does not
support. This version separates what is measured from what is guessed.
Baseline: 0.9.3, Linux x64. Related: [runtime attribution](../benchmarks/0.9.3-runtime-attribution.md).
Probes: `artifacts/benchmarks/executable-ir-probe/` (local payload, not tracked by git).
Follow-up: [representation audit of closures, Range, calls and binding](xir-representation-audit.md).
Current proposal: [HPIR for the bound .mexe](hpir.md). This audit retains the original XIR evidence.

## Blocking error: readiness cannot be a producer-only type

The draft said only operations that can produce pending results yield `future`, and
`demand` converts and blocks, "which is what `DemandStep` does today". That is wrong.
A pending value flows through arithmetic, select, conversion, function composition,
native calls, fields, captures, parameters and results (`PendingBinary`,
`NativeResult.Select.cs`, `NativeResult.FunctionParts.cs`, `NativeResult.Machine.cs`).
Only some operations, such as a branch, block.

This is observable. `pending-flow.py` starts a process that exits only after the
script writes a file *after* the tested expression. Any early wait never returns:

| Expression on the pending exit code | 0.9.3, JIT off and auto |
| --- | --- |
| `+ 1`; `> 3` | completes |
| passed to a function; recursive accumulator | completes |
| stored in a Scope field after `* 2`; captured by a closure | completes |
| 1,000-iteration accumulation | completes |
| `if v > 100` (control) | waits forever; 8 s timeout |

The control shows what blocking does. Blocking at `+`, or demanding a pending
argument on entry to machine code, would hang programs that complete today.
An independent packaged 0.9.3 check reproduced all eight cases with JIT off and
auto through source, portable and prepared artifacts. For the branch control,
the child had started and the program had not written the gate; writing it
externally let the program finish with the expected output.

### Pending identity is independently observable

The existing [Native model](native-model-v090.md#L369-L372)
requires copied pending values to retain identity and separately evaluated
pending computations not to be merged just because their eventual scalars
match. The `binary` case in
[`FutureIdentityChecks.cs`](../../tests/NativeExecution/FutureIdentityChecks.cs#L11-L38)
specifies that contract, but its expected result is **not** the packaged
optimized baseline.

With the child gated on `ready`, this program must print `2` before releasing
it, then `8`:

```msync
let child = release /bin/sh("-c", "while [ ! -f ready ]; do sleep 0.01; done; exit 7");
let saved = child::exit_code;
let a = saved + 1;
let b = saved + 1;
let values = $std::ranges::materialize($std::ranges::unique([a, b]));
$std::io::println($std::ranges::size(values) as String);
$std::io::write_text(./ready, "go");
$std::io::println($std::ranges::front(values) as String);
```

Use a fresh working directory with no `ready` file for each run. Packaged
Linux x64 0.9.3 with `MSYNC_JIT=off` prints `2\n8\n` at O0 and `1\n8\n`
at O1/O2/O3. At O2, source, `.mso` and `.mexe` agree with JIT off and auto.
Disassembly shows two adds at O0, but one at O1+: `MakeRange` reads the same
result slot twice. In the SDK 10.0.404 control rebuild, lowering and executing
after just scalar replacement (SROA) still prints `2\n8\n`; adding the
production MemorySSA EarlyCSE pass leaves one `fadd` and prints `1\n8\n`.
The first differing pass in this trace is therefore EarlyCSE, not tiering or
binding. This does not establish that later passes are sound on the same IR.

The readiness distinction must exist **before** scalar optimization. Either a
scalar operation has ready operands by construction, or it represents a
lifted evaluation that can produce a distinct pending result. Copying that
result, evaluating the operation again, and observing its payload are different
operations. A future-capable operation should not inherit the equivalence rules
of a raw `fadd` without a readiness proof. Optimized output with the existing
identity mismatch cannot serve as the oracle for XIR.

The HPIR producer work now makes this distinction in the LLVM graph before
the first optimizer pass. It follows declared submission producers, calls,
storage, captures and Range item flow once. Ready Number/Bool evaluations
remain LLVM primitives; a possibly pending evaluation is a typed call over
scheduler state, using LLVM's
[inaccessible-memory effects](https://llvm.org/docs/LangRef.html#function-attributes)
rather than claiming a read/write of guest fields. The normal scalar passes
need no msync-specific CSE exception. A Bool demand yields a ready condition
for the branch or any if-converted select; the original datum remains
separate so ordinary copying never loses future identity.

This is a producer-side semantic fix, not the HPIR future ABI. Exported
legacy Native instructions still retain their original wire representation.
Mutable storage can join alternatives, and unresolved capture pointers join
exposed storage until the actual origin is known. Do not copy this flow graph
into the binder or loader. The physical target must carry the selected
carrier through parameters, results, and locations without reconstructing it.

Consequences:

- A value loaded, received, or returned may be pending. `f64` cannot mean "ready" there.
- Machine code must either carry the pending case or leave the function mid-way with all
  live values reconstructed. "Every function compiles whole and never leaves" is not
  available unless generic code handles futures.
- Entry-only readiness works for `NativeSsaBody` because its body has no pending source.
  It stops working if admitted loads or runtime calls may return pending data.
- Distinguish propagation from demand at the use site: a comparison can produce
  a pending Bool, and a branch on it demands the payload. A type test can
  sometimes use the producer's type contract without demanding its payload.
  Waiting for task completion is also not permission to erase a value's
  observable pending identity.

Candidate execution strategies, none evaluated: (a) ready-specialized code plus a side exit with a frame
state per guard into an XIR interpreter; (b) two compiled versions with transfer at guards;
(c) generic code with future-aware runtime calls on slow paths. Each needs a cost and a
check that propagation, identity, effects and ownership survive. If an exit
resumes execution, it must not re-run effects already submitted. A per-operand
guard failure is the useful measure here, not a global pending count.

## Alternative: typed places instead of general addresses

Test a simpler execution model before adding another tier. msync can observe
reads, writes and shared locations; it does not expose a flat address space,
pointer arithmetic, or arbitrary byte loads and stores. Represent a binary
operation as `op src1, src2 -> dst`, where a source can be a constant or a
typed place (private slot, shared cell, or field of a Scope) and the destination
is a place. Calls, control transfers and explicit effects retain their normal
arity. A copy is an operation in the same model: copying a Scope value
preserves the object's identity, and copying a pending datum preserves the
pending identity. Neither operation creates an alias to the source slot.

For example, with a resolved field place `p`, `p = p + 1` can become one
`add p, 1 -> p` operation. `AddressOffset / Load / Binary / Store` need not
be four or more executable dispatches. A statically known layout gives a
fixed offset; a genuinely polymorphic Scope resolves the field at the
projection. A private slot never needs that resolver. Physical loads, stores
and register moves belong to the handler or code generator, not the language
instruction set. This resembles [Rust MIR places and assignments](https://rustc-dev-guide.rust-lang.org/mir/index.html)
at the representation level, and [Lua's register operands](https://lua.org/source/5.4/lopcodes.h.html)
at the dispatch level; neither supplies msync's pending-value semantics.

A place is not a snapshot. With `s::n = 9` and `bump()` changing `s::n` to
`10` and returning `2`, the expression `s::n + bump()` must save the left
operand before the call and return `11`, not `12`. A pre-mutation copy of
the field must also keep its old scalar, while a second name for the Scope
sees `10`. Packaged O2 Linux x64 0.9.3 returned `5, 11, 10` for this
journey through source, portable and prepared artifacts, with JIT off and
auto. Keep the copy at the original read point across a possible mutation,
failure or resource-ordering boundary, and keep the original order of
projection and effect evaluation. Reading a place captures its current datum;
it does not demand the pending payload or leave a future read of the cell in
its place.

The key invariant is disjoint reachability, not a better global alias proof.
A private slot has no cell handle other than its activation's own direct slot
operand. Capture conversion puts a location capture, or a mutable session
root, in a shared place explicitly; a by-value Scope parameter can still hold
a reference to a shared object without exposing its parameter slot. Field
projection can reach an object's fields, not that private slot. Physical
stack placement of a Scope does not grant access to adjacent temporaries.
An unknown field layout or cell target can affect the corresponding shared
access, not every private write in the function.

This directly targets the [prepared-image regression](../benchmarks/0.9.3-runtime-attribution.md#1-binding-inlines-a-call-that-disables-the-whole-functions-jit).
Binding runs a second proof over a pointer-like IR after inlining. Unknown
addresses become `FrameAlias`, and one such flag vetoes every private raw
write, including machine-call results. Removing only the veto recovered much
of the loss with binding inlining left enabled; that is evidence about the
cause, not a blanket license to ignore aliases in the old IR. Replace the
representation that lets a shared-field uncertainty contaminate private
slots. Do not repair it by adding a third alias solver.

A later experiment could isolate dispatch and eligibility by using existing
compiler-supplied slot and field facts, without an artifact format change,
loader analysis, or another interpreter. This does not settle the broader
representation question or commit to an implementation order.

## Preserve layout, readiness and effect facts across calls

The same erasure problem appears in three places. A typed place keeps its
ownership; a concrete record reference keeps its allocation layout; a value
keeps its payload type and readiness mode. None needs to go through an
unrestricted carrier by default.

### Concrete layouts are internal types

Give each concrete record layout an internal type identity, for example
`Ref<DetectorLayout>`. `scope { ... }` constructs the record; it is not a
reason to use an erased representation for it. Named declarations and separate
allocations still have their existing identity rules. The internal layout
identity is not a new source nominal type, and does not change what source
`Scope`, concepts, `is` or `as` accept. Layout descriptors already exist in
`NativeProgram.Layouts`; the substantive change is carrying the exact physical
contract through parameters, returns, captures and fields instead of keeping
it as a fact to recover from otherwise open pointers.

In signal-hysteresis, all three calls to `detector` return the same record
layout with `ingest` and `report`; only the captures and instance identities
vary. The audited six field operations are already `AddressOffset` with
compiled layout and field ordinals, yet the binding analysis sees their receiver
addresses as `Top`. Knowing the layout of a referent and knowing the set of its
possible instances are separate facts. An exact record type plus the disjoint
place kinds makes unknown instance identity irrelevant to its field offsets
and to private slots. Layout does not by itself prove the target of a mutable
function-valued field, or that two records of that layout do not alias.

At a real heterogeneous join, retain the actual alternative layouts. Packaged
O2 0.9.3 returned `42` and `18` through source, portable and prepared runs
when input selects between `scope { n: 41 }` and
`scope { a: 0, n: 17, label: "second" }`, followed by `value::n + 1`.
The portable artifact uses a layout test and selects a field address before
loading. A compiler can instead read at a constant offset in each predecessor
when effect order permits, then join the two Numbers. An actually open call,
such as `text::format_fields` with runtime-selected names, still needs a
descriptor. B+ pages currently erase recursive links and child elements to
`Scope`; a recursive `Page<K,V>` reference can retain the layout on paths
whose producers and updates establish it, without recursively expanding or
checking the whole heap. Erasure should describe an actual source boundary,
not the default component of every record ABI.

### Typed futures do not make guest calls asynchronous

The ordinary external process-call path waits for process completion today;
explicit `release` schedules work. Separately, synchronous scalar host
builtins can still run through `InvokeNativeDatumAsync` and
`InvokeNativeAsync`, including argument collection, behavior probes and name
resolution. A process exit-code projection already declares Number and
`DeferredScalar`, but its completion goes through `Task<NativeValue>` and
reattaches type metadata. This is a representation and dispatch boundary,
not a requirement to make ordinary calls async.

Use one typed implementation model. `Number + Number -> Number` is the
ordinary scalar operation. A lifted `Future<Number> + Number -> Future<Number>`
creates a typed dependency; comparing it creates `Future<Bool>`. An actual
branch demands the Bool. Only a slot or interface that can genuinely carry
both states needs a readiness discriminant. Reading, returning or passing a
typed future does not make a function a coroutine. Keep one work-ownership
and scheduling protocol for submission, cancellation, ordering and failures,
with typed payloads. Pending identity must survive even if the underlying
task has already completed; completion is not an implicit change to the
representation as seen by identity-sensitive operations.

Choose storage and call modes where their producers have the facts, and carry
them through ordinary optimization and binding. A fact proved once is a
contract of the resulting IR, not a fresh whole-function proof at execution.
Specialize executable bodies by the offsets, operations and calling
conventions they actually use; distinct semantic types with the same relevant
machine representation can share code. This is the useful trade-off in
[monomorphization](https://rustc-dev-guide.rust-lang.org/backend/monomorph.html);
[Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#limitations-of-native-aot-deployment)
also pre-generates generic struct instantiations, so a guest layout ID need
not become a distinct CLR generic argument or a copy of the scheduler.

## What is measured

Same O2 `.mso`, one CPU, 1M iterations (fib: 242,785 calls), median of five, startup
subtracted, oracles equal:

| Shape | msync ns | Path taken | Managed B/iter | C stand-in, -O0 / -O1 ns (not comparable, see below) |
| --- | ---: | --- | ---: | ---: |
| captured cell, leaf call | 81 | machine call | 0 | |
| Scope field, inline | 43 | machine code | 0 | |
| Scope field write, in a call | 997 | bridge per call | 304 | 11 / 8 |
| non-leaf call | 1,751 | bridge per call | 400 | 18 / 10 |
| fib | 1,519 | bridge | 337 | 7 / 5 |
| allocate + read two fields | 1,348 | two bridges | 306 | 19 / 12 |
| closure call, monomorphic | 55 | machine call | 0 | |
| closure call, one target and two environments | 4,347 | bridge per call | 401 | 32 / 7 |
| self tail call | 2,091 | two bridges | 349 | |

A later run of the same probe gave 1,109 / 1,867 / 1,611 / 1,381 / 4,346 ns for scope_call,
nonleaf, fib, alloc, closure_poly, so treat differences under about 10% as noise. Against
`MSYNC_JIT=off` (2,882 / 5,339 / 2,196 / 2,941 / 4,133 ns), the tier helps 1.4-2.9x on the
first four and **not at all** on the two-environment closure probe. The gap is
relative to the shapes that stay in machine code. The probe selects between
`mk(3)` and `mk(5)`; both use the same guest code target, and the emitted
loop selects callable components and recomposes a descriptor before its
fixed call. It does not measure two-target dispatch.

gdb sampling, 200 samples each, control rebuild, inclusive shares overlap:

| Shape | Managed call path | EnterActivation | EnterFrame | Largest other | Largest leaf |
| --- | ---: | ---: | ---: | --- | ---: |
| nonleaf | CallDirect 62% | 21% | 18% | ExecuteComposeFunction 31% | 5% |
| fib | RunDirectFunction 97%* | 24% | 22% | | 9% |
| scope_call | CallDirect 90% | 22% | 18% | | 10% |
| alloc | CallDirect 90% | 14% | 12% | ExecuteAllocate 20% | 5% |
| closure_poly | CallDirect 28% | 11% | | interpreted outside the tier (RunTier only 42%) | 6% |

\* recursive, so every sample contains it. Supported: for four shapes most time is inside the
managed call path, and it is flat. Not supported: one cause for all five. closure_poly is
mostly interpretation; nonleaf has a separate function-composition cost.

### Function and slot census (12 workloads)

`NativeSsaBody` accepts 4 of 202 candidate bodies (731 functions = 500 templates + 29 layout
variants + 202). Reported rejection reasons are first-failure only. Counted separately, with
overlap: captures 160, non-scalar parameter 150, non-scalar result 129, result the only
one of the three 13, scalar-only lowered body 7. The body count reads Native lowering.

| Property | Measured | Limit |
| --- | --- | --- |
| Slots with at most one definition | 17,682 / 18,158 | static |
| Address-taken slots | 400 | mostly `Location` captures |
| Dynamic reads of union slots | 9% | JIT off |
| Slot reads not definitely assigned | 0 of 84.2M | parameters and captures assumed assigned |
| Number/Bool reads not provably ready | 77% | approximation of the tier's rule |
| Entries added to the native pending index | 124 | managed pending table not counted |
| Scope reads with no layout set in the type | 82% | not a count of executed checks |

`Copy`/`Move` in B+ is 25% of dynamic operations: 8% target a multiply-defined slot (PHI-like)
and 17% a singly-defined one. Static singly-defined copies are mostly same-type or
to/from union. Out-of-SSA is therefore not the main source.

## Claims withdrawn

| Earlier claim | Why |
| --- | --- |
| `demand` blocks, as today; readiness is a producer type | false, see above |
| Each bridge *builds* an Activation | descriptors are reused once per call (3-4 allocated per run; fib 72) |
| `closure_poly` measures two code targets | portable disassembly has a single fixed target and two selected environments |
| The C model bounds the cost of the semantics | it omits readiness, layout dispatch on untyped Scope parameters, real allocator metadata and all collection; it bounds nothing in either direction |
| A 12 ns round trip proves crossing is not the cost | it proves only that an empty crossing is cheap |
| "Every slot may be anything" | slots are typed and definitely assigned |
| 24% of B+ copies come from out-of-SSA | at most a third of them look PHI-like |
| 121,699 fallbacks caused by pending values | the counter records that *some* value was pending globally at that moment |
| 129 rejected "because" of signature | first-failure ordering |
| No locals other than SSA values | ignores REPL sessions (`PreserveLocals`, session roots) and captured frame cells |
| About 40 operations; leaf/alloc/effect is enough | never enumerated against the opcode and builtin sets |
| A state byte replaces the pending index | never checked |
| Phase 0 gate on four workloads | hid regressions already measured: live-policy +11%, B+ +7% |
| ORC rejected for ~34 ms | from a 0.8.6 prototype; the same note measured 3.43 ms warm vs 50.68 ms, omitted here |

## Literature, corrected after reading the pages

| Source | What it supports | What it does not |
| --- | --- | --- |
| [TPDE](https://arxiv.org/abs/2505.22610) | 8-24x faster compile than LLVM -O0, run time within 9%, SPECint 2017 | garbage collection is unsupported in its LLVM back-end; stack maps not covered |
| [Maglev](https://v8.dev/blog/maglev) | CFG SSA for a middle tier, about 10x faster than TurboFan | relies on feedback **and deoptimization**, which the draft said we would not need |
| [Leaving Sea of Nodes](https://v8.dev/blog/leaving-the-sea-of-nodes) | CFG halved compile time | Turboshaft is the optimizing tier, not a fast tier |
| [Wasmtime stack maps](https://fitzgen.com/2024/09/10/new-stack-maps-for-wasmtime.html) | producer spills references | gives no numbers; notes redundant reloads |
| [Pulley](https://docs.wasmtime.dev/examples-pulley.html) | expect about 10x slower than native | |
| [CPython JIT](https://blog.python.org/2026/03/jit-on-track/) | 3.13/3.14 JIT was often slower than the interpreter | gives no cause; cannot justify rejecting copy-and-patch |
| [Deegen](https://arxiv.org/abs/2411.11469) | baseline JIT 33% slower than LuaJIT, **using copy-and-patch** | contradicts the draft's rejection; GC was off |

Pages were read through a fetch-and-summarize tool; the full papers were not.

## What still stands

- Bind-time inlining makes `.mexe` slower on five workloads (controlled rebuild; see note).
- In four call-heavy shapes the managed call path dominates and is flat.
- These are evidence for cheaper guest calls. They are **not** evidence that a new IR is
  required: calling non-leaf functions machine-to-machine on the existing IR was rejected
  in one line and never tried.

## Next steps, each able to kill the idea

1. **Pending contract.** Specify propagation, fresh pending identity and payload
   demand separately. Use the eight probes plus identity, cancellation and
   failure order as the oracle; use the contract, not current O2 output, for
   identity. Repair or explicitly account for the existing LLVM lowering/CSE
   mismatch before using optimized parity as an acceptance gate.
2. **Cheap rival.** Compare a fixed non-leaf machine-to-machine call on the
   existing IR against the managed bridge with the same semantics. Start with
   an acyclic scalar chain. Recursive fib is a separate experiment: the current
   SSA linker resolves a callee by compiling it and retaining its completed code
   page, so removing `CanBeCalled`'s call restriction alone cannot link a cycle.
   Report whether the bridge disappears and what performance gap remains before
   attributing any residual to the IR.
3. **Phase 0, honest gate.** Remove bind inlining only if all twelve workloads stay within
   noise, or the live-policy and B+ regressions are explained.
4. **XIR slice** only if 1 succeeds and 2 does not cover the shapes: one function kind,
   pending side exits included from the start, measured against 2 rather than a C model.

The earlier phase plan, line-count targets and 100/250 ns gates are suspended; those
thresholds were never derived.
