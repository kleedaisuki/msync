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
