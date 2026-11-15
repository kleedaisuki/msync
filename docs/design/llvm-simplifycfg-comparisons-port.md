# LLVM SimplifyCFG comparison and conditional-store families

Source pin: LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`.
The implementation is staged with the whole SimplifyCFG cohort in
`.temp/llvm-simplifycfg/`; integration/build/execution is coordinated by the
root agent. Staged files are not evidence that the default compiler runs these
transformations yet.

## Source correspondence

| Managed file | Upstream implementation |
| --- | --- |
| `LlvmConstantRange.EquivalentICmp.cs` | `llvm/lib/IR/ConstantRange.cpp::getEquivalentICmp`, both overloads |
| `LlvmSimplifyCfg.ComparesGatherer.cs` | `SimplifyCFG.cpp::getConstantInt`, `ConstantComparesGatherer` constructor, `setValueOnce`, `matchInstruction`, `gather` |
| `LlvmSimplifyCfg.EqualityComparison.cs` | `isValueEqualityComparison`, `getValueEqualityComparisonCases`, `valuesOverlap`, `simplifyEqualityComparisonWithOnlyPredecessor`, `performValueComparisonIntoPredecessorFolding`, `foldValueComparisonIntoPredecessors` |
| `LlvmSimplifyCfg.ComparisonCloning.cs` | `cloneInstructionsIntoPredecessorBlockAndUpdateSSAUses` |
| `LlvmSimplifyCfg.ConditionalStores.cs` | `findUniqueStoreInBlocks`, `ensureValueAvailableInSuccessor`, `mergeConditionalStoreToAddress` |
| `LlvmBasicBlockUtils.IfThen.cs` | `BasicBlockUtils.cpp::SplitBlockAndInsertIfThen`, block-pointer `SplitBlockAndInsertIfThenElse`, and `BasicBlock.cpp::splitBasicBlock` used by that helper |

The comparison gatherer retains the original depth-first traversal, visited set,
logical AND/OR distinction, one unmatched extra clause, retry ignoring the first
mismatched comparison, masked equality expansions, add-adjusted interval, and
eight-value enumeration limit. Constants and ranges use actual arbitrary-width
APInt operations, not floating-point reinterpretations or host integer masks.

Equality folding preserves the original incoming-edge/outgoing-edge product
limit of 128, per-edge PHI multiplicity, predecessor case composition, explicit
infinite-loop block for self cases, and the original distinct-successor DT
updates. Context-uniqued constants use APInt total ordering where C++ used
pointer ordering solely to find equal keys; the source's actual stable unsigned
case ordering is also retained for explicit new cases.

Conditional-store merging keeps the source compatibility rules, intervening
memory-operation scans, generic TTI size-and-latency cost budget, aggressive
option, optional post-block split, available-value PHI reuse, conditional store
predicate, minimum alignment and atomic ordering/sync-scope transfer. It is not
an adjacent-store-only substitute and does not add a heap/alias solver. The
normal pass driver owns invocation, source defaults and telemetry spans.

## Representation adapters

- New PHIs and the merged store copy the already-known originating store Use's
  logical ABI annotation. This does not infer identity/layout from users and
  never writes per-use contracts into context-unique constants.
- The managed graph represents source spans rather than LLVM debug records or
  atom groups. Clones retain the span only when the source clone routine keeps
  the matching predecessor location. Debug-record salvage has no represented
  carrier to transform.
- General instruction metadata, branch weights, `unpredictable`, parameter and
  return UB attributes are not represented by this IR subset. No fabricated
  profile probabilities or metadata stubs are used. The five represented
  call-site attributes are not UB promises and remain preserved by cloning.
- The represented DataLayout has stable integral pointer representations.
  Pointer-null comparison constants use the actual DataLayout pointer integer
  width. `ConstantExpr` inttoptr and unstable pointer layouts are not represented;
  this helper does not manufacture them or add an alternate pointer semantics.
- PseudoProbe instructions are not represented; there is no corresponding clone
  to skip. Ordinary calls still retain their real effects and operands.
- The block-splitting helper operates on represented branches/PHIs with real
  eager/lazy DomTreeUpdater updates and optional LoopInfo mutation. It does not
  rebuild analysis globally or suppress updates.

None of these families distinguishes the language Range object. It remains an
ordinary builtin object pointer and runtime constructor call. LLVM integer
`ConstantRange` here is only the existing modular interval analysis datatype.

## Observability and focused verification

Work counters identify gather traversal/retry/unmatched outcomes, pruned branch
or switch cases, predecessor folding, bonus-instruction cloning/live-out PHI
rewrites, conditional-store retention causes, created PHIs/post-block splits,
and actual merged stores. The existing pass Work metric dictionary carries
these counts; disabled recording does not allocate a separate diagnostic sink.

`EquivalentIcmpProperties.cs` is staged alongside the component. It exhaustively
checks all nondegenerate modular intervals plus full/empty sets at widths i1
through i5, every member/nonmember, all source output predicates/offsets, and the
two-overload agreement. The integrated pass coordinator additionally owns real
source/CLI/Native2 journeys and actual transformation results; no execution or
speedup is claimed before those runs complete.
