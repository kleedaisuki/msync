# Dependency-local optimizer graph reuse

Run the focused contract program:

```sh
dotnet run --project tests/OptimizationDependencies -c Release
```

The 44 checks cover unchanged and changed direct callees, transitive effect
summaries, dispatch targets, capture-layout/body replacement, an unrelated added
function, and constant-pool growth. Selected effectful calls remain selected;
unselected calls can disappear. Actual Scope values and results are consumed
through O0/O2/O3, relocatable roundtrips and executable roundtrips.

The cache is private to one `Optimizer.Optimize` invocation. Function identities
and constant indexes remain stable until final module compaction. A cached graph
tracks the immutable bodies and effect summaries of its direct and dispatch
callees. Existing effect summaries already contain transitive effects; a change
in an unrelated function therefore does not invalidate an independent body.
Unresolved calls retain conservative behavior rather than acquire facts from a
module-global summary. Ordinary preparation caching remains unchanged.

This adopts the dependency-aware invalidation principle described by LLVM's
[new pass manager](https://llvm.org/docs/NewPassManager.html#implementing-analysis-invalidation),
not LLVM's pass-manager implementation or a new general analysis framework.
Body identity also covers frame/capture layout. This change does not add a
callee-body constant evaluator or a new user-bytecode transformation.

## Component observation

The checked-in `Library` fixture can measure the public production optimizer:

```sh
DOTNET_TieredCompilation=0 dotnet run \
  --project tests/OptimizationDependencies -c Release -- --measure
```

A matched experiment froze the same Core and Runtime source graph into two
isolated Release images, changing only the graph-cache implementation. An
experimental counter at the entry of `GraphOptimization.Optimize` counted real
graph invocations; it is not present in product code. The input exports 96
independent native-read functions and a 16-level direct-call chain whose dead
read disappears during optimization. The control exports only the independent
functions. Both results are actual Scope values, not discarded pure arithmetic.

Setup: .NET SDK 10.0.403, runtime 10.0.14, tiered compilation disabled equally,
24 warmups per process, nine observations of eight optimizer executions, ABBA
process order. Before and after each have two processes. Times below are pooled
p50 milliseconds per optimizer execution, not Native AOT or CLI startup results.

| Input | Graph calls before → after | Allocation bytes before → after | p50 ms before → after |
| --- | ---: | ---: | ---: |
| One changing call domain | 227 → 130 | 1,939,544 → 1,557,904 | 4.2068 → 3.0959 |
| Unchanged native-only control | 97 → 97 | 1,088,600 → 1,088,592 | 1.8711 → 1.7307 |

The changed-domain allocation reduction is 19.7%. The control has no additional
allocation. The implementation uses indexed instruction traversal: the first
prototype's interface enumerator added 32 bytes per dependency scan even when
there was no call, and was removed before acceptance. Concurrent builds widen
timing samples, so the stable call counts and allocation difference explain the
mechanism more reliably than a universal speedup claim.

Optimized artifact bytes match exactly between images. SHA-256:

- changing-domain artifact:
  `9819c4e9bebe5dde9777105b38ed609cc45231f14835755b66a349582e118f37`;
- unchanged control artifact:
  `d5c27b420411fbf3a81a93ab6219d8b924f7346beb6e67967f6c203f12c4144f`.

The measured before Core DLL is
`9a2e54a5ef707bb2105b40c079bf14f58b857281aca6c85b6ca81ac8629b1c1d`;
the measured after Core DLL is
`6df96ac89dad340b1c7d171fb3a296ac3f20350b96a1342ff2d599500d503c90`.
Frozen source hashes and raw observations are disposable records under
`.temp/optimization-dependencies/{manifest,paired}.json`. The fixture, setup and
measurements above remain useful if those temporary records are cleaned.
