# Function leaf SSA / runtime projection review

## Scope and received performance evidence

Read [shared context](../../design/llvm-performance-regression-review.md) first; reviewed current target lowering, Function ABI/storage,
ready/pending projection and composition, tier execution, existing rendered
artifacts, and instruction-recording receipts. No production edits, builds,
new executions, or timing runs were performed.

The final telemetry-off standard/29 five-pair results supplied to this reviewer
are numeric bound/source +1.71%/+3.66%, policy +1.70%/+5.01%, Unicode
+3.97%/+7.69%. The Unicode bound process p50 values are 3049.088/3161.940 ms;
the percentages are medians of paired changes, not ratios of those p50s.
These are the residual results **after** tier-direct integration, literal
reuse, PHI carrier-copy repair, and actual environment-schema recovery.

## Ranked findings

### 1. High-value performance lead: canonical nullable environment leaves erase the exact-address path at all 195,000 hot Unicode projections

**Evidence strength:** confirmed extra runtime work and actual artifact carrier
change; plausible contributor, not isolated wall-time attribution or a language
correctness defect.

**Producing source locations:**

- `src/Msync.Hir/Lowering/LlvmNativeLowerer.AggregateSlots.cs:38-41` assigns
  every canonical Function environment leaf `Scope | Null`.
- The same file, `:124-141`, projects whole Functions using that canonical
  leaf carrier.
- `src/Msync.Hir/Lowering/LlvmNativeLowerer.AggregateOperations.cs:27-38`
  aliases an extract to that leaf slot instead of retaining the extract's
  more precise logical carrier.
- `src/Msync.Hir/Lowering/LlvmHirLowerer.Calls.cs:155-162` already publishes
  the known source call's concrete capture-storage logical type on the
  environment extract. This is an existing source ABI fact, not a request
  for a new proof/alias solver. Whether all relevant annotations survive
  SROA should be checked before choosing the precise lowering repair.
- The separate memory-component route likewise unconditionally selects the
  canonical shape carrier at `LlvmNativeLowerer.FunctionMemory.cs:49-57`;
  it should use preserved ABI facts where available, not infer nonnull from
  the mere presence of a dereference.

**Actual rendered artifact and executed shape:**

`.temp/function-perf/final/unicode-path-manifest-{baseline,candidate}-artifact.json`
contains:

| Control `<main>` | Candidate `<main>` | Executions in final observation |
| --- | --- | ---: |
| EnvironmentAddress pc1266 -> slot951, type32 Scope/layout7 | FunctionComponent pc1206 -> slot891, type61 Null,Scope | 65,000 |
| EnvironmentAddress pc1273 -> slot958, type32 Scope/layout7 | FunctionComponent pc1213 -> slot898, type61 Null,Scope | 65,000 |
| EnvironmentAddress pc1279 -> slot964, type32 Scope/layout7 | FunctionComponent pc1219 -> slot904, type61 Null,Scope | 65,000 |

All three are environment component 1. Each is immediately consumed by
`AddressOffset`, then a capture load and the original whole-Function `Call`.
The source calls are `path::extension`, `path::filename`, and `path::stem`.
`artifacts/diagnostics/llvm-sroa/unicode-tier/after.jsonl` records the exact
candidate PCs and 65,000 hits each, so the pre-tier-patch rendered artifact is
still relevant to the final hot instruction shape. Final counters report all
195,000 projections ready and zero pending.

**Runtime difference:**

1. Both old and new paths first use `Frame.GetDatum(...).Value.AsFunction()`.
   That source read is shared work; it must not be presented as wholly new.
2. The old tier path at
   `src/Msync.Runtime/NativeModel/Tiering/NativeInterpreter.Tiering.cs:277-280`
   obtains the environment address and calls `Frame.SetPointer`. Exact Scope
   storage selects `memory.WriteAddress` at `NativeFrame.cs:155-161`, then
   the fixed address writer (`NativeResult.cs:177-181`,
   `NativeStorage.cs:413-420`).
3. The new ready projection at `NativeInterpreter.FunctionParts.cs:18-23`
   calls `NativeFunctionParts.Component` (`NativeFunctionParts.cs:9-16`),
   reconstructs an address `NativeValue`, and uses generic `Frame.Set`
   (`NativeFrame.cs:113-115`) / `NativeMemory.Write`
   (`NativeResult.cs:143-168`) / `Data.Write`
   (`NativeStorage.Data.cs:87-105`). This selects representation by kind,
   moves roots, writes the union tag, and marks the nullable carrier's bytes.
4. More importantly, the **next** `AddressOffset` calls `Frame.Pointer`.
   `FrameAllocation.cs:52-54` marks only monomorphic Address carriers as
   `ExactAddress`. Candidate `Null | Scope` therefore misses
   `LoadAddressDirect` at `NativeFrame.cs:148-150` and takes another
   `GetDatum(...).Value.AsAddress()`. This rereads the tag/payload/roots through
   `Data.ReadInitialized` (`NativeStorage.Data.cs:67-85`) and reconstructs the
   address (`NativeValue.cs:87-90`). The old monomorphic Scope slot directly
   loads it. Thus the representation change adds work on both sides of the
   projection boundary even though the opcode count is almost unchanged.

**Narrow next probe / correction:** separate (a) generic ready projection
dispatch cost from (b) loss of exact-address carrier. First probe a component-1
ready address path while retaining the same nullable destination (preserve the
tag and the pending MapMachine path); then probe restoring the existing known
call's monomorphic capture-environment carrier from its surviving logical ABI
annotation and use `SetPointer` for that exact destination. Compare the same
frozen Unicode journey with telemetry off, then inspect one diagnostic run to
confirm the intended storage path. A raw `StoreAddress` into the current union
slot is not a valid fix: its payload offset/tag differ. Preserve nullable
carriers for genuinely nullable PHIs/selects and pending projections.

The preferred durable fix is carrier preservation at the established typed
boundary, if the annotation is available, rather than inventing a target
nonnull analysis. The current typed Function ABI can remain unchanged.

### 2. Lower priority: ComposeFunction performs generic cold boundary work, but repeated hot packing is excluded by the actual workloads

**Evidence strength:** confirmed execution cost, weak/no evidence for material
residual latency impact. This is not a request to redesign the ABI.

`NativeInterpreter.FunctionParts.cs:35-42` reads three datums, checks readiness,
and invokes composition; `NativeFunctionParts.cs:31-40` decodes the code,
resolves actual storage schema for nonnull environments, and creates an
environment view. `TryAggregateSlot` at `AggregateSlots.cs:115-120` may emit a
composition per boundary for a descriptor assembled only from leaves. Repeated
uses of existing whole ABI registers avoid composition via `values.TryGetValue`.

But the existing observations show **180 ComposeFunction executions total**
in Unicode, replacing 180 MakeFunction, and **164 total** in policy, replacing
164 MakeFunction. Each rendered composition site executes once. Policy has no
FunctionComponent executions at all. Therefore neither repeated per-record
composition nor code/name projection can explain these hot loops. It would be
low-value to add cross-branch pack caches here before resolving the carrier
lead or the separately assigned cold register-analysis lead.

`NativeEnvironment.View` really does allocate a small managed wrapper:
`NativeDescriptors.cs:83-91` defines a class and returns `new`. It does **not**
allocate/copy capture storage. The previous capturing MakeFunction already
created a NativeEnvironment plus its allocation
(`NativeInterpreter.Aggregates.cs:21-34`). No allocation regression can be
deduced merely by seeing the view constructor. `NativeValue`, `NativeFunction`,
and `NativeDatum` movement likewise must not be called heap allocation.

## Excluded or already-resolved explanations

- Tier direct handling is present at `NativeInterpreter.Tiering.cs:282-285`.
  The final 194,763 warmed operations moved to direct; no remaining omission is
  asserted here.
- The initial literal-register inflation and old capture-schema bug are repaired.
- Unicode control/final `Copy` execution counts both equal 2,255,730; policy
  both equal 347,826. This evidence does not support a new blanket explosion
  of hot PHI copies or repeated pack/unpack instructions.
- Unicode Native instruction totals are 6,355,934/6,355,874 and tier work is
  unchanged. Equal VM work does not imply equal per-op storage cost.
- The reported 295.225/302.524 MB Unicode diagnostic execute allocations are
  not attributed to the projection structs. The ready projection path itself
  contains no new managed object construction; frame layout/analysis and
  diagnostic observation costs need their own ownership evidence.
- Native code token conversion and code/name leaf projection are cold/absent
  in the observed hot workload. No public signed code-field compatibility
  problem was found in `NativeFunctionParts`.

## Recommendation

Prioritize the nullable-to-exact-address carrier probe for Unicode. It is
grounded in three actual hot sites and existing typed source metadata, and
isolates work that survives the repaired tier-direct path. Do not generalize
the result to policy, which executes no FunctionComponent; do not claim it
explains the measured +3.97% until the focused same-journey comparison exists.
