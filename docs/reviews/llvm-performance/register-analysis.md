# Runtime register planning review

## Scope and result

Reviewed `NativeRegisterPlan`, `NativeTierLayout`, `NativeCompiledFunction`, shared
`NativeInstructionFacts`, and the actual standard-backend/Function-leaf producer.
Read [shared context](../../design/llvm-performance-regression-review.md) first, including the frozen binary hashes, final paired changes
(bound +1.71/+1.70/+3.97%; source +3.66/+5.01/+7.69%), compile timings, allocation
observations, and repaired causes. No product edits, builds or wall-time benchmarks.

**The expensive no-op register planner is confirmed shared waste. The candidate's
specific amplification is also established structurally: hoisted Function code/name
leaf constants increase live-set cardinality, despite reducing instructions and
worklist iterations. This is not extra loop convergence or a new opcode-facts bug.**

## 1. High priority: skip register analyses when there is no eligible region

Locations: `src/Msync.Runtime/NativeModel/Tiering/NativeRegisterPlan.cs:44–55,72–76`,
`NativeTierLayout.cs:40`, `NativeCompiledFunction.cs:25–27`;
`src/Msync.Core/NativeModel/NativeStandardBackendEmitter.cs:48`;
`NativeExecutionImage.cs:98,154–155`.

Actual path: a hot function passes Auto profitability, enters `NativeTierLayout.Create`,
then `NativeRegisterPlan.Create` constructs per-instruction CFG and computes
whole-function `LiveAfter` and `PendingSlots`. Only afterwards does `Eligible` inspect
raw-write capability. The standard backend persists a fresh all-false `RawWritable`
array. Consequently **none of those liveness/pending answers can be consumed**:
all region candidates fail `CanWriteRaw`, and `Create` returns an empty array.
Ordinary direct tier paths still work, so zero regions does not mean no tier code.

The recorded Unicode runs each compile only `<main>` successfully, with zero
regions/values/spills. Their `tier.register_allocation_ns` is 23.4669/47.0480ms;
policy's earlier literal-cache cohort reports 31.399/37.619ms, also zero regions.
These measurements cover **all of NativeTierLayout.Create**, not only liveness,
and are single instrumented observations, not paired telemetry-off causal timings.

Correction: first scan actual instruction heads for any `Eligible` operation; return
empty if none exist, before building CFG. This exactly gates the consumer rather
than adding a new raw-write/alias solver or re-enabling an old backend. A lazy first-
eligible initialization is another equivalent implementation. Preserve existing
raw-write contracts; do not mark slots writable merely to make the analysis useful.
For a genuinely eligible future function, retain existing liveness/pending behavior.

Narrow validation: verify the same Unicode/policy detached-artifact outputs, direct
and bridge counts, and zero region counts; separately observe one diagnostic
layout span with analysis-skipped counters. Root can then use the existing paired
telemetry-off route once. Do not interpret the entire 47ms as the recoverable amount.

## 2. High priority causal finding: Function-leaf prelude lifetimes amplify HashSet work

Locations: `NativeRegisterPlan.cs:164–180`, especially successor union at175 and
live-set cloning at177; `src/Msync.Hir/Lowering/LlvmNativeLowerer.AggregateSlots.cs:70–101`
(`AggregateComponentSlot`/`ConstantSlotValue`), and `LlvmNativeLowerer.cs:171–187`
(prepending the prelude).

The cached leaf representation materializes code tokens (`MachineInteger`), native
names (`String`), and null carriers into registers in a shared entry prelude.
`ComposeFunction.Inputs` correctly reads those slots at their later construction
sites. By contrast, the previous `MakeFunction` encodes code/name as instruction
metadata, not as live leaf slot operands. Many new leaf slots therefore remain live
through large stretches of straight-line stdlib initialization. One shared slot
removes repeated definitions/slots but spans the interval from its single dominating
prelude definition to its last use; deduplication does not imply a shorter lifetime.
Even a name used once has a long interval when hoisted to entry.

Deterministic replay of the actual `LiveAfter` queue order and transfer equations:

| `<main>` metric | Unicode control | Unicode candidate | Policy control | Policy candidate |
| --- | ---: | ---: | ---: | ---: |
| Metadata instructions | 1370 | 1310 | 1324 | 1210 |
| Slots | 1026 | 966 | 975 | 861 |
| CFG edges | 1376 | 1316 | 1330 | 1216 |
| Worklist pops | 1501 | 1441 | 1394 | 1280 |
| Successor set elements supplied to UnionWith | 54,225 | 161,537 | 87,503 | 159,361 |
| Elements supplied to live-set copy constructor | 53,977 | 161,289 | 87,279 | 159,084 |
| Final live slot/PC incidences | 51,961 | 159,273 | 86,713 | 158,518 |
| Peak live set size | 72 | 248 | 135 | 249 |

Thus Unicode union/copy element counts grow about3x, policy about1.82x, while both
worklists shrink. `HashSet` unions/copies process entries, not merely instructions;
this is a demonstrated increase in actual algorithmic work, consistent with the
recorded layout-time increase. It is **not** a measurement of exclusive wall time
or an allocation-byte estimate. A compiler/runtime optimization that reduces dynamic
Nativeinstructions need not reduce preparation graph work.

### Exact members and where they live

Unicode baseline peak is after pc73:54 `AddressOffset`,7 `MakeFunction`,7 Number,
1 Null,1 Bool,1 Allocate and1 slot without a direct Result definition.
Candidate peak is after pc254:89 String constants,86 MachineInteger constants,
3 Null,54 AddressOffset,6 ComposeFunction,7 Number,1 Bool,1 Allocate and1 slot
without a direct Result definition. This is immediately before the sequence of
Function compositions, not the hot loop. Examples:

| Candidate slot | Definition PC | Last read PC | Value |
| --- | ---: | ---: | --- |
| 74 | 0 | 1163 | shared zero code token |
| 77 | 3 | 256 | `string.contains` |
| 80 | 4 | 259 | `string.starts_with` |
| 83 | 5 | 262 | `string.ends_with` |
| 86 | 6 | 265 | `string.concat` |

Across all final per-PC live sets, Unicode MachineInteger leaves account for50,445
slot/PC incidences and String constants63,872 (old String only515). Policy's peak
at pc520 contains62 MachineInteger+50 String constants; old peak pc402 had neither
category. Policy final MachineInteger/String incidences are54,956/30,470 versus0/0.
These groups explain the added set occupancy; AddressOffset occupancy actually
falls (Unicode32,392→26,381; policy58,503→47,782).

### Loop hypothesis excluded

The live sets on every backward CFG edge have the **same size and category counts**
in control/candidate. Unicode's edge-target counts are23,7,26; policy17,4,17.
Unicode's23-member outer-loop entry contains6 Number,6 Copy,1 Null,1 FrameAddress,
3 AddressOffset,5 String,1 Bool. Its26-member inner entry adds2 Convert and1 Copy;
the7-member target has6 Copy+1 Null. Policy's17-member entries contain10 Number,
2 Copy,2 Allocate,1 MakeRange,1 FrameAddress,1 Null; the4-member target contains
2 Copy+1 Allocate+1 Null. **No added code-token/name leaf population survives on
these loop backedges.** The expensive growth is in the initialization prefix,
not a loop trip-count or iteration-order amplification.

Recommendation: fix finding1 first. That removes the entire exposed amplification
without undoing the repaired exact constant reuse. Do not locally de-cache constants
or invent rematerialization heuristics to cure an analysis with no consumer.
Only if register-eligible workloads later justify it should root consider an
upstream-derived liveness representation/placement change.

## Pending analysis: shared additional cost, incremental growth not established

`NativeRegisterPlan.cs:138–157` makes full forward sweeps to a boolean fixpoint.
At149–150, `with` clones a **class record** (`NativeCode.cs:82`), once per instruction
head per sweep; this is a real allocation, unlike merely moving NativeDatum structs.
The LINQ predicate at152 captures `pending`, and shared facts use iterator methods.
All of that is wasted when no region can consume `PendingOutputs` (line132).

However, no existing telemetry isolates PendingSlots sweeps. Fewer metadata
instructions imply fewer clones per equal number of sweeps; I have **not** proven
that the new representation increases pending sweeps. Do not attribute the extra
7.3MB Unicode execution allocation, or the doubled layout observation, to these
record clones specifically. Gating the unused analysis handles this cost as well.

## Other inspected paths and limits

- `NativeCompiledFunction`'s profitability rejection precedes layout, so the
  unavailable compilations do not themselves establish repeated LiveAfter work.
- The register-allocation metric includes scalar slot collection, frame-slot
  classification and layout map/flush construction. `NativeTierLayout` constructs
  both a provisional and final layout, duplicating derived arrays/maps; this is
  existing shared work, with no independent evidence of candidate amplification.
- `NativeInstructionFacts.Reads/Definitions` are shared iterator implementations.
  The small diff removes obsolete range opcodes; it does not change read semantics
  for the opcodes in these artifacts. ComposeFunction's extended-input reads are
  necessary true data dependencies, not accidental liveness over-reading.
- Sparse sets, block liveness, region-only queries or backwards scanning are possible
  broader directions, but there is no reason to redesign the solver for all-false
  raw-write functions. Current upstream [RegAllocFast source](https://llvm.org/doxygen/RegAllocFast_8cpp_source.html)
  describes block-local backward allocation with bounded def/use scans and no
  dataflow liveness. This supports consumer/workload-shaped analysis, not a claim
  that msync should adopt its machine ABI. Current [LiveVariables header](https://llvm.org/docs/doxygen/LiveVariables_8h_source.html)
  explicitly marks that analysis defunct; do not blindly port an older tutorial's
  LiveVariables implementation. Neither page is the project's pinned source port.

## Reproduction artifacts

- `register-structural-probe.py`: fully typed Python infrastructure; run from repo
  root. It reads four already-preserved artifact JSONs, replays `LiveAfter` using
  sets/queue in the same enqueue order, and emits deterministic counts, peak member
  definitions/constants, backward-edge members, and input SHA-256 identities.
- `register-structural-probe.json`: complete output including exact peak slots and
  backward-edge constant values. No timings are measured.
- Input JSONs are `.temp/function-perf/final/{unicode-path-manifest,live-policy-dispatch}-{baseline,candidate}-artifact.json`.
  Candidate is the final literal-cache cohort; the later tier projection repair
  changes handler execution rather than these metadata graphs. JSONs are
  relocatable metadata views; runtime LiveAfter consumes function Metadata, not the
  linked execution instructions. The standard backend uses span1. The probe includes
  all opcodes in these four `<main>` functions and no approximate alias solver.
  Its origin label `no-direct-result-definition` is intentionally not a claim that
  such a slot is a function parameter; indirect frame/storage slots also exist.
