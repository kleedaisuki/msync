# LLVM-to-Native performance regression: parallel review

## Scope and measured context

Eight independent reviewers examined the current source and retained frozen
artifacts/profiles. No production code, builds or wall-time observations changed
in this review. The complete common brief, benchmark identities, raw results,
individual findings and the typed structural probe are retained in
[versioned review bundle](../reviews/llvm-performance/README.md).

Control AOT: `aaf100d5999642f8566761e86c37b31ed0d71d4f8cf87e5e36f3cb845f175ff6`.
Current AOT: `3c821875c8c954db6f2e1c70c7eb47bc69228e32ad0c572c1453b58f8dceb410`.
Final standard/29 five-pair bound changes are +1.71% numeric, +1.70% policy,
+3.97% Unicode; source changes +3.66%, +5.01%, +7.69%. Fresh O2 compilation
nine-pair median deltas are +0.370ms numeric and +4.738ms midend. Positive means
more elapsed time. These compare a producer/runtime cohort, not SROA in isolation.

The initial repeated-constant bug, PHI raw-copy mismatch, template capture-schema
mismatch and missing FunctionComponent tier-direct branch are already fixed.
They must not be recycled as unfixed explanations of the current percentages.

## Primary causal structure

```text
Function code/name become SSA leaf constants defined in the entry prelude
    -> long straight-line live intervals (not larger loop-backedge live sets)
    -> substantially more HashSet union/copy element traffic in runtime LiveAfter
    -> the entire register-region analysis still has zero eligible consumers
```

The current standard backend persists all-false RawWritable. The tier planner
nevertheless constructs CFG, whole-function liveness and pending propagation
before testing whether any instruction can form a register region. Every
relevant profile reports zero regions, values and spills. This is confirmed
wasted runtime preparation, independently of the measured regression magnitude.

The structural probe replays the existing native liveness equations and queue
order over preserved rendered artifacts, with source hashes in its JSON. It
finds:

| Main-body work | Unicode control/current | Policy control/current |
| --- | ---: | ---: |
| Instructions | 1370 / 1310 | 1324 / 1210 |
| Worklist pops | 1501 / 1441 | 1394 / 1280 |
| Successor-union elements | 54,225 / 161,537 | 87,503 / 159,361 |
| Live-set clone elements | 53,977 / 161,289 | 87,279 / 159,084 |
| Peak live set | 72 / 248 | 135 / 249 |

At the Unicode candidate peak, 89 native-name String constants and 86 machine
code-token constants are live together. Backedge live sets have identical sizes
and kinds across control/current. Thus fewer instructions and fewer queue visits
coexist with about3x Unicode /1.82x policy set-element work. This explains the
algorithmic amplification, not an exact share of the off-telemetry milliseconds.
The profiled tier-layout observations (Unicode23.467/47.048ms, policy31.399/37.619ms)
include more than liveness and must not be treated as its exclusive timing.

**First action:** demand the existing Eligible condition before constructing the
analyses; return an empty register-region plan when there is no consumer. Do not
undo literal reuse, enable unsafe raw writes, or replace the solver to solve an
analysis that is not needed. PendingSlots' per-instruction class-record clones
also disappear on this path. No new optimization heuristic is required.

## Other concrete findings, ordered by likely value

1. **Lost exact environment carrier at hot projection.** The Unicode image has
   three environment projections, 65,000 executions each. Source lowering already
   annotates the concrete capture-environment type, but aggregate extraction
   aliases it to the canonical Null|Scope leaf. The old exact Scope address uses
   SetPointer/LoadAddressDirect; the new path uses generic Set and then the next
   AddressOffset calls Pointer -> GetDatum/AsAddress because ExactAddress is
   false. Both sides of the boundary gain work. Preserve existing producer ABI
   information rather than inventing nonnull/alias analysis. Nullable PHIs and
   pending semantics still need their actual encoding. Its marginal time is not
   isolated, and policy executes no FunctionComponent, so it cannot explain
   policy's regression.
2. **Widened initialization bookkeeping on ordinary storage.** Ordinary typed
   writes/copies now mark/clear payload-wide initialized spans; ordinary reads
   check only their starting flag, and generated scalar tier writes still set
   only that flag. Explicit raw interior-view APIs need their own contract, but
   the represented source Number/Address/Function/Path traffic is paying work
   for interior byte flags it does not consume. There is no ordinary scalar
   boxing or enlarged NativeValue/NativeDatum struct established. Most raw-tier
   arithmetic bypasses the managed helper, so opcode totals are not Fill counts.
3. **SROA container subtraction differs from pinned SetVector.** OrderedSet's
   ExceptWith scans all survivors, whereas upstream drives membership removal
   from the deleted keys. Absent deleted originals consequently rescan growing
   worklists. The review also finds290 singleton presplit HashSets for290
   unsplittable slices. These are bounded translation/allocation issues, not
   evidence of runaway speculation. One13.167ms alloca observation contains only
   0.046ms of direct child spans; the uninstrumented interval cannot be assigned
   to partition rewriting or extrapolated to recurring SROA cost.
4. **Small shared analysis-lifetime debt.** InstSimplify/ADCE/DSE can reuse the
   existing forward dominator tree and its already-implemented update API,
   avoiding33 constructions. Observed DSE rebuilds total only0.3025ms: do not
   prioritize this over the zero-consumer analysis or hot carrier loss.

## Excluded and limited explanations

- Forward/postdominator build count132 and MemorySSA build count71 are unchanged.
  Memory Def/Use totals are identical;191 extra scalar instructions are scanned.
  There is no new MemorySSA/AA graph explosion in these receipts.
- Callback frame shapes, parameter copies, frame/activation reuse and snapshots
  are unchanged; main frames actually shrink. Environment wrappers are constructed
  at most180/164 times, not once per record. No new capture retention defect was
  found. The diagnostic allocation increases7.300MB/4.256MB are real process
  allocations but not attributed byte-for-byte to one subsystem.
- Final Unicode bridge counts match the control. Direct/leaf policy calls,
  scalar returns and workspace reuse match. Large flush scans are recording-only;
  they cannot explain telemetry-off timing.
- Bound Executable input skips contextual NativePreparation; it still pays fresh
  runtime initialization and first-tier compilation each process. External process
  warmups do not preserve those in-process caches.
- Source and bound medians must not be subtracted. The existing comparison uses
  per-producer artifacts, so it does not independently isolate runtime changes.
  The runner does not record an inherited MSYNC_JIT value. A later discriminating
  runtime experiment should explicitly set that policy and use a balanced2x2
  artifact/runtime crossover, rather than grow a broad timing matrix.

## Review artifacts

All reports below are versioned under `docs/reviews/llvm-performance/`:

| Reviewer remit | Report |
| --- | --- |
| Runtime register/liveness work | `register-analysis.md` |
| Function projection and ABI | `function-projection.md` |
| Ordinary scalar/storage paths | `scalar-storage.md` |
| Frames, allocations and retention | `frames-gc.md` |
| Tier dispatch and scheduling | `tier-dispatch.md` |
| SROA source fidelity/cost | `sroa-cost.md` |
| Compiler analysis ownership | `pipeline-analyses.md` |
| Benchmark interpretation | `benchmark-evidence.md` |

`scripts/compiler/analyze_native_liveness.py` replays the structural comparison;
`docs/reviews/llvm-performance/results/register-structural-probe.json` preserves
input hashes, peak members and unchanged loop-backedge inventories. The next
implementation tranche should address the top findings, with the existing
end-to-end artifacts/oracles and one matched timing comparison, not new safety
machinery or timing gates. This review itself has made no performance fix.

## Implementation follow-up

The follow-up implements the reviewed hot-path findings rather than adding a
new alias, lifetime, or heap solver:

- Register planning tests the existing eligibility predicate before constructing
  CFG/liveness/pending data. Actual no-consumer functions now report zero work
  for those phases; eligible functions keep their existing allocation path.
- Function environment extraction uses the producer/per-use carrier at its
  definition. Exact Scope results use the existing direct pointer write;
  nullable results and pending data keep their ordinary representations.
- Compiled source locations update readiness at the declared start, while the
  public raw-view and machine-carrier APIs preserve their existing byte-extent
  behavior. Payload/root writes are shared, not duplicated.
- SROA removes deleted keys membership-first, stores the first presplit slice
  inline, and avoids constructing disabled-recording abort strings. Critical
  edge handling skips lifetime markers at the upstream-prescribed point.
- The compiler reuses the maintained forward dominator tree after SROA and
  through ADCE/DSE cleanup. Real InstCombine replaces the standalone
  InstSimplify slot, using its worklist, visitors, and actual analysis context.

InstCombine exposed two target-selection omissions on real source programs:
scalarized Function stores needed packing to the existing compact Native ABI,
and byte/zero-GEP rewrites needed the actual producer storage declaration,
not an abstract Scope row contract, preserved for compact Native selection. Neither change adds runtime checks,
new Native opcodes, capture semantics, or a backend dependency on LLVM.

The first Native AOT candidate (`6573eea8…`) was not accepted as a performance
result. Its polygon bound route regressed by a paired median 14.13%, while fresh
source processes intermittently failed on an undeclared formal Scope layout.
The diagnostic retained practically identical dynamic instruction counts
(6,486,814 versus 6,486,823), but 196,608 floating comparisons left the direct
native path for generic MachineCompare execution. This is an instruction
selection gap for canonicalized IEEE predicates, not extra Function projection
traffic: compositions were 32,776 versus 32,777. The follow-up implements all
16 binary64 predicates directly, preserving unordered/NaN behavior and the
existing MachineCompare encoding. See `llvm-machine-execution.md`.

Focused source, Native2, and AOT receipts are recorded under
`artifacts/diagnostics/llvm-instcombine/`; matched timing must refer to the final
payload, not substitute these functional runs or the rejected candidate's
partial timings. The final comparison and portable receipts follow below.

The complete pre-coalescing matrix (`4f00d116…`) retained a second instruction
selection regression: Signal bound +8.40%, Unicode bound +15.07%. Matched
recording runs established the mechanism, not merely an inference from time:

| Dynamic work | Signal baseline / candidate | Unicode baseline / candidate |
| --- | ---: | ---: |
| Function compositions | 65,552 / 262,164 | 180 / 195,180 |
| Function projections | 196,612 / 589,836 | 195,000 / 585,000 |
| Instructions | 12,737,430 / 14,113,718 | 6,420,873 / 7,415,499 |

InstCombine's source aggregate-load expansion is appropriate LLVM IR. The
compact Native target was loading the same whole descriptor independently for
each scalar field and then recomposing it. Signal gained exactly 196,612
compositions and twice that many projections; Unicode gained 195,000 and
390,000 respectively. Both recording runs produced the independent expected
output. They are work-count diagnostics, not telemetry-off timing samples.
The existing exact-address projection path was actually selected 195,000 times
in Unicode, and no-consumer register CFG/liveness work was zero; those fixes
did not explain or cancel the new target reassembly cost. The target load-group
selection follow-up is scoped to this concrete regression.

## Final integrated result

Final linux-x64 Native AOT payload: `ba275ad35b0c65b2ed9c6d22bf7f15608b77abca759908228d5377760c4b0324`.
Control: `3c821875c8c954db6f2e1c70c7eb47bc69228e32ad0c572c1453b58f8dceb410`.
The source/target code is frozen for these measurements. `MSYNC_JIT=auto`,
telemetry off, standard scale, seed 29, alternating order, three retained pairs
after one warmup per image/route. Each producer supplies its own O2 object and
bound artifact. Every execution checks independent expected output/files.

Final paired median percentage changes (negative is faster):

| Application | Bound run | Object run | Source run |
| --- | ---: | ---: | ---: |
| polygon-queries | -7.46% | -2.94% | +2.03% |
| signal-hysteresis | -4.76% | -6.53% | -6.65% |
| numeric-record-stream | -5.18% | +3.14% | +2.42% |
| live-policy-dispatch | -5.83% | -4.07% | -8.66% |
| unicode-path-manifest | +3.05% | -6.01% | +2.34% |
| local-manifest-copy | -16.85% | -11.12% | +21.56% |
| local-range-copy | +6.87% | -5.31% | +28.10% |

This is not a universal speedup claim. The extra scalar Function load/reassembly
traffic is gone: final AOT Signal has 65,552 compositions / 196,612 projections;
Unicode has 180 / 195,000. Unicode still selects 195,000 exact-address
projections and performs zero register CFG/liveness work. The final recording
runs match their independent output. Residual timing differences, including
Unicode/local-range bound increases, remain visible in the table rather than
being hidden by a blended score. A local-range diagnostic found no new machine
operation fallback or Function reconstruction traffic; its instruction count is
2,661 versus 2,805 and its one recording run was faster, which does not identify
a stable causal explanation for the telemetry-off bound difference.

The new real InstCombine pass also has a real compilation cost. Nine fresh
process pairs with telemetry off give:

| Compile-only source | Control p50 | Final p50 | Median paired delta |
| --- | ---: | ---: | ---: |
| numeric | 37.16 ms | 38.00 ms | +1.28 ms |
| midend | 97.05 ms | 138.20 ms | +37.39 ms |

Source-copy routes therefore retain meaningful regressions: adding a full
optimization pass is not free on short programs. One separately recorded
midend compile has 75.37 ms inclusive InstCombine time with recording enabled;
that is a phase diagnostic, not the telemetry-off incremental cost above. No
source/bound median subtraction is used to invent compilation, binding, JIT or
execution stage times. Preparation's single compile/bind observations in the
corpus report are not presented as paired compiler statistics.

Portable evidence is in `docs/reviews/llvm-performance/results/instcombine/`:
`benchmark-final.json`, both `compile-*-final.json`, `aot-receipt.json`,
`range-aot.json`, `focused-executions.json`, and `final-aot-work-counts.json`.
The earlier rejected/pre-coalescing results are retained under explicit names.
The final AOT midend journey covers O0/O2 source, detached HIR/object and bound
execution; the Range journey covers six routes and 33 files per route, including
four generated indices `0123`. LLVM/native opcode contracts and Native2 compact
revision 4 remain unchanged. There is no CI timing threshold or new safety gate.

The subsequent [O2 compile-cost inquiry](../reviews/llvm-performance/o2-compile-cost.md)
separates pass time from recorder overhead and uses a diagnostic-only Native AOT
four-cell ablation. It identifies the extra DSE work induced by aggregate-store
expansion as the majority of the measured compiler increment, rather than
attributing the whole increment to InstCombine itself. Product policy is not
changed by that diagnostic experiment.

## Folded pointer-load physical storage follow-up

The address-cost repair exposed a production target-lowering failure during a
fresh compile of the same midend source: `MemoryStorageType` reached
`StorageFromRoot` and tried to select a native carrier from opaque LLVM
`PointerTyID`. This is a target-layout selection path, not a DSE decision.

A folded zero GEP already retains its physical storage declaration on
`LlvmUse.TargetStorageType`. Address emission consumed this declaration, but
storage-type selection ran first and ignored it. An abstract Scope base has no
unique physical layout, and an optimization-created pointer load need not carry
a source `LogicalType`; deriving its representation from either value discards
the declaration that is already present. Storage selection now uses the same
existing offset-zero `StorageField` lookup as address emission. The load's
selected result carrier follows that resolved physical field. No generic
pointer inference, extra analysis, fallback or opcode is introduced.

`addresses.memory_storage.*` records the actual selection path (pointer ABI,
retained physical use, GEP field, root ABI/layout or scalar payload ABI).
Unresolved opaque-pointer errors identify the function, root and payload rather
than only the LLVM type ID. `DeclaredStorageUseJourney` deterministically models
the folded-GEP state with an abstract Scope argument and an unannotated
pointer-valued field load, then exercises actual execution and Native2 artifact
roundtrip. The delivery owner's focused journey and matched compiler results
establish the integrated outcome.

A longer balanced comparison subsequently caught the same class of failure in
`zero_trip` at the `R::iota` Function load. Consuming an existing Use hint was
necessary but not sufficient: InstCombine's aggregate unpacking constructed new
scalar pointer operands from `load.PointerOperand` without transferring the
old memory Use's physical declaration. If the namespace's zero GEP had already
folded, that Use was the only remaining declaration. The first `.unpack` load
therefore lost the geometry even though no source layout had changed.

Retyped memory operations now copy the retained Use declaration. Newly derived
scalar byte GEPs inherit the original root geometry on the GEP, preserving their
real displacement; they do not get an incorrect offset-zero declaration on each
leaf Use. The same helper covers struct/array load/store unpacking and narrowed
aggregate loads. Function component selection now accepts the actual memory
Use, consuming its explicit declaration when a zero component GEP folds to its
base. There is no generic pointer reconstruction or reduction in scalarization.
`instcombine.memory.storage_view.{use,gep}` counts propagation, and
`functions.memory_component.physical_use` counts actual component selection
through retained declarations. The focused journey includes real three-leaf
Function scalarization with the zero GEP folded both before InstCombine and by
InstCombine, followed by actual execution and Native2 roundtrip in both orders.
