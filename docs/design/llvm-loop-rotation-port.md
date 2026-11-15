# LLVM loop rotation source port

Pinned upstream: `a55e64432a516fd6507d3ecfba7407cfe425f940`.
License: Apache-2.0 WITH LLVM-exception, retained on each translated file.

## Sources and correspondence

| Pinned upstream source | Managed source / coverage |
| --- | --- |
| [LoopRotation.cpp](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/LoopRotation.cpp), `include/llvm/Transforms/Scalar/LoopRotation.h` | `LlvmLoopRotation.cs`: pass constructor, threshold selection, option combination and utility entry. No fabricated LLVM pass-manager preserved-analysis result. |
| [LoopRotationUtils.cpp](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/LoopRotationUtils.cpp), utility header | `.cs`: processLoop; `.Rotate.cs`: rotateLoop, header cost and exiting-latch profitability; `.Clone.cs`: header cloning/hoisting and RewriteUsesOfClonedInstructions; `.Latch.cs`: shouldSpeculateInstrs and simplifyLoopLatch. |
| `lib/Transforms/Scalar/ScalarOptions.td` | Source defaults: maximum header cost 16, header duplication at minsize false, prepare-for-LTO false. |
| `include/llvm/Support/GenericLoopInfo.h` | `LlvmLoopInfo.RotationQueries.cs`: IsLoopExiting, shared loop-domain query rather than a private rotation approximation. |

The four rotation files are published in `src/Msync.Optimization/Llvm/`.
They have not independently been built or validated: the root owns serialized
integration builds and focused source-to-execution journeys. Source hashes for
its vendor-manifest refresh are in `.temp/llvm-loop-rotation/source-files.sha256`.
This document records component coverage, not a claim of pipeline integration.

## Algorithm and defaults

`LlvmLoopRotatePass.Run(loop, loops, target, tree, memoryUpdater, options, trace)`
uses EnableHeaderDuplication=true, PrepareForLTO=false, CheckExitCount=false.
`LlvmLoopRotation.Rotate` preserves the separate utility defaults:
RotationOnly=true, unlimited unsigned threshold, IsUtilMode=true.
Utility mode bypasses the source exiting-latch profitability heuristic; it does
not claim to have proved profitability. The pass combines its LTO constructor
option with the supplied scalar option and disables duplication at minsize
unless the source scalar option enables it.

The represented scalar implementation retains source eligibility and cost
checks, optional latch folding, invariant non-memory instruction movement,
effectful instruction cloning in original order, eager operand remapping and
simplification, SSAUpdater repair of live-outs, successor-PHI edge duplication,
preheader incoming removal, real DominatorTree/MemorySSA updates,
LoopSimplify-preserving critical-edge splits, and final block merging. It is not
a pure-instruction-only rotation. Range remains an ordinary source object: no
Range instruction or special lowering is introduced.

Source header hoisting is deliberately not speculative LICM: the header's first
execution already occurs on loop entry, and its instruction order is retained
in the old preheader. The source permits potentially trapping non-memory
instructions here. Latch folding separately uses the exact source speculation
and one-increment opcode/cost rule; common CFG merging does not invent a second
legality policy.

CodeMetrics consumes actual generic target instruction costs, nonduplicatable
and convergence state, and LTO inline-candidate counts. The header threshold
compares `LlvmInstructionCost` objects, retaining fractional fixed-point cost;
integer cost formatting is not used as the decision. No unit-instruction-count
approximation or custom duplication heuristic replaces the source model.

## Shared dependency and ownership contracts

| Dependency | Contract |
| --- | --- |
| CodeMetrics / generic TTI | Actual AnalyzeBasicBlock and ephemeral-value algorithm. The optional actual AssumptionCache supplies live weak-handle seeds; a null cache selects the source empty-seed case. |
| ValueMapper | RemapInstruction with NoModuleLevelChanges and IgnoreMissingLocals; ordinary value maps also feed MemorySSA's cloned-block API. |
| BasicBlockUtils | Source FoldSingleEntryPHINodes, MergeBlockIntoPredecessor (including the two-successor latch case), RemovePredecessor and SplitCriticalEdge. Rotation uses the shared block-to-block split overload, not a duplicated edge-index walker. |
| SSAUpdater | Real local-dominator/PHI-placement solver seeded with original-header and preheader values; no private SSA approximation. |
| LoopInfo / DT / MemorySSA | Real mutable membership and CFG update APIs. Eager DomTreeUpdater is disposed at the source destructor boundary. No full-analysis recomputation substituted for updates. |

Instruction cloning retains Source, LogicalType and per-use ABI hints through
the existing typed clone/remap operations. New entering unconditional branches
copy the old branch Source. SSA-created PHIs use the definition's LogicalType;
canonical constants are not mutated to carry source ABI hints.

## Representation boundaries

Branch weights, loop/vectorization metadata, debug records/atom mapping, local
noalias-scope-declaration identities and ScalarEvolution are
not currently represented. Source absent/null branches are selected explicitly,
not represented by fabricated callbacks. In particular:

- no branch-weight recomputation, loop-ID save/restore or forced-vectorization
  threshold override is claimed;
- no local-noalias declaration scope cloning or debug-record deduplication is
  claimed;
- optional-null ScalarEvolution permits the actual header-PHI profitability
  branch but cannot make an exit count computable; CheckExitCount does not
  fabricate that information;
- BlockAddress is absent, so represented latches cannot have address-taken block
  constants; no synthetic flag stands in for that missing constant family.

Actual function attributes HasMinSize and IsPresplitCoroutine and actual call
convergence/nonduplicatable properties are consumed from their shared carriers,
not guessed from names. The utility requires the represented DominatorTree;
upstream's null-DT invocation mode is not exposed. Metadata-dependent source
extensions can be added when those IR objects exist, without changing the
represented scalar CFG algorithm.

## Telemetry and verification handoff

The detailed trace records function/header/source context, utility/pass mode,
header threshold and option settings. Child phases distinguish latch folding,
header eligibility/rotation, target header cost, cloning, SSA repair and
LoopSimplify restoration. Counters record concrete skip reasons, analyzed and
cloned/hoisted instructions, PHI inputs, rewritten/retained uses, inserted PHIs,
header cost (including quarter units), split CFG edges, folded entering branches,
merge outcomes and MemorySSA updates. Shared dependency work counters flow into
the same sink-neutral metrics dictionary. Recording-disabled calls allocate no
metrics dictionary or detailed trace attributes.

Source/API inspection was performed after all shared components were published.
No independent build, host execution claim or blanket regression run accompanies
this handoff. Root integration should exercise a zero-trip conditional entry,
a nonzero constant-folded entry, a header with memory/call effects and live-outs,
a nested-loop exit split, and latch folding, retaining the actual source graph
and end-to-end behavior as the acceptance surface.

## AssumptionCache representation update

Both Run and Rotate append optional `assumptions` parameters, preserving existing
positional callers. Header CodeMetrics now receives the supplied cache's actual
live instruction handles, and retained cloned assume instructions are registered
with that same function cache, matching the source registration point after
clone simplification. Clone simplification receives the cache through the shared
SimplifyInstruction/SimplifyQuery path; it is not an accepted-but-ignored value.
Detailed telemetry records cache availability, actual ephemeral work in shared
CodeMetrics, and cloned assumption registrations. Latch speculation remains the
source default isSafeToSpeculativelyExecute(&I) call without an extra cache-based
proof policy. Null caches remain usable for standalone utility calls.
