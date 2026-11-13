# Benchmarks

Measure useful work, not how often an optimization fires. The suite is a
starting set of distinct workloads, not a claim about their production frequency
or a permanent scorecard. Choose the affected journeys before running a matrix.

## Run

Use published Native AOT images built with matching toolchains for comparisons.

```sh
python3 tests/Performance/run.py --list
python3 tests/Performance/run.py --binary PATH_TO_MSYNC
python3 tests/Performance/run.py --binary NEW --baseline OLD \
  --group applications --workload set-dedup --route all --seed 17 --seed 503 \
  --work .temp/benchmarks/set-comparison
```

With one image this records an absolute snapshot, not an invented A/A comparison.
Runs are serial. Work directories are new/empty children of `.temp/`; the default
is uniquely named. `result.json` indexes per-group raw reports and logs, including
completed work and the failing command if a later group fails. `--samples`,
`--warmups`, `--scale`, `--submissions`, `--window`, and optional `--cpu` control
the experiment. `--jit auto|off|linked|force` selects the requested guest tier
policy; ambient `MSYNC_JIT` is not inherited. `linked` and `force` are diagnostic
policies, not recommended defaults or evidence that promotion is profitable.
Confirm actual tier use in the separate `--profile profile|instructions` runs;
an older image may not implement the requested policy. Instrumentation never
runs in the timed samples.

## Coverage

| Group | Question and current coverage |
| --- | --- |
| `cli` | What does a small command cost? Startup, one/128-statement checks, 4 KiB replacement, 256-file tree; source and portable-object routes. |
| `io` | How does ordinary large-file replacement behave? One 64 MiB file, real writes and exact verification; not a storage-bandwidth score. |
| `applications` | What happens across data and control shapes? Geometry, captured mutable state, record construction, mixed Set/B+/Heap use, lazy stdin, changing policies, Unicode paths, explicit manifest copies and finite Range copies. [Cases and oracles](tests/Performance/Corpus/README.md). |
| `interactive` | What does an actual request wait for? Persistent REPL, LSP initialization/warm controls, change-to-current-semantic response, and checked semantic-token deltas. Not editor repaint. |
| `editor` | What does ordinary editing cost? Fresh-session standard-library queries, typed completion/details/navigation/shadows, and a 400-binding edit burst. Each observation starts a process; no artificial warmup. Larger documents remain explicit diagnostics. |
| `session` | Does cost grow with retained state? Real colored Linux PTY, fixed and growing exports, Enter through result and next prompt. Age windows stay separate; unsupported hosts are reported, not silently counted as tested. |

The four overlapping scan/fold motifs (`band-classification`,
`segment-screening`, `literal-segment-parity`, `ordered-calibration`) are removed
from the default application suite. Select them with `--workload` when testing
their mechanism. Pure container/SHA kernels, opcode/call/field probes and frozen
adaptive-runtime overlays are diagnostics or historical experiments, not general
application evidence. Correctness regressions remain worth keeping even when
their timings are not representative.

## Interpretation and evolution

- Every timed application must pass its independent result/file oracle. Copy
  destinations are reset outside timing; ordinary CLI copies also vary source
  generations so a previous successful output cannot certify a no-op.
- Fresh CLI times include launch, load and exit. Source includes compilation;
  object/bound preparation is outside timing. Persistent request clocks and
  terminal readiness are different boundaries. Do not subtract route medians
  or inclusive profile spans to invent phase costs.
- Freeze images/dependencies and retain inputs, configuration and raw samples.
  Use paired order for short comparisons and record host/cache conditions;
  session-age comparisons retain complete sessions, not interleaved requests.
  Editor request initialization excludes spawn; interactive initialization
  includes it. Fixtures are OS-cache-warm; small samples describe a window,
  not a P99 promise.
  Keep CPU time, allocations, retained memory and elapsed time distinct.
- No universal weighted score, timing CI gate, or claim that Wine/QEMU proves
  physical-platform throughput. Current SSH/WAN, native Windows/ARM hardware,
  and editor rendering need their own real environments; 4,000-binding editing
  is available explicitly through `editor_latency.py --case flat4000`.
- Add a case when it exposes a useful missing workload, with input variation and
  an independent oracle. Replace/demote cases when their behavior is redundant,
  obsolete, or only discriminates a particular implementation. Retain a nonhit
  control when claiming broad gains; do not grow the suite for each new pass.

The application-oriented approach is informed by [DaCapo](https://www.dacapobench.org/)
and [Renaissance](https://renaissance.dev/); environment disclosure and validation
also have practical precedent in [SPEC's run rules](https://www.spec.org/cpu2017/Docs/runrules.html).
We borrow the useful principles, not a certification process. Results live in the
[performance record](docs/design/performance-v080.md); open work is in
[RESEARCH.md](RESEARCH.md).
