# Native value and logical-heap model (0.9.0)

## End state

`scope { ... }` allocates statically described builtin storage on the logical
heap and binds declarations/references to that storage. A Scope is a namespace,
not a generic object. Compiler and VM can remove, split, stack-place or coallocate
physical allocations while preserving freshness, aliases, effects and lifetimes.
There is no source/library allocator design, Storage kind or collection-specific
native layout. All builtin kinds, not a selected numeric subset, need complete
native representation through locals, fields, calls, returns, captures and ranges.

This work replaces the representation and lowering, not merely its names. The
current `Value` inheritance tree, generic Scope/Cell payloads, number sidecars,
red-black-native arena and native vector/page owners are migration inputs, not
the destination. Existing performance receipts are historical controls.

## Ownership and representation

- Builtins: Null, Bool, Number, String, Path, Connection, Function, Range,
  Process and Type have closed, typed representations. Path retains raw spelling,
  endpoint and captured local projection. Describing a Connection never opens it.
- A finite sum is represented by known alternatives and a discriminator. A
  monomorphic slot uses its actual representation without a universal value tag.
  No arbitrary `object`/Value payload or dynamically inferred field layout.
- Namespace references and capture locations are IR addresses, not additional
  source types. The referenced allocation's static layout describes the builtin
  fields. Recursive structures use references, not infinitely expanded products.
- A stored address retains its optional **physical pointee ABI**, as well as its
  allocation and raw/root offsets. An untyped namespace base has no pointee ABI.
  The address representation uses eight raw bytes and two typed GC roots
  (allocation and optional immutable NativeType); its equality/hash still name
  only the location. This is pointer metadata, not another source kind or a
  generic object payload. Frames do not maintain a second mutable pointer-value
  cache: rebindings, indirect stores, captures and memory copies observe the
  same descriptor. Ready monomorphic address loads/stores use typed entrypoints;
  unions and pending data retain their normal checked/deferred paths.
- A stored IR pointer preserves its actual physical pointee format, not merely
  its owner and offset. The optional immutable NativeType is a typed GC root;
  it is not a source object type or a per-pointer field dictionary. This removes
  the activation-local pointer side cache, which could become stale after
  BindLocation or an indirect/cross-frame pointer store. Namespace identity
  still compares owner/offset, not the descriptor.
- GC-visible references remain typed roots. Primitive bytes never hide managed
  pointers. An allocation/address preserves shared storage; copying a descriptor
  does not mean copying underlying contents or duplicating a Process.
- A frame owns private storage and active iterators/work. Escaping captured
  locations have independent logical-heap lifetime; they do not retain unrelated
  private frame inputs. A function environment distinguishes values and places.
  A typed interior pointer reaches its one location; a namespace reference
  reaches its logical region. Pending-memory metadata follows weak allocation
  identity, so returning a FrameAddress must not erase its pending value when
  the activation ends.

Namespace shape admission is a typed lazy boundary, not an eager recursive graph
scan. A Scope field stores an address rather than an invariant nominal referent
class. Outer admission checks compatible possible builtin carriers; a dependent
member access first narrows to its finite compiled layout cases, then selects an
exact checked allocation tag and field offset. The load uses the actual physical
field format before narrowing to the logical flow type. Nested obligations are
therefore enforced when accessed, including after alias mutation and through
cycles. The current interpreter checks allocation tags even for single-case
projections; this is implementation debt, not a required execution contract.
A backend must discharge proven physical-layout facts during lowering and emit
direct field offsets without repeating those proofs. Genuine alternative-layout
selection and explicit checked conversions retain only the unresolved dynamic
condition. External values require admission at their boundary, not a fresh
structural validation on every trusted field access. Logical schema information
must not be mistaken for a proven physical layout merely to delete a necessary
check.

The compact codec, typed-flow verifier and executable linker share
`NativeInstructionFacts.Schema`: encoded operands, semantic value-read operands
and required/optional result behavior have one readonly opcode description.
Noncanonical optional wire operands still roundtrip, but do not become semantic
input reads merely because the codec preserved them. Historical unpooled native-v1
inputs remain readable; their retired production encoder is removed, and encoding
comparisons use original frozen input bytes rather than reconstructing history.

Native address, stored-reference and escape facts share one compile-time
`NativeAddressAnalysis` graph. Escape reachability traverses that graph; memory
passes reuse it rather than solving another alias graph. `ProvenLayoutsAt` is the
linker's physical evidence, distinct from historical declared layout cases used
for finite variant selection. Loads obtain evidence from actual stored allocation
origins, not their Scope carrier annotation. Effects invalidate the reference
contents of their escaped reachable locations, including references retained by
closures and ranges; they do not invalidate unrelated unexposed allocations or
an entire frame because one captured location escaped. Later-discovered fields
inherit the same invalidation. This is a flow-insensitive proof: a later store
currently does not regain evidence lost to a prior call. Improving reaching-store
precision must not change admission or add runtime shadow state.

Instruction definitions and incoming arguments have distinct compile-time value
identities. `At(pc, slot)` joins only definitions reaching that use through the
CFG; slot coloring cannot carry an old capture pointer's unknown origin into a
new allocation-field pointer. `ProvenLayoutsAt` consumes the same value facts.
Whole-slot summaries remain conservative lifetime joins, never current-value
proofs. Location bindings have their own reaching edges to immutable pointer
values: a new FrameAddress follows the current binding, while a pointer formed
before rebinding retains its original physical cell. Escape roots use actual
instruction inputs rather than a colored slot's historical values. The
[value-identity controls](../../tests/NativeOptimization/ValueIdentityChecks.cs)
cover diamonds, unknown joins, backedges, loop-carried bindings and saved pointers.
Reaching state is retained per basic block with prelinked use edges, not a copy
of every frame at every instruction; scalar-only definitions do not allocate
reference-provenance nodes.

Typed pointer pointees remain invariant: `ptr<Number>` cannot store or bind
`ptr<Bool>`. This differs from an ordinary Scope field's opaque referent carrier.
[The provenance controls](../../tests/NativeOptimization/ProvenanceChecks.cs)
construct a legal callee that replaces a Scope field with a different physical
layout, proving why the field's declared layout domain is not allocation evidence;
an unrelated private field retains its proof across the same call.

A finalized HIR construction domain is not a nominal namespace class. Lowering
uses its finite rows as alternative field/carrier contracts and enumerates the
compatible physical layouts, including when a join has a domain but no single
Shape. Actual allocation sites and alias identity remain separate. Calls likewise
receive the concrete target result ABI before narrowing into the caller's flow
type; a more precise caller observation cannot shrink the callee's return slot.

Physical namespace alternatives come from constructor storage ABIs, retained
session storage and declared builtin record factories, not every HIR flow schema.
Logical schemas remain compile-time contracts; admitting their never-allocated
rows as storage alternatives can make a valid numeric write appear to target a
Scope-only field. `NativeBuiltinLayouts` is the shared ABI authority for host
record field order/types, including the generic zip/enumerate templates.

Namespace domains distinguish `null` (unrestricted) from `[]` (finite empty).
Opaque Scope and a structural empty shape impose no field requirement. An
inhabited narrowing into an empty domain fails; a dependent access with no
possible physical case emits a source-attributed trap after evaluating its
operands. Early unpooled native-v1 payloads normalize their historical empty-open
encoding on read. A physical allocation cannot inhabit a finite empty domain.

An opaque `Scope` link is not a proof of its members. Traversal can establish a
logical view with `node as decltype(tree::nil)`, after excluding Null for a
`Scope?` link. This supplies compiled layout cases and checked projections, not
dynamic member lookup. Raw collection-inspection fixtures must express the same
view as the library traversal while retaining their independent graph oracle.

An associated-type sample is a logical schema witness, not its writable field's
physical carrier. For example, a policy's sample cell may store arbitrary Scope
addresses while its current record establishes `Self` fields for a comparator
whose parameters are annotated `Scope`. Concept checking retains that sample
schema and still rejects missing fields or a non-Bool comparison result. It does
not introduce a runtime record class or strengthen the sample cell's storage ABI.

A refined read updates both its HIR runtime-kind mask and its Nullable marker.
Keeping the declaration's old Nullable marker on a non-null Number read makes
native lowering reconstruct `Null | Number`, even though semantic arithmetic was
checked as Number. The location's independent Null admission remains unchanged;
the reader still requires an explicit checked narrowing from nullable storage.

Return-shape queries bind their formal inputs and register the recursive scheme
before expanding future-readable captures. Expanding captures can recursively
query the same callable through future assignments; a guard installed only before
the body is too late. Bound capture recipes without a live cell are normalized
against matching instantiated captures by template, capture position and original
declaration. Actual live closure locations remain distinct, even for two factory
results using the same template. These boundaries are exercised by
`tests/RecursiveScopesV090 --shapes` through source/stripped HIR and strict native
portable/executable routes, alongside invalid-policy and distinct-capture controls.

Structural read views are not invariant writable storage ABIs. Member writes
evaluate receiver and RHS once, exclude provably incompatible physical cases,
then guard the receiver's finite layout set. A common compatible ABI uses the
ordinary projection/store. Mixed ABIs dispatch by exact layout, performing a
checked RHS refinement or permitted implicit conversion for that selected field.
Pending RHS values stay pending; checking the receiver never demands their
payloads. The producer and strict reader share `NativeTypeRelations`, so this
boundary does not weaken the total physical-store proof.

Public standard modules have one canonical namespace allocation from the first
published public-module access. Its statically known public-name cells store
opaque Scope references; loading a module refines its logical schema, not its
physical cell ABI. Query forks use that same opaque Scope storage ABI; their
value facts retain the refined logical schema. Module initialization remains lazy and unused cells stay
uninitialized. Later `$std` exposure is a logical view over those same cells,
never another allocation or binding pass that could invalidate captured
addresses. Private helper modules are not exposed as public root members.
Runtime feedback recovers member names from the construction layout, not from
qualified binding-location names (`bool` and `$std::bool` need not be identical).
The canonical-namespace acceptance receipt includes 56 basic routes, all 72
O0/O2/O3 demand-loading routes and 93 captured/staged/session routes; the latter
ran on an unchanged hashed managed payload with a 900-second/18-GiB RSS bound
and no termination. Its test-process peak RSS was 17,811,345,408 bytes
(`.temp/stdlib-root-order/captured-final-receipt.json`). This is a correctness
receipt, not evidence of acceptable compiler memory use or latency; nested
family compilation still warrants a separately isolated profile. A subsequent
one-line query-only ABI consistency fix has its own 18 source/stripped-HIR/
codec/bound and editor-control receipt (`.temp/stdlib-root-query/maintained.log`).

Invocation projection binds formal parameters/captures against their immutable
**input** Scope descriptions. The instance object table describes the **final**
write/result graph and is consulted when projecting outputs, not when discovering
input aliases. Refreshing an input from that final table can mistake a freshly
replaced child for the caller's old child, erase a legitimate replacement and
select code with the wrong capture environment. The independent callee-before-
mutating-argument oracle retains the old callee for the current call and observes
the replacement in the next call (2 then 99, combined as 299).

## Relocatable IR

Typed HIR owns lexical names, specialization, effects, fixed storage types and
initializer order. Its successor expresses typed registers/locations, logical
allocation, address formation, loads/stores, control flow and function/native
calls. Calls declare argument, result and capture contracts. Field names survive
as diagnostic/layout metadata, not executable named-object lookup.

An instruction carries an operation, explicit register operands/result, static
type/layout references and source origin. Layout projections enumerate finite
alternatives. Session/static declaration identities and uninitialized state are
separate from an allocation's fields. Native code is derived from this same
complete IR; unsupported machine instructions use the typed interpreter, never
a generic-object implementation of the language.

Portable constants contain literal data, not live allocations, process handles,
connections or code pointers. Artifact loading validates once; relocation and
target layout resolve machine details without replaying HIR/source. Preserve
existing source behavior and explicitly audit format-6 import/compatibility
before changing the accepted artifact contract. Product version is 0.9.0;
product version alone does not silently invalidate old user artifacts.

### Compact portable encoding

Native format 1 now has an explicit compact-encoding discriminator after the
format number: signed `0x80004952`, followed by encoding revision 1. The earlier
unpooled layout started there with a nonnegative UTF-16 language-string length,
so detection is unambiguous. Earlier unpooled function records both before and
after the optional reflection-signature field remain readable; the former
unversioned extension is not a model for future format evolution. Unknown compact
revisions are rejected rather than guessed.

The compact payload contains one exact UTF-16 string pool, one deduplicated source
span pool, and eleven ordered, length-bounded sections: header, types, layouts,
projections, statics, constants, function metadata, debug mappings, code,
allocation hints and execution plans. Metadata integers use canonical signed
varints; Number constants retain their exact eight IEEE-754 bytes. Function and
plan names, paths, symbols and source filenames refer to the same string pool.
Unpaired surrogates are preserved as code units, not transcoded/replaced.

Instructions use opcode-specific operand schemas. An ordinary Nop occupies one
code byte, not a record containing unused register and immediate fields. An
explicit extra-operand mask preserves non-default optional fields without making
each instruction pay for them. Source spans live in sparse per-function PC maps;
debug information is deduplicated, not stripped. The reader checks section order,
lengths, pooled indices, varint overflow/canonical form and complete section
consumption before applying the existing typed IR/ABI/ownership validator.
Nonseekable streams remain supported without allocating arrays from an
unverified, merely declared section length.

This is the portable interchange encoding, not a claim that the loaded execution
image is already a packed instruction array or native code. The reader still
constructs the typed program model used by optimization/inspection; executable
linking and JIT tiers have their own representation and ownership requirements.

## Optimization obligations

### Execution lowering and optional native compilation

Every source/HIR/portable/bound program enters one `NativeExecutionImage`.
Off disables guest JIT, not backend lowering. Linked instructions resolve static
storage/layout/field/call operands once; the baseline and native compiler consume
the same code and source-PC mapping. There is no second legacy VM or planner
invocation during execution. See the [execution-lowering contract](native-execution-lowering-v090.md).

Frame bindings and canonical storage remain authoritative; Runtime CWT payloads
retain exact pending-value semantics. A static Number ABI does not establish
readiness. External admission happens once, actual layout unions select a
variant, and checked casts inspect actual values. Trusted concrete layout/field
facts are not revalidated on each access. The Core provenance authority separates
actual layout evidence from logical Scope carrier domains.

The shared address authority tracks reaching **value definitions**, not the
union of everything ever stored in a register slot. Temporary slot coloring can
reuse one slot first for an unknown capture pointer and later for a concrete
allocated field. Link-time field/binding facts use `At(pc, slot)` and
`ProvenLayoutsAt(pc, slot)`; an old Unknown must not contaminate a later value,
nor may a later Bind retarget an earlier saved pointer. CFG joins/backedges keep
their real alternatives. This is compile/link data, not another VM state cache.

Unexposed dead FrameAddress/access intermediates lower to typed location
transfers. Original logical visits/edges and checked-access source spans remain
observable. Native exits resume before the first uncommitted logical operation;
no generic runtime pointer recipe/materialization authority is required. A
started asynchronous operation saves its existing ValueTask and is resumed once.
Guards never cache captures, mutable payloads or filesystem/authentication state.

Generated code uses W^X pages, opaque managed handles and SafeHandle page leases.
Only actually used primitive byte/init buffers are pinned, never GC root banks;
pins are released before awaiting. Native workspace and sparse hit storage use
one compiled-size allocation. Scalar writes preserve pending cleanup and GC-root
writes retain managed barriers. All builtin kinds retain complete typed helpers.
Guest native compilation is independent of the Native AOT host compiler.

The [state/phase audit](native-tier-state-audit-v090.md) records the historical
shadow-state implementation and its removal rationale. The 539-input checkpoint
had correct triggered Native AOT contracts but negative representative timing;
those receipts remain in the performance audit. Neither coverage nor successful
code generation establishes profitability, and automatic promotion remains gated.

### Tail-call execution

The native runtime trampolines eligible source calls using fresh activations,
not host-stack recursion or recycling possibly escaped frame storage. Actual
callee selection and capture retargeting happen before the handoff; argument
values are snapshotted together, preserving parallel argument exchange. The
image generation is fixed across the complete invocation, including tail steps.

A cached, generation-owned tail plan admits only a return path of same-static-
type copies/moves through private Temporary slots, Nops and unconditional jumps.
It must return the call result with the caller's exact result ABI. Refinements,
conversions and observable writes are not erased. Exposed/bound destinations or
geometric access to frame storage prevent this proof. Owned work, active regions,
live iterators and persistent entry locations retain the ordinary call boundary;
cleanup is not silently moved before the callee. Logical return-site/edge counts
are completed only after successful callee completion, and failed targets retain
the original call-site diagnostic. Native code exits to the same trampoline
before executing a tail helper; this is not a speculation failure or replay.

`tests/TailCallOptimization` checks O2/O3 100,000-step source/strict-artifact sums,
parallel arguments, actual closure environments, escaped fresh captures and
selected real file effects. Additional Off/Linked/Force controls retain writes
to externally bound and escaped result locations and compare logical instruction,
edge and target profiles. These establish behavior, not a universal allocation
or throughput improvement; fresh frames intentionally remain valid heap owners.

This is explicit first-order control transfer, related to the continuation
representations in [Danvy and Nielsen's defunctionalization study](https://www.brics.dk/RS/01/23/BRICS-RS-01-23.pdf),
not reliance on the host compiler recognizing a recursive C# call. In contrast,
[LLVM's tail-call contract](https://llvm.org/docs/LangRef.html#call-instruction)
has explicit caller-stack lifetime restrictions. Keeping native logical storage
alive through escaped addresses is therefore a separate obligation from removing
the host return frame. Neither reference proves msync's implementation correct.
The maintained CLI journey additionally passed nine Linux x64 Native AOT routes
(three policies × source/portable/bound) on one frozen image, printing the exact
100,000-step result after detached inputs were removed; its per-command receipt
is `.temp/native-tail/aot-cli-140d027f2a8145a7bb63ab183a557f7a/result.json`.

The portable middle-end operates on this complete native IR, independently of
the execution tier. CFG cleanup first threads effect-free jump chains and drops
unreachable operations; final instruction compaction relocates every control
target, live allocation hint and execution-plan interval. Removed allocations
have no runtime placement record. Original source spans remain on surviving
operations, rather than preserving dispatched Nop anchors as debug metadata.
Cross-block facts use an intersection at joins and invalidate locations that
an effect or alias may change. A static Number ABI is not proof that an operand
is ready: distinct pending computations have observable identity in structural
Range operations and cannot be merged solely because their scalar expressions
look equal. Inlining must also preserve capture places and work ownership; an
ordinary call that joins selected effects or private Processes is not a
transparent syntactic boundary.

Private fixed-frame address/load/store pairs can be residualized as exact-ABI
register copies, across every builtin kind. Their intermediate pointer is not an
observable language object. This proof excludes captures, rebound/session cells,
unknown geometric aliases and cell addresses recovered through local closure
environments. Block-local initialization facts and stable value parameters allow
checked loads to become discardable copies only when their input read cannot
fail. Store promotion and dead/overwritten-store elimination share the same
private-source proof: an unobserved destination does not make reading an
uninitialized location capture unobservable. The original checked-read failure
and source span must survive even when its result is unused. These rules do not
erase Path conversion or narrow a variable's fixed physical storage type.
The allocation-first design has production precedent in
[LLVM's memory-to-register promotion](https://llvm.org/docs/tutorial/MyFirstLanguageFrontend/LangImpl07.html#memory-in-llvm):
explicit memory is a useful frontend contract, not a requirement to execute every
load/store literally. msync additionally preserves checked uninitialized-read
failures; LLVM-style undefined-behavior assumptions cannot justify erasing them.

The implementation follows the separation between CFG cleanup described by
[LLVM SimplifyCFG](https://llvm.org/docs/Passes.html#simplifycfg-simplify-the-cfg),
memory definitions/uses described by [MemorySSA](https://llvm.org/docs/MemorySSA.html),
and explicit inlining legality/value mapping in
[MLIR's inlining interface](https://mlir.llvm.org/docs/Interfaces/#inlining-interface).
These mechanisms inform the native contracts; their published speedups are not
msync measurements.

The current native pass policy is explicit: O0 leaves portable code unchanged;
O1 runs local value/memory cleanup, private temporary-drop elimination,
escape/lifetime analysis, placement, CFG compaction and typed virtual-slot
allocation. O2 adds forward must-constant/private-memory facts and transparent
static-call inlining. Its callee budget is 48 non-Nop operations with a cumulative
384-operation growth budget per caller; O3 uses 144 and 1536. A higher budget is
not itself a speedup measurement.

Inlining proves the actual callee comes from a source Function construction,
including immutable nonescaping local function cells used across a loop. A
direct-target operand alone is insufficient because a native descriptor still
dispatches to its native capability. Captures are loaded from the actual current
environment: by-value captures retain construction-time data and by-location
captures bind the existing place. Invariant capture ABIs and a nonescaping
callee frame are required. Calls with selected work, iterator lifetimes, opaque
callback execution or unproved source targets retain their ordinary boundary.
Return values preserve their declared ABI before downstream logical narrowing;
cross-type Copy bridges are not copy-propagation aliases.

Virtual-slot allocation is a liveness/interference transformation, not hardware
register allocation. Only nonoverlapping temporary slots of exactly the same
static type ID and the same allocation-layout and physical-pointee proof sets
share storage. These cohorts reuse the strict artifact reader's pointer facts:
its flow-insensitive producer union must remain valid after disjoint lifetimes
share a register. An open Scope ABI alone does not prove the same layout.
Canonical locals, captures, parameters, exported
roots and address-exposed/bound/escaping places keep their indices and format.
Functions exposing frame-wide arithmetic, bulk memory, pointer reinterpretation
or escaping frame locations are left unrenamed. Drop and Move consumption are
storage definitions, so consuming one temporary cannot clear a still-live color.
Register binding availability is separate from pointee data initialization:
BindLocation establishes an alias just as a location capture does at entry;
actual memory reads still diagnose uninitialized data with their source span.
The alias model treats a register and its FrameAddress as the same cell:
register definitions feed address loads, address stores feed register reads,
and bound references read the same contents. Losing that connection can make
an inlined Scope parameter appear unaliased and incorrectly delete even a
necessary Null initializer; initialized Null and uninitialized storage differ.

`tests/NativeMiddleEnd` checks these transforms through every builtin/IR address
kind and source, stripped-HIR and checked native artifact routes at all four
levels, alongside actual selected-transfer/private-Process publication oracles.
The full runtime suite separately retains pending-value identity and resource
cleanup controls; no scalar-ABI assumption replaces those controls.

Alias/use/escape/effect facts drive allocation elimination, scalar replacement,
frame placement, legal copy/move and cleanup. Failed proof retains the ordinary
typed logical allocation, not an object fallback or rejected source program.
Return, error, cancellation and actual suspension must preserve shared captures,
iterator disposal, selected effects, returned Processes and deferred Range roots.
Floating-point operations preserve the existing binary64 order and exact bits.

Collection algorithms remain source. Set may allocate ordinary linked namespaces;
Heap/tree code does not ask for a host layout or numbered allocator slot. Physical
pooling/dense placement is an optimizer/runtime choice over general allocations.
Do not sacrifice asymptotic operation costs merely to remove a native owner.

### Actual activation-frame coallocation

`NativeImage` consumes `NativeProgram.Allocations`: Frame plans are independently
intersected with `NativeAllocationSafety.FrameSafe` before reserving storage.
The current proof admits nonescaping, noncyclic, single-element allocations;
dynamic counts and cyclic sites retain ordinary typed logical-heap allocation,
even if a caller supplies an unproved Frame hint. Artifact admission also
normalizes unproved hints. The analysis is intentionally conservative across
calls and reference-field loads; a failed proof must not be bypassed to improve
a benchmark.

`NativeLayout.Compose` places the ordinary register frame first, then disjoint
aligned Scope regions, flattening their actual typed fields without fake padding
fields. Each region reserves at least one logical byte, including empty and
Null-only Scopes. Root offsets remain separate from primitive byte offsets;
all builtin roots stay GC-visible. One activation allocation owns the combined
byte/root/initialization arrays. Executing a selected Allocate returns an offset
address into that owner, not a new allocation or a per-Scope view wrapper.
Aliases retain the same address and distinct sites retain distinct offsets.
This is **VM activation-frame placement, not a claim of machine-stack allocation**:
the interpreter's activation storage is still managed-heap-backed.
The trade-off is eager reservation for selected sites even on untaken branches,
and region roots share the activation owner's physical GC lifetime. Image-time
proof/metadata cost is separate from per-activation savings; one-shot startup
and peak-memory improvements are not established by the focused receipt below.

The image records `(physical frame layout identity, region base offset)` to
logical layout identity. Finite projection, explicit Scope checks and exact
layout predicates resolve this static metadata rather than inspecting field
names during execution. Work reachability traverses logical regions rather
than treating every coallocated sibling as reachable. Indexing and memory
regions are bounded by the original Scope or register-frame allocation before
physical typed validation: coallocation must not make a formerly out-of-bounds
index/snapshot legal by exposing an adjacent region. Register-frame bounds
retain the old whole-register-frame contract rather than introducing stricter
per-local bounds. Runtime profile
`frame_placed_allocations` counts actual selected Allocate operations;
`logical_storage_bytes` counts the logical region's raw bytes plus root slots,
not the entire physical activation frame or managed object overhead.

An owned `RangeFromMemory` (`Symbol="move"`) retains the source backing region
as well as its elements' reachable references. Escape analysis must propagate
both; following only element contents wrongly permits frame placement for an
escaping Range of Numbers. Snapshot-copy ranges retain their own copied storage
and do not force the source region to escape. The allocation journey tests both
routes after the producing activation has retired, including untrusted Frame
hints and actual placement counters.

The conservative graph-based proof follows the reachability principle of
[Choi et al., Escape Analysis for Java (1999)](https://research.ibm.com/publications/escape-analysis-for-java).
For comparison, the [.NET object-stack-allocation design](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/object-stack-allocation.md)
also separates escape proof from physical placement. Those host optimizations
are not assumed by this interpreter implementation.

`tests/NativeAllocationV090` checks real compiler O0/O2 source and detached-native
artifact routes with independent result oracles: mixed aliases, finite layout
projections, loop freshness, returned nested captures, builtin roots and actual
Process fields. Direct typed-layout properties cover all eleven NativeKinds,
empty/Null separation, forged cyclic/dynamic Frame-hint fallback, and owned
Range backing escape versus snapshot-copy placement. A gated source transfer
exports a pending Scope field through a deferred Range, checks publication
bytes and eventual membership after the source activation retires.
IndexAddress and snapshot bounds controls compare heap, frame and detached
artifact failures at both Scope and original register-frame boundaries while
checking the original source diagnostic. The `--measure` mode isolates the
placement mechanism by running identical O2 IR
with Frame hints retained versus demoted to LogicalHeap; compilation, image
construction and warm-up are outside the process-local allocation-count window.

On Linux x86-64 with SDK 10.0.403, Release, 32 warm-up executions per route and
five alternating trials of 200 executions, median managed allocation traffic
was 5,816,520 bytes with heap placement versus 5,754,120 with frame placement:
62,400 bytes saved, or 312 bytes/execution for this two-Scope fixture. Every
execution checked result 27. This is a focused allocation-traffic receipt, not
a universal throughput or latency claim. Reproduce with
`dotnet run --project tests/NativeAllocationV090 -c Release -- --measure`.

### Unproven direction: materialized-producer allocation fusion

A finite materialization whose producer provably constructs fresh namespaces of
one static layout could reserve a counted `Allocate` and bind each logical Scope
through `IndexAddress`. Existing IR operations suffice; this is general compiler/
VM placement, not a source arena/vector API or recognition of a named record or
collection. The hypothesis is reduced allocation/dispatch traffic, not an
implemented optimization or measured throughput result.

First generalize `NativeReachable` to identify each logical region of a counted,
non-frame owner: an untyped namespace address currently can traverse its whole
owner. One escaped row must not promote unrelated rows' pending Process/transfer
work. Only then attempt a producer-freshness/layout proof and a checked runtime
count, preserving aliases, per-row identity (including empty/Null-only layouts),
initialization order and partial failures. Compare exact results, pending-work
lifetimes, allocation traffic and end-to-end CPU against ordinary allocation,
with nonhit controls and retained-memory measurements: one surviving row may
otherwise retain an entire physical batch's GC roots.

## Migration and acceptance

Shared artifact stage/semantics, binding context, connection kind and transfer
plan contracts now live outside historical bytecode/value files, with unchanged
public types and namespaces. `CompilationSize` observes actual native function,
instruction and slot counts; native compiler trace reports the lowered output,
the optimizer input and the optimized output. Retired bytecode size overloads
are removed rather than used to estimate native code.
The stable `compiler.lower` and `compiler.optimize` observation names remain;
profile-level native optimizer events aggregate actual control-flow/escape
analyses, local rewrites, dead stores/results, last-use moves and placement.
Phase-only observation does not enable pass profiling. Native runtime profile
keeps `artifact.stage` and `optimization.level` attributes alongside the explicit
representation marker; obsolete tier counters must not be fabricated.

The initial side-by-side migration has ended: the historical production backend
is removed. Compatibility readers translate portable data into the native
pipeline, not a parallel execution engine. Complete coverage, optimization and
performance acceptance remain requirements after that deletion.

Acceptance is real source, standalone HIR, detached relocatable/executable
artifacts, REPL and editor journeys covering every builtin, mixed layouts,
recursive references, aliases/rebinds, mutable captures, effects/suspension,
independent result/file oracles and actionable original diagnostics. Structural
disassembly must identify the native IR and cannot contain generic Scope/Storage
operations. Allocation/copy/drop optimization needs positive and alias/escape
controls. Performance comparisons use the maintained heterogeneous suite and
the frozen prior image, separate from instrumented diagnosis.

### CLI native cutover

Source and standalone HIR commands lower with canonical `Compiler.Compile` /
`LowerInstructions`, and execute with `NativeInterpreter`. Default `.mso` / `.mexe`
outputs contain `NativeArtifact` IR; disassembly identifies `native-v1`, including
JSON output and artifact bytes supplied on stdin. `check` validates the actual
source/HIR lowering rather than accepting a program the selected VM cannot run.
Binding captures the execution directory, home and optional source file without
replaying source. Native contextual preparation records selected work and leaves
unknown effects/control as residual instructions. Runtime initialize/execute,
binding/preparation and actual process/transfer telemetry remain attached.

Accepted format-6 input uses the closed reader/importer and this same native
pipeline. Historical HIR uses ordinary native memory adapters when its builtin
ABI needs migration; it does not require the retired object VM. REPL, editor,
source-free artifacts and native contextual preparation have distinct real-user
acceptance journeys; no individual one proves the full replacement complete.

### Recursive collection typing checkpoints

Ordinary linked nodes need one consistent link admission: sentinel and payload
nodes must both declare nullable links when the sentinel starts with `null`.
Helper functions can narrow a link to a non-null node; this does not change its
fixed field ABI. A payload node with non-null link declarations is a different
structural schema and cannot subsequently be rebound to a nullable-link sentinel.

An unannotated Scope binding fixes the builtin pointer kind, not the first
namespace's field set. Rebinding it to a different namespace layout is legal;
flow facts retain the finite possible layouts. Explicit Type-witness casts retain
the complete target HIR shape and construction domain and are checked layout
assertions, not generic `Scope` identity conversions. Collection link readers and
page readers use these source assertions at otherwise opaque pointer boundaries.
A narrowed read of `null` must retain its field's storage carrier; neither a
flow row nor alias invalidation may replace that carrier with a Null-only slot.

Inference substitutions are graphs, not just top-level aliases. Closing a
constructor's links must substitute through Scope rows, range elements and finite
alternatives, preserving nullable variants of the same recursive row. Returning a
fresh Scope must export the constructor's already-solved storage constraints;
creating fresh variables is valid only for still-unsolved cells. Type-only flow
projection alone cannot repair a missing fixed schema: deferred reads deliberately
forget current observations and must still retain the solved recursive layout.

Query-only invocation projection allocates fresh semantic cell identities for
fresh result fields. Reusing source declaration IDs aliases independent generic
instances (for example Number and Scope child chains created at one page site).
These negative IDs have source descriptors in `SemanticArena`, but are neither
executable locations nor entries in the published HIR location table. Source
provenance remains in `Definitions`; the ordinary object/alias graph still owns
field effects. Scalar input fields are abstracted to their fixed carrier rather
than an initialization observation such as Null, since shared code can run after
that mutable cell changes.

Explicit Scope assertions carry a separate logical `CheckedType` view. Reading
through the view combines current alias values with the declared member schema,
retains compatible flow refinements, and propagates the checked schema through
nested references. Physical storage locations/carriers stay separate. Dependent
loads therefore retain their promised scalar type after an alias mutation;
asserting an outer namespace does not eagerly walk its reference graph.

A control-flow read refines a value view, not the identified namespace's layout.
`FlowRead` updates located field facts but must not publish its retyped Scope
record into the region table: a widened empty structural summary otherwise
erases the original namespace's fields through every alias. Checked writes use
the same logical member schema as checked reads and create an alias write-view
when current descriptive facts are opaque, without changing physical storage.
This distinction was exposed by full Set analysis failing in two of eight fresh
processes on identical source/payload as hash iteration changed traversal order;
the corrected compiler passes sixteen fresh-process analyses and the strict
recursive/native-lowering contracts. Source/artifact collection execution remains
an independent acceptance requirement.

`tests/RecursiveScopesV090` includes a nullable sentinel/payload constructor and
helper-call loop regression, alongside canonical recursive-layout and rollback
properties. The public collection acceptance remains the source/artifact journey
in `tests/ScopeCollectionsV090/journey.py`; passing the small graph fixtures is
not a substitute for that journey or for native lowering/execution.

The fixed managed integration payload
`artifacts/collections-validation-v090-physical` passes all 32 public collection
cases at seed 17 (four families, source/detached HIR/object/bound, O0/O2), including
nullable keys, per-operation tree validation, snapshots, live policies and Scope
payload identity. The receipt at
`.temp/scope-collections-v090/final-tree-binary-physical/result.json` audits exact
result files, 24 deleted-source routes, eight deleted-portable routes and unchanged
executable/dependency hashes. Functional-only scheduling pauses are recorded, not
interpreted as performance samples. This closes that collection correctness
matrix for its recorded payload; it does not establish the whole release or the
performance of a subsequently changed image.

### Finite native/source callable domains

Finite callable alternatives may mix native descriptors and source closures.
Each source alternative is specialized under the same argument protocol; each
native alternative retains its checked argument conversions, callback
adaptations, result metadata and effects. A mixed call is ordinary HIR control
flow: save the evaluated Function once in a typed temporary, compare closed
native descriptors, execute the selected typed native call, or use the finite
source-specialization dispatch. Arguments execute once in the selected branch.
This does not add a generic callable object, a runtime planner or a wire-format
extension. Internal descriptor equality does not change the source language's
comparison operators.

Saving the callee follows the evaluation-order discipline of
[A-normal form](https://www.cs.tufts.edu/comp/150FP/archive/cormac-flanagan/anormal.pdf),
not a new runtime abstraction. Indirect calls retain a checked result ABI, as in
the upstream [MLIR Func dialect](https://mlir.llvm.org/docs/Dialects/Func/#funccall_indirect-funccallindirectop).
The physical call-result register admits every concrete source target's result;
an explicit Narrow then establishes the caller's more precise flow contract.
Structural return-layout sets must not be silently treated as exact flow sets.

`tests/NativeSessions/MixedCallableChecks.cs` exercises source, source-text-free
standalone HIR and detached native artifacts at O0/O2 with both
filesystem-controlled branches. Its oracles
cover native/native and source/native alternatives, heterogeneous results,
captured state, computed-callee evaluation count, argument mutation, native
callback specialization and builtin/namespace results. On POSIX a live Process
also crosses the mixed call before waiting. Persistent O0/O2 sessions reassign
compatible native callees, including a higher-order filter/take-while protocol,
without losing the callback specialization. The saved-callee location remains
a Temporary, not a persisted session root.

### Persistent native submission ownership

`CompilerSession` retains a semantic arena, specialization cache and one
`NativeHirLoweringSession`; it publishes semantic facts only after execution
reports initialized root locations. Runtime feedback projects `NativeValue`
descriptors and typed allocation fields directly into semantic observations,
without allocating historical runtime `Value`, `ScopeValue` or `CellValue`
objects. A location-capture observation updates the original semantic source,
not a newly shadowing declaration. Namespace allocation-site metadata reconnects
returned constructor fields to their semantic locations. Declaration source
occurrences distinguish an actually initialized branch from an unreachable later
assignment; copied per-invocation field identities remain distinct even when they
share one constructor's allocation site.

`NativeExecutionSession` owns one native interpreter and runtime context across
submissions. Canonical entry locations (including hidden lazy module imports), pending
memory, static declarations and escaped work belong to that session, not to a
single entry frame. Persistence and work escape are separate: private canonical
slots may retain storage, but only explicit exported roots extend child-work
ownership beyond the submission. Failure reconciliation accepts
only initialized new declarations while retaining mutations made to preexisting
aliases. Editor analysis forks these accepted facts and must not publish probe
specializations or change the runtime callable table.

Session call requirements retain the declaration provenance of their abstracted
capture locations alongside their cells and storage types. Projecting an
invocation propagates callable ABI obligations even when deferred writes are
suppressed, and attaches them to canonical storage owners rather than temporary
thunk aliases. Otherwise a later replacement can either lose its source
definition or escape the dispatch table of retained code.

Session call obligations follow executable lifetime, not evaluation laziness.
A published function body calling a mutable captured callable retains that
call's parameter/result ABI even for an eager scalar result. Replacing the
capture must satisfy that ABI (`E0213` at the replacement function); compatible
replacements extend the existing native dispatch table. A consumed top-level
scalar call creates no retained body and may be specialized again in a later
submission. Stable generic symbols still admit independent argument
specializations. Editor queries fork these obligations and never publish their
failed or successful candidate targets into the accepted generation.
`tests/PathProjection --editor-session` covers this through actual native execution
at O0/O2, accepting both with and without runtime feedback, including repeated
failed editor queries, failed real compilation, compatible live replacement and
independent top-level/generic specialization controls.

Path literal anchoring is definition-context state. Re-lowering old callable or
deferred Range code after `cd` must not capture the new directory. Native Path
constants carry their HIR source identity and source offset; repeated submissions
have distinct source identities even when their displayed name is `<repl>`.
Ambient-owned sessions adopt the host current directory at submission boundaries.
An explicit initial working directory or successful session `cd` takes permanent
ownership of that directory; subsequent host directory changes do not override
it. No session operation mutates the process current directory.
Directory snapshots are immutable: a directory-changing native call resolves
against its invocation origin but updates the originating mutable execution
authority. Previously captured IO readers retain their old directory. A
deferred invocation receives a mutable fork of its origin, and each later
capture snapshots that fork; iterator suspension restores the consumer context.
Deferred invocation retains its directory snapshot when it later selects newly
published code. The image projects Path literals under that invocation context,
without rewriting stored Path values or the default occurrence-origin cache.
Thus a deferred caller created in A can call a replacement `fn { ./b }` published
in B and yield A/b; replacing it with a closure over an already captured B/b
Path yields B/b. This is the existing session-image contract described in
[the runtime review](runtime-coreclr-review-v080.md), not a new literal rule.
An image-generation cache may preserve a captured literal by this occurrence
identity, never by raw spelling (two `./folder` occurrences can have different
origins). Newly executed occurrences capture the current session context.

`tests/NativeSessions` exercises the direct compiler/runtime boundary, including
actual configured stdin and a live Process returned across submissions. Real
terminal acceptance uses `tests/Msync.EndToEnd/repl_state_journey.py`; session-age
measurements use `tests/Performance/repl_tail.py` rather than compile-only counts.

### Typed pointer boundaries

The public `InvokeAsync` host boundary checks pointer arguments against the
formal invariant physical ABI and the owning layout, without reading payloads.
Thus an uninitialized output location is valid, but a Number location cannot be
passed as writable `Number | Null`. Indirect pointers may change after entry;
each dereference checks physical read compatibility, and Store admits ready kinds
or pending producer masks to the actual selected location after normal Path/String
coercion. A narrower physical location must not silently receive a wider value.

Pending memory belongs to its allocation through weak-key metadata, not to an
activation. Returning a FrameAddress keeps that allocation and its pending data
alive; function retirement cannot erase the metadata. `PointerStorageChecks`
exercises strict artifact validation/roundtrip, location rebinding, indirect and
cross-callee writes, a monomorphic pointer reloaded through a logical sum,
returned pending pointers and host/nested-store ABI rejection. Core properties
also pass pointer descriptors through typed cells, regions, environments and
materialized Ranges.

### Closed compile-time and editor descriptors

`MetaTypes.Evaluate` produces `NativeValue` descriptors directly. Semantic folding
extracts the closed literal alternatives and retains HIR structural denotations
for Scope, Range and nullable transformations; it does not instantiate runtime
`Value` subclasses. Native execution shares this pure metadata evaluator, so
trait semantics are not independently duplicated in the compiler and VM.

Editor `KnownPath`, `Endpoint` and physical-owner facts use `NativePath` and
`NativeConnection`. Native path derivation retains definition-context working
directory/home and separate symbolic/native spellings; qualifying a remote path
clears local projection context. Completion passes these descriptors through to
native transport startup without converting to historical runtime values.
PATH qualification resolves a pathname, not executable permission admission:
named lookup accepts an existing file, and explicit paths can name future files.
Relative PATH entries use the captured working directory. `shell.which` retains
its separate executable-access test; it must not replace qualification merely
because both operations search PATH.
`tests/MetaTypeValues` follows native source, relocatable and executable images
at O0/O2/O3. Compiler editor fixtures exercise qualification and projection;
`StaticPathChecks` also checks host-independent descriptor properties.

Editor metadata distinguishes stable lexical facts from scalar argument data
shared by type-specialized callables. `RuntimeDependentMetadata` follows those
scalar parameters through copied values and captures; it suppresses physical
endpoint, Path and text hints, without changing source inference or Type-value
witnesses. Stable Scope-member identities and lexical literal captures retain
their facts. Identical complete Path descriptors from all typed branches can be
projected without probing the filesystem; differing endpoints or definition
contexts cannot be collapsed.

Incomplete uncalled bodies use a static bound-return projection for a unique
known callable template. It intersects namespace capabilities across explicit
returns and the implicit tail, retains all field-definition origins, and stops
recursive return queries. It neither instantiates a callable nor evaluates source
effects or actual argument values. Unknown or mixed non-Scope returns do not
advertise namespace-only fields.

The historical registry's temporary metadata adapter was removed with that
registry. Compilation, editor projection and native execution do not convert
metadata results into historical runtime values.

### Native contextual preparation

`NativePreparation.Prepare` consumes bound native IR before VM construction.
The CLI keeps separate `bind` and `prepare` telemetry phases and passes configured
execution options, timeline ownership and cancellation through preparation. A
loaded executable is not rebound or replanned. Compute-only images bypass the
partial evaluator entirely; this is not a new per-instruction runtime layer.
The binding context travels separately from relocatable `NativeProgram` data;
only the prepared result receives both `Context` and `Executable`. An empty plan
table decoding to null is not permission to prepare an executable again.

Known registers and references use ordinary typed native frame/storage layouts.
The evaluator models scalar/control instructions, local allocation and memory,
closed native functions and selected filesystem metadata. A source call can be
computed only while its writes remain in call-owned storage; a write through a
captured or caller-owned location ends the region. Unknown control, backedges,
source callbacks, process calls and opaque effects remain executable instructions.
No old `Value`/`Module` conversion or invocation of either VM is involved.

Selected local metadata becomes portable constants and known branches become
jumps. A demanded regex/directory selection becomes an explicit typed membership
snapshot. Preparation may freeze an iterator's selected input without running its
loop body or callbacks. Undemanded lazy selections are not enumerated. Transfer
bytes are never read just to freeze membership. Remote finite work remains under
the native transport owner rather than opening a classification connection.

`NativeTransferGraph` records portable inputs, concrete resource vectors,
destination modes and source origins. Its destination-version liveness removes
an overwritten unobserved transfer, including its operational error; demanded
Bool results keep their producer. Live RAW/WAR/WAW edges form the native plan's
predecessors and grouping metadata. Prefix-indexed ancestor queries keep namespace
safety checks independent of the number of unrelated files. Local directory
merges preserve surviving members and follow destination links in their footprint;
a changing source/ancestor or unproven nested-link footprint ends preparation
instead of asserting an unsafe overwrite proof. Composite directory operations
remain composite; this implementation does not claim leaf expansion or execution
of grouping metadata as transport batches.

`tests/NativePreparation/test_journey.py` exercises the real CLI `.mso` → binding →
`.mexe` path at O0/O2, then removes source/object files, changes cwd and mutates the
filesystem before execution. Controls cover dead missing-source writes, frozen
metadata and regex/iterator membership, opaque writes, actual publication waits,
known wrappers, captured mutation, demanded Bool results, directory merge
survivors and symlink-alias footprints. Disassembly is inspected for actual native
plans; file contents and stdout, not plan counts alone, establish behavior.

### Native transfer acceptance after retirement

`tests/Msync.Transfer.Tests` calls native builtins/host IO and executes recorded
`NativePlannedTransfer` kinds directly; its independent-operation driver is not a
product batching layer. Local and remote byte/mode/link/overlap/cancellation,
directory-action, source-kind, concrete-error and continuation oracles survive
the old engine's removal. The old explicit lease API and its framing/owned-fd
white-box tests are retired rather than recreated around native descriptors.

One finite `NativeRange` transfer owns its channels. Real loopback OpenSSH probes
with connection sharing disabled copy 36 members (binary files, unusual names,
a directory and a dangling link): upload/download/same-endpoint use one actual
client/authentication/exec session, and cross-endpoint relay uses two. Separate
independent statements are not claimed to share that lease. The existing full
selector/deferred-filter/recursive-iterator journey still uses exactly 28 clients,
with no SCP and no extra metadata authentication. Local cancellation observes the
real owned stage on a dedicated thread, avoiding thread-pool callback starvation
while retaining old-target and owned-stage cleanup checks.

### Active acceptance catalog

`packaging/state_contracts.py --list` is the executable inventory. Required
projects fail loudly when absent; legacy selection names expand to deduplicated
maintained native contracts, never optional skipped projects.

| Retired selection/mechanism | Maintained contract |
| --- | --- |
| ConstantInterner, OptimizationGraph, PredicateOptimization, LoopOptimization, GlobalRedundancy | NativeOptimization / NativeMiddleEnd / NativeExecution |
| ScopeScalarization, LocalSlots, AggregateArguments, ScopeLayout, CrossBlockMemory, MemoryEffects | NativeModel / NativeAllocationV090 / NativeSessions / NativeMiddleEnd / NativePreparation |
| Historical format-one path probe | NativeArtifact plus PathProjection; old format-one stack bytes reject with recompile guidance |
| Scheduler graph and namespace internals | NativeExecution / NativePreparation; real transfer filesystem/metadata journeys remain primary |
| Tail-call pass instruction identity | TailCallOptimization source/codec gate, including 100,000 calls and captured freshness |

PathProjection retains host-independent Windows grammar, captured Path fields and
environments, strict native codec, real child process results and persistent
editor feedback. Its physical Windows-root checks require Windows. ContextBinding
retains origin/no-replan, residual branches, nested loop effects and post-symlink
canonical reads. StdlibLoading retains actual source publication and replacement
behavior. Old mechanism-specific counters are not manufactured for the native VM.
The Components workflow requests actual native preparation/lowering/runtime stages.
The language-upgrade affine scan defaults retain output/order/effect oracles; the
explicit `--require-fusion` experiment is not an achieved native fusion claim.

CI only supplements local acceptance with unavailable platform/install journeys.
An unexecuted Windows, SSH or install journey is not a local pass.

### Historical typed-HIR migration

Format-3 typed HIR is a source-free contract, including old public collection
programs whose embedded typed library bodies referenced historical native
bindings. `NativeHistoricalBindings` builds validated ordinary native functions
using the shared memory emitter; no HIR-to-stack backend or source parser is
involved. The HIR lowerer relocates the fragment's finite type/layout/constant/
projection/function tables and links direct and first-class references to those
functions. Current HIR without historical symbols does not build this fragment.

Historical Storage contracts become explicit finite address/header ABIs before
constructing `NativeProgram`. Their logical Type descriptions remain metadata,
not execution kinds. Metadata on Range construction is separate from its
physical element ABI; `empty_of` becomes an ordinary empty MakeRange with the
evaluated Type operand. Scope-or-Storage unions include actual namespace layouts
and compatible header layouts, not nominal identities. A typed union-copy before
dependent Narrow retains strict runtime layout admission where old HIR's logical
flow view is wider than its fixed physical storage declaration.

`tests/HistoricalHirNative/journey.py` exports unchanged HIR from a frozen v0.8.6
producer, deletes the source and checks native O0/O2 HIR/object/executable routes
against independent outputs. A second copy removes only diagnostic source text;
all typed tables remain byte-identical, and its native output must equal the
unmodified HIR's output. The script checks physical CodeTypes and executable
native calls rather than rejecting harmless historical names in debug metadata.

The 2026-11-12 integration run covers twelve frozen-source fixtures and 96 real
CLI route cases, including public Set/Heap, membership versus shared payload
identity, extraction, slots/arena links, first-class bindings and builtin
carriers. Every original/debug-text-stripped pair emits byte-identical native
objects. `.temp/hir-compat-owned/journeys-current.log` and
`.temp/historical-hir-native/33dbf13a5f354b5a9520f0ed923c4ec4/result.json`
retain the receipt; this is functional compatibility evidence, not a throughput
claim. The ordinary current native pipeline also passes its independent
source/standalone-HIR/codec routes after integration.
After appending layout-only `AllocateScope`, the same maintained journey again
passes all 96 cases on the canonical-namespace checkpoint, with producer sources
deleted and unchanged payload hashes. The receipt is
`.temp/historical-hir-native/bfed70addb4d4045abc26b4220bc2947/result.json`;
the native payload manifest SHA-256 is
`9185f10b60169917be81266b5fa95aa6598a58f6056e2177c1c7abea9e5493b2`.

### Retirement audit

The historical production backend is removed, not retained as a fallback:

- Old HIR lowering/session, object bytecode and optimizer tiers are gone. The
  canonical compiler and persistent session lower through `NativeHirLowerer`
  and the native optimizer only.
- The old Interpreter, scheduling/planning implementation, Value-based native
  registry and TransferEngine are gone. `RuntimeProfile` drains native counters
  only; no retired-tier counters are fabricated.
- The `Value` hierarchy, generic Scope/Cell/Storage owners and language-shaped
  bytecode/artifact implementation are gone. Accepted format-6 input is closed
  portable recipe data imported to native IR, never source replay or a second VM.
- The runtime project depends on Core only, not Optimization. Native contextual
  preparation is an explicit phase; execution does not invoke an optimizer.

The deletion preserved native code and pure shared mechanisms rather than
removing directories blindly:

| Shared contract/mechanism | Current owner |
| --- | --- |
| Language semantics version, artifact/binding/transfer contracts, function/session dispatch metadata | `Core/Artifacts` (`LanguageContract.Version` remains `0.8.0`; product `0.9.0` is separate) |
| Source spans, compilation observations and sizes | `Core/Diagnostics` |
| Process control interface and connection kind | `Core/Processes`, `Core/Connections` |
| Text slices, type/number/text/path semantics | `Core/Types`, including native path semantics |
| Lexical paths, Word32 and native process controllers | `Runtime/Native` |
| Resource trie/statistics and plan resource identity/index | `Runtime/Scheduling`, `Runtime/Planning` |
| Resource paths, shell protocol source, staging publication and overlap helpers | `Runtime/Transfers` |
| Filesystem identity/metadata, platform copy and Unicode tables | `Runtime/Platform`, `Runtime/Text`, `Runtime/Generated` |

`SourceSpan` and `TextSlice` were extracted from old instruction/range files;
`IProcessControl` no longer lives beside a deleted ProcessValue. Generic names
such as semantic `Value` facts, native capture mode `Value` and typed ID `.Value`
are not a runtime hierarchy. Historical `TypeKind.Storage`/`TypeRef.Storage`
remain descriptor knowledge for accepted wire metadata only: source Storage,
allocator modules and live allocator carriers are absent.

Post-removal verification used an isolated managed CLI build (zero warnings or
errors) and all eight real native-model journeys at O0/O2 through source,
standalone HIR, detached `.mso` and detached `.mexe` (64 route cases). Receipt:
`.temp/retirement-v090/native-model-journey/result.json`. This does not establish
whole-release acceptance or performance. Historical development probes that
construct removed bytecode/Value/tier internals need explicit migration to the
new typed contracts; they are not grounds to restore the old runtime. Their
source cases and filesystem/process oracles must be retained.

### Read-only native artifact inspection

The LSP `msync/artifact` request receives bytes and a display URI, not a source
path to load. Binary inspection decodes and validates portable native data (or
imports accepted format-6 portable data), then renders it without constructing a
runtime image, running source, contextual preparation or filesystem operations.
The editor schema remains version 1; binary artifacts keep the `bytecode` kind,
actual input format version and stage. The text identifies `native-v1`, including
when the input is accepted format 6. HIR retains its distinct `hir` representation.

Native rendering gives functions, registers, static layouts, projections,
constants and instruction targets selectable definitions. Internal references are
linked after all definitions exist and ignore quoted literal/debug metadata.
Source references retain UTF-16 source offsets; only file, stdlib and VS Code
remote URIs become actionable links. Unknown command schemes remain inert text.
Native plan rendering preserves ordered result transfers, predecessor edges,
resource access sets, batches, concrete destinations and source spans in both
text and the existing `executionPlans` JSON schema. Unpaired UTF-16 surrogates
are escaped rather than silently replacing the stored code unit.

`tests/NativeArtifactEditor` links the production renderer to check exact
UTF-16 ranges/navigation, native prepared plans, malformed-input diagnostics,
cancellation and closed format-6 views without compiler/runtime dependencies.
`tests/Msync.EndToEnd/artifact_view_journey.py` exercises the actual LSP request,
source-free native objects/executables, unchanged artifact hashes, injection-safe
metadata, virtual URIs, cancellation with continued source hover service, and
accepted format-6 inputs. Its optional `--plan-artifact` accepts the portable
prepared fixture produced by `NativeArtifactEditor produce PATH`.
