# cfg_hoist implementation participant self-statement

## Participation and limits of recollection

I participated as `/root/cfg_hoist`, a worker implementing bounded parts of the
later LLVM-source translation. My first direct assignment was common-successor
hoisting in SimplifyCFG. I subsequently received Local unreachable-block
utilities, SROA aggregate/PHI/select families, and InstCombine control,
extractvalue/insertvalue and freeze families.

I did **not** participate directly in the original Range thunk design, the
initial O2 slowdown diagnosis, selection of LLVM as the representation, the
initial source/backend architecture, or the original Native register allocator.
Those topics appeared in inherited parent context, which is not personal
recollection of having designed them. I also did not select the overall default
O2 pass sequence or integrate the completed source families into that pipeline.

This statement describes the work I actually authored and the choices I made.
Other participants subsequently integrated or changed some of those files.
Current product files are authoritative for the delivered implementation, not
my earlier staged snapshot or a completion message.

## Actual ownership

| Assignment | Work I authored | Publication and verification I personally performed |
| --- | --- | --- |
| SimplifyCFG common hoisting | Initial `LlvmSimplifyCfg.Hoist.cs`, `.Hoist.Terminator.cs`, `.Hoist.ConditionalMemory.cs`; common-prefix matching, skip flags, identical-terminator/select/PHI rewrites, and the shared PHI predecessor-add helper | Staged the family under `.temp/llvm-simplifycfg/`; coordinated the driver and shared dependencies. Did not build or execute it independently. Product copies now exist, integrated by the coordinator. |
| Local unreachable blocks | `LlvmLocalCfg.Unreachable.cs`, `.Unreachable.Change.cs`, and `UnreachableBlockProperties.cs` | Published the two product files and eight property groups at root's explicit request. Performed whitespace/diff checks, not compilation or execution. |
| Source attribute and switch prerequisites | Initial `Function.NoReturn`, `Function.NullPointerIsValid`, `NullPointerIsDefined`, call-site `NoReturn`/`DoesNotReturn` and clone preservation; corrected `Switch.SetCaseValue`'s assertion | Made narrow product edits. The ABI participant, not me, implemented the subsequent exact-versus-intersected call-attribute comparison and `NoReturn` intersection. |
| SROA aggregate and pointer PHI/select memory operations | Initial six `LlvmSroa.Aggregate*` / `LlvmSroa.Speculation*` files; common-type discovery, FCA decomposition, GEP unfolding, PHI speculation/store predication, select speculation/predication, and six public-SROA property groups | Staged the family and documented source correspondence. The coordinator later published product copies. I did not independently compile or execute this cohort. |
| InstCombine control, aggregates and freeze | Nine initial `LlvmInstCombine.Control*`, `.Aggregate*`, `.Freeze*` staged files; source dead-edge propagation, branch/switch visitors, aggregate reconstruction, overflow extraction and freeze transforms | Staged source translation only. These initial files contain dependencies that were still being supplied by other owners when my task changed. They were not a personally verified delivery. |
| Exact no-wrap range prerequisite | Staged `LlvmConstantRange.NoWrap.cs`, `LlvmAPInt.Rounding.cs`, `ExactNoWrapProperties.cs` | Ported the source helpers needed by overflow extraction and wrote bounded exhaustive properties. Did not compile or run them. |

I wrote the source-mapping notes `llvm-simplifycfg-hoist-port.md`,
`llvm-cfg-unreachable-port.md` and `llvm-sroa-aggregate-port.md` under
`docs/design/`. I did not author the central SROA partition containers, its
OrderedSet subtraction, the native register planner, the runtime frame layout,
the performance review measurements, or the default-pipeline integration.

## High-level choices and rationale

**Source algorithm fidelity.** The instruction I received was to translate the
pinned LLVM revision `a55e64432a516fd6507d3ecfba7407cfe425f940`, not design a
similar-looking custom optimizer. I retained the source budgets, ordering,
PHI/CFG conditions and worklist protocols. For example, common hoisting retains
the skip limit of 20; freeze recurrence traversal retains the limit of 32;
aggregate reconstruction retains its small aggregate/depth/predecessor limits.
This was an implementation requirement, not evidence that adding all these
passes would improve this project's workloads or compilation latency.

**Real shared graph utilities.** I used the actual shared dominator updater,
PHI predecessor operations, block splitting, constant folder and Use graph rather
than a no-op updater or a separate generic heap proof. The assumption was that
those shared components implemented their advertised LLVM behavior. Their
implementation and runtime cost were not independently validated by me.

**Range stayed an ordinary pointer object.** None of my families introduced a
Range opcode, delayed element evaluator, capture mode or Range-specific analysis.
The no-wrap interval helper is LLVM's integer ConstantRange, not the language's
Range object. I did not reintroduce the earlier thunk model.

**Unsupported representation was not disguised as an optimization.** The source
generic TTI reports no conditional-faulting load/store support. I retained that
actual result and, with root's agreement, left the masked-vector emitter as an
explicit unsupported-representation failure if invoked. I did not replace an
emitter with successful no-op behavior. This is still a coverage boundary: it
must not be advertised as implemented conditional-faulting code generation.

**Target ABI information is separate from optimizer eligibility.** I preserved
per-Use logical annotations where operand roles were copied, and avoided
modifying context-unique constants to attach a Native layout. I reported the
split Function-descriptor/code-pointer and arbitrary integer/vector target gaps
rather than filtering those source transforms to convenient Number cases.
That did not resolve the gaps; the target owner still had to implement them.

**Managed data structures were chosen for a direct initial translation.** I used
Lists, HashSets, Dictionaries and some snapshots such as `Uses.ToArray()` for
ordered mutation. This made source ownership and traversal practical, but it did
not preserve LLVM SmallVector/SmallPtrSet inline-storage costs. The semantic
algorithm can match upstream while allocating much more managed memory. I did
not establish an allocation budget or show that these translations were cheap.

**Detailed telemetry followed the project contract.** I recorded actual visitor,
rewrite, scan and retained-cause work rather than only an outer pass stopwatch.
I removed dynamic retained-cause string construction on disabled recording paths
where I noticed it. I did not measure the remaining instrumentation overhead,
and counters or source correspondence do not prove performance improvement.

## Specific omissions, corrections and unfinished work

1. I did not benchmark, profile allocations, execute a real source journey, or
   run MSBuild. Root explicitly serialized all builds. My checks were source/API
   inspection and whitespace/diff checks. Written properties are not passed
   properties, and static cleanliness is not execution evidence.
2. I accepted successive source-port assignments without first obtaining
   workload evidence that each new pass paid for itself. That followed the
   assigned porting objective, but it left compile-cost and managed-allocation
   tradeoffs unresolved. My work cannot support a claim that upstream names
   alone imply upstream performance.
3. Some handoff messages called staged families "complete" or "coherent" while
   separately listing unresolved shared dependencies. The more accurate status
   was **source family authored, awaiting integration and execution**. Root
   should correct any broader completion claim based only on those messages.
4. The InstCombine staging specifically awaited demanded-FP-class handling,
   frexp descriptor/APFloat support, and vector-constant helpers. I wrote calls
   to the intended shared APIs; I did not prove that the resulting cohort
   compiled or that every required API was subsequently supplied. Those files
   must not be treated as executable coverage merely because they exist.
5. I initially omitted fake-use decomposition from the aggregate visitor while
   the intrinsic was not represented. After the intrinsic owner confirmed its
   real implementation, I added the source per-component fake-use calls. The
   final integration still needed to validate that linkage.
6. The switch setter correction was a concrete source mismatch, not a new
   optimization: upstream checks case-index bounds, while the initial managed
   setter checked case type against the old condition type. LLVM's narrowing
   rewrite changes cases before replacing the condition, so that additional
   assertion rejected a legitimate intermediate state. I corrected the product
   method and the matching staged copy to the source index assertion.
7. My target-ABI annotations were local transport adaptations, not a whole-target
   proof. I did not verify every pending-value, function-descriptor or resource
   lifetime journey affected by the eventual integrated cohort. Other
   participants' executed receipts must be cited for their actual scope.

## Corrections the root narrative should make

Attribute only the families above to me, and distinguish my authored staging
from later integrated product code. Do not attribute the central SROA container
choice, pipeline rebuild policy, original Range behavior, or reported regression
measurements to this participant.

Do not describe my eight/six fixture groups or exhaustive no-wrap property file
as successful tests without an actual matching build/run receipt. Do not infer a
speedup from fewer IR operations, exact LLVM decision rules, source line counts,
or disabled-telemetry precautions. I have no personally measured contribution
to the current elapsed-time regression and no basis to assign it a percentage.

The useful next correction is to connect the integrated source translation to
the actual workload and native target: inspect remaining managed representation
costs, remove directly evidenced source-translation waste while preserving the
source algorithms, and use a coherent real-user journey for the next comparison.
Root should keep that engineering feedback loop distinct from claiming that a
large collection of staged pass files has already completed the requested end
state. I made no product edits, builds, benchmarks or commits for this
self-statement task; only this review document was written.
