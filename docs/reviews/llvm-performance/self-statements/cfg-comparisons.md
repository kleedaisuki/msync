# CFG comparisons implementer: personal account

## What I actually owned

I joined after the primary LLVM-like representation, Native target route, and
many analysis and LICM components already existed. The inherited conversation
contained the user's corrections and a parent-provided summary of earlier work;
that is context, not evidence that I authored or witnessed those earlier design
decisions. I did not implement the original Range element thunks, alias/heap
solver, frontend effect summaries, Function leaf-SSA representation, SROA,
register-region analysis, ordinary storage bookkeeping, or the measured runtime
cohort. I cannot supply their original authors' motivations as my own.

My first actual assignment was a bounded part of pinned LLVM SimplifyCFG:
comparison-chain gathering, equality-comparison predecessor folding, conditional
store merging, and ConstantRange's equivalent-ICmp conversion. I wrote the
following family files in the shared staging directory, with their source
correspondence recorded in
[`llvm-simplifycfg-comparisons-port.md`](../../../design/llvm-simplifycfg-comparisons-port.md):

- `LlvmSimplifyCfg.ComparesGatherer.cs`;
- `LlvmSimplifyCfg.EqualityComparison.cs`;
- `LlvmSimplifyCfg.ComparisonCloning.cs`;
- `LlvmSimplifyCfg.ConditionalStores.cs`;
- `LlvmConstantRange.EquivalentICmp.cs`;
- `LlvmBasicBlockUtils.IfThen.cs`, a dependency the CFG-utility owner asked me to
  take, rather than the whole CFG utility library.

I also staged `EquivalentIcmpProperties.cs`. Other agents owned the pass driver,
options, builder, general CFG operations, dominator updater, shared PHI
compatibility, and its other transformation families. Root owned publication,
compiler integration, builds and execution. Current files bearing my SimplifyCFG
family names exist in the product tree, and the compiler currently calls the
shared SimplifyCFG pass. That does not make its entire implementation or its
execution receipts my work.

My second assignment was the boolean-comparison subfamilies of
`InstCombineAndOrXor.cpp`, principally source ranges 65–1675, 3443–3715 and
4873–5020 plus FCmp reassociation. I wrote nine
`LlvmInstCombine.BooleanCompare.*.cs` staging files, comparison-code and sign-bit
query utilities, the fuller FP predicate/class utility, two small existing-query
dispatch patches, and `BooleanCompareProperties.cs`. The corresponding record
is [`llvm-instcombine-boolean-compare-port.md`](../../../design/llvm-instcombine-boolean-compare-port.md).
At this account's inspection those nine family files remain in
`.temp/llvm-instcombine`, not `src/Msync.Optimization/Llvm`, and the current
compiler caller does not invoke full InstCombine. I did not deliver an integrated
InstCombine performance improvement.

## Choices I made, and why

The assignment explicitly required translations of the pinned upstream
algorithms, not a replacement optimization of my invention. I kept the source
transformation order, one-use conditions, mask/range arithmetic, logical-select
versus bitwise distinctions, annotation removal and real CFG/SSA updates. I
expanded the FP helper because the old helper explicitly lacked fabs look-through;
it could not support the newly represented source paths just by being renamed.
The Function denormal input mode, StrictFP and NoImplicitFloat queries were
shared dependencies, not facts I was entitled to assume away.

I did make implementation choices. I used managed List, HashSet and Dictionary
carriers, translated the source helper boundaries into partial Work classes, and
used an APInt total order where the C++ implementation used uniqued-pointer order
solely to find equal constants. I copied already-known per-Use ABI information
from the originating store into generated PHIs/stores after consulting the ABI
owner. None of this was literally decision-free copying. The source algorithm
and its semantics constrain those choices; they do not establish the cost of the
managed implementation.

I kept incomplete families staged so that root could publish one coherent cohort
and serialize builds. I did not run MSBuild, product executions, timing runs or
commits during either implementation assignment. That respected coordination,
but left my own delivery without compiled or end-to-end execution evidence.
I checked interfaces, source branches and textual diffs, and supplied proposed
properties and source-journey cases. Those are preparation for verification, not
results proving correctness or performance.

## What I missed or overstated

1. My short handoffs used words such as "complete source families" and "coherent"
   before root had compiled or executed the cohort. The supportable statement
   was that I had written the assigned represented-scalar branches and mapped
   their dependencies. A staged translation with open shared dependencies is
   not a finished production optimization.
2. I found that upstream CmpPredicate carries SameSign as part of the predicate,
   whereas the then-current shared simplifier API accepted only an enum. I
   explicitly copied source flags in the local rewrites and reported the shared
   query gap. That did not resolve the full predicate-pack requirement. My
   coverage account must keep that limitation, rather than treating the explicit
   local flag copies as proof of complete source fidelity.
3. Another owner pointed out that my first IfThen implementation duplicated the
   basic-block splice already being published by the CFG owner. I replaced it
   with the shared source helper in both staging and the product copy. The
   SimplifyCFG owner also pointed out my string-based `.old` name suffix could
   corrupt arbitrary byte names; I changed it to raw-byte appending. These were
   genuine corrections to my implementation, not discoveries I should attribute
   solely to myself.
4. During source review I corrected initially one-sided NOT matching to the
   source's commutable form, restored the logical match type restriction, and
   corrected shifted-mask handling to the source population/leading/trailing-bit
   criterion. This is a reason not to equate a source-mapping table with a
   verified faithful port. I did not execute the staged properties to establish
   that no similar translation errors remained.
5. I did not investigate real workload frequency, managed collection allocation
   costs, repeated analysis ownership, target representation costs or the
   producer/runtime crossover. In particular I did not establish that these
   large ports were the best next remedy for the O2 latency complaint. Following
   a source-port assignment is not evidence that adding another source family
   improves the user's dominant workload.

The initial scope was represented scalar integer and binary64 paths. Vector
constants/materialization, other FP formats and unrepresented metadata were
explicit boundaries. These boundaries cannot be relabeled as completion of the
user's full LLVM migration request.

## Corrections root should preserve in its narrative

- I was an implementer of the bounded later families above, not one of the
  original authors of all representation and performance decisions. The
  ancestor's status summaries are not my personal observations.
- I did not choose or create a new Range evaluation model. My code treated the
  language Range as an ordinary builtin pointer/call. The LLVM ConstantRange
  work I wrote is an integer interval utility, not a language Range mechanism.
- Upstream-derived legality and poison rules inside an optional optimization
  are not a newly designed runtime admission solver. Conversely, saying a rule
  is upstream-derived does not excuse unnecessary invocation, expensive managed
  containers, missing preservation, or an unsuitable representation boundary.
  Those integration and cost questions still have to be answered.
- My unintegrated InstCombine family cannot be presented as a measured fix, or
  asserted to explain an observed runtime regression. The retained performance
  reports and artifacts are other participants' evidence; I did not produce or
  independently reproduce their timings.
- I have no first-hand basis to attribute the measured +4.738 ms compile change,
  hot environment-projection cost, or zero-consumer liveness work to my assigned
  transformations, nor to rule out my integrated CFG family's cost. I made no
  causal measurement isolating it.

This follow-up only records my account. It makes no product change, build, new
measurement or commit, and does not claim the performance problem is solved.
