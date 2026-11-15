# Optimizer Batch 3B Blocker Handoff (`NativeArtifact.Validate` Flow Failure at `-O2`)

> Status: **Batch 3B initialization blocker resolved; Native AOT acceptance complete (2026-11-13)**\
> Workload: `.temp/benchmarks/opt-fix-check-3/applications/bplus-inventory/standard/17/workload.msync` (note: line 10 calls `$std::io::read_line()`, so always redirect `< /dev/null`; expected stdout: `1218\n580\n298\n671\n200\n866\n836497\n866\n143\n169\n-1\n-1\n3287\n3322`)

Current runtime continuation: [latest handoff](perf-batch4c-handoff.md#latest-handoff-2026-11-15).
This document preserves the resolved optimizer failure, not an active blocker.

---

## Resolution: defer deletion until cross-block rewrites finish

The corrected stage probe reproduced the first invalid transition in local
optimization iteration 2, before dead-result elimination or last-use moves:

```text
pc 493: Narrow(result=339, source=2) -> Nop
pc 617: Narrow(result=417, source=2) -> Copy(result=417, source=339)
pc 619: ProjectAddress(result=419, source=417) -> ProjectAddress(result=419, source=339)
```

`ComputeEntryFacts` correctly made the earlier narrowing available downstream.
The problem was `Simplify` deleting a proven-redundant narrow based on `allReads`
from before rewriting, while downstream blocks were still consuming those entry
facts. A register unused in the input can become the representative of a reused
expression in the output. Initialization filtering alone cannot repair that
analysis/transformation lifetime mismatch.

`NativeLocalOptimization` now retains proven-redundant narrow candidates during
rewriting. Once all blocks have consumed the entry facts, `RemoveUnusedNarrows`
builds the actual output read set and deletes only candidates without readers.
It does not disable cross-block reuse, run an extra analysis after each rewrite,
or change artifact validation. Checked narrows without a redundancy proof are
not deletion candidates. The existing optimizer fixed point handles any later
dead-use cleanup.

The producing function/local-values span reports
`local_opt.redundant_narrow_candidates`, `local_opt.redundant_narrows_removed`,
and `local_opt.redundant_narrows_retained`, alongside the existing eliminated
narrow count. No new recorder or runtime execution path is introduced.

Acceptance completed:

- Original B+ tree source: Release CLI `compile -O2`, artifact read, and `run-ir`
  with EOF stdin produce all 14 expected lines exactly.
- `NativeMiddleEnd`: PASS 711, including new `CrossBlockNarrowReuse` journeys:
  2/3/5 basic blocks, O0/O1/O2/O3, artifact encode/decode, null and numeric inputs,
  signed-zero preservation, and exactly one shared narrow at optimized levels.
- `NativeTiering`: PASS 2165.
- `NativeOptimization`: PASS 3191, including existing type-bridge checks.
- Native AOT: source and artifact routes match exact stdout in all warmup,
  paired timing, and recorded runs; artifact disassembly succeeds.

The probe must install its mutable instruction array into the program's function
view before calling passes using `NativeLiveness`; otherwise it checks one array
while computing liveness from another. The original handoff probe omitted that
installation and stopped after three iterations. Its suspicion list below is
historical, not the final diagnosis.

This implementation follows the analysis-lifetime rule described in
[LLVM's pass manager](https://llvm.org/docs/NewPassManager.html): transformation
must preserve, update, or invalidate its analysis assumptions. The specific
dependency is the interaction between expression reuse, copy propagation, and
dead-code elimination described in
[Cornell's redundancy-elimination notes](https://www.cs.cornell.edu/courses/cs4120/2026sp/notes.html?id=redund_elim).
Here preserving definitions until rewrite completion is simpler than maintaining
an incremental cross-block use graph.

---

## 1. Exact Blocker (Where We Are Stuck)

Compiling `workload.msync` at `-O2` produces valid-looking bytes (`.temp/b3b.mso`), which immediately fail definite-initialization validation in [NativeArtifact.Flow.cs](file:///home/moesegfault/msync/src/Msync.Core/Artifacts/NativeArtifact.Flow.cs#L51-L53) when read back:

```text
error: Native IR artifact at byte 96442: Register 162 is not definitely initialized at instruction 205 in '<lambda>'
  --> msync-stdlib:///0.8.0/containers.msync (source offset 55961..55965)
```

### Stage Isolation Already Confirmed

Running `NativeOptimizer.Optimize` stage-by-stage on `.temp/b0.mso` (`-O0` compilation of the same workload) via `.temp/probe/Probe.csproj` established:

| Check / Stage | Result | Notes |
| --- | --- | --- |
| `NativeOptimizer.Optimize(b0, level: 1)` | **PASS** | All `Validate` checks pass at `-O1` |
| `NativeInlining.Run` (`level: 2`) | **PASS** | `p1` passes `Validate` |
| `OptimizeFunction` loop (`level: 2`) | **FAIL** | Fails on `<lambda>` (`Register 339 is not definitely initialized at instruction 617` before `Compact1`) |
| After `Compact1` -> `NativeRegisterAllocation` | **FAIL** | Same defect renumbered to `Register 21 at instruction 205` (or `Register 162 at 205` when compiled directly from source) |

### Smoking-Gun Observation Inside `fn[67]` (`<lambda>`)

Before `OptimizeFunction` (`p1` after `NativeInlining.Run`), `p1.Functions[67]` contains definitions of register `339` (`Narrow r=339, a=338, t=103`).

After `OptimizeFunction` (`level: 2`) finishes on `fn[67]`:
- **Zero instructions** in `fn[67]` have `Result == 339` (every definition of `339` disappeared).
- Yet instructions `617` and `619` still read `339`:
  - `617: Copy(r=417, a=339, t=103)`
  - `619: ProjectAddress(r=419, a=339, b=23)`

### Why `-O1` Passes While `-O2` Fails

Only three `-O2`-only mechanisms execute inside `OptimizeFunction` ([NativeOptimizer.cs](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeOptimizer.cs#L115-L175)):
1. `NativeGlobalFacts.Run`
2. `NativeGlobalMemory.Run` (rewritten in Batch 3B to track projected `Pointers` + `Fields` across blocks)
3. `CollapseSingleUseMoves` (added in Batch 3B at [NativeOptimizer.cs](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeOptimizer.cs#L172), lines [254-317](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeOptimizer.cs#L254-L317))

**Next diagnostic command (ready to run):**\
`.temp/probe/Program.cs` already contains an exact pass-by-pass mirror of `OptimizeFunction` for `fn[67]` (checking `Validate` after `GM`, `Loc`, `RD`, `MoveLastUses`, and `CollapseSingleUseMoves` across fixpoint iterations). Just execute it:

```bash
dotnet build .temp/probe/Probe.csproj -c Release -v q && dotnet run --project .temp/probe/Probe.csproj -c Release --no-build
```

Also check CLI repro end-to-end:

```bash
src/Msync.Cli/bin/Release/net10.0/msync disassemble .temp/b3b.mso --json > /dev/null
```

---

## 2. Known Clues & Suspicious Spots in Batch 3B Code

1. **`NativeLocalOptimization.cs` (two bugs already fixed just before pausing, double-check remaining edges)**:
   - Fixed: `ComputeEntryFacts` previously gated `trackInit` on `frameCells.Known` (`false` in closure-using `<lambda>`), paired with an unchecked `inheritedNarrows` bypass in `Simplify`. Now `ComputeEntryFacts` always tracks `entryInit` (including `CaptureCount + ParameterCount` at block `0`), and `Simplify` requires `initialized.Contains(prevNarrow)`.
   - Fixed: `Simplify` for `Narrow` (`availableNarrows`) now checks `prevNarrow != instruction.Result` so self-copies aren't generated in loops.
   - **Still check**: `ComputeEntryFacts` runs once *before* `Run()` mutates instructions in-place across blocks in a pass. If an earlier block rewrites or eliminates a `Copy` defining `prevNarrow`, downstream blocks reading stale `entryNarrows` / `entryInit` during the *same* `Run()` pass could rewrite `Narrow(..., ...)` into `Copy(..., prevNarrow)`.
2. **`NativeGlobalMemory.cs` ([lines 1-192](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeGlobalMemory.cs#L1-L192))**:
   - Batch 3B extended `BlockState` with `Pointers` (`ProjectAddress`) and `Fields` (`(BaseSlot, Field) -> Stored`).
   - Check `Transfer` ([lines 114-169](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeGlobalMemory.cs#L114-L169)): verify whether `state.Fields` / `state.Targets` properly handles:
     - `Move` / `Drop` invalidating `Stored.Source` (especially across back-edges in `Incoming` before all predecessors have non-null `outgoing[pred]`).
     - Aliasing/invalidations when `ProjectAddress` or `Store` runs on partially known targets.
3. **`NativeOptimizer.cs` `CollapseSingleUseMoves` ([lines 254-317](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeOptimizer.cs#L254-L317))**:
   - Check the `Move`/`Copy` collapse (`pc: Move/Copy(mid <- src)` + `nextPc: Move(dest <- mid)`) and producer collapse (`pc: op(mid <- ...)` + `nextPc: Move(dest <- mid)` -> `op(dest <- ...)`):
     - Wait: if `pc` is `Narrow` (which updates `availableNarrows` or whose `Result == mid` is `Escapes`? Wait: `!function.Slots[ins.Result].Escapes` checks the post-`finalEscape` bit, good), what if `pc` was `Copy(dest <- src)` -> `Move(dest2 <- dest)`, or `dest == ins.A` (`Nop`), AND **`live.After[nextPc]`** missed a live use because `flow` was not rebuilt between passes, OR `MoveLastUses` on line 171 already turned a still-needed `Copy` into `Move`?
     - Note: in one earlier probe run (`task-2370.log`) where `finalEscape.EscapingLocations` hadn't been synced onto `Slots` before `MoveLastUses`, `MoveLastUses` turned `3: Copy(118, 117)` into `Move(118, 117)` right before `4: ProjectAddress(119, 117)` (`Register 117 is not definitely initialized at instruction 4`). Double-check `NativeLiveness` / `NativeInstructionFacts` interaction with `MoveLastUses` too just in case!

---

## 3. Verification State Before Pausing

- Passing right now (after latest edits to `NativeOptimizer.cs` & `NativeLocalOptimization.cs`):
  - `dotnet run --project tests/NativeMiddleEnd/Msync.NativeMiddleEnd.Tests.csproj -c Release` -> **PASS 666**
  - `dotnet run --project tests/NativeOptimization/Msync.NativeOptimization.Tests.csproj -c Release` -> **PASS 3191** (fixed `TightenSlotTypes` earlier so `TypeBridgeChecks` passes)
  - `dotnet build src/Msync.Cli/Msync.Cli.csproj -c Release --no-restore` -> **0 Warning(s), 0 Error(s)**
- Note on slow/unverified suites (do NOT run `dotnet test msync.slnx` blindly during tight iteration):
  - `tests/NativeTiering` (`PASS 2165`) and `workload.msync` Tier-1 Auto profile were verified after Batch 3A Part 2 (before Batch 3B).

---

## 4. Summary of All Completed Optimizations So Far

- **Native AOT Baseline** (`.temp/aot-baseline/msync`, `bplus` workload with `< /dev/null`):
  - `compile -O2`: `5870.9 ms` | `run-ir O2`: `4172.7 ms` | `e2e -O2`: `10513.3 ms`
- **Batch 1** (already measured on Native AOT: `compile -O2: 3365.9 ms`, `run-ir O2: 3804.7 ms`, `e2e -O2: 7102.3 ms`):
  - [NativeAddressAnalysis.cs](file:///home/moesegfault/msync/src/Msync.Core/NativeModel/Analysis/NativeAddressAnalysis.cs), [NativeAddressAnalysis.Calls.cs](file:///home/moesegfault/msync/src/Msync.Core/NativeModel/Analysis/NativeAddressAnalysis.Calls.cs), [NativeInlining.cs](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeInlining.cs), [NativeOptimizer.cs](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeOptimizer.cs), [NativeRegisterAllocation.cs](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeRegisterAllocation.cs) (`sharedFacts` incremental `Invalidate`, `inline.captureless_direct`, `CompactSet`).
- **Batch 2A-1 & 2A-2** (Middle-End):
  - [NativeLocalOptimization.cs](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeLocalOptimization.cs): `TightenSlotTypes`, `ComputeEntryFacts` + `AdvanceTypeFacts` (`Branch` `IsNull` else-edge refinement, redundant `Narrow`/`Convert`/`IsNull` elimination).
- **Batch 3A Part 1 & Part 2** (Tier-1 JIT & Zero-Boxing Bridge — `MSYNC_JIT=auto`):
  - [NativeSpeculation.cs](file:///home/moesegfault/msync/src/Msync.Runtime/NativeModel/Tiering/NativeSpeculation.cs), [NativeExecutionImage.cs](file:///home/moesegfault/msync/src/Msync.Runtime/NativeModel/Tiering/NativeExecutionImage.cs) (fixed `HasKind(slot, Null)` bug!), [NativeTierMemory.cs](file:///home/moesegfault/msync/src/Msync.Runtime/NativeModel/Tiering/NativeTierMemory.cs), [NativeCompiledFunction.cs](file:///home/moesegfault/msync/src/Msync.Runtime/NativeModel/Tiering/NativeCompiledFunction.cs), [NativeRegisterPlan.cs](file:///home/moesegfault/msync/src/Msync.Runtime/NativeModel/Tiering/NativeRegisterPlan.cs), [NativeMachineCode.cs](file:///home/moesegfault/msync/src/Msync.Runtime/NativeModel/Tiering/NativeMachineCode.cs): added JIT `NumberMove`, `BoolMove`, `IsNull` (`tier.compilations: 21`, `tier.unavailable_compilations: 23`, `tier.direct_operations: 4,601,402`).
  - [NativeStorage.cs](file:///home/moesegfault/msync/src/Msync.Core/NativeModel/NativeStorage.cs) (`CopyKindTrusted`), [NativeResult.cs](file:///home/moesegfault/msync/src/Msync.Runtime/NativeModel/NativeResult.cs) (`TryNarrowReady`, `TryConvertReady`), [NativeImage.cs](file:///home/moesegfault/msync/src/Msync.Runtime/NativeModel/NativeImage.cs) (cached `AdmitsLayout`), [NativeInterpreter.Operators.cs](file:///home/moesegfault/msync/src/Msync.Runtime/NativeModel/NativeInterpreter.Operators.cs), [NativeInterpreter.Tiering.cs](file:///home/moesegfault/msync/src/Msync.Runtime/NativeModel/Tiering/NativeInterpreter.Tiering.cs) (synchronous fast path in `TierBridge`: `5,189,587` direct vs `230,403` fallback = `95.7%` bridge bypass).
- **Batch 3B** (WIP — root of the current blocker):
  - [NativeGlobalMemory.cs](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeGlobalMemory.cs), [NativeLocalOptimization.cs](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeLocalOptimization.cs), [NativeOptimizer.cs](file:///home/moesegfault/msync/src/Msync.Optimization/NativeModel/NativeOptimizer.cs).

## 5. Remaining Planned Steps Once Batch 3B Validates Cleanly

1. Fix the `Register 339` / `Register 162` `-O2` initialization bug in Batch 3B, verify `workload.msync` exact stdout (`< /dev/null`) + `tests/NativeTiering`, and check remaining runtime hotspots on Native AOT (`tier.bridge_*`, direct `Call` / `ProjectAddress` / `Load`).
2. Profile `msync compile -O2 --trace ... --telemetry-level phases` to eliminate dominant `Msync.Compiler` overhead (`compiler.flow.*`, frontend semantic checking/HIR).
3. Final **Native AOT** (`dotnet publish src/Msync.Cli/Msync.Cli.csproj -c Release -r linux-x64 -o .temp/aot-cli --no-restore`) A/B benchmark vs `.temp/aot-baseline/msync`, persist final benchmark & architecture doc, and clean up `.temp/`.

## 6. Reproduction and Native AOT acceptance (2026-11-13)

Build the current product once, then run the checked source/object journeys:

```sh
dotnet build src/Msync.Cli/Msync.Cli.csproj -c Release --no-restore
src/Msync.Cli/bin/Release/net10.0/msync compile \
  artifacts/benchmarks/batch3b/workload.msync -O2 -o .temp/b3b.mso
src/Msync.Cli/bin/Release/net10.0/msync disassemble .temp/b3b.mso --json > /dev/null
MSYNC_JIT=auto src/Msync.Cli/bin/Release/net10.0/msync run-ir .temp/b3b.mso \
  < /dev/null > .temp/b3b.out
diff -u artifacts/benchmarks/batch3b/expected.txt .temp/b3b.out

dotnet run --project tests/NativeMiddleEnd/Msync.NativeMiddleEnd.Tests.csproj -c Release
dotnet run --project tests/NativeTiering/Msync.NativeTiering.Tests.csproj -c Release
dotnet publish src/Msync.Cli/Msync.Cli.csproj -c Release -r linux-x64 \
  -o artifacts/native/batch3b --no-restore
```

The retained benchmark payload is `artifacts/benchmarks/batch3b/`: source, expected
stdout, baseline/candidate relocatable artifacts, binary hashes and raw paired
samples in `measurements.json`, plus separate compile/runtime profile traces.
The candidate is `artifacts/native/batch3b/msync`; the historical frozen baseline
is `.temp/aot-baseline/msync` from the earlier batches. This comparison measures
all accumulated working-tree optimizations, not the marginal benefit of the
initialization fix alone.

Timing uses fresh native processes, `--no-config --no-telemetry`, `MSYNC_JIT=auto`,
EOF stdin, one warmup per image/route and three measured pairs with alternating
image order. Compile includes writing its relocatable artifact; `run-ir` reads
that image's own artifact; source runs include compilation and execution. Every
execution requires exact stdout equality. Disassembly checks artifact admission
outside timing. Compilation, object execution and source execution are separate
observations, not additive components. Recording runs occur after timing.

| Native AOT route | Baseline median | Candidate median | Reduction |
| --- | ---: | ---: | ---: |
| Compile O2 | 5857.4 ms | 3817.1 ms | 34.8% |
| Run O2 artifact | 4077.9 ms | 2226.2 ms | 45.4% |
| Source O2 end to end | 10236.1 ms | 5969.4 ms | 41.7% |

These are three-pair local latency observations for this B+ tree workload, not
throughput measurements or an isolated Batch 3B speedup. Raw samples and hashes
are retained so subsequent batches can reuse this accepted candidate.

### Remaining optimization priorities from the accepted native image

The separate compile profile places most exclusive synchronous span time in:

| Phase | Exclusive recorded time |
| --- | ---: |
| Semantic instantiation | 755.9 ms |
| Native escape analysis | 728.0 ms |
| Native inlining | 504.1 ms |
| Native local values | 374.6 ms |
| Frontend flow solve | 371.2 ms |
| Frontend flow preparation, excluding child spans | 310.8 ms |

Exclusive time here subtracts immediate synchronous child spans, considering
span records only, not aggregate events. Recorded timing explains work but is
not substituted for telemetry-off latency. Semantic instantiation made 354
requests (200 cache hits, 154 misses). Its largest inclusive requests are the
B+ tree insert/delete/find wrappers; nested requests must not be summed as
independent costs. The current profile therefore does **not** support treating
lexing/parsing as the next major frontend target.

Next frontend work should focus on instantiation and the flow fact lifecycle.
`HirFlowGraphBuilder.Analyze` solves the graph, then reapplies block operations to
materialize per-node `Inputs`. Investigate sharing the converged transfer results
with input-point materialization, without caching projections across changing
semantic contexts or skipping required publication. This is a concrete next
optimization candidate, not an implemented change or a measured win.

Runtime recording reports 21 successful tier compilations, 23 unavailable
compilations, 4,725,915 direct operations, 5,159,351 synchronous bridge-direct
operations and 233,565 bridge fallbacks (95.7% direct among bridge operations).
There were no guard misses or deoptimizations. VM counts include 2,105,384 Narrow,
1,974,727 Load, 1,305,875 ProjectAddress and 250,029 Call operations. These are
frequency counts, not per-op time attribution: use them to select further
specialization experiments, not to claim each opcode consumes proportional time.

The new narrow cleanup telemetry records 67 proven-redundant candidates,
1 deletion and 66 retained definitions over all function/pass invocations.
Retaining a representative is a successful reuse dependency, not a failed
optimization. The original compiler/runtime hotspot work remains a separate
next batch; the invalid-artifact blocker no longer prevents that investigation.
