# LLVM InstCombine source port

## Source baseline and scope

The immutable source snapshot is LLVM
`a55e64432a516fd6507d3ecfba7407cfe425f940`. The complete source directory was
inspected and copied under `vendor/llvm-reference/llvm/lib/Transforms/InstCombine`
(19 source/header/TableGen files, 58,178 lines). The controller is
[`InstructionCombining.cpp`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/InstCombine/InstructionCombining.cpp),
with common interfaces in `InstCombiner.h`/`InstCombineInternal.h`, source
`InstructionWorklist.h`, and builder folding in `Analysis/TargetFolder.h`.

This component is the actual InstCombine worklist and visitor architecture,
not a renamed InstSimplify pass. InstructionSimplify is used at the precise
source call sites and supplies real query-based folds; it does not replace
visitor families, reassociation, distribution, demanded-bit transforms,
canonicalization, or memory/call transforms.

The entire new pass remains staged in `.temp/llvm-instcombine` until its source
families and shared dependencies are coherent. Its integration contract is
`INTERFACES.md` there. Existing live shared-query improvements may be published
separately with their actual consumers; that does not publish this pass or
change the accepted product pipeline by itself.

## State, ownership and source protocol

The managed partial `LlvmInstCombine.Work` contains the state of the source
`InstCombiner` and `InstCombinerImpl` objects. Source inheritance is flattened
into one cohesive object, not a second optimization algorithm. One Work is
created per source outer iteration, while the worklist and RPO traversal are
owned by the function invocation. Function pass execution requires the real
AA, assumption cache, dominators, TLI and generic TTI context. Missing AA does
not silently disable memory transformations or construct a different analysis
behind the caller's back.

`SQ` carries the actual context, dominators, assumptions, TLI and
`DomConditionCache`. A `Query(I)` copy changes only the instruction context;
recursive helpers preserve the other fields. The condition cache uses the
source's explicit registration/removal responsibility, not an invented global
proof engine or automatic heap solver. Ordinary pointer lifetime, identity and
known layout do not become execution admission checks.

The visitor return protocol is source-exact:

- null: no change;
- original instruction: mutated in place;
- another detached instruction: replace the current instruction, transfer the
  source/name, repair PHI/non-PHI insertion position, queue result/users and erase
  the old instruction;
- a fold to an existing Value uses `ReplaceInstUsesWith`, which queues users,
  performs RAUW and returns the original instruction for the controller.

`EraseInstFromFunction` removes worklist/cache references, erases the intrusive
instruction node, then notifies operand use-count decrements. It does not shift
an entire block array. `ReplaceUse`/`ReplaceOperand` likewise notify decrements,
including the source one-use opportunity that queues the remaining user.

## Worklist and iteration algorithm

The source utility is now named `InstructionWorklist`, not
`InstCombineWorklist`. Its state consists of:

1. a LIFO vector with an instruction-to-index map;
2. null tombstones for removals without shifting the vector;
3. an insertion-ordered unique deferred sequence.

Deferred instructions are popped in reverse order and pushed onto the LIFO
vector, so they are visited in insertion order. Both the deferred drain and
normal pop perform real DCE before visiting. This reduces use counts early and
unlocks one-use folds. The managed representation uses a List/Dictionary for
source vector/map and List/HashSet for source SmallSetVector; deferred removal
has the same ordered-vector removal role rather than a second scheduling
policy.

`PrepareWorklist` walks the source reverse postorder, marks dead edges and PHI
inputs, folds obvious constants, caches folded ConstantExpr operands, removes
nonterminators from unreachable blocks, then pushes the initial instruction
sequence in reverse program order. The source ordering avoids repeatedly
processing users before their definitions. ConstantExpr support is a real
shared IR/folding dependency; it is not treated as an unknown scalar constant.

The outer driver preserves the pinned defaults: `MaxIterations = 1` and
`VerifyFixpoint = false`. A source iteration continues until its actual worklist
is empty. It is not replaced by repeated whole-function InstSimplify scans.
The optional source fixpoint diagnostic is not enabled as a runtime safety or
quality gate. Debug counters/debug-only verification do not alter release
execution.

## Builder and common context

InstCombine uses **TargetFolder**, not ConstantFolder. The builder owner is
porting that source strategy and retains existing builder constructor defaults
for all other passes. The new insertion callback queues inserted instructions,
registers newly created assumes, and preserves source diagnostic context.
IRBuilder intrinsic operations may fold to a Value rather than always returning
a CallInstruction; callers retain that actual source return contract.

The source restricts TTI usage to target-specific intrinsic combining, with its
explicit legality-query exceptions such as address-space casts. It is not used
as an invented target-dependent cost policy for ordinary target-independent
InstCombine patterns. Profile pointers follow actual absent/present profile
representation, not fabricated weights.

## Family ownership

| Source family | Implementation ownership |
| --- | --- |
| InstructionWorklist, main run/prepare/iteration, mutation/query context, sinking | loop_framework |
| AddSub, Negator and freely inverted values/users | known_fp_class |
| AndOrXor main visitors and bitwise families | known_bits and parent-assigned source subfamilies |
| MulDivRem, multiplication overflow source queries | integer_value_facts |
| Shifts and integer casts/type evaluation | integer_sign_bits with direct_call_ir |
| Integer/pointer compares and source subfamilies | icmp_simplify and parent-assigned compare owners |
| FP compares, FP select pattern/folding dependencies | constant_fp_range |
| Select and operation/select shared transforms | loop_transforms and source subfamilies |
| Casts, pointer casts, exact int-to-FP, type/narrowing policy | direct_call_ir and source subfamilies |
| PHI, PHI operation pushing and recurrence | scalar_ssa |
| Load/store/alloca/allocation sites | storage_contract |
| Calls and intrinsic visitors | ipa_transforms and source intrinsic owners |
| Demanded bits/elements/classes and standard intrinsic carriers | scalar_intrinsics and source FP intrinsic owner |
| GEP, TargetFolder and actual ConstantExpr folding dependencies | code_metrics and ConstantExpr owner |
| Return/branch/switch/unreachable, scalar aggregates, freeze | cfg_hoist |
| Shared associative/distributive algebra | integer_simplify |

The implementation does not satisfy a missing source dependency with a
false/null/no-op placeholder. Shared dependencies are assigned by their actual
source routines, including LLVM flag/attribute operations, non-refining
substitution's DropFlags list, call memory destinations, exact cast-pair tables,
standard intrinsic descriptors, and numerical formats.

The only project instruction extensions remain HeapAlloc, FileIn and FileOut.
They participate in the real worklist/effect/DCE machinery but do not acquire an
invented upstream visitor. Range remains an ordinary runtime object pointer.
Canonical constants never receive a use-specific logical type; per-Use ABI
annotations must survive same-role rewrites and must not be copied across a
change in physical type.

## Progress and evidence

Controller, state, worklist, options, RPO preparation, sinking and visitor
routing are staged as real source translations. Source families are being
ported in parallel. No complete-InstCombine build, production journey or
performance result is claimed yet. The last accepted Native AOT product remains
the separately recorded pre-InstCombine baseline while this cohort is staged.

Telemetry records actual preparation, deferred/worklist operations, visitation,
constant folds, source rewrite decisions, removals, use-count changes and outer
iterations with function/block/source context. The disabled path does not
allocate a metrics dictionary or per-visit attribute map. Validation will use
focused real compiler/source/artifact journeys and discriminating instruction
counts, not a new quality gate or a count of staged files.

## Scalar cast family and shared cast semantics (staged cohort)

`LlvmInstCombine.Casts*.cs` translates the scalar paths in
[`InstCombineCasts.cpp`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/InstCombine/InstCombineCasts.cpp).
The source common-cast ordering is retained: contextual InstructionSimplify,
cast-pair elimination, select pushing and PHI pushing. `EvaluateInDifferentType`
rebuilds the selected expression and memoizes multi-use nodes; it does not
repeatedly clone a shared expression tree. The corresponding integer-family
owner supplies the source type-evaluation predicates, including pending-user
accounting, original recursion limit and the zext `BitsToClear` result.

Pointer casts normalize to the DataLayout's pointer width, preserving the
difference between pointer size and address/index size. One-use GEP chains
are converted to integer offset arithmetic only for the source-matched null or
inttoptr bases. The real shared `EmitGEPOffset` emits those offsets. Bitcast PHI
web rewriting traverses all related PHIs, retains the source's permitted
load/store users, uses the shared typed-load combine and rewrites the complete
web; it is not a local cast deletion heuristic.

The shared Core `LlvmCastInstruction.IsEliminableCastPair` is the complete
14-by-14 source table from `Instructions.cpp:2969–3238`, including byte/pointer
rules and PtrToAddr's address width. ConstantExpr construction and
InstructionSimplify consume that same table. Its optional DataLayout and the
context's source `i2p-p2i-opt` option retain the source distinctions; InstCombine
adds the source restriction against creating non-pointer-width inttoptr or
ptrtoint. The old identity-only InstructionSimplify table has been removed.
The lossless inverse-cast helpers use actual ConstantExpr/ConstantFolding and
retain `NNeg`, `NUW`, and `NSW` results; they do not replace poison/undef or
symbolic constants with an APInt-only approximation.

Floating casts retain exact-int-to-FP queries, integer/FP roundtrip folds,
FP-division-to-integer-division constraints, flag propagation and FPTrunc's
operation-specific double-rounding conditions. Minimum constant formats are
found by actual APFloat conversion. Lossless format conversion checks exponent
range and subnormal representability as well as precision: bfloat and half
cannot be ordered by mantissa alone. FPTrunc narrowing intersects `ninf` from
the operation and truncation, and SIToFP-to-UIToFP retains only meaningful
`ninf`/`nsz` flags. Floating intrinsic reconstruction preserves operand bundles.

The numerical-format dependency is the separately staged IEEE half, bfloat,
single and double APFloat implementation; extended multiword formats remain
an explicitly unimplemented dependency, not a double-precision substitute.
Vector resize/shuffle/lane casts are outside this scalar tranche. The represented
`vscale`, integer `abs` and `ptrmask` intrinsic branches use their actual typed
source declarations; vscale is not replaced by a guessed constant scale. No always-null helper is
provided for any represented scalar transform. Telemetry counts actual common
queries, cast-pair rewrites, rebuilt expression nodes/cache hits, integer-width
normalization, GEP/PHI work, exactness queries and the concrete floating rewrite
families. Publication, a coherent build and real execution remain owned by the
root integration cohort; this cast-family owner did not run MSBuild.

## Integer cast visitors and adjacent bitwise cast/shift helpers

The represented scalar integer-cast source cohort is staged in
`.temp/llvm-instcombine/` and shares `Work` with the driver and all other visitor
families. Its pinned `InstCombineCasts.cpp` mapping is:

| Upstream algorithm | Managed file |
| --- | --- |
| `TypeEvaluationHelper`, 330–708, 1504–1633, 1901–1957 | `Casts.TypeEvaluation.cs`, `Casts.TypeEvaluation.Truncate.cs` |
| `narrowFunnelShift`, `narrowBinOp`, 828–1017 | `Casts.Integer.Narrow.cs` |
| `visitTrunc`, 1063–1395 | `Casts.Integer.Trunc.cs` |
| `transformZExtICmp`, 1396–1478 | `Casts.Integer.Compare.cs` |
| `visitZExt`, 1634–1815 | `Casts.Integer.Extensions.cs` |
| `transformSExtICmp`, 1816–1899 | `Casts.Integer.Compare.cs` |
| `visitSExt`, 1958–2134 | `Casts.Integer.Extensions.cs` |

These files retain the source ordering of common casts, expression evaluation,
demanded bits, integer narrowing, comparison extraction and flag inference.
The type evaluator retains the default depth limit of 128, the initially-false
visited entries for recursion, and the deferred pending-user check. In
particular, multi-use expression nodes are not rejected outright or duplicated:
the source traversal requires all their users to belong to the evaluated tree,
and rejects select-condition uses at the same point as upstream. ZExt keeps the
separate single-use recursion and `BitsToClear` transfer functions. The managed
implementation selects the original truncation/sign-extension predicate without
allocating a capturing delegate at every recursion level; this represents the
upstream non-owning `function_ref`, not another analysis algorithm.

Integer visitors retain unsigned/signed truncation flags, `nneg`, sign-bit and
masked-zero queries, boolean shift extraction, saturating add/sub recognition,
truncation/extension expression rewrites, narrowed `ctlz`, comparison-intrinsic
result widths, and the typed `vscale_range` conditions. The `vscale` intrinsic is
rebuilt with its actual result type; no target scale is guessed. Funnel narrowing
retains the source matching shift-amount forms, masking checks, high-zero
requirements and distinct left/right intrinsic identities. ConstantExpr and
ConstantFolding retain their different source roles for symbolic truncation and
mandatory constant evaluation.

The adjacent `InstCombineAndOrXor.cpp` chunk maps as follows:

| Source algorithms / lines | Managed file |
| --- | --- |
| `matchDeMorgansLaws`, 1736–1770; `foldComplexAndOrPatterns`, 2062–2201 | `Bitwise.Casts.Patterns.cs` |
| `shouldOptimizeCast`, `foldLogicCastConstant`, `foldCastedBitwiseLogic`, 1772–1950; `narrowMaskedBinOp`, 2021–2060 | `Bitwise.Casts.cs` |
| `reassociateForUses`, `canonicalizeLogicFirst`, `foldBinOpOfDisplacedShifts`, `foldBitwiseLogicWithIntrinsics`, 2203–2375 | `Bitwise.Casts.Reassociate.cs` |

The five intervening helpers (`foldAndToXor`, `foldOrToXor`,
`simplifyAndOrWithOpReplaced`, `foldRoundUpToPow2Alignment`,
`reassociateBooleanAndOr`) remain with the primary bitwise family rather than
being duplicated here. Inversion profitability uses the shared source Negator
utilities, lossless cast checks use the shared preserved-flag helpers, and
intrinsic folds use registered identities and exact APInt byte/bit reversal.
The poison-sensitive complex And/Or asymmetry is retained: the source's
Or-only complemented-Xor rewrite is not applied to its invalid And analogue.
No general boolean solver or heap proof is introduced.

Optional counters report actual integer visitor and rewrite decisions,
no-wrap/nneg updates, type-evaluation visits/cache hits/depth limits and pending
user outcomes. Disabled recording does not construct keys dynamically.
`InstCombineIntegerCastProperties.cs` is staged with the cohort and exercises
the real shared pass on narrowing/extension, carry-boundary masking, boolean
extraction, casted logic, three-input complemented patterns and funnel shifts,
including zero shift. It checks defined small-width results rather than requiring
one brittle final SSA spelling. Vector lane/shuffle transforms and debug-record
propagation are outside this scalar mapping; numerical-format queries retain the
separately owned APFloat dependency. Publication, builds and execution receipts
remain coordinator-owned.

## Unintegrated source checkpoint

The unpublished staging tree is retained separately as
`artifacts/source-snapshots/llvm-instcombine-staged.tar.gz`
(SHA-256 `d707b568770c53f65707d442cf1b6bf948081ac03b0060b5b91ed24d57f84eeb`). This is a local
source payload, not part of the product build or the default pipeline. It
preserves the staging work if disposable `.temp/` inputs are cleaned. The
versioned upstream reference and these family notes remain the provenance;
this checkpoint is not an acceptance claim.
