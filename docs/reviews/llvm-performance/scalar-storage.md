# Independent review: scalar integration and ordinary value/storage paths

## Scope and result

Reviewed the frozen-control brief first, then the Native model, development contracts, observability, Function SSA, SROA and machine-scalar notes and actual storage/runtime/lowering code. No production changes, builds or timing runs were made. The foundation receipt explicitly lists Native machine scalars as remaining work, so this cohort's storage changes are incremental relative to that control, rather than merely relative to an arbitrary historical commit.

The supplied telemetry-off paired results remain authoritative: numeric/policy/Unicode bound changes are +1.71%/+1.70%/+3.97%, source changes +3.66%/+5.01%/+7.69%. The following findings explain additional *work* despite unchanged hot instruction counts; they do not assign milliseconds or percentages to unisolated code paths.

**Main conclusion:** widened initialization maintenance is a concrete, high-frequency incremental expense in ordinary storage, independent of machine arithmetic. Machine-type branches add a second, smaller plausible expense. The new typed root alternatives do **not** enlarge NativeValue/NativeDatum or box ordinary values. No independently established value-layout correctness failure was found in this remit.

## Ranked findings

### 1. Ordinary stores and copies now maintain byte initialization that current ordinary consumers do not observe

**Priority:** first discriminating experiment. **Evidence:** confirmed source-level extra work; marginal runtime impact unmeasured.

Locations:

- `src/Msync.Core/NativeModel/NativeStorage.Machine.cs:19`: `MarkInitialized` makes a checked span and fills every byte's flag.
- `NativeStorage.Data.cs:105,107–111`: ordinary generic writes/clears now fill/clear `max(1, format.Bytes)` flags rather than one.
- `NativeStorage.cs:192–202,205–223`: both same-carrier and carrier-adapting copies now fill a span instead of storing one flag.
- `NativeStorage.cs:340–348,381,390,420`: Number, Path, Function and Address stores now mark 8, 2, 4 and 8 flags respectively. Generic union writes mark the entire physical union extent, potentially including unused payload/alignment bytes.

Actual execution paths are ordinary tier bridge `Load`/`Store` -> `NativeMemory.Copy/TryCopyValue` -> `CopyTrusted/CopyKindTrusted` (`Tiering/NativeInterpreter.Tiering.cs:365–413`), frame argument copies -> the same helpers, generic `Frame.Set` -> `NativeMemory.Write` -> `Data.Write`, and ready math builtins -> `StoreNumber` (`NativeInterpreter.NativeCalls.cs:112–118`). These paths are used even when machine arithmetic counters are zero. `Span.Fill` need not allocate or execute one separate instruction per byte; the concrete delta is broader flag writes and dynamic span work versus one indexed bool store, not a claimed 8x runtime multiplier.

Current consumers:

- `NativeStorage.Data.cs:57–60`: `IsInitialized/RequireInitialized` examine only `initialized[address.Offset.Bytes]`.
- `NativeStorage.Machine.cs:8–12`: even `ReadMachineScalar` checks only the requested view's starting flag, not every byte in its width.
- Raw tier code still writes **one** initialization byte (`NativeMachineCode.Registers.cs:9–14`, and Arm64 equivalent), or elides that store when unobserved. Thus current production paths do not in fact maintain a uniform all-payload-byte initialization invariant.
- Product search finds no caller of the explicit `ReadMachineScalar/WriteMachineScalar` APIs outside their definitions. Machine-typed normal loads/stores instead flow through typed storage views. `LlvmNativeLowerer.Memory.cs:44–49` rejects a Number/i64 representation mismatch; `PointerArithmetic.cs:133–140` rejects GEP offsets inside unrepresented fields. Ordinary Address/Function/Path payload bytes are not independently represented source-language fields.
- `tests/LlvmIr/NativeMachineScalarProperties.cs:67–74` tests Number -0 -> i64 at **offset zero**, then an 8-bit write -> 64-bit read at **offset zero**. Both use only the first flag; the former test's phrase “whole raw double representation” does not actually distinguish one-flag from all-byte marking.

Existing workload evidence gives relevant scale without inventing a Fill count:

| Existing diagnostic | Copy opcode hits | Load hits | Store hits | Main tier copy fallbacks |
|---|---:|---:|---:|---:|
| Unicode final `unicode-tier/after.jsonl` | 2,255,730 | 455,001 | 65,306 | 259,690 |
| Policy literal-cache cohort `.temp/function-perf/final/live-policy-dispatch-candidate.jsonl` | 347,826 | 52,594 | 37,713 | 27,938 |

Unicode also copies 195,000 call arguments across extension/filename/stem. These are exposure counts, **not** 2,255,730 calls to Fill: 3,142,642 Unicode raw-tier operations use generated scalar paths, and scalar/raw copies bypass the changed managed helper. String-only copies still mark just one flag, though via a span; wider Number/Address/Path/Function copies do more flag writes. A precise marginal count needs the selected storage format, not just the opcode name.

**Narrow experiment/remedy:** first restore single-start-flag maintenance only in ordinary source-carrier storage paths, leaving explicit machine-byte-view APIs unchanged, and rerun the already-fixed bound artifacts and output oracles under the existing alternating harness. Establish the storage-view contract before generalizing the change: external direct C# consumers can request an interior `OffsetBy(+k)` machine read, so indiscriminately reverting the explicit raw-memory API would alter its current behavior. If interior byte views become target-supported, model their readiness at that boundary rather than requiring every unrelated Function/Address/Path write to maintain unconsumed interior flags. This is a representation issue, not a request for more initialization checks.

### 2. Machine discrimination now runs on ordinary generic reads/copies and physical/logical type queries

**Priority:** second, isolate separately from initialization. **Evidence:** confirmed extra tests/field reads in source; AOT instruction/code-layout impact and timing not established.

Locations and paths:

- `NativeStorage.Data.cs:69,89` adds a `type.Machine` test to each generic ready read/write. `Frame.GetDatum` -> `NativeMemory.Read[Compiled]` reaches the read branch even for ordinary String/Path/Function carriers.
- `NativeResult.cs:195,215,238` adds source/destination machine checks on non-identical copies, narrowing and conversion. The same-type `TryCopyValue` path returns before its extra machine branch, so it is incorrect to charge that branch to every copy.
- `NativeType.cs:40–44,74` adds machine distinction to admission and equality. Same ordinary-type comparison now compares `Machine` references after `Kinds`; ordinary type objects grow by two reference fields (`Machine`, `OptionalSourceType`), but slot payload sizes do not.
- `NativeValue.Machine.cs:8–11`, `NativeResult.cs:15–17`: ready source `PhysicalType` and `LogicalType` now pass through machine tag checks rather than the former direct `NativeType.Of(value.Kind)`/`value.Type`. `CoerceStore` (`NativeInterpreter.Operators.cs:119–135`) may query PhysicalType several times on nonmatching types. Most ready stores first succeed through `TryCopyReady`, so this slow-path query is not paid for every store.

These changes can affect ordinary traffic with no machine arithmetic. However, source-level branch counting alone is insufficient: AOT can inline/coalesce tests, branch prediction favors the source alternative, and raw Number/Bool tier code does not traverse generic datum reads for each arithmetic operation. Do not present these small branches as a quantified explanation for +4% Unicode.

**Discriminating experiment:** after isolating finding 1, compare the current generic source branches with source-specialized read/write/copy handlers selected once from immutable compiled slot/type facts. This follows the existing Native backend's prelinked handler model; it is not runtime rediscovery of layouts and need not create a second mutable value cache. Prefer a local selected-handler experiment over a broad tagged-value redesign.

### 3. Eager source projections add small metadata work, not a credible explanation for MB-scale execution allocation growth

**Priority:** low, metadata/linking cleanup only. **Evidence:** concrete construction cost, limited demonstrated scale.

`NativeType.cs:13–21` now scans all 11 source kinds on construction and allocates an additional `TypeRef` for a union/Never. `FromKinds` does not intern unions, and range primitives still construct physical unions (`NativeInterpreter.RangePrimitives.cs:25,68,108`). `NativeType.Union:60` additionally scans alternatives for machine types before its existing union pass. The new `NativeType.OptionalSourceType` is consumed only by `NativeResult.Machine.cs:27`, for deferred machine-aware transforms; it is not required to read ordinary Number/String slots.

Some cost is startup/type/range-metadata work rather than a per-opcode tax. The existing `new NativeType` union construction itself is preexisting; the projection scan/additional TypeRef is incremental. The 63 Native types in Unicode final decode cannot alone explain the observed execute-allocation increase of roughly 7.3 MB. Determine actual repeated union creation before changing this code. A source-kind projection directly derived when a deferred result needs it, or a shared immutable projection keyed by the finite source kind mask, would avoid repeated eager work if repeated construction proves material. Do not add a general cache absent that demand.

## Excluded hypotheses and shared costs

1. **No widened root/value struct.** `NativeRoot.cs` remains explicit-layout with all references at offset 0; new MachineType/WideScalar alternatives overlay the existing GC-reference word. `NativeValue.cs:8–14` retains the same bits/flags/five root fields, and `NativeResult.cs:10–13` retains the same NativeValue plus Future fields. Adding methods/partial declarations does not add instance payload. This is source-layout evidence, not a fresh sizeof experiment.
2. **No ordinary scalar boxing.** Number/Bool use the existing inline ulong payload. Machine values <=64 bits also use that payload and a cached descriptor root. `NativeWideScalar` allocation only occurs for wider values (`NativeValue.Machine.cs:17–19`), not ordinary source Number arithmetic or an i64 function code leaf. A zero machine-operation count does not mean zero component projections or descriptor constants, but it excludes an explanation based on millions of wide arithmetic allocations.
3. **No new root count for ordinary storage.** `NativeRepresentation.Of` (`NativeType.cs:240–251`) still specifies Number 8B/0 roots, Bool 1B/0 roots, String 0B/1 root, Path 2B/5 roots, Function 4B/2 roots and Address 8B/2 roots. Initialization arrays were already byte-sized to RawSize; the new fill changes traffic, not their allocated length.
4. **Ordinary constant conversion is unchanged.** `NativeImage.Constant` adds a separate machine case; ordinary Number/Bool/String conversion stays on its existing cases. Constants are retained by the image; no evidence here of per-hot-read Number constant object allocation. Repeated Function leaf constant materialization is already repaired and is not reopened.
5. **Generic NativeValue copying is largely shared cost.** The five-root transient carrier was already present. FunctionComponent choosing `GetDatum` rather than the old direct environment-address path introduces additional *uses* of this existing carrier, not a new expanded carrier; its distinct marginal work belongs to the Function projection review.
6. **Register-plan empty-region work is separate.** Both images pay the live-after/pending analysis despite zero regions; the graph can enlarge that shared preparation expense, but it is not caused by NativeRoot size or per-value machine allocation.

## Recommended next step

Use finding 1 as the first isolated runtime experiment, preserving explicit raw-view behavior and using the current bound artifact/input pairs. It has a concrete changed operation, ordinary high-frequency callers and consumers that explain why the extra work is not needed on the measured path. Retain current telemetry; only after that experiment should generic machine discrimination be specialized. None of these findings removes the separately observed source-compilation SROA expense or establishes a complete attribution of the paired deltas.

## Remediation handoff: separate compiled readiness entrypoints

The reviewed full-byte initialization work now has a bounded implementation:
internal compiled ordinary-source writes/copies/clears maintain only the declared
start flag, while public raw-view APIs and machine extents retain their existing
behavior. Payload/root routines are shared. Exact-address writes and the three
ready Number writer sites use the internal policy; scalar returns likewise mark
only their declared start. No consumer scan or global analysis selects this path.
See the ordinary/public readiness contract in
[the machine scalar ABI note](../../design/llvm-machine-scalar-abi.md).

Root's serialized execution passed 57 ordinary initialization properties and
four public Compiler O0/O2 direct/Native2 source journeys, including the raw-view
compatibility and pending cases. The result is retained in
`artifacts/diagnostics/llvm-instcombine/ordinary-init.log`. Actual-operation
telemetry is published with the implementation. These focused results do not
claim a measured speedup: the existing paired application comparison remains
separate, and the supplied regression results above are not superseded by
correctness properties.
