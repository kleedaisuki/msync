# Power-user Scope scan tails

The [tail-latency investigation](../../../../docs/design/performance-tail-v081.md)
separates the Native/process/session boundaries and shared-host interpretation.

This managed execution probe separates scalable VM/preparation work from CLI
startup and source compilation. It builds 8,192 fresh escaping records in native
Buffer storage, then scans the frozen membership four times. The `references`
case retains String/Path/Bool fields and therefore does not use numeric-page
storage. Those payloads are deliberately shared constants: this is a
reference-only bookkeeping/packing control, not a full manifest distribution.
The `computed` control uses four freshly calculated Number fields and
checks the strict binary64 result bit-for-bit.

The source uses a small `iota` domain rather than thousands of source literals.
Compilation and binding happen once outside measurement. Every measured call
creates a new interpreter and constructs/scans its own fresh records; this is
not a cached constant result, a retained-rows-only microbenchmark or Native AOT
startup. The source, residual construction/member sites, probe and five product
assembly hashes are retained in every report.

## Root cause and fix

Function-wide contextual applicability did not imply applicability at every
continuation. `Preparation.Run` always stops before a backward-edge target,
without interpreting any instruction. `Interpreter.RunOwnedCodeAsync` nevertheless
created a new Preparation at that target after every loop backedge whenever the
function contained a contextual call elsewhere.

A test-only counted Runtime mirror of the 8,192-row reference source recorded
8,197 preparations: 8,192 stopped immediately at `IteratorNext`, without any
rewrite. The position-aware applicability fix reuses the existing immutable
function/loop-header facts and reduces this to five preparations. All Step
counts remain identical. There is no per-iteration code scan or new mutable
execution/FS cache. Continuation resource waiting remains independent of whether
a new preparation can do useful work.

The frozen control is `spatial-common-4`. Its five product assemblies remain
unchanged except for the candidate Runtime, SHA256
`92908c6351bf9fadefa737ae6c6e38f66d8d7795cf9122f2745ce2c7fa44bba1`.
A strict Release Runtime-only build passes without warnings. The focused
`tests/Msync.EndToEnd/loop_continuation_journey.py` passes 24 real
source/object/executable journeys: zero/one/many scans, selected metadata and
transfers, acquire/release, a 3,670,016-byte publication before loop reads, and a
selected conversion failure preserving earlier completed writes. Artifact routes
execute after deleting their source and preparation never writes the outputs.

The initial 8,192-row/four-pass calibration checks one sample per image only:
215.46 versus 118.48 ms, 99,037,240 versus 4,654,144 allocated bytes, and 12 versus
zero Gen0 collections. These observations determine the measurement budget;
they are not a P95/P99 or stable throughput result. The earlier seven-pair
whole-process reference-tail increase is not established as a regression and is
not attributed to this structural work merely because the fix is beneficial.

Disposable functional attribution and journey receipts are under
`.temp/tail-runtime/`: `count8192-before.log`, `count8192-after.log`,
`count8192-{before,after}.json`, `loop-journey.json`, and the two
`*-8192-calibration.json` reports. The counted mirror is never a product input.

## Frozen hosts and alternating batches

Build against immutable assembly directories, without rebuilding the product:

```text
dotnet build tests/Performance/ScopeSpatial/Tails/Msync.ScopeSpatial.Tails.csproj -c Release -m:1 -nr:false -p:FrozenAssemblies=IMAGE -p:ArtifactsPath=OUTPUT
```

For one self-contained host:

```text
DOTNET_TieredCompilation=0 NO_COLOR=1 OUTPUT/bin/Msync.ScopeSpatial.Tails/release/msync-scope-tail-probe --case references --rows 8192 --passes 4 --warmups 5 --samples 1000 --root .temp/scope-tails/work --output .temp/scope-tails/report.json
```

The paired runner first emits one bound artifact outside measurement, then both
hosts read those exact bytes. This removes the previously observed per-process
semantic-layout/field-hint ordering variation from the Runtime comparison; both
source and artifact hashes must agree. Each host warms once and stays alive, then
alternates AB/BA execution batches of 32 samples. With 1,000 samples this gives
32 paired blocks; the final block contains eight samples. The waiting host is
blocked on stdin, not busy polling. No command parsing, pipe traffic, filesystem
output checks or timing report is inside the execution clock.

```text
python3 tests/Performance/ScopeSpatial/Tails/paired.py --baseline-host BEFORE/bin/Msync.ScopeSpatial.Tails/release/msync-scope-tail-probe --candidate-host AFTER/bin/Msync.ScopeSpatial.Tails/release/msync-scope-tail-probe --case references --rows 8192 --passes 4 --samples 1000 --batch-size 32 --warmups 5 --output .temp/scope-tails/paired-reference
```

All samples are kept. P50/P95/P99 use nearest rank, with no outlier deletion or
replacement run. Per-batch UTC/load observations identify execution order and
host drift; raw samples must be interpreted alongside block stability rather
than pooling away an order effect. P99 at 1,000 samples still has only ten
observations above it. This is an empirical tail estimate, not a distributional
bound.

`Sample` is a readonly value record in a preallocated array. The timed region
contains only `Interpreter.ExecuteAsync`; result verification and collection
counter queries are outside it. Exact process-wide allocation counters are read
at batch boundaries, outside timing and gate/control traffic. Collection counts
identify overlap with GC, not stop-the-world pause duration. No forced GC,
latency-mode switch, product telemetry or profiler is used during the final run.
The runner preserves stdout/stderr, commands, raw reports, hashes and partial
block history, and lets both hosts exit naturally after the requested samples.

## Accepted reference-control window

The one formal window contains 1,000 checked calls per frozen Runtime, grouped
into 32 alternating block pairs. Both hosts read the same bound artifact. All
other product assemblies are identical; the candidate Runtime is the loop-header
fix identified above. The timer includes fresh interpreter/record construction,
four scans and selected preparation, but not compilation/binding/artifact read.

| Measure | Baseline | Candidate |
| --- | ---: | ---: |
| P50 execution (ms) | 207.9227 | 119.2367 |
| P95 execution (ms) | 252.0044 | 162.0638 |
| P99 execution (ms) | 276.5914 | 187.4903 |
| Maximum execution (ms) | 342.5174 | 213.7677 |
| Mean allocated bytes/call | 99,039,918 | 4,656,539 |
| Gen0 collections across calls | 11,841 | 556 |
| Gen1 collections across calls | 284 | 445 |
| Gen2 collections across calls | 284 | 3 |

P95 falls by 89.94 ms and P99 by 89.10 ms; traffic falls by 95.3%. Every paired
block's median improves, with candidate-minus-baseline block-median differences
from -119.11 to -77.70 ms. Gen1 count **increases**, so this is not a claim that
every GC measure improves. Counts do not establish pause time. Neither this
managed result nor the shared reference payloads establish Native startup or
distinct-reference manifest behavior.

The original runner waited for the first host's READY signal before releasing
its first batch, but did not first observe the second host's READY. Possible
second-host warmup overlap with the first baseline block therefore cannot be
reconstructed. No sample was deleted or replaced. Baseline P95/P99 originate in
blocks 14/17, with none of its top 11 observations in block zero. Candidate
P95/P99 originate in blocks 24/21, with four of its top 11 in block zero; that
candidate batch starts only after the baseline batch completes. As a separately
labeled sensitivity calculation, excluding block zero gives P99 280.95 versus
180.75 ms, but the table above retains all 1,000 samples as its primary result.
The future runner now observes **both** initial READY signals before any RUN.

Complete records, source/artifact/assembly identities, stdout/stderr, block order,
load/UTC observations and exact original tooling are retained in
`artifacts/candidates/tail-latency/runtime-4199e9d/references-final/`. Its
`block-analysis.json` records rank origins and the sensitivity calculation;
`tooling/paired.py` SHA256 is
`02a3a1fb413472c6e58a3508c45725d05280b8c2be58391ea6f01a78353bbfbc`.
There was no profiler, forced GC, replacement window or CI timing threshold.

## Distinct-reference manifest discriminator

`--case manifest` uses `ManifestSource.Build`: every row receives a runtime-derived
`file-N` String, a distinct joined Path under `./files/`, a shared Bool and a
shared local Connection. A scan reads the flag, extracts the actual filename from
the Path, converts it to String and compares it to the stored name. No per-row
filesystem I/O is required. The expected result is `rows * passes`.

This workload exposed repeated successful specialization, not just empty loop
entry. In 32 rows/four scans, the private counted mirror records 128 caller
preparations at a source `filename` Call and 128 empty backward-Jump entries.
The actual Call has A=1, B=7, Text=null, selecting function #6; the selected
six-instruction source wrapper contains `NativeCall fs.filename` but no planning
opportunity. Direct linear emission alone reduces a matched cold allocation
calibration from 5,910,640 to 2,923,616 bytes. A bounded exact-source-call decision
then reduces that to 455,040 bytes. It does not elide actual invocation, assert
purity, or infer transitive effects: calls with a planning-capable selected target,
future GetIterator/metadata/I/O, native/unresolved targets or open-session policy
remain on their existing preparation path.

The one 8,192-row/four-scan discriminator returns 32,768 exactly. The emission /
region version allocates 625,415,320 bytes; the bounded-call version allocates
86,723,704 bytes on the identical bound artifact
`a01632c1ba9e4d2510748eb0603f45977e9cf5a845821b0731a675800b27aeeb`.
These are allocation/functional calibrations, not P95/P99 batches: their cold
managed timings include JIT and must not be represented as Native latency.
The earlier 1,000-sample reference control was not rerun or replaced.

The final metadata controls also exposed caller publication racing callee entry
preparation. The product fix admits selected observations against existing work
at their actual frontier. Held-process tests check prefix progress before waiting,
process-flow deferral, known-false branches and unrelated ready sibling reads;
actual source calls still mutate counters and perform opaque I/O exactly once.
See the [backend ownership note](../../../../docs/design/contextual-io-backend.md)
and `tests/ScopeLayout/CallPreparationChecks.cs`.

All calibration sources, bound bytecode, raw counts/allocations, implementation
hashes and final selected CLI receipts are linked by
`artifacts/candidates/tail-latency/runtime-cbb6e9e/calibration/index.json`.
The reference-control window's exact original runner remains separately archived;
the current paired runner contains the future two-READY startup correction.
