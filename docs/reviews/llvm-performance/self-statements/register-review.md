# Register-analysis reviewer self-statement

I participated as an independent reviewer, not as the original designer or
implementer of the Function-leaf representation, constant cache, backend, or
register planner. I cannot explain those authors' historical intentions.

My assignment was to review runtime register planning and liveness/pending
analysis, specifically the unconditional work with zero regions and all-false
RawWritable, and explain why it could grow despite fewer Nativeinstructions.
I inherited the frozen-cohort identities, benchmark results, repaired-cause list,
and restrictions from `artifacts/diagnostics/llvm-sroa/reviews/BRIEF.md`. I treated
those measurements as supplied evidence, not as measurements I independently ran.

I traced the actual eligibility and metric boundaries before attributing costs.
This showed that the planner computes CFG, LiveAfter and PendingSlots before
checking whether any instruction can form a region. I then replayed the existing
LiveAfter queue and transfer rules over preserved artifact metadata, counting set
elements instead of timing execution. This inexpensive structural probe directly
addressed the apparent contradiction between fewer instructions and more work.
I inspected definitions and backward-edge members to distinguish long initialization
lifetimes from extra loop propagation, rather than inferring a mechanism from total
opcode counts alone. I also checked upstream LLVM sources as context, not as proof
that a new allocator was needed.

I established a redundant-analysis execution path and deterministic growth in
liveness set occupancy/union/copy inputs: approximately 3x for Unicode and 1.82x
for policy, with fewer queue pops and unchanged backward-edge live-set category
counts. Function code/name constants hoisted into the prelude account for the added
occupancy. My recommended first correction is to gate analyses on actual region
eligibility, not to undo repaired constant reuse.

I did not establish how many milliseconds this mechanism contributes to the
telemetry-off regression, exclusive liveness duration, actual managed bytes
allocated by liveness, or increased PendingSlots sweep counts. The recorded
register-allocation metric covers the whole layout builder. I identified class-
record cloning in PendingSlots but did not prove it grew in the candidate.
No statement from me should be used to claim that the complete observed 47ms
layout time, its 23.6ms increase, or the extra execution allocation is recoverable
by this change.

My probe reimplements the metadata equations in Python rather than executing the
production C# analysis. It covers the opcodes present in the four preserved main
functions, not every possible opcode, and uses relocatable metadata views from the
literal-cache cohort. Its validity depends on those views matching the runtime
Metadata graphs; the later tier-handler repair is not represented as a graph
change. Exact input hashes, counters and member lists are preserved with the probe.
An implementation or snapshot mismatch would require correcting those structural
counts. Set-entry counts are workload measures, not exact CPU instructions or
allocation sizes. The label for a slot without a direct Result definition does
not imply that it is a function parameter.

I made no production changes, builds, wall-time benchmarks, or commits. I wrote
review/probe artifacts only. I have no additional correction to the root's account
unless it presents the structural result as measured millisecond attribution or
as my original design rationale; either would exceed what I established.
