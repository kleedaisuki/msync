# Tier dispatch, call selection, and suspension review

## Scope and verdict

Reviewed current source and the frozen observations specified in [shared context](../../design/llvm-performance-regression-review.md); no production edits, builds, timing runs, or subagents. Function-component representation/body ABI costs are intentionally left to the separate reviewer.

**No remaining dispatch-selection regression is established.** The strongest actionable issue in this remit is eagerly running register-planning analyses even though the compiled raw-write contract makes every region ineligible. This is shared waste with a larger observed candidate cost, not a quantified explanation of the entire slowdown. The existing classifications, guest handoffs, hot native counts, and helper return mechanisms otherwise agree across cohorts.

Primary latency evidence remains the telemetry-OFF alternating-pair results: Unicode bound +3.97%, policy bound +1.70%. Instruction-recording observations below explain structure; their durations are not new paired latency measurements.

## Ranked findings

### 1. High-value correction: do not perform whole-body register analysis when no instruction can enter a register region

**Location:** `src/Msync.Runtime/NativeModel/Tiering/NativeTierLayout.cs:42`, `NativeRegisterPlan.cs:44-76,140-184`, `src/Msync.Core/NativeModel/NativeStandardBackendEmitter.cs:48`.

`NativeTierLayout.Create` unconditionally invokes `NativeRegisterPlan.Create`. That method constructs control flow, computes fixed-point `LiveAfter` with per-PC hash sets, and propagates `PendingSlots` before testing `Eligible`. Eligibility requires `function.CanWriteRaw(result)`; the current standard backend publishes an all-false `RawWritable` array. Accordingly these bodies can never create a region, regardless of liveness or pending propagation results. The analyses have no consumer on this execution path.

| Instruction-recording observation | Control | Candidate |
|---|---:|---:|
| Unicode register-allocation span | 23.467 ms | 47.048 ms |
| Unicode regions / values / spills | 0 / 0 / 0 | 0 / 0 / 0 |
| Policy register-allocation span | 31.399 ms | 37.619 ms |
| Policy regions / values / spills | 0 / 0 / 0 | 0 / 0 / 0 |
| Unicode execute allocated bytes | 295,224,704 | 302,524,368 |
| Policy execute allocated bytes | 62,334,384 | 66,590,368 |

**Strength:** wasted algorithmic work is proven by the contract and source. Larger candidate analysis time is directly observed, but these single profiled launches do not prove a 23.581 ms / 6.220 ms telemetry-OFF contribution or attribute the allocation delta to this analysis. The interval also includes layout work outside the liveness solver.

**Narrow correction/experiment:** first scan actual executable starts with the existing `Eligible` predicate and return an empty region array if none is eligible, before constructing control flow. Do not weaken the raw-write contract or invent raw-write proofs. Keep a lean, recording-disabled path; record the existing phase's zero-candidate decision and skipped work when recording is enabled. Root can run one same-artifact Unicode/policy end-to-end comparison to isolate this correction. This preserves machine selection, instruction semantics, and the current artifact contract.

### 2. Lower-priority shared opportunity, not a regression fix: pure native wrappers still use the guest loop

**Location:** `NativeStandardBackendEmitter.cs:26-30`; `src/Msync.Runtime/NativeModel/NativeInterpreter.Aggregates.cs:127-144`; `NativeInterpreter.Calls.cs:47-80,116-128,175-180`.

Every `CallNative` marks a body neither pure-synchronous nor context-free. The Unicode stdlib `extension`, `filename`, and `stem` wrappers each contain exactly five instructions, including one `CallNative`. Therefore their callers cannot use `RunDirectFunction`: they yield `activation.Call`, push a guest invocation, enter the helper, finish it, release it, and resume the destination slot. This happens **195,000 times in both cohorts**. Each helper also copies one parameter and makes one one-element native-argument array through `CallNative` (`NativeInterpreter.Aggregates.cs:150-160`).

This is genuine repeated work, but it did not increase in the candidate. Do not label these 195k handoffs a newly lost optimization, and do not classify all `CallNative` as synchronous: other natives own real work, context, and incomplete operations. Signature effect labels alone are not a proof that an operation cannot suspend.

**Recommendation:** do not change classification to explain this regression. If pursued later as a separate optimization, start from a compiler-owned native behavior contract that distinguishes synchronous completion, work ownership, and directory dependence, then prove one wrapper family end-to-end. Established LLVM function inlining is a possible longer-term compiler route rather than a handwritten wrapper bypass; it is outside the narrow current fix.

## Dispatch and execution evidence

### Unicode: repaired projection routing now matches control

Source observations: `artifacts/diagnostics/llvm-sroa/unicode-tier/{baseline,before,after}.jsonl`, `execute` and `vm.function` records.

| Metric | Baseline | Before repair | Final |
|---|---:|---:|---:|
| Tier operations | 5,373,192 | 5,373,192 | 5,373,192 |
| Raw/direct machine operations | 3,142,642 | 3,142,642 | 3,142,642 |
| Bridge direct operations | 1,646,249 | 1,451,486 | 1,646,249 |
| Bridge fallback operations | 584,301 | 779,064 | 584,301 |
| Tier entries | 259,429 | 259,425 | 259,588 |
| Tier suspensions | 64,665 | 64,661 | 64,824 |
| Main workspace leases | 1 | 1 | 1 |
| Main workspace reuses | 259,428 | 259,424 | 259,587 |
| Guest call pushes / resumes | 195,000 / 195,000 | same | same |

`NativeInterpreter.Tiering.cs:282-286` now routes `FunctionComponent` directly and retains its helper's pending behavior. The repaired direct/fallback difference is exactly 194,763 operations. There are 195,000 ready projections overall, zero pending projections; some execute before tier promotion. No readiness exits, guard misses, recompilations, or deoptimizations occur in these observations.

Each of functions 92/93/94 has 65,000 calls and 325,000 executed instructions on both sides. Their exported bodies are identical, including `FrameAddress`, two `Copy` operations, native call, and return. Each reports 64,999 frame-storage and activation reuses. Each helper's aggregate stack bytes/roots are unchanged. Main stack storage shrinks from 6,848 bytes / 1,805 roots to 5,664 / 1,411, not growth.

Hot native work is also unchanged: `fs.extension`, `fs.filename`, `fs.stem`, `string.is_ascii`, `string.length`, `string.starts_with`, `string.to_lowercase`, and `string.utf8_size` each execute 65,000 times; `io.stdin_lines` once and `io.println` six times.

The 159 extra final tier entries exactly accompany 159 extra suspensions, not 195k additional projection escapes. `CompleteTierDemand` increments tier suspensions for `AwaitExit` (`NativeInterpreter.Tiering.cs:112-123`), whereas `DemandStep` records an actual pause only when its completion is still incomplete (`NativeInterpreter.Demand.cs:16-28`). Main dispatch pauses vary 59,794 → 59,973 → 63,350. Thus these counters observe timing-dependent completion states and cannot by themselves prove additional deterministic scheduling work.

### Policy: hot direct/leaf selection and scalar return remain intact

Source observations: `.temp/function-perf/final/live-policy-dispatch-{baseline,candidate}.jsonl`; candidate is the literal-cache cohort, not the later Unicode-only tier repair.

Both sides execute 1,088,487 tier operations: 803,950 machine-direct, 179,841 bridge-direct, 92,442 fallback. Main makes 27,752 direct guest calls, all 27,752 counted as context-free leaf calls; zero pushes, resumes, stack-limited calls, or guest-loop handoffs. `ranges.at` also executes exactly 27,752 times.

| Helper | Calls, both | Instructions, both | Return / workspace behavior, both |
|---|---:|---:|---|
| 98 | 15,436 | 61,744 | 15,436 direct slot returns; no tier lease |
| 99 | 9,157 | 91,570 | 31 slot + 9,126 scalar returns; 9,126 workspace leases |
| 100 | 3,159 | 15,795 | 31 slot + 3,128 scalar returns; 3,128 workspace leases |

The local `tier = null` in `RunDirectFunction` (`NativeInterpreter.Calls.cs:145-166`) causes one pool lease per tiered direct invocation. These **12,254 leases are identical** across cohorts; they are not heap allocations by definition. Main leases once and reuses 27,324 / 27,357 times. Extra 33 entries match extra 33 suspensions. Main storage again shrinks: 6,776 bytes / 1,778 roots → 5,328 / 1,286. Candidate has no hot `FunctionComponent` executions here. Projecting Function environments cannot directly explain this policy loop's differential.

## Explicitly excluded hypotheses

- **Current lost FunctionComponent bridge fast path:** repaired; final bridge counts match control exactly.
- **Machine arithmetic dropped out of tiering:** raw and total tier work unchanged in both workloads; zero guard/deoptimization churn.
- **Direct policy calls became interpreter-loop calls:** counters and scalar-return counts refute this.
- **New per-call frame/activation allocation:** helper reuse counts unchanged; main frame shrinks. Profile allocation totals do not prove object-level attribution.
- **Full flush-entry scanning explains telemetry-OFF regression:** `NativeTierMemory.cs:285-292` returns before `FlushEntries` when unobserved. Hit clearing at `NativeTierMemory.cs:113` is also observed-only. Instrumented flush overhead can alter diagnostic run duration, not the reported telemetry-OFF paired latency by this mechanism.
- **Tier workspace explosion:** same leases; reuses track suspensions. Pool activity remains a common cost, not a new differential.
- **Native workload changed:** hot native counts, helper instructions, guest handoffs, and output oracles agree.

## Next action

Prioritize the no-eligible-region register-analysis skip, preserving all semantics and telemetry decisions. Do not pursue a broad dispatcher rewrite or relax purity/context classification on these observations. Remaining runtime attribution should focus on changed main-body helper costs and startup analysis rather than opcode-count reductions; Function-component ABI movement is owned by the parallel review.
