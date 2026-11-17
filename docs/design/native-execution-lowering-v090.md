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
