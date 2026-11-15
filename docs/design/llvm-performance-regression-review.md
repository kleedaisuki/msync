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
