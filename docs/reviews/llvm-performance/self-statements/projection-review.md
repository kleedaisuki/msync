# Function projection reviewer: personal account

## My role and ownership

I participated as an independent reviewer after the Function leaf SSA design,
implementation, correctness repairs, literal reuse and tier-direct repair
already existed. I did not design the original Function SSA representation,
choose its introduction, implement those repairs, or run the performance
comparison. I cannot supply the original authors' motives as my own.

My assignment was to inspect Function code/environment/name carriers, the
Native ABI boundary, nullable movement, and projection/composition work. Root
explicitly excluded production edits, builds, new timing runs and further
delegation. My output was the review now retained as
[`../function-projection.md`](../function-projection.md). In this follow-up I
have only written this personal account; I made no product change or commit.

## Context I inherited

I received the shared brief before investigating. It supplied the frozen
control/candidate identities and final standard-scale paired results: bound
numeric +1.71%, policy +1.70%, Unicode +3.97%; source +3.66%, +5.01%, +7.69%.
It also identified repaired bugs and a specific lead: new ready projection
used generic Frame.GetDatum/Set where the previous environment path used
SetPointer. These measurements and the history of repairs were supplied
evidence, not observations I generated.

I treated the documented language obligations as constraints: captured
locations remain shared, pending projection must not force a value, and
existing whole-Function ABI values remain valid. The design note says leaf SSA
is intended to represent LLVM aggregates without allocating a runtime tuple.
That explains the published design, but is not a design decision I personally
made. I did not assume that generic structs imply managed allocation or that
unchanged instruction counts imply unchanged execution cost.

## My investigation choices and contribution

I chose to connect actual emitted sites to their immediate consumers rather
than assess the new opcodes in isolation. A wrapper may look more expensive in
source without being important in a workload; conversely its output carrier
can make the following operation more expensive. I inspected target lowering,
frame addressing, memory read/write implementations, rendered artifacts and
the existing per-PC instruction counts together.

That exposed the strongest finding I personally established: the three hot
Unicode environment projections produce `Null | Scope`, whereas the previous
three EnvironmentAddress instructions produced monomorphic Scope/layout7.
Each site executes 65,000 times and feeds AddressOffset immediately. The new
nullable carrier therefore not only uses generic projection output writing;
the following Frame.Pointer also misses LoadAddressDirect and reconstructs an
address through GetDatum/AsAddress. All 195,000 projections are ready in the
supplied final diagnostic observation.

I also checked the alternative explanation of repeated Function composition.
Unicode executes only 180 compositions, policy 164, matching the previous
MakeFunction counts, and policy executes no FunctionComponent. I therefore
excluded repeated per-record composition as an explanation for these measured
loops. I did not propose a cross-branch composition cache.

My recommendation was a narrow comparison separating generic ready-path cost
from loss of the exact-address carrier. Existing source-call lowering already
attaches concrete environment logical metadata; I suggested checking its
survival and preserving that established fact rather than adding a new
nonnull/alias solver. I did not implement or verify that remedy. In particular,
a raw address write into the current nullable storage is not valid because
the tag and payload offset differ.

## Corrections and limits I want retained in the integrated narrative

The source Function GetDatum read occurs in both the old and new paths; it is
not entirely new work. The extra downstream generic address read is the more
specific observation my review added to the initial lead.

The nullable carrier is not intrinsically incorrect: genuine nullable
PHIs/selects need it. My finding concerns lost precision at established typed
boundaries, not a general instruction to remove null tags. Whether the exact
needed metadata survives SROA remains an implementation question.

The tier omission was already repaired. My finding must not be described as
another missing direct handler. Likewise, I found additional executed work,
not a measured explanation for all of Unicode's +3.97%, and not an explanation
for policy's regression. No new timings isolate its marginal cost.

NativeEnvironment.View does construct a managed wrapper, but not capture
storage; the previous capturing MakeFunction also constructed an environment.
Neither that constructor nor NativeDatum/NativeValue struct movement establishes
an allocation regression. I did not attribute the diagnostic allocation delta
to this path.

I have no personal basis to say that the original design was motivated by
premature optimization, a deliberate tradeoff against application performance,
or disregard for compatibility. My account supports a narrower conclusion:
the reviewed target/runtime boundary has a concrete precision-related cost
worth isolating, and the existing benchmark evidence does not yet quantify
its contribution. The broader architecture's success or failure is outside
what my bounded review established.
