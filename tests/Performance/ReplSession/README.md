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
