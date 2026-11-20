# Benchmarks

Measure useful work, not how often an optimization fires. The suite is a
starting set of distinct workloads, not a claim about their production frequency
or a permanent scorecard. Choose the affected journeys before running a matrix.

## Current baseline

The [0.9.2 Linux x64 distribution-suite baseline](docs/benchmarks/0.9.2-linux-x64.md)
covers all six default groups and both opt-in SSH loopback directions with a
frozen release package. It includes [portable raw samples](docs/benchmarks/0.9.2-linux-x64.json)
for same-scenario comparisons. It is a single-version snapshot, not a
speedup over the older suite.
The [0.9.2 I/O attribution](docs/benchmarks/0.9.2-io-attribution.md) separates
local flush, SSH upload publication, and download installation costs.
The [native Windows tree comparison](docs/benchmarks/windows-tree-io.md)
keeps NTFS-local and Windows-initiated SSH deployments separate.

## I/O scenarios and distributions

The default `io` group measures first deployment to an absent target and
then redeployment of a changed tree into that same target. Measure the two
phases separately. Default transport is local; SSH upload and download are
explicit, real OpenSSH loopback lanes with no external network dependency.

| Profile | Input distribution | Redeployment |
| --- | --- | --- |
| `source-tree` | Release tree plus deeper Unicode paths, variable directory fan-out, and several hundred additional entries drawn from 0 B–12 KiB | Same-size edits spread across populated subdirectories and one new file |
| `release-v1` | 100 files: mostly small text, plus 256 KiB and 4 MiB binary assets, executable and empty directory | Three same-size edits and one new file |
| `artifact-batch` | Release tree plus three 6/11/24 MiB artifacts; bytes concentrated in a handful of files | Rebuild one 24 MiB artifact without changing its size, edit a small settings file, add one new file |

The report records per cell the seed, file-size histogram, directory count
and depth, selected files/bytes, changed files/bytes and SSH process,
authentication and session counts. Changed bytes mean the sum of sizes of
edited/added files, **not** modified bytes or wire bytes. All phases check exact
files, content, modes, file mtimes and an empty directory. Redeploy must
preserve a receiver-owned file in the existing destination.

```sh
python3 tests/Performance/run.py --binary NEW --baseline OLD --group io \
  --io-profile source-tree --io-profile artifact-batch \
  --io-transport ssh-loopback --io-transport ssh-download \
  --seed 17 --seed 503 --samples 7
```

Both phases run the same whole-tree source script, not automatic incremental
transfer, a deletion mirror or a delta protocol. The destination is merged;
local and SSH results keep their own timing boundaries. `--io-transport
ssh-loopback` is upload, and `ssh-download` is download on the same-host
fixture. No cache eviction or WAN emulation is implied. The standalone
[sync journey](tests/Performance/sync_journey.py) still defaults to only
the original `release-v1` fixture. The original isolated 64 MiB replacement
is still available from `journeys.py --file-bytes 67108864 --case
copy-file-source --case copy-file-object`; the explicit manifest and Range
copy workloads also remain available.

These profiles bracket different costs; they do not estimate workload
frequency. The [FAST '07 metadata study](https://www.usenix.org/conference/fast-07/five-year-study-file-system-metadata)
motivates separating distributions by file count and bytes, and keeping
namespace shape independent. Its populations are not msync usage data.
[Filebench](https://www.usenix.org/publications/login/spring2016/tarasov)
provides precedent for explicit workload profiles rather than a universal
filesystem score. [rsync's default quick check](https://rsync.samba.org/ftp/rsync/rsync.1)
illustrates why an explicitly selected update, a whole-tree merge, and a
tool's automatic change selection must not be conflated.

The next calibration inputs are real tree profiles (sizes, depth, fan-out,
changed-file fraction and destination occupancy) and real two-host SSH
lanes (RTT, link capacity, connection reuse policy). Add explicit list-driven
update and retry lanes when a script and its result semantics make those
comparisons meaningful. Do not infer such workflows from a bigger synthetic
matrix or use loopback to claim WAN behavior.

Previous `release-v1` pilot: 24 timed deployments across 0.9.1/0.9.2, both
phases and local/SSH upload. 0.9.2 first/redeploy P50 was 179/177 ms locally
and 930/988 ms over loopback in that specific three-pair window. Its raw
receipt remains at `artifacts/benchmarks/sync-journey-pilot/result.json`.

Initial suite check on packaged Linux x64 0.9.2, standard size and seed 17:
all 18 I/O cells (three profiles × two phases × local/SSH upload/SSH download)
and 24 application cells (six families × two shapes × source/bound) passed
their independent oracles. The first-deploy profiles contained
377 files / 47 directories / 5.53 MiB, 100 / 16 / 4.75 MiB, and
103 / 16 / 45.75 MiB respectively. This was one checked observation per
cell, not a latency baseline. Receipts:
`artifacts/benchmarks/distribution-suite-092-validation/`.

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
Default runs contain six serial groups. Independent-process saturation is opt-in only.
Work directories are new/empty
children of `.temp/`; the default is uniquely named. `result.json` indexes per-group raw reports and logs, including
completed work and the failing command if a later group fails. `--samples`,
`--warmups`, `--scale`, `--submissions`, `--window`, and optional `--cpu` control
the experiment. `--jit auto|off|linked|force` selects the requested guest tier
policy; ambient `MSYNC_JIT` is not inherited. `linked` and `force` are diagnostic
policies, not recommended defaults or evidence that promotion is profitable.
Confirm actual tier use in the separate `--profile profile|instructions` runs;
an older image may not implement the requested policy. Instrumentation never
runs in the timed samples.

## Optional saturation diagnostic and SQLite

`--group throughput` is retained to reproduce independent-process saturation
experiments, not to score the product. Its closed-loop requests/s depends on
the fixed worker population, fixture reset and oracle work, and says nothing
about parallelism within one sync command. Run it only to answer a named
independent-process question. The Linux systemd/cgroup-v2 scope caps it at
**12 CPU / 24 GiB**; concurrency levels are 1/4/12/24 unless selected.

```sh
python3 tests/Performance/run.py --binary PATH_TO_MSYNC --group throughput \
  --workload local-manifest-copy --route bound --samples 3 --requests 96 \
  --concurrency 1 --concurrency 12 --concurrency 24 --profile instructions \
  --work .temp/benchmarks/copy-throughput
```

`throughput/benchmark.sqlite` contains every timed request, successful
requests/s, P50/P95/P99, failures, child CPU, resource limits, and losslessly
merged separate compiler/runtime profiles. Timing is telemetry-off; profile
cost is never mixed into throughput. Bound workers bind in their own directories
so captured paths cannot send writes into another worker's fixture.
See [measurement boundaries, schema, SQL and acceptance](docs/design/benchmark-throughput-v090.md).

## Coverage

| Group | Question and current coverage |
| --- | --- |
| `cli` | What does a small command cost? Startup, one/128-statement checks, 4 KiB replacement, 256-file tree; source and portable-object routes. |
| `io` | What does first deployment and small/large-edit redeployment cost under different file-size and namespace profiles? See above. |
| `applications` | Six application families with two shapes each: Set, two numeric workloads, streamed selection, changing policy, and path text. [Distributions and oracles](tests/Performance/Corpus/README.md). |
| `interactive` | What does an actual request wait for? Persistent REPL, LSP initialization/warm controls, change-to-current-semantic response, and checked semantic-token deltas. Not editor repaint. |
| `editor` | What does ordinary editing cost? Fresh-session standard-library queries, typed completion/details/navigation/shadows, and a 400-binding edit burst. Each observation starts a process; no artificial warmup. Larger documents remain explicit diagnostics. |
| `session` | Does cost grow with retained state? Real colored Linux PTY, fixed and growing exports, Enter through result and next prompt. Age windows stay separate; unsupported hosts are reported, not silently counted as tested. |

Set is the only default collection workload. B+, Heap, polygon and the four
overlapping scan/fold workloads remain explicit diagnostics. Their performance
still matters when changing those implementations; their regressions do not
get extra weight in a claim about general applications. The two existing copy
corpus cases are explicit controls since the default I/O lane owns transfer
scenarios. Use `--distribution both` with an explicit application selection
to run both input shapes, or `contrast` to run only the alternate one. Scale
and seed vary size and draws; they are not substitutes for changing the shape.

## Recent measurement evidence

On 2026-11-20, packaged Linux x64 Native AOT 0.9.1 and 0.9.2 were
compared on standard `set-dedup` (8,192 events and 4,096
queries/expirations, seed 17). CPU 6, Auto tiering, telemetry off, one
warmup and seven alternating pairs per route; each image prepared its own
O2 artifacts. All 42 timed invocations matched the independent oracle.

| Entry point | 0.9.1 P50 | 0.9.2 P50 | Median paired elapsed change |
| --- | ---: | ---: | ---: |
| Source | 3,436 ms | 751 ms | −78.0% |
| Portable object | 3,139 ms | 509 ms | −83.8% |
| Bound executable | 3,060 ms | 497 ms | −83.7% |

The older `9703b23` Set compilation crash did not reproduce. This is a
release-to-release comparison for one workload, not a suite-wide gain.
Raw observations and binary/fixture identities are retained locally in
`artifacts/benchmarks/release-092-vs-091-set/corpus.json` and `suite.json`.

On 2026-11-16, a paired Linux x64 Native AOT development-image run
(`3e7e581b95af…` versus `ac86f071905a…`) completed all six serial groups,
including all eleven standard applications on source and bound routes
(seed 17, seven pairs). Its concurrent stage was interrupted; a separate
run with the same images completed 48 batches and 1,152 checked requests
across both routes and the default four concurrency levels, with one
24-request window per cell. Local receipts:
`.temp/benchmarks/full-ssa-register-call/result.json` and
`.temp/benchmarks/full-ssa-throughput/throughput/result.json`.
These are development-image results, not a 0.9.2 release baseline.

[9703b23: latency, throughput and telemetry analysis](docs/design/benchmark-telemetry-9703b23.md)
records ten successful standard-scale applications, a Set compilation crash,
all other serial groups, 1,152 checked concurrent requests and a same-artifact
Auto/Off tiering experiment. It is a partial application baseline, not a clean
full-suite pass or a paired comparison with the older image.

[04465c4: full product suite and detailed compiler telemetry](docs/design/benchmark-telemetry-04465c4.md)
records all six groups, standard-scale application routes, editor/session results
and the separate single-vCPU LSP startup diagnosis for that historical image.

### Packaged local I/O, 0.9.1 versus 0.9.2

2026-11-20, Linux x64 Native AOT on guest ext4, page-cache warm, CPU 6,
one warmup and seven alternating pairs per route, telemetry off.
The CLI fixtures overwrite deliberately stale targets and check exact bytes,
mode, and nanosecond mtime. Application fixtures check stdout and the expected
output paths and hashes. The two application cases each copy 135 files
(6,588,849 bytes) from the same standard/17 input but preserve different
destination layouts.

| Workload / route | 0.9.1 P50 | 0.9.2 P50 | Median paired elapsed change |
| --- | ---: | ---: | ---: |
| 4 KiB / source | 20.33 ms | 21.58 ms | +4.78% |
| 4 KiB / object | 20.45 ms | 21.53 ms | +5.28% |
| 64 MiB / source | 148.36 ms | 153.31 ms | +1.62% |
| 64 MiB / object | 166.03 ms | 162.65 ms | −1.53% |
| 256 × 4 KiB directory / source | 381.10 ms | 391.85 ms | +2.82% |
| 256 × 4 KiB directory / object | 321.44 ms | 323.54 ms | +1.38% |
| Manifest copy / bound | 246.88 ms | 253.08 ms | +1.76% |
| Range copy / bound | 235.29 ms | 233.08 ms | −2.97% |

A separate diagnostics replay of the 0.9.2 object directory copy recorded
256 file syncs totaling 261 ms inside its 324 ms transfer span. The tiny
file was 1.47 ms of transfer in its own replay; its normal CLI latency
includes process and command setup. Diagnostic spans are inclusive and do
not contribute to the latency table. All six SSH loopback selector
journeys also passed on 0.9.2, each using one client, one
authentication and one session, with no SCP. This checks orchestration,
not WAN capacity or current Windows/WSL latency.

The separate 0.9.2 manifest-copy throughput sweep (bound, standard/17,
12 CPU / 24 GiB, two windows of 48 requests per concurrency) verified
all 384 timed requests. In concurrency order 1/4/12/24, the
repetition-level medians were 5.75/14.37/11.60/9.53 successful requests/s
and 114/130/262/679 ms invocation P50. Throughput includes fixture reset
and verification; invocation latency excludes them. Four workers were
the best measured setting for this fixture, not a global tuning default.

Local receipts and diagnostic databases:
`artifacts/benchmarks/release-092-io/`.

### Packaged SSH I/O, 0.9.1 versus 0.9.2

2026-11-20, Linux x64 Native AOT, real OpenSSH 10.0p2 loopback with
public-key authentication and multiplexing disabled. Client and server
share the same guest ext4 filesystem; the cache is warm. Each source-form
CLI journey overwrites a stale target, varies the source generation, and
checks exact bytes, mode and nanosecond mtime outside the timed interval.
Telemetry is off; one warmup per image and seven alternating pairs per
workload/direction. This is end-to-end command latency including remote
completion, not TCP or WAN throughput.

| Workload | Direction | 0.9.1 P50 | 0.9.2 P50 | Median paired elapsed change |
| --- | --- | ---: | ---: | ---: |
| 4 KiB | Upload | 160 ms | 165 ms | +0.6% |
| 4 KiB | Download | 155 ms | 152 ms | −2.0% |
| 64 MiB | Upload | 702 ms | 699 ms | +1.6% |
| 64 MiB | Download | 722 ms | 776 ms | +5.7% |
| 256 × 4 KiB directory | Upload | 1,710 ms | 1,718 ms | −0.7% |
| 256 × 4 KiB directory | Download | 524 ms | 511 ms | −0.3% |

All 84 timed copies passed and used one SSH client, one authentication,
one remote exec session and no SCP. A separate OpenSSH no-op command
on the same fixture had 115 ms P50; this includes remote command startup
and is not an additive authentication-only phase. Directory upload is
about 3.4× the elapsed time of directory download on 0.9.2. The
[remote upload installer](src/Msync.Runtime/NativeModel/NativeHostIO.Protocol.cs)
recurses through the staged tree with a shell subshell and external
`mkdir -p` and `mv` per file. Measure server-side installation separately
before attributing the full gap or changing publication semantics.
Raw samples and experiment driver: `artifacts/benchmarks/release-092-ssh/`.

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
