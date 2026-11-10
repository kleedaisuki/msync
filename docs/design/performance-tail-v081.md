# Tail latency: process, session and scan boundaries

## Question and ownership

The first seven-pair Native spatial comparison left two actionable questions:
whether the scalar source-run paired median increase of 3.04 ms is repeatable,
and whether the reference-only control's larger upper tail represents product
work rather than sampling or shared-host variation. Those original observations
remain in [the spatial layout note](object-spatial-layout.md); this investigation
does not replace or selectively discard them.

The workload is a power user's actual CLI/REPL interaction, not a throughput-only
microbenchmark. Compilation, contextual preparation, execution and prompt
rendering have separate ownership. A process-start observation cannot establish
an object-layout mechanism, and a warm managed component cannot establish Native
launch latency. The same distinction applies to allocation traffic, retained
heap, GC pause and operating-system scheduling delay.

## Measurement boundaries

| Workload | Clock interval | Interpretation |
| --- | --- | --- |
| Native `--version` | Child spawn through process exit | Fixed CLI/startup control |
| Scalar source `run -O2` | Child spawn through process exit | Startup, compile, contextual prepare and one small output |
| Matching prebuilt `run-ir FILE.mexe` | Child spawn through process exit | Startup, artifact read and recorded execution; no source compile |
| Reference/numeric source build/scan | Child spawn through process exit | Actual combined Native user journey |
| Frozen managed execute probe | Execute entry through completed result | Construction, scan and selected continuation preparation; not initial compile/bind |
| Ready-gated PTY REPL | Enter through verified result and next rendered prompt | Closed-loop interactive response; typing is outside the clock |
| Queued redirected REPL | Actual enqueue through each verified completion | Burst queue latency, not independent service time |

The Native process is fresh on every sample. The OS is not reset: fixture setup,
functional launches and previous samples warm filesystem and executable caches.
Call this **child-process-cold, OS-cache-warm**, not disk-cold or machine-cold.
Source/seed creation, output deletion and byte verification are outside timing;
actual product output writes remain inside. Prebuilt artifacts are produced by
their matching binary outside timing, with artifact hashes retained.

Readiness-gated interactive input is a legitimate closed-loop workload. It must
not be relabeled as an open arrival-rate test: delayed admission would omit the
queue experienced by prequeued or externally scheduled requests. The
[wrk2 measurement discussion](https://github.com/giltene/wrk2) motivates retaining
the actual enqueue/scheduled-arrival timestamp for the latter. A completion
stream without service-entry markers cannot independently reveal service time.

## Sample design and tail interpretation

`tests/Performance/ScopeSpatial/native_tail.py` compares two frozen Native images
with AB/BA order alternating within paired observations and cases interleaved
across 20 blocks. The initial diagnostic window collects 1,000 pairs for each
light phase and 200 reference-scan pairs. Every output is checked; raw samples,
timestamps, command/source/image/artifact hashes and stdout/stderr are retained.
No outlier removal, cache flushing, timing CI thresholds or replacement batch is
part of the method. Subsequent windows answer a changed causal question or
measure a changed producer; they do not overwrite an unfavorable window.

P95/P99 are observed nearest-rank order statistics, not interpolation between
seven points. At 1,000 observations, P99 is rank 990 and only 11 observations
occupy that rank or above. At 200 it is rank 198 with three observations at or
above; that heavy-case P99 is descriptive and uncertain. The report gives a
binomial order-statistic 95% population-quantile rank interval under an iid
assumption, using unbounded endpoints when the sample cannot bound the quantile.
That interval is a sampling reference, not a claim that adjacent shared-host
observations are independent. Keep block order and paired differences visible;
the difference between two marginal P99s is not the P99 of paired differences.
`analyze_tail.py` additionally resamples whole paired blocks, retaining their
internal ordering and paired producer membership. Its 2,000 deterministic draws
describe finite-window uncertainty in paired medians and marginal P99
differences; they do not remove a shared-host cause or establish stationarity.

Session age and retained exports are input variables, not stationary noise.
Separate fixed-binding updates from growing sessions and report windowed
distributions with their age ranges. Do not pool early and late submissions into
one apparently stationary P99. Startup and first submission stay separately
visible rather than being silently deleted as warmup.

## Attribution and shared resources

One coordinator serializes team measurement windows and excludes builds,
profiling and unrelated functional journeys during those windows. The host can
still contain external services and other users' work. Record host/platform,
load, CPU/memory/I/O pressure and relevant process context rather than claiming an
isolated machine or terminating unrelated work.

The Native harness records per-process child user/system CPU deltas where the
platform supplies `RUSAGE_CHILDREN`; unsupported capabilities are null, not
zero. It records host `/proc` snapshots and load averages at block boundaries
when available. A short host `true` executable control describes launcher cost;
it is not subtracted from product samples. These observations can distinguish
questions worth investigating, but correlation with CPU/load/context switches
does not establish a product-level cause.

[The Tail at Scale](https://research.google/pubs/the-tail-at-scale/) motivates
examining transient shared-resource effects as well as average work. Its
distributed-request techniques do not justify speculative duplicate file I/O in
msync. We first remove demonstrated redundant preparation, allocation or session
work while preserving execution and mutable-state semantics.

Profiling and GC attribution run separately from latency evidence. Managed
diagnostic mirrors can expose paths and allocation sources, but are not Native
latency results. Native AOT supports platform CPU profiling and selected optional
EventPipe facilities; its capabilities differ from the full managed runtime.
Use the actual deployment's supported facilities rather than inferring GC pause
from an elapsed-minus-CPU gap. See the
[official Native AOT diagnostics documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics).

## Reproduction and records

```text
python3 tests/Performance/ScopeSpatial/native_tail.py --calibrate-only --output artifacts/candidates/tail-latency/baseline-5d36d5b/native-diagnostic
python3 tests/Performance/ScopeSpatial/native_tail.py --output artifacts/candidates/tail-latency/baseline-5d36d5b/native-diagnostic
python3 tests/Performance/ScopeSpatial/analyze_tail.py artifacts/candidates/tail-latency/baseline-5d36d5b/native-diagnostic/report.json --output artifacts/candidates/tail-latency/baseline-5d36d5b/native-diagnostic/analysis.json
```

The default images are the accepted contextual backend Native SHA256
`7d1d2856e75631f162bdded1f7258dc7b1d71620afcd2ae28ae42939a08865f4`
and spatial Native SHA256
`54d591a87ab831c07459333d3e06acc39a1164b37beaae0eb83cccfd9df21bfc`.
`--baseline`, `--candidate`, `--work` and `--output` select later frozen producers
and independent record directories. `raw.jsonl` is append-only within a window;
`report.json` contains complete setup, all blocks and descriptive statistics.
`--rows` selects the Scope population. `--cases` admits scalar controls plus
`references`/`references-mexe` and `computed`/`computed-mexe`; the latter use the
same runtime-seeded source for source-run and matching prebuilt-artifact routes.
`--reference-pairs` sets the sample budget for either Scope family. The final
repair comparison uses the spatial image as baseline, rather than adding a
third producer to every workload matrix.

The initial host is Linux x86-64 with 16 available CPUs and Python 3.15.2.
Separate non-team network experiments and persistent Forge/Postgres services
were visible before the diagnostic window. This is shared-host evidence, not
an unloaded dedicated-core benchmark.

## Initial frozen-Native diagnostic window

The single window ran from 2026-11-10 14:09:24 to 14:11:51 UTC. All expected
outputs passed. Columns below give baseline to spatial candidate milliseconds;
the paired delta is the median of candidate-minus-baseline observations.

| Case | Pairs | Median | P95 | P99 | Paired median delta |
| --- | ---: | ---: | ---: | ---: | ---: |
| `--version` | 1,000 | 15.18 → 15.54 | 19.29 → 19.51 | 21.71 → 21.58 | +0.410 |
| Scalar source | 1,000 | 22.20 → 22.59 | 27.31 → 27.37 | 32.03 → 32.23 | +0.375 |
| Scalar `.mexe` | 1,000 | 19.47 → 20.05 | 24.01 → 25.29 | 29.19 → 28.85 | +0.649 |
| Reference Scope, 512 rows | 200 | 63.43 → 61.96 | 72.95 → 73.98 | 77.91 → 76.24 | -0.331 |

The original seven-pair scalar increase of 3.04 ms is not reproduced at that
magnitude. A smaller common-path increase remains visible in source, artifact
and version controls, with median child CPU increases of approximately 0.45,
0.58 and 0.35 ms respectively. This pattern does not isolate a compiler cause,
and does not justify a speculative compiler rewrite. Source/version/artifact
P99 population-quantile reference intervals overlap between the two producers.
The whole-block resampling intervals for paired-median differences are
+0.243–+0.562 ms (`--version`), +0.209–+0.643 ms (source) and +0.423–+0.772 ms
(`.mexe`). Thus a small common-path cost remains consistent across blocks; the
new window does not justify dismissing every observed difference as noise.
An untimed `strace` pair subsequently reports an identical 281-syscall count
vector for both `--version` images, including 56 reads, 28 `openat` calls and
22 mappings. No additional I/O syscall work is observed; the common-path cost
is still not attributed. Commands, image hashes and summaries are retained in
`artifacts/candidates/tail-latency/baseline-5d36d5b/loader-check/report.json`.

The reference median and P99 move down while P95 moves up. Only 105/200 pairs
favor the candidate and the block paired medians have both signs. Its P99 rank
interval is 195–201 for a sample of 200: the upper endpoint exceeds the sample,
so neither image has a finite sample-based upper confidence bound for its
population P99. This window does not establish a stable reference-tail
regression or a universal improvement.
Its paired-median block-resampling interval spans -1.600 to +0.528 ms. All four
cases' resampled marginal-P99 difference intervals include zero.

The host `true` launcher control's median is 1.54 ms across 200 observations;
this cost remains in every product result rather than being subtracted. Full
records and the earlier functional-only calibration are in
`artifacts/candidates/tail-latency/baseline-5d36d5b/native-diagnostic/`.

## Frozen Runtime execute-only result

The Runtime-only comparison for `4199e9d` uses identical Core, HIR, Compiler and
Optimization assemblies and one shared bound artifact. Runtime changes from
`5c9e8a5423ff32b04989aa333b21e731f09af2ea84f23b3707f5f6fe8dd2f5c1` to
`92908c6351bf9fadefa737ae6c6e38f66d8d7795cf9122f2745ce2c7fa44bba1`.
Two persistent managed hosts compile/bind outside timing, warm five executions,
then alternate 32-sample batches (the final batch has eight). Each of 1,000
executions constructs 8,192 fresh reference-only Scopes and scans four times;
the rows share three constant payloads, making this a preparation/negative
numeric-layout control rather than a distinct-filename manifest workload.
Every numeric result is bit-checked outside timing.

| Execute-only metric | Before | Fixed Runtime |
| --- | ---: | ---: |
| P50 ms | 207.92 | 119.24 |
| P95 ms | 252.00 | 162.06 |
| P99 ms | 276.59 | 187.49 |
| Maximum ms | 342.52 | 213.77 |
| Mean allocation bytes/execution | 99,039,918 | 4,656,539 |
| Total Gen0 / Gen1 / Gen2 collections | 11,841 / 284 / 284 | 556 / 445 / 3 |

These collection counts are not pause durations; Gen1 counts rise even as
allocation traffic and observed tails fall. Functional counting independently
locates the change: 8,197 preparations become five, removing 8,192 empty loop
header preparations without changing other execution-step counts. This is not
an isolated physical-packing gain or a Native CLI latency result.

The first batch's coordinator did not explicitly consume both initial READY
messages before sending the first RUN. Candidate warmup could therefore overlap
the first baseline batch; its actual readiness time cannot be recovered from
message-consumption order. Preserve all 1,000 samples as the primary result.
All 32 block paired medians improve, by 77.70–119.11 ms. The baseline's top 11
observations contain no first-batch sample; the candidate's contain four.
The separately labeled after-first-batch sensitivity gives P99 280.95 versus
180.75 ms, without replacing the primary result or rerunning the batch. The
future coordinator now fences both initial READY messages before any RUN.

Exact frozen images, shared artifact, original runner bytes, raw samples,
stdout/stderr and block/tail-rank analysis are retained in
`artifacts/candidates/tail-latency/runtime-4199e9d/references-final/`.
