# Indexed-memory availability

Run `dotnet run --project tests/IndexedMemoryOptimization -c Release`.

The focused contracts cover O0/O2/O3 and source-equivalent typed bytecode,
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

For a read after a join, the pass projects the receiver and index into each
predecessor. Each projected read must already be available there, after that
path's clobbers. A common producer still needs no merge. If the producers
differ, reuse a local that already holds their selected result; otherwise put
copies on the exact incoming edges and load one private result slot. A shared
producer slot alone would be wrong when two producers execute on a path but a
later condition selects the earlier one. Conditional edge splitting does not
execute the copy on the other arm.

An unprocessed incoming edge (including a loop backedge) starts fresh read
availability. The pass does not speculate, hoist a read out of a loop or
introduce a serialized Phi instruction. Cached values preserve the exact
Scope/Function/value identity. Replacement keeps argument evaluation and
removes only the redundant native read; each selected path still executes its
first operation and potentially failing bounds/type check at its original
source span.

This is a small indexed-memory counterpart of LLVM's alias-aware
[MemorySSA clobber queries](https://llvm.org/docs/MemorySSA.html) and
[GVN phi translation](https://blog.llvm.org/2009/12/advanced-topics-in-redundant-load.html),
not a general MemorySSA or partial redundancy elimination (PRE) implementation.
Unlike load PRE, a missing edge witness is never filled with a new native read.
No store is deleted or forwarded speculatively: store elimination also needs
exception and must-execute proofs, as illustrated by LLVM's
[tagged DSE implementation](https://github.com/llvm/llvm-project/blob/llvmorg-23.1.5/llvm/lib/Transforms/Scalar/DeadStoreElimination.cpp).

The existing function-feature scan admits only repeated get or size sites.
Distinct literal-index-only bodies skip CFG analysis. Ordinary non-Buffer code
does not allocate pass state. Persistent immutable availability maps share branch
state rather than copying an ever-growing table at every edge. Analysis is one
reverse-postorder traversal of the shared CFG, with conservative resets on
unprocessed incoming edges. Join queries translate value-number expressions on
demand and memoize both hits and misses. There is no fixed-point speculation.

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

## Selected-read join journey and SQLite profile

`python tests/IndexedMemoryOptimization/journey.py /absolute/path/to/msync`
checks both input-dependent predecessor choices, an alias-write control, and a
131,072-iteration selected-child loop through source, portable and executable
routes at O0/O2/O3. O2/O3 remove the redundant read in the selected paths while
retaining all three reads in the alias-write case. The typed-bytecode suite
also covers a taken critical edge with both earlier reads executed, translated
arithmetic folded on the predecessor, correlated receiver selection, mixed
cell/plain alias behavior, and distinct first-failure spans on the two paths.

The [selected loop](selected-loop.msync) reads both children, chooses the index,
then reads the selected element. Run the existing
[`profile_compare.py`](../Performance/profile_compare.py) harness with the
same profiled Native CLI as both VM runners, the pre-change and post-change
O2 compilers as `--baseline-compiler` and `--candidate-compiler`, and the loop
as `--source`. The expected stdout is `720896\n`. The artifacts must differ;
no profiling overhead belongs in the elapsed samples.

In eight AB/BA pairs after two warmup pairs on Linux x86-64 logical CPU 6,
the same Native VM (SHA-256
`3a605f6253525ef5959caad242073a53edc655737a0f2dcd16a14503a8139ea8`)
ran the two pre-bound images in 391.33 → 371.82 ms median, with −20.04 ms
paired median (six of eight faster). The compiler candidate isolated this
indexed pass and the shared CFG dependency; it did not include concurrent
loop or interprocedural transformations. All results matched. Separate
SQLite profiles show exactly 393,216 → 262,144 `storage.buffer_get` calls
and 589,824 → 524,288 numeric boxes. Dispatch count grows from 6,029,391
to 6,619,215 because value and edge moves replace a more expensive native
operation; this is why an opcode count alone is not a profitability model.
The same pair of artifacts on the old release VM also showed a
−22.74 ms paired median (seven of eight faster).

Raw paired timings and SQLite receipts are in
`.temp/indexed-join/{paired-profile,paired-old-vm}/`. The exact call count
and checked result are stronger causal evidence than the noisy wall-time
samples. The stock Heap currently has a source-level finite-comparator
shortcut; the fixture demonstrates that the middle end can remove a selected
reread without asking source authors to implement that shortcut. Calls still
kill availability without a proof about their actual runtime target and
effects.
