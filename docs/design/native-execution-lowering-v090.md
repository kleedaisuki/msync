# Execution lowering contract

This deletion-first backend batch is not an additional VM or artifact format.
The former gap was that Off bypassed execution lowering and interpreted portable
instruction objects. The current worktree uses the same lowered handler set in
Off, Linked and Force; final integrated throughput acceptance remains open.

## One representation and one execution path

Source/HIR -> verified portable NativeProgram -> **NativeExecutionImage** ->
baseline execution or optional native compilation of that same lowered code.
Source-free .mso/.mexe inputs enter exactly the same backend. Off disables JIT,
not backend lowering. The existing execution image is extended/reused; there is
no new mutable model alongside it and no source/planner replay in the VM.

The linked function owns immutable concrete operands: storage ABI, frame offsets
or binding indices, allocation layouts, field offsets, projection alternatives,
constant/contextual-literal plans, call signatures, and handler targets. Resolve
these once. A static single-layout field access is offset arithmetic, not a
runtime field/type/layout proof. Aliasing changes contents or bindings, not the
allocation's immutable layout. Dynamic layout unions select an admitted variant.

Both execution modes use those operands. General helpers remain complete for all
builtins and effects; this is not a numeric-only accepted language subset.

## Register-allocated native regions

The guest compiler now derives definition-specific scalar values and live
intervals before machine-code emission. This is process-local compilation data,
not another portable format, VM, frame authority or pointer cache. The portable
`.mso`/`.mexe` contracts remain unchanged. Sharing operation semantics does not
require preserving the interpreter's per-operation memory traffic.

`NativeRegisterPlan` partitions lowered basic blocks at helper/effect boundaries,
independently reachable PCs, incompatible checked refinements and cancellation
safepoints. Eligible outputs are compiler-proven private, unexposed scalar cells
without managed roots. Each definition has its own value identity even when
portable register coloring reuses the same slot. CFG liveness determines actual
live-out stores. Linear scan assigns volatile scalar registers; excess live
values spill to the native invocation's stack, not canonical guest frame cells.
Both x64 and ARM64 consume the same allocation plan and preserve their host ABI.

Region inputs perform actual semantic readiness/union selection once before any
region operation commits. An unavailable input exits before the region; baseline
execution then propagates the existing Future or reports the original operation's
failure. Region intermediates have no pending/init/type/private-write tests and
need no frame descriptors. Only live-out values are committed at the boundary.
If a live-out replaces a pending private value, a conditional boundary helper
removes that exact Runtime payload after successful computation; it never awaits
the overwritten value. No effect is replayed for state reconstruction.

Immutable storage ABI and write eligibility are code-generation decisions, not
runtime permission flags. The native descriptor no longer duplicates the static
kind or raw-write authority. Fixed field accesses are linked offset arithmetic;
single-case projections do not rediscover allocation layouts. Trusted bulk memory
operations check real dynamic extent/logical-allocation bounds, not the layout
graph again. External Core/host admission still owns physical type admission.
Indirect helper calls and dynamic projection helpers no longer acquire speculative
guards when no target/offset specialization was actually generated.

One region hit accounts for its original logical visits and edges; scalar
operations no longer update a hit counter individually. Cancellation polls reuse
the existing invocation, pin leases and workspace instead of allocating them
again. Obsolete owners from rebinding lose their pin leases at the next poll.
Jumps and conditional branches are native transfers rather than managed bridge
calls. Branch hit storage distinguishes the actual taken/untaken edges, including
when both alternatives share a target. Asynchronous suspension still releases all pins before waiting on the
already-started operation. Code pages retain their existing SafeHandle lease.

Codegen telemetry separately records selection, allocation, emission and
publication time, plus region/value/spill counts and eliminated canonical stores.
The existing per-function/opcode/source observations remain exact across policies.
`tests/NativeTiering/RegisterRegionChecks.cs` exercises detached artifacts, actual
register pressure/spills, source and bound routes, incompatible union refinements,
colored definitions, concurrent invalidation and multi-safepoint execution.

This follows the normal separation of value flow, register allocation and final
encoding described in the [LLVM code generator](https://www.llvm.org/docs/CodeGenerator.html)
and [Cranelift's allocator design discussion](https://www.cfall.in/blog/2022/06/09/cranelift-regalloc2/).
It is a local linear-scan backend, not an adoption of LLVM or regalloc2, and it does
not claim cross-block register residency or interprocedural native inlining.

### Linux x64 Native AOT receipt (2026-11-13)

The frozen candidate has 545 inputs under
`.temp/jit-register-backend/candidate-manifest.json`; all frozen and working-tree
hashes matched after publication/execution. SDK 10.0.403 produced one CLI, one
tier acceptance executable and one public-API numeric harness. The tier image
passed 2,162 journeys. The CLI passed source/`.mso`/source-free `.mexe` arithmetic,
capture and real-file-transfer routes, three actual missing-source failure routes,
and three recorded executions. The existing 128 preparation equivalences and
alias/cancellation/no-replanning controls also pass. ARM64/Windows have encoding
receipts, not claimed execution on this Linux x64 host.

The independent warm kernel decodes a portable artifact containing 901 dependent
adds, uses 32 untimed warmups, then checks the exact result after each of 2,000
public `InvokeAsync` calls. Eight order-balanced samples per condition on one
pinned CPU gave these medians; guest compilation is outside this timed interval:

| Kernel condition | Elapsed ms | Managed allocation bytes | Guest code bytes |
| --- | ---: | ---: | ---: |
| Control Off | 209.982 | 2,448,000 | 0 |
| Candidate Off | 212.237 | 2,448,000 | 0 |
| Control Force | 67.420 | 6,611,168 | 260,487 |
| Candidate Force | 16.684 | 3,440,000 | 19,083 |

This kernel is 4.04x faster than old Force and 12.72x faster than candidate Off.
Its four safepoint regions eliminate 897 intermediate frame stores. Median guest
compile time is 6.215 ms control versus 6.383 ms candidate: this is a warm-execution
result, not a cold-start compilation speedup. The separate pressure fixture has
12 live inputs, actual stack spills and only one canonical result store;
saved x64/ARM64 disassembly confirms no frame round trips between its 11 adds.

The two application artifacts still do **not** establish profitable automatic
promotion. Six rotated fresh-process samples include artifact read/startup and
guest compilation, exclude source compilation and telemetry, and check complete
stdout on every run. CPU medians in that window were:

| Application | Control Off ms | Candidate Off ms | Control Force ms | Candidate Force ms |
| --- | ---: | ---: | ---: | ---: |
| Record stream | 195.840 | 203.040 | 217.498 | 204.583 |
| Polygon queries | 855.340 | 876.109 | 1,071.404 | 1,041.076 |

Automatic promotion remains disabled. Baseline timing has unresolved variation:
the subsequent Off-only ABBA window averaged paired changes of -2.55% record and
+2.02% polygon, with individual pairs ranging -6.85%..+4.26% and -1.15%..+4.89%.
Record's dynamic host instructions decrease 814,701,642 -> 811,838,984 (-0.35%).
Neither that instruction reduction nor the large kernel gain accepts default
whole-application throughput. Both timing windows and the earlier pre-control-flow
window are retained, rather than selecting a favorable window.

Drivers, raw samples, traces and disassembly are under
`.temp/jit-register-backend/`; reusable receipts and reproduction drivers are
archived in `artifacts/validation/jit-register-backend/`. Published validation
payload directories are `artifacts/validation/jit-register-{candidate,control,tier}`
and `jit-register-kernel-{candidate,control}`. These are local validation payloads,
not release assets.

### Contextual specialization must not freeze one loop iteration

The actual source -> `.mso` -> `.mexe` transfer-plus-computation journey exposed
an older preparation bug: the first loop visit produced constant replacements
for its condition/body, then preparation stopped only at the backedge. The
resulting residual loop repeated first-iteration constants forever, including
under the old backend and JIT Off. Preparation now identifies cyclic CFG nodes
before recording replacements and leaves that suffix residual. Prefix I/O plans
remain prepared. A first-iteration value is not an invariant; full cyclic
specialization requires an explicit invariant/iteration representation, not a
VM freshness check or replanning. `ContextualLoopAsync` covers O0/O2, binary
round trips, actual transfer bytes and Off/Force completion.

## Authorities and checks that remain

* Frame bindings, canonical bytes/typed roots and Runtime pending payloads remain
  authoritative. Do not move Future into Core or add a native kind/object slot.
* External values/addresses require admission once at their boundary. Trusted
  linked operations do not repeat external admission or validate compiler facts.
* Actual union alternatives, explicit checked casts, readiness, dynamic binding,
  index/count bounds, cancellation, resource lifetime and effect suspension are
  real runtime obligations. They are not excuses for repeated graph validation.
* CWT pending precedence is preserved, including ordered host writes that set
  init bytes while a pending payload remains. Static Number is not ready Number.
* Native code contains no managed references. GC-root reads/writes use the typed
  managed boundary; primitive buffers are pinned only while actually used.

## Pointer and value intermediates

Lower dead, unexposed FrameAddress + Load/Store intermediates into direct typed
location transfers where established alias/control-flow facts allow it. Lower
static field projection plus access using linked offsets. Preserve complete
managed lowering for genuinely dynamic pointer flows; do not emulate general
pointer values in a second runtime recipe/cache authority.

Ready same-ABI transfers copy the canonical representation directly, including
its typed roots; pending transfers preserve the same Future identity. Avoid
NativeValue/NativeDatum construction when no value-level operation needs it.
Actual dynamic physical subsets retain the required representation conversion.
The portable Copy's destination `Type` is not a same-ABI proof: mono-to-union
and union-widening transfers have different tag/payload offsets. Backend raw-copy
selection needs both physical carriers and stable binding evidence. Resolve that
at lowering, not with a new type check on every executed copy.

## Lexical bindings are not namespace storage writes

The portable local pass now reuses an already live, private, exact-pointee
projected address when `FrameAddress` names a location bound to that same field.
It does not resolve a field twice or invent a pointer recipe. Once ordinary
copy/dead-result cleanup removes the intermediate, an otherwise unobserved lexical
Bind can disappear even when the namespace itself escapes. Binding a local name
is not a write to the field's bytes.

The removal requires no reads **or writes** of that lexical slot outside Bind,
no location capture/escape/persistent observation, and a ready private projected
pointer source. A Bind's RHS is a real read, not another definition. Binding must
remain if a later Constant/Copy/Drop writes through it; an uninitialized source
read must still fail with its original span. Reusing a pointer register for a
different allocation must not redirect an old binding or pre-bind address.

`tests/NativeMiddleEnd/NativeBindingLoweringChecks.cs` exercises every builtin,
pointer reuse, RHS aliases, writes through bindings, uninitialized-source spans
and actual persistent alias observation. The isolated 584-source batch passes
626 middle-end contracts. Its twelve polygon constant Scope constructors shrink
22→14 portable operations at O1/O2/O3 (four dead Bind and four FrameAddress steps
per constructor), without hoisting a single Scope outside lazy enumeration.
This is a lowering result, not a demonstrated throughput or product-LOC reduction.
The production pass/test edits await the next combined Core/AOT cohort; source
and diagnostics: `.temp/native-binding-lowering/receipt.json`.

Deleting the Bind alone does not delete its old lexical frame cell. Register
allocation now protects the actual parameter/capture ABI, used local locations,
escaping locations and persistent entry/session roots, rather than reserving a
cell only because a dead slot still has the Local role. No frame-observing
geometric operation gains permission to rename storage. On the same frozen O0
inputs this removes 52 polygon slots (559→507 at O2) and seven record slots
(132→125), with instruction counts unchanged by this follow-on change. The
isolated old-Core gate passes 659 contracts, including all builtin and persistent
binding controls; the existing sparse Native AOT image executes both newly
optimized artifacts in Off/Linked/Force with exact oracles. This is a storage
and functional result, not new-image throughput acceptance. Evidence:
`.temp/native-dead-lexical-cells/{receipt.json,polygon/code.tsv,record/code.tsv}`.

### Lexical initializer addresses are static lowering facts

HIR lowering now records a lexical Scope field as `(base register, layout,
ordinal)` while lowering its initializer subtree. Reads, writes and location
captures emit an AddressOffset at the use instead of resolving FrameAddress
through the canonical binding. This is compiler state, not a VM address recipe,
pointer cache or new object model. Canonical BindLocation and public
HirLoweringLayout slots remain intact for persistent/session observation and
references outside the initializer subtree. Captures still store actual
addresses or values at capture time; later field rebinding cannot redirect an
older capture or pre-bind address.

AllocateScope deliberately records an unknown binding: its initialization is
split from allocation and can cross branches or loop iterations. A nested
construction that rebinds the same location invalidates its earlier lexical
fact; lowering does not restore an obsolete outer base. Unresolved uses take
the canonical FrameAddress path. No source construct is rejected for this
optimization, and no new runtime checks are added.

The isolated frozen-Core batch passes 659 middle-end checks, the existing 8,471
native HIR checks, and 192 new static-field execution/observation checks. The
new checks cover detached HIR, checked native artifact execution, O0–O3,
value/location captures inside initialization, pre-bind snapshots, split
allocation in taken/untaken branches and zero/two loop iterations, and public
session slot observation. With dead lexical-local pruning, all twelve polygon
constructors retain 14 operations but shrink from six slots to three: projected
pointers no longer overlap for the whole initializer. Whole-program O2 slots
fall 507→471 with 620 operations unchanged. O1 trades two extra one-time stdlib
initialization offsets for 283→246 slots (564→566 operations); a shared field
write/capture no longer retains one long-lived pointer. These are IR/storage
results, not an accepted throughput measurement. Receipts and the frozen source
are under `.temp/native-static-fields/`; combined current-Core/AOT acceptance
remains the root integration batch's responsibility.

## PC, source, observer and deoptimization contract

Every lowered operation maps to its original portable PC(s) and source span(s).
A fused operation records the original logical visits/edges, not an invented
single opcode count. Do not fuse across branch entries or observable pointer
uses. A checked access reports the access's span, not the removed address step.

A native side exit resumes before the first uncommitted logical operation. A
fused private-address/access pair may exit before the address step; no effect
has started. Canonical state plus the lowered PC mapping is the deopt contract;
remove generic runtime pointer materialization machinery rather than retaining
it under another name. An asynchronous helper saves and resumes exactly its
already-started handle. Never replay an effect to reconstruct state.

## Concrete deletion boundary

* Replace the Off `StepAsync` opcode switch and duplicate Step/Linked forwarding
  layers with one lowered handler set consumed by default and by native bridges.
* Remove runtime virtual FrameAddress emission, pointee shadow banks, recipe
  dictionaries/history IDs and Materialize/MaterializeAll once lowered pointer
  intermediates no longer require them. Preserve their observable alias/future/
  binding/GC tests; replace recipe-specific internal tests with lowering tests.
* Remove repeated trusted-producer `ValidateAddress`, declared-type/layout
  lookups and monomorphic-kind checks; resolve immutable facts at link/admission.
* Merge/sparsify the remaining native workspace and hit storage from immutable
  compiled offsets. Do not replace removed checks with more caches or flags.

Review net code/state reduction and the remaining necessity of every check.
Then validate the complete batch once: managed + Native AOT, source/REPL and
standalone artifacts, IR/observer properties, actual future/alias/GC/async/guard
paths, and end-to-end performance on one frozen image. Preserve negative data;
no automatic promotion claim before representative measurements support it.

## Contextual preparation shares semantics, not execution ownership

The preparation audit found that arithmetic already uses the interpreter's
`UnaryValue`/`BinaryValue` and native builtins already use their ordinary pure
implementations. Its remaining instruction dispatch primarily owns knownness,
private-write admission, residual rewriting and observation ordering. Replacing
it with an unrestricted VM would lose these contracts, not remove duplicate work.
This is analogous to LLVM's
[SCCP solver](https://github.com/llvm/llvm-project/blob/main/llvm/lib/Transforms/Utils/SCCPSolver.cpp):
its value lattice and reachability remain analysis-owned while folding uses
shared operation semantics.

One real duplicate was incorrect: preparation interpreted every `Convert` as an
explicit conversion, ignoring its `Immediate` flag. Restoring only that old
behavior in an isolated current-source control turns the required failure of
implicit Bool-to-Number into the value 1. Preparation also described callables
without their public reflection signatures or pointer-pointee adjustment.

`NativeImage` now owns the existing pure conversion/refinement and callable
description operations. Execution and preparation consume the same semantics;
the runtime Narrow path still receives its prelinked storage type. No new cache,
runtime check layer, source restriction or catch-and-continue path was added.
`tests/NativePreparation/PreparationSemanticChecks.cs` checks 126 conversion
combinations plus two independent callable metadata controls through preparation,
binary encoding/decoding and execution. Managed acceptance passes; this is a
semantic unification receipt, not a claim about whole-product performance or the
30% budget. Causal control and logs are in `.temp/native-preparation-shared/`.

## Next proof boundary: definitions are not colored storage slots

The 573-input record profile has zero raw native operations in `make_row`.
The discriminating proof trace identifies one blanket denial: register 10 holds
an unknown captured FrameAddress at PCs 0/6/8/12, then an Allocate/AddressOffset
result at PCs 18/20. Binds at PCs 19/21 consume the new known value, but the
flow-insensitive whole-register Points union retains the earlier Unknown and
denies all private writes. There are no escaping frame locations in this case.
Evidence: `.temp/native-backend-proof/proof3.log`.

Slot coloring preserves physical ABI, not value provenance. The next Core batch
therefore assigns finite value identities to instruction definitions and entry
arguments, resolves each use through CFG reaching definitions, and joins paths
and backedges in the same address-analysis authority. Whole-slot summaries stay
derived lifetime joins for existing consumers, not a second mutable proof graph.
FrameAddress, Bind and aliased memory retain their location identities; redefining
a value register does not erase an aliased cell. Runtime consumes the immutable
per-use facts. No VM cache, extra check or linear-last-writer guess repairs this.

The corresponding unmeasured runtime batch already moves diagnostic metadata
lookup to exception paths and omits Visit/Edge work when no RuntimeProfile exists.
Enabled observers and explicit guest-tier counters are unchanged. These edits are
outside the preserved 573-input image and require the next coherent validation
and performance comparison; they are not yet an accepted speedup.

## Canonical frame bindings

A slot's physical offset and type are immutable image data, not an address that
must be allocated and initialized in every activation. `FrameAllocation` links
`NativeFrameSlot` descriptors once. A fixed slot constructs its address directly
from the current allocation owner and this descriptor; there is no general
layout lookup, dictionary, field-name resolution or type proof on this path.
Only location captures, `BindLocation` targets and persistent entry locations
have entries in the activation's compact canonical binding array. An address
obtained before a bind retains its old owner and offset; binding changes the
slot, not previously produced pointer values. The allocation's complete logical
layout and geometric bounds are unchanged.

Persistence has two different inputs: the image identifies eligible locations
(non-temporary/non-Never entry cells plus explicit session roots), while an
actual session dictionary owns persisted storage. Preparation deliberately
constructs frames without that owner, even for session-capable programs. Such
frames remain ephemeral; neither construction nor binding may invent or access
a session owner. The immutable slot descriptor replaces the per-activation
persistence-membership set. It is not a second mutable location authority.

### Program-wide provenance ownership

The linked image owns one `NativeAddressAnalysis.ProgramFacts`. Both frame
allocation safety and execution lowering use that owner, rather than independently
solving callee/Range producer graphs for each function. Core owns synchronization
and the publication boundary: demanded reachable summaries are closed before
publication; published function views are frozen, and a newly demanded caller
imports into caller-owned nodes rather than mutating an existing callee view.
These locks belong to compile/link analysis, never to guest field access.

`Unknown` and `MayAliasCurrentFrame` answer different questions. A returned fresh
allocation can lack a local allocation-site identity while still being unable to
alias the current frame. Runtime private-write eligibility uses the latter for
bind/geometry checks; optimization strong updates still require the former's
stricter known-target guarantees. Actual ABI, pending ownership and pin lifetime
rules are unchanged. The historical whole-function unknown-environment denial
has not been silently removed under this direct-alias predicate.

The middle-end's `NativeFrameCells` consumes this same distinction for private
canonical-register lowering. Bind and geometric operands are queried at their
actual PCs, not from the union of every value that occupied a colored register.
An unknown local identity is not a reason to disable unrelated numeric frame
access when the shared graph proves the operand foreign to this activation.
Unresolved formals and actual local-frame targets retain their existing denial;
no parameter annotation is promoted to a no-alias promise. The new
`NativeFrameAccessChecks.ForeignAllocation` control retains a real factory call,
foreign field access and independent private numeric traffic at O1, then checks
binary round trips and execution through O0–O3. The combined managed gate passes
633 middle-end contracts, 3,179 optimization observations and 28 source/standalone
HIR routes, including named recursion and returned closures. Logs are in
`.temp/core-hir-code-reduction/final-summary-{middle,controls,pipeline}.log`.
This is not yet a Native AOT or throughput acceptance result.

## Next measured deletion: monomorphic scalar carriers (proposal)

This is not implemented by the 602-input provenance batch. Its question is whether
linked scalar arithmetic can eliminate transient finite-sum values, without
introducing another VM, readiness cache or scalar object model.

Today a statically Number/Number operation takes the path
`GetDatum → Read → NativeValue.Restore → NativeDatum → AsNumber → FromNumber →
NativeDatum → Write`. The API's closed sum is appropriate for actual alternatives
and external values; it need not be the arithmetic ABI for known eight-byte
Number cells. The intended path is instead:

`link-selected typed handler → canonical scalar read → shared scalar operation →
canonical scalar write`.

| Fact/state | Authority and lifetime |
| --- | --- |
| Exact Number/Bool storage ABI | Existing linked slot descriptors and invariant typed bindings; decide the handler once, never test the value kind per operation. |
| Bytes and initialized state | Existing Core storage, with no Future dependency or new scalar native kind. |
| Pending payload/read priority | Existing Runtime exact-location CWT; a host ready write does not silently override its pending entry. |
| Operands during an operation | Ordinary local double/bool or an existing Future reference, not a shadow bank or reusable cache. |
| API, actual sums and pending computation | Existing NativeValue/NativeDatum contracts remain complete. |

A scalar reader must consult the existing pending authority before reading ready
bytes, then preserve the existing uninitialized error. Read the left operand once,
then the right once. If either is pending, retain the other already-read scalar
and construct the existing pending-computation operands from those snapshots;
do not fall back by rereading both cells. This preserves evaluation order and
alias snapshots without replaying an effect. Ready scalar writes must remove
exact pending metadata before performing the existing byte/init transition,
even when the output overwrites a formerly pending destination.

The primitive storage operations should expose existing byte representations,
not add Number/Bool sidecars or a second ready-state authority. Initially choose
only source/result physical monotypes for the typed handlers; genuine sum
selection and API crossings retain the complete closed-sum path. This is a
link-time representation choice, not a runtime kind-probe fast path.

Arithmetic should factor the *existing* six-operation numeric switch from
`BinaryValue`, so preparation, generic values and typed execution share one
implementation. Comparisons must share `NumberSemantics.Compare` and its nullable
unordered mapping; negate remains the existing sign-bit XOR, including NaN
payloads and signed zero. Bool-not and Bool branch can use the same physical
selection, with the existing pending continuation on suspension. Do not fold
`ValueType`, `IsType`, casts or other producer-contract observations into this
change: their nondemanding/logical-type behavior is a separate contract.

Acceptance needs the same real record/polygon artifacts and source routes, plus
pending/ready host overwrites, initialized errors, source/destination aliases,
indirect writes, captures, cancellation, generation/GC and observer equivalence.
Use separate attribution to verify fewer Restore/carrier operations; only the
source-matched Native AOT end-to-end gate establishes a performance benefit.
All added storage/handler code counts toward the product-code budget; extracting
one operation implementation is preferable to maintaining a second arithmetic
engine. Constructor/callback activation elimination remains a distinct measured
question, not a promised consequence of scalar carrier deletion.

## Deletion-first storage cleanup (2026-11-13)

The next standalone pass removes 80 net product code lines: linked instructions
keep neither unused Type/PC fields nor a redundant next-PC cache (56→40 bytes),
region copy/clear consume the existing image-boundary admission instead of
revalidating it, and pending region copies snapshot only futures while copying
ready bytes, typed roots and initialization flags directly. All six builtin-kind
maps now use the same conversion, without temporary List/array construction.
Public checked storage APIs remain checked. Managed controls and the new AOT
record/polygon, preparation, static-observation and telemetry journeys pass;
evidence is `.temp/deletion-first-01/receipt.json`. This is not throughput or
30% budget acceptance; the generic scalar arithmetic carrier chain remains open.

The following Number pass removes the ready arithmetic carrier chain itself:
exact Number inputs and Number/Bool output with fixed physical cells select the
typed handler during linking. It reads doubles and writes the typed result using
existing storage primitives; only genuine pending inputs reconstruct the existing
pending operation from snapshots. Union, bound, location-capture and persistent
cells retain their admitted path. Arithmetic/comparison semantics are shared with
constant folding; two forwarding frame methods are removed. Net product change
is -1 line, not another analysis/state subsystem. IEEE and mutation-after-pending
controls pass in Off/Linked/Force; AOT record/polygon and detached paths pass.
Same-artifact Off CPU medians fall 231.725→206.331 ms (record) and
963.2305→906.784 ms (polygon), six balanced samples plus one warm-up. Force still
loses to Off; no automatic promotion claim. Evidence:
`.temp/deletion-first-02/receipt.json`, including disassembly and raw measurements.

## Tier overhead elimination and Auto profitability (2026-11-13)

Profiling (`polygon-queries` and `signal-hysteresis` Callgrind + `--telemetry-level profile`)
identified five architectural sources of overhead when tiering or forcing JIT execution:

1. **Unbounded profiling on unavailable functions (`NativeExecutionFunction.NeedsProfiling`, `NativeInterpreter.RunActivationAsync/FromSync`)**:
   Once `IsProfitableForAuto` rejected a function (`unavailable = true`), `NeedsProfiling`
   continued returning `true` for the rest of the run, charging `ObserveTier` and `Metadata[pc]`
   reads on every interpreted step (`157.9M Ir` in `signal-hysteresis`). Fix: precompute
   `HasObservableBinary` once per lowered function, require `HasObservableBinary && !IsUnavailable && compiled is null`
   in `NeedsProfiling`, and immediately disable `enableTiering` once `IsUnavailable` becomes true.
2. **Per-entry `GCHandle.Alloc` pinning for captured/borrowed external slots (`NativeTierMemory`, `RunTierSync`)**:
   Captured location slots (`DynamicExternalSlots`) previously allocated/freed paired pinned
   `GCHandle`s via `PinLease` on every `RunTierSync` entry (`67.8M Ir` in `signal-hysteresis`).
   Fix: `RunTierSync` now `fixed`-pins up to two distinct non-frame `NativeAllocation`s on the stack
   alongside `frameAllocation` and passes those pinned bases into `NativeTierMemory.Reset`, keeping
   `PinLease` strictly as the fallback for 3+ dynamic external owners.
3. **`TierBridge` inline bloat, dead backedge atomics, and loop-body `RefreshPending` (`NativeInterpreter.Tiering.cs`)**:
   Removed dead `executable.Backedge()` inside compiled `TierBridge`, hoisted non-pending
   `RefreshPending` out of the inner bridge loop, kept inline dispatch inside `TierBridge` compact
   to avoid register-spill splitting, and kept `[MethodImpl(AggressiveInlining)]` only on small
   accessor/byte helpers (`NativeAddress`, `NativeStorage` 8-byte fast copy path) rather than
   multi-branch `CopyTrustedUnchecked`/`Pointer`/`SetPointer` helpers.
4. **Tiering image lookup and lowering gaps (`NativeExecutionImage.cs`, `NativeInterpreter.cs`, `Execution.cs`)**:
   Replaced per-function `ConcurrentDictionary` lookup with a lock-free array indexed by function id,
   added `!programUpdated` fast bypass around repeated `AsyncLocal` generation reads, enabled
   `CanEliminateDead` for proven non-escaping `FixedCell` temporaries even in functions containing
   `EnvironmentAddress`/`ProjectAddress` (without altering `PrivateWrites`/`RawWritable` contracts),
   eliminated intra-block redundant `Narrow` chains, and added synchronous fast paths for
   immediate `Allocate`/`IndexAddress`/`CopyMemory`/`ClearMemory`.
5. **Transition-aware `Auto` gate (`NativeCompiledFunction.IsProfitableForAuto`, `NativeMachineCode.AutoEnabled = true`)**:
   Count direct↔`Bridge` control transitions alongside loops and direct compute operations:
   accept loops with real compute (`directCompute > 0 && directTotal >= transitions`) and
   compute-dense helpers (`directCompute >= 2 && directTotal >= 4 && directTotal >= transitions * 2`),
   while rejecting pure constructor/glue helpers such as `<range-item>` in `polygon-queries`
   (which previously bounced through `TierBridge'2` 243,900 times under `Force`).

Verification (`Release` after final code edit):
- `tests/NativeTiering`: `PASS 2165 native tier journeys`; `tests/NativeMiddleEnd`: `PASS 666 native middle-end contracts`;
  `tests/CompilerTelemetry`: `PASS`; `tests/NativeExecution`: 17/17 earlier checks passed while
  `PendingMetadataAsync` hit host `inotify_add_watch(...) = -1 ENOSPC` (`ready` file was created cleanly);
  `tests/TailCallOptimization`: paused on concurrent external `Msync.Compiler.Semantics.SemanticAnalyzer.ProjectReturn` stack overflow on `fresh-escaped-location O0`.
- Native AOT `tests/Performance/run.py` (`polygon-queries`, `small`, `seed 17`, `object`, `--cpu 2`):
  `off` `p50=320.794ms` (`p95=349.586ms`), `auto` `p50=247.780ms` (`-22.8%`, `p95=251.996ms`),
  `force` `p50=267.991ms` (`-16.5%`, `p95=275.799ms`). Callgrind `auto` total: `1,012,373,477 Ir` (down from `1,255.9M Ir` in r10 `force`).
- 11 precompiled `baseline.mso` workloads (`.temp/jit-diag/r8-all-force/applications/*/small/17`, 7 samples `--cpu 2`, verified stdout):
  `auto` beat `off` on 10/11 workloads (`polygon-queries` `0.737x`, `bounded-sensor-pipeline` `0.850x`,
  `numeric-record-stream` `0.862x`, `live-policy-dispatch` `0.911x`, `signal-hysteresis` `0.917x`,
  `set-dedup` `0.924x`, `unicode-path-manifest` `0.928x`, `heap-scheduler` `0.956x`,
  `bplus-inventory` `0.970x`, `local-manifest-copy` `0.984x`; `local-range-copy` `1.036x` vs `force` `0.940x`).
