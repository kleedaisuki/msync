# General-purpose execution workload corpus

This is an end-to-end suite for changes spanning the compiler, executable
preparation and VM. It complements the narrower LibraryMatrix and the
existing interactive, editor, copy, SSH and scheduler journeys. In
particular, SHA-256 and scalar recurrences remain useful diagnostics; they
are not counted as evidence of a general VM speedup.

The workload selection follows the principle used in
[DaCapo](https://doi.org/10.1145/1167473.1167488) and
[Renaissance](https://d3s.mff.cuni.cz/publications/prokopec_renaissance_2019_2/):
select applications with materially different dynamic behavior and inspect
their measured operation mix. This corpus uses deterministic generated
inputs, not a claim about the frequency of workloads in production.
Keep the I/O, mixed application and computation results separate rather
than producing a single weighted score without a real workload distribution.

## Core applications and explicit diagnostics

The default is six application families. Set is the only collection case.
The two numeric representatives exercise different mechanisms: stateful
scalar recurrence and numeric records that are materialized, filtered and
then reduced. B+, Heap, polygon and the old scan/fold cases stay available as
engineering diagnostics, not extra votes for a general speedup. The legacy
copy cases remain explicit controls; the default I/O group owns deployment.
`--list` shows every case and its role; see [catalog](catalog.py).

| Default case | Reference shape | Contrast at the same scale | Observed input characteristics |
| --- | --- | --- | --- |
| `set-dedup` | Hot duplicate-heavy IDs, 1/4 expiration actions in the generator | `broad-expiring-window`: wider uniform ID space and 1/2 expiration actions | Distinct and duplicate IDs; lookup and expiration hits/misses |
| `signal-hysteresis` | 32-sample bursts every 128 samples | `long-bursts`: 512-sample bursts every 1,024 samples | Burst width/period and detector transitions |
| `numeric-record-stream` | Retain six of every seven materialized rows | `sparse-selection`: retain one of every seven | Materialized and selected rows |
| `bounded-sensor-pipeline` | About 20% rejected readings | `high-rejection`: about 70% rejected, longer input for the same demand | Supplied, demanded, accepted and rejected rows |
| `live-policy-dispatch` | Mixed call targets (5:3:1 apart from updates) | `dominant-policy`: about 95% normalize calls | Calls per target, configuration updates |
| `unicode-path-manifest` | 1/5 of ordinary basenames drawn from the Unicode pool | `unicode-heavy`: 4/5 drawn from the Unicode pool | Actual non-ASCII name count and UTF-8 basename bytes |

A shape specifies a generator, not a production probability. Standard size
and seed are independent axes. The
[YCSB configuration](https://github.com/brianfrankcooper/YCSB/blob/master/workloads/workload_template)
is an example of separating population size, key distribution and operation
mix. Each contrast here keeps the operation and its
independent oracle; do not pool across shapes to make a generic speedup.
The legacy reference fixtures are unchanged. The report indexes shape and
its observed characteristics, in addition to fixture identity and timings.

The copy cases share source distributions, not destination semantics: the
explicit loop preserves manifest directories, while the cohort targets a flat
directory with unique basenames. Report both as different user operations, not
an isolated loop-versus-cohort speed ratio. Both restore absent/stale outputs
before every invocation, so a no-op cannot pass by retaining previous bytes.

Explicit diagnostics keep the original standard sizes: band 8,192 × 8,
segment 6,144 × 8, literal parity 4,096 × 19, and ordered calibration
2,048 × 129. They isolate mutable-row, crossing-arm, odd-producer and strict
IEEE recurrence mechanisms. They are not additional independent application
families and should not be pooled into a generic throughput score.

SHA-256 in the copy oracle runs only in the Python harness outside the
measured guest. There are no guest bit-operation workloads in this corpus.
The earlier [specialized LibraryMatrix](../LibraryMatrix/README.md) keeps
SHA and its other diagnostics independently runnable.

The heap scheduler queues ordinary records containing their requests through
the public Heap API, ordered by priority and arrival. It does not retain a
separate all-arrivals table after completed requests leave the queue. Its eight
lane accumulators are ordinary Scope references in a materialized Range: lazy
literal re-enumeration must not allocate fresh counters for each update. The numeric
record stream materializes a record-producing Range before consumption. Neither
fixture uses an allocator owner API. Historical owner-based receipts describe
their original source; regenerate the same current fixture for both images in
a new comparison rather than treating different source shapes as a speed ratio.

## Run

```sh
python3 tests/Performance/Corpus/run.py --list
python3 tests/Performance/Corpus/run.py \
  --baseline .temp/old/msync --candidate .temp/new/msync \
  --distribution both --scale small --scale standard --seed 17 --seed 503 \
  --pairs 5 --warmups 1 --work .temp/workload-corpus
```

- With no candidate the runner establishes a checked single-version baseline.
  `--case` and `--family` select relevant workloads without an indiscriminate
  full matrix. `--scale smoke`, `small`, `standard`, and `stress` are 1/64,
  1/4, 1, and 4 times the case's standard input, rounded down with minimum 1.
  Seeds 17 and 503 are the default development examples; include a different
  seed and scale when checking a proposed general optimization. The direct
  corpus entry defaults to `--distribution reference` for reproducibility;
  the suite's unfiltered applications group defaults to `both`.
- Each product and its adjacent native/managed dependencies are frozen before
  execution, fingerprinted in the report and checked again at completion. A
  concurrent rebuild cannot silently replace a measured image. Object/bound
  production and input setup are outside timing; these routes share a fixture
  workspace with source, so this harness does not establish source-free execution.
- `--route bound` (default) compiles and binds each version separately, then
  times fresh CLI execution from that version's executable, including process
  startup and artifact load but not preparation. Separate compile/bind wall
  times and artifact hashes are recorded. `--route source` times the entire
  source pipeline and `--route object` times execution starting from the
  portable .mso. `--route both` reports source and bound separately;
  `--route all` covers all three routes. `--same-artifact` supplies the
  baseline's artifact to both VMs when wanted; it is not the default or
  a required ablation.
- Oracle checks run on every invocation, after wall timing. Overwritten outputs
  are restored outside the measured interval. The runner does not evict the
  OS page cache or claim disk/WAN bandwidth. Linux `--cpu N` optionally
  pins the measurement process; a pinned copy result is not multi-core copy
  throughput. Use matching deployment settings for both images.
- Timings are telemetry-off, alternating AB/BA. One separate checked
  execution per route and version collects an opt-in SQLite profile; choose
  `--profile instructions` for site/edge data or `--profile off` for a
  correctness-only large-input run. JSON records raw pairs, per-variant
  quantiles, paired median change, host/binary/input identity, inclusive
  phase timings, allocations, frame counts, and leading opcodes/native
  operations. Keep overlapping phases and telemetry-on times separate from
  telemetry-off wall time. Small samples are evidence, not precise tails.
- Disposable generated sources, inputs, oracles, artifact files, and SQLite
  remain under the selected repository-root `.temp` directory. The report
  checkpoints completed fixtures and captures failures. Retain release
  comparison receipts in `artifacts/benchmarks`; don't install the harness
  as a product dependency or enforce timing thresholds in CI.
- A failed route retains its successful raw samples and completed profiles,
  the active variant/pair and warmup/timing/profile phase. Process failures
  also retain the full command, cwd, exit status, stdout/stderr or timeout
  context. Empty or partial routes are not presented as complete comparisons.

Use unrelated families and non-hit controls when making a broad claim.
For example, a word-operations win must stay labeled as such; a new region
backend should also show what happens on closures, text, mixed containers,
and workloads whose dominant cost is file I/O. A failing oracle is an invalid
timing sample, not an acceptable trade for performance.

## Historical acceptance

Two frozen Linux x86-64 Native AOT images passed every workload on the
source, object and executable smoke routes. At standard scale both seeds
passed on both images, with 80 alternating timed pairs and independent
SQLite profiles. All eight workloads also passed at 4x scale on a third
seed. The receipts include actual non-hit controls and one instructions
profile demonstrating three selected targets at one call site. See the
[baseline receipt](../../../artifacts/benchmarks/workload-corpus-v086/README.md).
