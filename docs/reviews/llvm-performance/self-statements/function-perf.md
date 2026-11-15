# Personal statement: function-perf

## My assignment and actual authorship

I joined as a performance fixer after the first published SROA/Function-SSA
candidate regressed on the existing paired Corpus workloads. I did not design
or implement the original Function aggregate representation, code-token ABI,
canonical nullable environment carrier, entry-prelude placement, SROA default
selection, PHI lowering, or environment-schema reconstruction. Those were inputs
to my assignment, not decisions that I can explain as their original author.

I made two production changes:

1. In `LlvmNativeLowerer.AggregateSlots.cs`, I added exact
   `(NativeConstant, NativeType)` register reuse to `ConstantSlotValue`, with
   materialization/hit telemetry in `LlvmNativeLowerer.cs`.
2. In `NativeInterpreter.FunctionParts.cs`, I factored projection into the
   shared `ProjectFunctionComponent` implementation, gave ready operands a
   direct value write, and retained the existing pending dependency path. In
   `Tiering/NativeInterpreter.Tiering.cs`, I added the missing direct bridge
   case for that operation.

I also collected the focused frozen-artifact instruction profiles and recorded
commands, work counts, and interpretation in the existing Function-SSA design
note. Root owned builds, AOT publication, the paired timed comparisons, and the
end-to-end acceptance runs. I did not independently build or publish a binary.

## Why I chose exact literal-register reuse

The initial live-policy observation had almost identical hot execution work,
but the candidate emitted 447 additional Constant executions and grew the main
body from 975 slots to 1,140. Its recorded execution allocated more managed
memory and spent more time in register planning. I found that the target-created
Function-leaf helper created a fresh register for every repeated literal, unlike
the neighboring LLVM-constant/type materialization cache.

I chose to reuse the exact same immutable target literal at the same Native
type. That was a narrow extension of existing target materialization ownership,
not a new optimization pass or a rewrite of source evaluation. My intended
benefit was a smaller emitted target graph and less downstream per-slot work.
It was not a claim that Function decomposition itself was free or beneficial in
all workloads.

The unconditional entry-prelude placement was already present. I left it there;
I did **not** introduce it and I did **not** fix or justify its general placement
policy. My cache consolidated existing prelude definitions. It did not address
whether each surviving constant belongs at entry, in a block, or at an actual
use. The cache itself adds a per-body dictionary and equality/hash work during
compilation. It also keeps the inherited function-wide register ownership model;
it is not a liveness-aware placement strategy. I did not quantify those tradeoffs
separately before the first patch.

After the changes, live-policy's main target body was 861 slots/1,210
instructions rather than 975/1,324 in the original control. That structural
comparison is established. The snapshots also contain other integration fixes,
so I cannot assign the entire wall-time improvement from the initial candidate
to this cache alone.

## Why I added the tier projection case

The later Unicode diagnostic was much more specific: 195,000 old
`EnvironmentAddress` executions became 195,000 `FunctionComponent` executions.
The old operation had a direct tier-bridge case; the new one did not. Exactly
194,763 warmed executions changed from direct to fallback bridge accounting.
I treated that as an omitted execution-path integration, not a reason to add an
analysis, change language semantics, or disable the tier.

I used one shared implementation for the regular handler and tier bridge.
Ready values avoid the generic mapping/type-lookup path; pending values retain
the same `MapMachine` dependency, source, and physical result descriptor. I kept
the actual ready/pending and bridge counters. The same artifact and input under
the final AOT restored precisely those 194,763 operations to the direct bridge,
with unchanged Native instruction count and byte-identical output.

That confirms the dispatch mechanism, not an isolated elapsed-time gain. I
changed both bridge integration and the ready helper in the same patch, so the
measurements do not separate their individual costs. The final paired Corpus
results still contain residual slowdowns; I do not claim otherwise.

## What I missed and corrections to the account

My initial investigation prioritized graph size/register-planning evidence.
That was a useful contributor, not a complete explanation of the regression.
I did not establish a quantitative decomposition of elapsed time among register
planning, generic projection, descriptor storage, GC, or other runtime work.
An instruction count or one diagnostic span cannot provide that decomposition.

In the next bounded fix I identified the missing tier case, but I did not also
trace the projection's destination carrier through the subsequent address read.
The later projection review identifies another concrete cost: canonical
`Null | Scope` leaves discard an available exact-address path, causing generic
value encoding on the write and generic decoding on the following pointer read.
I did not author that carrier choice, but I also did not catch that remaining
cost during my ready-path patch. My patch bypasses generic dispatch; it does not
restore the precise source ABI carrier or eliminate all generic storage work.

Accordingly, an account saying that I designed entry hoisting, chose nullable
carriers, or authored the original Function ABI would be incorrect. An account
saying my fixes solved all observed runtime regression would also be incorrect.
The supported account is narrower: I removed duplicated literal materializations
within inherited placement, repaired one missing tier execution path, and
confirmed the exact work-count changes. I should have reviewed the complete
producer-carrier-write-read path alongside dispatch before treating that
projection boundary as sufficiently investigated.
