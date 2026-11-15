# Personal decision and responsibility statements

The user requested personal accounts from the coordinator and participating
agents. Each contributor wrote its own statement, identifying actual authorship,
assignment, high-level rationale, missed costs and limits. The root statement
owns integration and direction; later fixers/reviewers are not substituted for
original designers. These accounts are not transcripts of private deliberation
and do not reconstruct another contributor's unrecorded motives.

## Coordinator

- [Root integration owner](root.md)

## Earlier implementation and architecture contributors

| Contributor | Account / actual remit |
| --- | --- |
| aggregate_memory | [Initial target/session/emitter and SROA central driver](aggregate-memory.md) |
| native_scalar_abi | [Original internal machine-scalar architecture proposal](native-scalar-abi.md) |
| direct_call_ir | [Early HIR-to-LLVM source Function/closure/call producer](direct-call-ir.md) |
| abi_annotations | [ABI use metadata, pointer/GEP lowering and shared utilities](abi-annotations.md) |
| builder_publication | [TargetFolder/Builder publication and shared floating/vector interfaces](builder-publication.md) |
| cfg_comparisons | [CFG comparisons and staged Boolean-comparison families](cfg-comparisons.md) |
| cfg_hoist | [CFG hoisting and SROA aggregate/PHI/select work](cfg-hoist.md) |
| cfg_local | [CFG tools and SROA pointer/slice traversal](cfg-local.md) |
| code_metrics | [CodeMetrics, attributes and builder/query prerequisites](code-metrics.md) |

## Later integration repair contributors

| Contributor | Account / actual remit |
| --- | --- |
| benchmark_integration | [Benchmark preparation and actual-carrier PHI-copy repair](benchmark-integration.md) |
| fix_function_environment | [Carrier selection and actual capture-storage schema repair](function-environment.md) |
| function_perf | [Literal register reuse and missing tier-direct integration](function-perf.md) |

The benchmark integration statement explicitly corrects its initial stale
present-tense claim that final AOT results did not exist. They did exist and
were executed by root; lack of received results at that worker's earlier handoff
was not lack of project results.

## Independent reviewers (not original design authors)

| Contributor | Account |
| --- | --- |
| review_register_analysis | [Register/liveness structural audit](register-review.md) |
| review_function_projection | [Function projection/carrier audit](projection-review.md) |
| review_scalar_storage | [Ordinary storage and machine-path audit](storage-review.md) |
| review_frames_gc | [Frames, reuse, allocation and retention audit](frames-review.md) |
| review_tier_dispatch | [Tier dispatch/call classification audit](dispatch-review.md) |
| review_sroa_cost | [Pinned SROA source/cost-model audit](sroa-review.md) |
| review_pipeline_analyses | [Analysis lifetime and preservation audit](pipeline-review.md) |
| review_benchmark_evidence | [Benchmark boundary and inference audit](benchmark-review.md) |

## Attribution boundaries

These are21 personal statements: root plus20 contributors. They are not a
complete recovered authorship map of every line ever changed. In particular,
the original later Function-leaf implementation and production MarkInitialized
authors were not positively identified merely from another participant's task
name. The machine-scalar architect does acknowledge its own overbroad initial
readiness recommendation, while distinguishing it from production authorship.

No product code, builds, new performance measurements or Git commits/pushes were
performed for these statements. The source/measurement review remains in the
[parent bundle](../README.md). Corrections distinguish source-level extra work,
structural replay, individual profiled observations and actually paired timing.
