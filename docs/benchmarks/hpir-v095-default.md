# HPIR v0.9.5 default CLI against the published v0.9.3

Linux x64, SDK 10.0.403 Native AOT. The compared binaries were the
published 0.9.3 (`9edc10c0…`) and the final installed 0.9.5 HPIR-only CLI
(`5cf0dbff…`). Each CLI compiled the same frozen O2 sources, bound its
own portable artifacts after removing source, and executed its own bound
image from an unrelated directory. No hidden HPIR target or legacy
execution path was used.

Each row is the existing reference workload, not a rewritten input.
Every execution checked the independently frozen stdout, zero exit
status and empty stderr. On one logical CPU, after one warmup, seven
alternating pairs were timed with recording disabled and binding outside
the timer. CPU is child user plus system time. The change is the median
of paired ratios, not the ratio of two medians.

| Original workload | Published / HPIR CPU p50 (ms) | Paired CPU change | Published / HPIR wall p50 (ms) |
| --- | ---: | ---: | ---: |
| Signal hysteresis | 709.56 / 41.48 | −94.2% | 712.42 / 43.20 |
| Polygon queries | 379.26 / 24.67 | −93.9% | 381.15 / 26.00 |
| Bounded sensor | 134.80 / 42.68 | −69.8% | 137.40 / 45.19 |
| Dynamic policy | 261.36 / 53.21 | −79.5% | 265.34 / 59.48 |
| B+ inventory | 2,745.15 / 106.02 | −96.2% | 2,749.82 / 107.32 |
| Set deduplication | 541.14 / 82.95 | −84.4% | 544.55 / 84.61 |

## Binding and process startup

A separate paired run on the same six workloads and binaries timed
`bind-ir` with the source absent. Each invocation published a new bound
file. The last image from each lane ran once from an unrelated directory,
without source or portable object, and matched the independent oracle.

| Original workload | Published / HPIR bind CPU p50 (ms) | Paired bind CPU change |
| --- | ---: | ---: |
| Signal hysteresis | 26.33 / 14.44 | −46.4% |
| Polygon queries | 27.26 / 14.66 | −46.5% |
| Bounded sensor | 40.71 / 14.37 | −64.7% |
| Dynamic policy | 50.60 / 14.91 | −65.3% |
| B+ inventory | 168.21 / 24.32 | −86.0% |
| Set deduplication | 61.29 / 17.00 | −70.6% |

The fresh-process `--version` control, with filesystem caches warm,
measured 15.55 → 15.24 ms median wall time. The per-workload first-run
samples are in the raw stages receipt; they include startup and
execution, rather than estimating startup by subtracting other runs.

## Interpretation

The earlier [signal and polygon](hpir-unmodified-corpus.md), [live
pipeline](hpir-live-pipelines.md) and [ordered container](hpir-ordered-containers.md)
experiments isolate mechanisms rather than assigning the whole gain to
x64 emission: retained producer observations avoid spurious future work;
selected offsets and entry/environment ABI avoid generic lookup and
frame wrappers; both engines execute the same bound operations and
consume the same input. These timings cover these workloads on this
host, not a performance claim for every script or platform.

The raw pairs, source/input/oracle checksums and
[package release replay](../../artifacts/benchmarks/hpir-v095-release-audit/README.md)
are retained with the [installed and cross-architecture receipt](../../artifacts/validation/hpir-v095-release-audit/README.md).
The [earlier package snapshot](../../artifacts/benchmarks/hpir-v095-final/README.md)
predates the release-specific collection and inspection repairs.
The earlier [source-default capture](../../artifacts/benchmarks/hpir-v095-default/REPLAY.md)
measured the same six workloads before final runtime/source retirement.

## What the x64 JIT contributes

The installed release binary (`5cf0dbff…`) and the exact bound images from
the release comparison were held fixed. Only `MSYNC_JIT` changed. These
are fresh subprocess CPU medians on Linux x64 CPU 6; each mode was
warmed once, then measured in all six execution orders. Every run
checked the frozen output, exit status, and empty stderr. Diagnostic
censuses were collected separately from the timings.

| Existing workload/input | off | auto | force | Auto trigger |
| --- | ---: | ---: | ---: | --- |
| Signal hysteresis, 64 rows | 14.55 ms | 15.36 ms | 14.45 ms | None, about 11.5k guest operations |
| Signal hysteresis, full | 180.33 ms | 31.18 ms | 35.08 ms | Direct call at 131,083 |
| Bounded sensor, full | 38.52 ms | 32.31 ms | 34.26 ms | Backedge at 179,214 |
| B+ inventory, full | 483.26 ms | 96.75 ms | 70.54 ms | Direct call at 1,659,270 |

- **Admission:** Eager B+ execution used 23–38% less CPU than auto in
  all six rounds, although both modes use the same emitter. Admission
  charges against `max(131072, 128 * total HPIR instructions)`; this
  image's 12,963 static instructions count towards the budget, regardless
  of which path is hot. The auto diagnostics emitted 101 functions,
  504,522 bytes, in a 5.88 ms instrumented span. 94 functions recorded
  machine HPIR hits, so selecting just the triggering function need not
  preserve the gain from direct native calls. Full signal used about 82%
  less CPU on auto than off by the median of paired ratios, without a
  consistent advantage for eager emission. The 64-row input is governed
  by startup at this scale; it is not a claim that force is free.
- **Service boundary:** Bounded sensor's forced run crossed back into
  the executor for roughly 30,000 service calls, dominated by
  `text_range.next` and `parse.number` (about 15,000 each). Earlier
  promotion alone showed no consistent advantage. These are counts,
  not a profile of CPU time in parsing versus crossings or input.
- **Code quality:** The emitter is still a baseline instruction
  selector: for example, consecutive floating-point operations each
  read frame operands and write the result back, even when the next
  operation immediately consumes it. Keep the physical stack/heap
  contract; experiment with forwarding or register-resident temporaries
  using producer facts where available. Initialize registers from the
  frame at alternate entries and materialize live state before exits
  that observe the frame. Do not infer safety for an addressable/aliased
  value from its most recent use.
  This is a code-inspection opportunity, not a measured speedup.

Try separating admission from whole-image size first: compare earlier
publication for observed guest hot paths, including their direct callees
where useful, without raising the cost of short CLI runs or adding a
runtime IR. Measure elapsed publication, emitted bytes, machine
coverage and detached execution separately. Then optimize whichever
boundary or code sequence still dominates. [V8 Liftoff](https://v8.dev/blog/liftoff)
is a production example of retaining temporaries in registers and
deferring materialization in a single-pass baseline emitter.
[Xu and Kjolstad's copy-and-patch study](https://arxiv.org/abs/2011.13127)
is evidence for keeping emission latency a separate objective; it is not
a reason to replace the current backend without an emitter bottleneck.

[Raw rounds and census](../../artifacts/benchmarks/hpir-v095-release-audit/jit-modes.json)
and [replay](../../artifacts/benchmarks/hpir-v095-release-audit/README.md#jit-mode-isolation).
