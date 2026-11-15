# LLVM Dead Store Elimination source component

Reference: LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`,
`llvm/lib/Transforms/Scalar/DeadStoreElimination.cpp` (2898 lines) and
`llvm/lib/Transforms/Scalar/ScalarOptions.td`. Source and license are retained
under `vendor/llvm-reference/llvm/`. The coherent component is published under
`src/Msync.Optimization/Llvm/LlvmDeadStoreElimination*.cs`; integration compilation
and execution remain root-owned. No MSBuild invocation is run by
this component worker.

This is the modern MemorySSA-based DSE controller and its represented-path
transformations, not a neighboring-store scan or a new pure-only DSE algorithm.
Range objects are ordinary runtime objects/calls. The only project extension
instructions remain HeapAlloc, FileIn and FileOut.

## Controller and state ownership

The caller supplies the actual AAResults, MemorySSA, dominator and
postdominator trees, target-library description and GenericCycleInfo result.
The source controller order is preserved:

1. Collect postorder MemoryDefs and throwing blocks.
2. For each candidate, walk dominating memory definitions and their uses,
   traverse MemoryPHIs and eliminate dead/partially overwritten definitions.
3. Process accumulated partial-overwrite intervals.
4. Remove redundant writes of already-stored values.
5. Iterate end-of-function elimination when deleting pointer stores invalidates
   the object-capture cache.
6. Remove stores implied by dominating load/compare conditions.
7. Erase delayed dead computations.

One EarliestEscapeAnalysis and one BatchAAResults are retained for the entire
run, as in the source. Deleted memory accesses are removed through the real
MemorySSAUpdater; removal notifications invalidate earliest-capture entries.
DT/PDT/CycleInfo remain valid because DSE does not change the CFG. MemorySSA is
not rebuilt as a fallback. Delayed erasure retains the source cache-lifetime
rule, even though managed object references do not reuse native pointer values.

## Source correspondence

| Managed source | Upstream mechanisms |
| --- | --- |
| `LlvmDeadStoreElimination.cs` | ScalarOptions defaults, DSEState construction/collection and eliminateDeadStores controller |
| `.Facts.cs` | isInvisibleToCallerAfterRet/onUnwind, getLocForWrite, isRemovable, isReadClobber, isGuaranteedLoopIndependent/Invariant, canSkipDef, mayThrowBetween, isDSEBarrier |
| `.Walk.cs` | getDomMemoryDef: weighted upwards scan, source scan/walk/partial limits, defining-access optimization, use exploration, postdominance and bounded reverse-exit CFG coverage |
| `.Eliminate.cs` | eliminateDeadDefs MemoryPHI worklist, overwrite/partial/merge decisions, storeIsNoop and load/definition/PHI checks |
| `.Overwrite.cs` | isOverwrite constant-offset/AA/object-size cases, isPartialOverwrite interval accumulation and containment |
| `.Merge.cs` | memoryIsNotModifiedBetween with actual PHITransAddr cloning/translation; source constant integer partial-store merge including endian and atomic-order requirements |
| `.Redundant.cs` | eliminateDeadWritesAtEndOfFunction, eliminateRedundantStoresOfExistingValues, eliminateRedundantStoresViaDominatingConditions |
| `.Delete.cs` | deleteDeadInstruction: MSSA updates, capture and overlap invalidation, recursive trivial-dead operands, immediate/deferred erase policy |

The source default options are unchanged: dominator-condition depth 1024,
definition collection limit 5000, same-block/other-block walk costs 1/5,
partial-candidate budget 5, exit-path limit 50, access scan limit 150 and upward
walk limit 90. MemorySSA optimization, initializes-attribute consideration,
partial-overwrite tracking and constant partial-store merging default on.
Their absence of additional source facts is not encoded by disabling an option.

Managed containers adapt the source SmallVector/SmallPtrSet/SetVector and
MapVector lifetimes. Ordered block/instruction lists retain MapVector order;
intervals use an ordered array-backed map with source lower_bound and interval
merge operations. This is a container adapter, not a different overlap solver.
KnownBits/ConstantRange do not replace memory-def/use traversal. Pointer offsets
use the shared source Value::stripAndAccumulateConstantOffsets helper, and
non-inserting PHI translation uses the existing PHITransAddr source component.

## Actual representation boundary

Load/store, arbitrary represented scalar integer store widths, volatile and
atomic-order attributes, scalar constants, PHIs/selects/GEPs, real memory
accesses and all current project operations follow their source paths. Unknown
calls remain actual AA/read/throw barriers, not guessed pure functions. The
four registered standard integer min/max intrinsic identities use their shared
actual Core/AA contracts.

The current target library explicitly uses LLVM's all-C-library-functions-
disabled configuration, because the Native runtime ABI is not a C library ABI.
No malloc/free/memset/check-function facts are guessed from source names.
Memory-transfer/set/masked/lifetime/coroutine intrinsic identities,
initializes/dead-on-return/byval argument attributes, debug/AA metadata and the
broader allocation-kind attributes are not represented. Their source fact
queries therefore have no match. In particular:

- Ordinary StoreInst is rejected by both source memory-shortening predicates;
  partial interval tracking and constant-store merging are still performed.
- No memcpy/memset instruction can take the intrinsic shortening or calloc-fold
  routes yet; these routes are not exposed as a pretend complete intrinsic API.
- No initializes attribute means the source location list has only the actual
  single write location, not fabricated initialized argument ranges.
- Source post-return/unwind logic consumes actual capture facts. HeapAlloc's
  explicit successful-result fresh/uninitialized carrier uses the separately
  established project allocation adapter, never a call-name heuristic.

No runtime admission, symbolic heap, proof prerequisite, new backend, pass
manager, instruction family or optimization mode is selected here.

## Observability and integration

Detailed observations separate collection, definition elimination, partial
interval processing, redundant writes, end-of-function iterations,
dominating-condition elimination and delayed erasure. Work counters record
actual definitions/uses visited, AA read-clobber calls, source budget exits,
throw/cycle/read barriers, reverse-CFG path checks, recorded/completed intervals,
constant merges and concrete deletion categories. Rewrite events retain the
function, block, opcode and producing source span. Recording-disabled runs do
not allocate a telemetry map or rewrite-attribute dictionaries.

Root-owned focused integration should exercise real source programs with
mutable fields/locals, overwritten writes across blocks, intervening reads and
throwing/file operations, returned/captured objects, end-of-function-local
writes and loop-carried accesses. Raw integer partial-overlap kernels additionally
check merged bits and byte order. These are pending execution checks, not a
claim of measured performance or completed end-to-end validation.

## Entry and focused source journey

```csharp
LlvmDeadStoreElimination.Run(function, alias, memorySsa, dominators,
    postDominators, targetLibraryInfo, cycles, options: null, trace: trace);
```

`postDominators` is the actual postdominator tree (`IsPostDominator == true`),
not the forward tree. AA, MemorySSA, DT and CycleInfo describe the same current
function graph; the caller retains their ownership. The pass updates MemorySSA
through its real updater and leaves the CFG intact. Default compiler/pipeline
integration is owned by the root, not selected by this component.

A focused source-facing candidate is a mutable Scope field initialized/stored
several times before its next read: the final read and observable result must
remain unchanged while the overwritten stores disappear. Add an intervening
field read or external call to distinguish an actually dead write from a
required one; return/capture the Scope to exercise after-return visibility.
An uncaptured local object that is never read exercises the separate end-of-
function route rather than requiring a later overwriting store.
