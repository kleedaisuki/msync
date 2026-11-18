# Execution lowering contract

## Current working-tree status

The evaluator is synchronous. Work-scope-free calls use a thin direct entry and
caller-owned return slots; actual stack availability controls recursive direct
entry, with explicit guest caller records for the remaining chain. Host APIs
remain asynchronous adapters. Typed Futures carry
pending data; background submissions own released processes, standard input,
filesystem scalar reads and scalar stream consumption. Background submission
returns without a start/admission handshake; incomplete result demand waits on
the result Future. Ready pure work may execute synchronously; there is no
caller-coroutine guarantee or added closure snapshot.
Compiled storage, numeric representations, handlers and connection resource
indices are persisted in the Native 2 / compact 4 executable contract (HIR 4).
Compact 3 is rejected with a recompile diagnostic, not upgraded at runtime.

Implemented and exercised: parameter storage contracts; independent escaping
locations; native numeric regions and endpoint resources; delayed input through
function return; queued process arguments; scope/selected/global ownership;
input abandonment and ordered reads; deep non-tail calls and readiness/type
exit separation. Native publication and measurement receipts are documented
below; the final goal audit records the completed scope and its evidence.

The deletion-first runtime cleanup removed the submission rendezvous and
duplicate compiled-path checks, ownership objects and loading metadata. The
user clarified that deletion targets runtime mechanisms, not repository files.
The latest measured image is `artifacts/native/runtime-shared-plans/msync`;
see [benchmark status](benchmark-throughput-v090.md#latest-measured-runtime-2026-11-15).
The previous active goal has been cleared by the user; this is a state handoff,
not a claim that every proposed architecture change has been implemented.

The next discussed direction is unsafe C# with genuinely unmanaged guest
storage: native segmented byte frames, native escaping objects, and managed
host handles only at interoperability boundaries. This is **not implemented**.
Current segmented frames still use managed byte/root/initialization arrays;
CLR reference stores and their write barriers remain. `unsafe` alone does not
change those representations. Existing escape facts choose allocation class;
escaping-object reclamation still needs an explicit ownership/lifetime design.

This deletion-first backend batch is not an additional VM or artifact format.
The former gap was that Off bypassed execution lowering and interpreted portable
instruction objects. The current worktree uses the same lowered handler set in
Off, Linked and Force; the final native throughput receipt is recorded below.

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

## v0.9 compiled storage contract (2026-11-13)

The compiler now owns `NativeStoragePlan` on the final Native function. HIR
tracks source-level object/alias relations; Native optimization handles the
actual lowered address operations and colored slots. The finalized storage
contract records escaping slots, private call inputs, borrowable parameters,
frame-alias sites, admitted narrow layouts, frame allocation eligibility, and
per-call snapshot/borrow plans. It is materialized from the existing middle-end analysis before physical slot
coloring, then relocated with the code, not reconstructed by the execution image.

`NativeOptimizer.Optimize` captures each function's existing converged escape/address
analysis before ownership-preserving last-use moves and producer retargeting. It
does not reconstruct escape summaries or a second analysis graph at the end of
optimized compilation. Call plans are linked once all parameter plans are available.
Register coloring intersects borrowing/private flags and unions escape flags for
merged slots. Instruction compaction and function folding relocate plans alongside
their instructions/slots. `Compiler.LowerInstructions` preserves those plans through
folding. `NativeOptimizer.Optimize` also returns a planned program to direct IR clients;
`NativeOptimizer.PlanStorage` finalizes hand-constructed IR without optimization.
O0/raw IR has no preceding optimizer analysis, so its producer computes the plan
once. Changing raw IR after finalization requires updating or clearing its plan
in that producer.
Preparation relocates unchanged instruction/call plans, removes replaced call
sites and describes its new constant slots. It does not run alias analysis.

### Bind-time execution selection

A serialized LLVM-produced relocatable object contains the logical instructions
and their compiler-owned storage plans, but no selected execution image. Binding
first prepares contextual file operations and relocates the corresponding code,
allocation sites and call plans. Instruction selection then consumes that final
code and writes the fused backend to the executable artifact. Loading a bound
artifact consumes that image without repeating selection or file planning. Older
relocatable payloads that already contain a backend do not dictate the newly
bound image. Interactive submissions can still carry a session image directly;
publishing one does not invoke the contextual file planner.

JSON disassembly exposes both the logical instructions and, when present, the
selected backend dispatch sites and their spans. Logical profiling still counts
the original instructions covered by a span. Use the backend sites, rather than
those logical counts, to determine whether binding removed a dispatch.

The selector folds non-escaping address temporaries into their memory users
when the producer dominates the access and the base cannot be rebound. A
numeric frame load/store may then use the tier's direct scalar path; a dynamic
pointee still needs the existing managed ownership and pending-value path.
Register planning uses the selected operands, not the eliminated logical
address slots. Literal globals are not Native addresses: when SimplifyCFG
sinks whole immutable literal loads through a select, target selection uses
the literals directly so contextual planning can still freeze file decisions.
Bind-time liveness is a block dataflow over packed slot sets, expanded once
to per-PC membership for the selector. This avoids allocating large
per-instruction hash sets for long straight-line preparation bodies.

A synchronous guest root owns the frame arena: payload and initialization
bytes are in unmanaged segments, while references remain in a separate
GC-visible root bank. Nested synchronous calls borrow the arena, and root
exit frees its segments. The tier consumes those byte addresses without
creating GC handles for them; escaping allocations retain their existing
ownership. A tier invocation holds one code-page lease across poll resumes
and calls the published machine entry through its address.

On Linux and Windows x64, a statically bound call to a fully native scalar leaf can enter
the callee directly from generated code. The caller borrows one scratch extent
per activation, builds the ordinary tier context there, and calls the same
published entry used by the runtime tier driver. The caller's executable
page retains the callee's page until its own last invocation ends, including
when a version is retired. No secondary copy of the callee or managed entry
call is required. Applicable callees have a bounded native body and return
one exact Number, Bool, or Null. Hot-caller linkage can compile a leaf the
standalone Auto heuristic had rejected; Auto evaluates the caller after linking.
Pending inputs resume at the original call before any native capture writes.
The artifact representation and non-x64 paths are unchanged. Telemetry
distinguishes linked sites and successful machine calls, including captured
calls and their targets.
The hidden context argument follows the same broad pattern as
[Cranelift's VM context](https://github.com/bytecodealliance/wasmtime/blob/main/cranelift/docs/ir.md);
publication also tracks the lifetime of referenced code, as in
[LLVM ORC's dependency model](https://llvm.org/docs/ORCv2.html).

Windows x64 uses the same leaf eligibility, context layout, guards and
code-page ownership as Linux x64. The x64 emitter puts the child context
above the Windows home area and caller spills, passes the context and entry
PC in RCX and EDX, and clears child initialization bytes without clobbering
RDI. Large combined frames probe the intervening stack pages. Scalar
floating remainder uses the system UCRT's `fmod` on Windows, so a captured
leaf with binary64 remainder does not require a managed bridge there.
These are platform ABI details, not a separate guest call protocol.
See [Microsoft's x64 calling convention](https://learn.microsoft.com/en-us/cpp/build/x64-calling-convention)
and [stack probing requirements](https://learn.microsoft.com/en-us/cpp/build/prolog-and-epilog).

The machine-call journey on the Windows .NET 10.0.14 x64 host under Wine 10
recorded 3,544 completed scalar calls with zero bridge invocations, and
7,488 captured field calls with matching alias mutations, under both Auto and
Force. This executes the Windows emitter and Microsoft calling convention,
but does not substitute for a packaged Native AOT run on Windows.

On Linux x64, a detached two-callee loop (150,000 iterations, five
alternating pairs pinned to one CPU, telemetry off) went from 414.6 ms to
42.1 ms with the same artifact. An instruction-profile run at 2,000
iterations replaced 3,744 managed bridge calls with 3,744 machine calls.
Command-owned closures now publish capture vectors from the guest allocator.
Each entry holds the payload and initialization addresses of a scalar capture.
The collector marks the vector with its environment. A native call sets up
location captures as pointers to the original cells, not copies; a value
capture still initializes its private frame slot. Shapes and pending inputs
are resolved at the call boundary, before the callee writes to shared storage.
Binary64 remainder uses [libm fmod](https://sourceware.org/glibc/manual/2.24/html_node/Remainder-Functions.html)
on Linux when the entry is available, rather than entering the value bridge.

Direct LLVM calls include one argument edge per location capture, but Native
uses the call's evaluated closure descriptor, not those LLVM arguments. Native
lowering now drops ordinary pointer loads and address producers whose only
remaining uses are those discarded edges. Function reassembly uses that same
view of the use list. It keeps volatile/ordered loads under the existing
[LLVM load contract](https://www.llvm.org/docs/LangRef.html#load-instruction)
and keeps any producer still needed by the actual closure or a Native input.
The lowering trace counts capture ABI edges and omitted producers by kind.

Command-owned heap layouts with exact Function fields have a native callable
vector lane indexed by the existing reference offset. A typed field write or
representation copy publishes that field's closure vector there. For a field
load consumed only by the next linked call, generated code loads the vector
from that offset and does not write a temporary Function value. It can also
consume a directly preceding composition whose code/name are entry constants
and whose environment has a published vector. Fallback resumes at the original
producer, not the call, so it can materialize the full value. CLR root storage
still retains compatibility references; the new lane does not replace the
whole root graph. The detached journey replaces a callable through a shared
scope field in a hot loop and checks both the replacement and the original
capture's state after collection.

For signal hysteresis (65,536 samples), the compiler omits 40 static capture
loads, including 24 per hot-loop iteration. An instruction run records
130,816 field-to-call fusions and 65,408 composition-to-call fusions; bridge
entries fall from 392,451 to 130,819. Rebuilding the same source with each
image and running detached artifacts on one CPU with telemetry off (five
alternating pairs) changes the run median from 470.7 to 103.8 ms, a paired
median change of -77.4% before native sequence iteration.

Numeric sequence ranges now carry their start, limit and step directly.
A VM iterator uses a 32-byte cursor in its frame's raw suffix; it does not
create an async enumerator or enter the GC root graph. External Range
consumers can still request the original async traversal lazily. Opaque
iterators retain the existing lifetime and effect scheduling. The range
cursor retains binary64 stepping, signed zero, both directions, an
exclusive limit, and the delayed failure if adding the step stops
advancing. The x64 tier increments and branches directly against this same
frame state. Lua's [numeric loops](https://www.lua.org/source/5.4/lvm.c.html)
similarly keep the arithmetic progression in frame-local state, but use
different endpoint and overflow semantics. The profile distinguishes
sequence opens, yields and machine-side yields.

Field-to-call fusion also covers one intervening pure scalar argument
operation on a private destination. It reads the field first and executes
the arithmetic before entry; on a pending operand the original sequence
is still materialized in order. The signal loop no longer crosses the
bridge in steady state: 65,407 advances and 65,408 nonadjacent field
calls run as machine code, while the full execution records three bridge
entries, down from 130,819. On the same bound artifact, five alternating
single-CPU runs with telemetry off change the median from 112.8 to 62.9 ms
(paired median -42.4%). The detached heap journey also sweeps 19 times
while a numeric cursor is open, reclaiming about 78,000 dead objects.

Signal hysteresis, 65,536 samples, one CPU, five alternating telemetry-off runs
of the same artifact: median 1,182.4 to 490.7 ms in the final stack/capture
image. Closure linkage and native remainder/Null handling are included.
Separate instruction runs record 261,632
machine calls and 842,018 to 392,451 bridge entries. The alias-and-sweep
journey also checks both JIT policies after collecting over 16,000 dead objects.

Ordinary call continuations now use the activation chain already owned by the
segmented guest stack; there is no parallel list of suspended callers. Tail
selection consumes the compiled private-slot flags, and its two alternating
argument buffers use the guest heap. The collector treats the cached buffer
and active incoming buffer as roots. The detached O0 journey exercises
20,000 non-tail calls followed by 100,001 tail swaps and checks matching
push/resume counts and exactly two allocated argument buffers. This reuses the
current call record as in [Lua's tail entry](https://www.lua.org/source/5.4/ldo.c.html),
but keeps alternating parameter buffers because an incoming parameter can
still borrow a cell in the previous buffer until the next snapshot is written.

Bool/Number selects use typed machine loads of the chosen arm only. A
pending condition or chosen arm still transfers control to the existing
semantic handler. Instruction telemetry distinguishes machine selects and
actual managed bridge invocations from operations dispatched inside a bridge.

Command, embedded, and REPL execution use the same non-moving guest heap for
object payloads and initialization flags. Explicit objects, escaping frame
cells, closure environments, and interpreter-created ready range snapshots
use unmanaged slabs. Block headers and free-list links live in those slabs;
sweep coalesces dead blocks and subsequent allocations reuse them before
growing. A command can sweep at interpreter backedges and machine poll exits,
tracing active frames as well as statics and retained literals. The REPL also
sweeps between submissions, adding its persistent cells to the roots.
Pending work and opaque active iterators defer active collection. Embedded
interpreters do not sweep while their public value exports can be retained
outside the VM's root set. Disposal transfers slab lifetime to one lease in
that case so existing address aliases keep working; other instances release
their slabs on disposal.

Scoped 64-bit command and REPL execution store same-arena object edges in an
unmanaged pointer lane in both stack segments and heap blocks. A canonical
closure environment uses the same lane with a low-bit tag. Typed reads and
writes select the lane by value kind; only copies across different storage
representations materialize and classify a generic root. The CLR lane still
holds host resources and type metadata, not a second copy of each guest edge.
Even an empty Scope has a unique native block. A live block header identifies a
reusable allocation descriptor; sweeping returns that descriptor and any
canonical environment view to the next lifetime. A second composition of the
same live environment still has its own callable identity. Public embedding
does not recycle exposed descriptors. The mark frontier is native and queues
each local object once; hosted resource graphs retain the compatibility tracer.

`heap.collect` records the live bytes, reusable extent, and reclaimed blocks
or the reason a requested collection was deferred, as well as raw object and
edge counts and frontier capacity. The `guest-heap-repl` journey checks stable
live size and block reuse across REPL submissions, and keeps cycles reachable
via an active frame, closure, and range snapshot in a bound command under both
interpreter and JIT policies. The machine call journey also overwrites 40,000
captured functions through a Scope field, preserving an independent live
closure while checking that the majority of guest environment views are reused.

LLVM byte offsets identify fields within a declared storage view; they are not
the VM's split byte/reference offsets. CSE keys typed GEPs by storage view and
logical carrier as well as their base/offset. Select and phi folds retain typed
field addresses rather than selecting a variable LLVM byte offset. Aggregate
memory transforms keep a callable whole when it would otherwise be expanded
into independent pointer loads/stores, and carrier selection uses the same
function-component mapping as emission for any remaining component loads.
Iterator operations retain their individual call sites because tokens lower to
immediate cursor IDs scoped to a single activation, not runtime pointer values.

At bind time, unsigned-number range propagation now joins with prior block
entries before widening loop endpoints. This preserves a widened endpoint on
revisits and prevents a later narrow edge from restarting the same interval
cycle. The existing numeric-lowering pass reports rounds, transferred
instructions and the number of widening events.

The official application runner now passes the standard-scale seed-17
`heap-scheduler`, `set-dedup`, and `bplus-inventory` cases from source, portable
object and detached executable. The previous AOT image could not compile those
fixtures. In one telemetry-off Native AOT observation pinned to one CPU, B+
compiled in 1.67 s, bound in 0.19 s, and ran from the bound artifact in 4.05 s
(5.72 s on the separate source route). The earlier same-object managed binder
was stopped after about two minutes while still in numeric range propagation;
after the fix a separate bind trace saw at most seven rounds per function.

Allocation sampling of the managed build on the same executable exposed two
unrelated hot-path allocations: `Enum.HasFlag` boxed frame-slot flags on reads
and copies, and the cached JIT lookup created a captured compiler callback
before checking for a published entry. The former is now a bit test; the latter
is created only on the cold compilation path.

On Linux/x64, nine interleaved runs pinned to one CPU of the same 40,000-pair
cycle workload (80,017 guest allocations, JIT forced, telemetry off) moved the
median process wall time from 221.5 to 210.9 ms (median paired change -3.9%).
A separate diagnostics run reports 31.73 to 15.48 MB of process-wide managed
allocation during execution, including instrumentation, and 77,636 descriptor
reuses out of 80,017 requests. Native slabs reserve the same 327,840 bytes.

The x64 tier lowers exact Number and Bool field accesses directly from the
frame's edge lane. The native block header supplies the payload extent, so the
same base pointer computes both the data and initialization-flag addresses;
the packed address bytes supply any interior offset. The field displacement
comes from the bound operand (also for projections with one shared offset),
not from a field-name lookup or a CLR descriptor. Pending values, uninitialized
reads and non-native targets use the original linked access. An isolated
field access between two bridged operations stays in that bridge run rather
than splitting it into two machine/host transitions. Profiles distinguish
emitted sites, retained bridge sites, native load/store hits, and redirect causes.

On Linux/x64, 11 alternating runs of the same bound artifacts (one CPU, JIT
forced, telemetry off) moved the 40,000-iteration shared-field loop from
89.7 to 25.1 ms (paired median -73.4%). Diagnostics recorded 119,617 direct
loads, 119,616 direct stores and 199,361 to one bridge entry. The 40,000-pair
allocation workload moved from 201.9 to 195.9 ms (paired median -3.1%):
79,846 scalar accesses moved into machine code without increasing its 119,819
bridge entries. The focused field journey checks both alias writes and exact
Bool and Number reads under off, auto and force.

For genuinely mixed layouts, the x64 tier and linked interpreter can compare
the layout ID in a native object header directly. The ID uses the high 16 bits
of the existing extent word; unknown IDs retain the ordinary lookup. No header
or payload grew. In the official B+ benchmark, with the same bound artifact,
five telemetry-off Auto pairs measured 4,404 to 3,511 ms (paired -19.9%).
A separate profile recorded 1,556,532 machine layout comparisons and
4,102,845 to 2,928,210 bridge entries.

The HIR lowerer now eliminates the comparison entirely when the producer is
a known allocation or all admissible layouts map the accessed field to the
same native byte and reference offsets and the same field contract. It emits
one typed projection retaining the original layout/ordinal pairs for analysis
and artifacts. Backend operand linking collapses equal geometry to one fixed
displacement; no runtime layout ID is retrieved for that projection. Distinct
LLVM byte offsets are not reinterpreted as a common Native offset. Mixed
member dispatch remains explicit, and LLVM can reuse its layout comparison
for an unchanged SSA receiver.

On the official standard-scale B+ workload, this removed 621 static
`HasLayout` sites (1,680 to 1,059). Separate instrumentation recorded
2,188,663 to 1,709,684 direct header comparisons and 2,928,210 to 2,823,554
bridge entries. Five telemetry-off Auto pairs with each image's own artifact
measured 3,452 to 3,508 ms (paired +0.8%); this lowers dynamic work but does
not establish a wall-time improvement.

Each anonymous scope already has a concrete LLVM struct and a guest payload
layout. The source category `Scope` does not specify a universal field
displacement. When a direct call supplies an allocation or a source call with a
known concrete result, target lowering can select a fixed entry for that
argument layout. It traces parameter forwarding only for parameters that feed
a layout test, reuses variants by function, parameter and layout, and drops
constant layout branches and their unreachable blocks. The original entry is
retained for calls without a known layout; function capture and argument
passing remain unchanged. This does not specialize incremental session entries.

The official corpus B+ case (standard/17, bound, Auto, five telemetry-off pairs,
one warmup, CPU 12) passed its output oracle. It generated 25 variants with
15,849 profiled calls, reducing native and linked layout-header comparisons from
1,709,681 to 1,647,971 while bridge entries remained at 2,823,554. Median
bound-run wall time moved from 3,659 to 3,633 ms (paired median -0.6%); the bound
artifact grew from 575,058 to 652,337 bytes. The numeric-record-stream control
also passed. The B+ measurements establish fewer layout comparisons, not a
material throughput improvement.

### Passing guest object addresses across machine calls

When a layout test becomes constant, the remaining blocks are emitted along
the surviving edges. A field-address phi with a single reachable definition
uses that address directly. Fallthrough leaves the field address next to its
load for the existing offset fusion; no address slot is copied just to feed a
load in a later block.

On Linux x64, a bounded machine leaf can read exact guest-object parameters
and return a scalar using its existing published entry. The caller puts the
guest payload pointer and interior byte offset in its existing stack scratch.
The leaf reads scalar fields from this pointer and the bound displacement.
Read-only borrowed parameters use the same pointer access through the slot's
edge address. The scratch context has no interpreter handle: an unresolved
read exits to the original call, not to a bridge owned by the scratch frame.
Guest stores and mutable captured locations are not part of this entry.
Codegen records linked pointer call sites and profiles count completed calls
separately from the existing scalar/captured call families.

In official B+ (standard/17, bound, Auto), six linked sites completed 494
pointer calls. Managed direct calls fell by exactly 494 (592,489 to 591,995).
The combined lowering and runtime change reduced bridge entries from 2,823,554
to 2,792,754 without changing the 1,647,971 layout comparisons. One
`_Chain::at` instance still accounts for 550,232 of them. Both B+ and the
numeric-record-stream control passed their output oracles. Telemetry-off B+
pairs gave +2.0% on CPU 12 (five pairs, 3,895 to 4,111 ms median wall time)
and -1.2% on CPU 6 (seven pairs, 4,008 to 3,929 ms); this establishes the
new call path and the reduction in bridge entries, not a throughput win.

### Layout test and branch fusion

When the x64 tier can read an object header directly, an adjacent branch that
is the only consumer of the layout test uses the comparison flags. The machine
path does not publish or reload a Boolean frame slot and decrements fuel once
for the pair. The original branch PC is bridge-only: a host or pending fallback
materializes the Boolean and consumes it within the same bridge dispatch loop.
Profiling still attributes both logical instructions and their edges; fused
native executions are counted separately.
No artifact or object layout changes. The parallel in LLVM is its
[x86 flag reuse during compare optimization](https://llvm.org/doxygen/classllvm_1_1X86InstrInfo.html).

On official B+ the bridge-only cold continuation kept 406 fused sites and
1,014,244 native executions. JIT output shrank from 646,944 to 593,758 bytes.
Bridge counts did not change; the five same-artifact, telemetry-off CPU-6 pairs
showed a paired median +4.7% elapsed. This result is not a throughput win.

### Native guest-reference copies

The x64 tier moves an exact scope or nullable scope between fixed frame slots
by copying the guest edge lanes and packed offset. Both slots must have the
same representation. It never writes managed references. The separate host
root lane is pinned only for an activation that emits these copies, and read
only to detect an occupied host root. Pending data, a missing native lane or
an occupied host root still use the original operation. This retains the
guest heap's original ownership and does not move host values into it.

Selection leaves runs of copies between bridged operations in the same bridge
instead of introducing two extra transitions. Codegen reports selected and
retained sites, and execution reports completed native copies and redirect
causes. The on-disk format is unchanged.

Official B+ (standard/17, shared bound artifact, x64 Auto) executed 893,505
native copies at 132 selected sites with no copy redirects. Bridge entries fell
from 2,792,754 to 2,070,036 (-25.9%). Five telemetry-off CPU-6 pairs measured
3,649 to 3,785 ms median wall time and -0.85% paired median change. The numeric
control triggered no native copies and its bridge and generated-code counts
were unchanged. Both output oracles passed. This establishes fewer bridge
entries, not a stable wall-time speedup.

### Inline checked guest-reference forwarding

The x64 tier can inline a statically linked function whose result is just one
of its nullable scope parameters converted to a single concrete layout.
Selection recognizes the linked body, not the library name, and discards
only private null setup. Call-site machine code tests the native object's
header (null needs no header), then uses the ordinary guest reference copy
without creating a callee frame. The runtime still evaluates the callable
and argument expressions as before; the original call handles a mismatch or
a host/pending input. Call edges and actual frame entries remain separate
profile measurements. Isolated runs of forwarding calls and copies are kept
in the bridge rather than adding a new round trip. Neither the object header
nor the bound artifact format changes.

Official B+ (standard/17, shared bound artifact, x64 Auto) inlined 359,729
executed calls at 26 sites with no redirects; 17 isolated sites stayed within
their respective bridges. Bridge entries stayed at 2,070,036. Guest frame
entries fell from 760,457 to 400,728 and logical instructions from 20,455,434
to 18,297,060. Managed allocation in the separate profiled run fell from
297.8 to 188.5 MB. Five telemetry-off CPU-6 pairs all favored the candidate
(paired median -19.8%; wall-time medians 4,157 to 3,025 ms). The numeric control
emitted no forwarding sites and its bridge and generated-code counts were
unchanged. Both output oracles passed.

### Whole-function SSA machine entry

Execution has a separate Linux x64 path for ready, pure scalar
functions. Target selection reads the optimized LLVM graph, not the serialized
slot instructions. It retains blocks, scalar values, and phi edge assignments
in a typed function body. The first call emits a single machine body; the
selected function does not dispatch partial regions through TierBridge.
Comparisons and arithmetic use XMM registers, with native state storage for
pressure and simultaneous copies on interfering phi edges. A backedge budget
returns only to check cancellation; its continuation resumes the same body.

The first body format accepts Number and Bool arguments and results and has no
producer of a future. It therefore selects away ready Boolean demands. A
pending argument or an unsupported whole body uses the existing function
entry. A direct call with no captures can feed the original arguments into
native scratch storage and write the result into the caller's destination
without a callee frame. Full
instruction-site recording uses the slot executable so its original site IDs
remain meaningful.

A compact `WholeSsa` section carries these typed bodies in relocatable and
bound artifacts. Existing artifacts omit the section and run as before on the
new reader. An older reader that has not learned the section cannot read a
new artifact containing it. Decode checks the scalar signature, operands and
definite initialization over the control-flow edges once. The body contains
no machine code, host pointers, or runtime dependencies. Direct call operands
carry function IDs, not relocated host addresses.
This follows the separation of SSA and physical registers documented in
[LLVM code generation](https://llvm.org/docs/CodeGenerator.html#machine-code-in-ssa-form).

The existing NativeJit `merge` fixture (2,000,000 iterations, initial 1.125) passed
its binary64 oracle on both x64 Native AOT CLIs. Five alternating source-route
pairs pinned to CPU 6 with telemetry disabled moved process medians from
150.1 to 53.7 ms (paired median -62.3%). A separate profile shows one
`accumulate` machine entry, 702 bytes of code, 488 cancellation polls and no
slot instruction dispatches in that function. The old route reported
24,000,012 logical instructions in the function and also reported zero
TierBridge entries; this isolates bypassing dispatch, not an application-wide
bridge reduction.

Detached Native AOT verification used the same new executable for both
artifacts. The old `merge` artifact bound and ran without an SSA body. The
new artifact bound after the temporary source was removed and ran both as
`.mso` and `.mexe`, each matching the existing binary64 oracle. The optional
section added 80 bytes to both artifact stages. For 2,000,000 iterations,
five telemetry-off CPU-6 pairs measured 121.5 to 37.6 ms median whole-process
time (paired median -69.0%). The bound profile recorded one frameless
`accumulate` execution, 702 machine-code bytes, 488 cancellation polls, and
zero TierBridge entries; the old artifact also reported zero TierBridge
entries. The existing detached `estimate` fixture matched its oracle and
recorded 31 frameless entries during warmup of the older caller tier.

#### Direct whole-function machine calls

The source call's evaluated descriptor must denote the same capture-free
function as its static target. Selection admits it into an SSA body only if
the target also has an SSA body and can complete without returning a loop
continuation. This first linker accepts forward-control-flow leaves. At JIT
publication it resolves each target once and retains its executable page for
the caller's lifetime. Machine code passes parameters through native stack
scratch and executes a native call without an interpreter handle or callee
VM frame. The state-buffer entry follows the
[System V x64 ABI](https://refspecs.linuxfoundation.org/elf/x86_64-SysV-psABI.pdf).
A leaf whose values fit in the 14 allocatable XMM lanes uses a private register
entry instead: arguments arrive in XMM2 onward, literals are created in the
leaf, and the result comes back in XMM0. The host adapter and native callers
share the same emitted body. A parallel argument copy uses a scratch register
to break cycles. Wider leaves keep the native state-buffer ABI.

The emitter computes backwards liveness over the selected control-flow graph
only for functions with native calls. It saves the intersection of live
registers after each call with the lanes that its callee can overwrite; phi
definitions and uses belong to edges. This is the same distinction between
live ranges and a call's physical register mask used in
[LLVM machine code generation](https://llvm.org/docs/CodeGenerator.html#call-clobbered-registers).
The optional profiler records actual register calls separately from total
machine calls and SSA entries.
Guest pointer fields and nonleaf resumable native calls are not yet supported.

The detached Native AOT `estimate` fixture runs 8,192 `estimate → step`
machine calls through one linked site and no SSA entry redirects. The same
executable running the previous bound artifact recorded 32,353 bridge
entries; the new artifact recorded 24,192. Both the new relocatable and bound
images passed the existing oracle after the temporary source was removed.
The previous whole-SSA `merge` executable also retained its output. Seven
telemetry-off pairs pinned to CPU 6 gave 175.1 to 164.6 ms process medians
(paired median -6.0%, five faster pairs).

On the same detached `estimate.mexe`, the register-entry revision executed
all 8,192 calls through one register site, with three caller-save slots there.
The caller's emitted body shrank from 449 to 256 bytes. Both AOT binaries
recorded 24,192 TierBridge entries in the separate profile runs. Eleven
alternating CPU-6 pairs with telemetry disabled measured process medians of
148.9 to 138.7 ms (paired median -6.1%, seven faster pairs). The profile run's
execution span was slower on the candidate, so the process result should
not be interpreted as a precise per-call estimate. The relocatable artifact,
the old bound estimate, and the existing one-million-iteration bound merge
fixture all passed their oracles. Receipt: `.temp/ssa-register-call/results.json`.

The standard seed-17 collection workloads do not enter this whole-function
SSA tier. The register-entry image's telemetry-off bound medians were 2.762 s
for B+, 4.358 s for Set and 1.190 s for Heap. Separate bound profiles show
2.07 M, 2.43 M and 1.14 M bridge invocations respectively; tier compilation
is 121, 39 and 47 ms in those profiles. These counts are not CPU time.
Four source-mapped variants of `_Chain::at` account for 9.0 M of B+'s 18.5 M
logical visits: page keys and children are linked chains even though the
search indexes them. Linked-node `_heap_link`, `_heap_value` and
`_heap_node` account for 3.97 M of Heap's 6.62 M visits. Set's ordered-tree
helpers are hot, but the end-to-end fixture also submits 12,288 separate
line reads. The console reader schedules each read with `Task.Run`; the main
thread's demand-wait stack is common with one CPU, but only 29/432 samples
with a pipe and four CPUs. Do not extrapolate the one-CPU wait share.
Receipts: `.temp/benchmarks/full-ssa-register-call/applications/result.json`
and `.temp/benchmarks/collection-cost-register-ssa/`. Source phase spans
come from the lossless phases-only replay, not the overloaded profile replay.

`NativeImage`, `FrameAllocation` and `NativeExecutionFunction` no longer create
`ProgramFacts`, `NativeEscapeAnalysis`, or invoke `NativeAllocationSafety` /
`NativeCallFacts`. Physical ABI layout and machine-code register allocation remain
backend work; semantic ownership and borrowing are compiler decisions. Existing
allocation descriptions remain inspectable, while the compiled instruction flag
controls frame eligibility. There is no read-time allocation-hint normalization.

### Deliberate artifact break for the 0.9 release

At the user's explicit direction, old artifacts are not supported by this
release. Native artifact format **2** / compact revision **2** requires storage
plans; HIR artifact format **4** requires recompilation/export from source.
Native format 1, compact revision 1, unpooled encoding detection, and format-6
CLI imports are removed. The old bytecode reader and validator are deleted.
Source-side HIR storage-adapter emission still shares historical emitter helpers;
those are not an accepted old-artifact input path. Language grammar identity is
unchanged by the storage ABI change.

`NativeArtifact.Read` and `HirArtifact.Read` decode their current formats without
rerunning graph/type/flow verification or changing allocation decisions. `NativeArtifact.Validate` and `HirValidation.Validate` remain explicitly callable
compiler/test diagnostics. Neither encoder implicitly invokes them. Loading is not an alternative
compiler. Raw IR test/host producers must finalize storage before execution or
encoding, just as the source compiler does; there is no missing-plan fallback.

### Calls and tail handoff

Call sites consume the encoded snapshot/borrow plan instead of constructing
`NativeCallSite` analysis objects or comparing caller/callee storage types at
link time. Borrow compatibility compares physical carrier kinds, not logical
layout domains; moving the decision must not narrow the existing ABI contract. Snapshot arrays belong to the caller frame and are reused only after
the callee finishes. Native call materialization detaches these arrays because a
native function may retain arguments in a returned lazy value.

Tail diagnostics carry immutable function/instruction context, not the previous
Activation. All normal completions, including asynchronous ones, return the
Activation. A tail argument buffer is leased from the target execution function;
the next argument set is copied **before** returning the old buffer or frame.
Self-tail calls therefore alternate two buffers, preserving parallel argument
permutations even when parameters borrow their input cells. One spare buffer per
function bounds retention; concurrent active calls never share a leased buffer.
Frame storage can still escape under the compiled plan, in which case its
language storage is not reset/recycled. This work does not yet replace the frame
object with a segmented byte/root execution stack.

Compiler `compiler.storage.analysis` spans describe O0/raw/template plan construction
when there is no converged optimizer analysis. Optimized functions reuse their
existing analysis. `compiler.storage.function` spans retain function identity and counts of
slots, escaping locations, borrowable parameters, private arguments, frame alias
sites, narrow-layout decisions and frame allocations. `compiler.storage.calls`
records each function's call-site, snapshot and borrowed-argument decisions. Runtime function counters
add `call_argument_snapshot_allocations` and `tail_argument_buffer_reuses`; the
existing snapshot/copy counts continue to describe semantic work, not allocation.

The ownership approach is consistent with frame-limited reuse rather than
unbounded caches: [Microsoft Research's frame-limited reuse report](https://www.microsoft.com/en-us/research/wp-content/uploads/2021/11/flreuse-tr.pdf).
The synchronous/asynchronous split retains a single execution semantics and only
captures suspended work when needed, matching the engineering guidance on
[ValueTask completion](https://devblogs.microsoft.com/dotnet/understanding-the-whys-whats-and-whens-of-valuetask/).

## Synchronous guest execution and Future-owned I/O (2026-11-14)

The user clarified the language contract: ordinary external program calls are
synchronous by default; existing acquire/release constructs delimit explicit
ordering and completion requirements. Guest functions do not promise coroutine
semantics or host-thread affinity. Do not propagate C# async/ValueTask invocation
protocols through all guest calls merely because an operand can be pending.

The selected architecture direction is a synchronous guest evaluator plus a
background I/O/dependency executor. Existing `NativeDatum` already represents
ready values and Futures. Ready operations execute immediately; pending pure data
operations can create dependency nodes. Actual control/value demand waits for the
relevant result. The evaluator thread may park; the dependency/I/O executor must
remain able to make progress. Submission does not imply lazy start of side effects,
and lazy result observation must not make default-synchronous process calls
asynchronous. A dependency edge can require completion without consuming output.

Jobs retain owned inputs/results, never live stack-frame wrappers or pointers to
non-escaping frame storage. Escaping objects/captured locations are heap-placed
by the compiler; ordinary locals stay on the execution stack. No general runtime
stack promotion or guest function continuation is required for this design.
Native async I/O may remain inside the host executor, rather than being mirrored
as a guest async call stack. The single dependency owner manages readiness,
resource admission, cancellation, completion, failure and existing acquire/release
edges; workers must not all block waiting for jobs queued to the same worker pool.

This supersedes the earlier assumption that ordinary frames must survive a guest
`await` suspension. It is an accepted implementation direction, not a claim that
the current async dispatch chain has already been removed. Current
`NativeDatum.Combine` already propagates pending operands, but does so using host
async tasks while guest calls independently use RunFunctionSync/async continuation
helpers; that duplicated representation is the next deletion target.

## Proposed backend boundary: mso to mexe representation selection (2026-11-14)

The user proposes that the mso-to-mexe backend select concrete instructions and
representations, including immediate payload versus typed Future/result storage,
while preserving source abstractions rather than a universal Value object ABI.
This is the recommended design direction; it is not implemented by the existing
storage flags alone. Current `NativePreparation` contextualizes selected work and
`NativeExecutionOperand.Link` resolves part of the physical ABI, so this requires
consolidating their responsibilities, not adding another interpreter.

Treat logical type, representation, readiness and storage lifetime as separate
compile/backend dimensions. A source T may lower to an unboxed T, a typed result
cell or a known finite sum where control flow genuinely mixes representations.
Use completion dependencies to establish readiness before direct payload loads;
do not choose a ready-only ABI from a guess that I/O will finish quickly. Pure
ready paths contain no universal Future check. Type-changing lowering must update
producers, uses, calls and return ABIs together. Default-synchronous process
completion and existing acquire/release effect edges remain semantic constraints.

Statically known Connection descriptions belong in an image resource table;
operations reference table symbols/relocations, not repeated descriptor objects.
The serialized descriptor and the live transport are distinct: mexe stores the
former and reserves runtime session slots for the latter, not socket handles or
process addresses. A loaded image/session owns mutable transport state. Dynamic
connection inputs populate the same resource table rather than falling back to
universal objects. Deduplication preserves semantic endpoint/configuration identity;
sharing a transport does not mean sharing one mutable command channel.

The target executable image should contain instruction/function code, physical
layouts and root maps, immutable constants/descriptors, resource-slot declarations,
I/O/dependency plans, relocations and source maps. Actual connections open when
selected work needs them, not merely because a descriptor was compiled. Loading
relocates and resolves symbols; it does not redo alias or representation analysis.

Relevant mechanisms: [MLIR type conversion](https://mlir.llvm.org/docs/DialectConversion/)
updates value/signature representations and their materialization boundaries;
[Call-by-Unboxed-Value](https://doi.org/10.1145/3674654) studies separating type
abstraction from concrete representations; [libssh sessions/channels](https://api.libssh.org/stable/libssh_tutor_guided_tour.html)
illustrate why a shared connection still needs operation-specific channels.

## Synchronous evaluator transition (2026-11-13, in progress)

The active objective is the full synchronous guest evaluator plus background
I/O/dependency execution, not just scalar specialization. The representation
batch now serializes backend handlers, numeric forms and endpoint resource
indices in compact revision 3. Escaping locations are allocated individually;
a single escaping location no longer disables reuse of the entire activation's
storage. This is not yet a segmented byte stack.

The next implementation removes `AwaitFunctionRunAsync`,
`AwaitFunctionFinishAsync`, `CompleteTailOrReturnAsync`, `AwaitActivationTierAsync`,
`AwaitActivationStepAsync`, and the duplicated pending-callee/result paths.
`RunFunction` and the baseline instruction loop return `NativeDatum` directly;
JIT helper demands park the evaluator rather than create a guest continuation.
Public execution runs on a default-scheduler LongRunning task, keeping the
waiting evaluator off the I/O completion pool and caller synchronization context.
Public host invocation retains its asynchronous API; internal callbacks use the
current evaluator context rather than spawning a thread per guest call.

`io.read_line` and `io.read_stdin` return typed Futures. A reader turn is reserved
before submitting physical reading to the background pool, since Console and
custom TextReader implementations can block synchronously inside an Async API.
A new CallLowering journey returns a pending input through a guest function,
observes `submitted` from the caller before supplying input, and then demands
and checks the returned value with both Off and Force execution.

This transition is incomplete: ordinary non-tail guest calls still recurse on
the host stack; some native/iterator helper state machines still hold frames;
filesystem scalar reads still need resource-dependency registration before they
can safely become Futures; unconsumed I/O ownership and background guest callback
execution need a unified owner. Do not mark the full goal complete from the
numeric benchmark or this one demand journey.

The threading choice follows the platform's
[LongRunning scheduler implementation](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/ThreadPoolTaskScheduler.cs).
It does not assert that blocking arbitrary thread-pool callbacks is starvation-free;
[the .NET starvation guidance](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation)
explains the remaining callback ownership concern. The existing scheduler note's
[A Granularity Characterization of Task Scheduling Effectiveness](https://arxiv.org/abs/2602.20561v2)
remains relevant to distinguishing submission cost from actual waits.

Evidence and continuation: `.temp/synchronous-evaluator/` contains the current
build, focused calls, native publication, scheduling and timing logs. The frozen
pre-evaluator image is `artifacts/native/evaluator-before/msync`; the candidate
is `artifacts/native/evaluator-sync/msync`. The typed measurement driver is
`tests/CallLowering/benchmark_evaluator.py`, with persistent output under
`artifacts/benchmarks/synchronous-evaluator/`. The older scheduling journey's
After-region parser expected positional disassembly; it now reads the actual
named `a`, `b`, and `symbol` fields without dropping its identity assertions.

### First evaluator receipt and regression investigation

The focused CallLowering tests, including the delayed-input returned-Future
journey, pass. Eight actual-process bound-executable cases pass: ordinary,
overlap, deferred-dataflow, selected, inherited-acquire, after-region,
acquire-break and resource-order. This establishes these cases, not full
scheduler or callback-liveness completion.

The first native receipt (one warmup, three alternating paired runs, exact stdout
oracles) is preserved as
`artifacts/benchmarks/synchronous-evaluator/pre-materialization.json`:

| Bound workload | Before | Synchronous transition |
| --- | ---: | ---: |
| 10,000 iterations, three O0 guest calls/iteration | 125.28 ms | 119.27 ms |
| O2 B+ inventory | 1810.50 ms | 2498.77 ms |

The B+ regression is unacceptable and unresolved by that receipt. Its profile
has no dispatch pauses or JIT suspensions, but the input is now a Future even
when EOF has already arrived. Inspection found that NativeMemory retained
successfully completed Futures as pending cells indefinitely; even ready reads
returned the Future carrier. This keeps global pending machinery active for
unrelated operations and can propagate completed dependency nodes.

The current change materializes a successfully completed result on ordinary
memory read/write. It does not add an asynchronous callback that writes into a
live frame, and does not erase Future ownership/failure accounting in work
scopes. Failed/cancelled results remain deferred until demanded. CallLowering
still passes after this change. A second native publish was started with log
`.temp/synchronous-evaluator/publish-materialization.log`; inspect its actual
process/result before running the next comparison. The first candidate binary
is retained as `artifacts/native/evaluator-sync/msync-pre-materialization`.
Do not report the regression fixed without the follow-up measurement.

### Next structural deletion

Replace recursive ordinary `RunFunction` calls with a single evaluator loop and
an explicit caller stack. A call instruction should yield a call request
(target/environment/compiled arguments/destination/resume PC); the loop pushes
the caller, enters the callee, and on return writes the result into the saved
caller destination. Tail calls replace the current frame and retain the existing
argument lease/diagnostic machinery. JIT helper exits need the same call-request
representation, not a second compiled calling pipeline. Error unwinding must
finish owned iterators/work and attach each saved call site without depending on
the host recursion stack. This is a required next step, not implemented by the
synchronous Demand adapter.

Follow-up receipt: materialization did **not** resolve the regression. The second
receipt is `materialization-measurements.json`: calls 95.78 -> 114.87 ms and B+
1991.07 -> 2731.96 ms. Sample variability makes the short call result inconclusive;
the repeated large B+ regression is real enough to require investigation. A
no-input B+ discriminator is prepared under `.temp/synchronous-evaluator/`
(`no-input.msync/.mso/.mexe`), substituting the known EOF seed `rng = 1` while
preserving the workload/output. It separates pending-input bookkeeping from the
synchronous call-loop change. Do not attribute the regression to completed
Futures without that discriminator and actual live-pending evidence.

The no-input discriminator completed in 1.916 s (1.882 s user CPU), exact B+
stdout matched. The ordinary candidate remains about 2.73 s. This substantially
narrows the regression to input-Future propagation/lifetime rather than the
synchronous call loop alone. `NativeMemory.Read/Write` materialization cannot
retire a completed pending entry that is never read again. Inspect the surviving
pending entries and the global `HasAnyPending` / `PendingRevision` path next;
do not paper over this by disabling input Futures. The requested final model
must keep unrelated ready computation free of global pending-value overhead.

### Readiness isolation and explicit guest calls (follow-up)

The initial pending-retention hypothesis was incomplete. Sampling the managed
workload showed zero pending cells; native receipts revealed the important
mechanism instead: before/sync profiles had 1,478/2,208,466 cancellation polls and
4.55M/2.28M raw scalar operations. `StoreTierResult` polls on every scalar helper
callback. Two overbroad decisions were found:

1. Machine code used one guard exit for both unavailable operands and mismatched
   types. `DemandExit` now preserves compiled code; only actual type guards
   invalidate it. Both x64 and ARM64 emit distinct exit stubs. A gated Force
   journey requires a readiness exit while requiring zero deoptimizations.
2. Fixed-slot stores used the VM-wide pending bit to enter a host callback even
   when the destination had no Future. They now inspect the destination's
   existing scalar descriptor. Ordinary memory lookups also short-circuit on
   allocation-local pending occupancy, not another allocation's pending cells.

The readiness-only native receipt is `readiness-measurements.json`: B+ improved
from the preceding 2.73 s candidate to 2.27 s, but not to the 1.89 s paired
control. Its profile has one readiness exit, zero deoptimizations, and still
2.21M polls; the fixed-destination store change is therefore independently
necessary. Do not conflate the two fixes.

`NativeInterpreter.Calls.cs` now owns an explicit list of caller records. Normal
Call instructions yield target/environment/arguments/destination/PC; the engine
pushes the current invocation and enters its callee. Both native helper exits
and interpreted calls use this same request. Returns resume the saved caller;
tail calls replace the current invocation. Error unwinding closes iterators/work
and releases frames in guest-stack order. There is no ordinary recursive
RunFunction call inside instruction dispatch. Host higher-order callbacks can
still enter a nested evaluator and remain a separate integration concern.

The focused test executes 20,000 non-tail calls with Off and Force, checks the
result, requires guest depth >= 20,000, and requires pushes == resumes. Deep-call,
closure/alias, delayed-demand and tail-storage/diagnostic tests pass before the
last fixed-store change. New telemetry: `guest_call_pushes`,
`guest_call_resumes`, `guest_max_call_depth`, and `jit_readiness_exits` per
function; `tier.readiness_exits` per execution. A candidate with the explicit
stack and destination-local store handling is being published as
`artifacts/native/evaluator-stack/msync`, log
`.temp/synchronous-evaluator/publish-stack.log`. Its performance is not yet
accepted. The complete goal remains active, including background callback and
I/O ownership, remaining frame-capturing helpers and broader scalar I/O Futures.

### Explicit-stack receipt and next required I/O case

`stack-measurements.json` records the native explicit-stack candidate against the
same frozen before image: the call workload medians are 105.34 / 124.47 ms and
B+ medians 1769.90 / 2139.81 ms (before / candidate). The regression is not
accepted. The new profile confirms readiness isolation itself is working:
4,551,635 direct operations are all raw stores; one readiness exit, zero type
invalidations, zero scalar-store callback polls. Native entries rise to 387,216
from about 222,166 because ordinary calls now leave and re-enter native regions.
The next call-cost investigation should target this repeated tier entry/linking,
not disable Futures or restore guest coroutines.

The final focused CallLowering run includes a live pending input during 1,024
ready loop iterations and requires RawOperations == DirectOperations, then
requires a readiness exit without deoptimization at actual demand. It also
includes the 20,000-call explicit-stack property. Both execution policies pass.
Eight actual-process bound scheduling cases still pass with the stack candidate.

A newly added `deferred-process-argument` real-process case exposes a missing
part of the requested semantics: `release python(..., background::stdout)`
currently resolves arguments before returning to the evaluator. The producer
waits for a gate published by the following instruction, so it times out before
the gate can be written. The journey fails with empty forwarded output rather
than `background`; see `.temp/synchronous-evaluator/deferred-process-argument.log`.
This is not a blocker requiring user input. Explicit release must submit a
process job with owned datum arguments and a Future<Process>, then allow the
following gate publication to run. Default calls must still await process exit.

Before implementing that submission, extend work ownership so a not-yet-created
process is owned through its submission Future: global/selected acquire, scope
exit, disposal and failures must account for it even when ProcessCreated fires
after the original scope starts finishing. Do not merely background the existing
frame-capturing ProcessCallAsync helper. Snapshot datum inputs at submission,
retain dependency identities and origin context, and register ownership before
starting the job. This ties the pending callback/I/O ownership work to a concrete
failed user journey rather than an architectural guess.

### Owned deferred process submissions

`NativeProcessSubmission` now owns admission cancellation, a result Future and
the eventual child lifetime. A released call snapshots its executable/argument
datums and current ordering predecessor, registers the submission Future in its
work scope, then queues launch on the background pool. No activation/frame is
captured by the job. Ready/default calls retain synchronous process completion;
release/acqrel calls return a typed Process Future before argument demand.

The job context preserves the external ProcessCreated observer but does not
register the child in an expired guest work scope when launch happens later.
The submission itself is the lifetime owner. Scope finish/global acquire wait
for admission and child completion; returning a derived Future promotes its
submission dependency to the parent. Selected process acquire follows symbolic
Future dependencies, including processes whose identity did not exist when a
transfer was submitted. Abort cancels queued admission or stops an already
launched child. Its own disposal cancellation is not reported as a second guest
failure. A direct cancellation-before-input test checks that a submission stored
in a returned scope can be disposed without ever invoking the host launcher.

The first integration run exposed an ordering cycle in acquire-break: deferred
native stdout waited for arguments, then consulted the *later* global ordering
value, which contained a transfer depending on that same stdout. Native datum
invocation now captures its ordering predecessor before argument resolution and
carries it into the deferred native operation. Range enumeration retains its
separate observation-time ordering behavior. This distinction is necessary
before expanding deferred filesystem scalar reads, whose resource versions
must likewise be captured at admission rather than looked up after later writes.

A launch failure can be observed at demand and again at scope cleanup. The guest
unwinder now preserves a single exception when both observations reference the
same failed job, rather than turning it into an unhandled duplicate aggregate.
The CLI reports the original process-call source span. Maintained acceptance:

- CallLowering: existing frame/alias/deep-call/readiness properties plus queued
  process disposal before input; current managed run passes.
- Real bound-process cases: deferred-process-argument, returned-process-submission,
  ordinary, selected, after-region and acquire-break pass with the native
  `artifacts/native/evaluator-submissions/msync` candidate.
- `submission_failures.py`: demanded and discarded-owned launch failures through
  source/bound routes pass with original file/line or bound source-offset
  attribution, and no duplicate unhandled AggregateException.
- Logs: `.temp/synchronous-evaluator/submission-ownership.log`,
  `submission-native-journeys.log`, `submission-native-failures.log`, and
  `submission-call-checks.log`.

The final telemetry addition is built in the managed CLI and checked with the
failure trace `submission-error.jsonl`. It is newer than the native submission
payload and must be included in the next native publication. `process.submit`
records argument/dependency counts, source and predecessor links;
`process.background` owns the complete background lifetime and outcome;
`process.launch` attributes resolved executable/admission failure separately.

The active goal is still incomplete: remaining work includes the measured
explicit-stack tier re-entry cost, ownership of unconsumed scalar input I/O,
resource-correct filesystem scalar Futures, and background guest-callback
execution. These submissions fix an actual deadlock but do not establish those
remaining contracts or remove the known numerical-throughput regression.

### Owned input Futures and continuation entry cost

Process/input submissions now share `NativeSubmission`, with explicit Process
and Input lifetime semantics. Unreachable input submissions are cancelled at
scope exit; reachable ones transfer through the existing Future reachability
closure. Disposal aborts owned work, including input. Input admission reserves
its order before dependencies run; an earlier failed/cancelled read must finish,
but its error is not spuriously inherited by a later independent read.

Physical TextReader work owns its reader turn until the physical operation
actually finishes. The guest waits on a cancellable task view. Thus an
uncooperative reader cannot hold a discarded guest Future or release the reader
turn early and permit concurrent reads. Cancellation does not undo physical I/O
that has already started. Those workers suppress execution-context flow and
capture only the reader, turn and cancellation token, rather than retaining
ambient guest work/generation contexts. The caller still owns the TextReader.

The new managed property uses an intentionally noncooperative reader: execution
returns before the physical read finishes, and a second execution does not start
a concurrent read. Existing delayed-return, queued-process cancellation,
20,000-call, alias and readiness properties pass. Four affected real process
ownership/acquire cases pass after the shared-submission change.

For the known call regression, the next candidate avoids reconstructing a native
invocation merely to execute a short caller return continuation: resumed callers
use the existing lowered handlers until a normal loop backedge promotes sustained
work again. Entry calls retain normal JIT selection. This adds no alternate
instruction representation, frame lifetime or proof system. Long straight-line
post-call regions are a trade-off to assess, not a claimed universal improvement.
Native publication: `artifacts/native/evaluator-input-owned/msync`, log
`.temp/synchronous-evaluator/publish-input-owned.log`. Measure before accepting.

The native input lifetime journey passes with stdin deliberately left open (no
input and no EOF), for source and bound artifacts; two submitted line reads
retain input order with O0/O2. Deferred process launch-error journeys also pass.
Managed properties cover a physical reader that ignores cancellation, a queued
second read, returned input Futures and disposal of a queued dependent process.

The cold-continuation experiment was **rejected and reverted in source**.
`input-cold-continuation-measurements.json` records calls 107.96 -> 153.33 ms and
B+ 2068.57 -> 2168.35 ms. It did not establish a B+ improvement and substantially
hurt the call workload. `evaluator-input-owned/msync` retains that experimental
entry policy; the current source restores native re-entry on ordinary resumes.
Do not use the experimental image as the final performance candidate.

A further concrete simplification candidate is work-scope ownership:
`NativeExecutionFunction` currently treats every guest Call as non-pure, even
when its compiled direct target only computes ordinary values. Consequently
`EnterGuest` allocates a work scope for ordinary transitive calls. Inspect and
reuse existing effect/call facts before refining this backend flag; do not add
runtime alias/escape analysis. If pure functions share an outer work scope,
TailCallPlan must distinguish the function's own work from unrelated outer work.
This is an investigation direction, not an implemented optimization.

### Compiled ownership and bounded direct calls

`NativeWorkPlanning` reuses native Effect/Behavior annotations and compiled
Call/dispatch targets. Reverse call-graph propagation identifies functions that
need their own work scope; pure/local-memory computation no longer gets a scope
merely because it calls another known function. Unknown/session targets and
actual I/O/callback/enumeration paths keep ownership. This is not alias/escape
analysis and the loader consumes the serialized flag without rebuilding it.
Tail eligibility tests the activation's own work, not unrelated work in a shared
outer scope. Effectful function factories/templates must not be classified by
name: tests inspect their actual instructions and target edges.

The same graph computes finite call depth. Cycles never receive a finite depth.
`DirectCall` backend handlers reuse the same function/frame/instruction engine
while preserving the caller native invocation for shallow, work-free targets.
Recursive, unknown and deeper targets retain explicit caller records. This is
bounded host nesting, not a restoration of recursive host execution for arbitrary
guest programs. Dynamic context-free leaves use the existing stronger leaf
contract. The 20,000-depth property and executable handler round trip pass.

Normal callable loads now use `NativeStorage.LoadFunctionDirect`, including the
physical union payload offset, rather than constructing and copying a full
NativeValue through ValueTask on every ready call. Pending callees still demand
their actual Future. The compiler owns callability/initialization; the typed load
does not revalidate those facts. Current managed alias/closure/argument tests and
native process ownership journeys pass. The recording-disabled native-call path
also no longer performs an ambient work-scope lookup solely for a no-op counter;
this last telemetry-only change is newer than the typed-callee native payload.

Receipts (100,000 loop iterations = 300,000 guest calls; one warmup, five
alternating measured pairs, exact output checks):

| Candidate against its paired frozen control | Call wall median | B+ wall median |
| --- | ---: | ---: |
| Typed callable + direct depth 1 | 899.74 -> 854.12 ms | 2005.35 -> 2168.16 ms |

See `typed-callee-measurements.json`, with binary/source hashes and CPU-time
samples. Earlier three-pair short call measurements were noisy; the driver now
accepts explicit binary paths/count/repetitions and records child CPU time too.
The longer controls still show a real B+ regression, not just scheduler delay.
The call improvement is not a completion claim for the overall goal.

Current source tests a depth-2 planning bound. A metadata-only experiment avoids
another AOT rebuild: PlanProbe re-lowers the same executable, asserts every
function contract and instruction field except Handler is identical, and changes
only three B+ call sites (46 direct sites total). Both plans execute with the
same `evaluator-typed-callee/msync` binary. Probe source/log and timing driver are
under `.temp/synchronous-evaluator/PlanProbe`, `depth2-bind.log`, and
`depth_benchmark.py`; the receipt is `direct-depth-measurements.json`. Inspect the
receipt before retaining the depth change; this experiment is not yet accepted
by the text above. Ordinary guest-call, background callback, filesystem Future
and frame-storage work remain distinct requirements.

The depth-2 metadata-only experiment is rejected: the paired B+ medians were
2286.32 / 2617.94 ms wall and 2279.49 / 2614.45 ms CPU for depth 1 / depth 2.
Source restores `DirectDepth = 1`. This supports retaining a genuinely shallow
fast route rather than expanding bounded host nesting to more complex callees.
The failed depth-2 artifact/receipt is retained for reproducibility, not shipped
as the current plan. Next performance work should profile the remaining entry,
argument transport and native-call costs rather than repeatedly raise that bound.


## Segmented private frame storage (2026-11-13)

NativeFrameStack owns nonmoving segments of raw bytes, initialization flags and
GC-visible roots. Synchronous guest evaluation pushes a layout extent and pops
to a saved segment/cursor. A physical thread owns its stack, including nested
calls from another interpreter; background callbacks enter the stack of their
executing thread. There is no guest continuation migration or frame copy.
Segments are retained for reuse on that thread, so residency follows its peak
stack demand. Defaults are 64 KiB raw/initialization space and 2,048 root entries;
an oversized frame gets a segment large enough for its compiled extent.

The existing NativeAllocation/NativeStorage descriptor binds to a segment slice
while the frame is active and detaches on return. Heap allocation retains its
independent backing. Escaping cells still use the compiled storage plan and
independent heap allocation; no new runtime escape/alias analysis is performed.
Session-preserving entry frames retain their persistent allocation behavior.
Frame/Activation descriptors and their existing pools are **not yet eliminated**.

Both interpreter storage operations and JIT pin leases use the slice origin.
Logical address identity and offsets remain allocation-relative; pointers into
two frames sharing a segment are not conflated. Return/unwind clears roots and
pending-value records before releasing the slice. Tail arguments are copied
before the caller slice is released. Constructor/argument-initialization failure
also releases a pushed slice.

The vm.function metrics stack_frames, stack_frame_bytes, stack_frame_roots
record cumulative admitted extents, not resident/peak memory;
stack_segment_allocations counts new segments attributed to the admitting
function. The disabled-profile path adds no recording work.

Focused validation: the Release tests/CallLowering runner passes including
20,000 non-tail calls across segments, repeated 3,000-frame string-root journeys
in Off/Force, returned closures, aliases, parameter borrowing, tail handoff and
pending input. The filesystem source/bound O0/O2 journeys also pass with
segmented backing. Native publication/paired timings are recorded below when
available; structural acceptance alone is not a speed claim.

Design context: [Wasmtime pooling](https://docs.wasmtime.dev/api/wasmtime/struct.PoolingAllocationConfig.html)
illustrates amortizing backing allocation, but its async fiber pool is not our
guest execution model. [Optimistic stack allocation (2024)](https://doi.org/10.1145/3656389)
combines static escape facts with dynamic heapification for managed open-world
execution. Here the artifact already supplies authoritative placement facts:
independent escaping cells are heap-allocated at creation, not discovered or
promoted by the runtime.

## Filesystem scalar Futures and native write admission (2026-11-13)

NativeSignature.ReturnsFileValue is shared by backend work-scope planning and
runtime dispatch. Scalar path reads become owned FileRead submissions. Inputs
and the current ordering predecessor are captured at admission; results are
demanded as data rather than suspending guest continuations. Unreachable owned
reads are cancelled at scope finish; returned Futures participate in ownership
promotion.

Resource leases and value completion are separate. Regular-file reads retain a
reader lease until completion, so a following native overwrite cannot change
the submitted read's version. Native writes register their resources before
waiting and remain synchronous at the guest call boundary. Nested read/transfer
admission excludes ancestor leases, allowing atomic self-stream transforms
without depending on their own writer. process.spawn declares executable-path
read access rather than a filesystem write.

For io.read_text, FIFO/character/socket refinement releases the reader lease
before consuming stream data so a producer can start. POSIX FIFO/character input
uses nonblocking open and poll-before-read, draining readable data before HUP
EOF and checking cancellation while waiting. See the
[POSIX poll contract](https://www.man7.org/linux/man-pages/man3/poll.3p.html).
This implementation uses worker tasks and polling, not a centralized scalable
readiness reactor. Windows stream refinement and remote cancellation have not
received equivalent execution coverage; unresolved paths still use wildcard
resource admission.

tests/FileReadFutures/journeys.py passes 20 Linux source/bound O0/O2 journeys:
read-before-overwrite, lexical path aliases, self-read/write, FIFO co-start and
discarded FIFO read without a producer. file_read.submit, file_read.background,
file_read.refine and native_io.admit preserve source, resource keys, dependencies
and refinement outcomes.

### First segmented-storage native receipt

The first published candidate is artifacts/native/evaluator-segmented/msync.
Five alternating pairs against evaluator-typed-callee are retained in
artifacts/benchmarks/synchronous-evaluator/segmented-measurements.json:
300,000 guest calls median 928.27 -> 979.11 ms; B+ median
2183.52 -> 2395.19 ms. Both outputs match exactly, but both workloads regress.
This comparison includes the filesystem submission changes since the frozen
typed-callee binary; it is not an isolated causal measurement of segmentation.

Code inspection found redundant full-frame span construction before each
field slice in the first implementation. NativeStorageSlice now keeps backing,
origin and logical extent explicitly so a field addresses its backing directly.
The first receipt remains a failed performance result, not an accepted speedup.
The next publication is evaluator-stack-slices; its receipt must be evaluated
before attributing any throughput effect to this change.

Five bound native process journeys also pass: deferred process argument,
returned process submission, selected acquire, after-region and acquire-break.

### Fixed-origin slice native receipt

The current native image is artifacts/native/evaluator-stack-slices/msync.
Five alternating pairs against the same typed-callee control are retained in
artifacts/benchmarks/synchronous-evaluator/stack-slices-measurements.json:
300,000 guest calls median 796.28 -> 820.81 ms (+3.1%);
B+ median 2085.47 -> 2067.57 ms (-0.9%). Run-to-run spread is substantial;
this does not establish a B+ improvement. Do not subtract medians from the first
segmented run to claim an isolated slice speedup: those were different runs.

The focused CallLowering journeys pass again with fixed-origin slices.
The implementation now achieves shared segmented backing, but does not yet
achieve lower call latency than the previous typed-callee implementation.
The remaining entry/return descriptor and context traffic is still an open
engineering task, as is the original end-to-end throughput acceptance.
The fixed-origin Native AOT image also passes all 20 filesystem Future journeys
(source and bound, O0/O2), including FIFO co-start and abandoned FIFO cancellation.

## Frame-owned activation descriptors and job-owned callbacks (2026-11-13)

The interpreter-wide Activation pool is deleted. Each NativeFrame permanently
owns its activation descriptor and immutable function/execution/iterator layout.
Only work ownership, result and operation-local state change per invocation.
Normal return and failure use ReleaseActivation: clear dynamic state, return
tail argument storage, then publish the frame to its existing pool. Publishing
before clearing would race another worker reusing the same frame. Function/frame
identity is no longer rebound twice per call. No second evaluator is introduced.

The guest loop tracks its installed work scope locally rather than reading an
AsyncLocal on each resume. Compiler-proven scopeless calls skip asynchronous
finish construction entirely; actual owned work and iterators retain cleanup.
Metrics activation_descriptor_allocations and activation_descriptor_reuses count
exactly one binding per call.

The isolated descriptor batch is frozen as evaluator-frame-activation.
Five alternating pairs against evaluator-stack-slices are in
artifacts/benchmarks/synchronous-evaluator/frame-activation-measurements.json:
300,000 calls 828.14 -> 789.70 ms (-4.6%), B+ 2069.09 -> 2142.06 ms (+3.5%).
The call improvement does not establish overall throughput acceptance.

### Transfer workers, scope exit and acquire

A minimal transform([./input], fn(path) { acquire; path }) -> ./out journey
hung in evaluator-frame-activation until the external 12-second supervisor
terminated it. Its inline callback inherited the submitting scope and could
wait for its own transfer Future. This is an ownership boundary defect, not a
reason to retain guest coroutine stacks.

Transfers now use NativeSubmission alongside process/input/file jobs, with
owned cancellation and a promise created before resource admission. Execution
is queued to the VM's worker pool. Each transfer installs a separate work scope
and directory context. Bare acquire joins local descendants plus the submitting
frontier captured before admission; it does not include its own Future or
later work that may depend on it. Lifetime promotion still knows the submitting owner:
objects/processes retained in callback captures can outlive the task without
losing identity. The task cleans up callback work and resource registrations
before publishing its result. Error reporting also completes or faults the
promise rather than leaving an unobserved worker failure with a pending Future.

A finished lexical scope forwards later promotions to its lifetime parent.
Root cleanup drains work added while earlier children were joining. Acquire
uses admission revisions to include children promoted while it awaited their
producer; duplicate registrations do not advance that revision.
These are dynamic task-ownership operations, not repeated escape/alias analysis.

Submission cancellation is checked inside each operation's cleanup region.
Skipping the launch delegate for an already-cancelled job would bypass file-read
lease release (and transfer registration cleanup). Transfer cancellation now
uses the same owned submission lifetime rather than an unowned fire-and-forget
Task. Input admission remains inline only until its first wait to preserve
reader reservation order.

New scheduling journeys exercise callback-local acquire and a returned transfer
whose callback publishes a released process through a captured mutable scope.
The source journeys and eight focused bound scheduling journeys passed before
the final consolidation onto NativeSubmission; final native receipts follow.
The file-read runner now also repeats scoped discarded reads followed by a
write, covering cancellation-before-launch lease release.

ExecutionContext flow is not lifetime ownership:
[the .NET async implementation account](https://devblogs.microsoft.com/dotnet/how-async-await-really-works/)
explains why an AsyncLocal captured by a worker survives lexical return.
[Promise ownership research](https://arxiv.org/abs/2101.01312) similarly separates
the task fulfilling a promise from tasks waiting on it. Here ownership is made
explicit at submission; no general dynamic deadlock detector is added.

Telemetry adds transfer.background (owned submission lifetime), transfer.execute
(source, dependency count and callback cleanup), plus work_forwarded_futures and
work_forwarded_processes on the former lexical owner's function profile.

### Owned-job native receipt

The consolidated image is artifacts/native/evaluator-owned-jobs/msync.
It passes eight focused bound scheduling journeys (including callback acquire
and returned callback ownership), 24 source/bound O0/O2 filesystem Future
journeys (including repeated scoped cancellation before overwrite), and
demanded/discarded process launch failures with original source attribution.
The earlier two new scheduling journeys also passed through the source route.
Native timings against the original evaluator-before control are recorded
separately below; no comparison across different benchmark runs is implied.

The owned-jobs image versus the original evaluator-before control has five
alternating pairs in
artifacts/benchmarks/synchronous-evaluator/owned-jobs-measurements.json:
300,000 guest calls 794.16 -> 769.99 ms (-3.0%), B+ 1860.58 -> 1992.59 ms
(+7.1%). This is not throughput acceptance.

## Native workspace lifetime follows the guest chain

RunFunction now lazily holds one TierInvocation workspace for its explicit
guest call/return chain. A nested bounded direct call gets its own chain and
workspace, so an active caller native invocation is never overwritten.
Each machine-code entry still releases pins and clears transient references;
only the shared-pool rental is moved out of the per-entry path. The chain
returns its workspace on both normal and exceptional exit.

The residual cross-chain pool uses ConcurrentBag rather than a linked-node
ConcurrentStack: array-backed per-thread reuse avoids a node allocation on
every return. This reuses the platform implementation rather than adding
another custom pool. See the
[upstream implementation](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Collections.Concurrent/src/System/Collections/Concurrent/ConcurrentBag.cs).

The deep-call property now checks that 20,000 non-tail recursive calls under
Force use exactly one tier_workspace_leases event and over 20,000
tier_workspace_reuses events. It passes along with existing return, alias,
readiness and cancellation journeys. These metrics attribute workspace reuse
to the function whose native code is entering, not just to the root caller.
Native publication is evaluator-tier-chain; its paired receipt follows.

### Pool experiment and inherited frontier refinement

The ConcurrentBag experiment is not the accepted pool implementation.
Against evaluator-owned-jobs, its isolated five-pair receipt
(tier-chain-isolated.json) gave calls 791.82 -> 875.75 ms (+10.6%) and
B+ 2111.29 -> 2103.27 ms (effectively flat). The earlier comparison to
evaluator-before also did not establish a benefit. Chain-scoped leasing is
retained, but the residual pool now uses one atomic spare slot and an overflow
ConcurrentStack, avoiding both unconditional linked-node pushes and a
thread-local bag lookup on ordinary rentals.

The callback boundary must not discard inherited acquire semantics. Admission
now captures the submitting work frontier before registering the job's own
Future. Callback acquire joins that inherited frontier plus its local children.
Completed successful work is not retained in this snapshot; running processes
whose handle Future is already ready are still included. Later submissions are
not accidentally made predecessors of an already-running callback.
callback-inherited-acquire checks an earlier released process from inside a
transform callback; it and the self-acquire/returned-owner/inherited-call cases
pass as bound native executions in evaluator-chain-frontier.

The next comparison records CPU affinity and rotates three frozen images over
six measured rounds: evaluator-before, evaluator-owned-jobs, and
evaluator-chain-frontier. A single allowed CPU reduces migration variation for
these CPU-bound evaluator workloads. This does not measure parallel I/O scaling.

Native perf sampling is unavailable in this container: perf stat on task-clock
fails with perf_event_paranoid=3. The installed dotnet-trace is 10.0.750501;
published default Native AOT images disable EventSource support. Do not claim
CPU samples were collected. The
[Native AOT diagnostics contract](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics)
distinguishes optional EventPipe events from platform-native CPU profiling.

### Single-CPU three-image receipt

artifacts/benchmarks/synchronous-evaluator/chain-frontier-pinned.json records six
order-rotated measured rounds, exact output checks, hashes and CPU affinity.
The columns are original evaluator-before, owned-jobs reference, chain-frontier:

| Workload | Original | Owned jobs | Chain/frontier |
| --- | ---: | ---: | ---: |
| 300,000 guest calls | 810.42 ms | 823.55 ms | 812.98 ms |
| B+ | 1915.18 ms | 2211.59 ms | 2181.63 ms |

Calls are effectively flat against the original; B+ still regresses by 13.9%.
Compared to owned-jobs within this run, the new pool/chain changes reduce the
medians modestly, not enough to establish the requested end-to-end improvement.
The complete goal remains active.

The next call-path investigation should target the remaining structural
boundary, not another interchangeable collection: bounded DirectCall handlers
still enter the full RunFunction guest-caller loop, create/copy GuestInvocation
state and execute its general unwind protocol even though the backend has
already proved finite, work-scope-free callees. A thin direct-call entry using
the same RunActivation engine could remove that overhead. Its required invariant
is that nested calls in such callees use compiled bounded entries, with tail
handling adjusted consistently in baseline and TierBridge. This is a proposed
next change, not an implemented fast path or a measured causal explanation of
the B+ regression.

## Thin direct-call entry and caller-owned returns (2026-11-13)

Compiled bounded DirectCall sites and context-free leaf calls now enter the
shared RunActivation engine through RunDirectFunction, not through the general
RunFunction caller-stack loop. EnterActivation, cleanup and frame release remain
shared. Known compiled direct dispatch no longer repeats arity admission.
Recursive/unknown targets retain the explicit guest stack. Backend DirectDepth
remains 1; no unbounded managed recursion is introduced.

Bounded tail-position calls stay direct in both baseline dispatch and TierBridge.
Their finite-depth proof is already compiler output; letting them install a
general tail handoff would violate the thin entry's contract. Profiling retains
the existing guest_direct_calls/guest_leaf_calls, storage and function metrics.

The first thin-entry image, evaluator-thin-direct, has a six-round single-CPU
receipt in thin-direct-pinned.json. Original / thin / chain-frontier medians:
300,000 calls 793.12 / 687.79 / 813.85 ms; B+ 1916.51 / 2068.02 / 2102.68 ms.
This establishes a meaningful call-workload improvement in that run, but B+
still regresses against the original.

The following return-slot batch changes the internal calling convention, not the
public NativeValue API. A bounded callee receives the caller's destination
NativeAddress. Baseline/bridge returns copy the compiled representation directly;
a pending value transfers its Future without demanding it. Native Number/Bool
returns write scalar bits, union tags and required root clearing directly,
without constructing a NativeValue/NativeDatum result for that boundary.
Generic entry/recursive results still use Activation.Result. The common engine
returns void rather than forwarding large structs through multiple helpers.

Return initialization and kind admissibility remain compiled facts; CopyReturn
does not repeat generic storage admission. Future readiness remains a dynamic
fact. Scoped profiling promotes reachable Futures before release; normal
work-free direct calls need no such scope. Source failures use shared cleanup.
This is a caller-owned result-slot design, analogous to the return-storage
conventions described by [LLVM's sret parameter attribute](https://llvm.org/docs/LangRef.html#parameter-attributes), not an LLVM backend.

Tests cover tail-position direct calls, Number/Bool/union returns, delayed String
and Number Futures through direct return, and source-attributed conversion failure
in Off/Force with profiling enabled and disabled. Existing aliases, escaping
closures and deep recursion continue to pass. direct_return_slots and
direct_return_scalars distinguish representation copies from raw native scalar
publication. evaluator-return-slots freezes this batch before readiness changes.

## Invocation-local pending footprint

Code inspection found another broad check: NativeTierMemory.Reset initialized
its coarse pending mask from the whole VM's HasAnyPending state and scanned fixed
slots even when the current frame had no pending values. A deferred stdin value
could therefore burden unrelated numeric callees.

The context mask now summarizes pending values in that invocation's actual slot
descriptors. Fixed slots are queried only when their frame allocation has pending
values; dynamic/bound slots inspect their concrete addresses. A transition from
no relevant pending values to some clears recycled fixed-slot pending flags
before publishing the first flag. This matters both at entry and when a binding
changes without creating a new Future. Dynamic descriptors remain initialized
normally. Revision-based refresh still catches actual newly pending operands;
commit no longer reintroduces the VM-wide bit.

The disabled-profile path only performs this narrower state maintenance.
tier_context_pending and tier_context_unrelated_pending expose actual versus
irrelevant pending contexts. PendingContextIsolation holds stdin unresolved,
calls a numeric function borrowing a ready slot from the same caller allocation,
checks its result and observes an unrelated-pending context. Delayed numeric
arguments still take the correct readiness path. The focused runner passes.
The published image is evaluator-pending-footprint; its three-image receipt
compares the original, return-slot, and invocation-local variants.

### Pending-footprint receipt

pending-footprint-pinned.json contains six rotated single-CPU rounds.
Original / return-slots / pending-footprint medians were 817.01 / 703.62 /
725.26 ms for 300,000 calls and 1815.07 / 2018.36 / 1949.48 ms for B+.
The footprint change reduced B+ time against its direct predecessor in this
run, but did not remove the original-baseline regression. The no-pending fast
path in dynamic descriptor refresh was subsequently restored: global emptiness
can skip a lookup without making global activity the context's readiness mask.

## Stack-admitted work-free recursion (2026-11-14)

PureSynchronous is the existing compiled work-scope-free property, not a promise
of immutability. These callees now use the thin direct entry whenever
RuntimeHelpers.TryEnsureSufficientExecutionStack succeeds. Bounded sites and
context-free leaves retain their existing finite-depth shortcut. No runtime
escape, alias or type analysis is added.

The [Native AOT implementation](https://source.dot.net/system.private.corelib/System/Runtime/CompilerServices/RuntimeHelpers.NativeAot.cs.html)
caches a thread stack limit and compares the current stack pointer. The runtime
reserve is for an ordinary nonrecursive framework call chain, not arbitrary
large stack allocations. We use it to stop extending guest recursion on the
host stack, then run the remaining subtree through the existing explicit guest
loop on the same thread. A direct activation never leaks a guest-call request
to its caller: it completes the loop handoff before resuming native code.

Recursive tail calls still use the constant-space tail loop. A direct activation
hands its tail chain to that loop, copies the final result to its caller-owned
destination and records skipped tail edges. This keeps the two reusable tail
argument buffers rather than growing a native frame per tail iteration.
The initial implementation exposed this regression in TailStorage; it was fixed,
not accepted by weakening the storage property.

guest_stack_limited_calls records unavailable native-stack admission;
guest_loop_handoffs records a direct activation handing a subtree/tail chain to
the explicit loop. Deep-call properties now count direct, explicit and handoff
transitions together rather than requiring every call to use one mechanism.
Workspace leases remain bounded by direct entries, handoffs and the root;
explicit descendants reuse their chain's workspace.

The focused runner passes, including 20,000 non-tail calls, 3,000-frame string
roots, tail argument reuse, deferred returns and source failures. The actual
Native AOT CLI also compiled/bound and ran this source in both Off and Force,
printing 20000 in both modes:

    fn descend(n: Number) { if n == 0 { return 0; }; descend(n - 1) + 1 };
    $std::io::println(descend(20000) as String);

The image is artifacts/native/evaluator-stack-admitted/msync. Source-free
HIR/object/bound storage journeys pass under O0/O2 and Off/Auto/Force. Four bound
callback journeys (self-acquire, inherited acquire, returned owner and ordinary
inherited caller acquire) also pass.

### Latest three-image performance receipt

stack-admitted-pinned.json contains six order-rotated single-CPU measured rounds
with one warmup, exact output checks and binary/source hashes.

| Workload | Original evaluator | Pending-footprint | Stack-admitted |
| --- | ---: | ---: | ---: |
| 300,000 guest calls | 844.60 ms | 740.73 ms | 672.32 ms |
| B+ | 1875.85 ms | 2010.66 ms | 1901.84 ms |

The call workload improves by 20.4% comparing medians, and wins every matched
round (median paired change -19.25%). Do not call B+ a speedup or proven parity:
its aggregate medians differ by +1.4%, but five of six matched rounds are slower
and the median paired change is +5.08%. The main regression is substantially
smaller than earlier stages, but complete throughput acceptance remains open.

A remaining ownership audit case is a worker inheriting an older job whose
callback publishes an escaped released child *after* the frontier snapshot.
The current snapshot contains the older job, not a causal descendant graph.
That exact journey has not yet been exercised; the existing returned-owner test
covers outer acquire/revision updates, not this nested-frontier case. Keep the
complete goal active until this boundary and remaining performance are resolved.

## Escaped descendants across captured frontiers (2026-11-14)

The previously open nested-frontier case is reproduced by
tests/CallLowering/descendant_frontier.py against evaluator-stack-admitted.
The source admits jobs A and B before stdin releases A's callback. A then
publishes a released child process through a captured scope. The child waits on
a test-owned file gate; B invokes bare acquire from its callback. The old image
prints acquired while that child remains gated.

Work frontiers now follow escaped descendant work from completed jobs:

    B's captured frontier -> A's completion -> A's escaped process/job P

The task's existing ownership cleanup records edges when work is promoted beyond
the job, including promotion through a closing job scope. It does not record
every operation ever created: a discarded input that was cancelled and handled
inside A must not be re-awaited as a failed child of A. Child collections are
allocated lazily, and iterative acquire traversal deduplicates Futures exposed
both as roots and through promotion. Data dependencies and process-connected
transfer selection remain separate from these lifetime edges.

The investigation also exposed a representation-sensitive ownership defect.
If a process submission had already materialized into a Process value in a
captured location, the reachable set contained that Process but no longer its
Future wrapper. Cleanup then treated the submission as unreachable and waited
for process exit before returning. Tracing changed timing and could hide this
case. NativeReachable.Retains now recognizes either representation by the actual
process identity. Future-versus-ready representation must not alter lifetime.

Managed source/bound O0/O2 journeys pass, including a deliberately discarded
second stdin read. The focused call runner and six selected bound scheduling
journeys also pass. Native publication is evaluator-descendant-frontier; native
receipts follow. This is a task-lifetime correction, not repeated compiler
allocation escape/alias analysis.

Telemetry: work.disposition reports actual promotion decisions and reachable
Future/Process counts; work.escape links the creator job and escaping work.
acquire_descendant_futures/processes count followed lifetime edges. Recording
callbacks run outside scope collection locks. A native-factory lazy initializer
is used rather than reflective construction, preserving Native AOT support.

### Native descendant-frontier receipt and remaining performance question

artifacts/native/evaluator-descendant-frontier/msync passes the gated descendant
journey as source/bound under O0/O2, plus demanded/owned process-failure source
attribution and open-stdin abandonment/ordered reads. This closes the concrete
nested-frontier audit case above. The gate test is an ownership property, not a
performance threshold. No new throughput claim is made for this lifetime-only
batch; the last measured call/B+ comparisons remain the stack-admitted receipt.

A concrete next performance probe follows from the actual B+ source: read_line's
result is tested through a derived is-null Future; the successful null branch
does not read the original input cell again. Successful completed wrappers can
therefore remain registered after their data has already been demanded.
NativeMemory.Read/Write already materialize successful completions when a cell
is visited, but tier readiness setup currently only tests wrapper presence.
Investigate materialization of successful completed Futures in compiler-proven
private fixed frame slots at native entry/refresh. Do not eagerly observe faults
or cancellation, and do not extend this to externally aliased cells without an
ownership basis. The existing compiled frame/storage facts, not a second escape
analysis, must determine eligibility. This is an investigation direction, not
an implemented optimization or an established explanation of all remaining B+
regression.

## Ready-slot measurement and stream-demand gap (2026-11-14)

NativeTierMemory now materializes successfully completed Future sidecars in
compiler-planned fixed private slots during readiness refresh. External/borrowed
slots retain their wrapper, and pending, failed, or cancelled tasks are not
observed by this path. This uses existing storage plans, not another escape
analysis. The focused NativeTiering --materialization runner passes five
properties; CallLowering passes. The published image is
artifacts/native/evaluator-ready-slots/msync.

The six-round, single-CPU, alternating three-image receipt is
artifacts/benchmarks/synchronous-evaluator/ready-slots-pinned.json. Relative to
evaluator-before, paired median wall-time changes are -14.24% for 300,000 guest
calls and +4.85% for B+. All six call pairs improve; five of six B+ pairs regress.
The immediately preceding descendant-frontier reference measures -15.40% and
+4.92%, respectively. This does not establish a throughput benefit from ready
materialization, nor does it close the end-to-end regression. Aggregate medians
must not replace paired comparisons. Do not claim general performance completion.

A direct audit of the original Future requirement found a separate functional
gap: TryRangeDatumAsync awaits SelectDatum/MatchDatum at the call site. A stream
reduction that needs external input therefore blocks the guest before the result
is demanded. tests/CallLowering/stream_demand.py reproduces this with:

    let count = ranges.size(ranges.filter([1], fn(value) { io.read_line() is null }));
    io.println("submitted");
    io.println(count as String);

The supervisor keeps stdin open and withholds EOF until after checking for the
submitted marker. evaluator-ready-slots fails all four source/bound O0/O2 routes;
after EOF it prints the correct marker and count. Thus the failure is submission
progress, not compilation, result computation, or ordinary startup failure.
Receipt: .temp/synchronous-evaluator/stream-demand-before.log. The maintained
probe is intentionally red until the missing semantics are implemented.

Implementation constraints established from current code:

- Explicit materialize remains an eager snapshot by the language contract.
- DatumRange.MayDefer defaults to true even for callback-based in-memory ranges;
  it is not proof of external I/O. Queuing every such range is not a justified
  performance fix.
- A reduction job must own callback work, cancellation, inherited acquire
  frontier, and escaping descendants, using the established submission/scope
  model. Merely wrapping an inline-started Task or Task.Run would leave these
  responsibilities with the caller.
- NativeInstructionFacts.EscapingUse already includes CallNative and
  NativeEscapeAnalysis traces callable captures and retained range elements.
  Reuse this compiled storage contract rather than introducing runtime alias or
  escape reanalysis. Check any narrowed compiler call contracts when extending
  the path.
- Source location and native operation name belong on admission/execution
  telemetry. A successful job publishes only after its callback scope cleanup.

The concrete next implementation target is deferred scalar range consumption,
with a ready in-memory path, not further unrelated call-stack machinery.

### Owned scalar stream jobs

NativeInterpreter.StreamJobs now submits potentially deferred scalar range
consumption through NativeSubmissionKind.Stream. Selection, cardinality, and
predicate reductions share this path; explicit materialize is not deferred.
Submissions capture the caller's acquire frontier before registering themselves,
carry source/native-name telemetry, and execute under a child work scope. The
scope is cleaned before publishing the result. Discarded reductions cancel their
owned work rather than retaining an open input read indefinitely. Reachable
callback results and captures use the existing promotion/descendant mechanism.

Derived range readiness now propagates from input ranges and callbacks instead
of unconditionally setting MayDefer. Context-free callbacks with ready captures
stay inline; pending captures are discovered through the existing Future-root
walker. This is dynamic readiness, not a new allocation escape or alias proof.
The compiled storage plan still owns allocation placement. Function callbacks
not classified context-free remain potentially deferred; narrowing that class
using compiler effect information is a possible follow-up, not a measured win.

Element-returning reductions preserve the source range's physical item carrier
and logical item type in the Future. A generic Any carrier caused an actual
Number-location store failure in the returned-capture journey and was corrected.

Managed CLI passes 16 source/bound O0/O2 gated paths: filter-size, predicate-any,
captured pending input, and returned closure capture. Discarded reduction exits
with stdin still open. The focused CallLowering runner also passes. Native AOT
publication evaluator-stream-jobs and its measurements are pending below; this
batch does not establish full-goal completion or resolve B+ regression.

### Native stream-job receipt: deferred-producer classification is not sufficient

The Native AOT evaluator-stream-jobs image passes all 20 gated source/bound
O0/O2 paths (including callback acquire plus caller acquire), abandonment with
open stdin, and the explicitly eager materialize check. However the added ready
captured-value property exposes a real semantic regression:

    let bias = 1;
    let result = ranges.first(ranges.transform([2], fn(value) {value + bias}));
    bias = 9;
    println(result as String);

The previous evaluator-ready-slots image prints 3; the new image prints 11.
This is NOT an accepted change. The new stream_demand runner exits nonzero on
this counterexample. Native receipt: .temp/synchronous-evaluator/stream-demand-native.log.

Cause: source-level range-returning calls are represented by DeferRange producer
thunks. MakeDeferredRange always registers MayDefer=true. This outer producer
hides the ready inner transform, so the job is queued before observing bias.
Improving DatumRangeValue's callback readiness alone does not fix the outer
producer. ContextFree also means absence of calls, not absence of external I/O:
a thunk calling ranges.transform is not context-free even when construction and
consumption are entirely in memory. Therefore current metadata cannot be used as
an exact I/O capability summary.

Next work must separate producer evaluation, in-memory callback effects, and
external waiting using compiler-owned effect information (reuse existing facts
where available). Do not solve this by cloning closure locations, freezing all
aliases, disabling the pure-state property, or duplicating runtime escape
analysis. The original stream-Future progress and the ready-state observation
must both hold. Changes remain an unfinished working-tree implementation;
throughput measurements of this image cannot establish acceptance.

The unfinished stream-job image also completed the six-round pinned throughput
comparison, with exact workload stdout checks. Receipt:
artifacts/benchmarks/synchronous-evaluator/stream-jobs-pinned.json.
Paired median changes against evaluator-before: guest calls -9.13%, B+ +5.29%.
The ready-slots reference in the same run measures -10.18% and +5.51%.
These results show no established resolution of the B+ regression. They must
not be treated as acceptance of the stream-job image, whose ready captured-state
counterexample above still fails. No benchmark or publication process remains
running for this receipt.

## Stream admission at actual demand (2026-11-14)

The preceding proposal to solve every case through a new effect summary is not
required for correctness. The representation can separate scheduling eligibility
from the point where the caller may proceed. A potentially deferred scalar stream
job now has an admission rendezvous: its caller waits until the job either
finishes its ready prefix completely or actually demands an incomplete operation.
The background job owns its physical execution stack throughout; the caller does
not become a coroutine and closures are not cloned.

NativeSubmission's per-stream admission promise reports completed versus pending.
The completed outcome returns the concrete value (or raises the already-produced
error) at the call. The pending outcome returns the typed Future. Native Demand
and the scalar stream consumer's awaited enumeration/result observe incomplete
operations and release admission once. Other submission kinds clear the inherited
demand owner, so unrelated background input/process tasks cannot release a
stream's caller. We deliberately do not notify from every Future.CompleteAsync:
eagerly-created dataflow mappings can await inputs without the guest demanding
their values. Such awaits must not release the caller early.

This maintains ordinary state observation before the first actual wait. A gated
prefix-state journey assigns state=2 in the callback before reading stdin; the
caller must see 2 upon submission return, then changes the same cell to 3. After
EOF the callback reads 3, proving both prefix ordering and shared alias identity.
The ready-state counterexample now returns 3 rather than 11. Pending element
selection has a separate demand boundary even when enumeration itself completed.

Related production precedents are Kotlin CoroutineStart.UNDISPATCHED
(https://kotlinlang.org/api/kotlinx.coroutines/kotlinx-coroutines-core/kotlinx.coroutines/-coroutine-start/-u-n-d-i-s-p-a-t-c-h-e-d/)
and Python's eager task factory
(https://docs.python.org/3.12/library/asyncio-task.html#eager-task-factory): both
preserve an initial execution prefix until suspension. Msync differs by placing
that prefix on a worker stack and synchronizing admission, rather than executing
a coroutine in the caller thread. This costs a worker handoff for opaque deferred
producers; existing ready ranges retain the inline path. Measure this cost rather
than claiming an allocation or throughput win from the conceptual similarity.

Managed gated paths pass after removal of the overly broad Future notification.
Native publication evaluator-stream-admission and an isolated final receipt are
pending. Two intermediate managed probe invocations accidentally shared the same
fixture paths/log; their overlapping bound-artifact observations are discarded,
not treated as product failures or validation evidence. The native receipt must
run alone against the current published image.

### Isolated Native AOT admission receipt

artifacts/native/evaluator-stream-admission/msync passes all 32 gated
source/bound O0/O2 routes in stream_demand.py: filter cardinality, ready captured
state, pending element selection, predicate query, callback/caller acquire,
captured pending input, state writes before and after first wait, and returned
closure captures. Open-stdin abandonment, eager materialization, and immediate
ready failure ordering also pass. The focused CallLowering runner passes with
the updated runtime. Logs: .temp/synchronous-evaluator/admission-demand-native.log
and admission-calls.log. No overlapping fixture invocations were used for this
receipt.

The CLI probe now uses unbuffered stdout: mixing buffered readline for the first
marker with subprocess.communicate could otherwise lose the already-buffered
second line when ready results printed both lines together. Direct CLI output
confirmed the correct value; this harness issue was fixed rather than altering
runtime timing.

A ready-completed admission returns its concrete payload directly and propagates
an immediate failure at the call. An admission released by a pending demand
returns the Future even if the operation wins the race and finishes immediately
afterwards. The one-shot boolean admission result, rather than a later sampling
of task status, determines this distinction.

Six-round pinned throughput measurement is running for the stream-admission
image. Its result will be recorded below; no end-to-end speedup is asserted here.

### Admission throughput and telemetry receipt

The completed six-round, pinned-CPU receipt is
artifacts/benchmarks/synchronous-evaluator/stream-admission-pinned.json.
Against evaluator-before, paired median wall-time changes are -19.15% for the
300,000-call workload and +7.66% for B+. The ready-slots reference measured in
the same rounds is -16.95% and +6.81%. Aggregate medians are 833.62/673.84/677.24
ms for calls and 1806.93/1983.43/1923.64 ms for B+ (before/admission/reference).
All workload outputs match. This closes neither the B+ performance regression
nor the full goal; the stream semantic fix is not advertised as a throughput win.

Diagnostics traces for ready-state and an input-gated filter separately confirm
stream.admission outcomes completed and pending, with source spans and dependency
linkage, and stream.execute callback.work_finished=1. Receipts are
.temp/synchronous-evaluator/admission-ready-trace.json and
admission-pending-trace.json. These instrumented runs are not timing samples.

### B+ work-count probe after admission measurement

A separate diagnostics run of bplus-sync.mexe with evaluator-stream-admission
records zero stream.submit events. The B+ regression cannot be attributed to
executing the newly introduced stream admission path on this workload. Receipt:
.temp/synchronous-evaluator/admission-bplus-trace.json (about 6.9 MB); this is a
work-count probe, not a timing comparison.

Function 18 runs 58,668 times and leases a native workspace 58,637 times, with no
workspace reuse or pending-context entries. Function 17 runs 55,458 times and
makes 55,458 direct calls, while function 20 records 57,792 workspace reuses.
Function 26 similarly runs 7,853 times and leases 7,822 workspaces. These counts
identify direct-chain workspace ownership as a concrete next inspection target;
they do not prove workspace leasing is the dominant wall-time cost. Check whether
interpreter-side direct entry can reuse an inactive parent workspace, preserving
native pin/context lifetime and rebuilding caller state at reentry, before adding
more pools or repeating readiness changes.

## Direct child workspace ownership (2026-11-14)

Inspection of TierBridge rules out simply lending a parent's workspace to a
direct callee: the parent machine code, pinned frame addresses, bridge context,
and return continuation can remain live during ExecuteLinked. The implemented
change instead makes each leased TierInvocation retain one child workspace for
sequential direct calls. Nested native execution remains disjoint; sibling calls
reuse that child. The root still enters the existing interpreter pool once per
explicit guest chain. No second pool, runtime alias analysis, or context copying
is introduced.

Activation.Tier names the chain workspace it last entered; activation cleanup
clears that reference before publishing its frame back to the pool. A direct
callee starts with the parent's cached Child, lazily obtains one only on native
entry, and returns it to that parent rather than to the global pool. Each native
entry still resets its own context and each exit clears transient references and
pins. Disposal iteratively frees the owned child chain, avoiding recursive cleanup.

Existing per-function tier_workspace_leases/reuses observe the actual native
entry decision, including child-cache reuse. The focused CallLowering journey
passes, including 20,000-deep mixed calls and reference-root retention. The new
--workspace property compares 32 and 1024 iterations of direct sibling calls:
results retain live parent arithmetic, direct dispatch is exercised, and workspace
leases do not grow with iteration count. It passes. These are ownership and
work-count properties, not performance thresholds.

Native publication evaluator-tier-child, diagnostics and paired measurements
follow. Do not infer a throughput improvement from reduced lease counts alone.

### Child-cache experiment rejected after measurement

The evaluator-tier-child image passes the native stream suite and forced-native
20,000-deep recursion; its diagnostic B+ output matches the expected output.
B+ workspace leases fall from 77,442 to 9,467 (reuse rises from 239,647 to 307,622).
Function 18 leases fall from 58,637 to 1,200; function 26 still leases 7,822.
This establishes the mechanism, not a throughput benefit.

Six-round pinned receipt: artifacts/benchmarks/synchronous-evaluator/tier-child-pinned.json.
Relative to evaluator-stream-admission in the same rounds, child-cache paired
median changes are -0.87% for calls and +0.38% for B+; five of six B+ pairs are
slower. Relative to evaluator-before they are -17.68% and +8.74%. No meaningful
end-to-end benefit is established by eliminating about 88% of workspace leases.
The hierarchical cache, activation field, and visibility changes were therefore
removed from production code. Retain the simpler existing pool/chain model.
The frozen experimental binary/receipt permit revisiting the result without
keeping speculative machinery in the product.

CallLowering --workspace remains a focused arithmetic/dispatch journey and
reports 32/1024-iteration lease counts. It no longer asserts a cache-specific
implementation policy and is not added to the default runner. Actual parent/
child arithmetic preservation remains required. Next performance work should
sample host CPU attribution rather than infer CPU cost from another large count.

## Native instruction attribution, not another counter hypothesis

Kernel perf sampling remains unavailable (perf_event_paranoid=3). Official .NET
Native AOT diagnostics distinguish EventPipe event collection from OS-native CPU
sampling: https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/diagnostics.
The managed dotnet-trace fallback was therefore used only as a proxy. With
--show-child-io and stdin EOF it completed the real B+ workload; without that
flag the trace tool kept its child's input pipe open, and that initial idle trace
is discarded. The execution-thread proxy is strongly weighted to PollGC and
RunTierSync, so safe-point bias prevents treating it as Native AOT CPU attribution
(https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-highcpu).

Callgrind 3.24 works on the actual frozen Native AOT images without perf privileges.
Both full B+ executions complete with the exact expected stdout and stdin EOF.
The typed maintained driver is tests/CallLowering/profile_evaluator.py; pass
--binary, --artifact, and --output (repository-local file stem). It persists
image/artifact hashes, output validation, stderr, raw profile, and annotation.
These are simulated instruction counts, not cycle counts or throughput timings.

Receipts live under artifacts/benchmarks/synchronous-evaluator/profiles/:

- native-bplus-before: 7,513,169,270 instructions.
- native-bplus-admission: 8,206,521,903 instructions (+9.23%).

The accepted admission image's exclusive instruction attribution includes:

| Native path | Instruction share |
| --- | ---: |
| TierBridge (both recursion contexts combined) | 20.97% |
| RhBulkMoveWithWriteBarrier (both listed forms combined) | 7.97% |
| NativeStorage.CopyTrusted | 5.18% |
| NativeMemory.TryNarrowReady | 3.29% |
| NativeMemory.TryCopyReady | 2.95% |
| NativeAddress.Allocation getter | 2.85% |
| NativeStorage.CopyKindTrusted | 2.83% |
| NativeMemory.TryPending | 1.85% |
| NativeAddress.Type getter | 1.50% |
| NativeStorage.RequireInitialized | 1.30% |
| NativeTierMemory.Reset | 0.75% |

This changes the next target: bridge-side representation copies and repeated
address/type access, not workspace pool ownership. Inspect the existing compiled
copy/narrow contracts and small GC-root copies before broad structural changes.
NativeRoot is a single overlapping GC-reference slot; Address/Function carriers
use two such slots, whereas Path uses five. Any copy specialization must retain
memmove-style overlap behavior and the platform write barrier. CopyTrusted also
repeats RequireInitialized after several runtime callers already established
readiness; distinguish public checked storage operations from compiled trusted
operations rather than dropping checks indiscriminately.

The baseline profile lacks managed symbol names because its frozen directory
contained only the executable. A matching debug file was located at
artifacts/native/backend-native/msync.dbg (ELF build ID
eaa0aa8f7288f4a096dd6ba3a17ac2d840b12b20 matches evaluator-before). Raw managed
profile addresses are relocated, so do not feed them directly to addr2line and
claim the resulting method names. The baseline total instruction count remains
usable; cross-image per-method attribution needs symbol-aware collection or
correct relocation mapping. No profiling process remains running.

## Representation-copy hot path (2026-11-14)

The next implementation targets measured instruction-heavy paths rather than
workspace ownership. NativeStorage.CopyTrusted no longer repeats initialization
validation: its five runtime call sites either test source readiness first or
use the compiled return contract, and public NativeStorage.Copy still validates
and checks initialization before entering the trusted helper. The public failure
behavior is unchanged; this is removal of a duplicate responsibility, not removal
of all storage checks.

One- and two-root carriers now copy GC references directly. NativeRoot gains an
object view at its existing overlapping reference offset (no representation-size
change). CopyRoots snapshots both references before either store, preserving
forward/backward root overlap. Assignments remain normal managed reference stores
with the platform write barrier. Wider carriers, notably five-root Paths, retain
Span.CopyTo. This does not substitute raw byte copying for GC-aware storage.
Allocation and Type address getters carry explicit inlining hints, preserving
exact exceptions while exposing their repeated loads to the AOT optimizer.

Observed native bridge Narrow/Convert outcomes report ready-copy root widths
(zero/one/two/bulk) or fallback through the existing per-function counters. The
Core storage layer does not gain per-copy callbacks or telemetry dependencies.
Recording-disabled execution does not compute the diagnostic root-width value.

Focused properties pass (NativeOptimization --storage-copy, 25 checks): scalar,
text, address, and Path carriers; fixed and narrowing copies; equal, forward,
and backward root positions; reference/pointee identity after GC; and public
uninitialized-copy rejection. These operate on trusted internal carrier slices
to exercise root overlap independently of raw-byte offsets. The CallLowering
user journeys also pass, including deep calls and retained string roots.

Publication evaluator-small-roots and end-to-end measurements follow. No speedup
is claimed from inlining hints or instruction-share estimates alone.

### Small-root Native AOT and paired throughput receipt

artifacts/native/evaluator-small-roots/msync passes all 32 source/bound stream
paths, abandonment/eager/ready-failure properties, and a forced-native double
3,000-frame string-root recursion (output 6000). The observed B+ run matches all
expected output. Its scoped Narrow/Convert counters report 2,084,426 ready
2-root copies, 57,858 zero-root copies, and 12 fallbacks; this confirms the actual
workload reaches the small-root path extensively. No one-root or bulk-root copy
is observed in that particular bridge subset.

Six-round pinned receipt:
artifacts/benchmarks/synchronous-evaluator/small-roots-pinned.json.
Against the immediate evaluator-stream-admission reference, B+ improves in all
six paired rounds, with paired median -10.82%; the call workload's paired median
is -4.86% (five of six rounds improve). Against evaluator-before, paired medians
are -20.21% for calls and -2.03% for B+. The B+ baseline comparison has three wins
and three losses, so describe it as restoring baseline-scale performance, not
as an established general B+ speedup. Aggregate medians (before/new/reference)
are 825.81/665.24/691.21 ms for calls and 1832.02/1772.87/2044.16 ms for B+.
All measured stdout checks pass. The improvement over the immediately preceding
implementation is substantially clearer than the previously rejected child-cache
experiment. Keep this representation-copy change pending the remaining full-goal
boundary audit; do not conflate this result with completion of every I/O contract.

A matching native instruction profile is being collected with the maintained
Callgrind driver to discriminate the mechanism from a timing-only correlation.

### Instruction-count confirmation

The final native-bplus-small-roots profile records 7,514,159,198 instructions,
down 8.44% from admission's 8,206,521,903 and within 0.013% of the original
7,513,169,270 baseline. Exact workload stdout passes. The two listed bulk
write-barrier forms fall from 654,293,022 to 216,081,773 instructions, while
ordinary reference-assignment barriers rise as expected. RequireInitialized
falls from 106,527,608 to 19,971,644 instructions. The private copying/getter work
moves into callers after inlining, so use total instruction cost and actual
throughput rather than the isolated CopyTrusted self percentage. These results
support the intended mechanism: small reference copies retain write barriers
without invoking the bulk helper, and checked/trusted initialization duties no
longer overlap.

## Final synchronous-evaluator / Future goal audit (2026-11-14)

Delivered native image: artifacts/native/evaluator-small-roots/msync.
SHA-256: bcc5b0a797f08663f663b5f96c8b765dc4905a588ab920861db5af5182d9fdd0.
Production source matches that image; subsequent additions are focused journey
coverage and documentation. No child-workspace cache experiment remains in the
production implementation.

| Requirement from the active goal | Implementation and authoritative evidence |
| --- | --- |
| Ordinary external programs remain synchronous | ProcessCallAsync's non-release branch explicitly demands launch and process exit. The final bound `ordinary` scheduling journey observes the finished child before continuing. |
| I/O-derived scalar values can be Futures | NativeCalls routes standard input and filesystem reads to owned submissions, and propagates pending arguments through scalar operations/process views. Input lifetime journeys, 24 filesystem Future journeys, and CallLowering delayed String/Number return journeys pass. |
| Stream scalar results wait only when needed | StreamJobs covers every registered scalar enumerating `ranges.*` operation; comparison `ranges.less` is not an enumerator and materialize remains eager. Native source/bound O0/O2 gate journeys prove progress before external input and eventual correct values. |
| Ready computations preserve source state and immediate errors | Admission waits for completion or the first actual incomplete demand. Ready-state, prefix-state/shared-alias, eager-materialize, and ready-error journeys pass. The delayed failure journeys additionally verify source attribution after input arrives, in all four source/bound O0/O2 routes. |
| Background jobs own dependency and resource lifetimes | NativeSubmission owns cancellation/completion; stream/transfer workers own child work scopes and admission-time acquire frontiers, and finish cleanup before publishing results. Both stream and transfer descendant-frontier journeys pass in all four routes, including a gated escaped process and an abandoned extra input read. |
| Acquire/release retain observable behavior | Final real-process journeys cover ordinary calls, returned process submissions, resource order, callback inherited acquire, and deferred process arguments. Demanded/owned process failure attribution and open-input cancellation/order also pass. |
| No main-thread guest coroutine contract | RunFunction/RunActivation are synchronous evaluators with explicit deep-call records; ExecuteAsync/InvokeAsync are host adapters. Native deep recursion, retained roots, direct Future returns, and detached executable journeys pass. Jobs hold their own physical execution stacks. |
| Remove blanket runtime responsibilities without breaking data identity | Storage/borrow/snapshot/escape facts remain compiler-owned. Segmented frames and independent escaping cells are exercised by CallLowering. The final trusted-copy change removes an already-established initialization check while retaining public checked-copy behavior, platform GC barriers, overlap behavior and pointee identity (25 focused properties plus native string-root recursion). |
| Obtain end-to-end performance results | small-roots-pinned.json contains six alternating, single-CPU Native AOT rounds, image hashes, exact output validation, wall/CPU times, and paired comparisons. Call workload improves by a paired median 20.21% against the original; B+ is back at baseline scale and improves 10.82% against the immediate previous implementation in all six rounds. Native instruction profiles corroborate the hot-path change. |
| Keep changed decisions observable | Existing source/function/opcode counters remain, with stream submission/execution/admission outcomes and escaped-work links. Final Narrow/Convert copy-width counters report actual ready/fallback decisions. Recording-disabled native journeys and benchmarks pass separately from diagnostics runs. |

Final boundary receipts under .temp/synchronous-evaluator/:
small-roots-stream.log, small-roots-stream-failures.log,
small-roots-stream-descendants.log, small-roots-transfer-descendants.log,
small-roots-input.log, small-roots-process-errors.log, small-roots-files.log,
small-roots-scheduling.log, small-roots-native-roots.out, and
small-roots-properties.log. The source of each maintained journey remains in
tests/CallLowering, tests/FileReadFutures,
tests/Msync.EndToEnd, or tests/NativeOptimization. Durable measurement payloads
are in artifacts/benchmarks/synchronous-evaluator, including profiles/.

The active goal's implementation, named boundary journeys, Native AOT publication
and performance deliverable are complete. No build, benchmark, or profiler from
this work remains running.


## Deletion-first runtime cleanup (2026-11-14)

Scope clarified by the user: runtime mechanisms and metadata only. Do not delete
repository data. The active cleanup includes redundant validation, submission
rendezvous, and generic representation/dispatch overhead; preserving essential GC
reference handling is a platform obligation, not a reason to retain custom guards.

First ownership change: FrameAllocation is a shared immutable descriptor rather
than a repeatedly copied struct. It owns the function definition and slot table.
NativeFrame now stores one descriptor reference instead of image, function index,
function definition and slot-table fields. Initialization and allocation lookup
use that owner directly; the unused image-level allocation lookup was removed.
Frame payload, bindings and escaped allocation ownership are unchanged. Existing
frame/activation telemetry remains attached to actual frame entry and reuse.
CallLowering's end-to-end/deep-call/reference-lifetime journeys pass after the
ownership change. No throughput gain is asserted yet.

The submission rendezvous is still present. Its removal must come with correct
placement of ready computation and I/O jobs, not another runtime handshake under
a different name. This first ownership change does not close the broader goal.

### Submission rendezvous deleted; classification compatibility still open

Removed NativeSubmission's admission promise, AsyncLocal demand owner,
WaitForAdmission/YieldAdmission/ObserveWait, stream admission events, and the
AwaitStreamDemand wrapper. SubmitStreamReduction now queues work and returns its
existing result Future. No replacement handshake was introduced.

NativeSignature.RequiresWorkScope centralizes the existing native work rule:
constructing a Deferred range does not invoke its callbacks. NativeWorkPlanning
uses this rule in its existing propagation pass, with a deferred-constructor
count in compiler telemetry. At scalar consumption, work-free range producers
are resolved using the stored PureSynchronous fact and actual pending captures;
resolved inner ranges then determine whether consumption needs a job. Producer
resolution no longer automatically forces every nested producer. Pure callbacks
use the existing work-scope fact rather than the stricter ContextFree hint.

Fresh source/bound O0/O2 paths pass: ready captured arithmetic, pure native math,
I/O reductions, pending elements, callback acquire, captured input, state visible
after get, returned captures, and delayed failures. The CallLowering runner and
four stream descendant-frontier paths also pass. Cancellation exposed duplicate
body/cleanup cancellation aggregation; cleanup cancellation under the already
cancelled invocation token no longer replaces the primary outcome with an
AggregateException. Independent cleanup failures remain observable.

The earlier prefix-state test imposed the rejected rendezvous policy. It now
checks shared-state visibility after get, not that a queued worker has already
performed its prefix before submission returns. Descendant tests accept either
order of worker-start and caller-submitted markers, but require both before
releasing the input gate and still hold the escaped child until acquire waits.

Important open compatibility reproduction: compile/bind ready-state.msync using
artifacts/native/evaluator-small-roots/msync, then execute that old .mexe with
the current managed CLI. It prints 11 rather than 3 because its old conservative
work-scope flags label a pure deferred constructor as work-owning. Files:
.temp/runtime-cleanup/old-ready.mso, old-ready.mexe, old-ready.out. Freshly compiled
artifacts print 3. This is an unresolved regression, not an accepted contract
change. Do not publish this intermediate as complete or restore the handshake
as a workaround. No artifact-format change or load-time replanning was added.

Remaining goal scope also includes compiled-versus-dynamic initialization/type/
layout duties and generic storage/bridge overhead. This deletion batch does not
close those items.

### Compiler-selected private copy path

Added CompiledCopy as an executable backend handler using existing facts only:
exact equal carrier types and fixed source/destination locations are already
established by lowering; PrivateArgument additionally proves that the source is
an unaddressed, nonescaping temporary. Register definite initialization is the
compiler's responsibility on this path. The handler tests actual Future presence,
then copies the known representation without initialization, kind-admission, or
layout checks. Dynamic/aliased operations retain their existing boundary path.
No runtime alias or escape analysis was introduced.

Removed the old final copy-fusion loop: NativeExecutionInstruction always
installs a nonzero Handler, so its Handler != 0 break made all rewrites unreachable.
The compiler now selects the explicit copy handler in that location instead.
`copy.compiled_sites` records backend sites; `compiled_copies` records executed
linked/bridge operations when profiling is enabled.

CallLowering passes. Its new --compiled-copy journey checks the compiler proof,
serializes/deserializes the backend handler, and executes the string-valued
conditional under Off and Force with real stdin EOF. Both execute the new path
and return the expected value. Logs: .temp/runtime-cleanup/compiled-copy-calls.log
and compiled-copy-proof.log. This is a scoped proof-backed deletion; generic
checks, initialization storage, and the older executable scheduling regression
remain open in the full cleanup goal. No new throughput claim is made.

### Private scalar initialization guards removed from machine code

X64 (System V/Windows) and ARM64 scalar loads and IsNull emission now consume the
existing PrivateArgument source fact for fixed cells: no initialization-byte
load/test/branch is emitted for those compiler-private temporaries. Real pending
Future checks and speculative kind guards remain separate. Dynamic/exposed cells
retain their boundary checks. This extends the compiled-copy contract into native
scalar execution without another flow or alias analysis.

Actual code generation reports jit_initialization_checks_elided through the
existing compilation statistics. NativeTiering --private-initialization compares
the same source with its private proof versus a conservative metadata copy:
all three target/ABI emissions omit checks and produce smaller code; Force runs
produce the same result and report the actual elisions. Five focused properties
pass, as do CallLowering's call/lifetime/delayed-value journeys. Receipts:
.temp/runtime-cleanup/private-init-proof.log and private-init-calls.log.

This batch removes emitted reads, not the initialization storage or its remaining
writers. Those still serve generic readers and must be disentangled before
storage removal. The old executable work-scope classification regression also
remains open; no rendezvous was restored and no load-time replanning was added.

### Checked decoding separated from compiled private reads

NativeStorage now separates Read's initialization boundary from ReadInitialized's
payload decoding. NativeMemory.TryRead uses the latter after its existing
initialization test instead of checking twice. NativeFrame.GetDatum selects
ReadCompiled from the already stored private-temporary fact; that path decodes
ready payloads without reading initialization state. Pending Future resolution
and successful materialization share ReadFuture with the ordinary path.

CallLowering and the serialized compiled-copy/read journey pass under Off/Force;
the observed journey confirms compiled_reads executes. Public read/copy rejection
and overlapping carrier-copy properties also pass (26 checks). The preparation
interpreter's Known path still uses TryRead; its one direct GetDatum call is
preceded by Known, so skipped partial-evaluation inputs do not become zero values.
No new initialization bitmap or runtime proof analysis was introduced. Flags and
writers remain until their remaining generic consumers are removed; the old
executable work-scope compatibility case remains unresolved.

### Compact-3 work-hint regression resolved

The frozen .temp/runtime-cleanup/old-ready.mexe now prints submitted/3, not 11,
without modifying the file or restoring a submission handshake. Native format 2
now writes compact revision 4 and reads revisions 3/4 through the same decoder.
The byte/section layout is unchanged. Revision 3 alone corrects conservative
PureSynchronous hints for deferred constructors; a revision-4 read does not run
this correction. Existing positive work-free facts remain authoritative.

The compiler's existing work-fact propagation was extracted into Core's
NativeWorkFacts and reused for that decode-only erratum. The importer changes
only false-to-true work-free hints. It does not rebind context, rerun optimization,
recompute escape/alias/storage/call plans, replace handlers, or change instructions.
There is no legacy execution pipeline. The transient analysis is not stored in
frames or runtime jobs. `compat.work_hints.corrected` attributes this old-payload
normalization; current payloads do not emit that metric.

A genuine compact-3 fixture from evaluator-small-roots is embedded in
tests/BackendLowering/Fixtures. Its
--legacy-work journey compares raw versus normalized instructions, handlers,
raw-write flags and storage/call plans; all are unchanged except the intended
work hints. Both normalized and reserialized artifacts execute with correct
output under Off/Force, and current-format decoding does not repeat normalization.
Receipt: .temp/runtime-cleanup/legacy-work-properties.log. This closes the known
old-executable regression, not the remaining initialization-storage cleanup.

The native runtime-cleanup publication also executes the untouched compact-3
fixture with submitted/3 and passes the fresh stream/delayed-failure journeys.
Receipts: .temp/runtime-cleanup/native-legacy-ready.out and native-stream.log.
The cumulative cleanup benchmark against evaluator-before and evaluator-small-roots
is in progress; no result is inferred from code deletion alone.

### Cumulative cleanup measurement

artifacts/benchmarks/synchronous-evaluator/runtime-cleanup-pinned.json records
six alternating pinned-CPU Native AOT rounds with evaluator-before as baseline
and evaluator-small-roots as immediate reference. New-image SHA-256:
4ca54f5706204a3191368bf3b526f27e41a1e55d6849ed9d2d8efd2425a1d9a0.
All workload stdout checks pass. Aggregate medians (before/new/reference):
793.55/634.63/652.39 ms for calls; 1697.33/1644.36/1654.62 ms for B+.
Paired changes versus the original baseline are -21.30% and -2.45% respectively.
Versus the immediate small-roots reference, paired medians are -0.85% for calls
and +1.12% for B+, with mixed round outcomes. Do not claim a clear additional
throughput win from these deletion batches. Their confirmed outcomes are reduced
runtime mechanisms/checks and restoration of old-artifact behavior. Initialization
storage and remaining dynamic-versus-compiled boundary work are still open.

### Duplicate image/backend metadata removed

Removed NativeImage's cloned admitted-layout arrays; admission reads the existing
immutable program type contracts. Instruction metadata already reused arrays in
the current tree, so no change was needed there (the initial inspection hypothesis
of another instruction-array copy was incorrect).

Loaded NativeExecutionFunction no longer copies per-instruction number forms,
connection indices, or RawWritable proofs. It reads the existing backend records
and shares RawWritable. Compiler-only operand-source and number-analysis arrays
are allocated only while lowering, not for executable loading. Exporting a loaded
function returns its existing backend record. Existing codegen/resource telemetry
continues to describe the same operations; no parallel metadata counters or maps
were added.

CallLowering passes after layout-table removal. BackendLowering's 71 representation
and resource checks pass after backend sharing, and NativeTiering's focused
private-initialization test verifies reference sharing plus X64/Windows-X64/ARM64
emission and Force results. Receipts: shared-image-metadata.log,
shared-backend-metadata.log, shared-metadata-properties.log under
.temp/runtime-cleanup. No throughput change is claimed from these removals.
Initialization storage and the remaining generic guard boundaries are still open.

### Ordinary refinement without value materialization

Ordinary interpreter Narrow/Convert now uses the same ready-storage operation as
the native bridge. Successful carrier-preserving refinements copy bytes and roots
directly instead of decoding a NativeValue, checking it again, and encoding it into
the destination. Pending values and actual conversions retain the existing shared
semantic operation. This adds no new handler, proof table, task, or artifact field.

Removed the destination-kind checks from TryNarrowReady and TryConvertReady:
lowering assigns the destination carrier for the declared result type. The
source-kind admission check is different: Narrow is an explicit checked type
bridge, not universally an unchecked assertion (see NativeHirLowerer.Function
and TypeBridgeChecks). Dynamic layout admission remains where the existing
operand proof requests it. InitializedValues also decodes directly after its
existing initialization test rather than checking that same flag twice.

CallLowering --refinement compiles and serializes Number/String union branch
journeys, executes each under Off and Force, and observes direct refinement
copies under Off. The call/alias/capture lifetime journeys pass too. Receipts:
.temp/runtime-cleanup/refinement-journey.log and refinement-calls.log.
BackendLowering's 71 representation/resource checks also pass after the shared
Convert path change (refinement-backend.log in the same directory).
These establish execution and removed work, not a measured throughput gain.
Per-byte initialization storage remains unresolved.

### Private copies across physical carriers

NativeFrame's ordinary Copy/Move now consumes the same existing PrivateArgument
storage proof as GetDatum. Previously only the exact-carrier CompiledCopy handler
omitted initialization/kind admission; a private Number copied into Number|String
still went through generic readiness/admission. Private copies now share the
trusted return-copy operation, renamed CopyCompiled, which selects raw copying or
kind-directed carrier adaptation without rechecking initialization/admission.
No additional proof, backend record, or artifact revision is introduced.

The shared operation also forwards an existing Future from its first lookup,
instead of checking pending state and looking it up again through Read. Completion
materialization stays in Write. Address-exposed generic sources keep their dynamic
path. Preparation only invokes frame.Copy after Known has found the source value.

CallLowering's real call/alias/capture journeys pass. The --refinement journey now
requires a compiler-private widening Copy/Move and demotes exact-carrier handlers
to Standard in the test executable, proving ordinary execution consumes the slot
proof rather than accidentally exercising only the specialized handler. Number
and String values survive Off/Force execution and serialization. Receipts:
.temp/runtime-cleanup/private-copy-calls.log and private-copy-journey.log.

Native AOT image artifacts/native/runtime-private-copy/msync has SHA-256
6ff7210baefd09d7b84751e2a9b1ad3bf4f42d36c8a60e6749ec3b6b6556e1dc.
Six alternating CPU-0 rounds against runtime-cleanup are recorded in
artifacts/benchmarks/synchronous-evaluator/private-copy-pinned.json. Median wall
times (previous/new) are 653.20/640.89 ms for calls and 1686.42/1691.06 ms for B+;
paired median changes are -0.52% and +0.75%, respectively, with mixed signs across
rounds. This does not establish a throughput improvement. This image includes
metadata sharing and refinement/private-copy changes, but predates the Erase
change below.
The published image also passes source/bound O0/O2 stream-demand journeys,
discarded-work cancellation, and delayed-error attribution; receipt:
.temp/runtime-cleanup/private-copy-stream.log.

### Slot invalidation without raw-byte scrubbing

NativeStorage.Erase now clears managed roots and marks the cell uninitialized,
without zeroing its raw payload. Drop and pending-Future writes use this operation.
Initialization, not stale payload bits, controls readability; subsequent writes
establish their own representation. Clearing roots still releases reachability.
Explicit ClearRegion is unchanged. This removes redundant writes rather than
adding a lazy-clear map or another per-slot state. Existing instruction telemetry
continues to account for Drop execution.

Union Write and kind-directed copies likewise no longer scrub the full raw
carrier before writing the active payload and tag. Padding/inactive payload bits
do not participate in decoding; surplus managed roots are still cleared. The
kind-directed raw copy keeps Span.CopyTo's overlap behavior, without a preceding
destination scrub that could erase an overlapping source. No language-level
initialization state or public region-clear operation was removed.

The useful engineering distinction is accessibility versus residual bytes, also
explicit in LLVM's [object lifetime model](https://llvm.org/docs/LangRef.html#object-lifetime).
This implementation does not adopt LLVM poison/undefined behavior: msync's
checked uninitialized-read behavior is still enforced where observable.

Focused storage properties cover all transitions among Null, Bool, Number,
String, Address and Path in one reused union carrier; clear/reuse and neighboring
cells; root-overlapping copies; and an overlapping Bool-to-union payload widening.
The call/alias/capture journeys pass after the raw-write removals. Receipts:
.temp/runtime-cleanup/payload-storage.log and payload-calls.log. These changes
postdate the private-copy Native AOT benchmark; they have no throughput claim yet.
Initialization-flag allocation and its remaining consumers are still open.

### Carrying private-slot proofs across value-transfer boundaries

NativeArguments initially routed frame views through NativeFrame.GetDatum and
CopyTo. The final implementation removes that intermediate proof check entirely:
NativeStoragePlanning.Call only selects Snapshot=false when every input has the
PrivateArgument fact. A frame-view argument is therefore initialized by its
construction contract, and a storage view is a fully written tail snapshot.
Both use ReadCompiled/CopyCompiled directly. Value captures use CopyTo because
their inputs are not uniformly private. Borrowed parameters retain their existing
address identity and do not take a copy. Address-exposed capture sources retain
their generic path.

The typed Copy case in TierBridge now calls the same NativeFrame.Copy overload as
ordinary execution; it previously bypassed that proof-aware implementation.
CopyTo is the shared implementation of ordinary frame Copy, not another storage
representation or analysis. Existing compiled_reads/compiled_copies observations
count frame operations; existing call/snapshot/borrow telemetry accounts for
argument transfer. Full call-lowering journeys pass after the intermediate change:
.temp/runtime-cleanup/proof-boundaries.log.

The intermediate runtime-payload Native AOT image passes stream-demand and delayed
failure journeys (payload-stream.log). Its CPU-0 six-round comparison against
runtime-private-copy is saved in payload-pinned.json under the synchronous-evaluator
benchmark directory. Paired medians are -2.50% for calls and +2.67% for B+, with
mixed round signs. This is not evidence of a general performance win, and does not
isolate the cost of proof lookup from raw-payload clearing. The final argument
construction-contract simplification above passes the call/alias/tail/snapshot
journeys (argument-contract.log) and is being measured separately.

The final artifact is artifacts/native/runtime-argument-contract/msync, SHA-256
ee5eca09d822838491f07754b5c71a8ec9bf8333650a86745f84705c97c688bd.
argument-contract-pinned.json records six alternating CPU-0 rounds against the
same runtime-private-copy reference. Median wall times (reference/new) are
640.11/624.02 ms for calls and 1680.20/1668.83 ms for B+. Paired median changes
are -6.42% and -0.81%. Calls improve in five rounds but vary from -19.61% to
+16.41%; B+ has three improvements and three regressions. Treat calls as a
positive signal with substantial noise, not a precise guaranteed speedup; B+
remains effectively inconclusive. All benchmark outputs match expectations.
The final Native AOT image passes all stream-demand journeys, open-input
abandonment, eager/ready ordering, and delayed-error attribution across source and
bound O0/O2 routes (argument-contract-stream.log).

Remaining initialization provenance boundaries (source audit): TryNarrowReady and
TryConvertReady are called with frame-register sources but still test the generic
address flag. TryCopyReady serves both frame stores and arbitrary-address loads,
so those cases must not be conflated. NativeFrame.Pointer uses initialization as
part of its ready-address shortcut. TryFastUnaryMath has its own source flag test.
Removing native private-slot flag writes first is not sound: a read-only borrowed
parameter can refer to a private caller temporary while the parameter itself does
not have PrivateArgument (PrivateSlots only marks Temporary roles). Those borrowed
reads still consume the source flag. Carry initialization provenance through the
actual call/storage model before removing flag writes/allocation; do not add a
fresh runtime alias or escape analysis.

### Initialization read boundary and native flag-store elimination

Frame readiness now combines the existing PrivateArgument proof with actual
pending-Future state. Frame stores, Narrow/Convert, pointer reads and fast unary
math use this common boundary. TryNarrowReady/TryConvertReady consume an already
ready source; arbitrary-address Copy still checks initialization. No additional
readiness table or runtime analysis was introduced. Existing refinement/copy
telemetry records execution outcomes.

Final compiler storage planning adds UnobservedInitialization (bit 8) to private
slots, then removes it from every source actually borrowed by a compiled call.
This is a use of the existing call plan, not another escape analysis. The bit
occupies the existing serialized slot byte. Programs without it continue to
write flags through the same executor; no artifact layout change is required.

X64, Windows-X64 and ARM64 emitters omit fixed-slot flag writes for that proof,
including register-region outputs and clearing moved-from sources. Managed-root
writes are unchanged. When a destination flag store is omitted but a source flag
still needs clearing, the emitter reloads the flag base rather than assuming the
destination left it in a scratch register.

NativeTiering --private-initialization compares normal, flag-tracked and fully
checked code: native output is smaller with omitted writes while read-elision
counts remain unchanged. Force execution, telemetry, and serialized borrowed
argument journeys pass; borrowed sources retain their flags. The materialization
properties cover pending/success/fault/cancelled Futures for private slots.
CallLowering and its --refinement journey pass after the shared-readiness change,
and the full call/alias/capture suite passes with flag writes removed.
Receipts under .temp/runtime-cleanup: initialization-writes.log,
readiness-futures.log, readiness-refinement.log, unobserved-calls.log.

The per-byte flag array still exists, and generic storage writes still update it.
This batch removes native work, not the remaining redundant allocation.

The first Native AOT image, runtime-unobserved-init, failed the real B+ journey
at a math CallNative: fast unary math checked readiness, then called LoadNumber,
which checked the omitted initialization flag again. Ordinary NumberBinary had
the same checked-reader dependency. Fast unary math now uses LoadReadyNumber
after readiness/kind admission (with the actual union payload offset); binary
operations use that reader for private operands and the checked reader otherwise.
The same failing B+ executable passes with the repaired managed CLI and matches
the exact workload output (unobserved-bplus-fixed.out). The focused native test now
includes arithmetic -> math.floor -> arithmetic to exercise this bridge. The
failed image is not an accepted benchmark candidate; it has no completed B+
performance result.

runtime-unobserved-fixed repairs that failure, but its six-round Native AOT
comparison against runtime-argument-contract regresses in every round: paired
medians +4.50% calls and +7.74% B+. Receipt: unobserved-fixed-pinned.json. It is not
accepted as a performance result. The new common readiness path added a hot
compiler-record/interface lookup through Code.Storage.Slots on every private-slot
test. NativeFrameSlot now carries the existing storage byte in place of its
derived Borrowable boolean; Borrowable is computed only where needed for tier
layout. Frame reads now access a direct slot-array byte. This introduces no new
per-slot field or allocation and avoids the compiler object chain at execution.
The performance attribution remains a hypothesis until the same workload is rerun.

slot-flags-pinned.json still shows paired regressions against argument-contract
(+3.95% calls, +4.46% B+), so metadata relocation alone is insufficient. Paired
Callgrind runs on the identical B+ artifact record 7,415,568,540 instructions for
argument-contract and 7,314,116,176 for slot-flags. Thus fewer total instructions
did not imply better elapsed time. The slot-flags image executes non-inlined
IsPrivate/IsReady helpers with 73,398,892/72,778,780 self instructions; baseline
IsPrivate accounts for 18,175,157. These are instruction counts, not sampled CPU
time. The three small readiness entrypoints are now explicitly inlined to remove
helper boundaries and NativeAddress argument transfers. Profiles and annotations
are under artifacts/benchmarks/synchronous-evaluator/profiles/native-slot-flags
and native-argument-contract; their stdout checks pass.

Explicit inlining alone did not repair elapsed time (inline-ready-pinned.json:
paired +3.97% calls, +6.59% B+). A same-image ablation, produced by clearing only
UnobservedInitialization from the serialized slot bytes, records -2.99% calls and
+1.97% B+ for elision versus tracked flags (initialization-ablation.json). This
separates native flag-store elision from the newly introduced readiness path,
without recompiling the source or changing its execution image.

The next correction uses an existing invariant: NativeMemory.Write clears the
initialization bit before publishing a Future. A set bit already proves readiness
and needs neither proof lookup nor pending-table lookup. IsReady now returns on
that bit first; an unset bit falls back to the private proof and actual Future
state. Thus an omitted native flag is allowed, not required, and no native writes
are restored. This is reuse of the existing ready representation, not another
state cache or handshake. The ablation procedure is a NativeArtifact roundtrip
with only the UnobservedInitialization bit cleared, followed by alternating runs
of runtime-inline-ready on original and modified .mexe files, stdin EOF, CPU 0,
one warmup and six measured pairs; inputs and stdout expectations are the existing
calls-100000 and storage-contract B+ fixtures.

Public storage writes can set the physical initialization bit after the VM
has registered a Future for the same cell. In that case the VM's pending
entry wins. The typed and same-carrier copies forward the Future before
reading bytes, and only consult the pending table for allocations that have
outstanding entries. The `vm.storage.initialization` sample distinguishes
`pending_copies` from physical copies.

ready-hint-pinned.json still regresses (+6.77% calls, +4.41% B+ paired medians),
so the ready-bit fast path alone does not establish performance recovery. Frame
slot size was checked against the former boolean-field declaration: both are
40 bytes; the storage-byte substitution did not enlarge slot arrays.

The current interface keeps readiness inside the existing Narrow/Convert
operations by passing a frame and source index, rather than materializing and
passing the source NativeAddress through an extra caller-side readiness layer.
The source address is fetched for telemetry only when recording is enabled.
Frame TryCopyReady remains one operation boundary, with its small copy body
inlined into it instead of expanding the whole frame operation into TierBridge.
Generic address copies also stop looking up a Future after seeing a set
initialization bit, using the same Write invariant. No proof, flag elision, or
dynamic uninitialized-read behavior is rolled back. Full call/alias/capture
journeys pass (ready-boundary-calls.log).

ready-boundary-pinned.json recovers B+ (all six rounds faster; paired -5.05%) but
not calls (+6.23%, five slower rounds). The native stream/failure journeys pass
(ready-boundary-stream.log). This is not yet accepted as overall throughput
recovery. Calls-specific Callgrind runs on the identical calls-100000 artifact
show 2,667,579,895 instructions for argument-contract and 2,730,308,623 for
ready-boundary. The extra work includes previously inlined execution-step wrappers:
ExecuteLinked +29.67M self instructions across its split bodies, ValueTaskAwaiter
GetResult +17.08M, ValueTask IsCompleted +11.69M, GetAwaiter +5.39M, and NativeStep
property getters +3.60M. These are self instruction deltas, not elapsed-time
attribution. Profiles are calls-ready-boundary and calls-argument-contract in the
existing profiles directory.

NativeStep is now a plain readonly two-field struct instead of a record with
accessors. Completed ValueTasks use Result directly; genuinely incomplete waits
still use the existing demand path and preserve exception behavior. The tiny
function-pointer forwarding helper and two profiled scalar-state getters are
explicitly inlined. No extra task, result state, or synchronization is added.

### Synchronous instruction result protocol

The subsequent twelve-round three-image comparison is recorded in
step-fields-triple-pinned.json. It did not remove the call-workload regression;
the instruction dispatch representation therefore changed rather than adding
more per-operation inlining adjustments.

Instruction handlers now return NativeStep directly. A completed step contains
its next PC and return flag; only an incomplete operation carries Task<NativeStep>.
Existing genuinely asynchronous helpers keep ValueTask internally, converting to
the step protocol at their boundary. Completed helper results are consumed
immediately; incomplete results expose their existing task. Async completion
helpers produce ordinary completed steps. Pure arithmetic, copies, constants,
guest calls and iterator fast paths no longer construct or unpack ValueTasks.
The public host asynchronous interfaces are unchanged.

RunTier was already synchronous, including its demand completion path. Its
ValueTask<TierOutcome> wrapper and the corresponding Demand call are removed.
Pending instructions still exit native execution before waiting, preserving pin
and frame lifetimes. DemandStep waits on the optional completion task and retains
function/op/PC attribution, pause/resume counters and vm.demand timing; a task
which completed before demand does not create a false wait interval. Existing
linked-operation, bridge-route and call telemetry retains its meaning.

CallLowering's real call/tail/alias/capture journeys pass with the new protocol
(.temp/runtime-cleanup/plain-step-calls.log). The first native image is
artifacts/native/runtime-plain-step; asynchronous user journeys and the cumulative
throughput comparison are being evaluated as one batch.

The plain-step native image passes the full stream-demand/error journeys,
24 filesystem Future journeys, and source/bound O0/O2 descendant-acquire journeys
(plain-step-stream.log, plain-step-file.log, plain-step-frontier.log under
.temp/runtime-cleanup). Public asynchronous host hooks retain their existing
names and signatures; only internal synchronous instruction helpers lose the
Async suffix.

The updated v0.9 project contract requires obsolete artifacts to be recompiled.
Removed UpgradeLegacyWorkHints and all decoder-side invocation of NativeWorkFacts.
Native 2 compact-4 and HIR 4 remain the product formats. The historical compact-3
fixture now checks an explicit recompilation diagnostic; fresh compilation of
the same constructor-state source executes correctly under Off and Force without
any migration (current-revision.log). Earlier compatibility experiments above
are historical, not the current loader policy. StoreScalarTrusted also no longer
zeros inactive raw union bytes before writing the active scalar/tag; root clearing
and initialization/readiness state remain intact.

The native direct-step image is SHA-256
2637384db8824ab83b6faec5a1d22de11e20d90caca142ab87b9999c9e352ceb.
direct-step-pinned.json records twelve alternating CPU-0 rounds with
runtime-argument-contract as the earlier stable baseline and runtime-step-fields
as the immediate reference. Median wall times (baseline/new/reference) are
705.06/710.33/738.79 ms for calls and 1999.24/1801.18/1873.15 ms for B+.
Paired changes against the earlier baseline are -0.31% calls (six wins) and
-7.94% B+ (ten wins). Against step-fields, paired changes are -4.91% and -4.51%,
with eight and ten wins. Calls are effectively at parity with the earlier
baseline; B+ improves. Every workload output matches expectations. The actual
native CLI rejects the old compact-3 fixture with its byte position and a source
recompilation request (direct-step-old.err).

No submission handshake or private-slot initialization guard was restored to
obtain this result. Remaining storage work includes the per-byte readiness/
initialization representation and the separate NativeStorage object owned solely
by each NativeAllocation. The latter can potentially be flattened internally
without changing public address identity, element-layout semantics, or static
storage APIs; it must not be replaced with another side table or value-copy layer.

### Storage state embedded in its allocation owner

NativeAllocation now embeds NativeStorage.Data, a value type containing the
existing full-layout and buffer slices. The separate NativeStorage heap object
and owner-to-storage reference are removed. NativeStorage remains the same public
sealed API type with its static entrypoints; NativeAllocation's base type,
address identity, public element layout, allocation-site identity and element
count are unchanged. Repeated-allocation storage still uses its full repeated
layout internally, distinct from the public element layout.

The internal Storage accessor returns a managed ref. Static memory operations
and native pin setup take ref locals, and root copying takes ref parameters;
they do not copy the storage descriptor. Read/write buffer operations are readonly
struct members; only BindStack/DetachStack rebind slices. InitializedValues keeps
the allocation owner, so an enumerable does not retain a stale struct snapshot
across rebinding. No new side table, analysis, synchronization or telemetry state
is introduced. Existing allocation/function/site and payload-size metrics retain
their logical meaning; the removed CLR object was not a separate language object.

The focused storage properties cover a retained address and enumerable across
detach/rebind, overlapping root copies, union overwrites, cleared cells and GC
retention. Call/tail/alias/capture journeys pass. Receipts are
.temp/runtime-cleanup/inline-storage-storage.log and inline-storage-calls.log.

artifacts/benchmarks/storage-owner/allocated-bytes.json records a managed .NET 10
allocation probe: both assemblies are loaded in isolated AssemblyLoadContexts;
an emitted factory invokes NativeAllocation(layout, -1, 1) with a cached one-Number
layout. After 1,024 warmups, 10,000 allocations are measured with
GC.GetAllocatedBytesForCurrentThread and kept observable with GC.KeepAlive. The
old assembly allocates 1,840,128 bytes (about 184 B/cell); the new assembly
allocates 1,600,000 bytes (160 B/cell). The approximately 24 B/cell difference is
the owner reference and separate object overhead, not payload compression. The
receipt includes assembly hashes; the old assembly is retained under core-before.
This is managed allocation accounting, not a Native AOT throughput measurement.

The native inline-storage image (SHA-256
299d2accda7c4c554003b053790bd56a34438e00f030aa6a92a7f4c3d6c23060)
passes both benchmark outputs. Twelve alternating CPU-0 pairs against direct-step
are recorded in inline-storage-pinned.json: paired +1.33% calls (five wins) and
-1.16% B+ (ten wins). Call timing is mixed; do not claim that allocation removal
made that workload faster. The allocation reduction is independently established.

### One source reference per argument descriptor

Inspection of the real calls-100000 artifact confirmed that all three guest call
sites already use snapshot-free, borrowed inputs. No additional alias analysis
or snapshot rule was introduced. The remaining descriptor waste was three
mutually exclusive owner references: values, caller frame, or tail storage.

NativeArguments now holds one explicitly overlaid GC-reference union and a source
kind. Typed constructors select the active reference; inactive alternatives are
never read. Slots and borrow contracts are unchanged. Count is captured from the
immutable call plan or initialized snapshot at construction, rather than obtained
through nullable owner chains and list interfaces on every parameter-loop test.
There are no new heap objects or runtime proof checks. Existing argument, borrow,
snapshot, and call telemetry retains its meaning. GC retention, source-mode
selection, short reusable snapshots, and default empty descriptors are covered
by NativeTiering --argument-sources; real call/tail/alias/capture journeys pass
(argument-source-calls.log).

The descriptor is 40 bytes on the measured x64 runtime; the three source modes,
GC retention and empty state pass argument-sources.log. Twelve native pairs in
compact-arguments-pinned.json compare the combined changes with direct-step:
-2.44% calls (eight wins) and -1.52% B+ (seven wins). Against inline-storage alone,
the paired changes are +3.05% calls (five wins) and -1.17% B+ (seven wins). This
does not isolate a clear throughput benefit for the smaller argument descriptor;
its established result is eliminating mutually exclusive references and repeated
count lookup without changing call behavior.

Loading a function now leaves the call lookup array empty when there are no calls,
and otherwise sizes it through the highest call PC rather than every instruction.
Direct PC lookup is retained. Native compilation reads already-existing profile
observations instead of creating a full empty profile array and a second shape
snapshot array merely to compile statically typed code. Observe remains the only
profile allocator; each selected instruction consumes its existing site's shape
snapshot. These are loading/compilation allocation removals, not new analyses or
runtime branches. Focused emission properties verify that compiling without
observations leaves ObservedProfiles null; real call journeys pass.

The intermediate shortened call lookup is subsequently removed entirely.
Loaded functions share the existing compiler call-plan array. Call's otherwise
unused executable Immediate field holds its plan ordinal, linked once from the
plan's instruction PC. Serialized backend records and diagnostic IR are unchanged;
exporting a loaded function still returns the original backend. No PC-indexed
copy of call-plan references is needed. Non-array host-provided plan lists are
materialized once; normal compiler/decoder arrays are reused directly.

NativeExecutionInstruction also has a direct backend-record constructor. Loading
no longer creates a temporary NativeInstruction object for every backend opcode
just to copy its numeric fields into the executable instruction. The call-plan
ordinal update copies only the executable value type. Real call/tail/alias/capture
journeys pass after these linking changes (shared-call-plans.log).

The native shared-plans image is SHA-256
42e527631aa0fb67df4eb78097d95c7adacb9566ce8b987e2b92ac634952c9c1.
shared-plans-pinned.json records twelve alternating CPU-0 pairs against direct-step:
paired -1.94% calls and -0.25% B+, each with seven faster pairs. Treat this as
approximately preserved throughput rather than a strong speedup claim. The
allocation/object and descriptor reductions are separately established above.
The final image passes stream-demand/error ordering, 24 filesystem Future
journeys, all four source/bound descendant-acquire routes, and the Force-mode
recursive live-string journey (6000). Receipts: shared-plans-stream.log,
shared-plans-file.log, shared-plans-frontier.log, shared-plans-roots.out under
.temp/runtime-cleanup.

### Scope audit at the managed-reference boundary

The final comparison with the original evaluator has completed:
`artifacts/benchmarks/synchronous-evaluator/final-original-pinned.json`.
It records twelve alternating pairs pinned to CPU 0, one warmup, independently
compiled artifacts for each executable, and matching expected workload output.
The original executable hash is
`49f47587ad5bf9f58c5e98c16498d9ab0507176d039265428a159cfe79538f6b`;
the candidate is the shared-plans image identified above. Median paired elapsed
changes are -20.63% for calls-100000 (12/12 faster pairs) and -13.10% for B+
(11/12 faster pairs). These measure the combined implementation against the
original evaluator, not the isolated effect of submission-handshake removal.
The benchmark process is terminal and the receipt is complete; it need not be
restarted. Final stream, filesystem and descendant-acquire journey logs also
contain their expected successful terminal results.

The named redundant paths have been addressed: CopyTrusted has no repeated
initialization admission; compiled frame/argument copies use their existing
proofs; Narrow/Convert avoid destination revalidation and honor compiled layout
proofs; native unobserved initialization writes are omitted; the second stream
submission handshake is absent. Instruction and tier execution return ordinary
results, not generic ValueTasks. Storage ownership, argument source references,
call-plan lookup and empty profile allocation no longer duplicate state as
described above. Alias/escape analysis is not invoked by guest execution or
artifact decoding; NativeWorkFacts is consumed by backend compilation only.

The remaining readiness/initialization bytes encode genuinely observable dynamic
storage state and provide the ready fast path for Future-backed cells. They are
indexed by existing byte offsets to avoid a second lookup representation. Actual
managed-reference stores still use CLR barriers, and copies retain address
identity. Removing those barriers altogether is a different native-reference
memory model, not deletion of a duplicate validator. The user has been asked
whether that further architecture change is part of the intended scope; the goal
has not been marked complete pending that distinction.

### Set: following a guest reference is still a bridged operation

The standard `set-dedup` workload (standard/17, 8,192 insert events plus
4,096 queries/expirations, with ordered snapshot checksums) was rebuilt at O2
and bound with the Linux x64 0.9.1 candidate
(`cbe6aa1c8565…`). The same detached executable (`ac1af9638768…`)
ran on CPU 6 in fresh processes with redirected fixture input. Every run
matched the existing byte-exact oracle. Telemetry-off Auto/Off wall samples
were 3.203/3.949, 3.470/3.995, and 3.371/3.828 seconds, reversing order in
the middle pair. This is a whole-process tiering comparison, not a per-load
latency measurement. The independent profile runs completed on both policies;
Auto recorded 27.2 ms of tier compilation, 2,466,478 bridge entries and no
machine guest calls or inline guest-view calls.

| Bound function | Frame entries | Logical instructions | Share of 14,284,671 |
| --- | ---: | ---: | ---: |
| Default `NaturalOrder::less` (function 80) | 190,249 | 4,559,518 | 31.9% |
| `_set_child` | 130,070 | 2,861,540 | 20.0% |
| Both hot `_set_key` variants | 126,950 | 1,650,350 | 11.6% |

The node's `left/right/parent` fields are stored as nullable broad Scope
references. The fixture uses one concrete node layout (29), including NIL.
Nevertheless the emitted `_set_child` converts the input to the checked view,
tests its layout for the member access, selects a fixed field, loads the
nullable reference, checks null, and converts the result back to the node
view. Each actual call executed 22 logical instructions: seven copies, four
branches, two jumps, two unary tests, two converts, and one each of constant,
address offset, load, narrow and return. These are IR counters, not machine
instruction counts. The field is not fetched by name; the bound offset is
known. Both `_set_key` variants retain a node layout check and load a nullable
Number; specializing the tree parameter did not prove the node parameter.

The x64 selector in `NativeExecutionFunction.GuestMemory.cs` admits exact
Number/Bool field loads and stores, and separately admits reference copies
between compatible frame slots. These are not the same operation. In Auto,
`_set_child` recorded 130,039 direct layout checks and 520,156 direct frame
copies but zero direct guest field loads. The linked/bridge offset load
(`NativeInterpreter.Execution.cs`, `NativeInterpreter.Tiering.cs`) materializes
the receiver as a `NativeAddress`, applies the prelinked offset and invokes
`NativeMemory.Copy` with initialization, pending and carrier handling. Reading
the guest root as an allocation resolves the edge through the descriptor table
(`NativeRootBank.ReadAllocation`). The original pointer is already in the
guest edge lane; the expensive route is not required by its storage geometry.
The comparator is also substantial: its statically resolved Number fixture
still executes the general nullable/Number order body, including its total
ordering behavior. Do not attribute its work to cross-scope loads.

A source-inlining control kept the casts and null handling but expanded
`_set_child` only at the traversal sites in `_set_find` and `Set::insert`.
Both arms imported the same checkout's `stdlib/containers.msync` as source
instead of using the embedded module, so compare only within this pair.
With separate O2 bound images, CPU 6, Auto, and the same oracle, base/inline
wall samples were 5.619/5.939, 5.773/5.963, and 5.798/5.925 seconds. The
profile confirmed 130,070 to 14,629 `_set_child` frame entries, 14,396,825
to 13,724,085 instructions, and 2,489,903 to 2,172,839 bridge entries.
Total observed machine plus linked layout checks were effectively unchanged
(493,235 to 493,235), as were direct guest field loads. This control does
not support source inlining alone as a speedup, nor does it price the
remaining field load separately. Its receipts are the `local-*` artifacts
and databases plus `inline-timing.txt` in the same directory.

Candidate direction: preserve proved node-layout and non-null facts for
internal recursive links across reaching stores and call/return; do not infer
them merely from a structural view or trust an arbitrary policy callback.
For a proven same-arena ready reference, lower a fixed-displacement field
read to a guest-edge read, preserving the packed interior offset, null/tag,
initialization, pending, host-root and GC ownership semantics by using the
existing slow path where needed. Select a contiguous useful region rather
than adding two tier crossings for each isolated load. Keep comparator
specialization separate (in particular retain NaN/null and user policies).
The precedent is fixed-offset in-object access in
[V8](https://v8.dev/blog/fast-properties) and receiver-map specialization in
[SELF](https://bibliography.selflanguage.org/implementation.html);
[Linux rbtree](https://docs.kernel.org/core-api/rbtree.html) similarly keeps
node indirection and comparator dispatch out of the generic walk. None
requires changing the source Set representation or observable Scope identity.

Raw receipts: `.temp/set-cross-scope/{hashes.txt,timing.txt,auto.sqlite,off.sqlite,bound.txt}`.
Hardware counters were unavailable on this host. The profile attributes
logical work, not cache-miss rates or exclusive CPU time.

#### Guest-reference field load and scalar comparator follow-up

The x64 slot tier now has a separate fixed-offset reference load. It selects
an exact-address frame base, a private destination and equal Address or
Address-or-Null carrier geometry. It copies the guest edge, tag and packed
interior offset without resolving a CLR allocation. The edge lane follows the
raw payload and flags at an aligned displacement; optional callable cells
follow it. A typed interior address marks its CLR type root in the second
native lane. This is a non-object marker, not a traceable edge. A host
reference, typed metadata, pending value, or uninitialized field uses the
existing linked load. The heap and the artifact representation are distinct:
the Native-2 artifact format did not change.

On the same old bound Set artifact, the first Native AOT reference-load
candidate recorded 172,855 native loads across 13 selected sites, including
130,039 in `_set_child`, with no redirects in this workload. Bridge entries
fell from 2,466,478 to 2,342,483. Five telemetry-off Auto pairs on CPU 6
gave 3.224 to 3.080 s process medians and a -5.7% paired median (three wins).
The reference-load comparison did not recompile the guest program.
Set/17 and Set/503 detached Force journeys passed their alias, nullable-key and
retained-snapshot checks. The separate B+ inventory corpus and guest-heap
REPL/Off/Force cycle journey also passed. The larger nullable B+ validation
journey currently fails semantic checking on both the old and new image,
before any reference load runs.

A raw-header shortcut for `Convert`/ `Narrow` was discarded: a temporary
instrumented image saw zero checked refinements through that path on this
fixture. The explicit member layout test is a different instruction from
the conversion's existing carrier copies.

Production source compilation runs LLVM function passes, not the legacy Native
inliner. The default Number comparator also failed whole-SSA selection because
it retained literal type testing and tagged scalar views. HIR lowering now
folds tests of two relocated Type literals, and the ready scalar SSA selector
can erase proven non-null tests and their tag-only representation scaffolding.
The slot path still preserves pending behavior, and custom order policies are
untouched. In a managed-host profile of the same real Set fixture this selected
the comparator and ran all 190,249 calls through its frameless machine entry.
These are not inlining events. Full compile profiling also exposed a preexisting
target trace bug: void operations now record a void result instead of trying
to map it to a data slot.

Receipts: `.temp/set-cross-scope/{guest-load-timing.txt,guest-load-aot.sqlite,delivery-compile.sqlite,delivery-run.sqlite,bplus-corpus/,heap-check/}`.

#### Executable-only inlining

Explicit `bind-ir` runs the Native inliner at O2/O3 after contextual
planning and before backend operand selection. Portable emission, ordinary
source/object execution without serialization, O0/O1 and replay of an
existing executable do not run this pass. The pass reuses the value-flow
graph to prove a split callable's code/name constants and one reaching
constructor. An allocated environment supplies the physical capture ABI
and readiness; no speculative descriptor check is inserted into execution.
Location captures bind the original typed interior pointer, not a copy
of its current contents. Cloned instructions keep callee spans; parameter
and environment setup use the call-site span.

Rewriting relocates control-flow edges, existing allocation sites and
contextual plan ranges; cloned allocation sites retain logical heap
placement. The storage plan is invalidated for modified callers and is
recomputed before any backend slots or linked operands are emitted. Existing
frame placements must remain justified after the rewrite. The executable
artifact reader performs neither inlining nor analysis.

Unrestricted inlining was rejected on Set: it expanded 57 call sites, but
reference-field native hits fell from 172,855 to 3,833. Restricting it to
scalar carriers expanded the eight comparator sites, yet lost the existing
ready scalar machine entry. On three same-host pairs using the same Native
AOT runtime, the no-inline/inline bound wall samples were 2.544/2.732,
2.414/2.720 and 2.860/2.791 seconds (paired median +7.4%); bridge entries
increased from about 1.51M to 2.38M in separate profiles. The executable
admission rule therefore leaves reference-carrier helpers and multi-block
ready scalar bodies independently tierable. Tiny scalar functions and
scalar location captures can still inline. The next route to cheaper calls
for large comparators is a machine-to-machine ready scalar entry or native
region inlining, not slot expansion ahead of selection.

`tests/Msync.EndToEnd/executable_inlining_journey.py` checks that operations
really expand in the detached executable, alias writes reach the original
location, an ordinary transfer still executes only at runtime, and a cast
failure identifies the callee's original source offset without the source
or object artifact.

Final Linux x64 Native AOT candidate `98e65c71468f…` rebuilt and bound
standard/17 from source. Compile, bind and run profile sessions completed;
binding rejected the eight multi-block ready comparator sites on cost,
and execution recorded 190,249 frameless comparator calls, 172,855 direct
reference field loads, and 1,506,812 bridge entries. The original 0.9.1
candidate recorded 2,466,478 bridge entries. Three telemetry-off Auto
pairs on CPU 6 (each image's own fully bound executable) gave
3.089/2.451, 3.029/2.400, and 3.103/2.490 seconds. The paired median was
-20.7%; all six transcripts matched the corpus oracle. The combined result
does not attribute the entire timing change to field loads alone.

The final image also passes the new detached inlining journey (including
prepared and residual copies), the O2 bound Set journey at seeds 17 and
503, the guest-heap cycle/reuse journey, and the contextual I/O overwrite,
metadata branch and retry cases. An additional detached Number Set checked
NaN membership/deduplication and signed-zero equivalence with Auto and Off.
Raw final receipts are in `.temp/set-cross-scope/` (`final-*.sqlite`,
`final-timing.txt`, `final-hashes.txt`, `final-context.json`,
`final-set/`, `final-heap/`, and `ieee-set/`).

#### Set follow-up: skip reachability for settled work

The 2.45 s bound Set run was still dominated by the runtime, not just
pointer chasing. Linux x64 wall-clock stack sampling of the same Native AOT
image and final bound artifact, with telemetry off, found
`FinishOwnedWorkAsync` on 1,045 of 3,600 task-leader samples (29.0%; five
runs, 27.7–30.0%). `NativeReachable` appeared on 984 of those stacks.
The sampler uses ptrace stops with 1.5–4.5 ms randomized intervals,
matching AOT symbols and frame-pointer chains; startup before the initial
65 ms and worker threads were not sampled. Stack presence is inclusive
and can undercount across unwinding gaps, not an exclusive time breakdown.
Hardware counters remained unavailable.

Function-scoped diagnostics located the cleanup at each return from
`read_number`, not at top-level exit: 12,288 reads, zero promoted input
submissions, and zero reachable Futures or Processes. In every invocation
the old path still walked statics to answer whether an already completed
input value should be transferred to its parent work scope.

An activation now closes and snapshots its owned batch before deciding
whether it needs the root graph. Successful input and completed pure
scalar values carry no work to transfer. Pending and failed submissions,
transfers, live processes, and values that can retain work still use the
existing reachability and disposition logic. Once an activation scope
closes, later work is forwarded to its parent; an asynchronously arriving
addition cannot make the no-scan decision stale. Diagnostics report the
actual outcome rather than treating an unvisited graph as empty.
The detached pending-input journey keeps its pipe empty until the callee
returns with a Scope holding the pending read; it then verifies the caller
can demand that same read.

With the same bound artifact and redirected fixture, five telemetry-off
Native AOT Auto pairs pinned to CPU 6 moved the process median from 2.463 to
1.744 s (paired median -29.1%, five wins). All runs passed the byte oracle.
The candidate diagnostic run recorded 12,288 `settled-values-only` exits
from `read_number` and no retention traversals. The separate pending
Scope-return probe records a `traversed` exit and one promoted input.
The full stdio journey, including pipe handoff and PTY Ctrl-C, and all 18
source-mode real-process scheduling journeys passed.

Resampling the candidate found only nine exit-cleanup stacks (0.6%) and
no `NativeReachable` stacks out of 1,476. `TierBridge` is now on 73.2%
of sampled stacks. This is a larger *share* after removing unrelated
work, not evidence that bridge time increased. The old workload profile
attributes 910,273 bridged logical instructions to `_set_child`,
approximately seven per hot helper call even after the child field read
itself became native. Receipts: `.temp/set-cross-scope/`, files
`sample-baseline.json`, `sample-settled.json`, `work-hashes.txt`,
`work-timing.txt`, `work-before.sqlite`, `work-after.sqlite`.

A second change reuses the x64 reference-copy path for `Convert` and
`Narrow` if the linked operand says layout admission is unnecessary and
the source, operation and destination have the same Scope carrier.
Exact and nullable Scope are different carriers here: the rule does not
discharge a tag test or collapse nullable to exact. Pending, unready, and
host-root cases still leave at the original instruction. The artifact
format and proof contract are unchanged.

The two Scope-to-Scope conversions in `_set_child` already meet those
conditions. The same-artifact AOT profile counted 287,820 native refinement
copies (260,078 in `_set_child`, 27,742 in `_set_parent`), taking bridge
entries from 1,506,812 to 1,362,902 (-9.6%). These are matched `profile`
recordings, not the higher-overhead diagnostics recordings. Five further
telemetry-off pairs against the settled-work-only build gave 1.960 to
1.956 s process medians and -1.8% paired median; pair changes ranged from
-7.9% to +8.9%. This establishes fewer bridges, not a stable additional
wall-time gain. The final image passed bound Set alias/nullable/snapshot
checks, redirected stdio including the pending Scope return, and 18 bound
real-process scheduling cases. See `copy-timing.txt`, `copy-hashes.txt`,
`work-settled-profile.sqlite` and `work-copy.sqlite` in the same receipts.

#### Native reference lanes and header-only marking

In scoped 64-bit execution a guest reference slot is one unmanaged word.
A positive aligned word addresses guest payload (bit zero can identify a
closure environment), zero is empty, and a negative marker identifies a
host boundary entry. The latter is keyed by the physical slot address and
stored only for actual host values; no CLR reference or GCHandle is written
into guest memory. The host registry is sparse and independent of numeric
and guest-pointer storage. Overwrite, stack pop, sweep and arena retirement
release the appropriate boundary entries. Same-arena copies of one or two
ordinary edges do not read or write any CLR reference. Overlapping copies
that involve host entries still use memmove ordering. The x64 JIT checks
tags in the native lane, not a pinned CLR root array.

Ordinary typed interior addresses put the source carrier bits directly
in their second word. Copying such an address retains the edge, packed
interior offset and type word together. Interpreter APIs which still
return a materialized address decode immutable type metadata from that
word; non-source machine typing remains a host boundary case. These two
tag bits serve a similar purpose to other native runtimes' tagged
references (e.g. [LuaJIT object representation](https://github.com/LuaJIT/LuaJIT/blob/v2.1/src/lj_obj.h)),
without adopting their value or GC ABI.

The 16-byte guest block header now includes a reference-slot count:
extent/flags (low 32), count (16), and concrete layout ID (high 16).
The other word still contains descriptor ID and raw size. The arena mark
loop follows raw edges by computing the slot address from the header.
It does not resolve a CLR allocation view for ordinary live Scope
objects. Very large reference counts fall back to the descriptor's
full count; capture-vector ownership can still require a descriptor
lookup at its explicitly flagged boundary. Reclamation still retires
reusable descriptors and removes actual host entries before reuse.
The on-disk Native format is unchanged.

This is a storage migration, not a claim that the materialized ABI is
gone. The external embedding representation, some builtin-produced
records and Range snapshots, deferred execution state, string operations
and interpreter fallbacks still have CLR-backed implementations. Native
string/range payloads and removal of allocation-view materialization are
separate remaining work.

The Linux x64 Native AOT candidate `e5e77bc69334…` ran the unchanged
bound Set artifact `40405b62c3f6…`. Both Auto and Off profiles attributed
all 10,406,239 activation reference-slot instances to native storage.
Auto still performed 172,855 native field-reference loads. The REPL
cycle fixture had 585 header scans, zero descriptor scan fallbacks,
and zero managed root slots; the bound Force cycle fixture had 495
header scans and likewise zero fallbacks and managed root slots. In
the separate captured-machine-call fixture Force performed 120 header
scans and 32 explicitly flagged capture descriptor reads.
Scoped closure reuse, stack/tail handoff, detached Set seed 17/503,
and stdio/pending Scope transfer journeys passed. The machine-call
suite's scalar call-count assertion also fails in the previous candidate:
bound inlining removed the calls it expected to observe. Captures,
closure reuse and stack subjourneys were run independently.

Five telemetry-off Set process pairs on CPU 6 gave 1.611/1.621 s
baseline/candidate medians, paired median +1.1% (two faster pairs;
individual changes -5.4% to +4.3%). This window demonstrates a
storage/ownership change, not a stable throughput gain or loss.
Receipts: `.temp/set-cross-scope/native-storage-*`.

The next writable storage to migrate is the runtime-owned but
CLR-backed binding/argument state in `NativeFrame` and snapshots created
by `NativeRange.Snapshot` or builtin record factories. Keep the exported
CLR value API outside that storage boundary. For text, use owned native
character spans and materialize host strings at I/O or the exported ABI,
rather than making the sparse host marker a permanent guest String ABI.

#### Target design: the VM owns its writable memory

The reference-lane migration is not the VM memory boundary. Today
`NativeFrame.Location` still resolves indirect slots through
`NativeAddress[]` (a location contains a CLR allocation view), snapshots
retain `NativeDatum[]`, and `NativeMemory` associates pending values with
CLR allocation identity. `GuestHeapRoots.Trace` also discovers some live
guest values by traversing those arrays, activation plans, and managed
execution objects. Replacing just the arrays with arrays of handles
would preserve the dependency; it would not move the VM off CLR memory.

**Boundary.** Guest-visible writable state and everything necessary to
locate, publish, and trace it live in unmanaged memory owned by the VM,
including bindings, argument packets, plan results, globals, iterator
cursors, and the roots of suspended work. The interpreter and machine
tier use the same representation. Compiler/preparation models, host
scheduling, telemetry and embedding adapters may use CLR objects. They
cannot be the backing store of a VM operand, its location, its pending
state, or its guest lifetime. Immutable metadata needed to address or
trace guest storage is linked into native execution descriptors; retain
source maps and diagnostic names on the host. A CLR wrapper is an
adapter, not a unique owner or identity for a guest object.

| State | Native representation | Ownership |
| --- | --- | --- |
| Direct frame slot or Scope field | Existing byte and reference lanes; a prepared pair of offsets and a carrier | Frame or nonmoving guest block |
| Indirect binding | Unmanaged location: owner base and byte/reference offsets; carrier comes from the slot plan or an explicit native carrier word for a dynamic view | Binding points at the location; it does not own a copy |
| Call snapshot | Typed argument cells in one native packet, using linked offsets and the same lane format | Caller until callee installs its values; a tail handoff owns its packet independently of either frame |
| Persistent and escaping locations | Stable native cells plus an entry in the session's native published roots when appropriate | Session or reachable heap graph; never a borrowed stack address |
| Pending cell or plan result | Native state and a completion ID; a ready result replaces the dependency with the normal typed carrier | VM owns the cell; host work has a lease while it can publish a completion |
| Actual host resource | Tagged, stable resource ID with generation; not a guest payload pointer | Host adapter owns the OS/CLR resource; guest holders and in-flight work keep its lease alive |

**Addressing and identity.** A location is not a guest value. Resolve it
to a byte address and, only when needed, a reference-lane address. Keep
both offsets: interior aliases to different reference slots can share
a byte offset. The unmanaged owner header supplies the two lane bases
for an indirect location: heap blocks can derive them from their extent;
segmented frames publish their lane bases when pushed. Direct frame
access already has those bases, so it does not read a binding or owner
header. Location identity is owner base plus those offsets, not
type, CLR reference identity, or the physical slot that happens to hold
an address. When a location can survive a safepoint, retain its base as
the traced edge; compute interior pointers for the operation that uses
them. Do not put a descriptor-table lookup on ordinary field access or
wrap scalars or pointers in a universal carrier. Keep addresses stable
with the current nonmoving heap and segmented frame storage. Lua's
[thread-stack tracer](https://www.lua.org/source/5.4/lgc.c.html) is an
example of scanning live VM slots directly. LLVM's
[base/derived pointer model](https://llvm.org/docs/Statepoints.html)
explains the distinction between trace base and access address; this
does not require adopting LLVM statepoints or a relocating heap.

**Calls and publication.** Reuse the compiler's existing snapshot and
read-only-borrow decisions. No-snapshot calls read the source locations,
and borrow only when the linked plan permits it and the owner outlives
the borrower. Snapshot calls capture values, including shared guest
references and unresolved dependency IDs, not aliases to the argument
cells. A location capture, in contrast, copies the location descriptor
and must still see subsequent writes through the original alias. Never
publish an escaping reference to a stack slot: use the existing escape
plan to place its location on the heap before the alias is created.
Use the same typed packet fill for ordinary snapshots and tail calls.
A tail call does not use scratch memory in the frame it will pop:

    reserve and register outgoing packet and environment
      -> fill packet while caller and incoming packet are live
      -> retire caller and incoming packet
      -> install callee roots and parameters
      -> release the handoff; if the callee borrows from the packet,
         transfer packet ownership to that callee until it returns

Start a packet with empty edges; publish initialized entries before
any collection-capable work can inspect them. Release the handoff on
failed entry as well. Fresh frame bindings and suspended activations
follow the same publish-before-release rule. Keep the ordinary frame
path on its existing segmented arena, with no allocation or clearing
for an absent binding/snapshot region. Start by tracing only published
native reference spans, plus existing explicitly identified transitional
roots. Machine code may keep transient addresses in registers between
safepoints; an edge live across one must already be in a published cell.
Do not expand the current collection schedule while changing frame
ownership. The target root set is the native active/suspended frame chain,
session and program roots, handoffs, and registered external borrows;
the tracer must no longer reconstruct liveness from CLR object graphs.

**Completion and resource ownership.** Move the pending overlay before
requiring snapshots to handle arbitrary inputs without a CLR detour.
Keep the ready carrier unchanged. Use a native sparse pending index
keyed by owner and both offsets, with an owner-local empty fast path;
allocate index storage only when the owner actually has pending cells.
The overlay is not a second copy of ready values. A snapshot retains the
same dependency; it does not await it or resubmit it. A host callback
names a completion or an explicitly leased buffer, not the address of
a frame cell that may have been reused. The VM installs completion or
failure at the existing synchronization boundary. Shared aliases and
snapshots see the same outcome; failure keeps the original source and
existing demand semantics. Slots can retire without retaining finished
work, and stale callbacks cannot write into reused storage. Use the
same completion entry for plan results and ordinary calls. The CLR task
or transport remains in the adapter, not in the pending cell.

The existing per-slot host-marker registry is transitional. A real host
resource ID resolves only at the host boundary. Moving a slot moves its
ID, copying it adds a holder, and overwrite, frame pop, and sweep remove
holders; the last holder and the last outstanding work lease determine
when the adapter can retire the entry. This bookkeeping is confined to
resource values, not applied to guest pointers. A source-level String,
Scope, closure, or materialized Range is not a host resource. The public
`NativeValue` API remains an adapter: import host data into owned
storage; export scalars/text by value and identity-bearing values through
an adapter with an explicit arena/graph lease. Count an external lease for
an actual external owner or in-flight borrow; a host ID merely stored in
the guest graph does not create a permanent root for that graph. Never hand a raw stack
borrow to the host or free already exported storage at interpreter
disposal if the existing API still permits its use. Preserve which
aliases refer to the same location; do not deep-copy a Scope to make it
exportable.

**Payload migration.** Builtin-created Scopes use the same arena layout
as compiled records. A materialized Range is a native descriptor and
element storage (count, linked item layout and the usual typed lanes);
copying a descriptor shares its immutable backing, not any mutable
input region used to build it. Numeric sequences retain their compact
specification. Stream-backed ranges keep laziness and can acquire host
leases for I/O; they do not share a representation with materialized
arrays. Guest-created lazy ranges retain guest plan state/operands
natively rather than masquerading as host streams. Start String with
owned immutable UTF-16 code units and an explicit length: this preserves
existing input and lets ordinal, numeric and Rune operations work on
spans without changing which operations count code units or scalars.
Do not normalize on storage or treat Path projection as a text copy.
Copy or convert at actual host I/O/embedding boundaries; isolate APIs
that still require materialization until they are replaced. Prepared
literals enter these native representations at link/import time, not
once per instruction. These are in-memory changes: Native artifact
format 2, HIR format 4, source behavior and the exported API do not change.

**Delivery order.**

1. Land the native location and frame binding path. Put only indirect
   bindings in a native span; keep direct slots on prepared offsets.
   Use native locations directly for Scope accesses, captures, borrows,
   and persistent bindings. Move the reader and collector with the
   writer; do not mirror native locations in a managed root array.
2. Replace the pending index and then replace guest argument snapshots
   with native typed packets, including the tail handoff. Keep import
   from managed arguments as an explicit embedding adapter, not a
   fallback for any guest call. Move suspended roots, plan results and
   session root traversal to the same publication protocol.
3. Move builtin-created Scope, materialized Range and String/Path
   payloads; convert guest lazy descriptors and retire managed storage
   paths and the per-slot host registry as their remaining callers
   migrate. Preserve the external adapters throughout.

Extend sink-neutral telemetry with binding placements (direct, indirect,
escaping, persistent), borrowed/copy/pending argument counts and packet
bytes/reuse, handoff outcomes, native root origins and scanned edges,
and adapter ingress/egress with function/source and wait/failure reason.
Use existing per-activation/phase aggregation, not per-field events on
the hot path. A zero managed-reference-slot count alone does not prove
the boundary: separate counters for descriptor materialization, managed
root traversal and adapter calls must identify any remaining path.
Acceptance for the first slice is the existing bound Set artifact under
interpreter and machine execution, plus an alias mutated through a
location capture and observed from a second alias after a collection.
For the second slice, exercise a self-tail argument permutation with
borrowed inputs and a pending stdio Scope with a successful and a failed
producer, checking identity and source attribution. For payload and
adapter work, use the String/Range and REPL journeys, including a retained
export across arena retirement. Measure Set again only once the actual
execution path has changed; these are correctness and representation
criteria, not timing gates.

##### Implemented: frame binding cells and ordinary argument packets

`FrameAllocation` appends hidden binding cells and, only for functions with
snapshot sites, one typed scratch region sized at link time. Hidden cells
extend the physical lanes without exposing new source-addressable regions.
An uninitialized binding cell denotes the slot's own prepared location.
A populated cell uses the existing Address carrier: a packed byte/reference
offset pair, a base edge and its storage-type word. Same-arena heap bindings
are raw pointers in scoped x64 execution. The transitional adapter still
resolves a `NativeAllocation` on interpreter reads and handles stack-owned or
external bases through the host registry. Neither the direct frame-slot path
nor machine field access uses those lookups.

For an ordinary snapshot call the source frame copies typed cells into its
linked scratch region. The packet owns the values, not the source locations;
it shares Scope identity and preserves a pending dependency without waiting.
Later snapshot sites reuse the region. The tail path still uses its existing
independently owned packet, not the departing frame's scratch. Reusing or
leaving the frame clears the old scratch, including its pending entries.
The collector visits binding and packet edges in the frame lanes; it no
longer enumerates separate CLR binding or argument arrays.

Linux x64 Native AOT `97045022ad26…` ran the unchanged bound Set artifact
`40405b62c3f6…` in Auto and Off with the same output. Both traced profiles
accounted for all 418,905 binding-slot instances in native storage. With
function-level profiling Auto kept 190,249 frameless SSA calls and 172,855
machine field-reference loads; diagnostics profiling disables the frameless
entry for site census as before. The bound Set seed 17/503 and redirected
stdio journeys passed. The separate REPL/bound heap journey checks that
distinct closures can still read and write one captured location after live
heap collection. The targeted pending argument property overwrites the
source after taking a snapshot and verifies that the packet's unchanged
dependency resolves to the original value. The existing 100,001-step
self-tail permutation and captured machine-call journeys passed.

##### Implemented: native pending index and builtin backing allocations

Scoped x64 heap blocks and frame segments reserve one aligned native owner
word outside the guest-addressable lanes. A zero word means the owner has
no pending slots. Pending writes open an unmanaged sparse table on first
use; entries are keyed by both physical offsets and carry numeric
completion IDs. The CLR completion adapter maps IDs to existing scheduler
tasks and does not index guest locations. Aliases therefore share the
pending state without keeping a CLR allocation key. Reads and writes when
no work is pending skip the owner word and the adapter.

The native table uses linear probing with distinct empty and deleted
entries, a conservative occupied load, and rehashing when it accumulates
deletions. This is the same empty/deleted distinction described in the
[Swiss Tables design notes](https://abseil.io/about/design/swisstables),
without their SIMD metadata or the high-load algorithm of
[Linear Probing Revisited](https://arxiv.org/abs/2107.01250). Small owner
tables do not justify either. Allocation and release use the runtime's
[native allocator](https://source.dot.net/System.Private.CoreLib/src/runtime/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/NativeMemory.cs.html);
no guest slot is pinned on the CLR heap.

Removing the last entry returns the table to the native allocator. Frame
reset drops all entries before the stack segment can be reused; interpreter
disposal retires the remaining indices before releasing the guest slabs.
The collector retains its existing deferral while pending work is present.
The existing task handles completion, cancellation and failure provenance;
a producer does not retain or write a frame-cell pointer. The compatibility
overlay for externally materialized CLR storage still exists and is not
used when a native owner word is present.

Builtin Scope records (regex match/group, reflection, environment, process
table, zip and enumerate) and the migrated materialized builtin Range
backings now allocate directly from the same guest arena as compiled
records. Directory snapshots share the allocation route of their importing
interpreter. Standalone host-library callers retain their existing API and
do not need a VM arena. Root traversal already recognizes Range snapshots
and visits their backing; arena allocation does not make a Range adapter
its lifetime authority.

These changes do not finish the writable memory boundary. Scope field
carriers can still contain host-backed String/Path values; lazy Range and
future composition still use managed control and payloads. Generic
interpreter readers still materialize allocation views where they cross
the address API, and some builtin inputs still use managed staging.

The Linux x64 Native AOT candidate `111605193693…` retained the same output
from bound Set `40405b62c3f6…` in Auto and Off and from detached Set seeds
17/503. Auto's function profile recorded 190,249 frameless SSA calls,
172,855 direct reference loads, 3,868 builtin arena allocations and 106
pending entries added and retired. The gated detached-stdin fixture opened
three native owner tables, recorded eight successful lookups, and retired
all five pending entries; its Scope returned before input was written.
The Range materialization and Unicode regex journeys passed, including
the latter's source, relocatable and bound executable stages. The
collections O2 source journey has the same two failed assertions in this
candidate and in `e5e77bc69334…`; it is not a passing receipt.

For the unchanged Set artifact, five telemetry-off process pairs on CPU 6
gave 1.658/1.659 s baseline/candidate medians. The median within-pair change
was +2.6%, with two faster pairs and one +26% outlier. This does not
establish a speedup or confirm that the earlier binding regression is
gone. Receipts are under `.temp/set-cross-scope/guest-builtin-*`.

##### Implemented: operation-local native addresses and text backing

The interpreter and machine-tier bridge share ready indirect load/store
execution. An operation-local `NativeLocation` is a stack-only value of
raw byte, initialization and reference addresses. A packed offset is
added to each relevant lane. Heap bases use block headers; a stack edge
instead names an aligned native header within its owning frame. Taking a
stack address publishes the lane pointers in that header. No host
registry entry is created for each edge, and tracing does not follow
stack edges as heap objects: their active frames are already scanned.
Heap-only machine code guards the stack tag and uses the shared bridge
on those edges. The materialized API can still recover a pre-existing
allocation view from the live thread's frame chain. Stack edges must
not escape their owner, as required by the existing storage plan. This
separates address lifetime from referent placement, as also seen in
[Lua's native CallInfo and stack state](https://www.lua.org/source/5.4/lstate.h.html).

String and Path text fields in scoped x64 lanes use tagged edges to
length-prefixed UTF-16 arena blocks. Copies of text fields share the
edge. A native String datum can compare ordinal text through a span
without constructing a CLR string. Path and String export materializes
host text; operations still relying on CLR text APIs also materialize.
The collector marks text blocks without following a descriptor or
children. Both current storage changes are in-memory only.

This replaces ready copies of supported native carriers, not every
materialized execution path. The remaining paths include pending
locations, unlinked storage/projection kinds, callable publication and
host-owned carriers. Lazy Range execution still retains CLR iterator
and closure objects. Its native form needs an opcode and child edges
for each VM plan node, and native resumable cursor state; only genuinely
external producers should retain a host lease. Encoding a managed
iterator in another handle would not migrate it.

A text edge in a transient String datum reuses the ordinary value-bit
word and an arena reference. It does not enlarge all root cells. Initial
profiles of the wider-root version sampled 57 bulk write-barrier leaves
out of 1,121, versus 16 out of 975 in the previous runtime, and sampled
the allocator 42 times in the candidate. Do not turn a storage migration
into wider copies of unrelated operands. The arena keeps exact free bins
for 16–64-byte blocks and still coalesces adjacent free space before
placing runs in bins; larger Scope allocations no longer search the
text-sized population. Samples are evidence for this change, not a
cycle-level attribution.

The Linux x64 Native AOT image `9aaafc690c55…` ran the unchanged bound Set
`40405b62c3f6…`. Off used 501,910 native indirect loads and 67,623 native
indirect stores; Auto used 144,355 and 40,566 in the interpreter/bridge and
retained 172,855 machine-direct reference loads. Both runs produced byte
identical output to the reference, had no nonnative-owner outcomes, and
recorded 27,849 boundary-carrier outcomes. Unicode regex, line snapshots,
gated stdio and live aliases across collection passed. A separate
loop-site-generativity compiler fixture fails before runtime with
`HirLocationId { Value = 35 }` on both the pre-change and candidate builds.

Five telemetry-off Auto pairs on CPU 6 gave 1.889/1.741 s baseline/candidate
medians and a median paired change of -7.8% (four faster pairs; a noisy
window, not a general throughput claim). The previous comparison image
was `111605193693…`. Receipts: `.temp/set-cross-scope/raw-bins-*`.

##### Implemented: native pull plans and callable carriers

A native Range plan is an arena block with an opcode, immutable operands and
tagged child edges. The current opcode set includes numeric sequence and
repetition, take/drop/stride, same-item-kind concatenation, and UTF-16
character/line/word cursors. A cursor allocates native state for the maximum
active depth, not one state array for the expanded graph. It retains only
the active path and its counters; concat opens the right child after the
left ends. Two opens of one plan have separate state. The cursor stored in
a VM frame owns this native workspace until close or frame release, and
the collector treats its root as live even if the operand slot was
overwritten. Only actual cancellation tokens are kept by the host adapter.
The plan/cursor separation is similar to the program/cursor model in
[SQLite's VDBE](https://www.sqlite.org/vdbe.html) and the explicit stream
state in [Stream Fusion](https://www.cs.tufts.edu/~nr/cs257/archive/duncan-coutts/stream-fusion.pdf);
this implementation does not require fusion to preserve lazy demand.

A matching sequence still lowers to the established inline sequence cursor.
Composed loops use the native pull engine. Numeric selectors consume it
directly, and materialization stages Numbers in unmanaged memory before
publishing typed snapshot storage. The range tag traces either a plan child
or retained native text. Text results copy a UTF-16 slice into a new
immutable arena block, without constructing a CLR string for the slice.
Pure String span operations cover comparisons, length, search, trim, concat
and substring. Remaining text APIs and Path operations that still consume
materialized values have not been migrated.

Native indirect copies now publish callable cells directly using the
capture-vector pointer and optional callable lane in the owning block's
native trailer. The collector reads the native capture pointer rather than
resolving the descriptor. Native copies can transfer existing host boundary
leases by slot address when needed, without reconstructing a guest
allocation view. Matching machine carriers copy their bytes and extent
initialization flags directly. Pending and genuinely materialized operands
still enter the existing compatibility paths when those cases occur.

This opcode family does not cover the whole Range language. VM callback
transforms and filters, flattening, general item carriers, sorting/merging
and host-stream composition still use the existing datum iterator paths.
The adapter that enumerates a native plan for those consumers is not the
VM resume state. The subsequent migration needs a tagged native item and
an explicit callback-request/resume protocol, not iterator IDs.

The Linux x64 Native AOT candidate `c2d1ae79607e…` ran the unchanged
Set artifact `40405b62c3f6…` with byte-identical Auto and Off output.
The former 27,849 boundary-carrier outcomes dropped to zero in both
modes, and all other indirect outcome counters remained zero. Off
recorded 529,671 native loads and 67,711 native stores; Auto recorded
172,116 and 40,654, plus the same 172,855 machine-direct reference loads.

The detached Range journey recorded twelve plans, ten native child
edges, five cursor opens, and ten yielded items in both modes. It tests
independent traversal, early close, non-demand of a stalled sequence
under take(0), and Unicode character/word slices from real stdin. A
separate live-cursor probe overwrote the original Range slot and
allocated cycles inside the loop: it printed 1234 with 19 live
collections. Captured machine calls (Off/Auto/Force), numeric Range
machine execution, native heap/alias, redirected stdio and Unicode regex
journeys passed. The five CPU-6 telemetry-off pairs against
`9aaafc690c55…` gave 1.645/1.657 s medians and a -3.6% median paired
difference; the wide variation does not establish a throughput change.
Receipts: `.temp/set-cross-scope/plans-final-*`.

##### Implemented: native callback requests, snapshot leaves and ordered workspaces

Context-free, ready guest callbacks link as plan nodes. The child edge
names the producing plan and the callback edge names a typed native
Function cell, including its capture edge. The native cursor stores an
unboxed input row, an index for the parent continuation, and the
callback cell address. On request, the VM calls the compiled function
with a native argument packet; on return it acknowledges the request,
either preserving/discarding the filter input or publishing the
transform result in a native workspace cell. No CLR iterator or
per-request handle stores the continuation. This separates callback
invocation from the state required for resumption, like the
[continuation boundary in Lua's API](https://www.lua.org/manual/5.4/manual.html#4.5),
although this path is deliberately limited to ready callbacks.

A snapshot leaf retains a raw backing allocation, item count, byte and
reference strides, and a linked type descriptor ID. This supports ready
non-scalar values, including Scope values. Natural sort stores unmanaged
rows and performs stable bottom-up merges; natural merge latches two
unmanaged child rows and advances only the selected stream at the next
demand. The input plan remains a root of those rows. A cursor closes
nested workspaces on exhaustion, take truncation, explicit close, or
activation release. Ordered workspaces do not yet consume callback
plans, whose result scratch would otherwise be overwritten by the
next pull. Comparator-based ordering and effectful callbacks (including
callbacks with captured pending work) still use their existing paths.

Native casing writes the Unicode mapping, including final sigma,
directly to a pre-sized UTF-16 block. Native repeat and nonempty-pattern
replace also produce UTF-16 in arena memory. Ready text comparisons can
read the String or Path lane without constructing a managed Path.
Ready POSIX Path joins preserve the working directory, home and
connection fields by copying their native edges, and replace only the
spelling. Windows-specific projection, other Path operations and
remaining text consumers have not all crossed the boundary.

The Linux x64 Native AOT candidate `52fc2c89aa9b…` ran the unchanged
bound Set `40405b62c3f6…` with byte-identical output in Off and Auto;
all indirect fallback counters stayed zero. Set did not dispatch any
Range callbacks. The detached
`tests/Msync.EndToEnd/native_range_callback_journey.py` fixture covers
that missing surface: thirteen callback requests and thirteen
acknowledgements, a Scope-valued transform, stable natural ordering of
numeric and String snapshots, a two-child merge, Unicode final sigma,
repeat and replace, two native POSIX joins and a native text comparison
in each tier. The separate deferred callback mutation and captured
field journeys verify behavior, not native migration of effectful
callbacks. The original native lazy-plan, Unicode regex and
heap/alias journeys also passed. Receipts:
`.temp/set-cross-scope/callback-ready-*`.

##### Implemented: fuse Range demand, not a second instruction machine

A Range uses ordinary ownership: immutable plan blocks and shared child
edges in the guest heap, per-traversal state owned by the activation's
native cursor, and a separate workspace only for operations that need
one. Fusion reduces the number of plan stages the consumer walks. It
does not introduce a virtual register file, a second instruction stream,
or an emulation of the target CPU.

Adjacent take/drop/stride nodes with the same cancellation origin link
as one demand window. A window selects ordinals
`start + interval * k` and has an independent upstream demand fence.
Drop advances the first selected ordinal, stride multiplies the
interval, and a positive take caps the fence at one past the last
requested selected ordinal. Take(0) sets the fence to zero. When
geometry cannot be represented or cancellation origins differ, the
ordinary semantic composition remains in the plan. The shared source
node remains immutable so cursors over aliases are independent.

The fence is not a cardinality estimate: `drop(take(source, 2), 2)`
can yield nothing and still has to request up to two items from
`source`. Conversely a take fence must not probe for one more item,
because that could observe a pending effect or a sequence-precision
failure. The cursor uses relative countdowns rather than accumulating
an ordinal across an unbounded input. A window over a numeric sequence
pulls and selects in one kernel with one cursor entry; it still steps
the source in order rather than algebraically jumping floating-point
state. This is demand-oriented [stream fusion](https://www.cs.tufts.edu/~nr/cs257/archive/duncan-coutts/stream-fusion.pdf),
not [fusion of sibling CPU-oriented loops](https://llvm.org/docs/LoopFusion.html).
Callback, ordering and host boundaries remain explicit.

Linux x64 Native AOT `ce3cdb8e5ae8…` ran the unchanged Set executable
`40405b62c3f6…` with byte-identical output in Off and Auto and zero
indirect fallback outcomes. The detached Range fixture in both modes
recorded three absorbed windows and two root sequence kernels. Its
positive fence probe does not demand a stalled second sequence step;
the otherwise identical two-item fence does and reports the precision
failure. A bounded model compared output and the exact upstream probe
trace for 110,592 chains of three operations with short producers and
consumer demands. The numeric machine and callback/ordered Range
journeys also passed. Receipts are under
`.temp/set-cross-scope/range-fusion-final-*`.

##### Range window benchmark and cancellation check hoisting

An open cursor is driven with the invocation's consumer token. At push,
the cursor now records whether a plan node's cancellation token is
distinct. The ordinary consumer check still runs for each upstream
step. Equal producer/consumer tokens already observe the same
cancellation; they no longer cause repeated host-table lookups in the
window kernel. A distinct origin still has its own check at the same
demand boundary. The cancellation property exercises both cases and
verifies that take(0) never observes cancellation of its unused producer.

The Linux x64 Native AOT benchmark froze one `range-window.mexe`
(`24a87543b33c…`), read 1,500,000 from stdin, selected that many Numbers
after drop/stride/take, and checked the exact sum `4499998500000` on
every run. The profiled fixture recorded two window fusions, one root
kernel, and 1,500,000 yields. Five alternating Auto process pairs pinned
to CPU 6 with telemetry off measured:

| Comparison | Range wall medians | Range median paired change | Frozen Set median paired change |
| --- | --- | --- | --- |
| Pre-fusion `52fc2c89aa9b…` vs. fused `ce3cdb8e5ae8…` | 786.3 → 591.0 ms | −25.1% | +0.9% |
| Fused `ce3cdb8e5ae8…` vs. hoisted `1cc10f21162f…` | 599.8 → 471.8 ms | −22.7% | +1.6% |

The Range candidate was faster in all five pairs of each comparison.
The small, mixed Set changes are not evidence of a stable regression or
gain. Off and Auto still matched byte-for-byte on the unchanged Set
artifact `40405b62c3f6…`, with zero indirect fallback outcomes. Native
Range plan and callback journeys passed, as did the distinct/shared
cancellation property. Reproduce with
`tests/Performance/NativeJit/measure_range_window.py`; raw receipts are
in `.temp/set-cross-scope/fusion-bench/`.

##### Set: native nullable Scope carrier adaptation

On the unchanged Set fixture, two baseline samples (`1cc10f21162f…`)
placed 164 of 917 leaf samples in TierBridge. A fixed-slot-copy probe
did cover the workload (390,334 copies in `_set_child` alone), but its
process medians were 1.581 and 1.579 s with mixed pairs; that change
was discarded.

The retained change extends the existing native Scope copy lowering
for a linked Convert/Narrow whose operand has already discharged layout
admission. Widening writes the known Address discriminator and copies the
packed interior offsets and both reference words. Narrowing requires the
Address discriminator before writing the exact-address carrier; a Null
tag, uninitialized slot, pending state or host lease still takes the
original semantic edge. Neither artifact format nor guest Scope identity
changes, and the ordinary equal-carrier Copy rule is unchanged.

For the same bound Set `40405b62c3f6…`, Linux x64 Native AOT
`9c5b79432131…` against `1cc10f21162f…` yielded five faster telemetry-off
Auto pairs on CPU 6 (1.651 → 1.549 s process medians, −3.5% median paired
change). The separate profile counted 143,910 native carrier adaptations;
bridge-direct operations went from 1,776,092 to 1,632,182, with the
same 1,362,902 bridge invocations and 172,855 direct field reference loads.
The new path removes work from existing batches; it does not assert fewer
bridge transitions or change Set's algorithm. Byte-for-byte output
matched in Off and Auto, with zero indirect fallback outcomes. Detached
Set seeds 17 and 503 in Force, REPL/CLI heap and live-alias journeys,
optional source sugar, and a focused Null/non-Null alias mutation
(Force and Off) passed. Receipts:
`.temp/set-cross-scope/set-opt/{samples.json,fixed-copy-*,carriers-*,nullable/}`.

### Set: forward Scope field loads into their final frame slots

The frozen Set spends 130,070 executions in the two child-field paths of
`_set_child` that copy an already loaded optional Scope into the join slot.
A linked VM instruction can use the existing field-load handler and write that
slot directly. This removes both a dispatch and a payload/reference-lane
write, rather than caching a virtual register or scheduling host instructions.
Compare [CPython's basic-block superinstructions](https://github.com/python/cpython/blob/main/Python/flowgraph.c)
and [Ertl and Gregg's interpreter superinstructions](https://mural.maynoothuniversity.ie/id/eprint/10188/):
the transferable idea is dispatch elimination; the frame-cell forwarding proof
is specific to msync.

Linking only absorbs an adjacent same-carrier Copy with no alternate entry,
whose input has one definition, one read and the compiled raw-writable proof.
Arguments are not elided; the final destination must also be a private frame
cell. The field offset, reference payload and owner do not change. A pending
load still forwards the same future through the existing load semantics, but
the intermediate never owns a copy. Linked spans keep portable PCs and every
logical visit/edge; a failed field load keeps the original access source span.
This is an in-memory link, not a new artifact encoding, pass over the escape
graph, or change to the native emitter.

Linux x64 Native AOT `2d17e48e5afe…` versus carrier-only `9c5b79432131…`,
frozen bound Set `40405b62c3f6…`, CPU 6, seven alternating telemetry-off
pairs per tier:

| Mode | Process medians | Median paired change | Faster pairs |
| --- | ---: | ---: | ---: |
| Off | 2.894 → 2.530 s | −11.2% | 7/7 |
| Auto | 1.753 → 1.715 s | −2.0% | 4/7 |

Off has a consistent win. Auto is mixed: the reference-load and bridge
invocation counts are unchanged, as are carrier adaptations. Both modes
report 132,654 absorbed logical copies, 130,070 in `_set_child`, and match
the same byte output oracle. Off has identical logical instruction/edge
metrics to baseline (4,547 rows). The detached Set order-alias journey
also observes 15,069 fused copies in both modes, and seeds 17/503 (Force),
nullable Scope alias mutation (Off/Force), heap/REPL, lazy Range plans,
transform/filter resume, ordered Range and Path/text journeys passed.
Receipts are under `.temp/set-cross-scope/set-opt/` (`fusion-*-timing.json`
and `vm-fusion-*`).

### Set: keep the generated-tier bridge frame small

Sampling the frozen Set with the linked Scope fusion still found the largest
single leaf hotspot inside `TierBridge`. Mapping samples to Linux x64 Native
AOT instructions found 136 of 1,384 samples in its entry's stack-zeroing loop,
before it executed a VM operation. The bridge crossed 1,362,902 times in the
separate Auto profile. Inlined paths in the bridge had reserved 4,104 bytes
of stack locals and spills plus saved registers; the prologue also probed
the stack page. See the [.NET codegen description of prologue GC slot initialization](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/ryujit-tutorial.md#gc-info)
for background; the byte count and hotspot are from the disassembly of this
binary, not an estimate from source.

Keep iteration, cancellation fuel, pending propagation and tier-state refresh
in the bridge. Call the shared linked-VM handlers for synchronous memory and
value operations instead of rebuilding their operands and results inside the
bridge. Use separate non-inlined helpers for readiness/refinement and for
observed side-exit classification; don't suppress stack initialization or
disable GC diagnostics. The existing direct/fallback classification and
portable instruction/edge attribution remain. No new guest frame state,
serialization fields, machine instructions or allocation policy.

Extracting wide-value and observation work reduced the prologue reservation
to 1,512 bytes, removed the stack probe, and yielded a paired Auto median
change of −8.3% versus linked fusion alone (7/7 faster on CPU 6, same artifact,
telemetry off). Moving the remaining duplicated memory operations onto
linked handlers reduced the reservation to 312 bytes; the intermediate
A/B against the 1,512-byte version yielded −1.3% (4/7 faster), not a proven
incremental win. It removes duplicated semantics and the final build is
compared directly against the original linked-fusion baseline.

The final AOT `6fb87331892b…` against linked fusion `2d17e48e5afe…`
ran the same bound Set `40405b62c3f6…`, with identical stdin and output,
seven alternating CPU-6 Auto pairs, recording off. Process medians were
1.623 → 1.501 s, paired median −9.5%, seven faster pairs. Profiled runs
have identical counts for 1,362,902 bridge entries, 1,632,182 direct bridge
operations, 776,266 fallback bridge operations, reference loads, Scope copy
forwarding and all indirect-access failure reasons. The instructions-level
profile matched all 4,643 instruction, edge and call records. Detached Set
seeds 17/503 (Force), live order aliases, REPL/native heap reclaim, piped
pending Scope/stdin (Force), callback/ordered Range, and numeric Range with
machine verification passed. Disassembly, sampled PCs, A/B runs and profiles
are under `.temp/set-cross-scope/fusion-next/`.

A second three-run sample of the final binary found only 2 of 1,264 samples
in the zeroing loop (74 in the whole bridge). Guest calls appeared on 500
sample stacks; frame initialization, argument locations and storage copies
remain candidates for the next isolated investigation. Inclusive call counts
do not by themselves establish an argument-copy cost.

### Set: packaged 0.9.1 versus the current candidate

The 2026-11-18 focused corpus comparison used the packaged Linux x64
0.9.1 executable (`40bd25f280c0…`, not the earlier development image) and
the bridge-dispatch candidate (`6fb87331892b…`). Both used the same
standard/17 fixture and independent output oracle. Fresh CLI runs were
pinned to CPU 6 with Auto tiering and telemetry off, one warmup per image
and route, and five alternating timed pairs. Each image produced its own
O2 portable and bound artifacts outside timed execution.

| Entry point | Packaged 0.9.1 median | Candidate median | Median paired time change |
| --- | ---: | ---: | ---: |
| Bound executable | 3.138 s | 1.388 s | −55.9% |
| Portable object | 3.200 s | 1.435 s | −55.9% |
| Source | 3.554 s | 1.730 s | −51.3% |

The candidate won all 15 pairs and both images passed every output check.
This is a whole-product comparison on one standard seed, not an attribution
to the bridge change or evidence for other workloads. Use the frozen-artifact
comparison above for bridge attribution. Raw samples, hashes, and fixture
identity are in `artifacts/benchmarks/set-dedup-v091-vs-6fb87331892b/`
(`corpus.json` and `bridge-ablation.json`).

#### Cumulative Set result against packaged 0.9.1

The packaged Linux x64 CLI (`40bd25f280c0…`) was directly paired
against the direct-cursor build (`13987c81d973…`), rather than chaining
earlier ratios. The standard/17 fixture is byte-identical to the
earlier bridge-dispatch comparison (8,192 events and 4,096
queries/expirations); there is no Set policy change in the worktree.
Both images produced their own O2 portable and bound artifacts
outside timing. Seven alternating pairs per route ran on CPU 6
with Auto tiering, one warmup, and telemetry off. All 21 pairs
favor the newer build and every run matches the independent oracle.

| Entry point | Packaged 0.9.1 median | Direct-cursor build median | Median paired elapsed change |
| --- | ---: | ---: | ---: |
| Bound executable | 3.088 s | 0.572 s | −81.72% |
| Portable object | 3.008 s | 0.562 s | −81.68% |
| Source | 3.269 s | 0.741 s | −77.27% |

A separate same-artifact run compares the bridge-dispatch image
(`6fb87331892b…`) with the same newer build on the frozen bound
executable (`894232663310…`) and the same input/oracle. In seven
Auto pairs on CPU 6, the process medians are 1.326 → 0.539 s,
with a paired median of −59.94% (7/7 faster). This controls for
artifact generation; it measures all execution-side changes in that
interval, not CALL or direct Range consumption in isolation. The
first comparison is whole-product evidence for the Set fixture, not
a general speedup claim. Raw timings, operand identities and setup
are in `artifacts/benchmarks/set-cumulative-091/`.

### Set: residual cost after bridge compaction

On candidate `6fb87331892b…`, the frozen bound fixture
`40405b62c3f6…` still enters regular VM frames for small tree-access
helpers. The Auto function profile attributes the following to those
helpers, summing the two hot `_set_key` variants:

| Helper | Calls | Copied arguments | Logical instructions |
| --- | ---: | ---: | ---: |
| `_set_child` | 130,070 | 390,210 | 2,861,540 |
| `_set_key` | 126,950 | 253,900 | 1,650,350 |

Together these account for 69.4% of the 370,558 regular activations and
75.2% of the 856,976 copied arguments. Of all regular activations,
370,520 reused their frame storage, so allocation churn is not the
explanation for this boundary. `_set_child` used the native reference
load on 130,039 calls and still performed a layout check on every call
(130,039 native and 31 linked). Portable instruction counts include
absorbed operations and must not be interpreted as host instructions.

The ordinary Auto profile records 190,249 frameless default-comparator
calls. The instructions-level profile intentionally disables that path
(`NeedsSites` in `TryRunSsaDirect`); its 3,608,273 comparator instructions
and higher bridge totals are not representative of ordinary Auto. In the
three-run, telemetry-off task-leader sample, exclusive leaves include
`TierBridge` 74/1,264, `Call` 44, `NativeArguments.Location` 22,
`NativeTierMemory.Reset` 21 and `NativeFrame.Initialize` 19. These
are sampling counts, not additive timings with the inclusive caller
chains. Hardware cache-miss cost remains unmeasured.

Prioritize a bounded experiment eliminating the helper activation on the
predominant traversal path rather than making another generic copy
primitive faster. Keep shape/pending checks until they are proved away,
do not assume comparator purity or immutability through a policy alias,
and hold comparison semantics and tree work fixed. Measure the reduced
activation/copy counts separately from telemetry-off paired wall time.
The earlier source-inline control reduced entries without a speedup;
plain expansion is not sufficient evidence. Reproduce the attribution
from `fusion-next/final-profile.sqlite` (`vm.function` metrics) and
`fusion-next/final-samples.json` (task-leader PC stacks), under
`.temp/set-cross-scope/`.

### Set: call ABI and the unused tree argument

The frozen Set executable (`40405b62c3f6…`) and the candidate-produced
corpus executable (`ed042023795d…`) agree on the hot call plans:
insert 107:81/87 and find 116:30/38 enter the two key variants and child
helper using `Snapshot=false` and zero borrowed parameters. These are
statically targeted calls. The ordinary LLVM-to-Native source and callable
lowering emits copy-mode call records; the VM consumes the recorded plan
and does not rediscover borrowing. The proof-based Native storage planner
can request snapshot/borrow for programs it plans; its existence does not
imply that these call sites borrow. On the frozen fixture, the function
profile recorded 856,976 copied arguments, zero borrows and zero ordinary
snapshots. Tail-packet transfers have their own counters.

The ordinary call builds a frame-slot view, not an argument array.
Entering a reused activation still initializes its parameters:
`NativeArguments.Initialize` resolves the caller location and
`NativeFrame.CopyArgument` copies into the callee's slot with
`NativeMemory.CopyCompiled`. The copy preserves Scope identity and
pending dependencies; it is not a subtree clone. The direct VM return
normally writes the caller's result slot. Neither a direct target nor a
JIT-compiled callee implies machine-to-machine calls; this profile has none.
The scalar comparator enters SSA via a runtime wrapper that packs ready
values into a stack state buffer, without entering a regular VM frame.

Function 81 (`_set_child`) has three physical parameters in both artifacts,
but its 34 portable instructions do not read parameter slot 0 (`tree`);
its layout is already baked into the emitted field offsets and refinement.
The generic frame initializer nonetheless copies it on all 130,070 calls
in the frozen profile. This is a bounded dead-argument experiment, separate
from changing the calling convention or removing the whole frame. A private
specialized ABI must preserve evaluation/effects of the original argument and
leave public and dynamically callable signatures intact. Inspection receipts
are in `.temp/set-call-path-inspection/`; `plan.txt` comes from reading
the serialized `NativeProgram` with `NativeArtifact.Read`.

#### Where the slot transfer spends work

The current ordinary argument path resolves a source kind and an indexed
slot (`IReadOnlyList<int>`), computes both frame locations and their carriers,
then enters `NativeMemory.CopyCompiled`. The latter probes pending state,
selects an equal-carrier or kind-adapting transfer, and publishes the
destination state. Equal-carrier Scope transfer copies separate raw and
reference lanes, then marks the destination initialized; it does not
dereference the node or clone anything. Most probes need not go past the
fast no-pending branch. Frame exit separately clears roots and initialization
state so the frame can be reused.

The final Native AOT disassembly retains an interface dispatch in
`NativeArguments.Location` and separate copy helpers. In the 1,264-sample
telemetry-off receipt, 86 task-leader stacks include
`NativeArguments.Location` or `NativeArguments.Initialize` (union, not sum).
Their leaf counts include 22 in `Location`, nine in argument `Initialize`,
two in interface dispatch, 15 in the two root-bank copy functions, and one
in the out-of-line `SpanHelpers.Memmove`. Sampled leaves are not individual
operation latencies, and inlined work is not counted by the name of its
original function. The evidence points to the full transfer protocol, not
to slow raw stores or a measured cache bottleneck. The earlier fixed-slot
copy experiment targeted guest instructions inside the function body, not
the caller-to-callee transfer, and gave no stable wall-time win.

### Proposal: a physical frame ABI for executable code

Use the existing nonmoving segmented guest stack as the backing for an
explicit guest SP/FP and an ordinary CALL/RET convention. A stack segment
is arena storage with LIFO frame ownership, not a GC object per call.
NativeGuestHeap remains the allocator and tracer for Set nodes, escaping
locations and closure environments. A reference into a frame must not
outlive that frame.

The design targets linked execution, not the elimination of SSA values or
logical locations in the compiler. Lower ordinary locals to a frame base
and a constant displacement before dispatch. Keep the current raw-value
and reference lanes initially: a typed operand can use a constant in each
lane. Address-taken locations, dynamic views and genuinely pending values
are not plain scalar loads. Encode relative offsets, entries and frame
descriptors, never process pointers; execute with actual bases after
linking. First establish this physical form in the linked image without
changing Native-2 serialization. A serialized physical form, if needed,
is an explicit artifact contract change, not a second permanent executor.

The minimum productive calling convention is more than a new opcode:
ordinary calls should not execute a per-parameter initialization loop. A
typed outgoing argument window becomes the callee's incoming storage,
with the caller publishing GC-live entries during preparation and the
callee owning the window once committed. Place new values directly there
or coalesce compatible dead temporaries; preserve evaluation order and
liveness across nested evaluation, and move already-live or incompatible
values only where required. Reserve the actual window before filling it
so crossing a segment is a normal call-admission case, not an obligatory
second copy. The caller provides the result destination. CALL saves a
minimal return record and installs the next physical frame; RET publishes
the result before restoring the caller. Dynamic target/host transitions
and pending/work-owning exits use explicit boundary records and the same
return/unwind contract rather than a different VM stack.

Start by scanning live published reference-lane extents from native frame
records at existing collection points, not by adding per-load validation
or introducing precise liveness maps everywhere. Do not clear dead bytes
just to pop a frame; ensure reference regions cannot expose stale edges
on publication, keeping prompt release for real pending and host leases.
A machine-only temporary derived from a heap base can use direct pointer
arithmetic. Across a GC point its owning edge must be traceable; the
current nonmoving collector needs liveness, not relocation. JIT lowering
can keep frame bases in host registers, emit fixed displacements, and
eventually map eligible CALL/RET to host control transfers while
publishing the same frame/root contract.

Use a bounded Set helper path as the first discriminating experiment:
ordinary Off execution and Auto must agree on output; count args placed,
moved and adopted, physical frame entries/segment growth, bridge exits
and stack roots separately from telemetry-off paired wall time. Compare
against the current helper's already-native field read, not against an
older interpreter. Before broad rollout, cover deep segmented calls,
tail-argument permutation, an escaping alias across collection, and
pending/cancellation unwind. The architectural success criterion is
eliminating unnecessary work in linked execution; neither a hotter
microbenchmark nor renaming a slot number to an offset is sufficient.

Design precedents: [Wasmtime Pulley physical control flow and stack
operations](https://docs.wasmtime.dev/api/pulley_interpreter/encode/index.html),
[Lua 5.0 argument windows, Section 7](https://www.jucs.org/jucs_11_7/the_implementation_of_lua/jucs_11_7_1159_1176_defigueiredo.html),
and [LLVM base/derived roots](https://llvm.org/docs/Statepoints.html#base-derived-pointers).
These are precedents for individual mechanisms, not templates for
replacing msync's language or GC contracts.

#### Implemented slice: linked physical argument placement

Fixed-target, no-snapshot, nonborrowed calls with fixed equal-carrier
locations now link one physical argument plan. The linked image resolves
the source and destination byte/reference displacements once; entry
places values in the fresh callee frame without resolving per-argument
slot descriptors. Pending or non-native owner cases keep the same
physical displacements and perform semantic copies. The native path
publishes initialized bytes and copies reference edges; it retains the
address-keyed lease when the source is a host token. No caller slot is
mutated and no guest object is cloned.

The bind compiler marks unread formals in the existing storage-plan byte
for context-free bodies without wide frame accesses. Referencing a
formal's location counts as a use. Runtime loading does not scan the body
or redo an alias proof. The physical entry omits only the destination
write, so argument expressions and the logical calling signature stay
unchanged. Older Native-2 producers simply do not supply the optional proof.

| Prototype (paired against bridge-dispatch final) | Bound artifact | Off paired change | Auto paired change |
| --- | --- | ---: | ---: |
| Prelinked moves using the generic ready copy | Frozen `40405b62c3f6…` | +4.1% (1/5 wins) | −4.3% (6/7 wins) |
| Fresh-frame transfer and unread omission, experimental link proof | Frozen `40405b62c3f6…` | −3.5% (4/5 wins) | −8.4% (7/7 wins) |
| Fresh-frame transfer and compiled unread proof, `2f6c87164d4e…` | Rebound `10b298cf84aa…` | +1.3% (3/7 wins) | −4.4% (5/7 wins) |

Each row uses an identical artifact for both arms, fresh processes pinned
to CPU 6, telemetry disabled for timing, and exact output checks. The
compiled-proof result shows a default-tier improvement in this window;
Off is mixed, not a proven regression or gain. Do not chain the rows or
attribute the difference between rows to omission alone. All times and
hashes are under `artifacts/benchmarks/physical-frame-call/`.

In the separate profile, the final plan omitted 182,135 destination
writes, including 130,070 for the unread `tree` argument in `_set_child`.
Auto placed 667,111 physical arguments and Off 1,047,609. On the standard
fixture all selected transfers used the raw owner path. The detached
live-policy and gated stdin journey exercises both raw placement and a
pending fallback in Off/Auto, with stdout produced before input is
released. Deep segmented calls, native tail storage and live heap alias
retention were checked separately. This slice does not yet adopt an
outgoing argument window or eliminate live-value copies. Those are
separate requirements of the physical frame ABI above.

The bound Auto B+ control did not clear the performance gate: separately
built artifacts gave +2.2% paired median (0/5 faster) and the same candidate
artifact gave +5.4% (3/7 faster, with substantial spread) versus the original
bridge-dispatch image. All outputs passed. The same-artifact function
profile counts 851,169 physical placements and 414,333 omitted writes;
this is not a nonhit control. Keep those receipts separate from Set and
revisit B+ before calling the general ABI a performance delivery.

#### Rejected control-transfer experiment: re-entering the caller tier

Routing work-free physical calls through the existing explicit guest
driver with caller-owned result placement balanced all 132,840 new
call/return transitions on Set. The gated stdin, Set alias, segmented
stack and tail journeys passed. On the same rebound artifact against
`2f6c87164d4e…`, the experimental image `df1d8936dfd0…` gave −2.1%
paired Off (4/7 wins) and +7.6% Auto (2/7 wins). Auto tier entries grew
from 486,967 to 619,542 with nearly unchanged direct operations. When
the caller leaves machine code for each guest CALL, returning via the
driver rebuilds/re-enters the caller tier rather than resuming its
machine continuation. Replacing host nesting with this transfer alone
is not the physical ABI. This experiment is not in the retained code.

Continue with a calling convention that can retain the caller's machine
continuation, and a single caller-prepared typed argument window that the
callee can adopt. Use the existing native stack, not another runtime
argument array or a Set-specific opcode. Start with provably ready
fixed-target leaf calls and a shaped reference return (not only scalars);
ensure the mixed-tier caller/callee state has one root and unwind owner.
Compare with the retained fixed-displacement copy path as well as the old
bridge image. The control timing receipts use the `physical-call-control`
prefix under `artifacts/benchmarks/physical-frame-call/`.

#### Implemented slice: machine CALL/RET for guest references

The x64 call linker now admits fixed direct leaf calls with an exact
guest-reference result. RET keeps the caller's machine continuation:
the callee returns the packed interior offsets and both reference words
in RDX/R8/R9 (EAX remains the status), and the caller publishes the
result directly in its existing frame. The scratch argument path
preserves both reference words and initializes its private lanes.
Pending/host-owned result and argument states exit before publishing and
replay through the regular semantic path. A leaf with a replayable
operation cannot write guest memory or a captured location. A backward
instruction displacement is allowed when the original control graph is
acyclic; it does not by itself make a loop. Fixed Scope carrier copies
can use the existing narrow/widen guards instead of forcing an ordinary
bridge. This let the existing compiler-generated Set child body link as
a leaf, rather than adding an opcode for Set.

A stricter proof lets a leaf read the caller's argument window directly.
The only live input is one fixed reference formal; the leaf returns it
either directly or through an exact, layout-proven copy immediately
feeding RET. On a machine CALL the three lane bases designate the
caller's existing storage and the copy/return pair consumes that input
directly; no parameter or local slot is written by the leaf. An ordinary
tier entry with a host context still performs its normal copy and return.
The window does not move ownership: the caller's published frame remains
the GC root owner and the leaf has no collection point. General leaves
with locals still use the shaped scratch path. Neither path changes
Native-2 serialization or requires object relocation.

On Linux x64, candidate `4560196ccc5c…` produced byte-identical output for
Off and Auto Set, and the same B+ oracle in Auto. Separate profiles show
173,438 native reference returns at 38 Set sites, including 30,235
read-only-window calls at eight sites. Only 494 `_set_child` calls still
entered a regular VM frame, versus 130,070 before native reference RET.
B+ records 36,159 native reference returns at 26 sites and no window hits;
the B+ measurement therefore also checks the shaped scratch ABI.

| Same bound artifact, Auto | Baseline | Process medians | Paired change |
| --- | --- | ---: | ---: |
| Set `10b298cf84aa…` | Physical-copy slice `2f6c87164d4e…` | 1.341 → 0.999 s | −24.2% (7/7) |
| B+ `1e517ac26194…` | Pre-physical bridge image `6fb87331892b…` | 2.649 → 2.444 s | −7.7% (6/7) |

Each used one warmup and seven alternating fresh-process pairs pinned
to CPU 6 with telemetry disabled; the raw samples and binary/input
hashes are in `artifacts/benchmarks/physical-frame-call/`
(`physical-address-adopt-*.json`). The intermediate
`physical-address-window` and `physical-address-leaves` rows are separate
experiments, not percentage contributions to add to these results.
These Auto comparisons clear the earlier observed B+ warning for the
tested artifact/window, not a broad release benchmark. No Off speedup
is claimed for the machine-only change.

The detached reference-call journey verifies the identity and field
projection cases in Off/Auto/Force, mutating the returned Scope and
observing the change on the original object. Identity takes the
read-only window; the field projection takes a physical reference
return without the window. Live Set policy, gated pending input,
native collection/retained aliases in REPL and both CLI tiers, and ABI
emission checks pass on the final source.

#### Implemented slice: published SP/FP and writable ordinary CALL windows

The segmented stack carries a physical byte/reference stack pointer and
a native frame-head pointer. A header publishes the three lane bases
and the previous header, arena identity and root extent; its first word
is also the existing pending index/address token. Active collection
scans this native chain and does not rescan the corresponding managed
frame allocation. Activations still carry roots not represented in
these lanes, such as invocation environments and range cursors. Frames
remain distinct from heap objects. This uses typed, published root lanes
at collection boundaries rather than per-PC register maps (see
[LLVM statepoints](https://llvm.org/docs/Statepoints.html) for the latter).

For one selected fixed direct site per caller, the binder coalesces
distinct exact-representation temporary arguments dead after the call
into the caller's physical suffix. The callee takes that suffix as its
writable formal prefix, installs its own header immediately before the
shared payload, and trims the caller's published reference extent.
This preserves the initialization bits and heap identity without a
parameter store on entry. CALL requires the same immediate native owner
and arena, no caller pending index, and sufficient space in the same
segment. Otherwise it uses the normal linked copy path without changing
the source. RET drops the callee and restores the saved cursors and
caller reference extent. Both ordinary and adopting calls use in-band
headers; only a selected caller reserves the header prefix before its
outgoing window. Lua's [call preparation](https://www.lua.org/source/5.4/ldo.c.html#luaD_precall)
likewise starts a callee over preplaced arguments; our layout also
publishes root ownership for separate typed lanes.

The detached writable-window journey checks 4,096 transfers with two
arguments each and zero parameter copies under Off, Auto, and Force.
The callee writes a formal and shared Scope and allocates a cyclic
graph that triggers GC while the adopted frame is active. A live-after
control does not adopt; a gated stdin control takes the pending fallback
without demanding input ahead of visible output. The stack boundary
control tests a rejected cross-segment window and a pair of nested
adoptions; deep segmented and tail calls, native return/borrowed
windows, and command/REPL heap alias retention also pass.

The first implementation used a separate fixed-size header lane per
segment. On the same Linux x64 executable and inputs it regressed B+
Auto by 3.87% paired (one of seven faster) against the published-roots
baseline `b755e1bd0ee3…`, with Set within 0.5% in both policies.
No writable window was planned or adopted on either real workload, so
these figures measure the overhead of the representation, not a missed
speedup from parameter reuse. The separate lane was removed. The in-band
candidate `d6c7b0048d0d…` gave the following paired comparisons (seven
alternating fresh-process pairs pinned to CPU 6, telemetry off, exact
output checked in each process):

| Workload | Baseline | Paired change | Faster pairs |
| --- | --- | ---: | ---: |
| Set Auto | Published roots without writable windows | +0.46% | 3/7 |
| Set Off | Published roots without writable windows | +1.24% | 2/7 |
| B+ Auto | Published roots without writable windows | +1.23% | 3/7 |
| B+ Auto | Separate header lane | +2.29% | 2/7 |
| Mutable Scope/Number window stress Off (262,144 calls) | Published roots without writable windows | −3.62% | 7/7 |

The stress fixture benefits on this comparison, while the production
fixtures do not demonstrate an improvement; the direct layout-vs-layout
comparison also does not prove that removing the lane recovered B+.
Retain the in-band representation because it removes an allocation and
an artificial header-count limit, not on the basis of a B+ speedup.
Receipts use the `physical-stack-roots`,
`physical-owned-header-lane`, and `physical-inline-window` prefixes under
`artifacts/benchmarks/physical-frame-call/`.

On the bound artifacts, 66 of 86 nonempty direct Set sites and 134 of
229 B+ sites pass at least one non-temporary input; 29 Set sites and
159 B+ sites target a closure with captures. These are overlapping
static counts, not dynamic frequencies. General adoption cannot depend
on every argument being a disposable temporary or every target having
zero captures. Give eligible callees a physical incoming prefix
independent of capture layout; put a caller's outgoing suffix beyond
its cursor, snapshot and binding state; then coalesce dead values
and place only genuinely live/incompatible inputs at the boundary.

#### Mixed placements and captured callees

The selected direct site can place only a subset of its arguments in
the outgoing suffix. The producer proof still requires distinct,
unexposed, dead-after-call exact-representation temporaries. The other
inputs are stable displacements in the live prefix; the linked move
list writes them directly into the adopted destination after ownership
transfer. An unread unplaced formal needs no write. The linked image
puts the formals first for a closure that is a window target, keeping
logical capture indices and environment initialization unchanged.
The caller's frame layout is packed as surviving registers, frame
objects and hidden cursor/snapshot/binding state, followed by the new
frame record reserve and the physically typed window. A relocated
producer is represented only in the suffix, not by an unused second
allocation or root slot in the caller.

The proof is recorded in two previously unused storage bits: one
identifies the selected CALL, and one marks each coalesced producer.
The logical arguments and snapshot specification remain intact.
A pending callee still takes its original snapshot before demand;
a pending caller or segment capacity miss takes the usual copy path.
Borrowing, type-changing transfers, addressable/bound cells and wide
frame operations are not converted into this convention. The detached
journey checks a captured Scope mutated by the callee through active GC,
one live input staged and one coalesced, with the caller's source value
preserved. The outgoing region follows the caller's normal frame state.

On the first native AOT build with mixed placement but without
compacting the prefix (`b0980c2797a5…`), the binder chose 20 Set sites
and 61 B+ sites; 17 Set and 52 B+ selections targeted captured
functions. Same-artifact runtime profiles show:

| Runtime | Completed windows | Adopted arguments | Staged stores | Total argument stores, previous → mixed |
| --- | ---: | ---: | ---: | ---: |
| Set Off | 60,199 | 173,145 | 14,732 | 1,055,339 → 947,176 |
| Set Auto | 17,771 | 45,861 | 14,732 | 371,827 → 348,520 |
| B+ Auto | 335,822 | 665,573 | 16,282 | 953,333 → 608,004 |

The candidate and the previous binary produced exact workload output
on both rebound Native-2 executables. The same-artifact paired times
were +5.41% in Set Off (1/7 faster), −0.59% in Set Auto (5/7), and
−0.90% in B+ Auto (4/7). The store reduction is real but the timing does
not establish a general speedup. The sparse layout kept the old holes
of moved producers and enlarged total frame footprints (B+ allocated
142.5 MB across frame entries vs. 132.7 MB in the control, not peak
resident memory). Compaction removes those holes while retaining one
durable typed suffix. The sparse comparison and counts are stored
with the `physical-mixed-sparse` prefix.

The compact candidate `177e3c50851a…` preserved the same placements,
actual stores and machine reference returns on the same executables.
It reduced cumulative B+ frame bytes from 142.5 MB to 140.9 MB and
reference slots from 20.98 M to 20.78 M; it did not remove the cost of
reserving each selected caller's complete outgoing suffix. Its
seven-pair control results were Set Off +3.07% (3/7 faster), Set Auto
+2.20% (2/7), and B+ Auto +3.72% (1/7). Compaction is an ownership
and layout simplification, not evidence that the latency gate passed.
Do not attribute the remaining differences to frame footprint alone.
At most 16,282 of 335,822 completed B+ windows needed a staged
store. Separating the zero-stage transition from the
general transfer path is a useful causal control; it should be
measured independently from changes to JIT entry and resume.
The compact build also passed the legacy all-coalesced artifact,
detached call-order, nested capacity, GC, and machine reference-return
journeys. Receipts use the `physical-mixed-compact` prefix.

The generic interpreter still uses logical metadata and managed
activations. JIT scratch/borrowed leaves are not yet writable adopters
of the shared frame ABI. Keep the assembly caller continuation and the
native GC root handoff in that convention rather than returning by
re-entering the caller tier.

#### Direct CALL entry and a fixed machine continuation

There is one outgoing-window marker (`OwnArguments`) and one producer
marker (`WindowProducer`); the experimental all-or-nothing marker is
no longer consumed. Rebind experimental artifacts rather than
maintaining another window convention. The direct linked handler enters
CALL directly rather than passing a `bounded` mode through the dynamic
dispatcher, the tail-call selector and the depth decision. Entry takes
the resolved executable; it does not look it up a second time when
creating the activation. The linker rejects a declared window that
cannot be placed rather than choosing another call convention. Host
calls and callee-demand paths remain language operations, not adapters
for an older CALL ABI.

Once a frame has taken ownership, zero-stage entry never enters the
parameter mover. Stack segment sizing lives in the allocation path,
not every frame push. The x64 tier lowers physical CALLs for which it
did not emit an all-machine leaf to a distinct frame-call operation
after register planning. Bridge batches stop at it; on success it
falls through to the statically linked successor, which can itself
invoke bridge work. The machine caller stays on its
original continuation, without a second tier entry or jump-table
redispatch. Errors still leave the tier at the CALL's source PC.
The callee still uses the same stack lease, root handoff, and unwind
as the interpreter; this is not yet an assembly-only writable CALL.
Reducing indirect dispatch is a design motivation, not a speedup
claim (see [Ertl and Gregg's interpreter dispatch study](https://www.scss.tcd.ie/David.Gregg/papers/toplas05.pdf)).

The entry-only experiment `04c6766acb60…` (no JIT changes) measured
−1.65% on Set Off (6/7 faster) and +0.97% on B+ Auto (3/7) against
the compact-window runtime. This isolates the change from the
subsequent direct dispatch and machine continuation. The intermediate
x64 continuation emitted only before non-bridge successors
(`35f4048c2ee3…`) observed 14,301 continuations on B+ and zero on
Set; it still allowed preceding bridge batches to execute both kinds
of call. That implementation is superseded by the frame-call operation.

The explicit operation in `1e548625a353…` completed 17,376 x64 frame
continuations on Set Auto and 333,004 on B+ Auto. The corresponding
machine-only reference-return counts remained 173,438 and 36,151.
Against the compact baseline, same-artifact paired changes were
Set Off −0.79% (4/7 faster), Set Auto +1.22% (3/7), and B+ Auto
−0.54% (4/7). These do not establish a throughput win. Detached writable
windows, machine reference returns, retained heap aliases, gated stdin,
and source/IR/REPL call order all passed. Receipts use the
`physical-frame-opcode` prefix.

The Set tree itself also still uses runtime bridge dispatch for
ordinary non-I/O operations. The recorded Set profile makes that
distinction explicit: `_set_link` and `_set_rotate` retained reference
copy sites under bridge batching; `_set_repair_insert` and
`_set_repair_remove` retained guest-field sites. These sites are
lowering and representation decisions, not intrinsic host services.
For the ready native Set case, close these operation clusters under
the physical ABI: typed field/edge operations, representation
refinement, and guest CALL/RET. Keep allocation growth, collection
and actual demand at specific runtime boundaries.

The migration target is one physical call convention, not a hot version
of a generic boxed convention. Lower fixed operand locations, result
placement, callee entry and return continuation into the linked call
record. Dynamic closures may select a target at runtime, but should
enter the same frame contract after resolving the target. Segment
growth, demand and host calls are distinct operations required by the
runtime. They must not turn an inconsistent linked call plan into a
different executor. Delete superseded encodings and rebind the
affected experimental executables rather than maintaining adapters.

#### Ready heap reference writes

The linked guest-store plan now supplies constant byte and reference
displacements for a Scope field. This covers exact, tagged nullable,
exact-to-nullable, and Null-to-nullable stores; a projected field must
resolve to the same shape and both displacements on every admitted
layout. The x64 sequence writes the two edge words and packed interior
offsets, and publishes initialization last. Assigning Null clears the
edge lane, not just the value tag. A source or destination host token
needs an address-keyed lease update, so the guard exits before any
write. The nonmoving guest collector runs between mutator operations,
not concurrently with the update; unlike incremental or concurrent
collectors, it does not need a marking barrier here (see
[LLVM's write-barrier notes](https://llvm.org/docs/GarbageCollection.html)).
It still needs both published root words. A replayable machine leaf
does not admit this store: replaying a callee after it mutates the
tree is not a valid failure path.

The first exact-representation build `8f46b83c7ee4…` placed 32,563
Set reference stores directly, with no storage or pending misses on
the bound fixture. Its bridge invocations fell from 791,911 to
774,552; bridged direct operations fell from 1,034,092 to 990,493.
B+ executed 70,318 direct stores and 11 pending exits. Both outputs
match the independently expected bytes. On seven telemetry-off
fresh-process pairs, the exact-write build measured −1.94% Set Auto
and −2.45% B+ Auto (both 5/7 faster); these small mixed effects
should not be added to any earlier result.

The nullable representation extension preserves the production
store and exit counts on the same executables. The detached link and
clear journey observes the correct alias and final Null for 8,192
cycles; Auto/Force execute 16,128 machine reference stores, versus
none in Off. The O2 red-black Set journey independently checks tree
invariants and retained Scope identity on source, relocatable, and
detached routes. Heap/GC and reference-return journeys also pass.
The final `96e9666e1f1c…` build measured −0.54% Set Auto (4/7
faster) and −3.64% B+ Auto (5/7) against the previous frame-opcode
binary. Set's reduction in actual bridge visits is stronger evidence
than the noisy wall-clock result. Keep the same-artifact receipts,
separate profiles and graph oracle under the
`physical-reference-stores` prefix.

#### Closing the ready numeric comparator and key load

At instruction detail, separate actual generic bridge entries from
operations visited in a bridge batch. A linked offset load can be
attributed to a source address instruction, so the receipt keeps both
source and linked opcode. A separate guard-entry count distinguishes
lowering omissions from runtime demands. Dedicated frame CALLs are
not part of this per-PC frontier.

For a Copy with an exact Number/Bool destination, the existing typed
machine scalar reader can consume a tagged source that admits that
kind. It still checks the active tag before reading the payload.
Captured scratch descriptors retain their old leaf restrictions;
capture-free acyclic leaves can keep nullable scalar locals in their
own frame. A backward bytecode displacement does not imply a cycle.
The original comparator's branches and IEEE comparisons are retained,
including ordered/unordered tests (see the
[LLVM floating comparison contract](https://llvm.org/docs/LangRef.html#fcmp-instruction)).
No Set opcode or separately coded comparison policy is used.

The heap-load side uses the same fixed displacement and readiness
checks for nullable Number/Bool fields as for exact scalars, copying
the tag and scalar payload into the matching rootless caller field.
An inactive payload has no independent semantics. No type conversion
or guest-reference move is inferred by the load. A pending or foreign
field does not enter this path.

The same bound Set artifact under instruction detail records:

| Native AOT image | Tier bridge invocations (includes frame CALL entry) | Machine guest calls |
| --- | ---: | ---: |
| Previous reference-store build | 1,420,005 | 173,438 |
| Scalar-copy and acyclic-leaf change only | 449,685 | 363,449 |
| Plus nullable heap scalar loads | 322,797 | 363,449 |

The comparator's three leading Copy sites had 190,218 entries each
and no guards in the original bridge-frontier probe. After linking the
comparator as a machine leaf there are no bridge entries in that body.
The two specialized key functions remove a combined 126,888 generic
entry transitions at their nullable Number offset load, taking
per-PC generic bridge entries from 432,309 to 305,421. The largest
remaining Set entries are the key-function FrameLoad sites at PC 80
and PC 29 (78,976 and 43,958 entries), ahead of their fixed-target
guest calls. Their target contains a cold range path; leaf-only
linking does not express that mixed control-flow contract. Do not
replace the policy or replay a side-effecting guest body to avoid
these entries.

Final image `f6c32dbca2ab…` produced exact Set and B+ results. The O2
red-black graph/alias journey also passed under Force across source,
relocatable, and detached artifacts, including signed zero, NaN and
nullable keys. Gated input, heap/GC, writable windows and the
reference-store journeys passed. The same-artifact Auto Set gate
against the previous image was −19.68% paired (7/7 faster,
1.004 → 0.797 s process medians; telemetry off). The B+ Auto control
was −6.06% (6/7). An independent same-artifact comparison against the
comparator-only build measured −1.99% (6/7) for the key-load slice;
do not add that percentage to the end-to-end result. Receipts use the
`physical-ready-key` prefix.

#### Fixed captured calls retain the x64 continuation

The key invocation blocked here was not a dynamic target. The
relocatable call already identified a concrete function, but its
cold Range path prevented the leaf/work-free contract. The x64 linker
now joins an adjacent FrameLoad and fixed non-tail CALL in the same
block when the private callable temporary has no other read. A ready
callable is read from its real frame location before building the
argument view. The temporary is not written. An unready location
still executes the original frame read at its source PC, preserving
demand and error attribution.

The native caller continues at the linked successor after normal
return, not via generic opcode dispatch. The callee uses the same
guest frames and work owner as an ordinary invocation, including the
existing owned driver for an effectful target. Sufficient host stack
is checked before the transition; an exhausted boundary leaves both
source instructions unexecuted. Tail positions retain their existing
guest unwind contract. Normal continuation and exceptional unwind
are distinct, as in [LLVM invoke](https://llvm.org/docs/LangRef.html#invoke-instruction);
this is not a claim that the ready callee runs without entering the
runtime. No caller/callee policy is inlined by the fused operation.

On the same Set executable, native AOT image `ffde4b9df94c…`
completed 137,607 fused continuations, all of which avoided the
callable temporary store. Generic bridge entries fell from 305,421
to 172,699 under instruction detail; the former 78,976- and
43,958-entry key-call sites no longer enter the bridge. With recording
disabled, Set Auto measured −8.82% paired (7/7 faster;
0.852 → 0.766 s process medians). The B+ control selected no fused
continuations and measured +0.87% (3/7 faster), a mixed result.
Both workloads match their independent expected output. These
percentages are relative to the ready-key image, not additive.

The detached cold-demand test checks that output from 4,096 ready
calls and the single cold-path announcement are visible before
stdin is released. Force completes 4,065 fused calls and preserves
the source of a cold-path conversion failure without resuming the
caller. The independent Force red-black graph/IEEE test, call-order
journey, gated pending arguments, and retained heap aliases pass.
Receipts use the `physical-fixed-continuation` prefix.

Next remove the per-call owned-driver traffic on the ready part of a
fixed effectful target by entering its real physical frame in machine
code. Suspend that frame at the exact work boundary; do not restart
a body that may already have written a guest field or created work.

#### Fixed-call first frame and driver handoff

An effectful fixed call now enters one real guest frame without
opening a nested guest driver. Its invocation publishes the
environment and argument roots alongside the physical FP chain,
and runs the normal tier entry and PC-specific exit handling.
If the frame returns, finish its owned work before releasing it
and publishing the result to the caller. If it produces a nested
or tail call, pass the already-executed activation to the same
guest driver used by ordinary execution. The driver consumes that
transfer once and resumes at the linked successor. It does not
invoke the entry again. On failure both paths use the same unwind.

Avoiding the extra driver boundary makes the first frame eligible
for active collection with its caller frames still published; pending
work and opaque cursors retain their normal collection restrictions.
This is an explicit frame/ownership boundary, not a replayable
scratch leaf. Starting machine code after a frame prepare operation
must preserve the same root contract at each native safepoint (see
[LLVM statepoints](https://llvm.org/docs/Statepoints.html)).

Native AOT image `60e82700bc77…` recorded 126,670 Set returns from
the first frame and no driver handoffs, with the same 172,699 generic
bridge entries as the previous image. B+ did not enter the new path.
Same-artifact paired Auto times were Set +0.45% (3/7 faster) and
B+ −1.84% (5/7). This does not establish a Set latency improvement.
The detached test takes the cold branch at the same syntactic call
site used for warming; it checks one actual driver handoff in Force,
five in-flight collections in the retained three-frame chain, a
retained cyclic alias, and one gated input. The negative parse case
preserves source attribution and does not run the caller's success
continuation. Independent Set invariants, the heap journey and
writable parameter windows still pass. Receipts use the
`physical-fixed-frame-entry` prefix.

This establishes the frame lifetime and handoff contract, but the
emitter still invokes the managed fixed-call helper. The next
native change is to prepare that frame once, then have the emitted
CALL enter its tier code using the published bases; completion
and true work exits take over the already-executed frame.

#### x64 CALL enters a published callee tier

For a fused, effectful-capable fixed call, the prepare operation
retains the already-published code page and installs the real
callee activation, its pending/root lanes and its work owner.
The x64 caller uses the prepared entry and context for its own
machine CALL. The return operation consumes this preparation once:
normal completion finishes the owned frame, publishes the result
and restores the caller's work owner. Poll, demand, guard and tail
statuses use the ordinary tier status decoder and the recorded
callee PC. The resume path uses the same activation and, if needed,
hands that activation to the existing guest driver. It never
restarts at the callee entry. A not-yet-published target still
executes its first frame normally, without another artifact ABI.

The live callee frame remains in the physical FP chain throughout
the machine entry and return processing. This is the nonmoving
collector's explicit transition contract; compare the distinction
between a native transition and its GC state in
[LLVM statepoints](https://llvm.org/docs/Statepoints.html#gc-transitions).
Entry and return still pass through managed lifecycle helpers.
The same context header also carries ordinary tier state. Its
child fields are runtime-only and do not change Native-2 files.

Native AOT image `a52554e07f2e…` recorded 126,670 caller-emitted
native entries and completed returns on Set, with no fixed-frame
resumes on that ready workload. The Force cold-path controls
each complete 4,065 such CALLs and resume one existing frame. The
nested case also records a single driver handoff and five
collections with the three frames still active. The error case
preserves the callee source and does not reach the caller's
successful continuation. ABI emission, reference-return, Set
graph/IEEE, callable evaluation order, writable windows, and
heap journeys pass.

Against the fixed-frame-entry image, same-artifact AOT Auto paired
times were Set +0.67% (3/7 faster) and B+ +1.12% (2/7). B+ did not
hit the new direct-entry path; the small mixed timings do not
establish a speedup. Receipts use the `physical-tier-transfer`
prefix. The next cost to remove is preparation/completion on the
ready path: direct stack/argument placement, code-entry lifetime
at link level, and scalar publication without a second generic
frame dispatch. Keep the precise PC and owned work state for the
actual cold exit.

#### x64 scalar publication and caller code leases

Keep one versioned callee code lease per live caller tier invocation.
Reuse it only when the next call selects the same published object.
Acquire a replacement before releasing the old lease; clearing the
caller releases the last lease. Code entry remains stable through
the callee's exit even when publication changes.

The x64 guest result ABI transports exact Number/Bool bits in RDX
alongside the existing EAX exit status. Return processing must retire
the callee and restore ownership before authorizing a machine write.
Only an unbound rootless destination with a matching representation
is eligible; pending caller locations, owned callee work and cold
exits keep the same precise continuation semantics. The caller writes
its fixed payload and initialization offsets after authorization.
There is no artifact format change.

Native AOT image `6b843ea17dcf…` publishes 126,670 scalar results
on Set; 114,408 code entries reuse the caller's lease and 12,262
acquire one. No direct fixed entry required callee frame resumption
on this workload. The Force cold paths complete 4,065 direct
entries and publish 4,064 scalar results, including a Boolean
fixture with both true and false results. The driver handoff and
error fixtures preserve single execution, attribution and active
collection. Set graph/IEEE, reference return, writable window and
emission checks pass.

The Set harness still records 172,699 generic bridge entries.
12,226 start at the harness's `io.read_line` native call, not a
Set operation (this counts guest host-call entries, not kernel
syscalls). Attributed container sites include 28,982 linked Load,
24,670 Constant, 15,332 Copy and 10,687 Store entries; these
are lowering/guard boundaries rather than host I/O. Separately,
the ready fixed-CALL path still creates/initializes the physical
callee frame and retires it using runtime callbacks. The two
specialized key functions account for 253,900 physical argument
placements with no argument snapshots. Native argument movement
alone will not remove the runtime boundary: the normal frame
push/pop, root publication and work-ownership rules must move to
the same physical guest ABI. Keep specific exits for actual GC,
pending work, error or host calls, not an old execution contract.

Same-artifact x64 AOT Auto seven-pair, CPU-6 comparisons against
`a52554e07f2e…` show Set +1.57% paired (3/7 faster), B+
+0.29% (3/7; no new fixed entries), and Set lease-only −1.74%
(4/7). Adding scalar publication to the lease-only image measured
+2.89% paired (3/7). These mixed results do not establish a
latency improvement or isolate a stable regression. Preserve this
baseline for the frame-lifecycle change rather than summing
percentages across independent runs. Receipts use the
`physical-machine-return` prefix.

#### Arguments land in the physical callee from x64

A fused fixed CALL can use its existing linked physical move plan to
place live private parameters in a freshly pushed callee. The normal
callee frame is still pushed and published by preparation. For a compact
non-window plan, preparation can leave the inputs empty and return a
placement status. The caller writes payloads, typed guest root words,
and initialization bytes at the linked offsets, then enters the callee
without an intervening safepoint. This is one call convention: the
existing managed writer runs the same plan when a caller has pending
locations, a reference carries an address-keyed host lease, or the
activation needs to read argument values to bind its tier descriptors.
An adopted writable window still uses its existing producer locations.

Like [Lua's guest stack call setup](https://www.lua.org/source/5.4/ldo.c.html#luaD_precall),
argument placement belongs to the guest stack contract rather than to
the host call convention. Here byte, initialization and root lanes
remain separate; copying a guest reference preserves the original
allocation and offset, not a second allocation. The nonmoving GC sees
the child reference lane at the next safepoint; the caller is still
published while the child is prepared. Host leases are keyed by the
address of their root cell and cannot simply be bit-copied.

The current boundary still includes managed push/pop of a physical
frame, publication of its stack view, capture initialization, and work
scope setup/cleanup. Eliding the remaining boundary needs an explicit
native stack cursor and enough activation state in the frame header to
recover the child at an actual exit. Do not require a second CALL
protocol or a per-op compatibility VM.

Image `6e01d9d333a4…` moved Set's inputs in machine code on all 126,670
direct fixed entries (253,340 live parameters and 506,680 guest root
words; no placement exits). Generic bridge entries remained 172,699.
The detached Force handoff fixture transfers a live namespace reference:
the callee mutates it, resumes after five in-flight collections, and
the caller sees all 4,097 writes through its original alias. It records
8,130 argument moves and 8,130 root words, one real handoff and one
callee resume. The scalar-only, Bool, cold error, reference-return,
writable-window, and red-black graph/IEEE checks pass.

Compared with the scalar-return image `6b843ea17dcf…`, same-artifact
AOT Auto Set is −0.23% by paired median (5/9 faster); B+ is −1.00%
(4/7 faster) and records no placement sites or hits. This does not
establish a process latency win. Receipts use the
`physical-machine-args` prefix.

For the next boundary, the native cursor should become the single
owner of SP/FP rather than a shadow of the managed lease. The raw
`TraceStack` already consumes the in-band FP chain; managed
`Active` and `stackView` are separate consumers for ownership and
address materialization. A machine-only frame must publish its
physical roots, closure and exact resume PC without relying on those
managed chains, and attach the managed view only on an actual exit
that needs it. Segment growth and work handoff should use explicit
exits; normal return must restore both payload and reference cursors.

#### One native stack cursor for linked and emitted execution

The synchronous root owns one stable unmanaged cursor. It holds the
current FP, byte/root tops, and the bases and limits of both storage
lanes (including initialization bytes). The managed segment objects
still own allocation, recycling and host-only roots. Push, adopted
windows, segment reentry and pop update the same cursor; the collector
sees its in-band FP chain. A recycled segment restores all lane bases
and limits, not just the byte stack pointer.

A direct machine CALL's argument placer now obtains both physical
headers from the cursor: the child is the current FP and its
`Previous` is the caller. Payload, initialization and root addresses
come from those headers, not from separately copied argument bases in
the two tier contexts. The placement is admitted only when the
argument owner is the live physical caller. This also works when
caller and child are on distinct segments. Native format 2 and the
56-byte x64 frame header are unchanged; only the runtime tier context
gains a cursor pointer.

This is a nonmoving stack. [Lua's stack relocation code](https://www.lua.org/source/5.4/ldo.c.html#correctstack)
repairs live stack references when its backing allocation moves; here
the segment can be replaced without moving existing addresses, so GC
and alias identity continue to use the published headers. The next
emitter-owned operation is reservation/unreservation of ordinary
same-segment frames against the cursor: a constant-size prologue
checks both lane limits and leaves segment growth to a distinct exit,
as [LLVM split-stack prologues](https://releases.llvm.org/19.1.0/docs/SegmentedStacks.html)
do for the machine stack. Managed stack views, work and cleanup still
belong to the existing activation path until they can be materialized
from the physical frame at an actual runtime exit.

Image `216dc3732b7b…` runs the same Set and B+ executables against their
unchanged output oracles. Set still places 253,340 arguments through
the 126,670 direct calls (506,680 guest root words) without a placement
exit; it uses one stack segment. B+ records 39 cached-segment reentries.
The detached depth journey makes two chains of 1,200 nested calls and
restores each intermediate alias against an independently calculated
sum. It reenters 10 cached segments under both Off and Force. Cold
demand and driver handoff (including five active collections), writable
windows, reference returns, Force Set graph/IEEE, and descriptor ABI
checks also pass. Receipts use the `physical-stack-cursor` prefix.

Same-artifact AOT Auto Set is neutral against `6e01d9d333a4…`
(−0.002% paired median, 5/9 faster). The initial B+ comparison
was +3.78% (3/7 faster); a separate repeat on the same CPU and inputs
was −2.51% (8/9 faster). The two B+ runs disagree in direction, so
they do not establish a regression or a speedup.

#### Rejected slice: split same-segment RET across the runtime boundary

The experimental image let a prepared fixed call returning a ready
scalar with no owned work or active cursors transfer its last cursor
update to the emitted continuation. Managed retirement first clears the child
roots, pending state and view; it then returns a distinct status
while the physical FP still describes that retired child. Before
publishing the caller result, x64 restores the byte SP from the
child header, subtracts the child's reference count from root SP,
and restores FP from `Previous`. The saved byte SP occupies existing
x64 header padding, so the Native-2 argument-window offset and the
published frame size do not change. No interpreter/GC call occurs
between authorizing the return and those stores.

Adopted argument windows do not follow that arithmetic: their
parent root count was truncated and must be restored. Cross-segment
returns restore the segment owning the caller and both lane bases.
Both still retire the cursor through the same stack owner. If
retirement reports an exception after handing off the cursor, the
managed error exit restores it before unwinding the caller.
This follows the distinction in [LLVM split stacks](https://releases.llvm.org/19.1.0/docs/SegmentedStacks.html)
between an ordinary return and a segment-changing return; no
alternate guest calling convention is introduced.

This slice moved physical RET only. Both callbacks still constructed
and retired a managed activation; the experiment neither reduced
native-to-managed transitions nor removed generic bridge entries. The next useful
boundary is to reserve in the current segment, publish the physical
arguments/roots, and create a managed view and work owner only at
an actual exit that needs them. [Lazy Threads](https://www.cs.cmu.edu/~seth/papers/goldstein96-jpdc.pdf)
uses the analogous separation of an ordinary stack-based call from
promotion on actual suspension; the existing guest work queue and
exact-PC continuation remain authoritative here.

Native AOT image `2d571bd38b25…` records 126,670 emitted cursor
restorations on Set, covering all scalar completions of prepared
native fixed calls. The generic instruction bridge stays at 172,699.
The Force cold fixtures each have 4,064 emitted restores and one
precise resume; the reference-carrying handoff still preserves alias
identity across five active collections. Nested depth and cached
segments, writable windows, reference results, and the detached
red-black graph/IEEE checks pass. The cursor contract test separately
exercises managed completion after detachment and confirms that
window and cross-segment returns cannot defer the cursor update.
Receipts use the `physical-native-ret` prefix.

Against the shared-cursor baseline `216dc3732b7b…`, the same-artifact
Auto Set paired median was +2.57% (3/9 faster) and a separate 15-pair
repeat on the same CPU was +7.70% (3/15 faster). B+ did not execute
the emitted return and measured +1.37% (3/7 faster). These are
process-time observations, not a cycle attribution. The repeated Set
loss justifies withdrawing the isolated split from production. Keep
the shared cursor and FP-relative argument placement; do not add a
switch to keep two return mechanisms active. Revisit the in-band
return information as part of a call that actually avoids eager managed
activation, not as a second completion protocol after the same
managed callback.

The useful admission evidence is stronger than “no syscalls”: the
unchanged ready Set profile has 126,670 direct fixed entries, no
callee frame resumes and no argument placement exits. The next
prototype should admit that ready case using the callee's published
version and linked frame shape, and hand off on the first actual
poll, guard, pending operation or work boundary. Test that the handoff
attaches to the same FP and PC and neither replays stores nor keeps a
finished callee published. Compare three independent counters:
eager managed activations avoided, exits that materialize a managed
view, and generic bridge entries; a scalar-return count alone does
not establish fewer transitions.

#### Enter owned work on the actual managed boundary

A prepared machine callee already publishes its frame and heap roots;
that does not require installing its work owner in the ambient
`AsyncLocal`. With profiling off it borrows the parent scope only
as an ownership link and allocates no private `NativeWorkScope` until
an actual bridge, child-call callback, or runtime resume. These
boundaries establish and restore the ambient owner around the
managed actions; the same private scope stays attached to the
activation across repeated bridges. When profiling is on, entering
the activation still creates its function-attributed counter owner,
but no ambient switch occurs on a machine-only return.

When an unpromoted callee returns, its parent's work must not be
mistaken for the child's outstanding work. Once the callee has
entered managed work, its own jobs and cursors still gate normal
completion, failure cleanup, and handoff. GC roots, pending
descriptors, the exact exit PC and the physical frame are unchanged.
The relevant separation is between an ABI that can suspend and
work that actually suspends or escapes, similar to the promotion
scheme in [Lazy Threads](https://www.cs.cmu.edu/~seth/papers/goldstein96-jpdc.pdf).
Avoiding ambient switches also avoids new execution contexts for
changed values in [CoreCLR's AsyncLocal implementation](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/Threading/ExecutionContext.cs).

The prepare/finish callbacks and the managed frame view still exist.
Promotion of work ownership only at real managed operations removes a
dependency from the prepared hot entry and makes a subsequent
machine-owned physical frame possible without allocating a
`NativeWorkScope` eagerly. The unobserved cold handoff control
exercises first allocation of that scope without the profiler's
counter owner, including a real pending input while caller aliases
remain live.

Native AOT image `46a6367df482…` measures 126,670 direct Set
completions without ambient work entry and zero first work-boundary
entries. Generic bridge count is unchanged at 172,699. The detached
Force fixtures each complete 4,064 ready scalar returns without
activating work, and then enter managed work on the cold iteration.
The actual handoff retains the original alias through five active
collections; the unobserved handoff and failure also retain gated
input and source-order behavior. Native emission, Set graph/IEEE,
writable-window, reference returns and depth checks pass.

Against the shared-cursor baseline `216dc3732b7b…`, same-artifact
Auto Set improves 6.10% by paired median (10/11 faster; process
medians 0.743 s and 0.661 s). B+ is −1.37% paired (5/7 faster)
and did not use the new fixed-entry path; do not credit that
difference to work promotion. The benchmark uses no telemetry;
profiles documenting boundary counts are separate observed runs.
Receipts use the `physical-lazy-work` prefix.

#### Materialize callable parts at physical offsets

Set instruction detail linked to the executable disassembly showed
that several hot bridge entries reported as Constant were i64
function code words or nullable function components, not numeric
comparison or Set I/O. The tier now emits private i64 constants,
nullable Null values with up to two guest root words, and the
capture-free subset of ComposeFunction. It writes the existing
function representation (the low 32 bits of code minus one and
two empty roots). It does not add a callable kind or alter the
artifact. This preserves the distinction between bit-pattern
literals and the operation consuming them in [LLVM IR](https://llvm.org/docs/LangRef.html#constants).

The frame layout admits exact private offsets and includes the
three input readiness locations of a compose. Before mutation,
machine code checks relevant pending state, initialization, the
Null tags of nullable components and the absence of an old
address-keyed host lease. Capture and native-name cases therefore
execute the same source instruction with the existing guest
semantics. Direct writes also clear any tracked machine closure
descriptor. The scalar register planner now explicitly admits only
its supported scalar operations; a new raw i64 constant must not
treat a constant-table index as a scalar slot.

Measure the count of actual generic bridge entry transitions as
well as native composes: an isolated direct constant between two
managed instructions can move rather than remove a boundary.

On Native AOT image `eb07c2fb323e…` the unchanged Set executable
performs 68,336 native i64 constants, 60,826 native nullable Null
writes, and 68,336 native function composes; no materialization
guard exits were observed in this workload. Generic entry sites
drop from 172,699 to 123,861 (−28.28%). The global B+ bridge
invocation series drops from 2,056,973 to 2,008,495, and B+
performs 59,526 native composes. Global invocation counts include
additional tier transitions, so do not compare them to Set's
generic-entry-only series across workloads.

Against `46a6367df482…`, unchanged-artifact AOT Auto execution with
telemetry disabled measures Set −5.42% paired (9/11 faster,
0.675 → 0.638 s process medians) and B+ −3.21% (6/7). The live
Set policy-replacement journey completes 8,756 direct composes
while mutations of the comparator retain their exact observed
effect. Independent red-black graph/IEEE, gated continuation,
reference results, and callable evaluation-order journeys pass.
Receipts use the `physical-direct-values` prefix.

#### Bind location captures at the first managed use boundary

A published tier version can defer location captures when none of
them has a tier descriptor. Direct native access to a bound cell
uses a descriptor; direct raw operations require private offsets.
By-value captures are unchanged. A fixed machine entry can thus
keep the existing environment without duplicating unused location
bindings into the frame. When all captures are deferrable,
initialization skips the capture list entirely. The admission proof
is made once against the version's tier layout, not by rescanning
callee instructions on each CALL.

The environment already travels in the active invocation and is
traced by the guest collector. This is not an unrooted capture
optimization: the closure environment is the owner until the frame
needs concrete addresses. The first managed bridge, nested call, or
exact-PC exit writes the original locations, not snapshots of their
values. The binder runs once and reuses the current frame. This is a
small instance of the state-materialization boundary in
[TraceMonkey's side exits](https://www.cs.williams.edu/~freund/cs434-exemplar/gal-trace.pdf);
msync does not replay the callee or add a second frame representation.

The detached cold-demand fixture mutates a captured String location
after warmup and before the blocking read. Its cold path must print
the new value, and its hot path must never bind those captures. The
Boolean control has a mutable Number capture read by machine code;
its bindings must remain eager. The cold demand and handoff controls
also run with telemetry disabled.

Native AOT image `2d07a59e1d29…` skips 253,340 bindings across
Set's 126,670 direct native fixed calls; none of the deferred slots
is installed on this workload. Generic bridge entries remain
123,861. The Force cold-demand control skips 8,128 hot bindings
and installs two at the only cold iteration. The Boolean control
uses its mutable Number location normally with no deferral. The
handoff retains reference identity through active GC; depth, live
comparator replacement, independent red-black/IEEE and callable
evaluation-order journeys pass.

Against `eb07c2fb323e…`, unchanged-artifact x64 Auto Set with
telemetry disabled measures −2.80% paired (9/11 faster;
0.647 → 0.621 s process medians). B+ did not execute this path
and its global bridge and direct-operations counts are unchanged.
The first B+ timing batch measured +6.48% paired (0/7 faster);
a separate same-CPU repeat measured +0.80% (4/9 faster), so the
initial magnitude did not reproduce. Keep B+ as a control for
further changes to the common frame entry. Receipts use the
`physical-lazy-captures` prefix.

The Set target's emitted tier needs neither eager location capture
bindings nor an ambient work scope in the normal case. A truly
machine-owned frame still needs an entry descriptor, a rooted
environment handle, and a cold handoff that attaches the managed
view to the same published FP before executing the first managed
operation. Do not add eager setup to the ordinary case merely to
make that continuation callable.

#### Descriptor-free fixed callees do not rent a managed tier invocation

For a ready fixed entry whose tier layout has no dynamic local or
external descriptors and no cached callable projections, the caller
reuses a raw context with no managed tier handle. The physical callee
frame, argument placement, capture owner and code lease are unchanged.
Fixed offsets and actual frame/edge bases are sufficient; no pin
set or child `TierInvocation` is needed for a scalar completion.
The ready admission requires no pending work; the emitted code does
not add pending work without first entering a managed operation.
A register-region pending commit therefore cannot be reached in
the raw context. Observed runs use the same route and flush normal
instruction hits from the raw context.

A managed instruction boundary detects the absent handle before
doing work, records that instruction's PC, and exits. The caller
then installs the managed tier context and deferred capture/work
bindings for the same child and resumes from that PC. Poll, demand,
guard and non-scalar returns also use the same physical frame and
ordinary status handling. No operation preceding the exit repeats.
This follows the state-reconstruction distinction in
[TraceMonkey](https://www.cs.williams.edu/~freund/cs434-exemplar/gal-trace.pdf),
without tracing or compiling a second callee. It does not yet remove
the prepare callback or the managed physical-frame push/pop.

The detached controls cover a raw exit on the first managed cold
instruction, return after a raw poll in the middle of an accumulating
loop, and the ordinary bound-context path for a scalar capture read
directly by emitted code.

Native AOT image `fef752994717…` executes all 126,670 Set fixed scalar
returns without a child tier invocation; none needs to materialize
tier state. Generic bridge entries remain 123,861. The Force cold-demand
and cold-handoff fixtures each have 4,064 such returns and one raw
managed exit. The handoff writes the shared node before the exit and
the caller observes exactly one write per call, across five active
collections. The poll control records one raw non-bridge exit and
returns the original accumulated result. The bound Number capture
control takes no raw entries. Detached graph, live comparator
replacement, depth and callable evaluation-order checks pass.

Against `2d07a59e1d29…`, same-artifact x64 Auto Set with telemetry off
measures −3.87% on the first pass (6/11 faster), and −1.71% on a
same-CPU independent repeat (11/15 faster). B+ measures −1.99%
(5/7 faster) and took no raw fixed entries. Receipts use the
`physical-raw-tier` prefix.

The next boundary is physical reservation without attaching an
`Activation` or `NativeAllocation` view. For the current Set shape,
start from code versions that have only deferred location captures
and need no eager owned bindings. The prepared environment remains
the root owner until a safepoint or managed operation attaches to
the existing header. The reservation owner must support both
normal release without managed state and attachment on the exact
cold PC. After that split, the same-segment reserve/check and
release can be emitted without changing those semantics or
repeating the unsuccessful split-RET protocol.

#### Reserve and release a physical fixed frame without a managed view

A descriptor-free fixed callee with a physical argument plan, no
pending work, no eager captures or owned bindings, and no iterators
can reserve a fresh frame in the current guest stack segment. Its
header is the same header used by the ordinary stack path. The
caller retains the environment, physical argument view, code lease
and reservation; no child `NativeFrame`, `NativeAllocation` view,
`Activation`, `NativeWorkScope`, or `TierInvocation` is needed to
execute and return its ready scalar. The payload and roots are
written through the same FP-relative argument plan. Normal release
clears the initialization/root lanes and restores both SPs and FP.

If the current segment cannot accommodate both lanes, preparation
leaves the cursor unchanged and uses the existing stack owner for
segment growth. For a real native exit that needs managed state,
attach the allocation view and activation to the already-published
FP, with no initialization of arguments or locals. The environment
is retained by the prepared call and is copied into the attached
invocation before any managed guest operation or guest collection;
the raw entry cannot itself create pending work or address-keyed
host roots. An atomic segmented-stack prologue may later move the
reservation mechanics into emitted code, using the same fit check
and ownership rule (cf. [LLVM segmented stacks](https://releases.llvm.org/19.1.0/docs/SegmentedStacks.html)).
The exact-PC attachment is analogous to reconstructing a frame on
a [TraceMonkey side exit](https://www.cs.williams.edu/~freund/cs434-exemplar/gal-trace.pdf),
but does not replay code, relocate its storage, or add an artifact ABI.

This change removes the eager managed frame view, not the two fixed
call callbacks. A subsequent emitted reserve/release must retain
the same explicitly attached exceptional path, not split only the
normal RET epilogue across the same two callbacks.

Native AOT image `79e0d7cbba0a…` completes all 126,670 fixed native
Set scalar calls without attaching views, with zero reservation
misses and zero cold attachments in that workload. It retains the
same 253,340 physical parameter writes; generic bridge entries remain
about 124,000 (123,862 vs. 123,861). The three Force cold controls
each complete 4,064 unattached returns and attach exactly once:
at managed input, at the nested driver handoff or on a raw poll.
The shared-node mutation is not replayed, including across active
guest GC. Live replacement, red-black/IEEE, callable order, deep
segment reuse and writable-window journeys pass. The stack check
additionally verifies that capacity failure does not mutate the
cursor and attaching a view keeps the exact FP and existing value.

Against `fef752994717…`, same-artifact x64 Auto Set with telemetry
off measures −6.04% paired (10/11 faster, 0.612 → 0.586 s process
medians). B+ does not enter the new fixed path and measures +1.02%
(3/7 faster); this mixed control does not establish a regression.
Receipts use the `physical-unattached` prefix.

For the next emitter step, an admitted ready scalar can return
without calling the managed fixed-return helper at all. Restore the
same-segment cursor, clear the two raw lanes, and publish the
already-produced scalar; the pending-free admission and lack of
managed child work make those the full semantic RET. Keep managed
completion only for observation or actual exceptional/structured
exits, and account for a completed raw reservation before caller
reentry/cleanup. Moving just the final cursor stores while still
calling the managed return helper has already been measured and
rejected.

#### Emitted RET bypasses the managed completion callback

The already-unattached fixed path now arms a direct scalar return
only when the caller has an exact rootless destination and both
lanes fit the bounded emitted clear. The callee returns RDX bits
and the scalar status. The continuation clears the inactive
initialization/root lanes, restores the saved two-lane SP and
parent FP, stores the result, and jumps to the linked successor
without calling the fixed completion helper. Both ABI variants
use volatile registers for the lane clear and preserve the result
bits. Structured, non-scalar, demand, poll and error statuses still
enter the usual completion helper with the physical frame live.
This is a same-segment epilogue, not a split-stack return across
segments (compare [LLVM segmented stacks](https://releases.llvm.org/19.1.0/docs/SegmentedStacks.html)).

The caller context has one completion receipt: the negative
armed status becomes positive only after the physical RET and
result write. At the next managed bridge or tier exit, before
any frame reuse or guest GC, the owner consumes that receipt and
records the same instruction/call telemetry. No guest semantics
are postponed with it. Scalar publication cannot require an
address-keyed host or pending update on this admitted path. The
callee environment and code remain owned by the caller until that
boundary. The raw child cannot run a managed nested call without
taking its existing exact-PC exit first.

The main tests cover Number and Bool returns, rejection of Bool
with an eager scalar capture, cold demand and mid-loop polls,
one-time shared-node mutation during handoff, and two reusable
stack segments. This does not remove the managed prepare callback;
the next step is to publish a native entry descriptor with a
versioned lease and reserve the same segment at the actual CALL.
The implementation does not turn native return off when recording
is enabled.

Native AOT image `22a025c5be64…` completes all 126,670 Set fixed
returns with emitted RET, no reservation miss or managed frame
attachment. Set generic entries are effectively unchanged at 123,861
(123,862 in the previous recorded run). Force Number, Bool, and
poll controls each complete 4,064 emitted RETs. Their cold exit
attaches once and resumes at the original PC. The larger handoff
control takes the bounded managed completion path, retains its
shared-node identity through five active collections, and does not
replay its prior write. Detached graph/IEEE, live callable
replacement, evaluation order, writable windows and ABI checks pass.

Against `79e0d7cbba0a…`, same-artifact x64 Auto Set with telemetry off
measures −1.81% by paired median (8/11 faster); a separate same-CPU
repeat measures −2.15% (10/15 faster). B+ does not use these returns
and measures −0.15% paired (4/7 faster). Receipts use the
`physical-inline-ret` prefix.

#### Bottleneck audit after inline RET

Measure the existing `22a025c5be64…` Native AOT image and frozen
`894232663310…` Set executable before changing the call path again.
Auto, CPU 6, telemetry disabled, all outputs checked. Linux perf
events were unavailable; 2,507 randomized ptrace leader-stack samples
over 16 runs used frame pointers and AOT symbols (startup before
65 ms and worker threads excluded). Percentages are inclusive sample
presence, overlap, and are not an exclusive CPU-time attribution.

| Stack family | Samples |
| --- | ---: |
| Generic TierBridge | 752 / 2,507 (30.0%) |
| TierFrameCall (all subpaths) | 681 / 2,507 (27.2%) |
| Input submit/read | 309 / 2,507 (12.3%) |
| Native Range adapter | 272 / 2,507 (10.8%) |
| Fixed physical call preparation | 167 / 2,507 (6.7%) |
| Completed RET receipt | 60 / 2,507 (2.4%) |
| CLR collection | 177 / 2,507 (7.1%) |
| Guest heap collection | 12 / 2,507 (0.5%) |

A separate input-only artifact reads and parses the same 12,288 lines
and checks their sum. Seven alternating pairs give process medians
0.170 s for that control vs. 0.574 s for the full workload. This is
not an additive attribution: the control removes Set and Range work,
changes lifetimes/GC, and can change tiering and interleaving.

The snapshot controls keep the same input, Set operations and seven
primary results. In nine rounds rotating order, the original,
two snapshots consumed by size only, and no snapshots measured
0.576 s, 0.550 s, and 0.455 s (total CPU medians 0.576 s, 0.549 s,
0.453 s). Dropping the checksum traversals while keeping both
snapshots is −5.62% paired (9/9). Dropping the snapshots from that
control is another −15.55% paired (9/9); full to no snapshots is
−21.54% (9/9) and an independent seven-pair run agrees (−21.81%,
7/7). These are diagnostic workload contrasts, not speedups to
claim for the shipping workload. Source rebuild also changes
whole-program optimization; exact costs of individual substeps
cannot be read off the differences.

Inspect `Set::snapshot` and `_Chain::snapshot` in
`stdlib/containers.msync`: the ordered tree walk pushes keys into
an intermediate chain; the chain then generates a callback range
and materializes it. The Range sample includes those asynchronous
enumerators and callbacks. This supports profiling that path first
if optimizing full Set workload; if optimizing insert/lookup/remove
only, isolate a fed-data workload and time the residual linked
Load/Copy/Store paths rather than treating bridge counts as costs.
Fixed call preparation is real but not the largest demonstrated
opportunity. Receipts and diagnostic sources are in
`artifacts/benchmarks/set-bottleneck-inline-ret/`. No production
execution code was changed by this audit.

#### Datum Range consumption: remove the extra iterator layer

The fixed-call first-frame shortcut was tried for Range callbacks and
rejected. Set took the datum callback route: the profile recorded 3,864
callback driver handoffs and zero first-frame returns (physical-plan
callback requests were zero). On the frozen artifact, the experiment
regressed paired process time by 1.91%, with only 2/11 wins; a separate
effectful generate/materialize workload did not show a stable benefit.
The measured path goes through the callback's further guest call, so
optimizing only returns from the first frame misses it.

The datum Range collector, callback adapters and enumerate adapter can
pull directly from the original `GetDatumIterator` cursor. This removes
one pass-through async iterator object per traversal and one forwarding
`MoveNextAsync` plus `Current` hop per item at each changed edge. The
underlying iterator, cancellation token, pending datum, item order and
async disposal contract are unchanged. This is the same direct-consumer
shape used by [.NET's asynchronous ToList](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Linq.AsyncEnumerable/src/System/Linq/ToListAsync.cs);
it is manual elimination of an intermediate stream layer, not a
[compiler stream-fusion pass](https://www.cs.tufts.edu/~nr/cs257/archive/duncan-coutts/stream-fusion.pdf).
Datum materialization records `range.materialize.datum` with source, item
count and any failure; there is no per-item telemetry branch. The measured
programs and scripts are in `artifacts/benchmarks/range-direct-cursor/`.

Verified with the same frozen Set artifact and an independent effectful
`generate`/materialize bound artifact, baseline `22a025c5be64…` vs.
candidate `13987c81d973…`, Native AOT Auto, CPU 4, telemetry disabled.
The runner alternates order and compares the full output on every run.

| Workload | Process medians (baseline → direct) | Paired median | Faster pairs |
| --- | ---: | ---: | ---: |
| Frozen Set | 0.538 → 0.544 s | −0.55% | 10/19 |
| Generate 24,000 | 0.192 → 0.190 s | −2.02% | 12/19 |
| Generate 120,000 | 0.796 → 0.802 s | −0.57% | 8/15 |

This establishes no stable wall-time gain; the change is removal of
structurally redundant iteration. Set diagnostics showed two datum
snapshots of 2,154 and 1,710 elements. The independent numeric fixture
exercises effectful callback traversal, but its materialize takes the
ready path; the end-to-end journey separately forces the datum collector
with a deferred file input and checks its receipt. The callback (Off and
Auto), physical Range plan, file-line snapshot, and O2 midend journeys
passed. Neither Set nor the Range standard library changed.

The remaining fixed-call target is the managed prepare before jumping
into generated code, not RET or another argument copy. The profile on
the same artifact counted 126,670 fixed native calls, all of which
returned unattached via the emitted RET; entry acquisition reused the
caller's lease 114,408 times. Any emitted-prepare experiment should
target that **already-admitted** case and benchmark it independently.
The managed helper currently owns entry lifetime and checks both stack
lanes, pending state and host reference tags; an emitted entry must
retain those invariants, and a miss must occur before cursor
publication. If a child exits into managed code, attach the existing
frame at the original FP instead of replaying the child.

#### Link immutable fixed CALL preparation facts

Of the 2,507 earlier leader stacks, 163 contained
`TryPrepareFixedTier`, 111 entered `TryPrepareUnattached`, and 31
of the preparation stacks contained `FixedScalarResult`. The
latter was rediscovering the same private rootless result ABI
after every reservation. A fixed physical call now carries its
linked scalar result proof alongside its argument moves, and the
callee keeps the aligned stack extent resolved at link time.
Both the x64 emitter and managed unattached prepare read the
same result proof. The ordinary fixed continuation can still
use the same destination type check when it lacks a physical plan.

This moves immutable work out of a still-managed CALL prepare;
it does not yet advance SP in emitted code. Code-version leasing,
callee/pending/host-reference checks, aligned bounds in both
stack lanes, and same-FP attachment of cold exits retain their
existing ownership. The separate linked layout follows the same
static frame sizing principle as [Lua's function prototypes](https://www.lua.org/doc/jucs05.pdf)
and [guest precall implementation](https://www.lua.org/source/5.4/ldo.c.html#luaD_precall);
msync additionally tracks a separate guest-root lane.
Successful linked reservations have caller-attributed telemetry
distinct from reservation misses, inline returns, and attachments.

The fixed path only enters a retained, already-published tier version.
It therefore does not increment the function's tier promotion
counter on entry. That counter is read only to trigger the first
compile or a recompile, and invalidation resets it; observed JIT call
and instruction counts are independent. The threshold update remains
on the ordinary entry before published code is established. Removing
the atomic from fixed entry reduces the state that an eventual emitted
prepare would otherwise have to update.

Native AOT x64 disassembly confirms that removing the promotion
counter deletes the `lock incl` in both prepare branches. The
profiled 300,000-iteration fixed-closure control (Force) retained
299,969 reservations, 299,968 emitted returns and one same-frame
attachment/resume. Frozen Set (Auto) retained all 126,670
reservations/inline returns and zero attachments or misses.
Number/Bool, pending demand, poll, active guest GC, evaluation order,
writable windows, native reference identity and the stack capacity
boundary passed against the final AOT/managed builds as appropriate.

CPU 4, telemetry disabled; each alternating pair uses identical
frozen artifact bytes, input and output oracle. Baseline
`13987c81d973…`, linkage-only `bff1100aa43b…`, final
`331823d3590b…`:

| Workload | Linkage only, paired | Linkage and no promotion update, paired |
| --- | ---: | ---: |
| Fixed closure (Force) | −0.30% (7/13) | +0.83% (7/15) |
| Frozen Set (Auto) | −2.74% (8/15) | +0.63% (9/19) |

These noisy results do not establish a speedup or a regression.
The linked facts and removal of the promotion update simplify the
entry contract, but they do not remove the per-call host crossing.
The fixed closure control intentionally exercises a fresh caller
activation per call (zero lease reuses); Set reuses 114,408 leases,
so future entry caching needs a persistent-call-site control too.
Sources, bound fixed-call artifact, sample vectors and profile
receipts are in `artifacts/benchmarks/physical-linked-call/`.

#### The fixed CALL prologue publishes the guest frame

The bounded raw-scalar path splits the runtime admission from physical
frame publication. The helper leases the live callee code and validates
the callable, pending/reference state, and both stack lanes. Its
successful plan has an aligned start and a lease for later attachment;
planning does not mutate the cursor. The x64 prologue initializes the
empty pending word, data/initialization/reference lane bases, previous
FP, arena, and root count. It publishes both SP lanes and FP, writes the
arguments, and calls the code entry. There is no safepoint in that
sequence. Inline RET continues to restore the previous cursor. If the
child cannot finish in raw code, the managed exit attaches the frame
already at that FP and resumes it, without rerunning the prologue or
any guest effects.

Large/non-scalar raw frames and attached continuations still publish
in their existing runtime path, and a capacity miss changes neither
SP nor FP. The initial emitter step does not remove the admission bridge
or move the stack-limit check into the prologue (unlike [LLVM
split-stack prologues](https://releases.llvm.org/19.1.0/docs/SegmentedStacks.html));
it separates the operations that the machine owns from the live guards
that need to move before that bridge can disappear.

The persistent-call fixture runs the same closure call inside one caller
activation, rather than making a fresh wrapper activation for each call.
At Force it makes 299,873 fixed native entries (299,872 code-lease reuses);
the candidate publishes all of these in the machine prologue, with
299,872 emitted returns and one exact-frame attachment/resume. The
unchanged Set fixture has 126,670 emitted frame publications and returns,
506,680 moved reference words, and zero reservations missed. The new
rooted cold-demand journey increments one mutable shared object before
the cold I/O exit, observes exactly 4,097 increments, and verifies one
attachment and unchanged error source on the failure path. The ABI,
capacity, scalar/Bool, poll, reference identity, owned-window and call
order journeys passed.

A later bridge-free repeated admission must keep the code lease and
the exact environment used at entry until the frame has returned or
attached. Reusing a numeric target alone is insufficient: the caller
must recognize a changed closure/native-name lane and check pending
state, current segment capacity and argument tags before publishing
either SP lane. A failed preflight must not mutate the cursor, and
a cold child exit must use the same completed prologue and physical
frame rather than restart the call.

Telemetry-off Native AOT comparisons on CPU 4 use baseline
`331823d3590b…`, candidate `4efae39a9010…`, frozen artifacts,
alternating order and exact stdout on every run. Persistent Force
measured −4.40% paired (9/15) initially, then +3.44% (9/23) in a
repeat; frozen Set Auto measured −1.87% paired (11/19). These are
not an established wall-time improvement. The concrete result is
the machine-owned physical CALL/RET and independently observed
cold attachment; host admission must still be removed to test
the intended mechanism. The bound persistent workload, source,
profiles and raw timings are in `artifacts/benchmarks/emitted-call-frame/`.

#### Reuse fixed CALL admission inside a native stretch

After a successful inline RET the same fixed site can republish the child
frame without reentering the runtime. The receipt retains the caller's
code lease and the exact admitted segment and stack position. On a hit
the prologue reloads the physical argument lanes, resets child fuel, and
invokes the retained entry. A 63-reuse batch bound and existing native
poll points drain the receipt before another native stretch. This is
a same-site specialization, not a second generic call mechanism.

The static target is the linked CALL operand B; the native guard
intentionally does not reread the callable's function index or recheck
its signature. The stamp tests only facts the raw path still uses:
initialized callable storage, identical guest environment, absent
native name, no pending memory, and argument roots that need no
host-lease bookkeeping. The pending counter is pinned for the duration
of the tier entry, not stored as an unmanaged pointer across entries.
The cached source address remains valid within the native stretch:
binding changes and guest collection go through managed boundaries.
A guard miss leaves the current frame untouched and enters the normal
CALL preparation. The preceding native RET restored the same SP/FP
and segment; this is why the same reservation can be used without
testing capacity again.

Like a [guarded inline cache](https://bibliography.selflanguage.org/pics.html),
the reuse preserves a generic miss path but does not dynamically
resolve facts already encoded in the artifact. Accounting is batched
on native return or on managed attachment, so both seeded and reused
calls retain exact counts. On a cold exit, only the now-live physical
frame is attached and resumed; earlier scalar returns are accounted
for separately and are never replayed.

The Native AOT x64 candidate `cdf5d9b6dae0…` bypassed host admission
for 287,111 / 299,873 fixed calls in the independent persistent
control and 109,986 / 126,670 in unchanged Set. A separate rooted
cold-demand fixture admits 3,855 calls without a bridge and reports
one attachment from a reused admission. Its observable object count
is 4,121, including exactly one cold-call write; it passes both with
and without recording. Existing reference, pending, GC, frame-capacity,
argument-window, Range and callable-evaluation-order journeys passed.

Paired process times (CPU 4, Native AOT, telemetry off, identical
bound artifacts and byte-for-byte output oracle on every execution):

| Workload | Baseline image | Process medians | Paired median change | Faster pairs |
| --- | --- | ---: | ---: | ---: |
| Persistent closure (Force) | `4efae39a9010…` | 0.173 → 0.056 s | −68.38% | 19/19 |
| Frozen Set (Auto) | `4efae39a9010…` | 0.532 → 0.483 s | −10.02% | 19/19 |
| Frozen Set (Auto), cumulative control | `22a025c5be64…` | 0.536 → 0.493 s | −8.81% | 10/11 |

Raw samples, image and artifact hashes and profile receipts are in
`artifacts/benchmarks/repeated-call/`; the independent workload comes
from `artifacts/benchmarks/emitted-call-frame/`. Neither the Set strategy
nor the standard library sources changed. Dynamic readiness/host
ownership guards remain where the artifact does not prove them.

A separate frozen B+ inventory control at Auto (same baseline as the
paired Set experiment) measured 2.438 → 2.365 s process medians,
−2.53% paired (6/7), with exact output. This control found no
slowdown; the fixed-call mechanism's causal result is established by
the persistent call workload and the admission counts, not by B+ timing.
