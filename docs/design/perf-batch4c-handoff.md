# Performance Optimization Handoff — Historical Batch 4C and Current Runtime

## Latest handoff (2026-11-15)

The tables and profiles below are historical, not current HEAD measurements.
See [latest benchmark status](benchmark-throughput-v090.md#latest-measured-runtime-2026-11-15)
for the frozen shared-plans image, hashes, paired results and reproduction, and
[execution lowering](native-execution-lowering-v090.md) for current architecture.
The final comparison with evaluator-before gives -20.63% paired call latency and
-13.10% B+ latency over twelve pairs. Do not combine these with the historical
three-run compile/e2e table or present its 246 MB figure as current allocation.

Completed since this original handoff: synchronous evaluator/thin calls;
compiler-owned storage/argument contracts; segmented byte/root frames;
independent escaping locations; persisted numeric/connection lowering; owned
background I/O and Future demand; removal of submission handshakes, duplicate
compiled-path checks, generic instruction ValueTask wrappers, separate storage
objects, redundant argument references and dense per-PC call-plan arrays.
Compact 3 artifacts are rejected for recompilation, not repaired in the decoder.
Final-image real-user journey results are summarized in the benchmark document.

Not implemented: fully unmanaged guest storage. Unsafe C# is the next discussed
implementation direction, not a completed migration. Current segments still
contain managed arrays and roots. A continuation should define native storage
ownership/reclamation, consume existing escape/alias facts without reanalysis,
and confine managed host objects to interoperability handles. Adding `unsafe`
alone does not remove GC ownership or write barriers. The user cleared the
previous goal; no automatic continuation remains authorized by that goal.

Sections below retain original findings and intermediate snapshots. Their
remaining-work statements are superseded by this summary.

> Status: continuation implemented; see section 5 for the final v0.9 storage contract and receipt.\
> Date: 2026-11-13\
> Target Workload: `.temp/benchmarks/opt-fix-check-3/applications/bplus-inventory/standard/17/workload.msync`\
> Important: Always pass `< /dev/null` (line 10 calls `$std::io::read_line()`); keep `MSYNC_JIT=auto` (default).\
> Expected Stdout (14 lines): `1218`, `580`, `298`, `671`, `200`, `866`, `836497`, `866`, `143`, `169`, `-1`, `-1`, `3287`, `3322`.\
> Verification Rule: Measure end performance with Native AOT (`dotnet publish src/Msync.Cli/Msync.Cli.csproj -c Release -r linux-x64 -o .temp/aot-cli --no-restore -v q`). Pre-built baseline AOT binary sits at `.temp/aot-baseline/msync`.

---

## 1. Current Benchmarks & Work Completed So Far

### Native AOT Latency Progression (`bplus-inventory/standard/17/workload.msync`, 3-run `min` / `med`)

| Stage | `compile -O0` | `compile -O2` | `run-ir -O2` | `e2e run -O2` | Runtime Managed Alloc |
| :--- | :---: | :---: | :---: | :---: | :---: |
| Baseline (`.temp/aot-baseline/msync`) | `med 1485.0ms` | `med 5967.6ms` | `med 4101.1ms` | `med 9846.6ms` | — |
| Batch 3B (+ `NativeLocalOptimization` fix) | `med 1437.5ms` | `med 3812.3ms` (`-36%`) | `med 2131.4ms` (`-48%`) | `med 5828.7ms` (`-41%`) | `323.6 MB` |
| Batch 4A + 4B | — | `3863.5ms` / `3864.8ms` | `1981.1ms` / **`1991.5ms`** (`-51.4%`) | `5712.2ms` / `5928.5ms` | `246.0 MB` (`-24.0%`) |
| **Batch 4C (historical snapshot)** | `1413.1ms` / **`1422.7ms`** | `3713.4ms` / `4157.7ms` | `2046.9ms` / `2066.3ms` | `5954.1ms` / `6108.8ms` | `246–247 MB` |

### Changes Already Landed in Working Tree (Do Not Redo)

- **Batch 4A (Middle-End Optimizer)**:
  - `src/Msync.Optimization/NativeModel/NativeOptimizer.cs`: Added `NativeOp.MakeFunction` and `(NativeOp.MakeType && instruction.A < 0)` to `Discardable` (eliminated `141,598` dead `ranges.front` closure allocations in `fn[17]`/`fn[20]`).
  - `src/Msync.Optimization/NativeModel/NativeLocalOptimization.cs`: Extended `FrameAddress` for `NativeCaptureMode.Value` captures and `ComputeEntryFacts` `falseExtraNonNull` along `Branch(IsNull)` false edges.
  - `src/Msync.Optimization/NativeModel/NativeGlobalMemory.cs`: Handled non-bound non-escaping value-capture `FrameAddress` and `state.Pointers`/`state.Fields` fallback.
- **Batch 4B (Runtime Pooling, Layout Elision, Fast Unary Math)**:
  - `src/Msync.Runtime/NativeModel/NativeFrame.cs` & `src/Msync.Runtime/NativeModel/Tiering/NativeExecutionFunction.Calls.cs` & `src/Msync.Runtime/NativeModel/NativeInterpreter.cs`: Bounded pooling of `NativeFrame` (with `NativeAllocation` & `bindings[]`, stack cap `16`) and `Activation` (stack cap `32`) across non-tail calls.
  - `src/Msync.Runtime/NativeModel/Tiering/NativeExecutionOperand.cs`, `NativeExecutionImage.cs`, `NativeResult.cs`, `NativeInterpreter.Tiering.cs`: Link-time `NeedsLayoutCheck` elision on `Narrow`/`Convert` when `src.Layouts` is a subset of `dst.Layouts`.
  - `src/Msync.Runtime/NativeModel/NativeInterpreter.NativeCalls.cs`, `NativeInterpreter.Aggregates.cs`, `NativeInterpreter.Tiering.cs`: Zero-allocation fast path `TryFastUnaryMath` (`math.floor`, `ceil`, `trunc`, `abs`, `sqrt`, `round`, `round_even`) preserving `work.Counters.Native(name)` (eliminated array + async state machines for all `39,309` `math.floor` calls, dropping `tier.bridge_fallback_operations` from `233,565` to `194,334`).
- **Batch 4C (Compiler Flow Lattice Flat Array)**:
  - `src/Msync.Compiler/Semantics/FlowFacts.cs`: Replaced `ImmutableDictionary` AVL maps with shared `FlowDomainMap` (O(1) dense array lookup, precomputed `Defaults`/`DeferredSemanticTypes`/`MemorySlots`) + copy-on-write `FlowFact[]` + `ToMutable()`/`Freeze()`.
  - `src/Msync.Compiler/Semantics/FlowGraph.cs`, `SemanticAnalyzer.FlowPreparation.cs`, `SemanticAnalyzer.CallPolicy.cs`: Wired in-block mutable workspace and batch initial-fact population.

### Focused Test Status Observed After Batch 4C

- `tests/HirFlow`: **PASS 813 semantic flow contracts**
- `tests/NativePreparation`: **PASS 128** contextual conversion/diagnostic binary-roundtrip equivalences + aliased native transfer check
- `tests/NativeMiddleEnd`: **PASS 711 native middle-end contracts**
- `tests/NativeOptimization`: **PASS 3191 native optimization observations**
- `tests/NativePipeline`: 7 pipeline cases + **PASS 28 direct native lowering/artifact routes**, then failed at pre-existing line 123 (`NativePipeline/Program.cs:120` rejects `optimizer.pass.*` observations because `NativeOptimizationProfile.Add` reports `Aggregated: false` alongside `Publish`'s `Aggregated: true`).
- Note: `tests/HirCallPolicy` currently fails to compile against removed legacy runtime symbols (`Interpreter`, `HirLowerer`, `NumberValue`). Do not use `dotnet test msync.slnx`.

---

## 2. Bottleneck Group I — Compiler `-O2` Optimizer & Frontend Profiles

All measurements below were captured from the Native AOT binary `.temp/aot-cli/msync` (`--telemetry-level profile`).

### Observed `-O2` Profile Breakdown (`.temp/b4c-comp-o2.jsonl`)

Top inclusive/phase spans under `compile -O2`:

| Span | Count | Sum Duration (ms) | Sum Managed Alloc (MB) | Notes |
| :--- | ---: | ---: | ---: | :--- |
| `compiler.optimize` | 1 | `2508.31` | `906.98` | Outer optimize phase |
| `optimizer.function` | 182 | `1977.20` | `784.50` | `iter_hist`: `{0: 88, 1: 1, 2: 13, 3: 49, 4: 15, 5: 16}` |
| `optimizer.pass.native-escape-analysis` | 314 | **`796.79`** | **`319.43`** | **#1 optimizer pass** (`309` `ProgramFacts` invalidations!) |
| `optimizer.pass.native-inlining` | 728 | `428.63` | `93.68` | Calls `Facts.Function(index)` across rounds |
| `optimizer.pass.native-local-values` | 314 | `417.42` | `104.69` | Tightens slots + local fixed-point |
| `optimizer.pass.native-dead-results` | 314 | `189.72` | `84.04` | Rebuilds `new NativeLiveness(...)` every time |
| `optimizer.pass.native-global-memory` | 314 | `179.01` | `85.52` |  |
| `optimizer.pass.native-control-flow` | 628 | `108.75` | `55.02` | Built twice per inner iteration (`NativeOptimizer.cs:120,143`) |
| `optimizer.pass.native-last-use-moves` | 94 | `104.44` | `64.04` | Rebuilds `NativeLiveness` up to 4x (`MoveLastUses` + 3 rounds of `CollapseSingleUseMoves`) |
| `optimizer.pass.native-global-facts` | 314 | `79.33` | `39.67` |  |
| `compiler.analyze` | 1 | `1390.35` | `393.77` | `compiler.semantic.body` (`1387.48ms`, `392.70 MB`) |
| `compiler.semantic.instantiate` | 354 | `2425.67` | `721.06` | Nested inside `body` / `instantiate` (inclusive sums) |
| `compiler.flow.prepare` | 880 | `1040.38` | `277.06` | **`880` calls for only `146` lowered functions!** |
| `compiler.flow.solve` | 880 | `750.10` | `200.78` | Still ~201 MB despite Batch 4C flat `FlowFacts` |

Hottest functions inside `optimizer.pass.native-escape-analysis`:
- `0:<main>`: `3` runs -> **`435.85ms`**, **`97.71 MB`**
- `52:<lambda>`: `5` runs -> `110.60ms`, `60.04 MB`
- `10:<lambda>`: `5` runs -> `48.76ms`, `28.51 MB`
- `93:<lambda>`: `5` runs -> `17.10ms`, `8.69 MB` (`6` `program_facts.invalidations`)

Total `program_facts.invalidations` across `optimizer.function`: **`309`**.

### Root Causes in `-O2` Optimizer

1. **Coarse `factsDirty` & Transitive Caller Destruction (`NativeOptimizer.cs:118-175`, `NativeAddressAnalysis.Calls.cs:116-160`)**:
   - `NativeInlining.Run` (`NativeOptimizer.cs:28`, `NativeInlining.cs:28,100`) creates and populates `sharedFacts` for callers AND callees before the main bottom-up loop (`for (int function = functions.Length - 1; function >= 0; function--)`).
   - Inside `OptimizeFunction`:
     - ANY rewrite (`GlobalFacts`, `GlobalMemory`, `LocalValues`, `DeadMemory`, `DeadResults`, `MoveLastUses`, `CollapseSingleUseMoves`) unconditionally sets `factsDirty = true`.
     - Pure scalar/redundancy/move rewrites (folding `Constant`/`Unary`/`Binary`, removing a dead `Constant` or `Drop`, `Move` coalescing, eliminating a duplicate `Narrow` of an already-identical slot) do **not** change allocation/binding/call-target/store alias topology, yet they trigger `sharedFacts.Invalidate(view, index)` on the next iteration AND again at line 175 after `LastUseMoves`!
     - Worst of all, `ProgramFacts.Invalidate` (`NativeAddressAnalysis.Calls.cs:122-139`) does a BFS over `ana.callers` and **destroys `functions[caller]` for ALL transitive callers** (even ones not yet optimized, and even when `callee`'s external pointer summary did not change at all!), forcing `NativeEscapeAnalysis` + `SolveLocked()` to rebuild interprocedural graphs repeatedly (`309` invalidations total, `0:<main>` burning `435.9ms` on 3 runs!).
   - Furthermore, the final convergence pass (`iteration == 3..5`) runs `GlobalFacts` -> `ControlFlow` -> **`Invalidate` + `EscapeAnalysis`** even when only scalar/dead-code passes ran in the previous iteration, and in `RemoveDead`/`MoveLastUses`/`CollapseSingleUseMoves` (`NativeOptimizer.cs:210,238,266`) `new NativeLiveness(...)` is allocated afresh up to 6 times per function.
2. **Frontend Multiplication (`880` Flow Prep/Solve Calls vs `146` Lowered Functions, `compile -O0 = 1422.7ms`)**:
   - Even at `-O0` (`compile -O0: 1422.7ms` in AOT; `.temp/b4c-comp-o0.jsonl`), `compiler.semantic.instantiate` runs `354` times and `compiler.flow.prepare` / `compiler.flow.solve` run **`884`** times (`963ms / 277 MB` + `704ms / 201 MB`), over half of `-O0` compile time.
   - Because Batch 4C already removed `ImmutableDictionary` AVL churn inside `FlowFacts`, that remaining `277 MB` in `compiler.flow.prepare` comes from speculative `"query"` vs `"publish"` instantiations (`SemanticAnalyzer.Instantiation.cs:170`) and repeated per-body flow graph/domain construction (`SemanticAnalyzer.FlowPreparation.cs` builders, `HirFlowGraphBuilder` allocations).

---

## 3. Bottleneck Group II — Runtime `246 MB` Bookkeeping, `194k` Bridge Fallbacks & `5.2M` Bridge Direct Calls

Captured from `.temp/aot-cli/msync run-ir .temp/b4c-o2.mso --trace .temp/b4c-run-inst.jsonl --telemetry-level instructions < /dev/null`:

- `execute` metrics:
  - `managed.allocated_bytes`: **`247,379,752`** (`~246–247 MB`), whereas aggregate `vm.function` `logical_storage_bytes` across ALL `28,283` Msync allocations is **`1.75 MB`**! Over **99% (~245 MB)** of runtime heap allocation is pure C#/CLR runtime machinery overhead.
  - `tier.operations`: `12,011,664` (`tier.direct_operations`: `4,590,936`, **`tier.bridge_direct_operations`: `5,198,582`**, **`tier.bridge_fallback_operations`: `194,334`**)
  - `tier.compilations`: `21`, `tier.unavailable_compilations`: `23`, `tier.entries`: `222,161`, `tier.guard_misses`: `0`, `tier.deoptimizations`: `0`

### Exact Runtime Instruction & Function Telemetry

Top `vm.opcode` hits:
- `Narrow`: `2,105,384` | `Load`: `1,974,727` | `ProjectAddress`: `1,305,875` | `Branch`: `1,109,384` | `Copy`: `1,028,007` | `Binary`: `990,233` | `FrameAddress`: `769,355` | `Move`: `707,737` | `Constant`: `604,681` | `Unary`: `543,985` | `Jump`: `413,040` | `Convert`: `375,990` | `Return`: `251,771` | **`Call`: `250,029`** | `Store`: `176,225` | `AddressOffset`: `80,986` | `CallNative`: `44,111` | `Allocate`: `28,278`

Top `vm.native` calls:
- `math.floor`: `39,309` (now fast-pathed) | `ranges.empty_of`: `4,226` | `ranges.sequence`: `546` | `io.println`: `14` | others <= `3` each (total remaining non-fast `CallNative` = **`4,802`**).

Top `vm.function` breakdown:
- `fn[18]`: `6,368,708` ops (`58,668` calls, `jit=1`), `jit_direct=2,405,958` -> **`~3.96M` ops bounce to `TierBridge`!** Also `argCopy=113,260`.
- `fn[15]`: `1,430,011` ops (`11,386` calls, `jit=1`), `jit_direct=875,732`, `argCopy=11,386`.
- `fn[17]`: `998,244` ops (`55,458` calls, `jit=1`), `jit_direct=110,854`, **`tailBuf=55,458`!**
- `fn[26]`: `781,193` ops (`7,853` calls, `jit=1`), `jit_direct=293,212`, `argCopy=15,482`.
- `fn[20]`: `462,584` ops (`57,823` calls, `jit=1`), `jit_direct=173,376`.
- `fn[25]`: `139,158` ops (`7,731` calls, **`jit=0`**), **`tailBuf=7,731`!**
- `fn[35]`: `96,350` ops (`1,927` calls, **`jit=0`**), **`tailBuf=1,927`!**

### Root Causes of `~245 MB` Runtime Allocations

1. **Per-Call `NativeWorkScope` + `AsyncLocal<NativeWorkScope?>` Thrash (`NativeInterpreter.cs:189-200,229`)**:
   - `NativeExecutionImage.cs:76-87` sets `IsPureSynchronous = false` whenever a function contains ANY `Call`, `CallNative`, etc.
   - Therefore `trackScope` is `true` for almost every single one of the **`250,029` `Call`s**, allocating `new NativeWorkScope(...)` and mutating `currentWork.Value = owner` (`AsyncLocal` ExecutionContext copy) on entry AND restoring it on exit (~500k `ExecutionContext` mutations + 250k `NativeWorkScope` heap allocations), even when `profile is null` and the call executes synchronously!
2. **Per-Tail-Call Object & Storage Allocation (`NativeInterpreter.cs:180-182,244-253`, `NativeInterpreter.Aggregates.cs:122`)**:
   - Every tail call (`65k+` across `fn[17]` `55,458`, `fn[25]` `7,731`, `fn[35]` `1,927`, etc.) allocates `new TailInvocation(...)` at `NativeInterpreter.Aggregates.cs:122`.
   - Worse, `fn[17]` (`55,458` invocations) is entered from a non-tail caller and finishes with a tail call. Because `tailBuffers` (`Dictionary<NativeLayout, NativeAllocation>?`) is a **local variable** in `RunFunctionSync` (`NativeInterpreter.cs:180`), every single one of the `55,458` entries allocates a fresh `Dictionary<NativeLayout, NativeAllocation>` AND a fresh `new NativeAllocation(target.Parameters)` (`RawBytes` + `InitializedFlags` + `NativeAddress`!) only to drop it when `RunFunctionSync` returns (`tailBuf=55,458` out of `55,458` calls!). Pooling a 1-slot/small tail buffer (or reusing parameter storage directly on self-tail / matching frame!) eliminates all 65k allocations.
3. **Argument Array Copying (`NativeArguments.cs:46-68`)**:
   - `NativeCallSite.Arguments` allocates `new NativeDatum[inputs.Count]` whenever `!stable` (`argCopy=113,260` in `fn[18]` alone, plus `fn[15]`, `fn[26]`, `fn[14]`, `fn[23]`). A pooled/stack-backed or frame-view argument path for synchronous non-mutating arguments avoids `~150k+` small array allocations.
   - Remaining native calls (`ranges.empty_of: 4,226`, `ranges.sequence: 546`) still allocate `NativeDatum[]` + async `NativeValue[]` arrays via `ResolveArguments`.

### Root Causes of `194,334` `BridgeFallback` & `5,198,582` `BridgeDirect` & `23` `UnavailableCompilations`

1. **Why `tier.bridge_fallback_operations = 194,334`**:
   - In `NativeInterpreter.Tiering.cs:241-451`, `TierBridge`'s `switch` handles `Jump`, `Branch`, `Return`, `AddressOffset`, `ProjectAddress`, `EnvironmentAddress`, `Copy`, `Move`, `Drop`, `Narrow`, `Convert`, `Unary`, `Binary`, `Load`, `Store`, `Allocate (C < 0)`, `BindLocation`, `MakeFunction`, `MakeType`, `AdaptFunction`, `TryFastUnaryMath`.
   - Everything else falls to `default:` (`bridgeFallback++` -> `owner.ExecuteLinked(...)` async/step dispatch):
     - **`~185k` non-tail `Call` instructions** (`250,029` total `Call`s minus `~65k` tail calls handled by `IsTailCall` -> `TailExit`). Adding a synchronous fast-call path inside `TierBridge` (or direct call stub without `ValueTask` / `Tail` machinery when target & callee frame are known sync) removes almost all of the `194,334` fallback transitions.
     - Remaining `4,802` non-fast `CallNative` (`ranges.empty_of` `4,226`, `ranges.sequence` `546`) + `Allocate (C >= 0)` / iterators (`NextIterator` in jitted functions).
   - Meanwhile `23` functions (including `fn[25]` `139k` ops and `fn[35]` `96k` ops, both tail-recursive iterators/helpers!) failed `NativeCompiledFunction.IsProfitableForAuto` (`NativeCompiledFunction.cs:41-84`), leaving `tier.unavailable_compilations = 23`.
2. **Why `tier.bridge_direct_operations = 5,198,582` (JIT <-> Managed Ping-Pong)**:
   - Look at `NativeCompiledFunction.Select` (`NativeCompiledFunction.cs:86-156`) vs top workload opcodes:
     - **`Narrow` (`2,105,384` hits, #1 opcode)** only lowers to direct machine code (`NumberCopy`/`BoolCopy`) when `Result` is `Number` or `Bool` (`lines 119-120`). Nullable record/allocation pointer narrows (`Node? -> Node`, the hottest pattern in `bplus-inventory`'s B+ tree traversal inside `fn[18]`!) ALL compile to `NativeFastOperation.Bridge`.
     - **`Load` (`1,974,727` hits, #2 opcode)**, **`ProjectAddress` (`1,305,875` hits, #3 opcode)**, **`AddressOffset` (`80,986`)**, and non-frame **`Store` (`176,225`)** ALL compile to `NativeFastOperation.Bridge`.
   - In `fn[18]` (`6.37M` ops), `3.96M` operations (`62%`) hit `TierBridge`, causing millions of native-JIT-to-managed transitions (`[UnmanagedCallersOnly]` / `GCHandle` roundtrips). Supporting inline native fast paths (or fused address-project + null/kind-checked `Load`/`Store`/`Narrow` of `Number`/`Bool`/pointer slots) inside `NativeMachineCode` directly eliminates the majority of those `5.2M` `Bridge` crossings.

---

## 4. Suggested Action Plan for High-Leverage Cuts

1. **`-O2` Escape Analysis & `ProgramFacts` Invalidation**:
   - Distinguish alias/call-affecting edits from pure scalar/dead-code/move edits in `NativeOptimizer.OptimizeFunction`; stop invalidating transitive callers inside bottom-up `OptimizeFunction` when the callee's summary didn't change (or lazily invalidate only on `SummaryVersion` change / `Allocate`/`Store`/`BindLocation`/`Call` mutation). Skip redundant `EscapeAnalysis` on final converging no-op passes; reuse `NativeLiveness` instead of reconstructing it up to 6x per function.
2. **Frontend Flow / Instantiation Multiplication (`880` flow solves for `146` functions)**:
   - Inspect why `compiler.semantic.instantiate` fires `354` times and `compiler.flow.prepare`/`solve` fires `880` times (`query` vs `publish` calls, speculative checks in `SemanticAnalyzer.FlowPreparation.cs` / `CallPolicy.cs`), and reuse prepared flow graphs/domains rather than rebuilding them per query.
3. **Runtime Allocation Eradication (`246 MB` -> near `1.75 MB`)**:
   - Avoid allocating `NativeWorkScope` / writing `currentWork.Value` (`AsyncLocal`) on pure-sync or untraced `Call`s (`IsPureSynchronous` / fast sync call path when `profile is null`).
   - Pool `tailBuffers` / reuse parameter allocations + eliminate `new TailInvocation` allocation in `RunFunctionSync`; avoid `new NativeDatum[]` snapshots for non-escaping/sync `NativeCallSite` calls and add zero-alloc fast handlers for `ranges.empty_of` / `ranges.sequence`.
4. **Tier-1 Direct Machine Code Coverage (`5.2M` Bridge Direct + `194k` Bridge Fallback)**:
   - Lower common `Load`, `ProjectAddress`/`AddressOffset`, non-primitive pointer `Narrow` (single null/layout tag test), and `Frame`/`Heap` `Store` directly in `NativeMachineCode` + `NativeCompiledFunction.Select` instead of calling `TierBridge` on every B+ tree field access in `fn[18]`.

### Reproduction & Quick Trace Commands

```bash
# 1. Rebuild & publish Native AOT CLI
dotnet publish src/Msync.Cli/Msync.Cli.csproj -c Release -r linux-x64 -o .temp/aot-cli --no-restore -v q

# 2. Correctness + Native AOT timing vs baseline
.temp/aot-cli/msync run .temp/benchmarks/opt-fix-check-3/applications/bplus-inventory/standard/17/workload.msync -O2 < /dev/null

# 3. Profile traces (compile O2 & runtime instructions)
.temp/aot-cli/msync compile .temp/benchmarks/opt-fix-check-3/applications/bplus-inventory/standard/17/workload.msync -O2 -o .temp/b4c-o2.mso --trace .temp/b4c-comp-o2.jsonl --telemetry-level profile < /dev/null
.temp/aot-cli/msync run-ir .temp/b4c-o2.mso --trace .temp/b4c-run-inst.jsonl --telemetry-level instructions < /dev/null > /dev/null

# 4. Focused verification suites (do NOT run full msync.slnx)
dotnet run --project tests/HirFlow -c Release
dotnet run --project tests/NativeMiddleEnd -c Release
dotnet run --project tests/NativeOptimization -c Release
dotnet run --project tests/NativeTiering -c Release
dotnet run --project tests/NativeModel -c Release
dotnet run --project tests/NativePreparation -c Release
```

## 5. Completed continuation: compiled storage authority (2026-11-13)

The user explicitly selected a breaking v0.9 artifact boundary rather than
legacy fallback machinery. The implemented contract is documented in
[native execution lowering](native-execution-lowering-v090.md#v09-compiled-storage-contract-2026-11-13).

### Delivered changes

- Runtime no longer constructs `NativeAddressAnalysis.ProgramFacts` or
  `NativeEscapeAnalysis`, or invokes `NativeAllocationSafety` / `NativeCallFacts`.
- The optimizer captures its existing converged address/escape results into
  `NativeStoragePlan`; no second final escape-summary pass is needed for optimized
  functions. Last-use move transformations preserve these facts, and coloring,
  compaction, folding and preparation relocate them with the code. O0/raw producers
  compute their plans once. Unoptimized empty/template entries have small explicit
  storage-analysis spans rather than acquiring runtime fallback behavior.
- Call snapshot/borrow decisions are compiled, including physical carrier ABI
  compatibility. There is no runtime `NativeCallSite` analyzer or type comparison.
- Native format 2 / compact revision 2 requires the plans. HIR uses format 4.
  Old encodings and format-6 CLI importing are removed; old bytes must be rebuilt.
  Neither Native nor HIR codec implicitly reruns semantic verification. Explicit
  diagnostic APIs remain available to compiler/test callers.
- Tail-call context no longer retains an Activation. Normal and asynchronous
  completions return activations. Tail arguments use nonoverlapping buffer leases;
  caller snapshots are frame-owned and detached for native/lazy retention.

An initial attempt rebuilt final address graphs in the compiler and regressed
source execution. It was replaced, not accepted as the final design. Reusing
only `ProgramFacts` still repeated escape summaries; capturing `finalEscape`
removed that duplication too. The final recorded storage materialization costs
5.35 ms across 182 function observations, plus 1.54 ms for 88 missing-plan
empty/template analyses, versus 226 ms in the intermediate shared-graph version.
These are separate instrumented receipts, not telemetry-off timing claims.

### Final Native AOT receipt

Images: `artifacts/native/batch4c/msync` (frozen input) and
`artifacts/native/storage-contract/msync` (delivered image). Payload/source hashes,
raw samples, source, expected output, artifacts, traces, typed reproduction script
and test SQLite receipt live in `artifacts/benchmarks/storage-contract/`.

Same B+ tree source, EOF stdin, `MSYNC_JIT=auto`, `--no-config`, telemetry disabled
for timing, fresh processes, one warmup and three alternating measured pairs:

| Route | Frozen 4C median | Final median |
| --- | ---: | ---: |
| Compile O2 | 3785.2 ms | 3855.4 ms |
| Run O2 artifact | 2046.9 ms | 1817.7 ms |
| Source O2 end to end | 5873.9 ms | 5743.8 ms |

Artifact execution improved 11.2%; source end-to-end improved 2.2%. The compile
median was 1.9% higher in this small local sample: this is not a claim that every
route became faster. Source and artifact routes produced exactly the expected
14 lines in every checked run. Intermediate images/receipts remain under the
`storage-contract-initial`, `storage-contract-shared` and `storage-contract-abi`
suffixes; do not mix their binary identities with the final receipt.

Separate matched profile recordings show execution managed allocation decreasing
from **245,958,472 to 144,277,472 bytes** (41.3%). Logical storage byte counts do not
include CLR object headers, root banks and runtime representation costs, so the
original "99% is bookkeeping" estimate should not be interpreted as an exact
allocation attribution. Borrowed arguments (337,883), copied arguments (183,554),
semantic snapshots (68,883) and tail argument copies (138,946) match the baseline.
Only 11 physical snapshot arrays were allocated; tail buffers fell from 69,100
allocations to 11, with 69,089 reuses. Final artifact-run recordings contain no
`compiler.verify.*`, `compiler.storage.*` or allocation-hint-normalization records.

Acceptance:

- `CallLowering` source/codec, captures, aliases, parameter snapshots, writable
  parameters, parallel tail arguments, real asynchronous input and frame reuse.
- Focused `TailCallOptimization --storage`: escaped/bound result writes, call-site
  failures, exact logical instruction/edge/target telemetry and 100,000 tail calls.
- Current artifact CLI journey: old-format rejection, compiled object inspection,
  source removal, binding, object removal and execution from another directory.
- Native AOT `storage_journey.py`: source-free HIR/object/bound routes, selected
  transfer lifetime, O0/O2, Off/Auto/Force and Native/HIR old-format rejection.
- Native AOT compiler telemetry: O0–O3 replay, byte-identical observed compilation,
  failure context, HIR/SQLite ownership and coarse/detail selection.

The pre-existing broad tail source suite also exposed a frontend-only case:
`let first=fn {0}; fn walk(n) { let snapshot=n; if n==3 {first=fn {snapshot};}; if n==0 {null} else {walk(n - 1)} }; walk(3); first()`.
The frozen 4C compiler also exceeded a 10-second bound; the current trace remained
in semantic instantiation/flow preparation before Native lowering. This is a
separate frontend investigation, not a reason to restore runtime analysis. The
focused tail/storage cases above isolate the execution contracts changed here.

Remaining architectural work is still explicit: ordinary frames use byte/root
storage behind managed frame objects, not yet a segmented byte/root execution
stack; work-scope/AsyncLocal ownership and direct JIT call/field lowering have not
been replaced by this batch. The runtime's duplicate compiler responsibilities
are removed without claiming those separate changes were implemented.
