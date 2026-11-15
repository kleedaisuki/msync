# CodeMetrics / TargetFolder implementer: personal account

## Assignment and authorship boundary

I participated as the worker `code_metrics`. My first assignment was to translate
the pinned LLVM CodeMetrics component for the LoopRotation port. Later root
assigned me the real TargetFolder strategy and its integration with the existing
transform builder, followed by shared GEP utilities and the InstCombine GEP
family. These were implementation assignments, not an independent investigation
of the original O2 slowdown.

When I joined, the LLVM-shaped IR, frontend lowering, Native backend, AA,
MemorySSA, LICM, generic TTI work and much of the default pipeline already existed
or belonged to other agents. I did not select that architecture or those passes.
I did not design the earlier Range evaluation/capture behavior. I cannot give
the original authors' reasons for it as my own. The inherited conversation
helped locate the work; it does not make those earlier changes my authorship.

This follow-up writes only this account. I have made no product edit, build,
new timing run or commit for it.

## What I personally authored

### CodeMetrics and its small Core dependencies

I wrote `LlvmCodeMetrics.cs` and `.Ephemeral.cs`, using LLVM
`a55e64432a516fd6507d3ecfba7407cfe425f940` CodeMetrics.h/.cpp. The implementation
accumulates CodeSize estimates from the shared TTI, records block costs by
actual block number, and collects ephemeral instruction chains from supplied
assumption seeds using the source visited-set/worklist algorithm. It also reads
the source recursion, inline-candidate, dynamic-alloca and convergence state.

I added the initial small Core carriers for call/function `NoInline`,
`NoDuplicate`, `Convergent`, `ReturnsTwice` and `NoMerge`; effective call queries
include the directly called function while cloning copies only callsite state.
I added function size/jump-table/presplit-coroutine flags, static-alloca detection,
live constant-use counting and PHI-edge-aware outside-block use detection.
Subsequent attribute expansion and source attribute intersection/equality work
were other owners' changes. In particular, I should not be credited with the
later NoReturn or null-pointer-validity additions.

The reason for these additions was concrete source dependencies: LoopRotation
needs CodeMetrics, and its duplication decision reads actual IR attributes.
They were not new language capture rules, Range capabilities, runtime admission
checks or a generic heap proof. CodeMetrics is a compile-time code-cost utility,
not a measurement of compiler elapsed time.

I authored `CodeMetricsProperties.cs` and the initial source mapping in
`docs/design/llvm-code-metrics-port.md`. I initially sent an incorrect count of
21 checks, then corrected it to 20. I did not run MSBuild; root owned builds and
integration acceptance.

### TargetFolder and builder support

I authored the TargetFolder body and staged the builder changes that select it
through an optional typed constructor argument. I preserved the existing default
and recording-disabled paths, and preserved the existing `foldConstants: false`
behavior. Root approved correcting the new, unreleased builder intrinsic APIs
to return `LlvmValue`, because upstream permits a folded constant rather than
always a CallInst. The static intrinsic call factories kept their call-returning
contract.

The important distinction was not cosmetic: TargetFolder uses the Analysis
constant folder and DataLayout, including context-sensitive comparison behavior,
real ConstantExpr creation and intrinsic folding. Aliasing it to ConstantFolder
would not implement that source component. I coordinated its dependencies rather
than inventing substitute constant-expression or vector behavior. The Core
ConstantExpr, vector representation, floating-point and integer intrinsic
implementations were other agents' work.

I also authored APInt bit insertion/extraction, the corresponding ConstantByte
bit-field folders, an allocation-free insertion-point guard, and the initial
scalar bit/pointer cast-chain helpers. My initial cast-chain version explicitly
lacked mixed fixed/scalable vector insertion/extraction; the current source has
those paths from later integration. I staged CreateVScale/CreateTypeSize/
CreateElementCount using the actual vscale intrinsic, not an assumed scale of
one. Other owners supplied the vector, floating and assumption builder families.

My TargetFolder and builder work was staged when root authorized joint
publication with its dependency owners; the interruption preceded my own copy
step. Current product files and the updated publication note show that the
cohort was subsequently integrated. That final integration is not solely my
execution. The existing foundations receipt records 104 TargetFolder checks;
the current test adds eight intrinsic-dispatch checks beyond my staged fixture.
I did not author or independently execute that complete receipt.

### GEP work: completed utilities versus unfinished family

I authored the typed `LlvmGepOperator` view spanning instructions and constant
expressions; the ConstantExpr owner published it with that cohort. I added the
source GEP nowrap intersection helpers and authored/published
`LlvmCommonPointerBase`. I then staged the actual offset-emission utilities,
GEP-combination helpers, PHI/select handling and query adapters under
`.temp/llvm-instcombine/`.

I did not complete or publish the whole InstCombine GEP visitor. The staged
`LlvmInstCombine.Gep.*` files are not evidence that the default compiler runs
that full family. Root specifically instructed me not to publish it with the
TargetFolder prerequisites. This boundary should remain explicit.

I also flagged a concrete target-integration issue: new opaque-pointer results
cannot obtain a Native carrier from LLVM `ptr` alone. My first recommendation
was narrow inheritance of missing result annotations on genuinely new
replacement instructions, preserving per-use metadata and never mutating
canonical constants. Root and later target owners made the broader integration
decisions. Their final carrier-selection/environment solution is not mine, and
my initial recommendation should not be presented as the only possible design.

## Assumptions, trade-offs and omissions

- I treated the pinned source algorithms and defaults as the implementation
  contract. Translating their data structures into C# still involved engineering
  choices: immutable APInt updates, managed collections, typed views, optional
  folder injection and a value-type guard. It would be inaccurate to describe
  this as literally making no implementation decisions.
- I initially accepted an explicit assumption-seed enumerable because no
  AssumptionCache had yet been published. After the real cache existed, I
  coordinated passing its actual weak handles and corrected the design note.
  I did not implement the cache or claim that an empty list was a complete
  assumption analysis.
- My original CodeMetrics component did not represent InlineAsm or the
  convergence-control-intrinsic-specific ExtendedLoop transition. Ordinary
  convergent calls and control-token bundles were represented. Those omissions
  were documented, not implemented as affirmative analysis results.
- Source correspondence does not establish equal host execution cost. Managed
  HashSet/List/enumerable use and immutable APInt arrays do not have LLVM's C++
  inline-container or mutation costs. I did not measure their latency or
  allocation contribution. Likewise, generic TTI CodeSize numbers are not a
  calibrated performance model for the Native VM.
- I did not profile the original frontend/O2 workload, own the default pass
  ordering, measure an isolated speedup, or establish that these prerequisites
  paid for themselves on the user's workload. Root's later benchmark and
  source/artifact receipts belong to their recorded executions, not my own.

## Corrections root should preserve in the overall account

1. Separate code-cost statistics from compiler-performance measurements. My
   component's name and source fidelity do not diagnose the original slowdown.
2. Separate authored source ports, staged files, later integration and executed
   acceptance. In particular, do not count the unfinished GEP family as delivered
   or assign all current TargetFolder/intrinsic coverage to me.
3. Do not describe the small attribute carriers as a complete LLVM AttributeList
   implementation, or local property checks as proof of the entire optimizer.
4. Account for prerequisite expansion as real implementation and maintenance
   work. I followed the growing dependency chain from CodeMetrics to shared
   attributes, TargetFolder, ConstantExpr/vector coordination and GEP utilities;
   I did not establish an end-to-end performance benefit for that expansion.

The useful next reporting step is to connect the actual enabled consumers and
their recorded work to the supplied benchmark journey. Adding more source ports
or quoting more property-check counts cannot replace that explanation.
