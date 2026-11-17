# LLVM-derived graph to the existing Native target

## Boundary

`LlvmNativeLowerer` is the target-emission adapter after LLVM-derived IR passes.
It consumes the graph and producer-owned logical type/layout annotations, never
the original HIR body and never `NativeHirLowerer`. It emits the existing
`NativeProgram`/`NativeBackendCode` consumed by artifact serialization and the
existing runtime. It introduces neither an LLVM backend dependency nor a
second interpreter. It is now the default target adapter used by public Compiler
and CompilerSession callers after graph lowering and the shared LLVM-derived
optimization entry, including the integrated SROA and InstCombine passes. It does not call the old
optimizer/lowerer. Complete IR/optimization coverage remains separate work.

`HeapAlloc`, `FileIn` and `FileOut` remain explicit through the optimization
boundary. Only target emission converts them to the existing Native allocation,
typed builtin I/O or transfer operations.

## Target contracts inspected

* `src/Msync.Core/NativeModel/NativeCode.cs`: typed slots, instruction operands,
  signatures, allocation plans and storage plan records.
* `src/Msync.Runtime/NativeModel/NativeBackendCompiler.cs`: NativeProgram is the
  existing target input; backend compilation does not select an LLVM pipeline.
* `src/Msync.Runtime/NativeModel/FrameAllocation.cs`: storage plan arrays are
  required by runtime frame construction, independently of optimization level.
* `src/Msync.Hir/Lowering/NativeHirLowerer.Types.cs`: target addresses have
  `NativeCodeType(TypeKind.Never, Pointee: logicalTypeIndex)`. Scope values carry
  logical layout domains, while String and Range have different physical
  carriers despite all being opaque pointers in the LLVM-derived graph.
* `src/Msync.Hir/Lowering/NativeHirLowerer.Aggregates.cs`: known typed I/O uses
  `CallNative`; transfer uses `Transfer`; scope allocation uses `Allocate` and
  known field addresses use `AddressOffset`.

The existing lowerer is inspected for established target contracts, not invoked
or copied as an alternative implementation. `NativeStoragePlanning` currently
constructs address/escape analyses; this adapter does not invoke that pass or the
old optimizer. Already-known logical-heap placement and function-local scalar
storage produce the mandatory target plan directly.

## Emission mapping

| Graph operation | Existing target |
| --- | --- |
| SSA result / argument | Typed temporary / parameter slot |
| Scalar alloca | Local slot plus FrameAddress |
| HeapAlloc with producer layout identity | Allocate with LogicalHeap allocation plan |
| Struct GEP with known field ordinal | AddressOffset(layout, ordinal) |
| Load / Store | Direct declared-pointee Load/Store; relocated literal global loads become Constant |
| Floating arithmetic / comparison, Boolean not | Binary / Unary |
| PHI | LLVM IncomingReg construction: predecessor source copies and destination entry copies |
| Branch / conditional branch / return | Patched Jump / Branch / Return |
| FileIn Text/Lines/Bytes | Typed CallNative io.read_text/read_lines/read_bytes |
| FileOut typed write/append | Typed CallNative io.write_*/append_* |
| FileOut Transfer | Transfer |
| Explicit runtime binary declaration mapping | Binary, using supplied operation identity |

Constants are emitted in a function prelude, not at first use in an arbitrary
branch. SSA slots are memoized independently of definition order. PHI codegen
follows the baseline construction in LLVM `PHIElimination.cpp::LowerPHINode`
(commit `a55e64432a516fd6507d3ecfba7407cfe425f940`, lines 378–424 and
519–588): allocate a fresh IncomingReg per PHI, copy each predecessor source
into IncomingReg before its terminator, then copy IncomingReg to the destination
at block entry. Copies for different successor blocks have distinct IncomingRegs
and can execute before a conditional branch. No custom cycle solver, edge-stub
schedule, critical-edge optimization, register-class/liveness machinery, or
undefined-value lowering is introduced. The pinned reference is vendored as
`vendor/llvm-reference/llvm/lib/CodeGen/PHIElimination.cpp`.

Native export aggregates `{ ptr null, ptr null, ptr nativeName }` are decomposed
as compile-time components across their InsertValue chain, then emitted as the
existing MakeFunction with an empty environment and the exact relocated name.
This is target aggregate representation lowering, not an IR scalarization pass.
Source function/capture descriptors use the same decomposition, as described below; unrelated aggregate ABIs remain unsupported.

## Producer metadata dependency

Opaque pointers intentionally do not encode language storage types. The producer
records `LlvmValue.LogicalType`, `LlvmAllocaInstruction.AllocatedLogicalType`
and `LlvmHeapAllocInst.LogicalLayout` as explicit project codegen annotations
(not upstream LLVM fields). The result retains Entry, Types, Layouts and
StructLayouts; it does not retain a strong table of SSA values. Clone copies
annotations, and mem2reg-created PHIs copy the promoted pointee annotation.
`LlvmNativeMetadata` constructs target type/layout descriptors directly from HIR
source facts while producing the graph; it never invokes the old lowerer or an
alias/global heap solver. Only exact Boolean/Number(binary64)/empty-null-struct
primitive mappings can omit annotations. No arbitrary integer or opaque-pointer
carrier is guessed. No name-based builtin or stdlib recognition is introduced
at the target stage.

`Path` and finite value unions have an explicit Native record storage marker
on their LLVM struct type. Their fields describe the IR ABI, not separately
addressable Native cells. SROA and InstCombine retain whole-record memory
accesses; whole-value promotion and ordinary aggregate decomposition still
apply. In a file traversal, the iterator writes one typed `Path` cell and
the transfer consumes the whole load, including after loop rotation. This
also keeps the result's logical type attached through target emission.

A checked structural scope view supplies the member contract, while the
reaching value supplies the physical layout domain. Taking a view or reading
a member through one must not copy layout IDs from the view's type witness:
replacing a nested scope through an alias can leave the view valid and change
the field's offset in the replacement. A known singleton still lowers to a
constant offset; a finite domain uses the existing layout selection.

The adapter emits a nonnull NativeBackendCode directly, using existing Standard
handlers, one record per target instruction. It does not call
NativeBackendCompiler, NativeStoragePlanning or NativeOptimizer. Optional raw,
unsigned, fused and connection-specialized execution forms are not requested.
Typed operand-kind metadata is emitted directly from target type descriptors.
Same-carrier loads do not add runtime narrowing or layout admission checks:
logical Scope domains, Range element contracts and descriptor table indices do
not change a carrier. Different value Kind/tag formats require an explicit
producer representation conversion; this target slice does not synthesize one.
Store always
carries its pointee type and ordinary-write mode, as required by the existing
artifact wire contract. Allocation sites receive stable target ordinals rather
than collapsing all allocation telemetry to site zero.
Purity/context flags use direct opcode and producer-type facts, matching the
runtime's existing classification. Layout/field/builtin signature linking remains
the existing execution-image operation, not new proof work.

For iteration, producer-bound Open/Next/Close declarations map to existing
activation iterator indices, not ordinary user pointer slots. NextIterator
produces the original NativeDatum and Store writes it into the annotated item
cell without adding an eager resolve. Its exhausted-target jump is expanded
into the graph's Boolean protocol. Existing activation cleanup owns exceptional
close; producer-generated close calls handle exhaustion, break and return.

Representation-changing coercions, singleton initialization and unsupported source constructs remain explicit implementation
work, not a fallback to the old path.

## Physical Load contract

The graph's logical type annotation may differ from a location's declaration
only by referent layout domains or Range element information, without changing
its physical carrier. Such a Load is emitted directly; it does not cause a
Narrow instruction or an entry-layout check. The target and artifact contract
share `NativeTypeRelations.SameStorageRepresentation`. A genuinely different
carrier/tag layout requires an explicit producer lowering, not target-side
validation or a reconstructed heap proof.

Integrated supported-route execution and Native2 stream roundtrips are recorded
in [LLVM IR port status](llvm-ir-port-status.md). The default production caller
switch is covered separately by `artifacts/diagnostics/llvm-production-session/`
(O0/O2, 12 accepted/reconciled submissions per level). The actual Release CLI
source/detached HIR-object/detached bound O0/O2 journey also passed, with 29 exact
output files per route and selected cast-failure checks on each route; evidence
is under `artifacts/diagnostics/llvm-primary-cli/`. The Linux x64 Native AOT
binary in `artifacts/native/llvm-primary/msync` subsequently passed the same
source/artifact/bound routes; publish/run logs and receipt are in
`artifacts/diagnostics/llvm-primary-aot/`. These receipts establish neither
complete LLVM O2 coverage nor a performance gain.

## Source functions and location captures

The internal LLVM-derived descriptor is now `{ ptr code, ptr environment,
ptr nativeName }`. A source function stores its `LlvmFunction` symbol as code;
a native export stores null code and the relocated native name. This is an
internal representation choice: the existing Native target descriptor ABI still
stores its original integer VM function index, environment reference and native
name. No new runtime pointer ABI or capture mode is introduced.

`SourceFunctions` records capture count and logical result type. Capture-location
arguments prefix ordinary LLVM function parameters. Target emission places
Native location-capture slots first, then ordinary value parameters; each LLVM
capture argument maps to FrameAddress of the already-bound Native capture slot.
The signature contains `NativeCaptureMode.Location` for these original source
location captures. Captured scalar cells and captured iteration-item cells are
explicit producer HeapAlloc one-field objects, not frame objects promoted by an
escape solver.

Source closure construction first allocates an explicit environment struct of
captured pointers. Target code loads those pointer payloads and uses the existing
MakeFunction operation to create its NativeEnvironment. The pointers still name
the same original cells: there is no captured-value snapshot or second semantic
cell. The additional NativeEnvironment pointer container is a migration cost
recorded by `closure.abi_environment_containers`; the original source environment
allocation is not claimed eliminated. Extracting the environment from an
existing descriptor uses EnvironmentAddress and the source-known environment
layout.

A statically resolved source call retains its actually evaluated descriptor via
standard operand bundle `msync.callee`. Target Call uses that descriptor and the
known target index; only ordinary arguments enter Inputs, because activation
initialization supplies captured locations from the descriptor environment.
`Snapshot=false` and all `Borrowed=false` use the existing NativeArguments frame
view: NativeFrame.Initialize copies these evaluated SSA arguments into owned
callee parameter storage before running the body. No new borrow proof,
argument snapshot array, per-call closure reconstruction or Native optimizer is
needed. Existing tail-call snapshots remain the runtime's responsibility.

Template identity declarations have their own code symbols and entries in
`TemplateFunctions`. They emit the existing non-executable IsTemplate placeholder
with declared argument/capture metadata and a Null result; they are not replaced
by a particular specialization's identity. Actual calls select their specialized
target while retaining the original descriptor and environment.


Producer-bound CallableCalls declarations cover dynamic invoke/adapt without a
new opcode. Their metadata maps original identity symbols to specialized target
symbols. Emission uses the existing FunctionDispatch wire encoding and Native
Call/AdaptFunction operations, preserving the actual evaluated descriptor.
NativeCalls and Constructors are ordinary typed runtime declarations; their
explicit metadata selects existing CallNative or MakeRange target operations,
not source-name guessing or generic HIR evaluation.

## Singleton globals

The HIR singleton-to-global producer and target mapping below are implemented.
The real TypeOperationJourney exercises singleton initialization/reuse together
with physical member layout selection; this is not merely a metadata table.

`StaticScopes` binds an ordinary LLVM object global and its i1 initialized flag
to the producer's fixed target layout and declaration/site identity. The object
has actual concrete struct storage; the flag is an ordinary mutable global.
The source graph owns its ready-load, initialization/reuse branch, field GEPs,
initializer stores and final ready publication. These are not a hidden runtime
initializer or a global heap analysis.

At the existing target boundary, each binding creates one NativeProgram.Statics
entry. Flag loads become TestStatic and true publication stores become
PublishStatic. Uses of the object global obtain the canonical StaticAddress,
including uses in field-address formation and capture environments. Addresses
are materialized at the actual use rather than on first use in another branch
or in a function-entry prelude; this avoids uninitialized cross-branch temporary
values and preserves lazy allocation timing without adding dominance analysis.
All aliases name the same existing runtime StaticState allocation. The root
runtime continues to own static publication and allocation lifetime.

Target function telemetry counts static tests, address materializations and
publications; the module phase records its static declaration count. Emitted
operations retain the consuming source instruction's span.

## Representation-copy contract: nullable function results

The `nullable-returns` source journey exposed a target binding error: expected
8, observed 0 before the fix. Its source IR correctly returned Number|Null from
`choose(true)` and tested the two nullable results. The binding for Represent
had emitted Copy with `Type` set to the destination union descriptor.

In the existing runtime, a typed Copy means a shared-format byte copy:
ExecuteCopy → NativeFrame.Copy(type, ...) → NativeMemory.Copy(type, ...) →
NativeStorage.CopyTrusted(type, ...). Number has 8 bytes; Number|Null has a
16-byte tagged carrier with payload offset 8. Copying Number 7 (bits
`0x401C000000000000`) as the union therefore copied its leading zero byte as
NativeKind.Null, explaining the taken outer else branch and result 0. Neither
source function signatures, argument ownership nor ReturnSlot caused this loss.

Represent now emits Copy with `Type=-1`, selecting the existing generic
source/destination carrier encoder. It handles mono↔union movement and pending
NativeDatum values; no new narrowing/admission check, backend carrier analysis,
or runtime instruction is introduced. PHI same-format copies retain their
explicit typed-copy fast path. The changed nullable-return source journey subsequently passed in the
root-coordinated TypeOperationJourney scalar cohort.

Public `NativeArtifact.Validate` has its own logical Copy admission contract;
Read/Write do not implicitly run ValidateCore. A potential alignment of that
explicit validation contract is not a runtime check on this lowering route and
must not be described as an implicit artifact-load proof pass.

## Contextual preparation owns the rewritten entry image

NativePreparation's transfer rewrite changes entry instructions, added slots and
PC positions. Once primary target emission supplies Backend before preparation,
retaining the previous entry NativeBackendFunction would execute stale records:
NativeBackendCompiler returns an already-backed program without regenerating it.

`NativeStandardBackendEmitter` in Core is the shared direct encoder for both
initial LLVM-derived target emission and a rewritten preparation entry. It emits
existing Standard handlers, Span=1 and Operand=current emitted PC, with actual
slot count, no requested raw/unsigned/fused specialization, and the existing
opcode/context classification. ExecutePlan/PlanResult and the other contextual
operations participate in that classification. Explicit Convert/Narrow operand
metadata uses the same direct descriptor rule as NativeExecutionOperand.Link;
ordinary storage accesses do not acquire layout admission checks.

The rewrite rebuilds only the modified entry backend function and keeps every
untouched function object and the connection-index table unchanged, including
saved Native2 images whose untouched functions contain fused handlers. It never
sets Backend=null to reenter alias/storage analysis or NativeWorkPlanning. An
input without Backend retains its existing absence; this is not a new fallback
from the primary target path, which always supplies an image.

`compiler.prepare.image.function` records the actual rewritten source/emitted
instruction counts, replacement sites, added slots, preserved functions and
connections alongside baseline classification counts. A focused real source
fixture is available as `tests/LlvmSession --prepare-image`: it requires actual
ExecutePlan rewriting, freezes filesystem selection membership before adding a
new file, roundtrips the prepared Native2 image and checks transfer outputs.

The real fixture passed at O0 and O2. Its rebuilt entry streams contain 17 and
14 instructions respectively; each mode preserves one untouched function,
emits one execution plan and checks three filesystem outcomes (selected file
copied, ignored file absent, late-added file absent). Evidence is
`artifacts/diagnostics/llvm-prepare-image/{build.log,run.log,receipt.json}`.
This is actual source preparation and saved bound-image execution, not a
synthetic backend-table-only check.


## Native factory record field order

Ordinary native record-producing builtins use their physical schemas from
`Core.NativeBuiltinLayouts`, not the enumeration order of semantic Record Shape
fields. Member catalogue cases retain the physical LLVM struct, Native layout,
field ordinal and storage type. Enumerate/zip retain declared physical carrier
variants; a Range view does not re-encode its producer's item storage. This is
object ABI metadata, not a new Range operation or an entry admission pass. The
primary CLI journey exposed and corrected the concrete `index`/`value` order
mismatch in `std::ranges::generate`; its unchanged early-exit oracle is now 21.

## Source control-flow demand and ordinary LLVM branches

The actual public-session ADCE journey exposed a representation error: a Native
branch consumes a possibly pending Bool, but an ordinary LLVM `br i1` has no
waiting/exception effect. With `fn discarded(flag: Bool) { if flag { 1; } else
{ 2; }; 7 }`, a gate-held process output converted to Bool failed at O0, whereas
ADCE removed the unused LLVM branch and O2 returned seven. The optimization was
following its source algorithm; the producer had omitted a language effect.

`LlvmHirLowerer.Body.BranchOn` now represents that effect as an ordinary void
runtime call before a source `if`/short-circuit or `match` branch. Literal Bool
constants need no demand. Iterator-next and static/layout implementation guards
already produce physical ready Booleans through their runtime operations; they
are not source pending-condition expressions. The demand declaration has the
standard LLVM memory-effects attribute `memory(inaccessiblemem: readwrite)`:
waiting affects runtime state, not source-addressable fields. It can fail or
wait indefinitely, so it is not marked nounwind, willreturn, or speculatable.
No new LLVM opcode, heap analysis, validation pass, or source thunk is added.

The target fuses a demand with its immediately following branch on the same SSA
condition: the existing Native branch already performs the wait, so this usual
path adds no Native instruction. If optimization removes that control decision,
the target emits an existing Native branch whose two continuations are the next
instruction. It consumes the same datum without retaining an artificial LLVM
control-flow constraint. Constants folded by the middle end emit no target
demand. Prelude relocation and preparation retain both continuation PCs. Actual
compiler telemetry distinguishes constant, fused-branch, and standalone
continuation cases with the operation's source/function context.

The producer also mangles source functions in LLVM's reserved `llvm.` and
`__llvm_` symbol namespaces to `msync.source.<original>`, while preserving the
source name in `LlvmSourceFunctionInfo.SourceName` and the Native function name.
This prevents upstream ADCE's actual profiling-runtime-name rule from treating a
user function named `__llvm_profile_instrument_target` as instrumentation and
removing its observable file output. Ordinary runtime binding still uses
function object identity, not spelling. The independent review is recorded in
[`llvm-scalar-cohort-review.md`](../reviews/llvm-scalar-cohort-review.md).

## Replacement instruction ABI metadata

The first full scalar/CFG Native AOT CLI journey reached a Struct-typed Select
created from a previously annotated PHI. The Select's LLVM type was correct but
its definition lacked the existing Native ABI hint, so target slot selection
could not distinguish the runtime carrier. `LlvmValue.ReplaceAllUsesWith` now
copies a present hint to a replacement instruction only when that instruction
has none. Constants are excluded: their context uniqueness still requires
independent per-Use expectations. Existing annotations are never overwritten.
Cloning and replacement therefore preserve the same representation information
without each transform adding a target-specific admission or inference step.
The hint does not participate in LLVM instruction equality, pointer identity or
optimization legality. Constant GEP target selection likewise stores inferred
carriers in selected Native slots, never back into source graph IDs.

Both the managed CLI and Native AOT binary pass all three source/object/bound
routes at O0/O2 after the fix, comparing 29 exact outputs per route. See
`artifacts/diagnostics/llvm-scalar-aot/receipt.json`; the accepted binary hash is
recorded in its adjacent `binary.sha256`. The earlier failure binary is retained
under `artifacts/native/llvm-scalar-pre-abi`, not used as the accepted product.

Common-code sinking also creates fresh operand PHIs rather than replacing
an annotated definition through RAUW. A concrete component graph has two
predecessor loads from different Number locations and a join result PHI.
SimplifyCFG sinks the loads into one load whose pointer is a new operand PHI;
the original creation site left that opaque pointer without a definition ABI.
The sinker now copies the originating operand Use's expectation onto the new
PHI and its incoming Uses, falling back only to a nonconstant definition's
existing hint. Shared constants remain untouched. The existing sink telemetry
records `sink.phi_operand_abi_copied`; no legality constraint, alias inference or
additional solver is introduced.

SSAUpdater already accepts the caller's known logical ABI: LCSSA and rotation
supply the original instruction hint, and LICM's load/store promoter supplies
the load result or store operand view. These consumers require no new analysis.
Focused component, replacement properties and source journeys are staged in
`.temp/llvm-ssa-abi/`, including captured Number-location selection and dynamic
callable selection through the public O0/O2 compiler and detached Native2.
The captured-location source reproduces the pre-fix failure with the accepted
`32abb528...` AOT binary: O0 compilation succeeds, but O2 compilation exits 134
with `LLVM target requires producer logical type for PointerTyID` at
`Slot → Select → Body.Run`. The new operand PHI can subsequently become Select,
so its creation hint must survive that ordinary RAUW as well. The repro logs
are `.temp/llvm-ssa-abi/captured-prepatch{,-O0}.log`. The diagnostics trace
`captured-prepatch.trace.jsonl` confirms the actual source path creates exactly
one sink operand PHI (`sink.required_phis`, `sink.phi_created` and
`sink.instruction_group_sunk` are each 1), before the failing Select emission.
Post-fix execution is pending
root's serialized integration; the accepted CLI receipts above remain unchanged.
The minimal source is:

```msync
let left = 7;
let right = 9;
fn choose(flag: Bool) { if flag { left } else { right } };
choose(true) * 10 + choose(false)
```

Its result is 79: capture operands are existing Number locations, not new
by-value captures. The missing information concerns the new SSA carrier only;
ordinary location identity and runtime layout are unchanged.
## Constant GEP expressions and instruction-form addresses

The foundation cohort uses `LlvmGepOperator` for both instruction-form GEPs and
interned `LlvmConstantExpr` GEPs. The target resolves their common root, APInt
byte displacement and declared layout through one relocation implementation;
it emits the existing Native `AddressOffset`, not an executable constant
expression interpreter or a new LLVM extension. Constant expressions are
materialized at the consuming control-flow position, like global addresses,
while existing instruction definitions retain their SSA register reuse.
No source-graph type annotation is mutated by target selection. The existing
per-use Native pointer view remains authoritative. The target reports
`addresses.constant_gep_expression_uses` alongside relocation/materialization
counts in the enclosing function/source trace.

This closes the actual O2 source/CLI failure encountered when constant folding
turned a static-field GEP into a ConstantExpr. It is target representation work,
not another optimization rule, Range classification or heap proof. Raw scalar
reinterpretation, dynamic indices and broader machine-value lowering remain
the separate target work described in `llvm-machine-scalar-abi.md`.

## InstCombine scalar Function stores and producer storage layouts

The real combined `first-function-field` journey exposed a target-boundary gap:
LLVM `msync.function` occupies three pointer fields (24 bytes in the supplied
DataLayout), whereas the established Native Function carrier is an i32 callable
identity plus environment/name roots. The actual scope storage had its Function
at LLVM offset 0 and Number at offset 24. InstCombine emitted stores at offsets
0/8/16; resolving offset 8 as another ordinary Native field was incorrect.
The receipt is
`artifacts/diagnostics/llvm-instcombine/gep/instcombine-gep/first-function-field/combined/after.ll`.
Pinned `InstCombineLoadStoreAlloca.cpp::unpackStoreToAggregate` (1392 onward)
creates these field GEP/extract/store sequences from one simple aggregate store.

`LlvmNativeLowerer.FunctionMemory.Store.cs` selects the actual Function storage
location from the producer's struct-layout map, then interprets the relative
LLVM component offsets using the Function LLVM DataLayout. The same selector
serves component loads inside scope fields and direct Function locations. It
does not apply the LLVM offset as a byte offset into the compact Native carrier.

For a complete same-location three-component assignment in one block, the target
packs the actual SSA components into the existing ComposeFunction followed by
one typed Store. It only groups across GEP/cast/extract instructions, which do
not observe memory; an intervening different store, memory operation, call or
control transfer ends the group. There is no whole-Function load before its
first initialization. This also avoids inventing a partially initialized Native
Function value or a new Native2 component-store encoding. The existing compose
path retains pending NativeDatum semantics and the actual environment addresses;
no capture cells are duplicated. General isolated component mutation is not
silently treated as whole assignment.

Target function telemetry records component memory loads/stores and packed
assignments. Pointer-field failures now print the signed numeric LLVM offset,
root/structure/native-layout identity, declared field offsets and requested
view, rather than the APInt CLR type name. These changes are target codegen,
not a bypass or eligibility restriction on InstCombine's GEP/store algorithms.
The source-author did not independently build or time this patch; the root owns
the focused GEP and actual Range/midend execution receipts.

## Ordinary Freeze selection

A real InstCombine integration fixture introduced LLVM Freeze and reached a
missing target dispatch case. Scalar values and whole Function descriptors now
lower to an ordinary Native Copy at the definition point. Existing concrete
zero/null choices for LLVM undef/poison constants are copied once; no new runtime
check or Native/LLVM opcode is introduced. Copying a pending datum preserves its
future identity rather than demanding or remapping it.

A split Function/aggregate copies its existing leaves with their actual selected
carriers, retaining native-name provenance. It does not widen an exact address
leaf or force every whole Function through projections and recomposition. Whole
Function operands retain their ordinary representation, and the existing
aggregate boundary logic only projects fields needed by subsequent uses.
Pointer-carrier selection follows Freeze's operand identity and preserves any
existing per-use expectation. No source graph metadata is rewritten.

Detailed lowering records scalar copies, aggregate instructions/leaf copies and
explicit undefined-constant choices under `freeze.*`. The source-derived whole/split Function and repeated-use scalar-undef
journey is published as `tests/LlvmIr/FreezeLoweringChecks.cs`. Root ran
`--freeze-lowering`: all six direct/Native2 executions passed, recorded in
`artifacts/diagnostics/llvm-instcombine/freeze-focused.log`.

Root validation after the bridge: the managed InstCombine GEP source journey
passed all 16 executions, including raw/combined graph execution and Native2
roundtrips for the Function field case. This establishes the reported store/load
mapping repair on that journey; it is not a performance measurement or a claim
that arbitrary isolated Function component stores are represented.

The root subsequently reran the real managed default pipeline: both
`artifacts/diagnostics/llvm-instcombine/midend-managed-next.json` and
`range-managed-next.json` report passed. The Range receipt covers O0/O2 source,
HIR-to-object and executable routes (six routes, 32 files per route). These are
execution/artifact-boundary receipts for the integrated InstCombine/store bridge,
not isolated speedup claims.

## Physical storage facts across pointer canonicalization

### ScopeDomain is physical; Shape is a structural contract

`HirType.Shape` describes a structural row contract, not necessarily the actual
field order of an allocated or native-factory object. `LlvmNativeMetadata.Type`
therefore admits only the layouts in **ScopeDomain**; an empty domain remains
opaque even when Shape is present. `LlvmHirLowerer.Type` eagerly declares LLVM
storage structures only for those physical domain members. It caches the LLVM
value representation first so recursive source types follow ordinary cached
flow. ConstructedScope supplies an Exact domain explicitly.

This distinction matters for builtin records. Enumerate/zip physical storage is
defined by `NativeBuiltinLayouts`, while semantic row field order can vary.
The actual Range failure used an abstract `[value: Null, index: Number]` contract
for a canonical `[index: Number, value: Null]` enumerate object. Claiming that
contract was its physical layout caused either a compile error or a wrong read.
The root's four-item generate probe observed `0123`, `0000`, `0123` in fresh
processes, establishing a real miscompile rather than just missing diagnostics.
The producer no longer publishes that false physical claim. Semantic shape IDs
and ordering need not be made canonical to execute the correct object ABI.

### Retain the original declared storage type

Root tests then demonstrated why Domain-only correction was insufficient:
polygon members and ordinary zip/enumerate String fields use nonzero byte GEPs
from opaque formal pointers. Their original HIR GEPs selected a concrete physical
storage struct, but InstCombine correctly canonicalized the pointer arithmetic
to i8 offsets. Root semantic types could not recover the erased declaration.

Each producer field GEP now carries `TargetStorageType`, a reference to its
**existing** physical `LlvmStructType`. Target emission maps that exact type
through the existing StructLayouts table. LLVM arithmetic still uses the real
SourceElementType and offsets; no LLVM optimization reads the target hint.

* GEP-to-GEP RAUW preserves the declared storage type through byte-GEP rewrites.
* A zero GEP replaced by its own base transfers the type reference to the
  affected operand Use. Target code projects the offset-zero field relative to
  that base, rather than interpreting an abstract formal row as physical.
* Ordinary clone, operand growth and Use swap retain the reference alongside
  LogicalType. No SSA side table or per-projection object is allocated: the
  annotation is one nullable reference to an already owned type instance.
* Function unpacking can create a child GEP with no own hint. Flattening it into
  a replacement GEP inherits the first declared hint on its explicit base-GEP
  chain or pointer operand Use, stopping at a non-GEP value. It does not traverse
  PHIs, heap graphs or calls. A zero-to-base Use transfer still takes the GEP's
  own declaration, not a blind ancestor view that could project a field twice.

This preserves source-owned codegen geometry, not alias/effect constraints.
There is no target-wide structural search, field-name heuristic, runtime layout
identity guard, new Native2 field/opcode, or fallback optimizer/backend. Captures
and pending values retain the same actual storage and existing runtime paths.

### Zero-sized LLVM members and compact Native fields

LLVM's empty Null value remains `{}`. Native fields still reserve their existing
initialization storage, including a byte for Null. Consequently a Null member
and its Number successor can share LLVM offset zero while their compact Native
field offsets differ. Target field selection retains exact native type-index
lookup first, then matches an existing candidate at that **same known LLVM
offset** using `NativeTypeRelations.SameStorageRepresentation`. Distinct table
indices for the same Number carrier do not justify selecting the first Null
field, adding a narrowing check, or changing the physical carrier. The selected
type-index alias is cached for subsequent lookups.

The declared storage hint supplies the actual source layout for this selection;
the formal contract's field order is never substituted. Null value/storage ABI
and NativeLayout packing are unchanged. Detailed source/target telemetry counts
storage declarations, declared storage GEPs, zero storage views and actual
representation-field alias cache additions.

### Root validation receipts

All execution/builds below were performed by the root, not independently by the
implementation author:

| Focused actual path | Result | Receipt under `artifacts/diagnostics/llvm-instcombine/` |
| --- | --- | --- |
| Four-item generate, preserving folded index view and producing `0123`, direct + Native2 | PASS 3 properties | `hint-zero-sized-scope-fields.log` |
| Actual zip/enumerate Number members plus enumerated Function values returning `23`, direct + Native2 | PASS 6 executions | `parent-hint-builtin.log` |
| Distinct formal physical scope-domain declaration | PASS 3 properties | `hint-scope-layout-aliases.log` |
| Existing structured-range-elements String records, source-free object/bound workflow | PASS 3 routes | `hint-structured-ranges.log` |
| Full Range initialization/lifetime source, HIR-to-object and executable routes at O0/O2 | PASS 6 routes, 33 files per route | `range-hint-managed.json` |

The final parent-GEP-hint coherent build completed with zero warnings/errors
(`parent-hint-build.log`). These are correctness and representation-boundary
receipts, not an isolated speedup claim or acceptance of an intermediate AOT
benchmark image.

## Reuse whole Function loads across unpack/reassembly codegen

The pre-coalescer integrated profiles established a hot target-selection cost,
not new arithmetic work. Signal recorded Function compositions
65,552 → 262,164 and components 196,612 → 589,836, with unchanged 1,112,420 direct
operations. Unicode recorded compositions 180 → 195,180 and components
195,000 → 585,000. The work-count receipts are
`artifacts/diagnostics/llvm-instcombine/{signal-hysteresis,unicode-path-manifest}-work-counts.json`.
InstCombine had unpacked original Function loads into three scalar loads and
insertvalue reassembly. The target independently loaded the same compact Native
Function for each component, projected all three and recomposed it for the call.

`LlvmNativeLowerer.FunctionMemory.LoadGroups.cs` recognizes the actual same-block
load/insert chain: three simple component loads from the same declared Function
location, a poison/undef seed and insert indices 0/1/2. Only pure GEP/cast steps
may intervene; a different memory operation, call or control transfer prevents
matching. Intermediate insert results must have only their chain use. This is
local emission selection, not a new LLVM pass or a runtime value cache.

The first load emits one existing Native whole-Function Load. Components used
outside the reconstruction chain are still projected at their original emission
points. Existing FunctionFieldsUsed handling also supplies leaves when the final
aggregate feeds an insert/select/PHI consumer. The final AggregateSlots records
the already loaded BoundarySlot, so ordinary scalar ABI consumers reuse it
rather than emit ComposeFunction. Captured addresses, the original environment
object and pending NativeDatum remain the same; no snapshot, new runtime opcode,
wire field or alias analysis is introduced.

Target function metrics record matching candidates/groups, omitted components
and reused reconstructions; diagnostic rejection flags identify unmatched
member/reassembly/chain shapes. Group dictionaries are allocated only after a
successful match. This patch does not remove the real cost of running the new
LLVM-derived optimization passes: the root's pre-coalescer nine-pair observation
still reported +4.20 ms paired numeric compile cost and +33.70 ms midend compile
cost (105.19 → 138.87 ms p50). That work must not be attributed to this runtime
selection defect or claimed recovered without a subsequent comparison.

Root-managed executions passed independent output oracles for both affected
applications. Their actual post-group work counts are:

| Work | Signal | Unicode |
| --- | ---: | ---: |
| Native instructions | 12,737,434 (baseline 12,737,430) | 6,050,499 (baseline 6,420,873) |
| Function compositions | 65,552, exactly baseline | 180, exactly baseline |
| Function components | 196,612, exactly baseline | 195,000, exactly baseline |
| Ready exact-address projections retained | 196,612 | 195,000 |
| Static groups / omitted components / reused reconstructions | 7 / 14 / 7 | 3 / 6 / 3 |

The grouped managed observations are retained in
`artifacts/diagnostics/llvm-instcombine/{signal-hysteresis,unicode-path-manifest}-grouped-managed.sqlite`
and adjacent output/error files. The Function SSA checks passed their
7/16/16/4 groups and the builtin record fixture passed all 6 executions
(`load-groups-function-ssa.log`, `load-groups-builtin.log`); the coherent product
and CLI builds completed with zero warnings/errors. The instruction-count
reduction beyond restoration of Function overhead belongs to the integrated
optimized program, not an isolated attribution to this matcher.

These are output and actual-work receipts. Final AOT paired timing remains the
root's next step. The implementation author performed no independent build,
execution or timing run.
