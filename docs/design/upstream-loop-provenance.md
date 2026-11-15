# Upstream correspondence audit: loop structure and LICM

## Status and reference identity

This is an audit of the **unaccepted, subsequently removed Native loop drafts**,
not evidence that an upstream loop optimizer was delivered. During inspection the
drafts were `NativeLoopInfo.cs`, `NativeLoopOptimization.cs` and
`NativeLoopCodeRewrite.cs` under `src/Msync.Optimization/NativeModel/`. They are no
longer present in the current product tree. They must not be recreated or counted
as LLVM/GCC implementation coverage merely because they used familiar pass names.
No product changes or builds were performed for this audit.

The exact draft sources are preserved outside the product build in
[`artifacts/diagnostics/native-midend-drafts/QUARANTINE.json`](../../artifacts/diagnostics/native-midend-drafts/QUARANTINE.json).
The audited copies, rather than a future file with the same name, are:

| Archived source | SHA-256 |
| --- | --- |
| [`NativeLoopInfo.cs`](../../artifacts/diagnostics/native-midend-drafts/source/src/Msync.Optimization/NativeModel/NativeLoopInfo.cs) | `b7d108b08c71b8edde598a25d4125a2d0144f61c38cf9105daa98b5316d9a05d` |
| [`NativeLoopOptimization.cs`](../../artifacts/diagnostics/native-midend-drafts/source/src/Msync.Optimization/NativeModel/NativeLoopOptimization.cs) | `a5c12b165670a69a1dcce67dc4c5b4c7a0738d31ee82bf97250bf1b8898e98c3` |
| [`NativeLoopCodeRewrite.cs`](../../artifacts/diagnostics/native-midend-drafts/source/src/Msync.Optimization/NativeModel/NativeLoopCodeRewrite.cs) | `2248ccbb96b713589accec0f378f7c13cb035303e3823aa25a77795dfca403c9` |

The user's corrected direction is primary LLVM IR, with only the necessary heap
and file-I/O adaptations. It does not authorize selecting an LLVM backend,
executing LLVM's full optimization pipeline, or inventing a substitute IR/algorithm.

Immutable inspected source identities:

- LLVM: [`a55e64432a516fd6507d3ecfba7407cfe425f940`](https://github.com/llvm/llvm-project/tree/a55e64432a516fd6507d3ecfba7407cfe425f940).
  `vendor/llvm-reference/SOURCE.json` records the same research snapshot; it is not
  a deployment-version selection.
- GCC: [`c548a927b535fd8cb003e4b51aa4225613669905`](https://github.com/gcc-mirror/gcc/tree/c548a927b535fd8cb003e4b51aa4225613669905).
  This is a secondary comparison source, not an additional backend.

Links below use those commit identities rather than moving `main` URLs. Function
names are the principal anchors; line anchors identify the inspected revisions.
The separate [loop-transformation provenance audit](upstream-loop-transforms-provenance.md)
owns unswitching, peeling, cloning and the absence of an implemented unroller.

## Verdict matrix

| Draft component | Actual draft mechanism | Pinned upstream implementation | Disposition |
| --- | --- | --- | --- |
| Dominators | Iterative reverse-postorder immediate-dominator intersection | LLVM Semi-NCA construction and depth-based incremental updates | Not an implementation port |
| Loop discovery | Dominating backedges, reverse predecessor sets per header, pairwise subset parenting | LLVM DFS/header-chain loop identification with irreducible-loop reduction | Same mathematical natural-loop concept, different algorithm/data structures |
| Preheaders | Insert PC-level jumps before headers and relocate outside incoming edges | LLVM block predecessor splitting with SSA/loop/dominator/MemorySSA updates | Partial local mechanism, not LoopSimplify |
| Canonical loop form | Preheaders only, retain multiple latches and non-dedicated exits | LLVM LoopSimplify also forms dedicated exits and one backedge | Incomplete algorithm coverage |
| Invariant motion | Native-slot definitions plus a private sparse-value graph, repeated pending-candidate scans | LLVM SSA operands and region traversal with explicit motion dependencies | Not an upstream LICM port |
| Speculation | Local `ReadyAt` / `ClassifyValues` treatment of Native pending datums | LLVM instruction semantics and must-execute/speculation interfaces | Representation-specific custom restriction, not LLVM policy |
| Memory motion | No memory LICM/sinking/promotion implementation in the draft | LLVM load/call motion and scalar promotion with memory dependencies | Missing, not delivered |
| Mutation | PC arrays, origin maps, clear Storage and rebuild analyses | LLVM Value/use/PHI and analysis-updater mutation | Native artifact rewrite adapter, not primary LLVM IR mutation |

## 1. Dominators are not the pinned LLVM algorithm

The draft `Dominators` repeatedly visited reachable blocks in reverse postorder;
`Intersect` walked already-computed immediate-dominator chains until their RPO
indices met. This is a simple fixed-point construction. The name of the output
relation does not establish correspondence with LLVM's construction.

The pinned [GenericDomTreeConstruction.h](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/Support/GenericDomTreeConstruction.h#L8-L34)
identifies Semi-NCA for full construction and a depth-based algorithm for dynamic
updates. Its `SemiNCAInfo` stores DFS numbering, parent, semidominator, label and
idom records. The removed draft did not implement those phases or update model.

**Required action:** use the selected upstream dominator representation and its
corresponding construction/update routines. Do not describe the removed RPO
intersection routine as a simplified port of Semi-NCA.

## 2. Current upstream loop discovery is not the draft's backward-set pass

The removed `FindLoops` collected an edge when its target dominated its source,
walked predecessor edges backward from each header's latches into a HashSet, then
sorted all resulting sets and searched for the smallest containing parent. It
eagerly constructed dominators even when no loop was ultimately found.

At the pinned revision,
[GenericLoopInfoImpl.h `analyzeImpl`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/Support/GenericLoopInfoImpl.h#L500-L635)
uses DFS state and ordered enclosing-header chains. The source identifies the
Wei et al. SAS 2007 loop-identification algorithm; it separately records
reentered headers and reduces irreducible cases to natural-loop subsets. It can
request dominators for that reduction rather than eagerly requiring them for
every loop-free input.

**Difference that matters:** equal natural-loop membership on a few reducible
examples would not establish matching construction cost, irreducible handling,
loop order, block layout, identity reuse or mutation behavior. No such parity
claim was validated for the draft.

## 3. The PC preheader insertion was not complete LoopSimplify

The removed canonicalizer inserted jump prefixes at headers with no dedicated
outside predecessor. Existing inside-to-header branches skipped the prefix;
outside branches entered it. A special skip jump handled a lexical inside-loop
fallthrough. This was Native-PC relocation, without SSA PHI rewriting.

LLVM's pinned
[`InsertPreheaderForLoop`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/LoopSimplify.cpp#L112-L145)
splits outside predecessors as basic blocks and updates the supplied analyses.
The same file's
[loop simplification](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/LoopSimplify.cpp#L550-L615)
also creates dedicated exit blocks and unifies multiple backedges, including
header-PHI simplification. The draft did neither.

GCC's pinned
[`create_preheader`](https://github.com/gcc-mirror/gcc/blob/c548a927b535fd8cb003e4b51aa4225613669905/gcc/cfgloopmanip.cc#L1599-L1700)
and
[`force_single_succ_latches`](https://github.com/gcc-mirror/gcc/blob/c548a927b535fd8cb003e4b51aa4225613669905/gcc/cfgloopmanip.cc#L1720-L1735)
likewise operate on explicit CFG edges/blocks and maintained loop structure.

**Required action:** primary LLVM basic blocks, PHIs and the upstream loop form
must replace the PC-prefix substitute. Existing Native serialization bookkeeping
cannot stand in for that IR transformation.

## 4. The draft's invariant-motion mechanism was a separate local algorithm

The removed `Hoist` counted Native-slot definitions, marked selected slots exposed,
queried a temporary scalar graph at preheader exits, and repeatedly scanned
pending candidates. It simulated placement changes in side lists, then inserted
copies of selected instructions into preheaders and left Nops at original PCs.
It was restricted to globally single-definition temporary results and a local
opcode/type whitelist. It skipped whole functions with scheduled regions or a
wide-frame-access marker.

The pinned LLVM
[`hoistRegion`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/LICM.cpp#L611-L650)
uses loop-region order over SSA instructions and loop-invariant operands. Motion
is gated by separate instruction/dependency and execution conditions before
moving the original instruction. This is not the draft's mutable-slot
availability/classification fixed point.

LLVM's
[`canSinkOrHoistInst`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/LICM.cpp#L937-L1020)
contains load/call handling and consumes the established memory dependencies.
LICM also supports sinking and scalar memory promotion. The removed draft had no
counterpart for those transformations. Its narrow arithmetic success would not
satisfy complete upstream LICM coverage.

## 5. Native datum readiness must not be mislabeled as LLVM speculation

In the removed draft, `ClassifyValues(ready: true)` propagated readiness through
selected Native producers and PHIs; parameters were not assumed ready.
`ReadyAt` then prevented arithmetic hoisting when an operand could remain a
pending Native datum. This was introduced because the current interpreter's
pending arithmetic constructs and tracks a new future. It was a local response
to the Native representation, not a rule found in LLVM LICM.

LLVM's pinned
[`isSafeToExecuteUnconditionally`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/LICM.cpp#L1465-L1490)
uses instruction speculation semantics or guaranteed execution. Its
[ValueTracking interface](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Analysis/ValueTracking.cpp)
reasons about LLVM operations, not pending language-runtime values.
[MustExecute](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Analysis/MustExecute.cpp#L286-L310)
accounts for implicit control-flow exits as well as loop paths. Dominating latches
alone is not its full execution predicate.

The language's heap/file-I/O adaptation must expose actual suspension, demand,
exceptions and memory effects in the chosen LLVM representation. It must not
silently make an ordinary LLVM numeric instruction create a hidden runtime future,
then compensate by adding a new global readiness proof to every arithmetic
optimization. Nor does this audit authorize silently waiting for pending I/O at a
new point: the real demand/effect order still has to be represented faithfully.
This mapping decision precedes claiming upstream LICM implementation parity.

## 6. GCC is corroboration, not a license to mix local policies

The pinned GCC
[`movement_possibility_1` / `movement_possibility`](https://github.com/gcc-mirror/gcc/blob/c548a927b535fd8cb003e4b51aa4225613669905/gcc/tree-ssa-loop-im.cc#L348-L475)
distinguishes unrestricted motion, motion that must preserve execution and
impossible motion. It uses GIMPLE SSA/effect/trapping properties.
[`determine_max_movement`](https://github.com/gcc-mirror/gcc/blob/c548a927b535fd8cb003e4b51aa4225613669905/gcc/tree-ssa-loop-im.cc#L816-L900)
then finds permitted movement levels from the actual dependencies and execution
information. These routines are not the draft's Native opcode whitelist plus
pending-datum readiness graph.

A transformation resembling the same textbook idea is not a strict port of
these GCC routines. Primary LLVM IR also means GIMPLE-specific structures should
not be blended into the replacement IR under the name of following both systems.

## 7. Proposed first-iteration reuse is not upstream LICM

Before implementation was stopped, a separate helper draft kept the original
first iteration and appended a residual clone with selected invariant-definition
PCs replaced by Nops. `NativeLoopOptimization` had not integrated that helper.
The proposed selection would have used invariant operands and latch dominance to
avoid zero-trip speculation. No exact upstream LICM counterpart was established.

This proposal must remain classified **custom and unaccepted**, not described as
LLVM LICM, GCC LIM or proven upstream peeling. The
[separate cloning/peeling audit](upstream-loop-transforms-provenance.md) compares it
with actual upstream clone/PHI/analysis updates and profitability decisions.

## Implementation direction implied by this audit

1. Establish primary LLVM IR and ordinary SSA value/use/PHI/control-flow semantics.
2. Implement the selected pinned upstream dominator/loop/canonicalization code
   and required update interfaces, not a local algorithm with similar outputs.
3. Map heap/file-I/O effects at their actual boundaries; retain the language's
   observable scheduling, lifetime and IEEE behavior there.
4. Bring over LICM's actual instruction legality, execution and dependency
   consumers together with the transformations they support. Do not add a
   disconnected proof pass or count only scalar-copy motion as full LICM.
5. Record any unavoidable platform/language adaptation beside its exact upstream
   function and explain its semantic necessity. A local policy without such
   provenance is not delivered coverage under the corrected objective.

Upstream debug assertions and optional verification hooks are not instructions
to add mandatory source validation or whole-heap checks to ordinary compilation.
Only the dependencies consumed by the actual selected upstream transformation
belong in its implementation; they do not become new language semantics.

This note is a correspondence audit and a rejection of unsupported port claims.
It does not certify performance, correctness or completion of the new LLVM-IR
implementation, and it selects no LLVM backend or complete optimization pipeline.
