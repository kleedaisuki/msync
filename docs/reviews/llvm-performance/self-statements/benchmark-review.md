# Benchmark evidence reviewer: first-person account

## My responsibility

I was assigned an independent forensic review of existing performance evidence:
the final standard-scale Corpus five-pair comparison and the separate nine-pair
compile comparisons. I did not design the original experiment, implement its
runner, produce the original measurements, choose the original candidate changes,
or own the integration decisions. I cannot explain those participants' motives
from first-hand knowledge. My responsibility was to assess what the supplied
results establish, where their causal boundaries lie, and which small follow-up
experiment would distinguish useful product explanations.

My original report is `artifacts/diagnostics/llvm-sroa/reviews/benchmark-evidence.md`,
now also retained in this review directory as `benchmark-evidence.md`. The
observations below concern the reviewed frozen cohort, not unmeasured changes
that may have landed afterward.

## What I inherited

I read the assigned BRIEF first. It supplied the control/candidate hashes,
latest accepted measurements, detached-route acceptance receipts, already-fixed
issues, and current leads involving register planning and function projection.
It also constrained me not to build, run new wall-time benchmarks, edit product
code, discard cases, or create timing gates. I treated those as the scope and
starting context, not as proof that any suggested cause explained the timing.

In particular, the brief already warned that profiled SROA and register-allocation
spans were not paired telemetry-off attribution. I retained that distinction.
I did not independently reconstruct the original implementation history or
revalidate every repaired bug.

## What I actually did and why

I inspected the relevant repository contracts and representation/observability
notes, then read the JSON receipts and the Corpus runner. I followed execution
ownership through the CLI and tier runtime. This was necessary because a command
named `run` or an artifact named `bound` does not by itself identify everything
inside a process-level timer.

I recalculated pair deltas, signs, and small order-stratified summaries offline,
retaining every sample. I checked the recorded producer artifact identities and
rehashed both original and frozen executable copies. These checks directly
addressed stale-image and mismatched-estimand explanations without launching
new timed work. I read the independent oracle implementations rather than
assuming two binaries agreeing with one another established correctness.

I used descriptive calculations, not a significance threshold or a timing gate.
Five/nine pairs were enough to expose attribution mistakes and useful leads,
but not to manufacture a precise universal regression estimate. I chose not to
start more timing because that was outside my assignment and would not itself
separate artifact generation from runtime effects. I proposed a frozen-artifact
2x2 crossover for that purpose; I did not execute it.

## What I established

- The reviewed executable hashes matched the brief and their frozen copies.
- The final bound experiment used each binary's own generated artifact, not one
  common artifact. Its observed delta therefore combines artifact generation
  consequences, runtime behavior, and their interaction.
- The bound executable route returns early from contextual binding/preparation.
  It still pays artifact reading, runtime initialization and fresh-process tier
  work. External process warmups do not preserve a later process's tier cache.
- Source and bound were timed in separate blocks. Subtracting their sample
  summaries does not isolate compiler cost. The separate compile command also
  serializes an output object, unlike the in-memory source-run path.
- At the reviewed source state, register planning performed CFG/liveness/pending
  analysis before discovering that no raw-writable eligible region existed.
  This is shared wasted work. Its measured growth in instrumented observations
  was a lead, not a telemetry-off marginal-cost measurement.
- The runner verifies the same independently computed expected results after
  each timed invocation, outside the subprocess timer. The selected workloads
  are not file-transfer output-tree-reset cases.

## My limits and corrections to the narrative

I would correct any description of my work as having produced the benchmark,
proved a hot-loop regression, or measured a specific SROA/projection overhead.
I did none of those. The candidate's positive composite latency results are
worth acting on; that does not make their mechanism uniquely identified.

The numeric nine-pair compile median was only +0.370 ms, with mixed signs.
The midend median was +4.738 ms. Those results do not explain Unicode source
latency by substituting a separately profiled SROA-minus-mem2reg span. Nor can
inclusive nested timings be added to reconstruct an exclusive process delta.

The reviewed timing receipt did not record the inherited `MSYNC_JIT` value.
The runner inherited its environment, while the product default was Auto.
I therefore could not establish the exact policy from that receipt alone. This
is not a finding that the run used the wrong policy, and I did not invalidate
its results on that basis. My proposed follow-up explicitly fixes that variable.

My conclusion about shared unnecessary register analysis is stronger than any
claim about how much of the observed regression it caused. Likewise, aggregate
allocation and equal VM-opcode counts do not identify allocation sites or equal
host CPU work. I did not attribute heap allocations to value structs.

My own report's phrase "actionable end-user latency regressions" should be read
as a prioritization of the observed composite journeys, not statistical proof
that every workload has a stable positive effect of the displayed magnitude.
Numeric bound was particularly mixed. Order-stratified medians and the later
positive midend pairs describe the existing sequence; they do not prove a
scheduler, thermal, or cache mechanism.

I made no production edits, builds, new timings, or commits for this review or
this self-account. I wrote review artifacts and communicated the material
attribution corrections to the integration owner.
