# Native tier state and phase ownership audit

This audits the historical 539-input checkpoint. Its pointer shadow/recipe
authority and duplicate dispatch layers are removed by the ongoing
[execution-lowering batch](native-execution-lowering-v090.md); historical
findings below are retained for causality, not as the current design.

The 539-input checkpoint is correct on its triggered Linux x64 Native AOT
contracts, but not profitable: record Force is 299.915 vs Off 238.247 ms CPU;
polygon Force is 1,702.143 vs Off 1,083.664 ms. See the receipts in
[the performance audit](performance-v080.md#joint-middlebackend-checkpoint-correctness-is-not-profitability).
This is an implementation/phase-ownership problem, not a limitation of JIT
compilation. Do not add another runtime capability/state layer to address it.

## Authorities and derived state

| State | Authority, lifetime and invariant | Action |
| --- | --- | --- |
| Program types, layouts, operand contracts, projection cases | Immutable compiler/artifact-reader output; image lifetime. An alias does not change an allocation's layout. | **HOIST** to compile/link; no per-access graph/type validation. |
| `NativeFrame.locations` | Canonical logical binding; activation lifetime; only Bind changes a location after construction. | Retain. A binding change is not an object-layout change. |
| Storage bytes, typed GC roots, init bytes | Canonical committed physical data; allocation lifetime. | Retain; native code never accesses GC root banks directly. |
| Runtime CWT pending payloads | Current semantic authority for Futures, keyed by exact allocation/offset; weak-key lifetime. | Retain until an explicit authority decision; Core must not acquire Runtime dependencies or universal object slots. |
| `NativeExecutionFunction` metadata/linked instructions | Image-generation-owned executable/link data; no copied user payload. | Retain; static proof and operand preparation belong here. |
| `NativeTierLayout` maps/materialization sets | Compiled-version data, immutable. | Retain, reduce to actual operands; **HOIST** access mode and fixed representation data. |
| Native `Slots` descriptors | Derived addresses into canonical data, not values; one synchronous invocation. Bind refreshes affected descriptors. | **MERGE** allocation; omit descriptors/pins that native code cannot read/write. |
| Native `Pointers` descriptors | Derived pointee addresses; when dirty, also represent a virtual pointer not yet canonical. | Separate address leases from logical materialization; remove redundant admission checks. |
| Recipe list/dictionary/IDs | Strong managed allocation/type/offset snapshots for dirty pointers, including pre-Bind identity. | **DELETE** general per-entry hashing/history for fixed bindings: compile-time source IDs suffice. Dynamic bindings can retain complete managed lowering rather than creating a second runtime authority. |
| Pending flags plus memory revision | Derived CWT summary; must never have a false-negative ready result. Stale true is safe but slow. | Currently duplicated authority/check work. Compare alternatives below before changing semantics. |
| Owner dictionary and pin handles | GC pin leases for primitive byte/init arrays, never GC root addresses; released before await. | Retain leases; specialize the ordinary single-frame-owner case and avoid pinning write-only managed destinations. |
| Per-PC `Hits` | Observability only; no language semantics. | **HOIST/MERGE** sparse native-PC mapping; one contiguous native allocation; no full-PC scan per entry. |
| Context handle, code SafeHandle lease | Opaque managed root across unmanaged entry; executable-page lifetime across invalidation. | Retain. These are real interop/lifetime obligations. |
| Saved pending ValueTask | Exact already-started operation; one suspension. | Retain exactly once; never replay an effect to simplify state. |

## Semantic readiness: three states, not `init => Ready`

The current semantic priority is **CWT first**, then physical initialization:

| CWT payload | Init byte | Semantic state |
| --- | --- | --- |
| absent | false | Uninitialized |
| present | false | Pending (ordinary VM future write) |
| absent | true | Ready |
| present | true | Pending, with shadowed bytes (possible through public host Store) |

VM ready writes/clear remove CWT metadata. VM future writes clear physical
storage and then install the CWT payload. Public `NativeAddress.Store` and
`NativeStorage.Store` do not know that Runtime CWT; they can set initialized
bytes without replacing the pending semantic value. Host/public Core copy or
clear has the same authority boundary. This ordered case is not a data race and
must not be silently redefined as a JIT optimization.

The current machine `Scalar` checks a pending flag only for indirect loads;
direct canonical Number/Bool operands check init but not that flag. Consequently
VM Future -> host Store -> resumed native arithmetic can diverge from baseline.
A combined regression must cover this before accepting cleanup. Removing
redundant static checks does not authorize removing real readiness checks.

Two authority choices need a contract decision:

* **Retain Runtime CWT:** semantic reads retain exact CWT precedence. Native
  access needs an exact, bounded summary/check across actual writes; an epoch
  alone does not identify which address is pending. Avoid adding another cache
  authority. Private unexposed cells permit stronger compiler proofs than public
  aliases, but `Unknown` cannot mean external.
* **Canonical storage readiness with Runtime payload ownership:** Core would
  carry only readiness, not Runtime Future objects or a new native value kind.
  This can eliminate tier copies, but host writes and all copy/clear paths must
  preserve the chosen semantic contract. A new byte tag alone does not clear
  Runtime roots or resolve the current host-write behavior. Do not implement it
  as an incidental optimization.

## Current phase violations

1. `NativeTierMemory.Descriptor/Refresh` repeat representation/private-write
   derivation on entry although fixed slot ABI and access mode are link facts.
2. `RefreshPointer` calls `NativeFrame.Pointer` and
   `NativeStorage.ValidateAddress` after trusted IR pointer producers. These
   repeat declared-pointee and owner-layout validation; external admission
   belongs at the external boundary, once, not after each field access.
3. `NativeMachineCode.Scalar` branches between mono/union layout and compares a
   mono kind on every access. A statically fixed monomorphic ABI requires only
   real readiness; actual union selection/explicit casts still inspect values.
4. Profiled projection guards call `HasLayout`, followed by the helper's own
   projection/layout resolution. A proven single layout should lower to offset
   arithmetic. Dynamic layout unions select a variant, not validate a graph.
5. Each entry separately zero-allocates context, scalar descriptors, pointer
   descriptors and full-PC hit storage; Flush scans every instruction. Those
   sizes and offsets are compiled-version facts, not runtime discoveries.
6. Whole-function escape denial conflates external bound cells with historical
   own-frame exposure. Replace only with a minimal compile/link proof; do not
   invent a runtime permission system or erase pre-Bind escapes.

## Coherent cleanup boundary

First batch: fix the concrete readiness divergence without changing authority;
merge/sparsify native workspace allocation; hoist immutable access/representation
facts; replace fixed-binding recipe hashing with compiled source IDs; remove
trusted-producer field/type revalidation while retaining external admission,
real union/cast checks and original-PC failures. Keep dynamic pointer cases on
the same complete typed managed lowering. Do not broaden source APIs or types.

Then run one combined managed/Native AOT/real-user-journey/profile/performance
batch on one frozen source/image. Retain negative receipts. Promotion remains
disabled until actual workloads, not coverage counters, establish profitability.

## Verified phase/identity correction (580-input causal checkpoint)

This checkpoint keeps the 573 Compiler/Editor/HIR/Optimization unchanged and
overlays finalized Core/Runtime and relevant tests, double-hash verified. Native
AOT tier 1,283 controls and actual source/artifact/REPL/I/O acceptance pass.
Automatic native promotion remains disabled.

Two measured implementation mistakes were removed, not hidden behind caches:

* Without telemetry, generic Visit, Diagnostic and edge forwarding still ran
  per operation. Observation now gates that work; source diagnostics resolve on
  exceptional paths. Native statistics and language/resource work remain exact.
* A colored pointer register first held an unknown captured address, then a
  concrete Allocate/AddressOffset result. Whole-register points summaries kept
  the old Unknown and denied all private native writes in `make_row`. The same
  Core solver now uses reaching ValueIDs; `At(pc, slot)` distinguishes current
  value identity from storage-slot identity. Saved pre-Bind pointers stay intact.

Record still executes exactly 751,558 logical operations/8,197 calls in every
policy. Force raw operations recover from zero to 122,415, all in `make_row`;
main's unresolved iterator/callback provenance remains an explicit limitation.
Callgrind host instructions fall 1,034,993,027 -> 915,031,080 (-11.59%), not a
CPU-share attribution to individual edits. Balanced same-window Off timing
improves 6.62%/9.62% against 573 on record/polygon, and 3.45%/7.67% against 539.
Native Force is not a demonstrated win: record's small difference matches
Linked, while polygon stays about 22% slower than Off. This is backend-cleanup
evidence, neither a JIT success claim nor the separate 30% deletion gate.

Receipts are in `.temp/native-backend-pc-batch/` (`source-manifest.json`,
`performance-result.json`, `profiles.json`, `record-off-callgrind.json`); immutable
payloads and consolidated acceptance live under `artifacts/validation/`.
Remaining mechanisms are unnecessary transient carriers/call ABI work and actual
producer summaries, not another runtime permission/readiness cache.

### Empty activation state (post-580 isolated batch)

`Activation.Plans` and `Regions` now remain null until an actual prepared-plan
submission or region entry. Reads do not create either table; null has exactly
the previous empty-table meaning, including the tail-call eligibility test.
Zero-iterator functions share the empty iterator array. Nonempty iterator state,
region snapshots, prepared results and their cleanup/ownership are unchanged.
This removes approximately 184 bytes of unused managed state per pure activation;
it does not add a capability flag, readiness authority or alternative execution
path.

The control is the immutable 580-input source, with only these four Runtime files
overlaid in `.temp/native-empty-state-batch/candidate`. The source manifest is
`.temp/native-empty-state-batch/source-manifest.json` (SHA-256
`455c07daeff8d1f12f6fab7ea9d2d49a0f683b9a21efff249272e1262897c7b3`).
A shared managed harness reads and executes the exact same bound record/polygon
artifacts, checks their complete output, and samples
`GC.GetTotalAllocatedBytes(true)` across artifact read through interpreter disposal.
Six alternating fresh-process samples per image/workload produced these medians:

| Workload | 580 control bytes | Candidate bytes | Reduction |
| --- | ---: | ---: | ---: |
| Record stream | 26,133,512 | 24,619,144 | 5.79% |
| Polygon queries | 233,519,872 | 215,430,880 | 7.75% |

Raw samples and the typed reproduction driver are in
`.temp/native-empty-state-batch/{allocation-results.json,run.py}`. This measures
allocation traffic, not retained memory or Native AOT CPU performance. Managed
elapsed times are diagnostic only. Dense frame-address removal is **not** part of
this batch: replacing its direct array accesses with repeated general layout
lookups would merely trade allocation for hot-path work. A future change must
link direct immutable offsets and retain only actual mutable bindings, preserving
old pre-bind pointers and capture/session aliases, with its own performance gate.

The same candidate passed the combined existing NativeTiering (1,283),
NativeExecution (482 real programs), NativeSessions (mixed/deferred and 160 body
routes), and NativePreparation (128 contextual equivalences plus transfer
alias/cancellation/no-replanning) gates. These cover nonempty iterator cleanup,
region/work ownership, prepared plan results, tail execution and suspended
state; no assertion was relaxed. Both 580-file source manifests were rechecked
after the batch. No additional CLI AOT publish or AOT timing claim is made for
this small allocation change.

### Sparse canonical frame bindings (isolated allocation batch)

The next candidate overlays only Runtime frame planning/access on the validated
empty-activation-state source. It deletes the dense per-activation address bank,
its full-slot initialization loop and the per-activation persisted-slot set.
Immutable offsets/types and binding indices are linked in `FrameAllocation`;
`NativeFrame` holds a reference to that descriptor array, not a copy of the
whole plan. A fixed access uses this direct array entry and the allocation owner,
not `NativeLayout`'s general indexer. Mutable addresses exist only for location
captures, explicit bind targets and actual persistent entry locations. The
complete canonical allocation/bounds and pre-bind pointer values are unchanged.

An independent review caught an important ownership distinction: a
session-capable program can be contextually prepared without a session owner.
Image eligibility does not imply that a persistence dictionary exists. The
constructor and bind operation now use the actual optional owner; preparation
remains ephemeral. A new public Prepare → binary → Execute contextual-transfer
control exercises persistent Local metadata without an owner during preparation.
Explicit live session roots remain deliberately nonexportable and are covered
separately by frame-binding/session tests. An older low-level test also bound
slots absent from its IR; it was split into actual private-slot and declared
bind/alias cases rather than adding an arbitrary runtime rebinding fallback.

Final source: `.temp/native-sparse-frame-batch-final/candidate`, manifest SHA-256
`23cede78bdbb2d58f959f4dda2bfac5bef30baf34b5fe46e9b7f11d35720822b`
(584 maintained inputs, excluding generated test work files). Its control is the
empty-state candidate, not a rebuilt frontend/Core prototype. The combined managed
gate passed NativeTiering 1,292, NativeExecution 482, all mixed/deferred session
routes and 160 body routes, and NativePreparation's 128 contextual equivalences
plus the new ownership and existing alias/cancellation/no-replanning journeys.

Six alternating fresh-process samples per condition, all 24 complete-output
oracles correct, measured additional managed allocation reductions relative to
the empty-state control:

| Workload | Empty-state control bytes | Sparse binding bytes | Reduction |
| --- | ---: | ---: | ---: |
| Record stream | 24,612,976 | 22,070,056 | 10.33% |
| Polygon queries | 215,430,880 | 183,598,968 | 14.78% |

Raw evidence: `.temp/native-sparse-frame-batch-final/allocation-results.json`.
These are managed allocation traffic results, **not** Native AOT CPU claims.
The four changed product files have a net **+29 physical lines** relative to the
empty-state control: immutable linking metadata costs code even while deleting
per-call state/materialization. This is not credit toward the separate 30% net
product-code deletion acceptance gate.

The same final source also passed **1,292 journeys as a real Linux x64 Native
AOT executable**, with all 584 candidate inputs rechecked after publication and
execution. Receipt: `.temp/native-sparse-frame-batch-final/tier-aot-receipt.json`;
payload SHA-256:
`0e465187295b90aab076c6975be75c5b506e5f40f5df09a398f36cf920c336d7`.
CLI end-to-end AOT timing remains a separate gate on this frozen cohort.

The first combined CLI AOT CPU gate is **on hold**, not accepted as a speedup:
record Off changed 233.3885 → 239.5015 ms (+2.62%); polygon Off changed
983.6745 → 975.249 ms (−0.86%) in the same balanced window. See
`.temp/native-sparse-frame-batch-final/performance-result.json`. Allocation
reduction alone is insufficient.

Disassembly identifies a concrete new per-access cost to investigate. In 580,
`GetDatum` directly copies a dense address entry and `Location` has no separate
native function. In the sparse candidate, `GetDatum` calls the non-inlined
`Location` (0x2319e0); its fixed-slot path constructs a managed-reference-bearing
24-byte return value using two `RhpCheckedAssignRef` calls, after which the caller
copies that value again for `NativeMemory.Read`. This is ABI/inline codegen work,
not field-name/layout rediscovery. The saved disassembly is
`.temp/native-sparse-frame-batch-final/inspection/frame-access{,-580}.txt`.
Frequency attribution must precede a claimed fix; this observation does not
justify restoring a shadow address bank or adding another runtime cache.

Callgrind subsequently confirmed the sparse ABI cost on the exact-output record
run: **939,935,869 host instructions vs 580's 915,031,080 (+2.72%)**, consistent
with the observed +2.62% CPU result. Out-of-line `NativeFrame.Location` accounts
for 47,992,757 self instructions; `RhpCheckedAssignRefESI` totals 22,900,645 across
all contexts. These are attribution counts, not additive savings estimates.
The next unmeasured candidate explicitly requests inlining of this small
existing accessor so its address construction can remain in the consumer's
known stack context. It adds no reader, pointer cache or semantic authority;
its benefit must be demonstrated in the next combined AOT image, including
verification that the return-buffer assignment helpers disappear.

### Combined 602-input provenance/lowering cohort

The next full-current cohort is frozen at
`.temp/native-program-provenance-batch/source`. Its amended manifest SHA-256 is
`629621658624d60e376a6532226e50333116c0845ab74d3ebd15fd44cfe949c5`:
only a new test fixture's Store initialization mode was corrected after the
first validation attempt; the original failed manifest/receipt are retained and
all product hashes are unchanged. The combined receipt reuses unchanged Model
26,018 and records Tier 1,296, Execution 482, complete mixed/deferred Sessions,
Preparation, MiddleEnd 666, Pipeline 28 and HIR **8,663 actual checks**. The rough
8,707 expectation was not used as an acceptance oracle.

One CLI and one Tier Native AOT payload were built from those same 602 inputs;
the Tier executable passed 1,296 journeys, including actual producer-proof native
raw writes. All source hashes matched after execution. Receipt:
`.temp/native-program-provenance-batch/batch-receipt.json`.

The CLI's disassembly now has **no out-of-line Location symbol**: GetDatum forms
the fixed address directly in a stack local, with ordinary mov instructions for
owner/type/offset. Its previous extra Location call and two checked-reference
assignment calls are gone. Evidence is in
`.temp/native-program-provenance-batch/inspection/`. This verifies the generated
mechanism, not end-to-end CPU acceptance; the latter is recorded separately by
the source-matched and same-artifact measurement owner.

The 602 cohort is **not end-to-end accepted** despite those local/AOT gates.
The actual Set-dedup source journey exposed a Core symbolic-analysis CPU
regression before VM execution: a sampled stack repeatedly enters
`Invocation.Import` at the same producer PC. Its low retained heap and sustained
CPU are consistent with repeated shared-DAG traversal; finite producer identity
and termination controls do not by themselves establish a reasonable traversal
cost. The frozen bytes remain unchanged while Core investigates the general
traversal algorithm. Evidence:
`.temp/native-program-provenance-batch/set-compile-symbolic-stack.txt`.
No runtime guard, analysis cap or provisional-bottom result is an acceptable
substitute for preserving complete facts with bounded repeated work.
