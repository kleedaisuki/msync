# LLVM local CFG and critical-edge utilities

The implementation translates LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`; source is retained under
`vendor/llvm-reference`. These are transformation utilities used by
SimplifyCFG and LoopRotation, not a separate optimization pipeline or an
execution prerequisite. Range remains an ordinary runtime object and receives
no special treatment here.

## Source correspondence

| Product API | Pinned LLVM source |
| --- | --- |
| `LlvmLocalCfg.ConstantFoldTerminator` | `Transforms/Utils/Local.cpp:133–347`: conditional branch constant/duplicate-target folds; switch default-case removal, constant/single-destination fold, single-case equality branch |
| `LlvmLocalCfg.TryToSimplifyUncondBranchFromEmptyBlock` | `Local.cpp:841–1425`: bounded shared-predecessor query, PHI conflict/growth checks, full and partial predecessor redirection, PHI undef/poison normalization, physical block removal |
| `LlvmLocalCfg.RecursivelyDeleteTriviallyDeadInstructions` / `DeleteTriviallyDeadInstructions` | `Local.cpp:522–596`: operand detachment and recursive source worklist, optional MemorySSA removal and deletion callback |
| `LlvmCriticalEdgeSplittingOptions` | `Transforms/Utils/BasicBlockUtils.h:166–221`: source flags and defaults |
| `LlvmBasicBlockUtils.SplitBlock` / shared `SplitBasicBlock` | `BasicBlockUtils.cpp:1030–1092` and `IR/BasicBlock.cpp:491–526`: source split-point normalization, instruction splice, successor PHI repair, direct-DT or DTU updates, LoopInfo membership and MemorySSA movement |
| `LlvmBasicBlockUtils.IsCriticalEdge` | `Analysis/CFG.cpp:106–139`: exact edge multiplicity versus identical-predecessor mode |
| `SplitCriticalEdge` / `SplitKnownCriticalEdge` | `Transforms/Utils/BreakCriticalEdges.cpp:94–300`: exact PHI edge redirection, optional identical-edge merge, MemorySSA wiring, DT/PDT updates, loop membership and loop-exit resplitting |
| `CreatePHIsForSplitLoopExit` | `BasicBlockUtils.cpp:982–1010`: source LCSSA PHIs, including duplicate predecessor edges |
| `SplitAllCriticalEdges` | `BasicBlockUtils.cpp:1015–1029` |
| `LlvmBasicBlockUtils.SplitBlockPredecessors` | `BasicBlockUtils.cpp:1101–1217, 1237–1416`: existing LoopSimplify source implementation is reused; the additional DTU branch is the actual insert/delete batch or entry-root recalculation from `UpdateAnalysisInformation` |

`LlvmLoopSimplify.SplitPredecessors` is now internal so the two public utility
wrappers and LoopSimplify share the same PHI/LoopInfo implementation. Existing
public overloads are unchanged; the optional updater argument extends the shared
implementation rather than replacing direct-DT updates with a reconstruction.

## Representation and ownership

The current IR represents ordinary branches, switches, returns and unreachable,
not IndirectBr/BlockAddress, EH-pad terminators, debug records or lifetime
intrinsics. The corresponding source-only transformations are not invented from
other operations. Existing source restrictions for IndirectBr opcodes and EH-pad
flags remain in the generic edge helpers. The unrepresented loop metadata
preservation and branch weight transfer paths need their actual IR metadata
representation before they can be translated; `SourceSpan` provenance is retained.

PHI incoming blocks are stored outside ordinary `Use` objects in the managed IR.
Whole-block RAUW already updates these references through the Core block hook.
The partial empty-block rewrite redirects only the eligible predecessor
terminator uses, just as the source predicate does; incoming values are repaired
explicitly first. This preserves the common predecessor when incompatible
values prevent removing the block entirely.

Per-Use logical ABI annotations move with the transformed incoming values;
context-unique constants are never stamped with a new logical identity.
Instruction/block names use the existing symbol-table APIs.

A recursive deletion worklist cannot RAUW a queued instruction: only unused
instructions are enqueued. Its `Parent == null` entries therefore represent
source WeakTrackingVH entries already erased by another queued dependency; no
new tracking or ownership system is introduced.

## Source budgets and analysis updates

The shared-predecessor query retains the source budget of 16 successor edges.
The maximum PHI-entry increase defaults to 1,000, matching the source option,
and uses its unsigned arithmetic. No new growth heuristic is added.

Critical-edge splitting updates real DT/PDT with the source insertion-before-
deletion batch, so a destination stays reachable during the graph update.
LoopInfo receives exactly the enclosing loop chosen by the source containment
cases. Splitting a loop exit optionally creates LCSSA PHIs and restores dedicated
exits for remaining in-loop predecessors. MemorySSA uses its existing
`WireOldPredecessorsToNewImmediatePredecessor` update, not graph rebuilding.

The base block split retains the source direct-DT child-reparenting path and
DTU edge-update batch. Its graph splice is also shared with IfThen construction,
which owns its full source DTU batch rather than applying two intermediate
transformations. MemoryAccesses move through `MoveAllAfterSpliceBlocks`, and
successor MemoryPhi incoming blocks are redirected by the existing updater.

The DTU predecessor-split path submits actual updates. Only replacement of a
function entry uses source-selected `Recalculate`; ordinary splits do not.
The direct-DT path continues using the original `SplitBlock` update.

## Telemetry and focused execution

Callers pass their existing per-pass metric map, retaining the function/source
span that owns the utility work. `local_cfg.*`, `critical_edge.*` and
`cfg.split_predecessors.*` record actual candidate decisions, default-case
removal, PHI redirection, dead instruction removal, identical-edge merge, LCSSA
creation and exit resplitting. Existing LoopSimplify trace counters remain on
the shared implementation. Recording-disabled calls do not allocate a metric
map or a separate trace sink.

`tests/LlvmIr/LocalCfgProperties.cs` covers duplicate/constant branches,
constant/default/single-case switches, full and partial empty-block redirection,
Eager and Lazy DTU usage, identical critical switch edges, and LCSSA exit
splitting with natural LoopInfo, and base block splits carrying real memory
accesses and loop latch/backedge PHIs. Incrementally maintained DT/PDT parent/level
relations are compared with newly constructed source trees at the resulting
CFG. These checks supplement the coordinator's real compiler/CLI journeys;
they are not a substitute for those journeys or a claim that the whole LLVM
optimization pipeline is complete.

## Alignment queries and actual storage alignment

`LlvmLocal.Alignment.cs` now ports `tryEnforceAlignment` and
`getOrEnforceKnownAlignment` from pinned `Utils/Local.cpp` 1545–1630. These are
ordinary source-local optimization helpers, not a heap solver or an admission
check. An alloca's requested alignment is installed directly unless it would
introduce stack realignment beyond the DataLayout's specified natural stack
alignment. A global's alignment is installed only where the upstream definition
and linkage contract permits changing that emitted object. The thread-local
maximum is the module's `MaxTLSAlign` flag in bits; it is not a host-platform
measurement or process-wide setting.

The Core prerequisites preserve the source distinctions:

- `S<size>` gives optional natural stack alignment. `Fi<size>` and `Fn<size>`
  give independent or function-relative function-pointer alignment. These fields
  participate in DataLayout equality and hashing.
- `GlobalObject.CanIncreaseAlignment` follows `Globals.cpp` 422–470: a strong
  definition, section/alignment packing, ELF exported-object COPY relocations,
  and XCOFF `toc-data`. Global section and string attributes are explicit IR
  state; there is no invented rule about language object identity.
- `Triple` object-format queries project the pinned architecture/parser tables
  and `getDefaultFormat` rules, including explicit environment format suffixes,
  ARM endian spellings, PPC/AIX, SystemZ/zOS and Apple firmware. They use the
  module target string, never the executing host. The private projected
  architecture classes merge only source architectures with identical format
  outcomes; they are not exposed as a replacement architecture enumeration.
- `ValueTracking.GetPointerAlignment` now respects explicit global alignment,
  preferred versus ABI layout for strong versus replaceable globals, function
  alignment mode, argument align/sret types, call return alignment and constant
  `inttoptr` trailing zeros. Native HeapAlloc retains its declared alignment.
  Load-result `MD_align` metadata and absolute-symbol metadata are not represented
  by the current Core model and are not claimed as supported by these hooks.

KnownBits now accepts an optional explicit DataLayout at the end of its existing
scalar query API and carries it through expression, select, PHI/recurrence and
condition recursion without losing AC, DC or injected CondContext. Existing
callers remain source-compatible; when omitted, module/context ownership supplies
that layout, with the pre-existing default layout retained for detached queries.
The source pointer-alignment refinement runs after opcode facts and before
context facts, and pointer widths no longer silently remain 64 under an explicit
32-bit layout. GEP arithmetic uses the source index width and inserts the result
into only the low address bits when representation width is larger. No global
cache or ambient query context is introduced.

`local.alignment.*` counters identify queries, actual alloca/global updates,
natural-stack refusal, fixed global placement and TLS clamping. Existing
KnownBits/ValueTracking counters accompany the alignment facts. Caller-owned
pass telemetry retains source/function context; recording-disabled helpers do
not allocate diagnostic dictionaries or attach a global sink to Core queries.

`tests/LlvmIr/LocalAlignmentProperties.cs` checks actual alloca/global mutation,
explicit alignment, section packing, ELF versus COFF/XCOFF behavior, TLS flags,
function-pointer modes, argument facts, explicit pointer widths, null maximum
alignment, ARM C-compatible calling conventions and object-format source cases.
The class is published for root-owned runner integration; this owner did not
modify Program or execute a build. `CallingConv.h` was fetched at the same source
pin; the root manifest refresh should include it. In particular ARM_APCS is 66,
ARM_AAPCS is 67, and ARM_AAPCS_VFP is 68. TLI's compatibility query is independent
of its existing `disableAllFunctions` setting and does not infer a C library
contract from a source function name.
