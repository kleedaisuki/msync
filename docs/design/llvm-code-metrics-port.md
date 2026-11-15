# LLVM CodeMetrics source component

The component translates `llvm/Analysis/CodeMetrics.h` and
`llvm/lib/Analysis/CodeMetrics.cpp` at pinned LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`. Reference source is retained under
`vendor/llvm-reference/llvm/`; no LLVM library or target backend is linked.

## Interface and source correspondence

`LlvmCodeMetrics` accumulates the source measurements across explicitly analyzed
blocks. `AnalyzeBasicBlock` obtains each non-ephemeral instruction's **CodeSize**
cost from the shared, source-ported generic TargetTransformInfo dispatcher. It
does not substitute instruction count for cost or invent target-specific prices.
`NumBBInsts` is indexed by the actual block number and grows to the function's
maximum allocated block number, preserving holes left by removed blocks.

| Source behavior | Managed implementation |
| --- | --- |
| `analyzeBasicBlock` | `LlvmCodeMetrics.cs`: costs, recursive calls, source inline candidates, static/dynamic alloca distinction, vector-producing instructions, returns and duplication/convergence flags |
| `appendSpeculatableOperands`, `completeEphemeralValues` | `LlvmCodeMetrics.Ephemeral.cs`: visited-set plus indexed queue, append only side-effect-free non-terminator operand instructions, require all users to be ephemeral before expanding |
| Function and loop `collectEphemeralValues` | Same file: take actual assumption seed instructions, skip cleared seed handles, filter seed sites outside the requested loop, then run the same closure algorithm |
| `AllocaInst::isStaticAlloca` | Core memory instruction: constant array size, entry-block placement and absence of `inalloca` |
| `Constant::hasNLiveUses` | Core `LlvmConstant.LiveUses.cs`: count actual uses, excluding recursively dead non-global constant users |
| `Instruction::isUsedOutsideOfBlock` | Core `LlvmInstruction.Uses.cs`: PHI uses belong to their corresponding predecessor edge, not their containing block |

The assumption input is an explicit enumerable view, not a fabricated
AssumptionCache. A null view supplies no seeds. Callers with the source-ported
`LlvmAssumptionCache` pass its actual `Assumptions()` weak handles projected to
instruction values. The registered standard `llvm.assume` intrinsic provides the
seed identity; no assumption is inferred from an ordinary function name,
language assertion, Range, or builtin call. The closure implementation accepts
nonempty seed views and preserves the source treatment of cleared handles.

## Attributes and represented boundaries

The corresponding source attributes now have explicit Core carriers:

- Function and callsite `NoInline`, `NoDuplicate`, `Convergent`, `ReturnsTwice`
  and `NoMerge`. Effective CallBase queries combine callsite and directly called
  function attributes, following `hasFnAttrImpl`; clones retain the **callsite**
  flags rather than snapshotting effective callee state.
- Function `OptimizeForSize`, `MinSize`, `NoJumpTables`, and
  `IsPresplitCoroutine`. `HasOptSize` includes `MinSize`, as in the source.
- `GetConvergenceControlToken` reads the actual `convergencectrl` operand bundle.
  Controlled versus uncontrolled convergence is measured from the actual
  attribute/bundle state; ordinary names never establish these semantics.

The current IR has no InlineAsm or convergence-control intrinsic instruction
class. Therefore the source InlineAsm exclusion and convergence-control-specific
`ExtendedLoop` transition have no represented input path. The enum retains all
source convergence kinds; ordinary convergent calls and represented control-token
bundles are supported. These missing representations are not converted into
invented runtime capabilities or an execution prerequisite. The generic source
token-use and indirect-branch checks remain expressed in terms of actual IR
types/opcodes.

## Observability and focused verification

An optional caller-owned metrics dictionary records instructions visited or
skipped, calls and inline candidates, valid/invalid instruction costs, analyzed
blocks, ephemeral seed/operand/candidate counts, successful closure values and
live-user rejections. The enclosing transform owns source/function spans and
phase timing. Recording-disabled paths allocate no telemetry dictionaries.

`tests/LlvmIr/CodeMetricsProperties.cs` checks generic source cost totals and
numbering holes; static/inalloca behavior; dead versus globally rooted constant
users; effective and cloned attributes; convergence tokens; complete and
shared-use ephemeral chains; loop seed filtering; and PHI predecessor-edge use
semantics. Root owns the coordinated build and real LoopRotation/source journey
execution. No independent MSBuild was launched for this component.
