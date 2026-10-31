# v0.6 user-visible startup and background completion

## Workload and ownership

This workstream starts from the completed v0.6 language implementation. The
integrating owner preserved the optimized release executable before production
edits under `target/performance-v6/baselines/`, together with source archive,
environment and SHA-256 identity. Measurements use that immutable image, not
`target/debug` or a mutable build output.

The relevant user waits are distinct:

- Cold command completion includes process loading, argument/config selection,
  parsing, compilation, actual work and exit.
- Persistent REPL submission excludes process startup but includes the next
  command's compilation, execution and acknowledgment.
- Background EOF-to-publication and EOF-to-notice are separate. A file may already
  be published while the editor has not reconciled or reported its completion.
- Large file throughput and kernel fast-path selection are separate experiments;
  small-command timing cannot establish either.

## Eliminating unnecessary owners and periodic observation

A pure bool/string/path binding can lower to no runtime instructions. Previously
`State::execute_unit` still created Tokio workers, recovery/storage ownership and
an execution/authentication scope. The new empty-program path commits the already
validated compiler candidate and returns before those owners are initialized.
It preserves no-output behavior and keeps existing active jobs under their prior
ownership. A declaration does not acquire unrelated work or require writable
runtime scratch directories. Static compilation/config validation still occurs.

Previously, an active REPL prompt created a scoped blocking editor reader and
checked a channel every 20 ms. Completion checks were periodic; more importantly,
the notices they queued were not flushed until another input line arrived.
The new input path selects between a Tokio input channel and the runtime's
cancel-safe, non-consuming `wait_ready` future. Its completion signal represents
any fully completed flow, acquisition or drained child process, not the first
submitted job and not mere root-process exit. There is no polling interval.

The blocking terminal reader remains scoped to one prompt: rustyline owns input,
history, paste, cursor and interrupt state. The main thread reconciles completed
work while it waits. Ordinary background notices go through the editor's native
external printer, which redraws an unfinished Unicode line without accepting it.
Basic/piped mode writes and flushes complete lines directly. Error diagnostics
retain stderr and trigger a prompt refresh rather than silently changing channels.
A notice output failure is retained until the blocking reader returns, so an
error does not strand an unjoinable input reader inside a scoped thread.

`repl.wait` telemetry records whether input or a completion woke the wait. The
existing progress observer remains independently delayed/rate-limited: defaults
are 200 ms initial delay and 250 ms refresh, now exposed through typed settings.
A display refresh interval is not a background-completion polling policy.

## Logical job index and runtime identity

Job registration previously scanned the top-level instruction list for every
added work item. Besides repeated O(work * instructions) lookup, it missed nested
control launch sites and used static template process identities for dynamically
instantiated loop bodies.

Registration now builds one borrowed recursive HIR instruction/launch index.
The runtime provides actual work-to-static-instruction identities and actual
retained-flow program connections. These are operational ownership metadata,
never object inputs or source guesses. A loop-local child keeps its real process
identity for job connectivity, while its launch instruction supplies the correct
source attribution. Different iterations cannot merge because they share one
static launch template. No instruction tree or source module is cloned for the
index.

## Reproducible probes

The existing standalone `benches/latency_v4.rs` filename is retained. Its original
copy, persistent overwrite and two-flow publication controls remain available.
Set `MSYNC_PERF_V6=1` to select additional v0.6 journeys:

- `run-pure-bindings`: a zero-HIR bool/string submission;
- `check-range-1000` and `run-range-1000`: a 1,000-item typed string range, strict
  comparison, state update, and one verified final literal publication;
- `plan-independent-100`: static planning of 100 distinct destinations by default;
- `repl-binding-with-pwd-barrier`: persistent binding latency plus the existing
  cwd acknowledgment barrier;
- `background-eof-to-idle-notice`: actual localhost stream EOF to a completed-job
  message while stdin remains open and no follow-up line is sent.

```sh
rustc --edition 2024 -O benches/latency_v4.rs -o target/performance-v6/latency-probe
MSYNC_PERF_V6=1 target/performance-v6/latency-probe \
  target/performance-v6/baselines/msync-v060-before \
  target/performance-v6/baselines/msync-v060-final .temp 15
```

The harness warms each executable twice and alternates AB/BA order. Fixture
creation/seeding and output verification occur outside timing. Successful control
output must be recreated for every run, so a no-op cannot pass by retaining the
previous result. Input, executable staging and logs remain inside the project.
`benches/startup.rs` supplies the complementary help/version/small-plan controls.

The idle-notice probe has a 150 ms observation window. Missing baseline notices
are reported as censored, not invented 150 ms samples or a speedup denominator.
Unblocking input is sent only after that observation. General process/stream
operations retain a 15-second safety deadline. Report p50/p95 absolute times;
with small samples p95 is an order statistic, not a proven tail bound.

## Verification artifacts

`tests/latency_v6.rs` uses real CLI/REPL processes and a native PTY. It checks pure
bindings with deliberately unusable scratch roots, idle stream and process
completion reporting, and preserving an unfinished Unicode editor line while a
completion arrives. The private job unit test constructs two actual loop-runtime
identities at one semantic launch node and checks that a connected flow joins
only its actual process, with retained source attribution.

The sections below record actual central-gate results and paired quiet-window
measurements; authored probes alone are not counted as pass evidence.

## External mechanisms and interpretation

- [Tokio Notify](https://docs.rs/tokio/latest/tokio/sync/struct.Notify.html) documents
  notification registration/lost-wakeup considerations. The runtime uses sticky
  completion signals and checks real worker readiness; a wake is not a publication
  receipt or permission to replay an operation.
- [rustyline external printing](https://docs.rs/rustyline/latest/rustyline/struct.Editor.html#method.create_external_printer)
  is the mature editor mechanism for asynchronous notices; another terminal input
  engine or manual cursor-control protocol would duplicate its state owner.
- [Dean and Barroso, The Tail at Scale](https://research.google/pubs/the-tail-at-scale/)
  motivates reporting variability rather than only average latency. This desktop
  harness does not claim the paper's distributed fanout results apply to a REPL;
  its practical adoption is paired samples, absolute waits and separate causes.

## Background failure is not a global acquire

A ready-flow error previously called `cancel_active()` from `State::poll` before
printing the diagnostic. That operation gracefully waits for all released
programs. A child waiting for an independent external EOF could therefore block
the REPL's input loop indefinitely, even though the failing flow had already
retired safely.

The idle poll path now records the fully located error, reconciles actual
remaining work, and leaves unrelated retained work under its existing owner.
A neutral settled-after-background-failure notice does not claim that every
concurrently settled job failed. Explicit `/stop`, `/wait`, shutdown and
foreground submission error policy keep their separate established contracts.
The regression launches a real child msync blocked on one TCP connection,
induces a publication-parent failure in another flow, requires stderr diagnostic
and responsive `/jobs` before releasing the child's EOF, and verifies its output.
No destructive process kill is substituted for graceful drain.

## Initial focused acceptance and assertion corrections

The first integrated `latency_v6` gate passed pure CLI/REPL declarations,
background stream completion without input, and independent-work preservation
after a background publication failure. Two new assertions did not match existing
contracts: a completed child's stdout is still a consuming asynchronous stream
(not a synchronous `done:` transfer), and `/bindings` intentionally displays
string bindings using canonical `:=` even when entered with `=`. The tests now
wait for the actual second-job completion and verify its bytes; the PTY test
uses the established display spelling and additionally transfers the retained
Unicode binding to a file and checks the exact UTF-8 bytes. Production behavior
was not distorted to satisfy these incorrect expectations. Root-owned targeted
reruns provide their acceptance evidence.

Focused native Linux acceptance on 2026-10-31 is now **six distinct CLI/PTY
journeys passed**: four in `.temp/performance-v6/features.log`, followed by the
two corrected-contract checks in `.temp/performance-v6/final-functional.log`
(process completion 0.04 s; real Unicode editor/notice journey 0.09 s).
This is incremental acceptance, not a new full-suite pass. The binary job gate
also passed five model tests, including the new actual-runtime-identity case.
Paired optimized-image measurements are recorded separately below; no speedup
is inferred from these functional test execution times.

The user-journey plan fixture defaults to 100 operations; set
`MSYNC_PERF_PLAN_SIZE=1000` (or another positive count) for an explicit scaling
run. This choice follows the integrating owner's component evidence: the old
static-object path already takes roughly 245 ms at 100 operations, with costly
allocation scaling measured separately. An unbounded repeated 1,000-operation
CLI comparison would conflate a desktop-wait sample with a much larger memory
experiment. The 1,000-operation case was not silently timed out or counted as a
speedup; it remains available, while dedicated component probes cover larger
scaling. Exact source fixture size is encoded in every output case name.

## Final user-journey evidence (2026-10-31)

### Executable and measurement identities

Native Linux x86-64, kernel `6.12.113+deb13-amd64`, Rust/Cargo 1.99.1,
normal Cargo release profile, native ext4 (`stat -f` magic `ef53`, device 65028).
The integrating owner granted exclusive windows: no other agent builds, tests,
profilers or timed probes ran during these samples. Host scheduling/storage noise
remains possible. Neither executable was replaced while measured; begin/end
SHA-256 files compare byte-for-byte.

| Image | SHA-256 | Evidence role |
| --- | --- | --- |
| `target/performance-v6/baselines/msync-v060-before` | `ede9b2b020a21f96cb6149866d3f9ac98e1d6189481fe2bcccea470734ec06ca` | Preserved v0.6.0 before-image |
| `target/performance-v6/baselines/msync-v060-optimized-3` | `aa4749e3759d1cb55f898077e19f32a6cb8d37e8867c0c271f37da9af9adedcf` | Larger planning/control and exporter-cost measurements |
| `target/performance-v6/baselines/msync-v060-final` | `bd836d6750c855f5f4982b355ab37855f32efff4e971c6b50fe3b83f617d50c3` | Final algorithm image before metadata-only rebuild; selected small/copy/REPL/TCP journeys |

The final artifact adds the last unused dependency-index insertion elimination;
no logging or editor functionality changed after optimized-3. Measurements below
retain their actual image identity rather than relabeling an earlier executable
as the final artifact. The final startup probe SHA-256 is
`08ed5fef5cb4aa09cfdb3e329b6a05719e9aebb243d6300b9cd655c66738aeab`;
the flow/copy follow-up probe is
`2ccba1b9df863aab9edb98e0e43875328e284501fcfd7db4b975db2d9a9fc76b`.
Source/probe hashes live in `.temp/performance-v6/latency-probes-followup.sha256`.

### Final algorithm image (bd836d) startup: measured small fixed trade-off

Five warmups per arm, 30 alternating AB/BA samples, complete CLI process time.
Both arms receive isolated HOME/USERPROFILE/XDG_CONFIG_HOME/APPDATA and have
MSYNC_CONFIG/MSYNC_STATE_DIR/MSYNC_TRACE removed. Output is drained/discarded as
appropriate; every exit must succeed.

| Journey | Before p50 ms | Final p50 ms | Before p95 ms | Final p95 ms |
| --- | ---: | ---: | ---: | ---: |
| `--version` | 2.802 | 2.982 | 3.181 | 3.677 |
| `--help` | 2.740 | 2.826 | 3.528 | 3.508 |
| `check`, one operation | 3.352 | 3.759 | 3.827 | 4.359 |
| `plan`, one operation | 3.527 | 3.849 | 4.521 | 4.333 |
| REPL help then exit | 3.061 | 3.320 | 3.592 | 4.001 |

**This is not a universal startup improvement.** The integrated configuration,
portable toolchain and observability capabilities retain approximately 0.09–0.41
ms higher p50 for these small commands. This is an explicit small fixed setup
trade-off, not a claim that every microsecond has been causally attributed to a
particular feature. In particular, help/version do not discover a config file or
initialize a database/runtime, so file discovery cannot explain all image/setup
costs. Raw results: `.temp/performance-v6/user-startup-final4.csv`.

### Final algorithm image (bd836d) actual file and background journeys

Finite CLI cases have two warmups and 15 paired alternating samples. A target is
removed or seeded with incorrect old bytes before every invocation; correct bytes
are verified afterward, excluding setup/verification from timing. Persistent
REPL uses 32 paired submissions plus its cwd acknowledgment barrier. These are
warm-filesystem/loader results, not cold-storage throughput measurements.

| Journey | Before p50 ms | Final p50 ms | Before p95 ms | Final p95 ms | Final faster pairs |
| --- | ---: | ---: | ---: | ---: | ---: |
| CLI 64 KiB, missing destination | 41.016 | 38.220 | 43.412 | 46.577 | 10/15 |
| CLI 64 KiB, existing destination | 38.699 | 37.523 | 48.354 | 43.062 | 8/15 |
| Persistent REPL 64 KiB overwrite + cwd barrier | 26.652 | 27.724 | 29.131 | 30.134 | 11/32 |
| Second stream EOF → visible file, first peer still held open | 1.148 | 1.147 | 1.195 | 1.197 | 7/15 |
| Second stream EOF → explicit REPL acknowledgment | 1.387 | 1.371 | 1.626 | 1.410 | 9/15 |

The initial 32-operation persistent-file cohort has an observed **1.072 ms /
4.0% p50 increase**, with a mean paired increase of 0.808 ms. This negative point
is retained, not attributed to per-process configuration loading or erased as
noise. It triggered the 202-pair crossed-workspace discriminator below: its
aggregate mean difference is +0.051 ms, with opposing half means. Consequently
a stable 1 ms / 4% regression is **not reproduced**, but neither is universal
IO acceleration or a guaranteed zero interval cost. File-copy medians improve,
while mixed pair directions and tails remain explicit.

Both TCP arms verify the fast leaf's complete bytes, mtime, portable permissions
and (on Unix) device/inode/mode remain unchanged after the slower independent
publication. The slow destination is absent until its own peer EOF; each result
is checked and the REPL settles normally. The historical v0.4 probe initially
required unchanged **parent directory** mtime and failed on the untouched v0.6
baseline's first warmup. That is incompatible with the authoritative
[statement publication contract](statement-atomicity.md): namespace/journal
activity may change parent mtime, while unrelated leaves must remain unchanged.
The corrected stronger leaf assertion passes both frozen arms. The failed old
attempt is retained in `.temp/performance-v6/user-copy-final.csv`; it is not
counted as successful TCP acceptance or hidden as a candidate regression.

After persistent copies and in the capture-scope fixture with 1,000 unrelated
128-byte siblings plus an 8 MiB ballast file, **both images retain three private
files / 493 bytes**. The verifier also requires fewer than 128 private files,
less than 1 MiB total, and no retained 128-byte unrelated-X payload. These are
fixture-specific coarse safety bounds, not a hardcoded internal filename count
or a universal memory/storage guarantee.

The exact bd836d executable was additionally observed nine times by
`benches/idle_notice.py`: EOF → idle completion notice p50 **0.636 ms**, p95
**0.731 ms**. Every observation sends no input between EOF and notice, checks no
destination before EOF, exact published bytes, a still-live REPL, `/jobs` and a
clean exit. This independent typed-stdlib method is kept separate from the Rust
paired probe, whose before-image had no idle notice in all nine 150 ms windows;
there is no fabricated cross-method numerical speedup ratio.

Logs under `.temp/performance-v6/`: `user-copy-final4.csv`,
`user-repl-final4.csv`, `user-flow-final4.csv`, `idle-notice-final4.csv`,
`idle-notice-final4.log`, and the matching `final4-user-images-{start,end}.sha256`.

### Larger/pure work measured on optimized-3

The subsequent final-artifact patch only removes an unused index insertion; these
already accepted larger cohorts were not needlessly repeated. Exact optimized-3
results, two warmups and 15 paired samples unless stated:

| Journey | Before p50/p95 ms | Optimized-3 p50/p95 ms | Paired interpretation |
| --- | --- | --- | --- |
| 100-operation shared-source file plan, startup harness (30 samples) | 879.555 / 973.464 | 329.693 / 368.012 | Material complete-command planning improvement |
| 100-operation independent stdin plan | 1541.247 / 1690.436 | 423.755 / 458.957 | 15/15 faster; mean paired −1135.467 ms |
| Pure bool/string binding command | 4.692 / 5.020 | 3.728 / 4.075 | 15/15 faster; mean paired −1.078 ms |
| Persistent binding + cwd barrier (32 samples) | 0.128 / 0.217 | 0.091 / 0.131 | Sub-millisecond command interval |
| Static check of 1,000-item typed range | 5.496 / 7.222 | 5.844 / 6.625 | Only 4/15 faster; small fixed setup cost remains |
| Execute 1,000-item range and verified result | 41.901 / 45.499 | 42.537 / 44.538 | 10/15 faster, mean paired −0.776 ms; noisy mixed result, not a proven speedup/regression |

Raw records: `user-startup-final.csv` and `user-latency-final.csv`. Original probe
hashes and begin/end image identities are in `latency-probes-final.sha256` and
`user-images-{start,end}.sha256`. Earlier provisional images are not substituted
for either measured arm.

### Config discovery attribution, not a subtraction trick

`benches/config_startup.py` ran 30 paired same-image default/`--no-config` samples
on optimized-3, with isolated environments and no runtime/output effects. It uses
Python subprocess timing, so its absolute times are **not comparable** to the
Rust process harness above. Check p50 was 5.159 → 5.034 ms; plan 5.281 → 5.046 ms;
pure bindings 4.598 → 4.521 ms. Pair directions were mixed (13/30, 13/30 and
18/30 wins for `--no-config`), so these are weak small-effect comparisons.

One separate, non-timed JSONL startup profile reports arguments/configuration
**64.957 / 187.152 µs** with discovery versus **67.841 / 75.601 µs** without.
The measured configuration phase difference is approximately **111.551 µs**;
it does not explain or eliminate the complete small-command image/setup cost.
The profile's explicit JSONL exporter initialization is opt-in and must not be
charged to default-disabled tracing. Records: `config-startup-final.csv`,
`config-startup-attribution.log`, `config-startup-{default,no-config}.jsonl`.

### Opt-in exporter cost, exact optimized-3 image

Each comparison alternates the same executable's off/reference and enabled mode
for 15 samples, two warmups. Warm SQLite/JSONL append to one owned destination;
cold SQLite removes its DB/WAL/SHM before each enabled invocation. Configuration
and exporter code are unchanged by the final index patch; these remain explicitly
optimized-3 cost records, not relabeled final-artifact samples.

| Work | Exporter | Off p50 ms | Enabled p50 ms | Off / enabled p95 ms |
| --- | --- | ---: | ---: | --- |
| Static check, 100 operations | JSONL warm | 415.296 | 406.915 | 445.579 / 466.332 |
| Static check, 100 operations | SQLite warm | 425.663 | 532.631 | 449.530 / 558.510 |
| Static check, 100 operations | SQLite cold | 430.588 | 551.673 | 463.589 / 607.976 |
| 100 verified source diagnostics | JSONL warm | 10.563 | 11.557 | 11.628 / 12.493 |
| 100 verified source diagnostics | SQLite warm | 10.558 | 26.064 | 13.046 / 30.197 |
| 100 verified source diagnostics | SQLite cold | 11.015 | 31.683 | 12.894 / 35.646 |
| Actual 64 KiB copy | JSONL warm | 39.723 | 37.969 | 43.705 / 42.222 |
| Actual 64 KiB copy | SQLite warm | 38.039 | 48.120 | 41.566 / 51.686 |
| Actual 64 KiB copy | SQLite cold | 38.606 | 53.103 | 43.822 / 56.348 |

Negative JSONL differences are measurement variability, **not exporter speedups**.
SQLite has a real opt-in cost; do not promise free full tracing. The check case
has only six spans per run (17 warm sessions / 102 spans, no events), so its
approximately 107 ms warm difference must not be excused as a huge record count.
The diagnostic cohort has 3,400 events over 17 sessions. All 100 diagnostic
headings and finite-copy bytes are checked, and every SQLite cohort is inspected
with the actual read-only `telemetry summary` command; queryable session/span
counts and outcomes are retained in `telemetry-cost-final.csv`.

### Last discriminator: 202 paired persistent overwrites, crossed workspaces

To resolve the initial approximately 1 ms REPL point rather than either ignoring
it or repeating the entire matrix, the same final/before images ran 101 paired
operations per half. The second half starts new persistent processes and resets
fixtures, exchanging which image owns workspace A/B. Directories are always
allocated A then B; executable staging labels stay fixed, so image-to-workspace
assignment genuinely changes independently of directory creation order.
All commands retain exact-byte verification. Every one of the four persistent
processes retains three files / 493 private bytes after its 101 measured copies.

| Half | Before p50/p95 ms | Final p50/p95 ms | Final faster | Paired mean Δ ms | Paired median Δ ms |
| --- | --- | --- | ---: | ---: | ---: |
| Before=A, final=B | 27.099 / 32.921 | 26.809 / 33.159 | 52/101 | −0.133 | −0.057 |
| Before=B, final=A | 25.305 / 29.063 | 25.512 / 30.507 | 44/101 | +0.236 | +0.426 |
| Combined paired differences | — | — | 96/202 | +0.051 | +0.267 |

The full paired range is −21.788 to +11.028 ms. The opposite half means and
balanced aggregate do not reproduce a stable approximately 1 ms / 4% interval
regression; placement/storage-tail variation materially affects this journey.
This is not a claim of identical distributions, statistically proven equivalence,
a universal speedup, or a zero overhead guarantee. Raw differences are provided
instead of inventing confidence from a small initial sample. The small-command
fixed costs reported above remain acknowledged independently.

The swap-capable probe SHA-256 is
`57cc53452f6fc96d94a8750910d44caa8e78b92a7733feefc6a8f1a798247c81`.
Records under `.temp/performance-v6/`: `repl-crossed-normal.csv`,
`repl-crossed-swapped.csv`, `repl-crossed-differences.csv`,
`repl-crossed-images-{start,end}.sha256`, and `latency-probe-swap.sha256`.
All processes terminated successfully, and before/after probe and image hashes
compare byte-for-byte. No production source or measured image changed.

Selected reproduction commands (compile probes before taking a quiet window):

```sh
MSYNC_STARTUP_CASES=version,help,check/1,plan/1,repl/help-exit \
  target/performance-v6/startup-probe-final BEFORE FINAL 30
MSYNC_PERF_ONLY_FRESH=1 MSYNC_PERF_FRESH_CASES=run-missing-64k,run-existing-64k \
  target/performance-v6/latency-probe-followup BEFORE FINAL .temp 15
MSYNC_PERF_ONLY_FLOW=1 \
  target/performance-v6/latency-probe-followup BEFORE FINAL .temp 15
MSYNC_PERF_ONLY_REPL=1 MSYNC_PERF_REPL_ROUNDS=101 \
  target/performance-v6/latency-probe-swap BEFORE FINAL .temp 101
MSYNC_PERF_SWAP_WORKSPACES=1 MSYNC_PERF_ONLY_REPL=1 MSYNC_PERF_REPL_ROUNDS=101 \
  target/performance-v6/latency-probe-swap BEFORE FINAL .temp 101
python benches/idle_notice.py FINAL .temp/performance-v6/idle-notice.csv 9
python benches/config_startup.py FINAL .temp/performance-v6/config-attribution.csv 30
```

Named selectors fail on unknown cases rather than quietly producing zero samples.
Default harness behavior remains the full maintained workload; selection exists
only to avoid rerunning already accepted, unaffected cases during integration.

## Acceptance boundary

The integrating owner accepted the crossed-workspace discriminator and the
explicit small-command setup trade-off. The useful outcomes are much shorter
larger planning/compilation waits, removal of runtime activation for pure
bindings, improved pure-binding submission intervals, and unprompted sub-ms
background notice delivery. **Every command becoming faster was not a stated
SLO**, and neither the early negative points nor the opt-in SQLite costs are
removed from this record. Approximately 0.11 ms of discovery work is directly
profiled; remaining combined image/setup cost is not falsely causally assigned.

All final-artifact process groups and the independent idle probe terminated
successfully. Image/probe hashes were unchanged. The source assertions include
real file bytes, publication order, unchanged unrelated-leaf identity/metadata,
no input needed for completion, retained editor state, and bounded private
fixture capture. No Windows/WSL or authenticated SSH timing was performed;
these Linux ext4 observations do not establish cold-storage throughput,
power-loss durability, a universal tail bound, or whole-process RSS limits.

## Actual delivery-image audit: documentation-induced code layout

A final build audit found that a later README-only update changes the physical
executable despite identical Rust source: `lib.rs` includes README through the
crate documentation attribute, so crate metadata/code layout changes on rebuild.
The preceding bd836d measurements remain exactly bd836d evidence; they are not
silently relabeled as the newly generated artifact.

README and all Rust source were frozen before staging the actual release output
as `target/performance-v6/baselines/msync-v060-delivery`, SHA-256
**`1fc660d436c8c3d733f651a14a3eda89066aa0336a5f5294c79dc637fb815d8e`**.
The following minimal physical-image audit reuses the compiled selectors, skips
unchanged larger component/exporter/GUI matrices, and compares against the same
preserved ede9b2 before-image. Detailed results are written only under
`docs/design`, which is not a compiled README input.

| Actual delivery control | Before p50/p95 ms | Delivery p50/p95 ms |
| --- | --- | --- |
| Version, 30 paired samples | 2.513 / 3.016 | 2.577 / 3.116 |
| Help, 30 paired samples | 2.454 / 2.866 | 2.612 / 3.449 |
| Check one operation, 30 paired samples | 3.654 / 4.057 | 3.996 / 5.080 |
| Plan one operation, 30 paired samples | 3.717 / 4.250 | 4.089 / 5.042 |
| REPL help/exit, 30 paired samples | 2.797 / 3.295 | 3.226 / 3.905 |
| 64 KiB missing target, 15 paired samples | 38.264 / 43.042 | 41.133 / 44.255 |
| 64 KiB existing target, 15 paired samples | 42.546 / 44.794 | 40.778 / 44.377 |
| Persistent REPL overwrite, 32 paired samples | 27.297 / 31.107 | 27.397 / 34.905 |

The missing-target point is an unfavorable **+2.869 ms / 7.5% p50 observation**,
retained rather than dropped. Its paired mean is +1.763 ms with 6/15 faster pairs
and differences −8.934 to +10.099 ms. Existing-target copies have 8/15 faster
pairs, mean −0.359 ms. The persistent interval has 15/32 faster pairs and mean
+0.100 ms, with differences −7.742 to +10.234 ms: this audit does not reveal a
new stable approximately 1 ms interval regression from the physical layout change.
It also does not establish universal IO improvements or eliminate storage-tail
variability. Small command fixed setup increases remain explicit; the earlier
configuration profile is not falsely used to explain their entire difference.

All finite/persistent output bytes were correct; both persistent processes retain
three private files / 493 bytes. Nine independent exact-delivery idle observations
passed all no-input-after-EOF, no-destination-before-EOF, exact-byte, live-REPL and
clean-exit checks: p50 **0.606 ms**, p95 **0.689 ms**. No source change or Rust
rebuild took place inside this quiet audit. The frozen delivery copy and the
actual `target/release/msync` both match 1fc660 before and after the run; all
processes terminated successfully.

Records under `.temp/performance-v6/`: `user-startup-delivery.csv`,
`user-copy-delivery.csv`, `user-repl-delivery.csv`, `idle-notice-delivery.csv`,
`idle-notice-delivery.log`, and `delivery-user-images-{start,end}.sha256`.
The earlier aa4749 SQLite cost and bd836d crossed-workspace/leaf-integrity data
retain their own artifact identities. No claim is made that these old measurements
were rerun on 1fc660; the Rust source/algorithms are unchanged, while this section
operationally verifies the actual delivery image and its small physical controls.
