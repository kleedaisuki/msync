# Performance tools

The maintained product suite is [BENCHMARK.md](../../BENCHMARK.md):

```sh
python3 tests/Performance/run.py --list
python3 tests/Performance/run.py --binary PATH_TO_MSYNC
```

Defaults include CLI, I/O scenarios, application distributions, persistent
requests, editor workflows and colored terminal session age. The former
independent-process throughput sweep is opt-in only. Explicit `--group` selection runs only the chosen
groups. One image is a
valid snapshot; add `--baseline` for a comparison. Raw child reports retain their
own timing boundaries. Start with the affected group, not every tool below.

The [I/O group](../../BENCHMARK.md#io-scenarios-and-distributions) uses
`sync_journey.py` to keep first deployment and redeployment separate across
three distributions. Local is the default; SSH upload/download are explicit.

## Focused diagnostics

These answer narrower questions; they are not additional votes in a universal
performance score. Existing command-line interfaces remain usable.

| Question | Tool / details |
| --- | --- |
| Fresh CLI, small/large file and tree copy | [`journeys.py`](journeys.py), `--binary [--baseline]`; legacy paired/triple-image options remain. Every copy sample varies inputs and seeds incorrect existing destinations outside timing. |
| Persistent protocol latency, actual edits and token delta | [`interactive.py`](interactive.py), `--binary [--baseline]`, `--case`; warm requests are controls, not editing proxies. |
| First library query, completion, edit bursts and document size | [`editor_latency.py`](editor_latency.py), `--binary [--baseline]`; `realistic`/`flat400` default, `flat80`/`flat128`/`flat4000` explicit. |
| Actual prompt readiness versus queued results | [`repl_tail.py`](repl_tail.py), `--mode prompt|burst`, `--case fixed|growing`; never pool session ages or relabel burst delay as service time. |
| Mixed application behavior and independent oracles | [Corpus](Corpus/README.md); Six core workloads with two distributions each; explicit diagnostics, source/object/bound routes and optional profiling. |
| Independent-process saturation (diagnostic only) | `run.py --group throughput`, repeated `--concurrency`, `--requests`; Linux 12 CPU / 24 GiB scope, isolated worker paths and separate lossless profiles. [Contract and queries](../../docs/design/benchmark-throughput-v090.md). |
| Explain one exact fixture | [`profile_compare.py`](profile_compare.py): telemetry-off source/bound timing plus separate SQLite. `--same-artifact` isolates runtime; matched compiler options isolate compilation. |
| Pure Set/Heap/hash/SHA kernels and other languages | [LibraryMatrix](LibraryMatrix/README.md); diagnostic kernels, independent results, runtime warmup and algorithm/layout caveats. Not a general language ranking. |
| Current guest tiers, guards, continuations and nonhits | [NativeTiering](../NativeTiering/README.md), `tests/Telemetry/tiering.py`; unified runner `--jit off|linked|force` and separate profiles. [NativeJit](NativeJit/README.md) and [NativeCallbacks](NativeCallbacks/README.md) describe retired backend experiments. |
| Compiler stages and standard-library demand | [HirCompilation](HirCompilation/README.md), [StdlibLoad](StdlibLoad/README.md), `optimizer_pipeline.py`, `stdlib_load.py`, `stdlib_expansion.py`. |
| Planning, scheduling, real selected copies | [ContextualIo](ContextualIo/README.md), `scheduler.py`, [SchedulingComponents](SchedulingComponents/README.md). Source-overwrite elimination is useful work elimination, not bytes-per-second improvement. |
| Storage placement, boxes, frames and retained ownership | [ScopeSpatial](ScopeSpatial/README.md), [NumberExecution](NumberExecution/README.md), [ObjectAllocations](ObjectAllocations/README.md), [ReplSession](ReplSession/README.md). |
| Editor and primitive component attribution | [HirEditor](HirEditor/README.md), [Components](Components/README.md), `source_read.py`, `namespace_publication.py`. |
| Native compiler preference | `aot_preferences.py`: frozen-revision Size/Speed/blended experiment, not a release gate. |

`AdaptiveDispatch`, `AdaptiveFusion`, `AdaptiveInlineCache` and `AdaptiveReview`
are historical frozen-source experiments (the last is semantic review, not a
speed benchmark). Their local READMEs identify the exact revision/overlay and
accepted or rejected conclusions. They are excluded from the product suite;
do not run an old overlay against HEAD and label it a current result.

Keep reproducible findings in the relevant English design note, with raw
receipts in `artifacts/benchmarks` when worth retaining. Generated fixtures,
logs and SQLite belong under repository-root `.temp`. No product dependency
or timing CI threshold is introduced by these tools.
