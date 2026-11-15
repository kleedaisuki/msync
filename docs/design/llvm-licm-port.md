# LLVM LICM source component

Reference: LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`,
`llvm/lib/Transforms/Scalar/LICM.cpp` (2878 lines),
`Transforms/Utils/LoopUtils.h`, `Analysis/LoopIterator.h`, and
`Transforms/Scalar/ScalarOptions.td`. This is a source translation of the real
memory-aware LICM component, not an arithmetic-only replacement. The coherent
source cohort is published under `src/Msync.Optimization/Llvm/`;
its staging copies remain disposable under `.temp/`. Integration builds and
real-process kernel validation are root-owned. No backend or
linked LLVM dependency is selected by this work.

## Source controller and ownership

The source `LoopInvariantCodeMotion::runOnLoop` order is:

1. Read current loop state and initialize actual MemorySSA work caps.
2. Sink through exit LCSSA PHIs when exits are dedicated (reverse dominator-tree
   region order; exclude already-processed subloops).
3. Hoist into the preheader (LoopBlocksRPO definition-before-use order).
4. Repeatedly collect source promotion candidates and promote accesses, because
   one promotion can make another pointer invariant.
5. Reform LCSSA recursively after promotion. Preserve actual MSSA/DT/LI state.

The controller, source motion/sink/hoist/memory-legality and promotion
orchestration belong to the loop-framework owner. Source arithmetic reassociation
(`hoistArithmetics` and helpers) is separately owned and called through
`LlvmLicmArithmetic.HoistArithmetics`; it is not a substitute for the memory paths.
Optional ScalarEvolution follows the source null branch. No source TTI/TLI or
alias prerequisite is replaced with a guessed cost, fake empty query or new
heap solver.

## Required concrete dependencies

| Source consumer | Dependency and needed behavior |
| --- | --- |
| runOnLoop / sinkRegion / hoistRegion | Existing LoopSimplify, mutable DT/LI, LCSSA, ICFLoopSafetyInfo; source collectChildrenInLoop and LoopBlocksRPO traversal |
| canHoistLoad / pointerInvalidatedByLoop | Real AA/BatchAA, MemorySSA graph, skip-self clobber walker, local memory dominance and update hooks |
| move/erase/exit cloning | Actual MSSAUpdater move/create/insert/remove, ICF insertion/removal invalidation, source LCSSA PHIs and SplitBlockPredecessors updates |
| sink foldability | Source generic TargetTransformInfo instruction cost (especially free address-mode GEP behavior), not an invented instruction price list |
| collectPromotionCandidates | AliasSetTracker must-alias/Mod/Ref/forwarding sets, ordered memory locations and aliasesUnknownInst |
| promoteLoopAccessesToScalars | LoadAndStorePromoter + SSAUpdater, predecessor cache, source dereferenceable/alignment queries, capture-before and object visibility/writability/thread-local helpers |
| hoistArithmetics | APInt/APFloat, actual known-bit/overflow helpers, source builders and instruction erase maintenance |

Modern LICM requires MemorySSA; it does not select a legacy non-MSSA fallback.
Source caps are optimization cap 100 and promotion-access cap 250. Source scalar
arithmetic limits are FP reassociations 5, integer reassociations 5 and traversed
uses 8. These constants come from ScalarOptions.td, not new tuning policy.

## Representation boundaries

The current scalar IR has no LLVM coroutine/noalias/invariant intrinsic identity,
branch/profile/debug metadata, or general call attributes. Those states must not
be fabricated from source-language Native builtin names. First-class HeapAlloc,
FileIn and FileOut retain the explicit project extension contract; construction
of a fresh object is not license to hoist and reuse its identity. Source-defined
memory transformations consume the shared actual graph, walker and AA results.
No runtime guard maze, arbitrary readiness restriction or custom safety mode is
introduced. Component coverage and any unresolved dependency are recorded here
before publication; none of this document claims completed LICM until the actual
paths and their integration are delivered.

## Published source correspondence

| Managed files | Pinned source coverage |
| --- | --- |
| `LlvmLicm.cs`, `.Options.cs` | LICM options/caps and `runOnLoop` ordering; dedicated-exit sink, preheader hoist, iterative promotion and recursive LCSSA repair |
| `.Traversal.cs` | `LoopUtils.cpp::collectChildrenInLoop`, `appendLoopsToWorklist`, `LoopIterator.h::LoopBlocksDFS/Traversal/RPO` |
| `.Region.cs` | `sinkRegion`, `sinkRegionForLoopNest`, `hoistRegion`, `isSafeToExecuteUnconditionally` and source allow-reciprocal rewrite |
| `.Memory.cs` | represented `isHoistableAndSinkableInst`, `canHoistLoad`, `canSinkOrHoistInst`, skip-self walker/cap handling, `noConflictingReadWrites`, `pointerInvalidatedByLoop/Block` |
| `.Motion.cs`, `.Sink.cs` | source motion/erasure hooks, generic-TTI GEP foldability, LCSSA exit cloning, unreachable-use replacement, source exit splitting, per-exit clone reuse and sink replacement |
| `.Promotion.Candidates.cs` | actual MemorySSA access enumeration, source potential-access rules, AliasSetTracker population, must-alias Mod-set selection, outside-access Mod/Ref filtering and ordered pointer candidates |
| `.Promotion.cs`, `.Promotion.Promoter.cs` | `promoteLoopAccessesToScalars` and derived `LoopPromoter`: dereferenceability/alignment, must-execute stores/loads, unwind/object/thread-local rules, atomicity, load-only/store promotion, real SSAUpdater, exit LCSSA PHIs and MSSA insert/remove hooks |
| `LlvmLicmArithmetic*.cs` (separate owner) | source arithmetic minmax/GEP/add-sub/reassociation helpers, real standard integer minmax IDs, source overflow queries and exact FP flag requirements |

Entry:

```csharp
LlvmLicm.Run(loop, loopInfo, dominators, alias, memorySsaUpdater,
    libraryInfo, targetInfo, options: null, trace: trace);
```

The caller owns canonical-loop/LCSSA sequencing, the real AA and MemorySSA
lifetimes, and innermost-first loop visitation. The component accepts the actual
source LoopNestMode switch as an optional argument; it does not select another
backend or pipeline. Source `ScalarEvolution*` is absent and follows null-SE
branches. It does not rebuild dominator or MemorySSA graphs as a substitute for
updating them. The split-exit helper uses source SplitBlockPredecessors with
actual DT/LI/MSSA wiring.

## Exact current model boundaries

The current represented scalar domain has no Fence/vector element classes,
funclet pads, coroutine/guard/invariant-start/noalias declaration IDs, or LLVM
profile/debug/AA metadata attachments. The corresponding source branches cannot
match such values; metadata-dependent promotion paths therefore consume the
actual empty tag set rather than guessed information. Registered standard
`smin/smax/umin/umax` descriptors are real LLVM intrinsic identities and are
handled by the shared Core/AA/constant-folding sources; ordinary Native
function names are never treated as intrinsic IDs.

The target-info dependency is the real generic CRTP GEP/address-mode cost
implementation required by source sink foldability, not a new price list for
instructions. The target library is explicitly configured with all C library
functions disabled because the runtime ABI is not a C library ABI. This does not
claim a full TLI platform/prototype implementation. Unsupported general call
attributes are not inferred; current unannotated calls follow the actual unknown
call contract. In particular, no allocation/fresh-object effect is strengthened
to justify speculation or commoning of HeapAlloc/FileIn/FileOut identities.

Per-use Native ABI annotations are copied to generated memory/PHI operands, not
written onto shared canonical constant objects. SourceSpan retains diagnostic
provenance and is not fabricated LLVM DebugLoc. Default arithmetic has unchanged
IEEE semantics; reciprocal/reassociation transforms require their exact source
FMF conditions. No readiness graph, demand guard, heap proof or validation mode
is inserted.

## Telemetry and verification handoff

The actual function/loop pass, sink/hoist regions, candidate collection,
promotion iterations/sets, instruction sinking and actual hoisting are observed
through CompilationTrace. Work metrics include visited blocks/instructions,
source cap outcomes, clobber-walker calls, alias/conflict work, must-execute or
speculation outcomes, concrete retention causes, clones/LCSSA PHIs, memory
promotions, preheader loads and exit stores. Recording-disabled code creates no
metric or source-attribute dictionaries. Shared AA/MSSA/SSAUpdater/ICF sources
retain their own detailed work counters and cache/invalidation observations.

The 15-file LICM cohort was published only after its dependencies and API shapes
were present. No independent build was started by this owner. Root performed the
serialized integration build and source execution journey recorded below.


## Executed integration receipt

The source default optimization route now invokes this component. Root's actual
Release build completed with zero warnings and errors. The execution receipt is
[`artifacts/diagnostics/llvm-licm-loops/receipt.json`](../../artifacts/diagnostics/llvm-licm-loops/receipt.json);
the associated build log is
[`build.log`](../../artifacts/diagnostics/llvm-licm-loops/build.log).
The journey executed 9 source cases in 18 runs, all passing.

| Observed work | Before | After | Attribution |
| --- | ---: | ---: | --- |
| Duplicate multiply instructions / executed hits | 2 / 2 | 1 / 1 | Existing scalar EarlyCSE; not counted as LICM motion |
| Runtime executions of the kernel's invariant `scale * scale + shift` arithmetic | 16 | 2 | Actual LICM motion; kernel result remains 108 |
| Instructions hoisted by LICM | — | 5 | Recorded transformation count |
| Promotion exit stores emitted | — | 0 | No claim of promotion benefit on these fixtures/current facts |

The execution coverage includes scope alias/callback clobbers (results 7/69),
nested loops, early exits, per-iteration capture result 321, and zero-trip result
0. These are real source journeys, not a count of standalone unit assertions.
The 16-to-2 change is an executed arithmetic-work reduction, **not** an asserted
8x wall-clock speedup or a compile-time speedup. Compilation wall-clock comparison
was not added to this journey.

The later HeapAlloc semantic adapter exposes facts already defined by the
instruction: successful allocation returns fresh, non-null, writable storage of
its size/alignment; live references are not explicitly freed by this language.
Allocator state is inaccessible memory rather than an implicit object-field
store. The instruction remains non-speculatable and preserves each execution's
fresh identity. These are instruction semantics consumed by existing source
queries, not a new heap solver, temporal proof or runtime check.

The same nine-source/eighteen-execution journey passed after that adapter and
produced **one actual promotion exit store** in `loop-memory-alias`, while the
shared-reference result remains 7 and the callback-clobber result remains 69.
Evidence: `artifacts/diagnostics/llvm-licm-heap/{build.log,receipt.json,components.json}`.

Public pending-Number behavior passed separately at O0/O2: three submissions per
level, zero-trip result zero before releasing the child, two genuine pending-root
observations and the final exit result zero. The ordinary CLI source/detached
HIR-object/bound routes passed with 29 exact files per route and selected cast
failures: `artifacts/diagnostics/llvm-licm-pending/` and
`artifacts/diagnostics/llvm-licm-cli/`. The combined CLI kernel has an eight-trip
and a zero-trip invocation; its executed arithmetic is 16 → 4, not 16 → 2,
because speculation can execute two pure operations for the zero-trip call too.

The generic target-cost slice is the actual CRTP GEP/address-mode model from
`TargetTransformInfoImpl.h`; its API is strongly typed to the implemented GEP
family rather than inventing costs for other instructions. C-library function
availability uses LLVM's disable-all configuration: this target binds the msync
runtime ABI and source functions named `sin`/`malloc` have no C-library contract.
The complete target-specific TTI/TLI registries are not claimed by these adapters.

The Linux x64 Native AOT compiler passed the same real O0/O2 source,
detached-HIR-object and bound routes after this integration. Binary:
`artifacts/native/llvm-licm/msync`, SHA-256
`f32ca92a75196e0163d88b4a5ec56345836ea1e443682b43aba27656594d7652`.
Publish/run logs and receipt are in `artifacts/diagnostics/llvm-licm-aot/`.
All 29 exact outputs per route and selected cast failures passed. The combined
kernel count is 16 → 4; the standalone nonzero-trip kernel remains 16 → 2.
These receipts do not mark the wider compiler/optimizer goal complete or claim
full LLVM IR/TLI/target coverage.
