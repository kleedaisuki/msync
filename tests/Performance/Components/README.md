# Native component profile

This probe calls the production parser, compiler, optimizer, artifact codec,
contextual lowering and interpreter APIs directly. It has no alternate evaluator
and does not execute filesystem observations or transfers. The two value-oriented
workloads finish by returning an actual Path; the expected value is checked
before measuring VM execution.

```sh
dotnet publish tests/Performance/Components/Msync.Components.csproj \
  -c Release -r linux-x64 \
  --artifacts-path .temp/performance-v080/current/components/build \
  -o .temp/performance-v080/current/components/native
.temp/performance-v080/current/components/native/msync-components \
  --sizes 1,64,256,1024 --samples 31 --warmups 3 \
  --output .temp/performance-v080/current/components/baseline.json
```

On Windows, publish for `win-x64` and invoke `msync-components.exe`; the APIs and
fixtures are otherwise identical. Use `--stages parse,artifact_decode,vm_no_io`
to select only the affected components after a change. Coordinate with other
benchmark owners before building or collecting samples.

Three shapes distinguish different costs:

| Shape | Constants and locals | Measured VM work |
| --- | --- | --- |
| `transfers` | Unique source/target constants, no local bindings | Not executed |
| `generic_values` | One typed function, N retained Path bindings | Call and join paths |
| `scope_values` | Same function selected through a lexical Scope | Member lookup, call, join |

`compile_o0` includes parsing, inference/lowering and basic-block construction;
`compile_o1` additionally includes the normal optimizer. There is no separate
public inference/lowering entrypoint, so those phases are not assigned invented
individual timings. `artifact_encode` includes the production validation and
in-memory serialization; `artifact_decode` includes validation and block
construction. `artifact_validate` and `form_blocks` isolate those mechanisms.
`bind_and_lower` includes contextual binding and execution lowering, while
`execution_lower` operates on an already-bound module. Cumulative stages are not
summed as though they were disjoint.

One calibration invocation selects 1–256 operations per timed sample, targeting
about 2 ms. Three untimed batches warm the static APIs; 31 sample values supply
nearest-rank p50/p95 and retain every observation. Allocation bytes/op are
cumulative managed allocation traffic measured in a separate batch, not RSS or
live heap size. Gen0/1/2 collection counts cover the timed interval. The final
result is retained to prevent eliminating returned values. Fixture generation,
artifact setup, source identity, expected-result checks and report serialization
occur outside timed intervals.

Reports include the native image SHA-256 and the actual compiler/Core/runtime
assembly informational versions, including their build commits. The checkout
commit at execution is recorded separately because an independently frozen image
can run after the shared checkout has advanced. A managed build may check probe
correctness, but its timings are not production-performance evidence.

`msync-components --check-interner` runs the focused constant-index contract
without timing: first-insertion order, duplicate identity, rollback of a failed
submission, source-free artifact execution, and retained runtime values.

`--check-snapshots` exercises closure capture, lexical shadowing, recursion,
Scopes, deferred Range arguments and null refinement after `.mso`/`.mexe`
serialization. `--emit-capture` writes one deterministic relocatable capture
artifact to binary stdout. The driver checks both contracts and exact artifact
bytes from two independent native processes:

```sh
python3 tests/Performance/Components/verify.py \
  .temp/performance-v080/current/components/native/msync-components
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
same probe source can be built against two product revisions; no new production
API or alternate evaluator is needed. All default workloads are valid in the
previous C# baseline: straight-line generic calls, nested Scope factories with
captured methods, finite literal loops, and repeated branch expressions. The
optional `mutable_scopes` workload uses the new mutable-field contract and is
candidate-only, not a fabricated old-language comparison.

The explicit `call_dag` case builds valid direct-call bytecode:
`f0(v) = v + 1`, `fN(v) = fN-1(v) + fN-1(v)`, entry `fN(2)`.
Here `--sizes` denotes depths (use `4 8 12`, not ordinary binding counts).
There is no invented source-compilation timing for this case. Its optimize,
bind and actual execution phases expose expansion before DCE in linked/object
programs; the expected Number is independently `3 * 2^depth`. This complements
source functions that capture their callee and therefore exercise a different
inlining path. A large final-IR reduction does not justify exponential optimizer
work or retained dead local slots.

The explicit `transfers` case compiles ordinary straight-line file transfers,
without declaring unused values. Use `--case transfers --sizes 128 1024` in the
paired driver to inspect the dominant I/O-script compiler workload. It measures
compilation, raw optimization and binding only, and validates both typed codec
roundtrips; it does not execute fixture paths that do not exist. Actual transfer
bytes and publication behavior belong to the separate CLI/scheduler journeys,
not a fabricated zero-I/O VM timing for this case.

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
same O0 module, contextual binding, and actual relocatable/executable VM runs at
each O0–O3 level. These stages overlap in responsibilities and must not be summed
as disjoint phase times. p50/p95 use linearly interpolated empirical quantiles;
all observations, allocations and timed GC counts are retained.

Both stages record constants, functions, locals, blocks, instructions, opcode
counts and serialized byte sizes. Every level is executed before measurement,
including `.mso`/`.mexe` codec roundtrips, and checked against an independent
expected Path suffix (or the direct-call Number oracle). Scope instances are
recreated by the production runtime, not cached by the probe. Fixture writing,
verification and serialization for
shape reporting are outside timed intervals. The run phases contain no user
filesystem I/O, isolating interpreter and graph work; the actual CLI and copy
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
