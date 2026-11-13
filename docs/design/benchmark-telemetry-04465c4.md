# Full product benchmark and compiler telemetry: 04465c4

2026-11-13 · Linux x64 Native AOT · absolute current-image snapshot.

## Scope and measurement

- Commit: `04465c4713f7637718d24c48cdb3b316af602507`.
- Native executable SHA-256: `009a035ff4c623f12ba05b0547f404c2c10cb84a7f5a5ca14e202f20990f53a3`.
- The live worktree was concurrently changing. Product sources were exported from the committed revision before publishing; no unfinished backend edits entered this image.
- QEMU Virtual CPU version 2.5+, 16 available vCPUs; all primary benchmark processes pinned to vCPU 12. Host background work was present. These are local VM latency observations, not physical-platform throughput.
- All six maintained groups completed: CLI, local I/O, applications, interactive protocol, realistic editor, and persistent colored-PTY session.
- Applications: standard scale, seeds 17 and 503, O2/default Auto, source/object/bound routes, seven telemetry-off timed samples and one warm-up per combination. All 66 combinations passed their independent stdout/file oracles. There are 462 timed application invocations, separate from warm-ups and profiles.
- The eleven core applications are the maintained product suite. Historical/overlapping diagnostic kernels, WAN/SSH hardware, Windows/ARM hardware, and extended BTree properties are not silently relabeled as part of these results.
- Corpus `baseline` and editor `candidate` labels both refer to this single current image. No historical image or synthetic before/after arm was measured.
- Additional diagnostics-only replays of non-application journeys retain detailed SQLite evidence. Their timing fields are excluded from the performance tables.
- Telemetry: 91 complete SQLite recordings, 1,339,357 records, zero incomplete sessions and zero missing-parent/owner records.

## Telemetry-off application latency

Each cell is the median in seconds for **seed 17 / seed 503**, not a pooled percentile. Source includes compilation and preparation; object begins with portable IR; bound begins with a prepared executable. Route medians must not be subtracted to invent phase costs.

| Application | Standard size | Source, s | Object, s | Bound, s |
| --- | ---: | ---: | ---: | ---: |
| polygon-queries | 16,384 | 3.615 / 3.587 | 3.173 / 3.234 | 3.260 / 3.218 |
| signal-hysteresis | 65,536 | 3.185 / 3.556 | 2.958 / 2.926 | 2.982 / 2.915 |
| numeric-record-stream | 32,768 | 0.812 / 0.754 | 0.638 / 0.715 | 0.709 / 0.630 |
| set-dedup | 8,192 | 15.351 / 15.373 | 5.986 / 6.289 | 5.944 / 5.924 |
| bplus-inventory | 3,072 | 67.774 / 66.943 | 12.474 / 11.912 | 12.128 / 12.388 |
| heap-scheduler | 4,096 | 8.833 / 9.019 | 3.926 / 3.964 | 4.012 / 3.973 |
| bounded-sensor-pipeline | 12,000 | 0.884 / 0.869 | 0.232 / 0.269 | 0.263 / 0.247 |
| live-policy-dispatch | 28,000 | 1.097 / 1.100 | 0.435 / 0.432 | 0.423 / 0.449 |
| unicode-path-manifest | 65,000 | 4.259 / 4.489 | 3.075 / 3.159 | 3.087 / 3.181 |
| local-manifest-copy | 144 | 0.431 / 0.433 | 0.219 / 0.229 | 0.214 / 0.233 |
| local-range-copy | 144 | 0.764 / 0.767 | 0.227 / 0.234 | 0.247 / 0.258 |

## Other maintained journeys

CLI/I/O medians and empirical P95 are milliseconds. Files were verified outside timing; ordinary replacement used a 64 MiB payload, not a claim about storage bandwidth.

| Group | Journey | P50, ms | P95, ms |
| --- | --- | ---: | ---: |
| cli | startup | 14.225 | 22.186 |
| cli | check-small | 16.516 | 27.471 |
| cli | check-batch | 74.200 | 86.696 |
| cli | copy-file-source | 22.141 | 25.619 |
| cli | copy-file-object | 19.476 | 22.280 |
| cli | copy-tree-source | 359.036 | 395.903 |
| cli | copy-tree-object | 336.921 | 362.013 |
| io | copy-file-source | 127.955 | 147.529 |
| io | copy-file-object | 133.662 | 142.228 |
| interactive | repl-copy | 19.166 | 27.592 |
| interactive | repl-scalar | 12.404 | 23.841 |
| interactive | lsp-initialize | 1013.818 | 1017.445 |
| interactive | lsp-hover | 0.396 | 0.479 |
| interactive | lsp-tokens | 0.872 | 1.118 |
| interactive | lsp-edit | 11.588 | 14.432 |
| interactive | lsp-delta | 4.087 | 8.674 |

Editor observations use fresh LSP sessions; initialization excludes process spawn in that tool. Warm request controls are not proxies for actual edit latency.

| Editor scenario | Metric | P50, ms | P95, ms |
| --- | --- | ---: | ---: |
| realistic | cold_standard_completion_ms | 4.481 | 5.814 |
| realistic | first_resolve_ms | 0.426 | 0.587 |
| realistic | typed_hover_ms | 0.381 | 0.681 |
| realistic | first_tokens_ms | 1.098 | 1.659 |
| realistic | repeated_tokens_ms | 0.215 | 0.373 |
| realistic | ordinary_completion_ms | 1.197 | 1.713 |
| realistic | selected_resolve_ms | 0.288 | 0.413 |
| flat400 | initialize_ms | 1014.092 | 1016.995 |
| flat400 | open_hover_ms | 45.339 | 60.617 |
| flat400 | semantic_tokens_ms | 5.049 | 6.046 |
| flat400 | repeated_semantic_tokens_ms | 1.865 | 2.164 |
| flat400 | completion_after_burst_ms | 54.647 | 85.027 |
| flat400 | whole_burst_ms | 74.480 | 100.380 |
| flat400 | selected_completion_resolve_ms | 0.413 | 0.471 |

### Persistent session age

Enter through verified result and next rendered colored prompt. Typing/render work is measured separately; growing exports are nonstationary and are not pooled into a tail claim.

| Submissions | Fixed exports P50, ms | Growing exports P50, ms |
| --- | ---: | ---: |
| 1–64 | 20.732 | 30.555 |
| 65–128 | 21.229 | 41.507 |
| 129–192 | 23.847 | 57.767 |
| 193–256 | 20.114 | 61.638 |

Fixed state stays near 20–24 ms; retained growing state rises from 30.55 to 61.64 ms across these age windows. This is a real age effect in this workload, not evidence of a universal asymptotic bound.

## Compiler evidence from separate source profiles

The following are **telemetry-on** inclusive phase intervals for seed 17, in milliseconds. The five coarse compilation phases are disjoint siblings. Nested pass intervals and aggregate pass events must not be added to parent intervals.

| Application | Compile | Analyze | Lower | Optimize | Escape pass calls | Explicit escape pass time |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| polygon-queries | 366.35 | 8.18 | 1.73 | 348.29 | 130 | 222.76 |
| signal-hysteresis | 391.43 | 7.24 | 1.41 | 379.05 | 67 | 248.03 |
| numeric-record-stream | 177.94 | 4.69 | 0.97 | 168.77 | 28 | 95.90 |
| set-dedup | 11681.38 | 836.47 | 69.66 | 10753.24 | 1,212 | 7752.15 |
| bplus-inventory | 60911.37 | 3254.17 | 565.58 | 57058.80 | 1,917 | 43023.10 |
| heap-scheduler | 5573.40 | 429.97 | 24.08 | 5099.42 | 380 | 3601.17 |
| bounded-sensor-pipeline | 811.08 | 11.61 | 3.90 | 786.88 | 33 | 549.99 |
| live-policy-dispatch | 800.45 | 9.13 | 4.20 | 775.86 | 31 | 498.69 |
| unicode-path-manifest | 1250.18 | 15.11 | 9.98 | 1213.92 | 31 | 764.80 |
| local-manifest-copy | 253.13 | 5.20 | 2.26 | 240.51 | 10 | 133.84 |
| local-range-copy | 742.22 | 18.15 | 6.15 | 701.27 | 30 | 354.95 |

Optimization accounts for 91.5–97.1% of the compile interval in these eleven source profiles. Set and B+ compilation are not parser-bound. The prior Set timeout does not recur in this snapshot, but Set still has approximately 15-second source latency and B+ approximately 67-second source latency.

### Concrete mechanism and next experiment

- In `NativeOptimizer.OptimizeFunction`, each fixed-point iteration constructs escape analysis twice, followed by a final construction. B+ records 806 local-value iterations across optimized functions and 1,917 explicit escape analyses; Set records 514 and 1,212. These counts reflect the implemented schedule, not 1,917 independent changes.
- `NativeEscapeAnalysis` constructs a new `NativeAddressAnalysis.ProgramFacts` when no owner is supplied. Consequently a per-function pass may repeatedly rebuild interprocedural producer facts. The explicit escape timer includes those proofs; it is not only the final reachability walk.
- `NativeRegisterAllocation.Run` reads one unchanged input program and builds a separate output function array, yet `Allocate` constructs escape analysis without supplying shared facts. This is a concrete first reuse boundary: pass one program-owned facts object through the register-allocation traversal. The existing inlining builder already demonstrates that ownership pattern.
- B+ spends 6.35 s in the virtual-slot pass and 6.13 s in inlining in this profile, in addition to the 43.02 s explicit escape passes. Those pass totals include other work and are not promises of removable cost.
- For the mutating fixed-point loop, reuse requires explicit IR-version/invalidation ownership. Never keep a graph across in-place instruction edits merely because the `NativeProgram` object identity is unchanged.

This direction follows the analysis-preservation/invalidation discipline described in [LLVM’s New Pass Manager](https://llvm.org/docs/NewPassManager.html). [Incrementalizing Production CodeQL Analyses](https://arxiv.org/abs/2308.09660) also motivates measuring reuse potential before adopting a general incremental solver; it does not establish a speedup for msync.

## VM work and storage interpretation

Seed 17 source profiles. These are logical IR operation attempts and actual function entries, not hardware instructions or necessarily one host dispatch per IR operation.

| Application | Logical IR operations | Function entries | Logical allocations | Logical storage bytes | Execute managed allocation, MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| polygon-queries | 13,215,091 | 393,222 | 196,637 | 6,292,572 | 645.01 |
| signal-hysteresis | 12,446,171 | 196,620 | 53 | 1,383 | 416.58 |
| numeric-record-stream | 2,659,090 | 32,773 | 32,797 | 787,588 | 64.87 |
| set-dedup | 17,726,059 | 516,483 | 6,069 | 505,579 | 1201.63 |
| bplus-inventory | 24,761,724 | 734,962 | 28,336 | 1,752,045 | 2565.54 |
| heap-scheduler | 13,741,220 | 206,284 | 13,288 | 715,193 | 686.14 |

- Polygon executes 196,608 `<range-item>` constructor entries and 196,608 `crossed` entries. Its address/load/store family accounts for 58.23% of logical IR attempts. That is **not** a CPU-time share: the shared execution image already fuses some FrameAddress+Load/Store pairs while preserving logical counts.
- Signal has only 53 logical IR allocations but 196,620 function entries and 416.58 MiB managed allocation in its profiled execute interval. The logical-heap counter excludes activation-frame construction and other host allocations. It must not be interpreted as total GC allocation.
- Managed-allocation intervals are process-wide and include observer work. They suggest measuring short-call/Range-thunk frames and value transport, not assigning every allocated byte to guest Scope objects.
- Guest JIT compilation/entry counters are zero under the default Auto policy. Native AOT is the host deployment format; these results do not establish guest-JIT profitability.
- For the same seed, function-entry counts agree across all three routes in all 22 application inputs. Logical IR counts agree in 21/22 inputs; Heap seed 17 source has 2,086 extra attempts (1,043 Narrow+Move pairs in `_heap_link`) versus its separately compiled artifacts, approximately 0.015%. Fresh-process compilation can differ in layout/optimization decisions; causal runtime comparisons should freeze one artifact rather than assume source recompilation is bit-identical.

Boundary validation is not the leading cost in the measured collection runs: the Set bound profile spends 48.71 ms in native verification versus 7,361.14 ms in execute; B+ spends 75.58 ms versus 14,441.83 ms. Removing source diagnostics or weakening artifact admission is not the supported first optimization.

## LSP startup queue: a separate actionable finding

The single-vCPU primary suite reports approximately 1,014 ms initialization. A diagnostics replay records 1,000.49 ms `lsp.queue_ns`, but only 1.115 ms dispatch-to-reply for `initialize`.

A separate telemetry-off diagnostic kept product bytes and request/oracle unchanged, using three measured samples and one warm-up:

| Available vCPUs | DOTNET_PROCESSOR_COUNT | Initialize P50, ms |
| --- | --- | ---: |
| [12] | unset | 1015.757 |
| [12, 13] | unset | 14.362 |
| [12] | 2 | 12.694 |

The one-vCPU/count=2 condition removes the delay without providing a second execution CPU. Together with the queue interval and `LanguageServer.RunAsync` scheduling a permanently blocking `ReadMessages` loop through `Task.Run`, this strongly supports initial thread-pool starvation rather than expensive semantic initialization. The source fix to test is a dedicated blocking-read thread or genuinely asynchronous transport, not a global processor-count/MinThreads workaround. See [Microsoft’s starvation diagnosis](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation). No product code was changed for this experiment.

## Observation cost and correct use of detailed records

The diagnostics-only fixed-session replay has Enter-to-prompt P50 of 455.70–491.66 ms by age window, versus 20.11–23.85 ms in the telemetry-off suite. Growing diagnostics windows are 461.52–559.33 ms. These are matched journey shapes but separate recording runs; they are not mixed with production timings or presented as a balanced overhead experiment.

Full per-function/pass/frame records and lossless writer admission are expensive for repeated small submissions. Preserve this observability, use its work counts and ownership to explain changes, and optimize batching/serialization separately if this mode needs lower disruption. Do not hide recording loss or call diagnostics-on timings user latency.

## Reproduction and retained evidence

- Primary receipt: `artifacts/benchmarks/compiler-telemetry-04465c4/summary.json`.
- Raw per-group timing reports and commands: sibling `receipts/`; flattened timings: `timings.csv`.
- Native image and SQLite sidecar: sibling `image/`.
- Raw recordings and retained corpus evidence are archived in `dist/msync-04465c4-benchmark-evidence.tar.gz`; `evidence-manifest.json` records member hashes. The live working copy remains in `.temp/benchmarks/full-04465c4-20261113/`.
- Exact template and session extraction queries: sibling `template-query.sql` and `session-query.sql`.

Representative invocation (the completed run used independent workload invocations so a failure would not prevent later groups):

```sh
python3 tests/Performance/run.py --binary PATH_TO_FROZEN_NATIVE_MSYNC \
  --samples 7 --warmups 1 --seed 17 --seed 503 --scale standard \
  --route all --profile instructions --jit auto --cpu 12 \
  --work .temp/benchmarks/NEW_EMPTY_DIRECTORY
```

The five non-application diagnostic replays used the same harness with a typed experimental exec launcher that removed `--no-telemetry` and enabled `diagnostics`. Their child method labels are not authoritative about recording policy; the enclosing telemetry-only receipt explicitly records this override and excludes all replay times from benchmark scores.
