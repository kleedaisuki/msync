# Research status

2026-11-13 · merged baseline `f3bd9d5`; v0.9 integration checkpoint,
not complete or published. Universal execution values still need removal.

## Current mandatory simplification gate

The user now requires **at least 30% net deletion from the current product
implementation, with semantics and performance preserved**. This is additional acceptance, not
credit for the old backend already retired. The versioned
[budget baseline](docs/design/native-code-budget-v090.md) freezes 50,794 code
lines and a final maximum of 35,555 (at least 15,239 net removed). Replacement
code counts; moving/minifying implementation, deleting features/tests and
discarding generated data do not qualify. Baseline commit: `636a466`.

The implementation direction is deletion-first: one real backend execution
lowering for baseline and JIT, one semantic query authority, one lexical surface,
and shared immutable instruction/type/layout contracts. Repeated proof/checking,
temporary address/value roundtrips, materialization histories and duplicate
mutable cache authorities are targets to remove, not layers to hide behind new
helpers. Changes are implemented and validated in coherent batches. The frozen
539-input image below is a correctness/performance control, not the final backend.
Current deletions remain far short of the budget. The large frontend work targets
recursive graph ownership and one partial semantic transfer, not another cache
around graph copying. Prototype gains and gross deletions are not achieved net
reduction.

## Required architecture

The language belongs in typed HIR. Lowering to relocatable IR must express
complete builtin representations, statically computed layouts, allocation and
reference binding, typed memory access, branches, calls and pointer arithmetic.
Scope is a namespace over those locations, not a generic runtime object.
`scope { ... }` directly allocates on the logical heap and binds its fields;
compiler and VM optimize physical placement as needed. There is no separate
standard-library allocator, arena/vector/page owner API or `Storage` type.
This is the implementation objective, not an optional optimization admitted only
for favorable benchmark shapes. The [storage/lowering contract](docs/design/object-spatial-layout.md#authoritative-model-2026-11-12)
records the details. [BENCHMARK.md](BENCHMARK.md) describes validation, not the architecture.

## Implemented in the v0.9 worktree

| Area | Implemented | Still needed before completion |
| --- | --- | --- |
| Representation and IR | All builtin representations, monomorphic raw/root layouts, physical-pointee-preserving addresses, captures and direct HIR lowering. Old Value/backend production sources are removed. Portable native format 1 has pooled strings/spans, bounded sections and opcode-specific compact binary operands. | Final integrated validation after the remaining semantic/runtime changes; compact files alone do not prove faster execution. |
| Compiler and execution | Canonical compiler/session, source/HIR/artifact execution and REPL use one native pipeline. Closed format-6 import and historical HIR-to-native binding adapters preserve detached inputs without a second VM. Frozen historical HIR fixtures passed 96 routes; pending type/identity controls also passed against old/new images. Persistent callable provenance/ABI, editor return-contract and deferred directory-origin regressions have focused real execution controls. | Finish final integrated acceptance, including the canonical standard namespace and remaining source-library traces; active tooling now preserves old behavioral oracles rather than aliasing them away. |
| Allocation optimization | Escape/liveness analysis, dead-memory/results, last-use moves and actual activation-frame coallocation. Owned Range backing escape and logical-region bounds have destructive regression tests. | Resolve the measured record-stream dispatch/allocation regression before broader workload claims; the focused allocation receipt is not a throughput result. |
| Middle end and backend | CFG cleanup, global must-constant/private-memory facts, bounded general static-call inlining, actual instruction/plan-PC compaction and temporary-slot coloring that preserves static type and pointer/layout proofs. O1/O2/O3 policies are distinct. Fresh-activation tail trampolining passes 100,000-step source/detached CLI journeys in Off/Linked/Force modes without recycling escaped locations. | Final integrated workload measurements. A focused fixed-image O1/O2 loop improved 86.38→57.41 ms after removing 8,192 source-call frames; the record nonhit did not establish a gain. These are separate workload results, not an overall score. |
| Guest JIT and deoptimization | One lowered execution image serves Off/Linked/Force; duplicate dispatch, virtual pointer recipes and materialization state are removed. The 580-input Core/Runtime overlay uses per-use ValueID proofs and eliminates unobserved work; real journeys/AOT pass, Off improves 6.62%/9.62% over its causal control. | Native JIT profitability remains unproved: record Force matches Linked, polygon Force still loses 22%. Main's iterator/callback origins and blanket private-write denials remain work. Auto stays gated; the old runtime gap and 30% deletion are not solved by this batch. |
| Source library and editor | Allocator APIs retired; Set/Heap/BTree/BPlusTree use ordinary Scopes. One fixed managed CLI payload passed all 32 source/HIR/object/bound × O0/O2 collection routes with exact independent oracles, plus REPL/LSP controls. Host record layouts now have a shared static ABI authority. Source/HIR/native shape controls cover associated types, nested captures and recursive factories. | Finish canonical module-cell lifetime acceptance and the extended tree traces on a final image; the 32-route receipt proves its frozen snapshot, not later edits or Native AOT throughput. |
| Contextual preparation | Native typed preparation, selected metadata/regex freezing, alias/directory transfer graph and dead-write elimination. O0/O2 detached changed-context journeys plus no-replan, failure and cancellation controls passed. The old planner is removed. | Final platform/observer acceptance on the release candidate; preserve one finite native SSH client/channel and actual transfer ownership. |

These are implementation results, not merged research claims. The
[native-model note](docs/design/native-model-v090.md) records contracts, focused
evidence and the retirement inventory. A passing subsystem does not establish a
complete replacement or a release-ready product.

One 521-input Linux x64 Native AOT integration checkpoint now passes all eleven
core application smoke cases through source/object/bound routes (33 checked
executions), nine actual 100,000-step tail-call CLI routes and native/linked
telemetry/REPL-delta checks. The same image passes 104 local I/O and 12 SSH routes.
[Integration receipts](artifacts/validation/native-model-integration-checkpoint/acceptance.json)
retain image/source hashes and the independent child reports. These establish
functional integration on that image, not standard-scale throughput or a release.

An earlier middle/backend batch was frozen once (539 inputs) and validated together:
private fixed-frame access canonicalization and checked-source DSE rules, native
pointer materialization recipes, guarded typed loads/refinements, compact native
descriptors and bounded exact counter batching. The shared Native AOT image passes
the core application routes, larger record/polygon controls, deep tail-call CLI
and telemetry journeys; the same source passes 1,239 native-tier contracts.
[Batch receipts](artifacts/validation/native-mid-back-batch-provenance/acceptance.json)
also retain the negative performance result: on this image Force takes 299.9 vs
238.2 ms CPU for 8,192 records, and 1,702.1 vs 1,083.7 ms for 4,096 polygon
queries. Linked also fails to beat Off in this window. Automatic promotion stays
disabled; correctness and direct-op coverage are not a profitability result.

The following 573-input default-lowering snapshot also passes the real application,
tail, preparation and telemetry routes, but fails performance acceptance. Same
artifact, paired current-window CPU medians are record 252.38→266.49 ms and
polygon 1,131.91→1,118.52 ms; current Force is 272.84/1,364.29 ms. Profiling finds
unobserved Visit/diagnostic work on the hot path, whereas whole backend function
construction accounts for only 0.53% of dynamic host instructions. The
[negative-result record](docs/design/performance-v080.md#default-execution-lowering-batch-negative-throughput-result-2026-11-12)
preserves exact image/source identities, raw samples and functional receipts.

The subsequent 580-input causal Core/Runtime overlay fixes that regression
without changing the frontend: same-window Off CPU medians are record
254.65→237.80 ms and polygon 1,098.16→992.57 ms. Shared ValueID facts distinguish
new values from the history of colored slots; unobserved profiling/diagnostic work
is absent. All corresponding real routes and Native AOT controls pass. The
[causal backend record](docs/design/performance-v080.md#valueid-and-unobserved-work-batch-causal-default-backend-gain-2026-11-12)
retains the exact cohort and limits; it does not establish a profitable JIT.

The following Runtime-only sparse-frame batch lowers allocation but is held on
CPU performance: record is +2.62%, polygon -0.86% versus 580. Generated code adds
an out-of-line managed address-return ABI to each read; an inlining fix is not yet
verified. A same-source/input old-image control also exposes a 4.46×/30.39× runtime
gap, with zero old guest-JIT/native-worker entries but substantial managed literal/
arithmetic fusion. The
[sparse-frame record](docs/design/performance-v080.md#sparse-frame-addresses-allocation-gain-cpu-acceptance-held-2026-11-12)
separates these mechanisms and preserves the negative results; restoring old
pattern-specific VM state is not the proposed solution.

The next 602-input provenance/lowering image passes its managed and Native AOT
gates, but **fails full application acceptance**: the real Set smoke source
compile exceeds 300 seconds inside repeated Core `Invocation.Import` traversal,
before guest execution. Independent record/polygon, preparation, tail, telemetry
and static-observation routes pass; they do not cancel that failure. Same-artifact
Off CPU improves only 0.65%/2.18% versus 580 in one balanced window, while Force
remains 4.86%/21.04% slower than Off. Record main now executes 89,862 raw native
operations rather than zero, so removing one proof denial alone is not a JIT
profitability result. The new lexical lowering shrinks the twelve polygon Scope
constructors from 22 to 14 operations and seven to three slots without changing
freshness. The frozen product is still **120 code lines larger** than the deletion
baseline. Repeated-DAG traversal is being removed, and the typed scalar baseline
proposal is not implemented. [Current evidence and limits](docs/design/performance-v080.md#program-provenance-batch-end-to-end-compile-regression-2026-11-13).

An early fixed Native AOT v0.9 image passes the same 8,192-record stream oracle
as v0.8.6 but is roughly ten times slower on the bound route (three paired
samples, one warm-up, seed 17). This is a diagnosed performance problem, not an
accepted architecture trade-off. General dispatch-closure allocation, step size,
stack-frame clearing and typed memory access have since been improved in isolated,
source-matched experiments. The remaining throughput gap and tier-transition
costs are still open; gains from different snapshots must not be added together.
This work does not restore retired
allocator APIs or a generic-object fallback. The migrated corpus no longer uses
`storage::vector_*`; Heap directly retains queued request records.

The extended BTree property journey also exceeds its existing 180-second limit
in frontend compilation, before VM execution. The original trace and independent
tree oracle remain unchanged. A stack sample found recursive type-identity
serialization, but bounded phase instrumentation attributes only part of the
cost to it. Subsequent traces show recursive query-cache depth growing while the
published function count stays fixed; the investigation now targets query
convergence and repeated graph projection, not an assumed string-cache cure.
This is a separate open compiler-performance problem, not a
reason to reduce the trace or blame guest execution.

## Merged foundations we can reuse

| Foundation in current code | What it establishes | What it does not establish |
| --- | --- | --- |
| Typed HIR and specialization | Fixed storage types, static Scope layouts, lexical locations, captures, effects and evaluation order. [Model](src/Msync.Hir/Model/HirModule.cs). | Complete native lowering or a typed frame/return ABI. |
| Phase separation and standalone artifacts | Analysis is separate from bytecode generation; binding does not rerun portable optimization; execution does not replan recorded I/O. [Contract](docs/design/typed-hir-v081.md). | That the existing lower-level instruction set has erased language-level objects. |
| Format-6 place/capture metadata | Symbolic field places, static slots, signatures and value/location captures survive historical artifacts; the closed reader/importer preserves these contracts. | Physical builtin layouts, lifetime operations, or the elimination of generic Scope payloads merely by reading old metadata. |
| Alias/effect analysis and real continuations | Shared locations, effect order and actual suspension already have analysis/runtime evidence. [Effects](docs/design/mutable-state-and-effects-v080.md). | A reason to retain generic Cell/Scope objects as the default representation. |
| Editor revisions and checked measurements | Revision-owned analysis, actual edit/delta journeys, independent application oracles and separate telemetry-off timing. [Editor](docs/design/editor-semantic-context-v080.md), [benchmarks](BENCHMARK.md). | Architectural correctness inferred from a fast microbenchmark. |

Selected native workers, numeric lanes, layout packing, collection-specific
arenas and their measured gains are implementation history. They are not proof
that the required representation is complete. In particular, calling the current
Set/Heap storage the completed target architecture was incorrect.

## Open implementation and research work

This checklist concerns the final replacement, not whether an isolated native
path exists. Current worktree implementation is summarized above.

| Required work | Current divergence | Completion evidence |
| --- | --- | --- |
| Complete builtin-native representation | Native implementation exists; the historical Value/backend removal is applied in the worktree and undergoing integration verification. | All accepted routes use complete builtin representations without generic fallback; monomorphic locations have no universal tag. |
| Scope allocation and binding | Native lowering emits typed allocations/addresses/stores; logical schema versus physical carrier boundaries still need full source-library coverage. | Preserve freshness/aliases/effects and prove physical optimizations through actual user routes. |
| General storage/control IR | Native IR and the closed historical import boundary are active; compact portable encoding and process-local execution linking are separate. | Preserve complete source expressiveness, layout/ABI safety, original diagnostics and detached artifact behavior without source replay. |
| Remove separate allocators | Source exposure and old host owners are removed; historical Storage-mask handling is isolated as import/Type-description metadata. | Collections execute as source Scopes; historical descriptor import never resurrects an allocator runtime kind or native collection schema. |
| Unified lifetime and transfer analysis | Native escape/work ownership and contextual preparation are implemented; boundary regressions and whole-product coverage remain. | Shared captures, returned/deferred values, pending work and real Process/I/O lifetimes remain correct under optimization and cancellation. |

Full builtin support is required even when a workload has not demonstrated a
speedup. Optimization profitability selects implementations of that support; it
does not decide which types or language constructs deserve native representation.
A closed tagged union is not permission to put an arbitrary object behind the tag.

## Questions within that architecture

The user's performance requirement covers middle-end optimization, backend code
generation and VM speculation/deoptimization together. Host Native AOT is not a
guest JIT. Portable encoding, process-local execution instructions and optimizing
IR serve different purposes and must not be confused merely because all can be
serialized. Tiering work is required implementation, not a claim that historical
numeric-only experiments already satisfy it.

- Which allocation and copy operations disappear after locations, aliases and
  lifetimes are explicit? Measure complete producer-to-consumer paths, not just
  a packed container whose readers immediately rebox its contents.
- Which facts can be reused across submissions or edits without retaining old
  semantic environments or freezing mutable locations? Current REPL age growth
  and collection compilation costs remain observations to explain, not a reason
  to build more caches around the old object model.
- Which layout and scheduling choices help actual workloads while preserving
  effects and floating-point order? Register/stack placement, grouping, fusion
  and batching operate over the general typed representation, not named Scope
  patterns or recognized collection implementations.
- Which executable backend is most appropriate once that representation exists?
  An interpreter and native emitter must implement the same complete operations.
  LLVM or an embedded Wasm engine is an implementation alternative, not an
  excuse for a narrow native subset plus generic-object execution.

Older rejected patches remain evidence about those particular implementations,
not arguments against native return/reference support or compiler ownership
analysis. Historic [performance observations](docs/design/performance-v080.md)
retain their exact image identities and must not be added into a cumulative score.
Neither old benchmark receipts nor current subsystem passes establish completion
of the whole replacement. End-to-end performance is measured only after the
representative correctness journeys and image identity are fixed.
