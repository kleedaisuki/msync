# Native component profile

This diagnostic probe calls the canonical parser, typed HIR analysis/lowering,
native optimizer, native artifact codec, contextual preparation and native VM.
There is no historical evaluator or generic-object fallback. It is not a default
application benchmark or evidence of a general VM speedup.

```sh
dotnet publish tests/Performance/Components/Msync.Components.csproj \
  -c Release -p:RuntimeIdentifier=linux-x64 --artifacts-path artifacts/component-probe-build \
  -o artifacts/component-probe
artifacts/component-probe/msync-components \
  --sizes 1,64,256 --samples 31 --warmups 3 \
  --output .temp/component-probe/results.json
```

Use `--stages parse,analyze,lower_native,artifact_decode,vm_no_io` for a changed
question. The default source shapes remain transfers (never executed), ordinary
Path-returning calls, and calls selected through lexical namespaces. Result Path
oracles run before measurement. No transfer writes occur during preparation;
contextual metadata queries may occur and are part of that stage's cost.

| Stage | Actual boundary |
| --- | --- |
| `parse` | Source to syntax document |
| `analyze` | Source to immutable semantic HIR, including parsing/binding |
| `lower_native` | Already analyzed HIR to unoptimized native IR |
| `compile_o0` / `compile_o1` | Complete source compilation at that level |
| `optimize_o1` | Native portable optimizer on the same O0 input |
| `artifact_validate` | Native portable contract validation |
| `artifact_encode` / `artifact_decode` | Production codec including its validation boundary |
| `prepare_native` | Context-selected computation and native I/O planning |
| `runtime_initialize_dispose` | Native execution image construction and idle disposal, no guest execution |
| `vm_no_io` | Fresh interpreter construction, execution and cleanup of the prepared program |

Retired `form_blocks` and `execution_lower` stages are rejected, not assigned
fabricated timings. `Blocks` is JSON null: the native register program has no
old materialized block table. Shapes report actual slots, instructions, native
opcodes and encoded bytes. Cumulative stages overlap; neither sum them nor
subtract route medians to invent disjoint phase costs.

Calibration chooses 1–256 operations per sample, targeting roughly 2 ms. Reports
retain all wall-clock samples, empirical p50/p95, separately measured managed
allocation traffic, and timed GC counts. These are not RSS, retained memory or
CPU time. Setup/oracles/serialization are outside timed batches. Build/image
hashes and compiler/Core/runtime informational versions are recorded separately
from the checkout revision. Managed runs validate contracts, not production AOT
latency. Guest tier policy comes from the explicit `MSYNC_JIT` environment; use
matched policy and frozen complete payloads for comparisons.

`--check-interner` checks per-image first-insertion order and duplicate identity,
failed-vs-clean submission constant-table equality, and actual persistent values.
Constant IDs are image-local: native session projection may discard unused old
constants, so cross-generation prefix indices are not a language contract.
`--check-snapshots` preserves source-free closure/capture/shadow/recursion/Scope/
lazy Range/null-refinement oracles. `--emit-capture` emits one deterministic
native artifact. `verify.py` checks both and fresh-process byte identity while
hashing adjacent executable dependencies as well as the apphost.

```sh
python3 tests/Performance/Components/verify.py artifacts/component-probe/msync-components
```

## Typed editor analysis

`--editor-analysis` runs the actual editor parser, compiler analysis and lexical
context query independently of JSON-RPC framing. It emits CSV to stdout with
p50/p95 milliseconds and current-thread allocation bytes per operation:

```sh
dotnet run --project tests/Performance/Components -c Release -- \
  --editor-analysis --sizes 32,128,4000 --samples 15 --warmups 5
```

The scalar, literal-Path and generic Scope-return workloads distinguish common
single concrete values from shape/callable metadata. Setup checks that every
expected binding is visible at the final declaration boundary. The parse,
analyze, end-context and beginning-context measurements are separate, not
summed as disjoint costs. These measurements are synchronous; the retained
result prevents dead-result elimination. Use `--stages parse,analyze` to select
only the changed stage. A managed run explains allocations and causal changes;
matched Native AOT images and real LSP requests establish user-visible latency.
Coordinate timing windows and freeze the full adjacent assembly set before
building another candidate. For managed comparisons, keep tiered compilation
and warm-up policy identical (the follow-up study used
`DOTNET_TieredCompilation=0`).

## Staged optimizer cost and residual program

`--optimizer-pipeline` measures the optimizer itself as well as its output. The
same probe source can be built against native-ABI-compatible revisions; no
alternate evaluator is needed. Source shapes are straight-line generic calls, nested Scope factories with
captured methods, finite literal loops, and repeated branch expressions. The optional `mutable_scopes` workload exercises nullable field updates. Old
object-backend probe binaries remain historical experiments; these native API
sources do not promise to build against their deleted assembly contract.

The explicit `call_dag` case builds valid typed native register IR:
`f0(v) = v + 1`, `fN(v) = fN-1(v) + fN-1(v)`, entry `fN(2)`.
Here `--sizes` denotes depths (use `4 8 12`, not ordinary binding counts).
There is no invented source-compilation timing for this case. Its optimize,
preparation and actual execution phases expose expansion before DCE in native
programs; the expected Number is independently `3 * 2^depth`. This complements
source functions that capture their callee and therefore exercise a different
inlining path. A large final-IR reduction does not justify exponential optimizer
work or retained dead local slots.

The explicit `transfers` case compiles ordinary straight-line file transfers,
without declaring unused values. Use `--case transfers --sizes 128 1024` in the
paired driver to inspect the dominant I/O-script compiler workload. It measures
compilation, raw optimization and native preparation only, and validates both typed codec
roundtrips; it does not execute fixture paths that do not exist. Actual transfer
bytes and publication behavior belong to the separate CLI/scheduler journeys,
not a fabricated zero-I/O VM timing for this case.

The explicit `finite_sum` case consumes the result of its loop rather
than measuring unused work: a zero-argument function adds 1 through N in order,
and the caller selects the expected Path only if its result is `N * (N + 1) / 2`.
Use `--cases finite_sum --sizes 200 --levels 1,2,3` on one native image.
Whether a particular pipeline evaluates or retains the loop is observed in its
IR, not presumed by this case. The value must be consumed; a wrong sum takes the independently rejected Path.
This is a new mutable-language workload, so the paired driver exposes it only
as `--candidate-case finite_sum` when an older product control is also present.

### Cross-block memory and compact layouts

Three explicit workloads compare the same production source across compatible
native revisions; they do not require a benchmark-only runtime API:

| Case | Consumed work and interpretation |
| --- | --- |
| `memory_branches` | N distinct Scope instances live across selected branches; all resulting fields determine the returned Path. One `fs.exists` query supplies a runtime condition, so the run phases include that same metadata query in both images. |
| `memory_loop` | A Scope's `value` and `total` fields carry through a finite loop and branch. The returned Path depends on the exact sequential sum. Successful field scalarization can expose further pure evaluation. |
| `layout_factory` | A source factory returns a lazy Range of fresh three-field Scopes. The probe consumes the production Range delegate and reads every instance's file field; it measures actual construction rather than only returning an unevaluated adapter. |

Use `--cases memory_branches,memory_loop,layout_factory --sizes 1,32,128
--levels 0,2` in the component executable, or select them individually with
`--case` in `tests/Performance/optimizer_pipeline.py`. Defaults and the existing
ordinary-call/transfer controls are unchanged. The branch fixture is created
outside timing under the selected root's `.temp/memory-performance`; it is not
a frozen filesystem proof. `layout_factory` run measurements include the same
small asynchronous consumption/count/last-value driver in both images. Count
and final-Path assertions, artifact encoding and all codec roundtrips remain
outside timed sample loops.

The separate CLI journey is:

```sh
python3 tests/Msync.EndToEnd/memory_layout_journey.py --binary /path/to/msync
```

It executes actual source, `.mso` and `.mexe` at explicit O0 and O2 with exact
binary file payloads. Both branch edges, loop-carried fields, disjoint called
objects, captured field cells, multiple live instances from one constructor
site, selected/unselected deferred callbacks and selected failures are covered.
The executable is launched from a different directory than its bound origin.
Use `--case NAME` for a changed contract instead of rerunning unrelated cases.
The default also includes two frontend regressions found while constructing the
memory journey: nullable Scope joins must retain field metadata, and repeated
loop joins must preserve unchanged metadata identities. `--baseline-compatible`
excludes those two cases when exercising the frozen previous native image;
it does not weaken the candidate's default acceptance.

```sh
/path/to/native/msync-components --optimizer-pipeline \
  --sizes 1,32,128,512 --levels 0,1,2,3 --samples 15 --warmups 3 \
  --output .temp/optimizer-performance/components.json

python3 tests/Performance/optimizer_pipeline.py \
  --baseline /path/to/baseline/msync-components \
  --candidate /path/to/candidate/msync-components \
  --baseline-source BASELINE_REVISION --candidate-source CANDIDATE_REVISION \
  --sizes 1 32 128 512 --samples 15 --candidate-case mutable_scopes \
  --output .temp/optimizer-performance/paired.json
```

The paired driver alternates AB/BA fresh processes. Inside each process the
probe warms each API independently and calibrates repeated operations to about
2 ms per sample. It reports complete source compilation, optimization of the
same O0 native program, native contextual preparation, and actual relocatable/executable VM runs at
each O0–O3 level. These stages overlap in responsibilities and must not be summed
as disjoint phase times. p50/p95 use linearly interpolated empirical quantiles;
all observations, allocations and timed GC counts are retained.
The optimizer report retains its `bind` JSON stage key for existing consumers;
it now measures `NativePreparation.Prepare`, not the retired object binding API.
`run_relocatable` includes contextual preparation; `run_executable` does not.

Both stages record constants, functions, slots, unavailable legacy block count, instructions, opcode
counts and serialized byte sizes. Every level is executed before measurement,
including `.mso`/`.mexe` codec roundtrips, and checked against an independent
expected Path suffix (or the direct-call Number oracle). Scope instances are
recreated by the production runtime, not cached by the probe. Fixture writing,
verification and serialization for
shape reporting are outside timed intervals. The run phases perform no transfers;
`memory_branches` includes its documented metadata query. Actual CLI and copy
journeys remain necessary for user-visible latency and I/O conclusions.

Use `--case`, `--sizes` and `--levels` to test a scaling hypothesis without
repeating accepted measurements. The paired report is checkpointed before and
after each image; an exception retains previous samples and the active case.
Builds, editor hosts and other benchmarks must be stopped during the agreed
measurement window. Component and CLI native identities are distinct and are
never substituted for one another.

`tests/Msync.Compiler.Tests --editor-lookups` compares a selected `Resolve`
result with full `At` output for lexical bindings, shadows, incomplete functions,
Scope/factory members, polymorphic common members, Path joins and Process fields.
It also checks absent names stay absent. Resolution uses the same raw semantic
context selection, but materializes only the requested value.
