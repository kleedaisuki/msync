# Upstream provenance audit: aggregate and memory prototypes

## Scope and result

This is a source-provenance audit requested after the user changed the objective
to strictly upstream-derived algorithms with LLVM IR as the primary IR, without
selecting a LLVM backend or pass pipeline. It does not approve the preceding
Native prototypes for integration or claim that they implement LLVM IR.

**Result: `NativeAggregateOptimization` and `NativeMemoryOptimization` are newly
authored Native-IR prototypes, not source ports of LLVM SROA, mem2reg, GVN or DSE.**
Their operation names and some high-level rewrite goals overlap those passes.
That resemblance is insufficient provenance under the current requirement.
Both prototypes are now excluded from the product source tree and retained in
the diagnostic archive described below. They were not connected to
`NativeOptimizer` as accepted implementations. No successful build or executable
acceptance is claimed for them. Their test helper was interrupted while fixtures
were still being completed; it is not verification evidence. The obsolete
`ImportCapture` fixture was removed, not replaced with another operation.

Only this audit document is changed by the provenance task. No production code,
new algorithm, pipeline decision or build is introduced by this task.

## Inspected upstream snapshot

The shared source identity is LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`, recorded in
[`vendor/llvm-reference/SOURCE.json`](../../vendor/llvm-reference/SOURCE.json).
It identifies an inspected immutable upstream snapshot, not a user-selected
LLVM release, backend or pipeline. All links below resolve to that commit.
An earlier exploratory read of `llvmorg-21.1.8` is not the reference for this
comparison. No moving `main` link is used as algorithm provenance.

The local reference manifest currently lists `Instruction.def` and LLVM's
license. It does **not** list imported implementations of any memory pass below.
The upstream implementations were inspected at their pinned source URLs:

| Component | Concrete upstream file and anchor |
| --- | --- |
| SROA use slices | [`SROA.cpp`, `AllocaSlices` and `Partition`, lines 574–770](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/SROA.cpp#L574-L770) |
| SROA use visitor | [`AllocaSlices::SliceBuilder`, lines 960 onward](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/SROA.cpp#L960) |
| SROA partition rewrite | [`SROA::rewritePartition`, line 5358 onward](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/SROA.cpp#L5358) |
| SROA promotion | [`SROA::promoteAllocas`, lines 6061–6073](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/SROA.cpp#L6061-L6073) |
| Promotion eligibility | [`PromoteMemoryToRegister.cpp`, `isAllocaPromotable`, line 62 onward](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/PromoteMemoryToRegister.cpp#L62) |
| Promotion SSA formation | [`PromoteMem2Reg::run`, defining/live-in blocks and IDF, lines 899–948](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/PromoteMemoryToRegister.cpp#L899-L948) |
| Promotion rename | [`PromoteMem2Reg::RenamePass`, line 1142 onward](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Utils/PromoteMemoryToRegister.cpp#L1142) |
| Local load availability in GVN | [`GVNPassImpl::analyzeLoadAvailability`, line 1524 onward](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/GVN.cpp#L1524) |
| Nonlocal load processing in GVN | [`GVNPassImpl::processNonLocalLoad`, line 2224 onward](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/GVN.cpp#L2224) |
| MemorySSA DSE | [`DeadStoreElimination.cpp`, algorithm description, lines 8–26](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/DeadStoreElimination.cpp#L8-L26) |
| DSE barriers | [`DSEState::isDSEBarrier`, line 1897 onward](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/DeadStoreElimination.cpp#L1897) |
| EarlyCSE | [`EarlyCSE.cpp`, concrete pass implementation](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/Scalar/EarlyCSE.cpp) |

## Exact local audit artifacts

The root quarantined the drafts rather than deleting the audit evidence.
[`QUARANTINE.json`](../../artifacts/diagnostics/native-midend-drafts/QUARANTINE.json)
records their exclusion from product compilation. The source tree restored the
previous generic Call/storage-planning path with earlier Range work retained;
this audit does not claim that a full LLVM-primary IR has been implemented.

| Archived file beneath `artifacts/diagnostics/native-midend-drafts/source/` | SHA-256 |
| --- | --- |
| `src/Msync.Optimization/NativeModel/NativeAggregateOptimization.cs` | `2bfea5ee0c8613bb1f0f45a580b3c8c17e410e18334cb83acde2faf4a4542a57` |
| `src/Msync.Optimization/NativeModel/NativeMemoryOptimization.cs` | `eabcbc3360c9cba9cefa36fc7d98a2f2f065e70f4aa993a62ed64808b024832e` |
| `tests/NativeOptimization/AggregateChecks.cs` | `b1890fe0c8c153fe33a2390b67692c1dd9b9aa069645c09bef67732e6bcd2760` |

Local algorithm descriptions below refer to these exact archived bytes, not to
an active product pass or a newly proposed design.

## Aggregate prototype versus SROA and mem2reg

### What the local code actually does

[archived `NativeAggregateOptimization.cs`](../../artifacts/diagnostics/native-midend-drafts/source/src/Msync.Optimization/NativeModel/NativeAggregateOptimization.cs)
uses `NativeProgram`, mutable numbered slots and PC-indexed instructions.
`ScalarReplacement.Index` counts producing definitions and identifies
single-element `Allocate` sites. `Discover` repeatedly scans the instruction
array, assigning custom `(allocation PC, layout, field ordinal)` paths to
copies, accepted narrows and explicit address projections. `CheckUses` tests
those paths' consumers, location exposure and producer dominance. `Rewrite`
creates one mutable Native slot per declared field, replaces field loads/stores
with copies, inserts field-slot `Drop` resets at the old allocation and deletes
the now-unused pointer operations. The wrapper repeats successful replacement
rounds and then invokes a separate local-memory routine.

Those are the actual producer structures and scheduling rules. They were not
copied or translated from an inspected upstream implementation during the
prototype's creation.

### Concrete upstream mechanisms and deviations

LLVM SROA starts from LLVM `AllocaInst` uses. Its `AllocaSlices` collects accessed
byte intervals, and a partition iterator groups overlapping slices. Slice
rewriters choose representations using `DataLayout`, handling memory transfers,
integer/vector forms and pointer PHI/select users where supported. Promotable
allocas are handed to `PromoteMemToReg`; the worklist revisits affected allocas.

The prototype does not implement those mechanisms:

| Upstream mechanism | Local prototype status |
| --- | --- |
| LLVM `Value`/`Use` graph and typed instructions | Native mutable slots and repeated whole-array discovery |
| Byte-offset slices and overlapping partitions | One named field ordinal in one known Native layout |
| Partial aggregate promotion | Whole candidate allocation accepted or retained |
| Slice rewriting of memcpy/memset and mixed accesses | Bulk-memory/address uses retain the candidate |
| Pointer PHI/select handling | No corresponding pointer PHI/select representation; unique producer/dominance restrictions instead |
| Promotion to SSA values | New mutable field slots and copies; no SSA definitions or PHIs created here |
| Affected-alloca worklist | Repeat the complete function scan after successful rounds |
| LLVM lifetime/debug metadata migration | Native `Drop`, Source spans and PC/storage-plan relocation |

LLVM mem2reg's general case places live PHIs through iterated dominance
frontiers and renames memory uses into SSA definitions. Its single-store and
single-block cases are explicit fast paths, not substitutes for general SSA
formation. This prototype has no IDF computation or rename phase and cannot be
called a mem2reg port merely because it turns loads into copies.

LLVM's treatment of uninitialized values also differs: its promotion machinery
uses LLVM undef/poison semantics. The prototype inserts Native field-slot clears
to retain Native uninitialized-storage behavior. This is a custom semantic
adaptation, not an imported LLVM rule. Native pending datums, physical-versus-
logical Scope field types and invocation-owned resources likewise have no
established LLVM-IR mapping in this code. No equivalence evidence was produced
for those adaptations before implementation stopped.

## Local memory prototype versus GVN, EarlyCSE and DSE

### What the local code actually does

[archived `NativeMemoryOptimization.cs`](../../artifacts/diagnostics/native-midend-drafts/source/src/Msync.Optimization/NativeModel/NativeMemoryOptimization.cs)
restarts address and available-field maps for each basic block. Within that
block it tracks fresh allocations, direct layout/field projections, source-slot
version counters and a most-recent store for each tracked field. An unchanged
stored source can replace a load with a copy. A later overwrite can remove the
previous store if the previous value was not observed. Explicit address exposure
clears that allocation's field map; no interprocedural heap solver is invoked.

This is a limited, newly authored forward block walk. It has no whole-function
memory SSA, no nonlocal reaching-memory values and no provenance identifying it
as a translation of a concrete LLVM implementation.

### Concrete upstream mechanisms and deviations

LLVM GVN represents reaching memory definitions/clobbers, analyzes whether a load
has a usable value, and processes nonlocal availability with PHI translation and
load PRE. The local routine has none of that nonlocal machinery, nor a GVN value
numbering table. Its source-version counter exists because its Native operands
are mutable slots, whereas LLVM operands are SSA values.

LLVM EarlyCSE is a closer category comparison than general GVN: it performs a
dominator-tree traversal with scoped availability tables, including memory
availability and memory-generation handling. Even that is not the implemented
algorithm here: local maps are discarded at every block boundary and there is
no imported upstream memory-generation/scoped-table control flow.

LLVM DSE follows MemorySSA definitions and uses to find an overwriting store,
checks intervening observations and ordering/throw barriers, and updates the
memory graph when removing operations. It includes partial-write reasoning and
nonlocal cases. The prototype only remembers a complete previous field store
within one block of a tracked fresh allocation. It has no MemoryDef/MemoryUse,
MemorySSA updater, byte-overlap intervals or cross-path execution analysis.

The private-allocation flag and `Expose` routine are local code, not imported
capture-tracking or DSE logic. The common semantic fact that pointer aliases
refer to the same storage does not establish source provenance for these
algorithms. Conversely, an observable address remaining allocated is a rewrite
boundary, not evidence that a new global identity-proof engine was imported.

## Evidence and compliance status

- No LLVM transformation implementation was copied into these prototypes.
- No pass-specific upstream regression tests were imported or replayed against
  a translated implementation.
- The prototype test helper uses the project's independent Native test machine;
  it is incomplete/unverified and is not an upstream LLVM regression test port.
- No current successful build or real source/HIR/object/bound receipt exists for
  this aggregate/memory tranche. Prior Range migration receipts concern a
  different implementation and cannot certify these files.
- The classes operate on Native IR, not a primary LLVM IR implementation. The
  shared `Instruction.def` snapshot does not change that fact.
- The prototypes therefore do not satisfy the current strict upstream-algorithm
  and primary-LLVM-IR objective. Their names must not be reported as delivered
  SROA, mem2reg, GVN, EarlyCSE or MemorySSA DSE coverage.

This audit records the gap and the immutable upstream anchors. It does not pick
an LLVM backend/pipeline, approve a replacement algorithm or authorize further
prototype work.
