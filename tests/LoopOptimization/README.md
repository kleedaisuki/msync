# Loop optimization contracts

Run `dotnet run --project tests/LoopOptimization` from the repository root.
The standalone runner invokes the loop pass directly before any unrelated graph
optimization. Source and manually assembled baseline modules use optimization
level zero, including runtime binding, so the baseline cannot accidentally run
the same pass being checked.

The actual interpreter oracle covers empty/single/multiple/nested finite loops,
conditional bodies, early returns, captured per-iteration declarations, real
output file bytes, IEEE-754 accumulation, serialized relocatable and executable
artifacts, and a large literal table retained by the code-size cost model.
A typed but runtime-supplied Range checks LICM structurally and behaviorally:
the invariant Number operation moves before the iterator header while its
per-iteration destination declaration remains inside. A selected missing-field
error inside a known empty loop is never speculated.

## Pass contract

`LoopOptimization` recognizes a single-entry iterator natural loop with an
explicit lexical preheader and latch. A direct eager `MakeRange` has a known
finite item count independently of how its operands were calculated. Its
operands are retained in their original order and evaluated exactly once;
fresh temporary slots store those values before expansion. `LoadStorage`
preserves raw capture-cell identity rather than implicitly dereferencing it.
Each copied body retains its own original `InitializeLocal` instructions, so
escaping closures retain the correct per-iteration locations. Internal branches,
early exits and nested iterator targets are relocated, never source-replayed.
Expansion uses a level-dependent code-growth cost model. Deferred/lazy sources
are not enumerated at compilation.

LICM proves stable uncaptured operand locations and total scalar operations from
fixed parameter/storage types. It does not move filesystem observations,
processes, callbacks, member reads, or potentially failing operations out of a
possibly empty loop. It introduces a preheader value but preserves the original
body store, because hoisting a declaration would change captured location
freshness. Floating-point arithmetic is neither reassociated nor replaced by
integer identities.

Legacy scheduling modules retain their original iterator/control boundaries;
the pass applies only to modules with directional scheduling semantics.

## Component measurement

`dotnet run -c Release --project tests/LoopOptimization -- --performance` measures
prepared interpreter execution for summing the eager literal range 1 through 16.
It compares level zero, loop expansion alone, and the integrated level-three
pipeline. Binding happens before timing. After 32 warmups per variant, nine
rotated batches execute each variant 128 times, verify the result 136, and record
per-execution time and `GC.GetTotalAllocatedBytes` traffic. No timing threshold
is an acceptance gate.

The November 9 Linux x86-64 managed Release probe (.NET SDK 10.0.403, runtime
10.0.14, QEMU Virtual CPU, Linux 6.12.113) yielded:

| Variant | Static instructions | Median execution | Allocated bytes/run |
| --- | ---: | ---: | ---: |
| Level zero | 40 | 0.069824 ms | 13,344 |
| Loop expansion alone | 202 | 0.044487 ms | 11,992 |
| Integrated level three | 2 | 0.005710 ms | 1,552 |

Expansion removes iterator machinery even before propagation. Subsequent graph
passes fold this finite pure computation to its result. This experiment isolates
managed interpreter dispatch/allocation; native startup and I/O throughput have
their own end-to-end measurements.

## Scalar natural loops

The O2/O3 late portable pass consumes the shared control-flow graph. A
dominating backedge, single latch and unique preheader establish the
[natural loop](https://llvm.org/docs/LoopTerminology.html). Loop analysis
records graph membership independent of bytecode layout; this first
transform handles two-block loops with one ordered comparison and an
exact positive integral induction recurrence. The private counter has
one entering seed and one backedge update. The scalar body may read the
counter but may not call code, write the counter or bound, or expose a
carried slot. A mutable cell is not an invariant bound.

The shared sparse definition-use analysis identifies the exact counter seed
and latch update reaching the header, and follows the selected bound's
operand definitions through private slot copies. That proof is independent
of bytecode adjacency: a dominating bound writer or a cast operand may
cross a block boundary. It is not an alias proof. A frame-owned slot
holding a raw Cell remains aliased after `StoreLocal` writes a Number into
it; only a known plain prior value or a detaching `InitializeLocal` of a
plain value licenses the storage proof. The direct tests cover a cross-block
cast with equal serialized execution, two distinct reaching bound writers
that remain unresolved, and a raw Cell initialized before a numeric seed
store that must not be treated as private. In the isolated Release migration,
the runner passed 429 contracts and both recurrence fixtures' O2 relocatable
artifacts were byte-identical to the previous integrated compiler's output.
Receipts: `.temp/defuse-loops/snapshot-tests.log` and
`.temp/defuse-loops/compiler-comparison.log`.

The fast region uses the last counter of a four-iteration chunk to prove
that all four iterations enter. A finite bound within the exact integer
domain keeps the grouped counter update bit-exact. If the body reads
the counter, each copy retains the original update, so every read sees
the original binary64 value. A balanced counter update can execute above
the carried accumulator on the operand stack. Fractional bounds are
handled by the original comparison (there is no conversion to an
integer trip count), and a failed guard uses the original loop. Known
counts up to four are computed by applying the original comparison and
induction update, and fully expanded. NaN or infinity can retain the
original divergence. A private single-store recurrence stays on the
operand stack between copies, so arithmetic runs in the same order
without an intermediate box, store or reload. No format-5 opcode,
contextual binding policy, or VM rewrite is needed. This follows the
separation between [loop structure and iteration facts in GCC](https://gcc.gnu.org/onlinedocs/gccint/Loop-Analysis-and-Representation.html),
not GCC's integer overflow assumptions.

The source-driven differential tests check O2/O3 and both serialized
artifact stages against O0, including fractional and near-precision-limit
bounds, positive non-unit steps, negative zero, a captured location,
small full expansion, and cancellation of a NaN-bound divergent loop.
The literal iterator test also covers the current HIR iterator
initialization and its explicit owned-iterator close on early return.

[`recurrence.msync`](recurrence.msync) is the runtime-bound CLI fixture.
Supply `1048576\n` on stdin; stdout is `131078.87218188652\n`. An
independent Native AOT measurement compiled the same recurrence returning
its result directly (without the final stdout write) at O2 with release
HEAD and with only the shared CFG and scalar transform added, then
preloaded both artifacts in the same unmodified runtime host. Nine
forward/reverse rounds on Linux x86-64 CPU 10 checked the
bit pattern `0x41000036fa3a7f36` on every sample:

| Variant | Median execution | Median allocated bytes |
| --- | ---: | ---: |
| Release O2 | 696.12 ms | 50,334,072 |
| Automatic scalar transformation | 335.48 ms | 12,585,336 |
| Hand-written four-way nested recurrence | 349.00 ms | 12,585,336 |

The ordinary and transformed hot paths use respectively 60 and 29
bytecodes per four updates. This measures the bytecode transformation,
not a change to native dispatch. The source and both artifact routes
also printed the same checked value at N=101. Raw samples and
disassemblies are in `.temp/natural-loop-check/`.

[`counter-recurrence.msync`](counter-recurrence.msync) uses the counter
in the arithmetic. The instruction profile oracle runs 4,099 iterations
with O1 and O2: both perform 4,099 remainder operations and counter
stores, while backedges and accumulator stores drop from 4,099 to
1,027 (1,024 four-iteration chunks plus a three-iteration tail).

The O2 Native CLI comparison held the runtime binary fixed, compiled
both artifacts with the same compiler sources except for counter-read
support, and checked stdout `5243155.887799761\n` for 1,048,579
iterations. Nine alternating fresh-process pairs with profiling off on
Linux x86-64 CPU 10 gave medians of 905.70 → 516.12 ms (paired median
difference −343.16 ms, nine faster pairs). Separate SQLite instruction
profiles showed 17,825,870 → 12,845,144 dispatches, 1,048,579 → 262,147
backedges, and 2,097,158 → 1,310,726 Number boxes. Both artifacts
performed exactly 1,048,579 multiplies and remainders. The timing is a
controlled fixture measurement, not a general estimate; the dynamic
counts account for the dispatch and box changes. Reproduce with
[`profile_compare.py`](../Performance/profile_compare.py), supplying the
same runtime on both sides and different O2 compiler paths. Raw profiles
and timings are in `.temp/counter-loop-matched/`.
