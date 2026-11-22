# Executable machine IR: evidence audit and open design (XIR)

2026-11-22. **Not ready to implement.** An audit of the first draft found one design
error that would hang existing programs, and several causal claims the evidence does not
support. This version separates what is measured from what is guessed.
Baseline: 0.9.3, Linux x64. Related: [runtime attribution](../benchmarks/0.9.3-runtime-attribution.md).
Probes: `artifacts/benchmarks/executable-ir-probe/` (local payload, not tracked by git).

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

The control shows what blocking does. Blocking at `+`, or on entry to machine code, would
hang the seven programs that complete today. `tests/NativeExecution/FutureIdentityChecks.cs`
("binary") already guards this, with identity observable through `unique`.

Consequences:

- A value loaded, received, or returned may be pending. `f64` cannot mean "ready" there.
- Machine code must either carry the pending case or leave the function mid-way with all
  live values reconstructed. "Every function compiles whole and never leaves" is not
  available unless generic code handles futures.
- Entry-only readiness works for `NativeSsaBody` because its body has no pending source.
  It stops working once loads and runtime calls are admitted.

Candidate designs, none evaluated: (a) ready-specialized code plus a side exit with a frame
state per guard into an XIR interpreter; (b) two compiled versions with transfer at guards;
(c) generic code with future-aware runtime calls on slow paths. Each needs a cost and a
proof that the seven programs and identity rules are preserved. This must be settled first.

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
| closure call, two targets | 4,347 | bridge per call | 401 | 32 / 7 |
| self tail call | 2,091 | two bridges | 349 | |

A later run of the same probe gave 1,109 / 1,867 / 1,611 / 1,381 / 4,346 ns for scope_call,
nonleaf, fib, alloc, closure_poly, so treat differences under about 10% as noise. Against
`MSYNC_JIT=off` (2,882 / 5,339 / 2,196 / 2,941 / 4,133 ns), the tier helps 1.4-2.9x on the
first four and **not at all** on two-target closures. The gap is relative to the shapes
that stay in machine code.

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

1. **Pending contract.** Write the rules for which operations block, with the eight probe
   programs plus identity, cancellation and failure-order cases as an oracle. Choose (a), (b)
   or (c) on paper. If none keeps the oracle without exits everywhere, stop.
2. **Cheap rival.** Allow calls from callable SSA bodies (`CanBeCalled` rejects any body
   with a call) and run fib. If fib falls below 100 ns, calls, not the IR, were the limit there.
3. **Phase 0, honest gate.** Remove bind inlining only if all twelve workloads stay within
   noise, or the live-policy and B+ regressions are explained.
4. **XIR slice** only if 1 succeeds and 2 does not cover the shapes: one function kind,
   pending side exits included from the start, measured against 2 rather than a C model.

The earlier phase plan, line-count targets and 100/250 ns gates are suspended; those
thresholds were never derived.
