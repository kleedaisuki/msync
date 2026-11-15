# Pipeline analysis reviewer: personal account

## My assignment and ownership

I participated as an independent reviewer of an already implemented compiler
pipeline and LLVM-to-Native boundary. I did not design the original pipeline,
choose SROA or Function leaf SSA, implement the cohort, or produce its benchmark
runs. I cannot describe the original authors' intentions as my own.

Root assigned me analysis lifetimes/recomputation, whole-graph scans, default
pass integration, and target-boundary choices, explicitly excluding SROA internals
owned by another reviewer. I was told not to edit product code, build, launch new
timing runs, or spawn more agents. My review is retained in
[`../pipeline-analyses.md`](../pipeline-analyses.md). This follow-up only writes
this account; I have made no product change, build, timing run, or commit.

## Context and working assumptions

I first read the supplied review brief, then the relevant internal contracts and
LLVM design/port notes. The brief supplied the frozen control/candidate identities,
application regressions and telemetry-off compile results. In particular, the
midend compile regression was +4.738 ms median paired, not the difference between
separate detailed optimizer spans. I treated these as supplied measurements,
not measurements I generated. The brief also identified already repaired issues,
which I did not reopen as current defects.

My initial question was whether the replacement scalarization or representation
had made the surrounding compiler construct expensive analyses more often, or
throw away usable state. I did not assume that this was the explanation. I also
kept baseline shared waste separate from new incremental work: removing a shared
cost can help the product without explaining this cohort's regression.

## Why I looked at phase counts and upstream preservation

I aggregated the existing old/new JSONL records by span name and parent, then
compared their work counters. Construction counts are useful here because they
can distinguish an additional pipeline phase from increased work inside the
same phases without adding overlapping durations. I read the executable caller
paths alongside the records to determine which branches actually ran.

I compared the implementation with the locally vendored pinned LLVM sources,
particularly SROA, ADCE, InstSimplify and PassBuilderPipelines. My purpose was to
check specific analysis-preservation obligations and existing integration seams,
not to treat upstream's complete pipeline as a benchmark result for this custom
Native backend. This review used those local primary sources; I did not perform
a fresh external literature search or claim a broader research survey.

## What I personally established

Both recordings construct 132 dominator analyses, 71 MemorySSA instances, 33
LoopInfo and 33 CycleInfo instances. The candidate therefore does not exhibit
an analysis-construction count explosion in this journey. Its MemorySSA scans
increase from 4,159 to 4,350 instructions, while block counts and the counted
memory definitions/uses remain unchanged. This establishes additional scalar
scan work, not its elapsed-time contribution.

I found a concrete lifetime inefficiency shared by both cohorts: InstSimplify
constructs a fresh post-SCCP forward dominator tree, the caller does not retain
it, and DSE reconstructs one after ADCE. The existing ADCE API can accept and
update the forward tree, matching the pinned upstream preservation behavior.
The 33 DSE-owned constructions total about 0.303 ms in the candidate detailed
recording, so I did not present this as the principal +4.738 ms cause. The
suggested 132-to-99 construction reduction is an expected structural result of
a future change, not an implemented or measured result.

I also found an unnecessary caller rebuild when SROA changes CFG: SROA already
flushes its dominator updater, and upstream preserves that tree even when CFG
changes. But this branch ran zero times across the 33 candidate functions. It
is a future input-specific integration cost, not a cost paid in this profile.

Finally, I identified the known missing full InstCombine integration and the
extra 121 instructions entering EarlyCSE as a focused next inspection target.
I did not identify a specific missing combine rule responsible for them. Native
target emission was faster in the supplied detailed observation and emitted
fewer instructions/slots, despite allocating more bytes. That made it a weaker
supported compile-regression lead in this evidence, not an eliminated cause.

## Limits, omissions and wording root should preserve or correct

- I did not implement or execute the dominator reuse proposal. In particular,
  its CFG-changing ADCE path still needs a focused real journey before delivery.
  The existing APIs and source contract support the proposal, not a speedup claim.
- Equal MemorySSA definition/use counts mean equal counted graph sizes, not
  identical graphs, alias-query behavior, or analysis cost. My short coordination
  message saying there was no new memory graph/AA workload was too broad if read
  as ruling out such changes. Root should retain the narrower counter result.
- The target's 7.524-to-3.995 ms observations are unpaired and detail-enabled.
  They do not prove target emission cannot contribute to telemetry-off latency.
  Likewise, SROA's 31.098 ms inclusive profile does not quantify its exclusive
  contribution to +4.738 ms or prove it accounts for that whole regression.
- I inspected current producer code and historical execution records; I did not
  reconstruct and independently validate every old producer revision. Exact
  line numbers describe the reviewed snapshot, not a permanent source location.
- I did not trace the 121 extra EarlyCSE-input instructions back to individual
  source constructs or prove an upstream InstCombine rule removes them. That
  remains the important missing step before recommending a specific new port.
- I did not diagnose application runtime regressions. I noted all-false
  RawWritable and rejected speculative GC-leak claims, but detailed register
  analysis, runtime carriers and SROA internal costs belonged to other reviewers.
- My review ranking orders actionable observations within my remit; it is not a
  ranking of measured contributions to the total regression. The shared DT
  opportunity belongs below any independently demonstrated larger new cause.

My contribution was to narrow the causal story and identify a small existing-API
repair, not to justify the original design or claim the performance problem was
resolved. Root should distinguish my supplied evidence, structural findings,
proposed changes and unmeasured expectations in the integrated account.
