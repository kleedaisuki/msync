# Frame, environment and managed-allocation review

## Scope and evidence

Read [shared context](../../design/llvm-performance-regression-review.md) first; inspected current product paths and existing receipts only. No production edits, builds, execution benchmarks or subprocess experiments were performed. The control/candidate hashes and final telemetry-off results are those frozen in the brief. In particular, the real latency regressions are Unicode bound/source +3.97%/+7.69% and policy +1.70%/+5.01%; the following diagnostic allocation figures are **not** telemetry-off allocation measurements.

Primary diagnostic observations:

- Unicode: `artifacts/diagnostics/llvm-sroa/unicode-tier/{baseline,after}.jsonl`.
- Policy: `.temp/function-perf/final/live-policy-dispatch-{baseline,candidate}.jsonl` (literal-cache cohort, not final tier patch, as identified in the brief).
- Relocatable structural inventories: `.temp/function-perf/final/*-artifact.json`. These are not asserted to be the prepared execution image.

## Ranked findings

### 1. High-value confirmed wasted allocation: register-plan liveness runs despite no eligible writable results

**Location:** `src/Msync.Runtime/NativeModel/Tiering/NativeRegisterPlan.cs:44–48,72–76,138–195`; `NativeTierLayout.cs:39`; `src/Msync.Core/NativeModel/NativeStandardBackendEmitter.cs:47–48`; `NativeExecutionImage.cs:154–155`.

**Executed path:** first tier compilation -> `NativeTierLayout.Create` -> `NativeRegisterPlan.Create` -> control-flow construction, `LiveAfter`, `PendingSlots` -> only afterwards check `Eligible`. The current standard backend emits all-false `RawWritable`; `Eligible` consequently cannot accept a result. All four observations report zero regions/values/spills. This is an established unnecessary runtime preparation cost, not evidence that SROA creates per-row heap objects.

`LiveAfter` allocates two HashSet arrays, one List per instruction, and at least two working HashSets per visited instruction. Revisited loop instructions build more sets and backing arrays. `PendingSlots` additionally clones a `NativeInstruction` record on each visit (`metadata[...] with { ... }`, lines 150–151), then enumerates its reads/definitions until a fixed point. Their allocation depends on live-set cardinalities, CFG propagation and iteration counts, not only on final slot/instruction counts. Both reported main bodies actually shrink: Unicode relocatable 1026 slots/1370 instructions -> 966/1310; policy 975/1324 -> 861/1210. A claim that more instructions alone explain the growth would be incorrect.

**Evidence strength/scale:** code-proven shared wasted work; independently observed register-allocation time grows 23.467 -> 47.048 ms for Unicode and 31.399 -> 37.619 ms for policy. It is a strong candidate for part of the +7,299,664/+4,255,984 managed-byte deltas, but neither a byte breakdown nor causal wall-time attribution exists. The inclusive timing observations cannot be converted into the paired telemetry-off regression.

**Coherent fix/probe:** determine whether any actual executable instruction is `Eligible` before constructing CFG/liveness/pending state; return the empty region plan when none is eligible. This is exact reuse of the plan's existing acceptance condition, not a new optimization heuristic or heap-safety solver. First measure thread-local allocation and visits/cardinalities around `LiveAfter` and `PendingSlots` in a single diagnostic run, or compare allocations before/after this narrowly scoped rejection. Keep the genuine scalar tier layout construction intact. Do not merely delete all register planning for every backend.

### 2. Confirmed per-call value traffic survives, but frame allocation and argument-snapshot growth are excluded

**Location:** `NativeInterpreter.Calls.cs:114–135`; `Tiering/NativeExecutionFunction.Calls.cs:45–88`; `NativeFrame.cs:73–96`; `NativeArguments.cs:50–57`; `NativeFrameStack.cs:25–61`; `src/Msync.Core/NativeModel/NativeStorage.Data.cs:18–22,39–44`.

**Executed path:** a callback enters its function's pooled frame; frame is rebound to a thread-local segment, captures are installed and a parameter copied; execution returns, roots/initialization flags are cleared and frame is detached and returned to the spare slot. These are real repeated memory operations, **not** fresh storage arrays per callback.

| Property | Unicode control -> candidate | Policy control -> candidate |
| --- | --- | --- |
| Invoked callback functions | extension/filename/stem: 65,000 calls each, unchanged | lambda 98/99/100: 15,436/9,157/3,159, unchanged |
| Callback activation/frame reuse | each 1 allocation + 64,999 reuses, unchanged | each 1 allocation; all remaining calls reuse, unchanged |
| Callback physical frame per invocation | each 28 raw bytes / 24 roots, unchanged | 40/2, 72/2, 40/0 bytes/roots, unchanged |
| Callback argument copies | 195,000 total, unchanged | 27,752 total, unchanged |
| Argument snapshots / snapshot allocations | 0 / 0 both sides | 0 / 0 both sides |
| Escaping-location allocations | 0 both sides | 0 both sides |
| Main physical frame | 6,848 bytes / 1,805 roots -> 5,664 / 1,411 | 6,776 / 1,778 -> 5,328 / 1,286 |
| Logical heap storage payload | 7,008 bytes both | 7,124 bytes both |

`stack_frame_bytes` and `stack_frame_roots` are cumulative work counters. For example 1,820,000 Unicode callback bytes are 65,000 * 28 bytes, not 1.82 MB newly allocated to the CLR heap. Both Unicode runs allocate only one reported stack segment, and no callback grows a segment. Both policy runs have the same property.

**Evidence strength:** exact matching executed-function counters plus ownership implementation. There is no demonstrated frame/argument-plan regression to fix. It would be a distraction to replace the pooling scheme or introduce a new closure ownership model in response to these allocation totals.

**Narrow next probe:** if per-row callback CPU cost remains of interest after eliminating cold register-plan work, sample the actual callback entry/exit and typed argument-copy paths rather than count frame pushes as heap allocations. Unicode still performs 195,000 guest push/resume pairs on both sides; policy still uses 27,752 direct leaf calls on both sides. This is shared workload cost, not a newly selected slow-call route.

### 3. Function projection introduces generic carrier traffic, not an allocation per projection; composition allocates only a small environment view

**Location:** `NativeInterpreter.FunctionParts.cs:16–28,32–43`; `src/Msync.Core/NativeModel/NativeFunctionParts.cs:9–18,28–40`; `NativeValue.cs:45–46,79–83`; `NativeDescriptors.cs:83–96`; `NativeImage.cs:62–76`.

**Executed path:** Unicode main performs 195,000 ready `FunctionComponent` operations, zero pending ones. The ready path reads a whole `NativeDatum`, extracts the environment `NativeAddress`, and writes it through `Frame.Set`/`NativeMemory.Write`. `NativeValue`, `NativeFunction`, `NativeDatum`, and the address are structs. `FromFunction` uses typed root fields without boxing the function. For the hot environment component (index 1), projection creates no `NativeEnvironment`, allocation backing arrays, future or async state. Its wider generic value movement can cost CPU relative to the old `EnvAddress`/`SetPointer` path; no receipt isolates that marginal time.

Ready composition does call `NativeEnvironment.View`, which **does allocate a class wrapper** whenever the environment is non-null. It shares the original address and contents; it does not allocate or copy capture storage. However, Unicode reports only 180 ready compositions and policy 164, with zero pending compositions. Even an extreme 1 KB wrapper would yield under 0.2 MB, far short of either observed delta; the actual wrapper is substantially smaller. This is not a per-row wrapper-allocation explanation. Non-null composition count is bounded by these ready counts, not assumed equal to it.

Environment shape lookup is image-owned and cached by actual producer layout. Unicode publishes five schema-link events, policy ten, before execution. The layout identity is recovered from owned storage; capture cells are not reconstructed per callback. Repeated wrapper identity differs, but the underlying capture address identity and mutations remain shared. Do not re-open the repaired template-Any layout defect.

**Evidence strength:** confirmed allocation sites and exact execution counts; plausible generic-copy CPU cost only. No material GC-retention defect established: pooled frame roots and bindings are cleared on return; a view retains its capture allocation by design, not an unrelated private callback frame.

**Next probe:** if the profile still points to hot projection after cold planning is removed, compare the ready environment projection's typed address write against generic `Set`, preserving null-union encoding and the existing pending `MapMachine` path. This is an established representation-specific execution specialization, not a language or capture-mode change. Do not add a global environment-view identity cache to save at most hundreds of wrappers.

## Allocation attribution: what the numbers actually establish

`src/Msync.Cli/Telemetry/TelemetrySpan.cs:50–60,106–112` samples **process-wide** `GC.GetTotalAllocatedBytes(precise: true)`. `NativeCommandPipeline.cs:151–163` brackets `ExecuteAsync`, including tier compilation and concurrent observer work. Profile publication happens afterwards in the outer finally, so final `CollectNativeFunction` dictionary/export materialization is not itself inside this bracket. Lazy profile creation/counter updates, timeline/exporter activity during execution and per-call work ownership can be inside it.

A specific diagnostic-only repeated heap allocation is `NativeInterpreter.Calls.cs:119–122`: profiling forces `ownsWork=true` and allocates a `NativeWorkScope` even for pure synchronous callbacks that can reuse parent work with profiling off. Thus 195,000 Unicode / 27,752 policy callback invocations allocate work-scope objects under these observations despite reusing frames. Counts are unchanged across cohorts; this is shared profiling traffic and does **not** itself explain the delta. It does make the overall diagnostic allocation scale a poor estimate of normal execution allocation.

| Diagnostic execute interval | Control bytes | Candidate bytes | Difference |
| --- | ---: | ---: | ---: |
| Unicode | 295,224,704 | 302,524,368 | +7,299,664 |
| Policy (cache cohort) | 62,334,384 | 66,590,368 | +4,255,984 |

These establish additional real process heap allocation during the recorded interval, not just stack/struct movement. The attribution remains incomplete. The highest-value discriminating experiment is to eliminate or separately count the zero-region liveness/pending work before pursuing heap ownership redesign. One extra Gen0 collection occurs in both diagnostic pairs, but the policy candidate has fewer Gen1/Gen2 collections; collection counts do not establish a retention regression.

## Source versus bound routes

`NativePreparation.cs:11,19–26` immediately returns already Executable programs, whereas source/relocatable input can perform contextual evaluation and compile a prepared backend. `NativePreparation.Values.cs:8–23` recursively evaluates known source functions using a fresh `NativePreparationState`; its constructor (`NativePreparation.cs:63–84`) creates an ordinary allocated preparation frame and materializes argument datums. This is a separate cold path from execution's pooled callback frames. Its shared image and schema cache are reused through `parent`, not rebuilt per evaluated nested call.

The frozen bound artifacts exclude this contextual preparation route. Both routes still incur runtime image/linking and first-use tier compilation. Since bound timing regresses too, source-only preparation cannot explain all latency loss. Conversely, these bound execute allocation receipts do not explain the larger source regression or fresh compile timings. A source-minus-bound preparation trace with per-state frame/layout/argument materialization counts is the useful next source-specific probe; do not multiply preparation allocations by the 65,000 runtime callback count.

## Bottom line

No material new frame ownership, callback shape, call-argument snapshot, capture lifetime or GC-retention defect was found in the observed workloads. One substantial allocation-heavy runtime analysis is demonstrably unnecessary for the current backend; fix or isolate it first. Generic ready projection movement is a distinct plausible steady-state CPU cost, while composition-wrapper allocation is far too sparse to explain the MB-scale managed-allocation deltas.
