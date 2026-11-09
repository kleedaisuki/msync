# Indexed-memory availability

Run `dotnet run --project tests/IndexedMemoryOptimization -c Release`.

The 206 focused contracts cover O0/O2/O3 and source-equivalent typed bytecode,
relocatable roundtrips and executable roundtrips. They distinguish same versus
different indices, length versus contents, Scope identity, raw captured-cell
rebinding, field-cell aliases, CFG diamonds, repeated loop iterations, callbacks,
barriers, resizing, and the first selected failure's original source span.
The public source-family path is additionally exercised by
`tests/NativeCallSpecialization` and the real CLI journey
`tests/Msync.EndToEnd/static_optimization_journey.py`.

## Mechanism and boundaries

At O2/O3, a successful buffer get is available until a possibly overlapping
write. Exact nonnegative integral literal indices identify disjoint locations;
an unknown index may overlap every location. A value-only set or swap leaves
length available. Resizing invalidates both contents and length. Unknown calls,
enumeration callbacks and directional barriers terminate availability. Different
receiver value numbers are not assumed to prove different buffers: writes
invalidate every possibly overlapping read, including accesses through aliases.

At a join, the same producer must be available on every predecessor. Backedges
start fresh availability, so each dynamic iteration executes its own first read.
The pass does not speculate, hoist a read out of a loop or introduce a Phi
instruction. Cached values preserve the exact Scope/Function/value identity.
Replacement keeps argument evaluation and only removes the repeated native read;
the first operation and its potentially failing bounds/type check remain.

This is a small indexed-memory counterpart of LLVM's alias-aware
[MemorySSA clobber queries](https://llvm.org/docs/MemorySSA.html), not a new
general MemorySSA implementation. No store is deleted or forwarded speculatively:
store elimination also needs exception and must-execute proofs, as illustrated
by LLVM's [tagged DSE implementation](https://github.com/llvm/llvm-project/blob/llvmorg-23.1.5/llvm/lib/Transforms/Scalar/DeadStoreElimination.cpp).

The existing function-feature scan admits only repeated get or size sites.
Distinct literal-index-only bodies skip CFG analysis. Ordinary non-Buffer code
does not allocate pass state. Persistent immutable availability maps share branch
state rather than copying an ever-growing table at every edge. Analysis is one
forward traversal, with conservative resets on backedges, not a fixed-point loop
or a backwards scan for each read.

## Component observations

The test program includes a public production-API component probe:

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project tests/IndexedMemoryOptimization -c Release -- --measure
```

The measured loops consume 1024 iterations. The repeated case performs sixteen
same-index reads per iteration; the length case reads size, sets an existing
element, then reads size again. Controls use distinct indices and no Buffer.
Returned values are checked before timing. These are deliberately small causal
component workloads, not a claim that every tree traversal repeats sixteen loads.

The paired experiment froze the same Core and Runtime source graph and changed
only the indexed-pass hook and its feature admission. Setup: .NET SDK 10.0.403,
runtime 10.0.14, tiered compilation disabled equally, ABBA process order,
24 optimizer warmups and five VM warmups per process, nine observations of eight
executions. Before and after each have two processes. Allocations are measured
separately from timing.

| Prepared VM | Native read sites before → after | p50 ms before → after | Allocated bytes before → after |
| --- | ---: | ---: | ---: |
| Repeated element | 16 → 1 | 18.3638 → 2.8472 | 3,590,224 → 272,472 |
| Size around value-only set | 2 → 1 | 4.0681 → 2.8655 | 755,792 → 518,232 |
| Distinct-index control | 2 → 2 | 2.6384 → 2.8297 | 493,648 → 493,648 |
| No-Buffer control | 0 → 0 | 2.0393 → 1.9879 | 50,496 → 50,496 |

This spends compile work where it can remove repeated runtime work. The repeated
case's optimizer p50 increased 0.3453 → 0.5565 ms and allocation increased
131,560 → 235,744 bytes; the length case increased 0.1912 → 0.2901 ms and
72,632 → 129,312 bytes. No-Buffer optimizer allocation stayed exactly 102,496
bytes. A first candidate unnecessarily analyzed the distinct-index control;
the final cheap literal-index screen reduced its optimizer allocation from
79,856 to 68,392 bytes, versus 68,216 before. This final admission-only change
kept all optimized artifact hashes unchanged and passed the 206 contracts.

Concurrent builds widen wall-time samples, particularly the unchanged controls.
The new pass is not presented as a universal startup or Native AOT speedup.
The exact native-site counts and allocations explain the measured mechanism.
The final control rerun has no additional runtime allocation and the no-Buffer
path has no additional optimizer allocation.

Frozen inputs, DLL hashes and raw pairs are under
`.temp/gcc-llvm-optimization/paired-indexed/{manifest,paired}.json`; the final
admission-only observation is `.temp/gcc-llvm-optimization/indexed-gated-final.json`.
The fixtures and procedure above remain reproducible if disposable records are
cleaned. The ordinary controls' optimized artifacts are byte-identical between
images; the beneficial cases change bytecode as intended.
