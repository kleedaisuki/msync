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

## Cases

| Workload | Standard work | Distinct behaviors | Independent oracle |
| --- | ---: | --- | --- |
| `polygon-queries` | 16,384 points × 12 edges | Concave containment, nested iteration, Scope field reads, data-dependent branches | Python rational ray intersection and boundary test |
| `signal-hysteresis` | 65,536 samples × 3 detectors | Burst/noise stream, fixed-point recurrence, mutable captured state, hysteresis | Independent integer weighted-numerator recurrence |
| `numeric-record-stream` | 32,768 interval records | Input-dependent Scope construction, owned vector published as a Range, scalar field reads, ordered checksum | Python exact integer intervals and selected widths |
| `band-classification` | 8,192 samples × 8 bands | Live mutable Scope bounds, two ordered projections, conditional weighting and rolling checksum | Python half-open interval membership and equivalent integer score |
| `segment-screening` | 6,144 samples × 8 segments | Mostly crossing samples, horizontal and reversed segments, changing X endpoints, ordered hit checksum | Python exact rational interpolation over open segments |
| `literal-segment-parity` | 4,096 samples × 19 segments | Odd-sized literal producer, horizontal/reversed segments, mixed layout with unused String, dynamic initial parity | Python exact rational interpolation and parity |
| `ordered-calibration` | 2,048 readings × 129 rows | Ordered binary64 recurrence, actual literal Scope projections, invariant reading and changing initial accumulator | Python sequential recurrence, quantized score, ordered checksum |
| `bplus-inventory` | 3,072 transactions | Hot/cold reads, updates, deletion, ordered snapshot and live payloads | Python dictionary and ordered traversal |
| `heap-scheduler` | 4,096 arrivals | Bursts, stable equal-priority ordering, nested Scope payloads, backlog/drain | Python heap and exact lane totals |
| `bounded-sensor-pipeline` | Up to 12,000 accepted readings | Stdin parse, lazy transform/filter/take, order-sensitive reduction and discarded tail | Python eager selection and adjacent-difference reduction |
| `live-policy-dispatch` | 28,000 mixed records | Three actual targets at one call site, closure capture updates, skewed dispatch | Python stateful rule evaluation and ordered checksum |
| `unicode-path-manifest` | 65,000 path records | ASCII/BMP/astral/combining text, extension/stem/filename, mixed lexical predicates and lengths | Python POSIX lexical splitting, Unicode/UTF-8 counters and ordered checksum |
| `local-manifest-copy` | 144 manifest records | Local metadata, directories and missing sources, overwrite, many small and some medium files | Exact stdout, output path set, negative paths, and SHA-256 of every copied payload |

SHA-256 in the copy oracle runs only in the Python harness outside the
measured guest. There are no guest bit-operation workloads in this corpus.
The earlier [specialized LibraryMatrix](../LibraryMatrix/README.md) keeps
SHA and its other diagnostics independently runnable.

The current heap scheduler drains through the public Heap API. Its payload
table and the numeric record stream use vector owners; the record stream
publishes its completed membership as a Range before iteration. Compare these
fixtures using images with the owner API. For pre-owner results, use the
original fixture and frozen image recorded with that measurement.

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

Use unrelated families and non-hit controls when making a broad claim.
For example, a word-operations win must stay labeled as such; a new region
backend should also show what happens on closures, text, mixed containers,
and workloads whose dominant cost is file I/O. A failing oracle is an invalid
timing sample, not an acceptable trade for performance.

## Initial acceptance

Two frozen Linux x86-64 Native AOT images passed every workload on the
source, object and executable smoke routes. At standard scale both seeds
passed on both images, with 80 alternating timed pairs and independent
SQLite profiles. All eight workloads also passed at 4x scale on a third
seed. The receipts include actual non-hit controls and one instructions
profile demonstrating three selected targets at one call site. See the
[baseline receipt](../../../artifacts/benchmarks/workload-corpus-v086/README.md).
