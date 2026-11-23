# HPIR producer, operation and lifecycle inventory

2026-11-22 · Native format 2 at `9bd388d8` and the packaged Linux x64
0.9.3. This is the migration inventory for the [HPIR roadmap](hpir-roadmap.md).
The [representation audit](xir-representation-audit.md) explains the causal
performance issues; this note identifies the existing interfaces to replace
and the obligations their replacements must cover.

## Where facts cross the detached boundary

```text
HirModule
  -> LlvmHirLoweringResult + optimized LlvmModule and producer contracts
  -> LlvmNativeLowerer -> relocatable NativeProgram -> Native-2 .mso
  -> NativePreparation [contextual interpreter, optional guest inliner]
  -> NativeBackendCompiler [slot backend, work, windows, connections]
  -> executable NativeProgram -> Native-2 .mexe
  -> NativeImage / NativeExecutionFunction / NativeInterpreter and its tiers
```

[Compiler.LowerInstructions](../../src/Msync.Compiler/Compiler.cs)
still has the graph at target selection. Detached
[`bind-ir`](../../src/Msync.Cli/Commands/NativeCommandPipeline.cs) has only
the decoded object. [Native-2 compact sections](../../src/Msync.Core/Artifacts/NativeArtifact.Compact.cs)
carry a mandatory slot instruction stream, types, layouts, projection cases,
static and allocation identities, storage/call plans, constants, and
source spans; prepared images also carry plans and a PC-aligned backend
instruction stream. Do not mistake optional `WholeSsa` for a portable copy
of the graph: [its selector](../../src/Msync.Hir/Lowering/LlvmSsaBody.cs)
accepts only a ready Bool/Number subset, excludes used captures, has size
limits, and [callee admission](../../src/Msync.Core/NativeModel/NativeSsaBody.cs)
excludes calls and backedges. It is a tier-specific encoding alongside code,
not a suitable source for HPIR.

| Fact | Current producer and detached evidence | HPIR ownership |
| --- | --- | --- |
| Object/field representation | [LLVM metadata](../../src/Msync.Hir/Lowering/LlvmNativeMetadata.cs) and [GEP lowering](../../src/Msync.Hir/Lowering/LlvmNativeLowerer.PointerArithmetic.cs) own constructor layouts and storage views; compact layouts retain ordered field types, `AddressOffset` retains exact layout/field IDs, projections retain finite case pairs. A scope type's admitted layout domain is not instance provenance. The old wire lacks the original SSA value/use relation and final offsets. | Select exact physical meanings once from the producer; carry offsets or relocations against a fixed layout table. Keep a guard only at actual nullable/alternative-layout, open structural admission or reflective access. Do not infer the meaning of an opaque pointer from its numeric contents. |
| Values and places | Optimized LLVM SSA, allocations, use annotations and PHI edges precede lowering. Compact code has slots, aliasable locations, copies and serialized plans tied to PCs, not def identity or source LLVM edges. | Compute value placement, edge moves and frame extent before writing physical code. Emit real addressable cells only for materialized storage or ABI. Preserve read snapshots across mutation and close/unwind on correct edges. |
| Call, environment and result | [LLVM calls](../../src/Msync.Hir/Lowering/LlvmHirLowerer.Calls.cs) retain typed arguments/captures and the evaluated callee in an operand bundle; compact code stores parameter/result/capture roles and modes, fixed or finite dispatch, and call storage flags. It does not directly encode a fixed code + evaluated environment ABI. A separate reflected signature can be less specific than the physical call. | Fix outgoing/incoming offsets, result area and environment layout once, with identity-independent fixed code entries. Supply explicit finite/late selection where it exists, and keep reflection tables off ordinary CALL/RET. At entry/host interop, admit external values only at that boundary. |
| Readiness and Range | [Builtin signatures](../../src/Msync.Core/Native/NativeSignature.cs) give result rules, callback relation, effects, path access and deferred behavior. `CallNative` can carry logical item metadata, and LLVM has explicit demand sites, but ordinary Number does not distinguish ready result from a lifted operation in the artifact. General Range instructions carry item information, not every known producer recipe. | After correcting the upstream readiness/identity contract, choose a ready carrier or explicit typed completion/union; transfer producer facts to cursor/consumer selection. Carry actual demand, lazy callback and lifetime distinctions, not a Boolean permission to reclassify a generic operation. |
| Work, roots and unwind | [Backend compilation](../../src/Msync.Runtime/NativeModel/NativeBackendCompiler.cs) computes work scope, unread parameters/call windows and physical choices using the lowered code. The old runtime [traces stack frames, descriptors and host roots](../../src/Msync.Runtime/NativeModel/NativeInterpreter.GuestHeap.cs) and relies on activation state for cleanup. | The final physical emitter owns live root locations, call extent/continuation, and exceptional cleanup obligations. Classify work at its actual launch/retain/join point; do not encode a managed activation on every call. Host work must publish its own roots if live after a frame exits. |
| Context and effects | [Preparation](../../src/Msync.Runtime/NativeModel/NativePreparation.cs) interprets a selected acyclic known prefix in a `NativeFrame`, may evaluate known calls, and residualizes on opaque work. [Transfer preparation](../../src/Msync.Runtime/NativeModel/NativePreparation.Transfers.cs) observes metadata and writes captured values, plan dependencies and result references, but does not publish input bytes. | Producer supplies a contextual-effect slice/recipe: operation, input dependencies, observer/result use, boundaries and source. Bind may observe context or metadata and select prepared/residual calls; it must not reconstruct a general optimizer. Prepared images consume linked plans. For incomplete footprints, effects remain live with the existing resource/transport scheduler. |
| Diagnostics, relocation and compatibility | Compact debug data has file/offset/length and PC mapping; layouts, entries, resources, plans and constants are indexed. The CLI accepts both stages, HIR lowering, binary stdin and disassembly without executing a program. | Retain site IDs, source and function names, distinguished result/failure paths and relative references. Plumb all entry points to the same physical artifact contract. Reject unsupported wire versions with a recompile diagnostic; do not re-create execution from metadata. |

Prefer a relocatable physical program plus **portable-only** contextual
recipes, with the same guest ABI and a disjoint prepared section set. This
lets source and detached binding share code selection and leaves no duplicate
executable stream. It is a proposed wire shape, not a claim that the effect
slice is already implemented. Keep the prepared program's offsets, helper
contracts, frame and root maps outside of the old `NativeProgram` slot model.
Re-labeling generic code, retaining `WholeSsa` as the normal/polymorphic split,
or deriving new root maps in the loader all preserve the previous architectural
failure in a different file. Ordinary relocation is not repeated proof.

## Produced operations, not enum count

Disassembled 142 retained artifacts with packaged 0.9.3: 24 standard/seed-17
application corpus objects/executables, 16 O0–O3 application objects, 63
XIR/HPIR investigation artifacts, 33 contextual I/O artifacts from a previous
journey, and six new service/Word32 artifacts. The census saw 38 distinct
opcodes and 35 direct builtin names. The twelve application pairs alone
covered 31 opcodes and 24 direct names, plus first-class filesystem callable
symbols. These are static occurrences, not hot-path counts or declarations
of feature coverage. Inputs, probe and per-artifact hashes are retained in
`.temp/hpir-operation-inventory/`; the executable SHA-256 is
`a0f93c7ad725cd7844a00ae45d7b57a6341139d8e40a0b1e610084ef4d69a952`.
The census uses `msync disassemble FILE --json`, reading only
`functions[].instructions`; it does not count the duplicate prepared backend.

| Native operation family | HPIR destination and independent oracle |
| --- | --- |
| `Constant, Copy, FrameAddress, AddressOffset, Load, Store, Allocate` | Primitive operands/moves and typed physical access; guest allocator call for real allocations. A plain field snapshot precedes a mutator; two aliases observe the same write; an escaping cell stays live after return; independent empty objects retain distinct identity. |
| `ProjectAddress, Narrow, Convert, Unary, Binary, Select` | Fixed primitive, representation change, conditional branch, or typed helper call. Fold exact ready cases; retain actual null/type/reflection and alternative layout/checked-conversion behavior, Number's IEEE behavior and pending result identity. Path joining, qualification and regex are typed service calls, not integer ALU operations. |
| `MachineCompare` and produced `MachineBinary, MachineCast, MachineIntrinsic, MachineUnary` | Width- and predicate-specific scalar/bit/convert operations or fixed helper. The last four can be emitted by [LLVM target selection](../../src/Msync.Hir/Lowering/LlvmNativeLowerer.MachineScalars.cs) but did not occur in this source census. Do not lose the supported lowerer cases or interpret this as evidence that every source Word32 call was selected to primitives: the live-file Word32 probe still emitted `bits.bit_xor/pop_count` and returned 2. |
| `Jump, Branch, Return, Unreachable` | Structural control/return and a source-attributed failure/trap if reachable. Keep simultaneous PHI semantics on edges and demand a condition only where selected. |
| `Call, CallNative, ComposeFunction, EnvironmentAddress, AdaptFunction` and emitted `FunctionComponent` | Direct fixed guest/import call, finite dispatch or genuine dynamic-selected ABI; environment fields and captures are ordinary physical addresses. Adaptations create/choose real entries but need no universal callable payload in a known call. `FunctionComponent` is emitted in some [closure lowering forms](../../src/Msync.Hir/Lowering/LlvmNativeLowerer.FunctionProjection.cs) despite its absence from this census. Include two different targets, one code/two environments, and replacement during argument evaluation. |
| `MakeRange, OpenIterator, NextIterator, CloseIterator` | Concrete snapshot, producer loop/cursor, or live cursor helper. Literal elements run at construction; lazy callbacks run at demand (0/2/1 control), and close occurs on exhaustion or actual scope exit. No realization of an unbounded suffix for finite destructuring. |
| `BindLocation, TestStatic, StaticAddress, PublishStatic` | Stable session cells, lexical location rebinding in the producer, and known static storage/publication, with an actual first-use check where necessary. Keep named scope singleton identity and do not coalesce independent mutable cells. The inliner's synthetic binding is not a universal runtime operation. |
| `Transfer, ProcessCall, SelectPath, Barrier, Acquire, ExecutePlan, PlanResult` | Typed residual and prepared service calls; work submission, explicit order and joins, actual result values, and lexical close on abrupt exit. The service-mix probe observed `Acquire` and verified three file payloads from selection, released child plus process stdout, and a separately bracketed transfer. |
| `Move, Drop, IndexAddress, CopyMemory, ClearMemory, MakeFunction, RangeFromMemory, MakeType` and `Nop` | Not observed. No active source target emitter found for these forms; `Nop` does occur as transient optimizer/tiering machinery. Some are covered by hand-built Native-2 format/runtime fixtures, not a reason to preserve their opcodes after a format cut. Their relevant language roles (copy/move lifetime, indexed pointer and bulk memory, closure, snapshot, runtime Type test) use physical ops/helpers. Remove obsolete wire paths only alongside their consumers and fixtures. |

## Host and execution owners

Use [the registered signatures](../../src/Msync.Core/Native/NativeSignatures.cs),
the [builtin resolver](../../src/Msync.Runtime/NativeModel/NativeBuiltins.cs)
and the [native builtin journey](../../tests/NativeBuiltinsV090/README.md)
as the complete family inventory, not the 35 sampled symbols:

| Builtins / entry class | Retain on migration |
| --- | --- |
| `math, bits, bool`; pure `string, text, regex, path, cast` variants | Fixed ready ABI or primitive where semantics match; typed pure text/regex and explicit failure/locale/path projection. In particular a synchronous builtin does not require a universal asynchronous argument list. |
| `ranges`, iteration cleanup, `io.read_lines/read_bytes/stdin_lines` | Producer, typed item, callback and cursor ownership contracts, including stable sort and bounded/lazy consumption. An inert stream constructor must not observe live bytes, reader or callbacks early. |
| `fs, io, process`, source transfers and process calls | Work/endpoint ownership, path access annotation, real platform checks, fail/cancel and publication rules. Selected scalar read/view can produce a typed future. Ready process handle, readiness of its result, and required join are different. The existing directional admission/resource scheduler and transport services are not old guest-execution paths. |
| `environment, meta, shell`, writable text builders, builtin record factories | Context, concrete builtin layouts, requested reflection and local mutable state, reader/writer and process injection; ordinary fields stay nonreflective. Adapters across testing/embedding or editor/display may box results at the actual host boundary, not when a guest calls a guest. |

- [Session lowering](../../src/Msync.Hir/Lowering/LlvmHirLoweringSession.cs)
  assigns stable IDs for exports, unresolved/import cells, statics and
  callable specializations. [Execution session](../../src/Msync.Runtime/NativeModel/NativeExecutionSession.cs)
  keeps those roots and accepted generations in one owner, not serialized
  from a live REPL. Published dispatch tables allow new compatible targets
  without rebasing earlier captures or replacing suspended program counters.
  An editor projection checks a ready value without demand; the display
  boundary may consume a Range or explicitly observe a field. A compile
  error publishes no new language binding. An actual execution failure
  reconciles initialized prefix state; a new/disposed session must not
  accidentally inherit roots/work. The existing REPL command set has no
  blanket runtime reset command to add as a condition of this cutover.
- [Work scopes](../../src/Msync.Runtime/NativeModel/NativeWork.cs) own
  futures, process jobs and admission-frontier views. Retained jobs and
  transitive dependencies survive appropriate frame/submission boundaries;
  unneeded input/file/stream work can be abandoned without holding the
  submission open. Awaiting cleanup is not equivalent to canceling the wait.
  A worker joins the frontier captured on submission, not later work that
  can depend on it. Track host handles, GC roots and lexical iterator/acquire
  cleanup independently of the choice of ready frame ABI.
- **REPL test correction at migration.** In packaged 0.9.3,
  `repl_state_journey.py` cases `nullable-prefix` and `callable-prefix`
  fail their existing oracle in both JIT off/auto. A discarded
  `fs.canonical(missing)` constructs a file-value future, but is not a
  payload demand. The [established LLVM/Native contract](native-midend-v090.md#native-target-execution-journey-and-demand-boundary-2026-11-15)
  explicitly rejects inserting an eager await to make such a test pass.
  Replacing that stimulus with
  `io.println(fs.canonical(missing) as String)` makes the error real at
  the original position. In disposable copies, the nullable, callable,
  scope-alias and new-declaration prefix cases then all passed (eight
  runs: four cases, JIT off and auto). Use explicit demand when testing
  prefix recovery, and keep an independent control for discarded reads.
  This is distinct from the pending-identity optimizer defect.

The first emitted HPIR image must exercise the physical boundary, not
pass the retained controls through the old executor. A useful integrated
slice combines same-ABI nested calls, live roots on segment growth,
field snapshot and mutable escaping capture, and a detached prepared
transfer. Then expand using [persistent sessions](../../tests/NativeSessions/README.md),
[detached preparation](../../tests/NativePreparation/README.md),
[file-read futures](../../tests/FileReadFutures/journeys.py) and platform
host-I/O acceptance. Keep expected stdout/bytes/error source and runtime
ownership visible alongside timing and mechanism counters.
