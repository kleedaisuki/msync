# Compiler pipeline analyses and LLVM-to-Native boundary review

## Scope and evidence

Read [shared context](../../design/llvm-performance-regression-review.md) first. Reviewed current producer and compiler pipeline, analysis
construction/ownership, and the standard Native backend boundary against pinned
LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`. No production changes, builds,
new executions, or timing runs were made. Existing JSONL records were aggregated
by span name/parent and work counters. SROA partition internals are another
reviewer's remit.

The telemetry-off measured compile regression is **+4.738 ms median paired** for
the midend journey (92.125/97.010 ms process p50), and +0.370 ms for numeric.
Corpus bound paired changes are +1.71%, +1.70%, +3.97%; source changes are +3.66%,
+5.01%, +7.69% for numeric/policy/Unicode respectively. These are the authoritative
measurements supplied to this review, not derived from the profiles below.

Profile files:

- old: `.temp/llvm-foundations-midend-aot/O2/compile.jsonl`
- new: `.temp/llvm-sroa-midend-tier-aot/O2/compile.jsonl`

These are separate detailed observations, not paired measurements. Inclusive
spans overlap. Durations below are **not** additions explaining +4.738 ms.

| Existing observation | Old | New |
| --- | ---: | ---: |
| Optimizer inclusive ms | 85.414 | 107.328 |
| mem2reg / replacement SROA inclusive ms | 2.100 | 31.098 |
| Forward + postdominator construction calls | 132 | 132 |
| Dominator construction inclusive ms | 1.5004 | 1.6328 |
| Dominator construction allocated bytes | 781,360 | 781,360 |
| MemorySSA construction calls | 71 | 71 |
| MemorySSA construction inclusive ms | 5.9915 | 4.3880 |
| MemorySSA construction allocated bytes | 2,648,720 | 2,685,392 |
| LoopInfo / CycleInfo calls | 33 / 33 | 33 / 33 |
| Loop-motion owning spans | 5 | 5 |
| Native target inclusive ms | 7.5243 | 3.9955 |
| Native target allocated bytes | 1,985,816 | 2,310,712 |
| Emitted Native instructions / slots | 1,949 / 1,758 | 1,883 / 1,683 |

**Central conclusion:** the cohort does not introduce an analysis-rebuild count
explosion. Analysis lifetime has small concrete shared inefficiencies, but the
available evidence does not identify it as the principal new compile regression.

## Ranked findings

### 1. Confirmed shared waste: discard a current forward dominator tree before DSE

**Locations:** `src/Msync.Compiler/Compiler.Llvm.cs:54–59,124–132`;
`src/Msync.Optimization/Llvm/LlvmInstSimplifyPass.cs:12–16`;
`src/Msync.Optimization/Llvm/LlvmAdce.cs:14–20` and `LlvmAdce.Cfg.cs:49–52`.

After SCCP, InstSimplify constructs a fresh forward dominator tree. InstSimplify
preserves CFG, but its tree is not retained by the caller. ADCE is then called
without the optional forward dominator tree. DSE unconditionally builds another
forward tree. The existing ADCE implementation already updates a supplied
forward tree when deleting CFG edges; the product does not use that seam.

**Executed scope:** all 33 functions in both profiles take the second construction.
Only `sparse` reports ADCE CFG changes; thus even a conditional reuse would avoid
32 rebuilds. Passing the existing forward tree into ADCE enables preservation
across all 33 without inventing a new analysis framework. The DSE-owned tree
construction observations total **0.3048 ms old / 0.3025 ms new**. This is shared
baseline debt, not newly introduced excess and not a credible standalone
explanation of the paired compile regression.

**Source fidelity:** pinned `llvm/lib/Transforms/Scalar/ADCE.cpp:586–610` gets a
cached forward DT, updates it, and preserves both dominator analyses. The managed
implementation already has the corresponding optional parameter and update path.

**Narrow correction/probe:** explicitly own the post-SCCP forward tree in
`OptimizeLlvmFunction`, pass it to InstSimplify and ADCE, then into memory cleanup.
Keep the initial tree separate because SCCP may change CFG (`LlvmSccp.cs:29–39`).
The focused structural receipt should reduce DT construction132→99 on the existing
midend journey, with the existing `sparse` CFG-changing case included. Only then
consider a serialized telemetry-off timing comparison; expect modest benefit.

### 2. Confirmed but unexecuted incremental waste: rebuilding SROA's preserved DT

**Locations:** `Compiler.Llvm.cs:46–48`; `LlvmSroa.cs:25–29`;
`LlvmDomTreeUpdater.cs:137–151`.

When SROA reports CFG changes, the caller reconstructs a tree that SROA has
already maintained through its lazy updater. `LlvmSroa.Run` disposes/flushed its
updater before returning to the caller. CFG modification does not imply this
particular analysis is invalid.

**Executed scope:** **zero** of the 33 current profiled functions reports
`sroa.control-flow.changed=1`. This branch therefore contributes **zero** to the
observed midend candidate profile. It is new integration debt for sources that
actually trigger SROA PHI/select CFG rewrites, not an explanation for this run.

**Source fidelity:** pinned `SROA.cpp:6632–6644` explicitly preserves
`DominatorTreeAnalysis` even when `CFGChanged` is true; only preservation of the
whole CFG-analysis set depends on `!CFGChanged`.

**Narrow correction/probe:** retain the supplied updated tree rather than
reconstruct it. Exercise an existing CFG-modifying SROA source case and check
that the result retains valid dominators and the extra construction disappears.
Do not disable upstream CFG transformations to avoid the rebuild.

### 3. Confirmed incremental scan work, not extra memory analysis: new scalar IR crosses existing scan boundaries

**Locations:** `Compiler.Llvm.cs:49–59,93–109,124–132`;
`LlvmEarlyCse.MemorySsa.cs:43–55`; `LlvmMemorySsa.Build.cs:25–38`.

The compiler builds MemorySSA once for EarlyCSE in every function, once for loop
motion only in five loop-containing functions, and once for DSE in every
function. The current cohort makes those same analyses inspect more scalar
instructions, while producing precisely the same memory Def/Use graph sizes.

| MemorySSA owner | Builds old/new | Instructions scanned old/new | Defs old/new | Uses old/new |
| --- | ---: | ---: | ---: | ---: |
| EarlyCSE (function parent) |33/33|1736/1857|531/531|166/166|
| Loop motion |5/5|1137/1183|494/494|137/137|
| DSE |33/33|1286/1310|540/540|152/152|
| Total |71/71|4159/4350|1565/1565|455/455|

All283 scanned blocks also remain unchanged. Extra scan work is +191 instruction
visits (+4.59%), not extra aliasing objects, memory definitions, loops, or analysis
instances. InstSimplify visits grow2454→2503, of which1144→1163 are skipped as
not queued. Its whole-function rescan is faithful to pinned
`InstSimplifyPass.cpp:39–72`; replacing it with a bespoke optimizer is not a
source-fidelity fix.

**Evidence strength:** exact recorded work counters establish the additional
scan work. They do not quantify its latency; the recorded MemorySSA construction
duration actually decreases and allocations grow only36,672 bytes.

**Integration gap / narrow next probe:** the default pipeline has no full
InstCombine, while upstream's SROA/EarlyCSE pipeline follows with CFG cleanup and
InstCombine before loop optimization (`PassBuilderPipelines.cpp:519–550`), and
again after loops (`637–649`), before ADCE (`667–688`) and after final SimplifyCFG
(`705–710`). Current `Compiler.Llvm.cs:49–62` only runs the existing
InstSimplify replacement late and before final CFG simplification. This is a
real known coverage gap, not proof that a specific missing combine causes this
regression. Inspect retained post-SROA/EarlyCSE IR for the +121 instructions and
match any material residual pattern to the pinned InstCombine rule before
porting it. Avoid adding arbitrary extra simplifier rounds or deleting required
memory analyses just to compensate for incomplete scalar cleanup.

## Boundary assessment and excluded hypotheses

- **Target code emission is not the demonstrated compile bottleneck.** In these
  detailed observations target time falls7.5243→3.9955ms, instructions1949→1883,
  slots1758→1683. Target allocation rises324,896 bytes and current target records
  148 Function boundary compositions and6 projections. This justifies checking
  concrete boundary materialization work, not attributing the +4.738ms compile
  delta to the target without a discriminating measurement.
- **No new fallback optimizer/backend.** `LlvmNativeLowerer.cs:44–80` lowers the
  optimized graph into existing Native code; `LlvmNativeLowerer.Backend.cs:14–21`
  uses `NativeStandardBackendEmitter`. Machine instructions have actual target
  operations (`LlvmNativeLowerer.MachineScalars.cs:28–54`), not LLVM machine-code
  generation or a Number encoding of arbitrary-width integers.
- **Known raw-write target boundary remains shared.**
  `NativeStandardBackendEmitter.cs:47–48` supplies all-false RawWritable, and
  `NativeRegisterPlan.cs:44–48,72–76` nevertheless performs flow/liveness/pending
  analysis before eligibility rejects every result. `NativeTierLayout.cs:40–42`
  calls it unconditionally. This explains unavailable raw register regions and
  shared preparatory work, not why compiler analysis builds grew (they did not).
  It belongs to the separate runtime/register-plan review. Do not simply set
  RawWritable=true: the emitter supplies no compiled proof/contract for that.
- **MemorySSA recomputation is not uniformly redundant.** EarlyCSE owns and
  disposes its instance (`LlvmEarlyCse.MemorySsa.cs:14–31,43–60`). LoopSimplify's
  current API does not maintain an incoming MemorySSA, loop motion rewrites it,
  and SCCP/ADCE may invalidate later state. Blindly sharing one instance across
  this sequence is not a safe narrow fix. There is no evidence here that a
  general analysis manager would repay its complexity for this regression.
- **No proven retained-IR leak from missing MemorySSA Dispose.** The compiler's
  local loop/DSE memory objects do not use `using`; however memory operands link
  MemoryAccess objects to one another, while `MemoryInstruction` is a normal
  one-way reference (`LlvmMemorySsa.Access.cs:26–36`). Cyclic managed analysis
  objects alone do not establish a GC leak or extra product IR users. This is
  not promoted to a performance finding.

## Recommendation

Prioritize the separately reviewed SROA implementation/representation costs for
the new compile delta. For this remit, apply the tiny existing-API dominator
lifetime correction when convenient, remove the unexecuted post-SROA rebuild,
and use the exact +121 EarlyCSE-input scalar instructions to choose the next
upstream cleanup family. The evidence rejects “many more DT/AA/MemorySSA rebuilds”
as the current regression's main causal story.
