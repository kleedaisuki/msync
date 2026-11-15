# Dispatch reviewer self-statement

## My role and received task

I was an independent performance reviewer, not the original designer or implementer. Root assigned me tier machine-code/bridge dispatch, compiled function classification, call/return, and suspension/execution handoffs. Root specifically excluded the Function projection body ABI audit because another reviewer owned it. I was instructed to read the shared brief first, use existing measurements, and make no production changes, builds, timing runs, or subagent delegations.

## Starting context and assumptions

I treated `artifacts/diagnostics/llvm-sroa/reviews/BRIEF.md` as the supplied cohort and measurement context. In particular, I accepted that the missing FunctionComponent tier-direct branch had already been repaired and that the final telemetry-OFF paired bound results still showed Unicode +3.97% and policy +1.70%. I did not independently reproduce the binary hashes or timed results. I kept the policy diagnostic's literal-cache cohort distinct from the later Unicode tier repair.

## What I actually did and why

I read the development contracts and the openings of the relevant Function SSA, SROA, LLVM representation, and observability design notes. I then traced the current backend classification, execution linking, direct versus guest-loop call selection, activation/work ownership, tier bridge, tier workspace pooling, demand handling, and observed/unobserved memory flush paths.

I parsed existing JSONL records with inline Python commands to compare execute metrics, per-function work, and native-call counts. I inspected the exported helper instructions to check whether equal operation counts hid changed helper bodies. I used source line references and existing diffs to distinguish new changes from shared mechanisms. I wrote the resulting review to `artifacts/diagnostics/llvm-sroa/reviews/tier-dispatch.md` and sent root progress and completion summaries.

I chose these methods because the concrete question was whether changed dispatch, classification, or handoffs explained a measured regression. Comparing executable-path counts and tracing the conditions selecting those paths could reject several hypotheses without adding noisy overlapping benchmarks.

## What I established

- Final Unicode bridge-direct/fallback counts match control exactly: 1,646,249 / 584,301. The previously missing projection branch is not a remaining unfixed issue.
- Unicode guest pushes/resumes remain 195,000 each; each of the three path helpers still executes 65,000 calls and 325,000 instructions with identical exported bodies.
- Policy retains 27,752 direct/context-free leaf calls, the same helper returns, and the same 12,254 pooled tier workspace leases. Hot native-call counts agree across cohorts.
- Register-region planning performs CFG, LiveAfter, and PendingSlots work before eligibility checks, although the current standard backend's all-false RawWritable contract prevents any eligible register region. This is confirmed shared wasted work. The candidate's larger single-observation register-allocation spans make it a useful next experiment, not a complete latency attribution.
- Full FlushEntries scanning and hit clearing are recording-only. Those specific mechanisms cannot explain the telemetry-OFF paired slowdown.

## What remains unknown and limits of my conclusions

I did not establish the exact contribution of register planning, projection execution, allocation, or completion-state differences to telemetry-OFF elapsed time. Equal dispatch counts do not prove equal per-operation cost. Profiled duration and allocation deltas do not identify individual causes, and I did not sum nested spans.

The additional tier entries accompanied additional suspensions, but the demand path checks whether completion is still incomplete. I therefore did not infer a deterministic new scheduling workload from those observation-dependent counts. I did not audit the projection body's representation costs, benchmark a proposed fix, or verify the full compiler migration.

## Execution limitations and corrections

My first profile extraction printed too many fields and was truncated. I narrowed the next extraction to the relevant metrics and native-call records; the concise comparison informed the report. A filename glob for TierInvocation did not match because the nested type lives in NativeInterpreter.Tiering.cs; I subsequently located and inspected it there. These were investigation inefficiencies, not production changes.

Root supplied the already-repaired status and cohort distinctions in the task and brief; I incorporated them from the outset. I received no subsequent substantive correction to this review before writing this statement. I cannot speak for the original implementation choices or the designers' motives.

## My recommendation

Test the narrow no-eligible-region analysis skip first, preserving raw-write semantics and useful telemetry. Do not relax purity/context classification or rewrite the dispatcher on the evidence I inspected. Treat unchanged wrapper handoffs and pooled workspace leases as shared optimization opportunities rather than proven regression causes.
