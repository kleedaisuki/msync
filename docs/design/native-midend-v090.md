# Current directive: LLVM-based IR and source-faithful algorithms

The user selected LLVM IR as the primary representation, with heap allocation
and file IN/OUT expressed as first-class IR instructions. The only project opcode
extensions are `HeapAlloc`, `FileIn`, and `FileOut`; they are not ordinary calls
decorated with effect summaries, and do not introduce a file-handle API. This does **not** select
the LLVM backend, LLVM libraries as product dependencies, or LLVM's pass pipeline.
Optimization algorithms must be implementations from GCC/LLVM/javac, not locally
invented variants hidden behind upstream pass names. Source closures capture
locations, not definition-time value snapshots.

The former Native reconstruction plan below is superseded historical work.
The draft scalar, loop, aggregate and IPA algorithms failed source-provenance
review and are excluded from product compilation. Exact source is preserved in
`artifacts/diagnostics/native-midend-drafts/source/` with `QUARANTINE.json`.
The DirectCall/ImportCapture prototype and the private-slot-lowering experiment
have been retired as part of that reset; earlier delivered Range changes remain.
This restores a coherent transitional backend; it does not certify that old
Native algorithms already meet the new objective.

## Current authoritative source work

`vendor/llvm-reference/SOURCE.json` pins inspected upstream LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940` and exact source-file hashes. This
is a reproducible source snapshot, not an unrequested backend/release decision.
The upstream license is retained. Generated `Msync.Core.Llvm.LlvmOpcode` and
`LlvmTypeId` reproduce Instruction.def and Type.h identities, with the three
explicit project instructions in a distinct opcode range. Generators are typed Python development
infrastructure; product execution does not depend on them.

The source-aligned graph now has executable type/value/use, function/module,
instruction/constant, layout and lowering slices; their exact coverage is tracked
in [LLVM IR port status](llvm-ir-port-status.md). Default `Compiler.cs` and
`Session/SemanticSession.cs` callers now lower HIR to that graph, run the available
mem2reg → EarlyCSE → LoopSimplify → LCSSA → LICM → SCCP → DCE → late source
InstSimplifyPass sequence at nonzero levels,
and emit the existing Native target. O0 skips those graph passes. There is no
old optimizer/lowerer fallback. Heap and file IN/OUT stay explicit until target
lowering; no heap-proof subsystem, LLVM backend or complete LLVM O2 pipeline is
being inferred from this implementation.

The separate production CompilerSession receipt at
`artifacts/diagnostics/llvm-production-session/` passed O0/O2 with 12 submissions
per level (9 accepted and 3 reconciled failures). The actual Release CLI O0/O2
source/detached HIR-object/detached bound journey also passed, with 29 exact
output files per route and selected cast-failure checks on all three routes;
its zero-warning/error build and receipt are under
`artifacts/diagnostics/llvm-primary-cli/`. Linux x64 Native AOT publishing and
the same source/artifact/bound O0/O2 execution routes also passed; the binary is
`artifacts/native/llvm-primary/msync` and its evidence is under
`artifacts/diagnostics/llvm-primary-aot/`.
Those receipts precede the newer loop-pipeline checkpoint. The actual LICM
source journey subsequently passed nine cases/eighteen executions: invariant
arithmetic 16 → 2 with result 108, and callback/alias mutation results unchanged.
With direct HeapAlloc size/alignment/writability/freshness semantics, its shared
Scope-field example also produces one actual promotion exit store. Evidence:
`artifacts/diagnostics/llvm-licm-heap/`. The newer CLI source/object/bound journey
passed at both levels with 29 exact files per route:
`artifacts/diagnostics/llvm-licm-cli/`. Remaining IR and optimization-family
coverage and compile-time performance evaluation are still active work. These
executed-work reductions are not a wall-clock speed claim.

The updated Native AOT compiler is `artifacts/native/llvm-licm/msync`. Its O0/O2
source/detached-HIR-object/bound routes also passed, including all 29 exact file
outputs per route; evidence is `artifacts/diagnostics/llvm-licm-aot/`. This newer
loop-pipeline artifact is separate from the earlier `llvm-primary` checkpoint.

Source audits:

- [Scalar SSA/SCCP/redundancy](upstream-scalar-provenance.md)
- [Dominators, loop structure and LICM](upstream-loop-provenance.md)
- [Loop versioning and peeling](upstream-loop-transforms-provenance.md)
- [Aggregates and memory transformations](upstream-memory-provenance.md)
- [Interprocedural transformations](upstream-ipa-provenance.md)

All audits distinguish exact implementation provenance from naming resemblance.
The implementation objective is still active: neither audit notes nor generated
identity tables are evidence of a complete optimizer migration.

## Reset receipt

Release CLI build succeeded with zero warnings/errors; real Range initialization
source/HIR-object/bound O0/O2 journey passed against the reset image:
`.temp/test-execution/e49e3991e9714b44be7ffd34f3f484e7/results.sqlite`.
This receipt covers prototype removal and existing behavior, not yet LLVM-based
IR execution or new optimization benefits.

---

# Native mid-end reconstruction (v0.9)

## Objective and ownership

Active user objective: implement the applicable LLVM/GCC middle-end optimization
families comprehensively, and remove language-unnecessary optimization/analysis
machinery. This is not complete when one new loop pass works. The Native-2 ABI,
HIR4, shared-address semantics, source effects, resource/iterator lifetimes,
IEEE binary64 results and persistent-session contracts remain external contracts.
No portable/legacy optimizer is an execution or analysis fallback.

The current Native pipeline lost much of the transformation coverage described
in the historical [GCC mapping](gcc-optimization-map-v080.md) and
[stack CFG optimizer](optimizer-graph-v080.md). Those documents supply reusable
mechanisms, not evidence that their old passes run in the Native implementation.
The [O2 investigation](frontend-o2-diagnosis-v090.md) retains the actual Native
baseline and the address-analysis/storage coupling that must be removed.

The semantic analyzer owns language typing, capture modes and storage identity.
Lowering must preserve this explicit distinction: private value bindings are
Native slots; shared/captured/exported locations are addressable storage. Do not
first lower every value binding to a cell and then invoke whole-program analysis
to rediscover that it was private. A read of a mutable private binding still
produces a value copy so subsequent writes cannot change an earlier operand.

The mid-end owns transient function/CFG/SSA/loop representations and profitable
rewrites. Artifact admission owns structural validation. Runtime storage and
lifetime semantics must not depend on a general symbolic heap solver. Optional
analysis results are consumed by specific transformations and preserved or
invalidated at that transformation's actual scope.

## Implementation and completion matrix

Each row requires current producer code, sink-neutral causal telemetry, real
source/HIR/object/bound execution evidence and representative cost/benefit
observations. A row is not delivered by a class name or an analysis without its
consumer transformation. Whole-source compilation must improve without shifting
unbounded costs into another phase.

| Family | Intended implementation | Current status |
| --- | --- | --- |
| Value/storage representation | Lower private bindings directly to value slots; retain explicit shared locations and ABI roots | In progress |
| Execution storage contract | Derive mandatory lifetime/storage metadata from explicit IR ownership; detach optional escape/layout/borrowing proofs | Pending |
| Scalar SSA | Sparse def/use and phi representation, private-location promotion, copy/constant propagation, SCCP | Existing flat constant propagation only; reconstruction pending |
| Redundancy | Dominator-based value numbering/CSE and profitable partial redundancy elimination | Pending |
| Aggregates | Local scalar replacement, constructor/load/store forwarding, dead stores and temporary materialization elimination | Existing heavy memory passes; replace/generalize |
| Memory dependence | Sparse memory def/use with cheap identity/base-offset/effect queries; selective cross-function precision | Pending |
| Loop structure | Dominators, natural-loop forest, explicit preheaders/latches/exits and mutation-aware updates | In progress |
| Loop motion | Pure total LICM, memory LICM through actual dependencies, sinking | Pending |
| Loop control | Canonical induction/exit simplification, rotation, invariant unswitching, peeling and profitable finite/partial unrolling | Pending |
| Traversal/idioms | Bound known-iterator specialization, callback/traversal fusion and bulk copy/fill idioms | Pending |
| Numeric optimization | Exact Number-preserving simplification and recurrence/address reductions; no implicit fast-math | Pending |
| Interprocedural | Call graph/SCC scheduling, profitable inlining, constant-control specialization, dead arguments/results, tail-call reuse with explicit ownership | Existing inliner only; reconstruction pending |
| Final representation | Reachability/CFG cleanup, liveness and slot coloring, compact encoding | Existing; integrate with new facts/rewrites |
| Vector/bulk execution | Evaluate applicable elementwise/bulk operations against Native execution facilities and strict Number/effect contracts; implement supported profitable forms | Pending; no claim that pointer-chasing trees are SIMD loops |
| Unneeded machinery removal | Remove mandatory whole-heap solving, redundant validation on trusted transformations, disconnected proof-only passes and late reconstruction | Pending |

Coverage is by applicable semantic transformation, not a promise to copy every
LLVM target intrinsic or integer identity into this language. Unsupported
transformations must have concrete language/target reasons and cannot be silently
marked delivered. Baseline language operations keep their ordinary execution;
unknown optimization facts never reject otherwise valid source.

## First implementation tranche

1. Direct lowering of private bindings removes artificial FrameAddress/Load/Store
   traffic before optimization, using already-known capture/field/import/export
   ownership. Shared addresses remain unchanged.
2. Build local CFG dominators/natural loops, canonicalize entries and hoist
   invariant total operations without invoking interprocedural address analysis.
   This is the foundation for the full loop family, not the final loop coverage.
3. Exercise mutable operand snapshots, captures/aliases, zero-trip and nested
   loops, exact IEEE behavior and source-free artifact execution. Retain the
   B+/Heap inputs as scaling/control cases rather than a blanket regression run.

## Sources and engineering mechanisms

- [LLVM loop structure and canonical forms](https://llvm.org/docs/LoopTerminology.html):
  natural loops, preheaders, latches/exits and loop-closed SSA.
- [GCC loop representation](https://gcc.gnu.org/onlinedocs/gccint/Loop-representation.html):
  containment forest, maintained CFG/loop identities and loop mutation.
- [LLVM transformations](https://llvm.org/docs/Passes.html) and
  [GCC Tree SSA](https://gcc.gnu.org/onlinedocs/gccint/Tree-SSA.html): scalar,
  loop and interprocedural transformations over an explicit mid-end IR.
- [LLVM MemorySSA](https://llvm.org/docs/MemorySSA.html) and
  [analysis scheduling](https://llvm.org/docs/NewPassManager.html): sparse
  consumers and scope-correct reuse, not local requests triggering global solving.
- Braun et al., [simple SSA construction](https://pp.ipd.kit.edu/publication.php?id=braun13cc&lang=en):
  reuse the project's documented sparse definition lookup approach where useful.

## Validator: real Native mid-end journey

`tests/Msync.EndToEnd/native_midend_journey.py` exercises one source fixture via
O0/O2/O3 source execution, detached HIR-to-object execution, and an independently
bound executable. The original `.msync` is removed before compiling HIR; `.mhir`
and `.mso` are removed before bound execution. It checks 29 exact output files
per route. An explicit `--work` retains the source snapshot, disassembly and raw
compile/runtime traces; otherwise owned disposable work stays under
`.temp/native-midend/`.

The oracle covers mutable operand snapshots; private argument copies; conditional
joins and repeated expressions; zero-trip, nested, finite and invariant-branch
loops; fresh per-iteration captured cells; local aggregate fields versus escaping
aliases; ordinary tail-recursive arithmetic; preservation of argument and ignored
result side effects; IEEE operation order, signed-zero phi joins and a NaN
parameter compared with itself; a potentially failing cast in an unentered loop;
and callback order/early traversal exit. Iterator-close opcode hits are retained
as execution evidence, but the test does not require an optimized-away iterator
to execute an unnecessary close instruction.

Frozen baseline `artifacts/native/range-evaluation/msync` passes all nine routes.
Receipt `.temp/native-midend/baseline-final-receipt.json` identifies binary/source
hashes. Native dynamic instruction attempts are O0 3,816, O2 2,399, O3 2,620;
logical allocation attempts are 105 at each level; the baseline records nine
CloseIterator hits. These are deterministic mechanism observations for this
fixture, not latency or general runtime speedup claims.

The kernel's `scale`/`shift` come from runtime file-existence-controlled values,
so source inlining/constant folding cannot masquerade as loop motion. At the
source expression `scale * scale + shift`, all three old optimization levels
execute 16 Binary instructions across eight iterations. The optional
`--require-kernel-improvement` assertion compares candidate O2/O3 against O0 using
actual `vm.instruction` source spans. It is intentionally stronger than counting
hoisted copies and remains opt-in until arithmetic motion is implemented.

`--require-metric NAME` requires a positive actual rewrite counter across O2/O3;
callers should select rewrite counters (for example `loops.invariants.hoisted`
or `scalar.gvn.eliminated`), not an analysis node census. `--require-improvement`
requires fewer total dynamic Native instruction attempts than O0. Neither
assertion establishes the entire optimization-family matrix by itself.
A focused runner plan is `.temp/native-midend/behavior-plan.json`; the root owner
can add the relevant rewrite metric assertions at integration time.

The journey also compiles a selected failing cast successfully, then checks its
runtime error and original source identity through source, detached object and
bound execution. Its dependent transfer must not publish a result. This pairs
with the successful zero-trip cast case: neither moving a potentially failing
cast into an unentered loop nor deleting an executed failure is accepted.
The frozen old binary demonstrably fails `--require-kernel-improvement` after
passing its behavior routes: O2 and O0 each record 16 invariant Binary hits.
Thus the arithmetic-motion discriminator detects the previously missing loop
optimization rather than merely accepting an existing small-code cleanup.

## Storage contract implementation (storage_contract worker)

`NativeStorageFacts` now separates activation-local storage ownership from the
old general `NativeAddressAnalysis` domain. Its roots are explicit FrameAddress
instructions, not every Scope/allocation/callable in the program. Already-owned
shared slots and location captures are not current-frame roots. If no unowned
frame root exists, no address graph or propagation work is constructed. Otherwise
sparse value/binding edges propagate only origin frame slots through explicit
copies/conversions/address operations. An address published to a call, closure,
container, another location or a return receives independent location ownership;
ordinary scalar loads/stores do not imply that their address escapes. Local wide
frame access retains FrameAlias metadata and excludes incompatible borrowing or
private-slot promises. Scope allocations keep their ordinary logical-heap
ownership unless a real optimization supplies a placement result.

`NativeStoragePlanning.Run` and the three-argument
`Capture(program, function, trace)` use these local facts; they do not instantiate
ProgramFacts, NativeEscapeAnalysis or a callee heap. Readonly parameter and private
argument flags are derived from actual definitions/address exposure. DirectCall
alone supplies an authoritative callee for argument borrowing. A generic Call's
B hint is not a readonly ABI promise. The unpublished ImportCapture prototype
was removed when the user selected primary LLVM IR. The compiled Native-2 Storage/Calls records remain the
runtime authority; no loader or VM reconstruction path was introduced.

The old four-argument Capture taking NativeEscapeAnalysis remains temporarily
available only to existing optimization consumers while the root replaces that
pipeline. Its removal/optional use must not be confused with the now-local basic
storage path. The public NativeAllocationSafety helper has no current execution
consumer. A source audit found no general address/escape solver call in Runtime:
NativeImage consumes the supplied Storage plan directly. Explicit artifact
admission still has NativeArtifact.Pointers.ValidatePointerUses, which requests
physical-layout/address facts at that boundary; this is not per-execution
reconstruction.

Telemetry: `compiler.storage.function` owns `compiler.storage.local_facts`, with
function identity, slot/read-only/private/escaping/frame-alias counts and actual
address-edge/transfer counts. `compiler.storage.calls` retains snapshot/borrowing
and unobserved-initialization outcomes. Value-only functions report zero address
transfers rather than an empty global proof. Focused local contracts are in
`tests/NativeOptimization/StorageContractChecks.cs`; root integrates the command
and real-process source/HIR/object/bound journeys.

Verification so far: Core/Optimization Release build succeeded before the final
fast-path/direct-op additions. Subsequent ownership-run attempts were stopped by
concurrent worker compilation errors in their SourceSpan properties, not storage
failures. Final build and journey evidence must come from the root's serialized
integration image, not these intermediate attempts.

The selected-error pairing is now confirmed on the frozen baseline at O0/O2/O3,
including all three source/detached-object/bound paths. Its receipts are
`.temp/native-midend/baseline-failure-receipt.json` and
`baseline-failure-optimized-receipt.json`. This is ordinary language execution
coverage: source compilation succeeds, only entered conversion execution fails,
and an output dependent on that failed value is not published. It introduces no
product validation, permission protocol or additional optimization prerequisite.

## Storage algorithm provenance audit after the primary-LLVM-IR decision

This audit supersedes any implication that `NativeStorageFacts` is an LLVM/GCC
algorithm port. The user now requires primary LLVM IR, not an LLVM backend or
pipeline, and only specifically identified upstream algorithms. No algorithm or
replacement architecture was added during this audit. The unpublished
ImportCapture prototype and its storage/test support were removed; DirectCall
remains the explicit fixed-target call contract. No build was run during this
cleanup, per the root's serialized-build instruction.

Pinned primary sources examined:

- LLVM **llvmorg-21.1.0**, [`CaptureTracking.cpp`](https://github.com/llvm/llvm-project/blob/llvmorg-21.1.0/llvm/lib/Analysis/CaptureTracking.cpp),
  `PointerMayBeCaptured` and `DetermineUseCaptureKind`.
- LLVM **llvmorg-21.1.0**, [`FunctionAttrs.cpp`](https://github.com/llvm/llvm-project/blob/llvmorg-21.1.0/llvm/lib/Transforms/IPO/FunctionAttrs.cpp),
  `determinePointerAccessAttrs` and `addArgumentAttrs`.
- GCC **releases/gcc-15.2.0**, [`tree-ssa-structalias.cc`](https://github.com/gcc-mirror/gcc/blob/releases/gcc-15.2.0/gcc/tree-ssa-structalias.cc),
  the ADDRESSOF/SCALAR/DEREF constraint model, graph construction and `solve_graph`.
- OpenJDK **jdk-25-ga**, [`Gen.java`](https://github.com/openjdk/jdk/blob/jdk-25-ga/src/jdk.compiler/share/classes/com/sun/tools/javac/jvm/Gen.java),
  `visitVarDef`, `visitIdent` and local-item generation.

| Existing local implementation | Exact correspondence found? | Material difference / status |
| --- | --- | --- |
| `NativeStorageFacts.Build` value-slot/binding-slot graph | No | A custom Native slot graph with selected copy/address edges. LLVM walks SSA uses; GCC constructs its full constraint language and solver graph. Sharing a worklist idea is not a port. |
| `Propagate` unioning frame-root HashSets | No | Generic monotone set propagation, not LLVM capture-use traversal or GCC's constraint solver. It has no identified upstream function-level algorithm correspondence. |
| `ExportLocations` hardcoded publishing operations | Only an individual rule resembles LLVM's stored-pointer capture rule | The whole export policy is custom. LLVM distinguishes capture components, return capture policy and call attributes; this code unions Native slot origins across its own operation list. Do not call it LLVM capture tracking. |
| `ReadOnly` parameter-cell mutation scan | No | It asks whether a mutable Native parameter binding is overwritten/exposed. LLVM's pointer access attributes describe memory read/written through an SSA pointer argument, a different property. This is a custom ABI promise, not a FunctionAttrs port. |
| `PrivateSlots` and `Classify` frame-alias classification | No | Native temporary/exposure policy and runtime fast-path metadata. No matching LLVM, GCC or javac algorithm was identified. |
| `NativeStoragePlanning` snapshot/borrowing records | No | A custom Native calling/storage convention, not an upstream optimization algorithm. Its existence must not count as algorithm coverage. |
| `javac` local-variable lowering | Not a storage-analysis counterpart | Gen emits local load/store operations through local items; it supplies no correspondence for the Native frame-root solver. |

LLVM capture tracking in the pinned source visits pointer uses and classifies
capture effects; its return-policy/call-attribute handling is part of that
algorithm. GCC's pinned solver builds pointer constraints including indirection
and offsets, then solves its constraint graph. Neither is the current
frame-origin graph with renamed classes. The appropriate audit result is
**unmatched custom behavior**, not "similar to LLVM" or an attributed port.

The mandatory local storage path still constructs `NativeStorageFacts` and can
run its frame-root propagation when an unowned FrameAddress exists. It removed
the *general* interprocedural heap solver from that path, but did **not** remove
all mandatory custom ownership inference. The four-argument
`Capture(..., NativeEscapeAnalysis, ...)` also remains in the compatibility
optimizer path. These are unfinished migration facts for the primary-LLVM-IR
owner; this cleanup does not claim that the new goal has been met.

## Rejected Native call/capture opcode prototype cleanup

The unaccepted `ImportCapture` and `DirectCall` Native opcode additions have been
removed from product Core schemas/facts/admission, HIR emission, runtime frame,
preparation, execution and tiering support. No replacement opcode or capture mode
was introduced. Existing generic `Call` retains its NativeName-first descriptor
behavior and its historical B-hint/session dispatch semantics. Existing opcode
values, including the earlier Range retirement gaps, are unchanged.

The removed prototype was not a port of an LLVM/GCC/javac operation implementation
and had no delivered artifact or ImportCapture producer. A scoped working diff
was retained in
`artifacts/diagnostics/native-midend-drafts/direct-call-scoped-working-diff.patch`
before cleanup. That snapshot includes earlier Range migration hunks in shared
files and is provenance evidence, not a file-restoration patch. Draft fixture
references were removed or quarantined with the corresponding draft algorithms.
No build or runtime validation is claimed for these rejected operations; the
integrating owner serializes the restored product build.

### Direct HIR producer checkpoint (2026-11-15)

`Msync.Hir.LlvmHirLowerer` now produces the LLVM-derived object graph directly,
without Native opcode translation, an interpreter, or CLI selection. The initial
mapping covers binary64/Bool/Null scalars, local location loads/stores, blocks,
conditionals and short-circuit boolean control flow, anonymous scope allocation
and concrete struct field addresses, and known `io.read_*`, text/byte write and
append builtins plus transfer. Private local allocas are placed in the entry
block; scope allocations use the explicit HeapAlloc extension and DataLayout
size/alignment. Transfer passes its original source to FileOut: Path transfer
is not rewritten as eager read-bytes.

The mapping preserves existing runtime payloads: String is a managed-string
object pointer; Path is `{i8, i8, ptr, ptr, ptr, ptr, ptr}` for its two state
bytes and five reference fields; Range is the existing runtime range reference.
Static String/Path operands are typed loads from external constant globals.
`LlvmHirLoweringResult.Constants` owns the existing NativeConstant relocations,
including source-origin information; target binding of these globals remains
pending. This is not a new tagged-value ABI or executable runtime implementation.

The producer attaches instruction Source spans directly; it does not retain a
second strong instruction-to-source map after rewrites. Existing sink-neutral CompilationTrace records function work,
blocks, locations and optional detailed opcode counts. Unsupported HIR has an
explicit source-owned exception, not a generic call or Native fallback. Closure
and dynamic-call ABI, singleton initialization, multi-layout projections,
nullable/union representation, iterators and match remain unmapped. The producer
was not connected to the primary CLI at this checkpoint; the later default caller
switch is recorded in the current overview. This checkpoint is not an execution
or optimization performance claim.

The same checkpoint now also maps NativeFunction exports with their existing
`{i32 Function, ptr Environment, ptr NativeName}` payload, using -1/null/name
for native functions. Imported namespace scopes therefore need no special dead
initializer removal. PathJoin is an ordinary typed external call, with an
explicit `RuntimeCalls` binding to the existing NativeBinary.PathJoin semantic
operation. This is pending target binding, not an opaque HIR evaluation hook.

Parent-serialized validation built `tests/LlvmIr/Msync.LlvmIr.Tests.csproj`
with zero warnings/errors and passed real `Compiler.Analyze` source-to-graph
journeys (`.temp/llvm-ir/journey-build.log`, `journey-run.log`). The receipt
reported one scope HeapAlloc and five field-address operations; all three FileIn
formats and all five FileOut modes were present. The Path-transfer graph check
kept a Path operand on FileOut without an inserted FileIn. These are producer
and graph-structure journeys, not execution integration (`executionIntegration:
false`); no claim about O2 speed or runtime execution follows from this receipt.

### Source loop CFG producer checkpoint (2026-11-15)

`LlvmHirLowerer.Control.cs` maps HIR Loop and Break to ordinary LLVM-derived
basic blocks, unconditional branches, and exit PHIs. Each source loop owns its
header and exit, while each Break selects the matching HirLoopId and records
its predecessor/value. A loop without a reachable break has an unreachable exit;
an inner loop that exits an outer loop therefore does not manufacture an inner
fallthrough. Mutable source locations remain ordinary entry allocas for later
upstream mem2reg. Function telemetry includes loop count, break count and maximum
lexical loop depth, in addition to emitted opcode counts and Source attachments.

For protocol work must preserve the actual existing iterator contract:
NativeInterpreter.Aggregates opens an IAsyncEnumerator<NativeDatum>, advances it
asynchronously, and binds Current without resolving its potentially pending
datum. Normal exhaustion, source break and source return dispose the relevant
owned iterators in reverse order; activation unwinding also disposes on errors.
A typed `next -> {i1, scalar}` ABI must not silently add early datum resolution.
This item-demand boundary remains a concrete coordination item before For is
implemented; no generic evaluation instruction or extra project opcode is used.

The agreed For protocol is now produced as ordinary typed external calls with
explicit `IteratorCalls` bindings: Open(range) returns an internal capability;
Next(capability, itemCell) returns i1 and writes the original datum to the typed
item cell; Close(capability) returns Null. The capability maps to an existing
activation iterator ID in the Native target, not a new source kind or ordinary
user-value carrier. This avoids an eager Current resolve. For CFG has a header,
body, exhaustion cleanup and exit; break/return emit reverse-order closes for
the iterators they leave. Capturing source bodies are still explicitly unmapped,
so this checkpoint does not silently reuse a captured per-iteration cell.

Producer-owned target ABI facts are direct annotations rather than value-keyed
side tables: `LlvmValue.LogicalType`, alloca `AllocatedLogicalType`, HeapAlloc
`LogicalLayout`, plus result Entry/Types/Layouts/StructLayouts. Opaque LLVM ptr
therefore retains String/Scope/Range/address meaning without a second inference
pass. Empty scope allocations request at least one byte to preserve distinct
allocation identity; this does not change generic HeapAlloc semantics.

### Native target execution journey and demand boundary (2026-11-15)

`tests/LlvmIr/OptimizedExecutionJourney.cs` exercises fresh source analysis and
LLVM-derived graphs through `LlvmNativeLowerer` into `NativeInterpreter`, both
without passes and with an explicit mem2reg/DCE sequence. The fixture covers a
loop-carried summation with branch updates (17), shared Scope mutation (6),
actual text/byte/line files, append, iterator exhaustion/early break and Path
transfer. Optimized scalar and Scope graphs also traverse Native2 serialization
before detached execution; target-provided backend/storage must survive. This
is the existing Native target adapter, not an upstream LLVM execution backend
or a claim of complete language migration or O2 performance improvement.

The first integrated run passed the unoptimized scalar, shared-Scope and actual
IO cases, then exposed a fixture mistake: constructing and discarding
`read_text(absent)` does not demand its pending datum. `ExecuteEntry` demands the
returned value, while `SubmitFileRead` schedules a future. The failure journey
therefore consumes the read in a dependent write, expects the concrete missing
filename and source span, and checks that no dependent output is published.
Do not add eager resolution to product code to satisfy a discarded-read test.
Build/run ownership remains with the parent-serialized integration process;
this checkpoint does not preempt the final raw/optimized receipt.

The parent-serialized final integration build and run passed
(`.temp/llvm-ir/integrated-build.log`, `integrated-run.log`): zero build warnings
or errors; eight successful executions including two Native2-decoded programs;
five removed allocas across optimized scalar/Scope fixtures; three PHIs in the
actual loop; ten exact file-output comparisons; and two correctly attributed
consumed missing-file failures. The same run retained the earlier real-source
DCE checks (one discarded arithmetic removal, two unused lazy-reader removals,
retained Text reader and all five output modes). These are functional and
actual-transform results, not measured runtime-speed claims.

### Source functions and location closures producer checkpoint (2026-11-15)

`LlvmHirLowerer.Calls.cs` reserves ordinary source LLVM functions with captured
location pointers first and typed user parameters afterward. Its module-local
pending-body list permits recursive source functions without lowering HIR twice.
SourceBinder.Functions creates only Location captures; Value captures observed
in ReturnProjection.CallableShapeValue are semantic shape projections, not a
source snapshot-capture feature. A capture uses its actual SourceLocation (or
an explicit ReadLocation source), never a newly evaluated value snapshot.

A captured local/parameter uses an ordinary one-field heap cell by default,
without alias/escape inference. Field captures retain their constructor GEP.
Closure environments are HeapAlloc structs of those existing cell pointers;
copying a closure therefore retains the same locations. A captured For item
uses a scratch cell for Next and allocates its fresh captured cell only on the
successful iteration edge, preserving per-iteration closure identity without
allocating an extra captured cell at exhaustion.

The LLVM-side Function descriptor is `{ptr code, ptr environment, ptr nativeName}`.
This is an internal source IR representation: the Native target maps code symbols
to the established VM function index and keeps Native2's existing payload ABI.
TemplateClosure code points to a distinct declaration used only for template
callable identity. `TemplateFunctions` records its existing placeholder
signature; no single specialized body is substituted for a generic identity.
`SourceFunctions` records the capture prefix and logical result type.

A static source call first evaluates the actual descriptor and all user
arguments, loads capture addresses from that descriptor's environment, then
calls the selected specialized LLVM function. The standard operand bundle
`"msync.callee"(descriptor)` retains the actual descriptor as an SSA use for the
Native target call ABI. Dynamic invocation and callable adaptation are ordinary
typed runtime calls with explicit module-local `CallableCalls` bindings and
identity-to-specialization dispatch tables; they map to the existing Native
Call/AdaptFunction operations, not a generic HIR evaluator. Session dispatch and
process invocation remain explicitly unmapped in this producer checkpoint.

### Callable/ordinary-Range execution fixture (2026-11-15)

`tests/LlvmIr/CallableObjectJourney.cs` extends the actual graph-to-Native path
with source-level typed/dynamic calls, escaped mutable and transitive locations,
recursion, a template specialized to Number and Bool while preserving alias
identity, generic native callbacks, and ordinary Range objects. Range fixtures
check eager left-to-right construction effects, Function elements not invoked by
iteration, per-iteration closure cells and shared Scope element aliases. Selected
optimized programs traverse Native2 serialization before execution. A companion
operand-bundle property keeps the `msync.callee` input in the normal Use graph:
clone owns separate uses, RAUW updates views, and DCE retains a definition used
only by the bundle. These are executable acceptance fixtures; their addition
alone does not establish the current feature tranche passes or improves speed.

### Main-loop callable write-loss diagnosis (2026-11-15)

The real per-iteration closure journey exposed a pre-existing frontend bug,
not an LLVM producer alias issue. A main-level For that assigns three escaping
closures produced 0 instead of 321 in both the frozen old Native baseline and
the new unoptimized source-to-LLVM path. The typed HIR already replaced the
post-loop calls with initial zero-returning closure bodies; iteration stores
were present but their callable facts had disappeared before lowering.

The root SemanticContext had WriteEvents == null. For/Loop forked that null
state; Assign.RecordWrite therefore recorded nothing. MergeLoopExits replayed
only exit event sequences and retained existing Values through TryAdd, leaving
the three pre-loop callable facts unchanged. Function instantiations happened
to start with an active event log, explaining why escaped and transitive
function-local captures passed while main-level iteration assignments failed.

For and Loop now activate the existing write-event representation at their
flow boundary, before forking the loop body. For performs this after evaluating
its producer and applying producer effects, so the entry-prefix boundary stays
correct. Already active logs are unchanged; this is not global logging or a new
flow/alias solver. Detailed telemetry records actual activation under
`compiler.semantic.loop_write_events` with source context and initial event
count. The expected iteration result remains 321; unified validation is pending.
Reproduction material is under `.temp/llvm-ir/iteration-capture/` and the durable
callable-object journey source under `tests/LlvmIr/CallableObjectJourney.cs`.

Callable tranche final integration passed (`.temp/llvm-ir/callable-build.log`,
`callable-run.log`; artifacts `artifacts/diagnostics/llvm-ir-callable-objects`):
12 source cases, 28 executions including four Native2-decoded programs, alongside
the previous eight executions and ten exact IO outputs. Function equality was
not added to the language: the generic case invokes an alias with Number and
the original template with Bool, checks both results, then checks one shared
template/code-pointer declaration and Native template placeholder in the actual
lowered output. The source checker correctly rejects Function `==`.

The per-iteration closure case initially returned 0, including a frozen old
Native O0 diagnostic. Its expected 321 was retained. The frontend owner found
For/Loop forks had no write-event log, so loop-exit merging retained the initial
zero-returning lambda facts. Activating the existing event mechanism before
forking fixed the main-source result; the current typed HIR now loads the three
mutated callable locations before their final calls. The old-path comparison is
an isolated diagnostic flag, never a fallback inside new-target acceptance.

Parent-serialized callable validation subsequently passed all 12 real source
cases and 28 executions, including four detached Native2 artifact executions.
The iteration-location expectation remained 321 and passed after the frontend
loop write-event activation fix. Escaping mutable captures, transitive captures,
recursive source calls, template specialization/alias identity, dynamic calls,
Range construction order, and Range element reference identity were covered.
This receipt does not imply that the new producer is already the primary CLI.

### Singleton globals and physical member access (2026-11-15)

`LlvmHirLowerer.Statics.cs` now produces named Scope storage as a module global
of the concrete object struct plus an i1 initialization flag. Both globals are
registered by the existing HIR allocation-site identity in `StaticScopes`.
Field addresses and the lexical self location refer to the canonical global
object on both paths. An acquire flag load selects reuse versus initialization;
the initialization path evaluates source initializers and release-publishes the
flag. No HeapAlloc substitutes for the static object, and no field-wide admission
or heap analysis is introduced. The Native adapter maps these ordinary accesses
to the established NativeProgram.Statics/TestStatic/StaticAddress/PublishStatic
contract and preserves per-interpreter/session ownership.

Member lowering uses the actual finite ScopeDomain, not the Shape that may only
be a structural contract. An exact physical domain emits direct GEP/load/store.
Only a genuinely multi-layout access emits ordinary CFG and the typed HasLayout
runtime primitive (existing NativeUnary.HasLayout compares layout identity).
It needs K-1 tests for K admitted layouts, not an extra admission check on the
last case. Read values are represented to the source result type and joined by
an ordinary PHI; writes use each selected field's actual storage representation.
Process member reads route to the existing process.* builtins. A receiver with
no physical layout domain remains an explicit coverage gap rather than silently
using a contract's offsets.

Detailed source-owned compiler phases are `compiler.llvm.lower.static_scope`
and `compiler.llvm.lower.member`, with binding/initializer/layout-decision
counts. Per-function telemetry records static access/initializer paths, direct
and dispatched members, physical layout tests, and process field accesses.
Unified source/Native2 validation for this singleton/member checkpoint is pending.

The structural-parameter journey exercised the no-domain case with two real
object layouts whose `value` field has different physical offsets. HIR PlanType
intentionally removes reaching layout alternatives to share code across
structural inputs, so treating an empty domain as a frontend error or restoring
per-layout specialization would change that design.

For such unresolved accesses, lowering now builds a lazy closed-module object
ABI catalogue by visiting actual `hir.Functions` bodies once, including Match
arms. It registers only executable Scope/AllocateScope constructors, never raw
template schemas merely present in the Scopes table. The catalogue maps each
member name to concrete layout/ordinal pairs; a per-name/storage-kind cache
selects the relevant physical field mappings. This is object ABI declaration
metadata, not whole-row admission, heap inference, or an alias solver. Known
physical domains still bypass this catalogue and retain direct GEP or their
precise finite dispatch. The unknown-domain access uses the same ordinary
HasLayout CFG. Unknown external object layouts without a declaration in this
module remain outside this closed-module producer coverage.

Catalogue telemetry is `compiler.llvm.lower.member_catalogue` with visited node,
physical layout, and field counts; access telemetry includes the selected
candidate count. The actual structural fixture keeps expected result 45 and
checks Native2 roundtrip rather than weakening the expectation or reverting to
contract-shape offsets.

### Explicit source-session acceptance harness (2026-11-15)

`tests/LlvmSession/LlvmSessionJourney.cs` is an isolated friend-harness project
(`dotnet run --project tests/LlvmSession -- --output <repository-path>`), not a change to default
Compiler/SemanticSession callers. It reuses SourceBinder, SemanticAnalyzer,
accepted facts/dispatch hydration and NativeExecutionSession, replacing only the
selected test lowering path with LlvmHirLoweringSession.Lower/Emit and optionally
explicit mem2reg/DCE. Fifteen source submissions per fresh mode exercise root
mutations, Scope aliases, escaped captures, deferred native callback replacement,
shadowed declarations and generic aliases. A test-owned shell gate makes stdout
a demonstrably pending initialized root; another submission retains a captured
alias before the gate is released and the result is consumed into an exact file.
The test releases its child on failure. Source/HIR/IR/results remain under the
selected output path. Addition of the harness is not an execution-pass receipt.

Type/operation tranche passed the parent-serialized integrated run
(`.temp/llvm-ir/cohort-build.log`, `types-run.log`): 14 actual source cases,
32 executions including four Native2-decoded programs, two concrete conversion
errors with source context, and six exact file-output comparisons. This includes
nullable locals/returns/captures/fields, Number/String and Range/String unions,
Type denotations, Connection conversion without authentication, heterogeneous
regex/Path match, nullable signed-zero/NaN, named Scope reuse and two physical
Scope layouts with reversed field order passed through a structural Scope
parameter (result 45). Actual local-process stdout, acquire/release, file
selection and acquire regions produced their expected output files. The earlier
nullable-return and structural physical-domain failures were fixed in product
code without reducing the fixture expectations.

The source-session harness was moved into its own `tests/LlvmSession` executable
with Compiler friend access. Building the pre-existing HirBinding project would
also compile unrelated historical checks against deleted legacy types; those
checks were left unchanged instead of being repaired as a side quest. Default
Compiler/SemanticSession callers were unchanged at that harness checkpoint; the
subsequent default caller migration and production session receipt are recorded
in the current overview.

### Native builtin record ABI: actual primary CLI correction (2026-11-15)

The default CLI journey first reached `std::ranges::generate` without a physical
member declaration. Registering its semantic result Shape alone exposed the
more precise ABI defect: semantic `Record` rows come from an immutable name map,
whose enumeration order is not the runtime record's physical field order. A
focused real CLI probe of `enumerate([7,8,9])::index` produced `789`, not `012`;
`generate` read its Null value field as index 0, and the early-exit oracle was
55 instead of the established 21. HIR retained the correct runtime equality and
Break; this was neither a loop optimizer nor an input-validation problem.

Member catalogue entries now carry explicit LLVM struct, target layout, field
ordinal and field-storage type. Source Scope constructors retain their own
physical rows. Native record factories declare the canonical schemas from
Core.NativeBuiltinLayouts: enumerate/zip, environment variables, process table,
regex matches/groups and meta signature/member records. Semantic Shape field
order is never used as a native factory ABI. Enumerate/zip declare physical kind
carrier variants because an ordinary Range view does not repack its producer's
physical item type. This is module object ABI metadata, not a new Range opcode,
thunk, heap analysis, source capture mode or admission scan.

The source file and focused probe are retained under
`artifacts/diagnostics/llvm-primary-cli/journey/O0/midend.msync` and
`.temp/llvm-ir/index-probe/`. The unified actual CLI O0/O2 source/artifact/bound
rerun passed after this correction, including the unchanged early-exit result
21. `artifacts/diagnostics/llvm-primary-cli/receipt.json` records the exact binary,
source and managed assembly hashes. This does not establish AOT or full LLVM O2
coverage, and no performance improvement is inferred from the ABI repair.
