# Persistent REPL tail probes

`../repl_tail.py` observes the actual Native product. The `prompt` mode uses the
existing real Linux PTY journey with normal colors, lexical highlighting and
hints enabled: it types a complete expression outside timing,
then measures Enter through the checked numbered result **and the next rendered
prompt**. This includes accepted-state feedback and completion/highlight binding
refresh. Paste-to-first-complete-input-render is recorded separately using two
extra clock reads around the existing input operation. Its closed-loop workload intentionally waits for readiness; it is not
an independently paced arrival model or evidence about overload queueing.

`burst` is portable redirected stdin. After one checked setup response, every
submission is offered at a common burst instant, before the blocking stdin
write. Pipe backpressure is included; this is not each command's entry time into
the OS pipe. Measurements stop at
numbered stdout results, including queue/backpressure delay. They are not prompt
readiness, independent request service times, or equivalent to the PTY latency.
The product prints a result before semantic acceptance, so a redirected result
alone must not be labelled ready-to-edit.

Both modes have two source workloads:

- `fixed`: mutate the Number field of one retained Scope and return the next
  exact integer; no additional user exports;
- `growing`: additionally export one fresh three-reference-field Scope per
  submission (`String`, `Path`, `Bool`), retaining all previous exports.

The shell prelude remains the product default. `user_exports` counts only the
probe's exports, not imported shell functions. Growing sessions are
nonstationary: report windows by submission age/export count instead of pooling
all ages as a stationary latency distribution. Nearest-rank P95/P99 retain raw
samples and their number of upper-tail ranks; a 256-submission window contains
only three observations at/above its P99 rank. A 1,024-submission
window has eleven. The fixed-export case additionally retains a full-window
summary, while age windows still reveal accumulated compilation-history costs;
fixed binding count alone does not establish stationarity. No timing thresholds are CI gates.

```text
python3 tests/Performance/repl_tail.py --binary FROZEN_NATIVE --mode prompt --case fixed --submissions 1024 --window 256 --output .temp/repl-tail/native-fixed.json
python3 tests/Performance/repl_tail.py --binary FROZEN_NATIVE --mode prompt --case growing --submissions 1024 --window 256 --output .temp/repl-tail/native-growing.json
python3 tests/Performance/repl_tail.py --binary FROZEN_NATIVE --mode burst --case growing --submissions 1024 --window 256 --output .temp/repl-tail/native-burst.json
```

The timer/parser handles one request's terminal output at a time; retained logs
are appended outside the timed interval rather than repeatedly reparsing all
previous output. Exact numbered results and process status are required. Native
image and harness hashes, command semantics and every sample are recorded.
Fixture directories and raw terminal logs live beside reports under `.temp`.
Run only in the shared quiet window: no builds, profilers or concurrent probes.

## Managed phase attribution

`Msync.ReplSession.csproj` references immutable product assemblies and cannot
build the product. It reproduces the default shell prelude, then runs the same
fixed/growing source expressions through real `CompilerSession` and
`ExecutionSession`. Each submission separates compilation, execution, acceptance
and optional binding descriptions. `--describe` includes the same accepted-state
binding/concept queries performed before an interactive prompt.

```text
dotnet build tests/Performance/ReplSession/Msync.ReplSession.csproj -c Release -m:1 -nr:false -p:FrozenAssemblies=IMAGE -p:ArtifactsPath=OUTPUT
DOTNET_TieredCompilation=0 dotnet OUTPUT/bin/Msync.ReplSession/release/msync-repl-session-probe.dll --case growing --submissions 256 --describe --output .temp/repl-tail/component-growing.json
```

Stage probes record Stopwatch time, process-wide allocated-byte counter deltas
and generation collection-count deltas. Counter reads and reporting are test
instrumentation, never product hot-path telemetry. They identify a mechanism;
the managed stage distribution is not interchangeable with Native user-visible
tails. All result integers, exact product assembly hashes, runtime and tiering
settings remain in the report. No forced collection is inserted between
submissions, and disposal/JSON writing occur outside stage timers.

## Isolated Compiler attribution (2026-11-10)

The initial common4 image showed growing history costs even with a fixed set of
56 exports (55 default shell names plus the counter). Accepted facts retained
query/analysis parent overlays across every submission. Prompt descriptions
additionally ran an empty editor compilation, froze executable HIR and projected
historical/private facts and capture dependencies, although a prompt needed only
the current accepted lexical roots.

The isolated candidate changes only `Msync.Compiler.dll`: baseline SHA256
`50a28a42918f1053582be91d67e060646fa7ed284d430f2695bda3c0ab4e9b27`,
candidate `36bb4653f50e9285e33dd0ae164ce6c1247a992e8e572d618de0c8756873d7fe`.
Core/Hir/Optimization/Runtime and the exact probe assembly remain common4. This
mixed producer is an attribution control, **not** the final combined producer or
Native AOT. Both 256-submission cases passed every exact counter check in quiet
windows, with .NET 10.0.14 and `DOTNET_TieredCompilation=0`. The before/after
windows are separate, not an interleaved latency experiment.

Last 64 submissions, ages 193–256:

| Phase/workload | Baseline P95 ms | Compiler-only P95 ms | Baseline P99 ms | Compiler-only P99 ms |
| --- | ---: | ---: | ---: | ---: |
| Fixed exports, complete phase sum | 22.28 | 12.46 | 24.26 | 16.18 |
| Growing exports, complete phase sum | 75.07 | 25.07 | 83.72 | 30.48 |
| Growing exports, binding descriptions | 52.98 | 12.78 | 63.25 | 17.57 |
| Growing exports, runtime feedback/publication | 3.40 | 6.43 | 12.34 | 14.57 |

At N=64, nearest-rank P99 is the largest observation: these are diagnostic slow
samples, not precise population-tail estimates. The complete phase sum is formed
per request before taking its quantile; it is not the sum of marginal phase
quantiles. Initial first-call/JIT observations remain in the raw 256 samples and
first-window summaries; none were filtered or replaced.

Growing-case mean allocation-counter traffic falls from 10.42 to 5.26 MB per
request in the same final window (about 49.5% less). The publication trade-off is
explicit: dense actual-root feedback followed by persistent-map publication adds
about 76 KB/request in that phase and its observed P95 rises. Whole-request P95
still falls by about threefold because the unnecessary description compilation
and parent-chain traversal disappear. Runtime feedback still visits the actual
exports; this result does not justify hiding mutations or adding dirty-root
version machinery. An unchanged ordinary within-analysis query still allocates
1,368 B for both 10 and 100,000 borrowed bindings in the focused ownership check.

The two frozen hosts, complete baseline product source, candidate Compiler
source, source manifests, commands, raw four reports, analysis and every file's
SHA256 are retained under
`artifacts/candidates/tail-latency/repl-compiler-isolated/index.json`. The actual
1,024-submission colored PTY and offered-burst Native comparisons remain separate
user-visible evidence; do not substitute this managed attribution table for them.

### Exact published-fact identity follow-up

The first isolated producer above used the BCL's default value comparer when
merging published facts. A minimal direct map regression exposed a distinct
contract error: default `SemanticValue` record equality can identify `+0` and
`-0`, two different NaN payloads, or equal-valued newly assigned descriptors.
Three of five updates consequently retained the old value/reference. Reference-
valued publication tables now use assigned-reference identity, while value-type
location/type IDs retain normal equality. Selection occurs once per closed
generic publication table; ordinary mutable assignments and query lookups are
unchanged. The same direct test then passed every bit/reference update.

The real Number/global/Scope/alias/copy/NaN feedback journey returns eight exact
`true` results. It also passed on the old Native image: that source control is
not presented as a reproduced source miscompile. The direct publication contract
is the actual failing-before/passing-after discriminator.

One additional run of the **existing** growing-256 phase case checks cost, not a
new workload family. With Compiler SHA256
`a22391acc9a7c377c549fc394cd18a9278f9b7cd8bf717927e5cf7a5edc55296`
and the same common4 other assemblies/probe, last-64 mean total allocation
traffic changes from 5.264 to 5.302 MB/request (+0.72%). Publication accounts for
about 37.5 KB/request extra; its median changes 2.484 to 2.678 ms. Descriptive
publication/total P95 values are 9.81/28.59 ms, versus 6.43/25.07 ms in the earlier
isolated window. The orchestrator reports approximately one second of Ruff/mypy
work at the start of this additional window. It was not interrupted, replaced
or filtered; its timing does not establish an isolated tail regression or a
Native result. The modest allocation cost is retained for exact semantics,
without adding dirty-root tracking or another session mechanism.

The exact new DLL/PDB, complete Compiler source and source-hash manifest, failing
direct log, raw materiality report and overlap note are retained in
`artifacts/candidates/tail-latency/repl-fact-identity/index.json`. This correction
is commit `03b49c9`; the final Native session measurements must use a new package
that includes it, not relabel the preceding `9e5e997` CLI evidence.

## Final Native colored REPL tails

The final session producer is source
`604b5584e103e1fcb7c62cfee779b0dc43447cbe`, Linux Native SHA256
`8614cfa234930f599e35b5b901ad208b3d5202ed0bf7419066680dc171463d7c`.
The baseline is spatial source `a0cebdf6c5f31dd0b3650fd534cbbee7ac1d7ddd`,
Native SHA256 `54d591a87ab831c07459333d3e06acc39a1164b37beaae0eb83cccfd9df21bfc`.
Both run the exact same harness SHA256
`5705620dfccfe9b4418cf9b390b7390a98d3c314942cb9545de236e307614ae4`.
The preceding CLI six-case comparison uses the separately identified `9e5e997`
producer; it is not relabelled as this new session image.

Each image completes 1,024 submissions in each of three workloads: fixed-export
colored PTY, growing-export colored PTY, and growing-export offered burst.
Every one of the 6,144 numbered responses matches, and all six processes exit
normally with code zero. Full sessions run AB/BA by case (fixed A/B, growing B/A,
burst A/B), not per-request interleaved pairs. There is one complete session per
image/case, not repeated-session replication. A coordinated quiet window excludes
team builds/profilers/installation and other benchmarks; OS caches are not
flushed. No run or slow observation is replaced, truncated or filtered.

PTY measurements include **Enter through the checked result and next ready
prompt**. Colors, lexical highlighting and hints are enabled with
`TERM=xterm-256color`, 30×120 cells, default `auto` color policy and `NO_COLOR`
unset. Paste-to-complete-input-render is measured separately rather than hidden
inside an Enter latency. The fixed case has one user export; growing exports
increase from two through 1,025, in addition to 55 imported shell names.

### Ready-prompt age windows

| Case and submission ages | Baseline P95 ms | Final P95 ms | Baseline P99 ms | Final P99 ms |
| --- | ---: | ---: | ---: | ---: |
| Fixed, 1–256 | 23.45 | 11.31 | 28.03 | 14.77 |
| Fixed, 257–512 | 33.49 | 10.57 | 40.10 | 13.84 |
| Fixed, 513–768 | 44.47 | 11.48 | 56.63 | 14.73 |
| Fixed, 769–1,024 | 61.75 | 11.85 | 70.34 | 14.22 |
| Growing, 1–256 | 68.98 | 23.36 | 84.86 | 27.43 |
| Growing, 257–512 | 172.21 | 40.43 | 203.22 | 46.14 |
| Growing, 513–768 | 294.19 | 63.75 | 428.88 | 73.45 |
| Growing, 769–1,024 | 434.21 | 100.24 | 753.50 | 117.00 |

Every 256-entry window's nearest-rank P99 is rank 254, with **three** observations
at/above that rank. These are observed age-window tails, not precise population
quantile guarantees. The additional fixed-workload full-1,024 summary has eleven
upper P99 observations: P95 52.08 → 11.15 ms, P99 63.07 → 14.22 ms. Its original
age trend remains visible above; fixed export count does not prove stationarity.
Growing windows are deliberately not pooled. In the last growing window, the
largest observed response falls from 1,057.26 to 132.65 ms. The final growing
response still costs more with more retained roots; this is not a constant-time
or universal tail guarantee.

The fixed case removes the sharp history-age escalation even without increasing
exports. The growing case retains real feedback/projection costs while avoiding
the old historical parent-chain and whole-private-world description work. The
managed controls above distinguish that mechanism from unrelated Runtime fixes;
this Native table measures the complete combined user experience, not one
isolated optimization's attribution.

### Input rendering, queue completion and observation cost

Last-window paste-render P95/P99 values are 0.477/0.709 → 0.385/0.431 ms for
fixed exports and 1.486/11.011 → 1.271/1.638 ms for growing exports. This is not an
all-age render-speedup claim: the first growing window's render P99 is
0.792 → 2.200 ms, and the raw maxima remain. Enter-to-readiness and rendering
answer different input/output parts of the journey.

The offered-burst clock starts before the blocking stdin write, so it includes
producer pipe backpressure and accumulated queue delay. The final numbered
result arrives at 54.445 → 18.502 seconds. Last-256 queue-completion P95/P99 are
52.858/54.162 → 18.118/18.436 seconds; they must not be described as independent
request service times or compared directly with ready-prompt milliseconds. The
product emits redirected results before semantic acceptance; this mode does not
claim prompt readiness or an independently observed service-entry timestamp.

Per-run 4,096-pair adjacent timer calibrations give P99 read deltas of 201–379 ns;
none are subtracted. These describe counter reads, not the entire observer.
Terminal parsing/log handling remains bounded to one request, with raw segments
retained outside each measured Enter interval. There is no forced product GC or
new product hot-path telemetry. Spawn-to-first-prompt observations are recorded
separately, but four starts are not a startup-tail estimate; the larger CLI
startup comparison remains separate evidence.

Exact commands/order, six raw reports, every age sample, full terminal/stdout
logs, source/helper/producer identities, clock controls and postprocessing are
retained under
`artifacts/candidates/tail-latency/604b558/repl/index.json` and `summary.json`.
Both platforms pass the changed HIR/session-age/shell-REPL acceptance and actual
source-editor suite in Native candidate run `42869625070`; this latency window
itself is Linux only. It does not publish or replace a release.
