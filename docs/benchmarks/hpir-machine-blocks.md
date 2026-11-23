# HPIR native blocks: isolate dispatch from the guest ABI

2026-11-22 · Linux x64 bring-up of a machine backend over the same physical
HPIR image. This is a mechanism test, not the v0.9.5 release performance
result. Refer to the [published baseline manifest](hpir-0.9.3-baseline.md) for
the broader gate and image provenance.
The [platform and admission gate](hpir-platform-gate.md) separates actual
cross-target execution from publish and code-emission cost from guest work.
See [function-owned adaptive code cache](#function-owned-adaptive-code-cache)
for the production follow-up.

## Removing the frame layer is necessary but not sufficient

The physical [HPIR interpreter](../../src/Msync.Runtime/Hpir/HpirExecutor.cs)
avoids managed guest frames and generic values. It still decodes one
instruction per dispatch and distinguishes direct/indirect operands on each
access. The [producer](../../src/Msync.Hir/Hpir/LlvmHpirLowerer.cs) materializes
a frame location for nearly every LLVM result in the first implementation.
The O2 signal loop function consumes a 1,064-byte frame. This is concrete
storage, not yet effective storage or register reuse.

v0.9.3's [machine backend](../design/native-execution-lowering-v090.md)
already had direct branches, register regions and some register-passed
calls. The [attribution](0.9.3-runtime-attribution.md) showed it loses the
whole fast path for some prepared functions and bridges many other guest
calls through managed code. A switch interpreter alone will not
predictably beat both. Carry the physical ABI into machine code instead
of reviving leaf eligibility or serializing a parallel SSA stream.

[Ertl and Gregg](https://jilp.org/vol5/v5paper12.pdf) show why dispatch
and branch prediction matter in interpreters. [V8 Liftoff](https://v8.dev/blog/liftoff)
and [Wasmtime Winch](https://github.com/bytecodealliance/wasmtime/blob/main/winch/README.md)
emit baseline native code directly from existing bytecode; Liftoff also
caches values in registers. [Xu and Kjolstad's copy-and-patch
paper](https://doi.org/10.1145/3485513) gives a different fast emission
mechanism; a stencil build matrix is warranted only if current emission
cost or cross-platform opcode coverage is material. These are mechanisms,
not speed predictions or sources of msync's work/lifetime semantics.

## Bound image and explicit transfer points

The test-only [machine emitter](../../src/Msync.Runtime/Hpir/HpirMachineImage.cs)
lowers scalar operations, pointers, control flow and static literal
references from one bound HPIR stream. The stream remains authoritative;
generated machine code and label addresses are process-local, not
serialized. No callable, frame-alias, or generic readiness proof is
run. One native entry receives the physical frame, execution state and
resolved instruction address. Direct/backedge control stays in native
code; signed operand offsets select direct or indirect encoding once.
Binary64 remainder calls the shared system math helper, not the old VM.

In the first pilot, each `CALL` and `RET` stopped at the original HPIR PC
before any call behavior. The current native mode instead executes ordinary
guest calls and returns inside the native program, as described below.
Host services, actual segment transitions, heap roots and cancellation are
still handled by the shared executor. No completed region is replayed.
Native loops poll a run-owned cancellation word on taken backedges.
Registration is disposed before that stack-based state goes out of scope.
`NativeCodePage` owns publication and remains alive during execution.
x64 SysV is verified here; x64 Windows uses its own host prologue and
still needs installed-package evidence. This experiment adds no production
platform or tiering policy.

Instrumentation is separate from timing. Emission reports per-function
source and emitted bytes and a span for code-page publication. Instrumented
code counts actual native instructions and taken edges at HPIR sites.
Both modes use the same sink-neutral census; the focused test verifies
opcode/site, edge, call and import parity. Cancellation runs one real
output operation before an in-flight loop, attributes the token failure to
the guest backedge and prints only once. Both tests passed on the pinned
SDK's managed and Linux x64 Native AOT test runner.

## Focused comparison

The [ready signal fixture](../../tests/Performance/Hpir/ready_signal.msync)
adapts the standard signal-hysteresis application for early coverage: size
and seed are ready literals, a language loop replaces the range iterator,
and the driver owns its state. It still runs three mutable closures,
fixed nested calls, Scope fields, a sampled recurrence and real output.
The oracle is the original [integer detector](../../tests/Performance/Corpus/compute.py).
At size 1,024, seed 17, the promoted 0.9.3 and both HPIR modes returned
`24, 834252, 16, 776783, 16, 621000` on six lines. The block-only
pilot recorded 164,637 scalar/control instructions in 11,328 native
entries, which included both helper calls and guest frame boundaries.

Use a quiet machine and the pinned SDK. Native AOT applies to the test
runner too; a managed apphost is not comparable to a packaged release.

```sh
dotnet publish tests/HpirPipeline/Msync.HpirPipeline.Tests.csproj \
  -c Release -r linux-x64 -p:PublishAot=true -p:RestoreLockedMode=false \
  --artifacts-path .temp/hpir-block-aot-build -o "$PWD/.temp/hpir-block-aot"
.temp/hpir-block-aot/Msync.HpirPipeline.Tests hpir-block-check

python3 tests/Performance/Hpir/measure_blocks.py \
  --candidate-aot .temp/hpir-block-aot/Msync.HpirPipeline.Tests \
  --published .temp/hpir-baseline/release/msync-0.9.3-linux-x86_64/bin/msync \
  --cpu 6 --pairs 7 --warmups 1 --size 4096 --size 65536 --seed 17 \
  --work .temp/hpir-block-comparison
```

The first lane runs precisely the same prepared HPIR image in both modes;
fresh-process wall time includes native emission and publication. The
second uses the same source with each version's own artifact. Both bind
after deleting the source, execute from an unrelated directory, alternate
AB/BA, and check output on every invocation. A second lane with
`--seed 503 --long-bursts` changes the branch distribution. Preparation
is recorded separately once per image, not a latency claim. The test
runner is not the product CLI; use the size slope to distinguish useful
compute from process startup. Default v0.9.3 (auto) is the denominator.

### Pilot: native code amortizes startup on the larger signal fixture

Five alternating pairs after one warmup, one pinned Linux x64 CPU (6),
seed 17/reference, old policy auto. All outputs passed the independent
oracle. The Native AOT block test executable was SHA-256
`cf95b7be2731a95712c563bc0e3f72068cffd2ad4d74f49516bee7590ec7f63b`.
It is the ready-scalar snapshot before the subsequent integer expansion.

| Rows | HPIR interpreted / native, p50 ms | Paired native change | Promoted 0.9.3 bound / HPIR native, p50 ms | Paired native change |
| ---: | ---: | ---: | ---: | ---: |
| 4,096 | 25.4 / 32.3 | +24.9% | 73.5 / 23.5 | −70.4% |
| 65,536 | 174.7 / 96.7 | −41.2% | 819.3 / 118.4 | −86.8% |

Each comparison made its own samples; native medians are not reused
between lanes. The 65,536-row same-image improvement held in all five
pairs. Small cases were order-sensitive and the two native lanes differed
by up to 22% in median; the host was just leaving a concurrent build
window. No local build was present in the before/after process checks.
Use the pilot for the mechanism and next design decision, not a precise
small-process regression estimate or an unconditional native-on policy.
It is not the original Range/streaming workload and is not a release win
against the rest of the corpus. Raw pairs, host snapshots and build log
are checksummed in
`artifacts/benchmarks/hpir-machine-blocks/pilot-1795358755/`.

## Direct guest CALL/RET pilot

The current native mode uses the verified direct ABI without changing
the HPIR file, frame header or heap/root model. Ordinary calls advance the
active segment's SP, write the same native frame header and copy fixed
parameter offsets directly, then jump to the callee. Returns restore the
SP/frame and resume at the existing HPIR continuation through a relative
native label table. The emitter does not rediscover types, argument
conversions or closure eligibility. It returns to the common executor
only at actual host calls, stack growth or return across a segment
boundary, and cancellation. The original HPIR PC identifies the exact
state; no region is restarted after a completed effect.

A sibling may reoccupy the previous callee's exact stack address. A host
boundary therefore recovers the active function ID from the current
frame header, not from equality of frame addresses. The differential
signal test observed this naturally when leaving one detector frame and
entering another at the same address. It now checks exact instruction,
branch, guest-call and import counts against the interpreter. Additional
controls verify 2,048 non-tail recursive frames through native segment
growth and restoration, shared-field identity, escaping mutable captures
across actual collection, a typed host failure in a grandchild, and
cancellation inside a native callee after one output effect. All passed
on the managed and Linux x64 Native AOT runners.

On the 1,024-row reference signal, native entries dropped from 11,328
to 3,122: 4,103 CALL and 4,103 RET operations stayed native. The remaining
entries are dominated by real imports rather than by guest frame
transitions. Run the two code-generation modes from the same bound image
and the same Native AOT test executable (SHA-256
`a223de38a2aaa34cef1602362190bc111d91c1418811fa9fcecadafde7e9d25c`):
seven alternating pairs after one warmup, telemetry off, Linux x64 CPU 6.
CPU time is child user plus system time; wall time includes process
startup and native code publication in both modes.

| Rows and distribution | Block boundary / direct wall p50, ms | Paired direct wall change | Paired direct CPU change |
| --- | ---: | ---: | ---: |
| 4,096, reference (seed 17) | 25.3 / 31.1 | +28.3% | +28.4% |
| 4,096, long bursts (seed 503) | 26.8 / 25.9 | −7.9% | −8.2% |
| 65,536, reference (seed 17) | 103.6 / 58.5 | −41.7% | −42.2% |
| 65,536, long bursts (seed 503) | 95.3 / 57.7 | −43.9% | −44.3% |

The large-case CPU improvement repeats across branch distributions;
the small-case absolute times and ordering do not justify making native
emission mandatory for fresh short programs. The one test runner can
still compare the interpreter with direct native HPIR, but no longer
maintains a block-only code-generation choice. Checksummed raw pairs,
runner, frozen AOT image, build/smoke and host receipts are under
`artifacts/benchmarks/hpir-machine-blocks/direct-pilot-1795359588/`.
This was a controlled HPIR mechanism pilot, not another measurement
against the promoted legacy release.

Next reduce the cost actually left: choose a typed synchronous ABI for
pure numeric/helper calls and keep live scalar values in a small physical
register cache or reused frame locations. Still spill the compiled roots
and ownership state at actual service, collection, growth and cancellation
edges. Avoid an opcode/shape-specific tier. Retain unmodified
signal/polygon, B+/live-policy, and the full
[baseline guardrails](hpir-0.9.3-baseline.md) for release claims.

## Function-owned adaptive code cache

The final function-cache candidate and a same-source control were published
as Linux x64 Native AOT with SDK 10.0.403. Only the six HPIR runtime source
files changed between these images. Both used default JIT policy and ran
the exact already-bound release artifacts, with the same input and byte
oracle. Binding and tracing were outside timing. Each mode warmed once,
then ran seven alternating fresh-process pairs pinned to CPU 6. The
percentage is the median of paired CPU ratios, not the ratio of medians.

| Existing workload | Control CPU p50 | Function cache CPU p50 | Paired CPU change |
| --- | ---: | ---: | ---: |
| Signal hysteresis, 64 rows | 16.81 ms | 16.44 ms | −0.2% |
| Signal hysteresis, full | 39.68 ms | 32.41 ms | −14.5% |
| B+ inventory, full | 115.53 ms | 89.22 ms | −13.3% |
| Bounded sensor, full | 47.37 ms | 49.50 ms | +9.1% |

The short signal never emitted. Full signal emitted 4 of 9 functions;
B+ emitted 74 of 101 and used less CPU in all seven rounds. Its
diagnostic trace reported 359,104 code bytes, 156 emitted sites with a
baked-in cached callee address and 95 sites linked through the entry
table. Scalar-run counters reported forwarded operands; the targeted
alias/alternate-entry journey also verified that repeated writes may
disappear without removing a store observed through an indirect
operand.

The bounded sensor result was order-sensitive: the second arm used more
CPU in each initial pair. A balanced 12-pair ABBA follow-up on CPU 8
measured +2.1% by the same paired estimator, with wide scatter in both
directions. This is inconclusive, not a demonstrated speedup or slowdown.
Its roughly 30,000 typed input and parse service calls remain outside
scalar code generation.

The focused Linux CLI Native AOT and managed journeys preserved the
detached artifact, exact guest census, cold bypass, cached/late cross-page
calls, cancellation, and pending-work/heap ownership. The Windows x64
managed runner under Wine passed the adaptive and machine-block journeys,
exercising the Windows machine ABI; this is not a native Windows package
performance result. See [raw pairs and reproduction](../../artifacts/benchmarks/hpir-function-cache/README.md).

### Full release cohort: same-source pre/post comparison

The same two Native AOT binaries above ran all six frozen release
workloads on Linux x64. Both sides executed the same byte-identical
detached artifact with default JIT policy, checked stdout/exit/stderr,
and ran one warmup per arm and 12 fresh-process pairs (three ABBA
blocks) at CPU affinity 6. Timed runs disabled recording. CPU and wall
changes use the median paired ratio; the absolute values are median
process CPU times.

| Workload | Before CPU p50 | After CPU p50 | Paired CPU change | Paired wall change | Faster CPU pairs |
| --- | ---: | ---: | ---: | ---: | ---: |
| Signal hysteresis | 35.52 ms | 30.26 ms | −10.6% | −9.7% | 9/12 |
| Polygon queries | 22.64 ms | 22.98 ms | −1.1% | −1.1% | 7/12 |
| Bounded sensor | 33.15 ms | 32.89 ms | +2.2% | +12.7% | 5/12 |
| Live policy dispatch | 50.23 ms | 52.10 ms | +6.5% | +10.8% | 5/12 |
| B+ inventory | 97.58 ms | 89.47 ms | −9.4% | −9.1% | 11/12 |
| Set deduplication | 78.17 ms | 77.66 ms | −1.0% | −0.9% | 7/12 |

The unweighted geometric mean of the six paired ratios is −2.4% CPU
and +0.1% wall. This is a suite summary, not production request
weighting. Diagnostics kept the same dynamic HPIR instruction counts
in all six cases; total function emissions went from 165 to 120, and
diagnostic code bytes from 755,627 to 578,556.

The follow-up used only the candidate regressions plus stage controls
on CPU 8 with the same pair design:

- **Live policy:** auto CPU was +13.6% and wall +12.6%. The CPU
  difference also appeared with JIT disabled (+23.6%), while forced
  emission was +1.7% CPU and −0.4% wall. Both default comparisons have
  wide scatter, but the slower default median repeated; the evidence
  does not isolate a native code quality regression. The interpreter
  and other shared process/runtime paths remain candidates.
- **Bounded sensor:** the auto follow-up changed direction (−4.2% CPU,
  −3.7% wall, 6/12 faster), so the first-run wall slowdown was not
  reproduced.
- **Non-execution controls:** version startup wall went from +12.7%
  in the full pass to +2.8% in the follow-up (6/12 faster), not a
  stable startup shift. Signal and live policy binding were slower
  in both passes (CPU +8.2%/+12.0% and +7.1%/+8.7%, respectively);
  the follow-up medians differ by roughly 2.0 ms and 0.7 ms. Set
  binding reversed direction. Both binders produced byte-identical
  files. These are end-to-end bind command timings (including process
  startup), not evidence that binding invokes JIT or that a specific
  binder phase regressed.

[Full-suite raw pairs](../../artifacts/benchmarks/hpir-function-cache/full-suite.json),
[focused recheck](../../artifacts/benchmarks/hpir-function-cache/full-suite-recheck.json),
and [replay](../../artifacts/benchmarks/hpir-function-cache/README.md#full-six-workload-cohort).

## Host hot-path follow-up

The dynamic-policy diagnostic censuses agreed across pre/post-cache builds:
28,001 line pulls, 28,000 Number parses, and 135 pending reader results.
A slower process sample with JIT off was therefore not evidence of a
native-emitter regression. The pending reads were buffer refills, not
one scheduler handoff per line. In binding, `strace -c -f` on the exact
same objects identified a separate, reproducible boundary cost: the
seekable decoder queried `FileStream.Length` for each table and text.
The upstream [Unix handle implementation](https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs)
does not cache the length.

The follow-up snapshots the extent once, checks the end against the
actual stream, and keeps byte output and format 8. B+ bind `fstat`
counts dropped from 5,074 to 27 and live policy from 625 to 27;
the new binders produced identical bytes. The redirected reader stays
lazy and shared, with an 8 KiB buffer. Its pending reads fell 135 → 18
for live policy and 50 → 7 for the bounded sensor. Exactly
28,000 and 15,035 respective casts used the short integer path, preserving
all other syntax with the general parser and retaining the sign bit of
zero. This uses the exact small-integer subset of binary64, not a new
general decimal conversion algorithm (see [Lemire](https://arxiv.org/abs/2101.11408)
for the broader problem). Physical dispatch also hoists native-entry
readiness to function transitions/publication.

The same six detached images and independent byte oracles ran under
Linux x64 Native AOT, SDK 10.0.403, default JIT. The increments below
come from 12 balanced fresh-process pairs pinned to CPU 6 against
the first function-cache build; the cumulative column is a separate
12-pair direct comparison at CPU 8 against the original pre-cache
build, not a product of effects across runs.

| Workload | Follow-up CPU | Cumulative CPU | Cumulative wall |
| --- | ---: | ---: | ---: |
| Signal hysteresis | +29.2% | +3.1% | +2.0% |
| Polygon queries | −3.3% | +5.4% | +5.3% |
| Bounded sensor | −29.5% | −19.2% | −18.5% |
| Live policy | −9.5% | −16.5% | −22.6% |
| B+ inventory | −4.3% | −13.5% | −13.6% |
| Set deduplication | −5.0% | −0.9% | −0.7% |
| Equal-weight geometric mean | −5.2% | −7.5% | −8.6% |

Signal's follow-up CPU result depended strongly on which arm ran first.
On CPU 8, the same piped-input comparison was +1.4%; plain-file
auto remained order-sensitive, while off used 3.9–5.0% less CPU
across the two input carriers. The matching guest instruction, native
entry and code-byte censuses do not indicate changed guest work. Live
policy again improved on CPU 8 (−13.0% auto with a pipe, −24.7% off
with regular-file input, the latter faster in 12/12 pairs). The data do
not establish a uniform gain across all inputs or cores.

B+ binding improved 19.5% CPU over the first cache build and 15.9%
against the original. The cumulative signal/live-policy bind medians
no longer show the earlier regressions. Polygon bind commands did show
an end-to-end slowdown on both cumulative CPU pins (+18.1%/+14.4% CPU),
but isolated profile spans at CPU 10 show 0.54 → 0.32 ms for HPIR
decode, with preparation and encoding flat. The remaining shift is not
in the measured bind phases. Whole-process `--version`, which never
executes a binder/JIT, was also variable (+15.3% cumulative CPU at
CPU 8; +1.6% at CPU 10 with 6/12 faster). This is not a blanket
no-regression claim.

The existing detached CLI, redirected stdio, cast, live Text Range,
promotion and cooperative ownership journeys passed. The optional PTY
case failed before it read input, with the same pre-existing REPL
selection error before and after this change. The scalar and promotion
checks also passed on Windows x64 under Wine; no Windows timings
are inferred. [Receipts and replay](../../artifacts/benchmarks/hpir-hot-paths/README.md).

### Polygon bind: Native AOT startup attribution

Only the exact Polygon portable image was tested, with the original
pre-cache and follow-up executables and a byte-identical bound-output
oracle. A Linux/glibc probe marks libc entry, the native executable's
`main`, the first timestamp in C# `Program.Main`, input open, successful
output rename, and native `main` return. It records process CPU and
monotonic time without turning on application telemetry. The same
fresh-process comparison was also run without the probe. The markers
split the boundary shown by the upstream [Native AOT bootstrap](https://raw.githubusercontent.com/dotnet/runtime/v10.0.14/src/coreclr/nativeaot/Bootstrap/main.cpp):
runtime and module initialization occur before managed application code.

Each lane used 16 balanced pairs per logical CPU. On the original binary
paths the **unprobed** paired wall difference was −0.51 ms on CPU 8 and
+1.47 ms on CPU 10. The **probed** pairs localize the difference:

| Stage (paired wall delta, after − before) | CPU 8 | CPU 10 |
| --- | ---: | ---: |
| Native `main` → C# `Program.Main` | +0.85 ms | +1.56 ms |
| C# `Program.Main` → input open | +0.04 ms | +0.06 ms |
| Input open → bound file published | −0.24 ms | −0.12 ms |
| Native `main` return → reaped | +0.34 ms | +0.11 ms |

Among the 12 of 32 probed bind pairs with at least 2 ms of after-side
extra wall time, native startup accounts for 76.7 of the 101.1 ms
excess (76%, from summed per-pair stage differences, not added
medians). The bind interval accounts for 0.7 ms in that subset.
An independent `--version` control showed delays in the same
pre-managed interval (+1.25/+0.76 ms paired wall). A control staging
the exact binaries at symmetric new paths did not show a stable
unprobed binding delta; the measured costs are not a fixed per-image
startup penalty.

Standalone process-CPU sampling was armed only between the native and
managed markers. Matching debug symbols attributed **38/50** samples
before and **45/52** after to `StartupCodeHelpers.RehydrateData`.
The resident cost on both sides is Native AOT's reconstruction of
preinitialized data at process start, not HPIR decode, preparation,
code emission, or artifact publication. The sampled runs' startup CPU
medians were 9.66 and 9.86 ms; this does not prove a deterministic
increase in rehydration work from the HPIR changes.

The single-switch publication control (`IlcDehydrate=false`, same
follow-up source, not the default) removed the dehydrated data stream.
This is a mechanism test, not a proposed fix. On CPU 10 the paired
native-entry-to-input interval shrank 8.72 ms, but launch-to-libc-entry
grew 10.21 ms. The ELF relative relocation count grew from 6,175 to
407,448; the data relocation work moved into the ELF loader before C
`main`, with more work on exit as well. Executable size increased from
23.21 to 36.15 MB. Without probes, Polygon bind was 1.15/2.47 ms slower
on CPUs 8/10 and `--version` about 2.1–2.2 ms slower. All bound outputs
were byte-identical. Keep the default: reducing startup work is not the
same thing as moving it between ILCompiler and the loader.

See the [phase probes and raw samples](../../artifacts/benchmarks/hpir-polygon-bind/README.md).
