# InstCombine memory source family

## Ownership and source

This family translates LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`, principally
[`InstCombineLoadStoreAlloca.cpp`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/InstCombine/InstCombineLoadStoreAlloca.cpp).
The allocation-site use walk is from `InstructionCombining.cpp`
`isNeverEqualToUnescapedAlloc`, `isAllocSiteRemovable`, and `visitAllocSite`.
Cast legality comes from `lib/IR/Instructions.cpp`, not a new memory type system.
The Apache-2.0 WITH LLVM-exception source license applies to these translations.

The incomplete source cohort lives in `.temp/llvm-instcombine/` until the shared
InstCombine work loop and dependencies are published coherently. These files
extend the same `LlvmInstCombine.Work`; they do not introduce a separate pass,
heap solver, work queue, runtime validator, or execution admission prerequisite.
The parent owns default pipeline integration and serialized builds.

## Correspondence

| Translation | Pinned mechanisms |
|---|---|
| `Memory.Alloca` | `simplifyAllocaArraySize`, scalar count canonicalization, constant array allocations, index-width casts, zero-byte alloca movement/merging, allocation-site cleanup |
| `Memory.ConstantCopy`, `Memory.PointerReplacer` | Source bounded copied-from-constant use walk, alignment/dereferenceability queries, cross-address-space collection/revisit/reverse-postorder pointer replacement |
| `Memory.AllocSite` | Bounded derived-pointer user walk, mutually exclusive read/write access classification, equality folds, weak tracking handles and source deletion order |
| `Memory.Types` | Load-use and stored-value cast canonicalization, unordered/elementwise/swifterror/AMX gates, atomic type legality |
| `Memory.Aggregate` | One-element unpacking, padding-aware struct splitting, bounded array splitting, byte offsets, common alignment, aggregate reconstruction |
| `Memory.Pointer` | Object-size upper-bound walk, first-nonzero GEP index fold, subsequent sign checks, guaranteed-transfer scope, depth-three non-null simplification through select/GEP/PHI |
| `Memory.Load` | Existing source simplify-load query, shared source available-load scan, source safe-load queries, constant-pointer select folding, null UB canonicalization |
| `Memory.Store` | Stored-type canonicalization, dead alloca stores, AA constant-memory query, six-instruction local DSE/writeback elimination, unreachable marker handling, undef/poison and byte constants |
| `Memory.MergeStore` | Two-predecessor diamond/triangle store merging, source special-state/cast checks, memory/throw stopping conditions, value PHI and successor-store construction |
| `LlvmCastInstruction.MemoryQueries` | `isNoopCast`, `isBitCastable`, `isBitOrNoopPointerCastable`, including address-space and non-integral-pointer restrictions |

Shared helpers preserve their own actual LLVM algorithms. In particular,
`isSafeToLoadUnconditionally` is not approximated by dereferenceability alone;
its source local-access scan is a separate active dependency. Likewise,
`FindAvailableLoadedValue` is not replaced by pointer equality or a new heap
analysis. The AA object is a required function-pass dependency, not an optional
null path that suppresses memory optimization.

## Representation and explicit boundaries

`LlvmAllocaInstruction` and the project `LlvmHeapAllocInst` remain distinct.
Zero-byte alloca merging and the static object-size fold do not merge, resize,
or pretend stack ownership for heap objects. Heap identity and resource effects
remain ordinary project instruction semantics. Range has no special rule.

The coordinated memory-intrinsic cohort represents actual transfer/set,
lifetime, invariant start/end and launder-invariant-group identities. The alloca
constant-copy walk and its cross-address-space `PointerReplacer` are translated
and consume this source registry, the shared source alignment/dereferenceability
queries, and the actual target address-space-cast legality query. No ordinary
runtime function name is treated as an intrinsic. The allocation-site walk
retains the exact source switch: inline transfer/set variants are not added to
cases that only enumerate memcpy/memmove/memset. Objectsize and pointer-authentication
IDs are not represented. Allocation library families are unavailable under the
current all-disabled TLI; allocator-kind/family attrs are not represented. The
active allocation-site path is the represented alloca use walk, including the
registered assume and memory intrinsics. It does not adopt project heap
instructions as source removable library allocation calls.

Vector types exist, but vector extractelement instructions currently do not;
`likeBitCastFromVector` therefore has no represented instruction match. Scalable
GEP offset materialization additionally needs the source vscale intrinsic. LLVM
AA/poison/range/debug metadata graphs and sanitizer function attrs are not
represented; no metadata facts are fabricated. Load/store ordering, volatility,
sync scope, elementwise state, GEP wrap flags and source spans are preserved by
actual instruction construction. Newly changed physical types are not mislabeled
with an unrelated producer logical type. Native2 lowering must handle the
resulting real LLVM carriers rather than reinterpret a stale logical annotation.

Source budgets remain `MaxArraySize` (1024),
`MaxAllocsiteRemovableUsers` (2048), non-null recursion depth 3, and store backward
scan length 6. The copied-from-constant source budget 300 counts visited derived-pointer/offset
pairs, exactly as the source, rather than an unrelated replacement user limit.

## Work telemetry and validation

The shared pass owns phases, fixpoint scheduling and trace context. Memory
counters record real visitor entries, scanned instructions/users, budget exits,
aggregate elements, cast rewrites, GEP replacement scope, forwarded/CSE loads,
constant-select/speculated-select rewrites, store deletion reasons, allocation
removal, and store-merge PHIs. Disabled metrics add no independent trace sink or
per-memory-word recording.

No independent build has been run by this worker. The coherent cohort and shared
queries must be compiled together by the parent. Focused acceptance should follow
source Scope field stores/loads through HIR, the shared pass, Native2 lowering,
artifact roundtrip and execution; pair this with properties for volatile/ordered
accesses, padded aggregates, non-integral casts and heap identity. Build success
or rewrite counts alone do not establish user workflow correctness.

The byte-constant store uses the real interned `ConstantExpr.GetBitCast` API,
not an instruction cast or a failed-fold fallback. The source `m_ImmConstant`
exclusion of scalar ConstantExpr nodes is retained. Vector ConstantExpr splat
matching depends on the shared constant splat-query carrier, not on treating all
expressions as immediate constants.
