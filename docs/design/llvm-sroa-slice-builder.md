# LLVM SROA pointer-use and slice construction

This source family translates LLVM
`a55e64432a516fd6507d3ecfba7407cfe425f940`:

- `llvm/include/llvm/Analysis/PtrUseVisitor.h` and
  `llvm/lib/Analysis/PtrUseVisitor.cpp`;
- `llvm/lib/Transforms/Scalar/SROA.cpp:1023–1510`, including the local PHI/select
  fold, `AllocaSlices::SliceBuilder` and `AllocaSlices` construction.

It is part of the optional scalar replacement of aggregates (SROA) optimization.
It does not add a mandatory heap analysis, a runtime admission check, a Range
operation or another IR opcode. It visits an individual LLVM alloca supplied by
the SROA driver; heap allocation placement remains a separate question.

## Traversal and state

`LlvmPtrUseVisitor` follows the source per-Use worklist, not a per-User traversal.
Each pending entry carries its own known-offset flag and APInt offset. Thus an
unknown-index path does not discard facts on a different constant-offset path,
and two operands of the same instruction remain distinct visits. The source
visited-Use set terminates cycles. No new work budget or global fixed-point
solver is introduced.

Offsets use DataLayout's pointer **index** width, including signed extension or
truncation of GEP displacement. GEP accumulation reuses the already translated
`GEPOperator::accumulateConstantOffset` implementation. Struct and sequential
indices therefore name physical byte offsets, not source field ordinals.

The managed virtual visit methods replace the source CRTP/InstVisitor dispatch;
the dispatch decisions and callbacks remain the same. Memory intrinsics remain
ordinary calls with their actual standard intrinsic identities. Generic pointer
visitation treats memory intrinsics and lifetime markers as the source no-op
visits; the SROA specialization builds their slices instead.

`LlvmPointerUseInfo` retains the source escape, read-only escape and abort
outcomes and the concrete instruction causing each. SROA constructs a fresh
visitor for each alloca. The source visitor's reset and per-visitor visited state
are not replaced with an independent cache or a function-wide summary.

## Slice-building correspondence

| Source path | Managed behavior |
| --- | --- |
| `insertUse` | Ignore zero-size or wholly outside-allocation uses as dead; clamp an otherwise overlapping end to the allocation extent using the source overflow-safe difference comparison |
| `visitLoadInst` | Record the actual store-size interval; an unknown pointer offset becomes a read-only escape, allowing the source driver to retain other known-offset forwarding opportunities |
| `visitStoreInst` | A stored pointer escapes and aborts; unknown-offset stores abort; statically out-of-allocation stores are recorded dead according to the source transformation |
| `handleLoadOrStore` | Only non-volatile integer accesses whose bit size equals store bit size are splittable; no invented atomic/field/type heuristic |
| `visitMemSetInst` | Zero-length/outside destinations are dead; constant lengths are splittable, dynamic lengths retain the source remaining-allocation interval |
| `visitMemTransferInst` | Maintain the source instruction-to-first-slice map; handle duplicate operand visits, identity/same-offset transfers, out-of-range removal and unsplittable offset transfers within the same allocation |
| `visitIntrinsicInst` | Track actual droppable Uses separately; lifetime start/end cover the allocation size; otherwise use the real pointer visitor/call rules |
| `hasUnsafePHIOrSelectUse` | Cache the maximum direct load/store byte size through the exact permitted zero-GEP/cast/PHI/select graph; retain the concrete non-rewriteable user or scalable access outcome |
| `visitPHINodeOrSelectInst` | Apply only the source's constant/same-value fold, preserve separate dead operands, record unsplittable byte intervals and reject an unknown offset |
| `visitCallBase` | An actual data operand may retain read-only escape status only when its call capture components exclude full provenance and its argument memory contract is read-only; ordinary unannotated calls follow the source escape/abort path |

Scalable load/store sizes use the source function's exact nonzero fixed vscale
range value when present; otherwise the source records the access as the
aborting instruction. PHI/select use discovery follows its stricter source rule
for scalable accesses. These paths do not substitute host pointer width or
managed object size for LLVM DataLayout.

`AllocaSlices` stores `EscapingInstruction ?? AbortingInstruction` when traversal
cannot continue. Otherwise it retains the read-only escape instruction, removes
killed slices, and uses the central owner's stable source ordering. The data
model and ephemeral partition iterator are shared with the SROA rewrite family;
this constructor does not duplicate partition policy.

## Diagnostics and focused properties

The SROA driver's function/allocation span owns this work. Its metric map records
actual `pointer_uses.*` visits, deduplication and constant/unknown GEP decisions;
`sroa.slice.*` records byte intervals, splittability, clamping, dead users and
operands, transfer outcomes, read-only escapes and concrete abort categories.
The slice result retains the offending instruction so the driver can include
its source/opcode context. Disabled recording creates no additional sink or
metric dictionary.

`PtrUseVisitorProperties` uses actual IR Uses to check physical struct-plus-byte
offsets, independence of unknown and known paths, two operands of one store,
self-referential PHI traversal and signed modular displacement at a 32-bit
pointer index width on a 64-bit pointer layout. The coordinator builds this
cohort and exercises the complete SROA pass through source/Native journeys;
component properties alone do not establish that integration or completion of
the full LLVM migration.

## Presplit scratch groups

`LlvmSroa.Presplit.cs` and `LlvmSroa.Splitter.cs` retain the pinned
`InstructionSliceMap` and `findOverlappingCopySlice` operation sequence, but keep
one slice reference directly in each dictionary value. A nullable overflow set
contains only additional distinct slice references. Repeating the first slice
is a no-op; overflow retains reference-set deduplication. Lookup examines the
first slice and then the overflow's existing insertion traversal, preserving
the prior managed grouping order and the source overlap predicates. Load/store
candidate and split-point order are unchanged.

This is a private two-field value local to this source family, not a general
container framework or a new optimization. The partition map is still built and
visited for the same partitions, including unsplittable ones. There is no new
eligibility condition, work threshold or early-return gate. Ordinary load/store
instructions have only one pointer-use slice per group, so that path no longer
constructs a child HashSet or its first-insertion backing storage. Genuinely
multi-slice instructions still allocate overflow storage when needed. The
outer dictionary value becomes one reference wider; no elapsed-time benefit is
claimed from that structural allocation reduction alone.

The existing caller-owned metric map records `presplit.slice_groups` and
`presplit.slice_group_overflows`: groups created and actual overflow-set
constructions, respectively. Disabled recording does not construct keys or a
separate map. `SroaPartitionChecks` retains both-endian overlapping load/store
copy results and checks that their groups use no overflow. A real overlapping
memmove with two slices also exercises overflow while retaining the operation,
its common allocation and its source-to-destination byte displacement. Root
owns the grouped build and actual source-journey execution for this change.
