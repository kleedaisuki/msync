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

The default is eleven applications, not every optimizer experiment. Four
overlapping geometry/layout motifs (`band-classification`, `segment-screening`,
`literal-segment-parity`, `ordered-calibration`) remain available through an
explicit `--case`, `--family compute`, or `--include-diagnostics`; their semantic
coverage is retained, but they no longer overweight the default scorecard.
`--list` shows every case with its role. The shared role catalog is
[`catalog.py`](catalog.py); selection is a starting point, not a workload census.

| Workload | Standard work | Distinct behaviors | Independent oracle |
| --- | ---: | --- | --- |
| `polygon-queries` | 16,384 points × 12 edges | Concave containment, nested iteration, Scope field reads, data-dependent branches | Python rational ray intersection and boundary test |
| `signal-hysteresis` | 65,536 samples × 3 detectors | Burst/noise stream, fixed-point recurrence, mutable captured state, hysteresis | Independent integer weighted-numerator recurrence |
| `numeric-record-stream` | 32,768 interval records | Input-dependent Scope construction, materialized Range membership, scalar field reads, ordered checksum | Python exact integer intervals and selected widths |
| `set-dedup` | 8,192 events + 4,096 queries/expirations | Runtime-fed duplicate-heavy event IDs, mixed hits/misses/removal, snapshot retained across expiration | Python set plus ordered membership checksums and exact operation counts |
| `bplus-inventory` | 3,072 transactions | Hot/cold reads, updates, deletion, ordered snapshot and live payloads | Python dictionary and ordered traversal |
| `heap-scheduler` | 4,096 arrivals | Bursts, stable equal-priority ordering, nested Scope payloads, backlog/drain | Python heap and exact lane totals |
| `bounded-sensor-pipeline` | Up to 12,000 accepted readings | Stdin parse, lazy transform/filter/take, order-sensitive reduction and discarded tail | Python eager selection and adjacent-difference reduction |
| `live-policy-dispatch` | 28,000 mixed records | Three actual targets at one call site, closure capture updates, skewed dispatch | Python stateful rule evaluation and ordered checksum |
| `unicode-path-manifest` | 65,000 path records | ASCII/BMP/astral/combining text, extension/stem/filename, mixed lexical predicates and lengths | Python POSIX lexical splitting, Unicode/UTF-8 counters and ordered checksum |
| `local-manifest-copy` | 144 manifest records | Local metadata, directories and missing sources, overwrite, many small and some medium files | Exact stdout, output path set, negative paths, and SHA-256 of every copied payload |
| `local-range-copy` | The same 144-record manifest | Materialized finite Path selection, one cohort transfer, flat destination, overwrite | Exact stdout, flattened unique path set, negative paths and SHA-256 of every payload |

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
  --scale small --scale standard --seed 17 --seed 503 \
  --pairs 5 --warmups 1 --work .temp/workload-corpus
```

- With no candidate the runner establishes a checked single-version baseline.
  `--case` and `--family` select relevant workloads without an indiscriminate
  full matrix. `--scale smoke`, `small`, `standard`, and `stress` are 1/64,
  1/4, 1, and 4 times the case's standard input, rounded down with minimum 1.
  Seeds 17 and 503 are the default development examples; include a different
  seed and scale when checking a proposed general optimization.
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
