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
