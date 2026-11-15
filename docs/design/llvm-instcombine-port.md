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

The pass is published in `src/Msync.Optimization/Llvm` and connected to the
default O2 pipeline in `src/Msync.Compiler/Compiler.Llvm.cs`, after SCCP/DCE and
before ADCE/DSE. The CLI product builds successfully. Publication includes the
real driver, worklist, represented visitor families and shared-query closures;
it no longer depends on compiling a disposable staging tree. The focused real
source journeys below pass; Native AOT and performance acceptance remain
pending the coordinated root runs.

This is a source port into msync's IR and custom Native backend, not adoption of
LLVM's backend or pass pipeline. Standard vector IR carriers and selected source
vector helpers are present, but complete vector analysis/visitor coverage and a
Native SIMD backend are **not** claimed. In particular, the main ICmp visitor
retains its scalar boundary while the broader demanded-vector query closure is
not integrated. HeapAlloc, FileIn and FileOut remain the only project instruction
extensions; Range remains an ordinary object.

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

InstCombine uses **TargetFolder**, not ConstantFolder, while retaining existing
builder constructor defaults for other passes. The insertion callback queues inserted instructions,
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

The coordinated CLI product build completed with zero warnings/errors
(`artifacts/diagnostics/llvm-instcombine/build-sixth.log`). The LlvmIr harness
also builds successfully (`artifacts/diagnostics/llvm-instcombine/tests-build.log`)
after adapting its old null-AA calls to actual AA/DT contexts. These are managed
compilation receipts, not Native AOT, end-to-end or benchmark acceptance.

Root reports passing real source journeys: GEP (16 executions), scope/branch/
capture (six executions), the O2 middle-end journey and Range initialization.
Native ordinary initialization also passes. The GEP journey includes target-side
packing of scalar Function stores; the source transform was not disabled to
avoid that backend boundary. These results do not imply complete property-group
coverage or a speedup: the integer-cast structural assertion remains under
investigation, and benchmark/Native AOT acceptance remains pending.

Telemetry records actual preparation, deferred/worklist operations, visitation,
constant folds, source rewrite decisions, removals, use-count changes and outer
iterations with function/block/source context. The disabled path does not
allocate a metrics dictionary or per-visit attribute map. Validation will use
focused real compiler/source/artifact journeys and discriminating instruction
counts, not a new quality gate or a count of staged files.

## Scalar cast family and shared cast semantics

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

The numerical-format dependency is the published IEEE half, bfloat,
single and double APFloat implementation; extended multiword formats remain
an explicitly unimplemented dependency, not a double-precision substitute.
Vector resize/shuffle/lane casts are outside this scalar tranche. The represented
`vscale`, integer `abs` and `ptrmask` intrinsic branches use their actual typed
source declarations; vscale is not replaced by a guessed constant scale. No always-null helper is
provided for any represented scalar transform. Telemetry counts actual common
queries, cast-pair rewrites, rebuilt expression nodes/cache hits, integer-width
normalization, GEP/PHI work, exactness queries and the concrete floating rewrite
families. The managed product build includes this family; real execution and
performance acceptance follow the coordinated journeys described above.

## Integer cast visitors and adjacent bitwise cast/shift helpers

The represented scalar integer-cast source cohort is published alongside the
driver in `src/Msync.Optimization/Llvm` and shares its `Work` context with all
other visitor families. Its pinned `InstCombineCasts.cpp` mapping is:

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
`tests/LlvmIr/InstCombineIntegerCastProperties.cs` exercises
the real shared pass on narrowing/extension, carry-boundary masking, boolean
extraction, casted logic, three-input complemented patterns and funnel shifts,
including zero shift. It checks defined small-width results rather than requiring
one brittle final SSA spelling. Vector lane/shuffle transforms and debug-record
propagation are outside this scalar mapping; numerical-format queries retain the
published APFloat dependency. Execution receipts remain coordinator-owned.

## Historical staging checkpoint

The pre-integration staging tree is retained separately as
`artifacts/source-snapshots/llvm-instcombine-staged.tar.gz`
(SHA-256 `d707b568770c53f65707d442cf1b6bf948081ac03b0060b5b91ed24d57f84eeb`). This is a local
historical source payload, not a product build input or a snapshot of the current
implementation. The published source under `src/` is authoritative; integration
repairs and shared-dependency merges postdate this archive. The versioned
upstream reference and family notes retain the source provenance independently
of disposable `.temp/` inputs.

## Integer subtraction visitor integration tranche

The `LlvmInstCombine.AddSub.Sub*.cs` family translates pinned
`InstCombineAddSub.cpp:2441–3143`, with the adjacent min/max and NUW-mask helpers
at 2371–2440. It retains source visitation order, including the early zero-minus
Negator path, flag-deduction timing, shared distributive and select/PHI helpers,
freely invertible operands' consumption requirement, and the final narrowing
query. This is the actual visitor, not a replacement table of a few subtraction
identities.

The family includes constant and AddLike reassociation (including disjoint OR),
bitwise arithmetic identities, min/max and unsigned-saturation relations,
select sinking, ctpop inversion, difference-of-squares factoring, signed-extension
cancellation and source instruction-tradeoff counting. Pointer subtraction uses
the shared CommonPointerBase/GEP-offset routines and distinguishes direct,
truncated and zero-extended pointer casts; it does not infer a new heap identity.
The vector-reduce-add rule forms a vector difference and the actual standard
reduction intrinsic, with the vector element as its scalar result type.

Pattern adapters preserve the source distinctions between plain `m_APInt`
(no poison lanes), predicate-bound splats (poison lanes allowed), SExtLike
(sext or nneg zext), and AddLike (add or disjoint OR). Constant element-wise
equality uses the source bitcast-and-compare rule, including poison results,
rather than managed reference equality alone. The shared `DynCastNegVal` helper
translates `InstructionCombining.cpp:1565–1601`, including fixed-vector lane
checks and integer splats, for all source visitor consumers.

Telemetry records visitor work, concrete rewrite identities, inferred NSW/NUW
flags and the old/new instruction counts used by the signed-extension transform.
Disabled detailed recording does not construct rewrite metric names. This family
is published and included in the successful managed product build; actual
execution and performance results are recorded by the integration coordinator.

The additional `AndOrXor.Arithmetic.cs` tranche supplies the actual
`foldXorToXor`, `canonicalizeAbs`,
`canonicalizeConditionalNegationViaMathToSelect` and
`foldMaskedAddXorPattern` routines from the same pinned source (4822–4875,
5088–5121, 1677–1691 and 5455–5475). It retains each routine's different
commutation policy, exact one-use/two-use conditions, poison-aware constant
matching, and NSW/NUW transfer to newly formed negation/subtraction. Queries and
successful rule identities use the existing detailed sink with no dynamic metric
name construction when recording is disabled. This fills existing visitor
calls; it does not introduce a second pass or four unconditional rewrites.

## Publication and analysis ownership

The driver API takes the actual function AA, assumption cache, target-library
info, generic TTI and forward dominators. It uses TargetFolder and the source
InstructionWorklist; it is not a wrapper around the old InstSimplify pass. The
compiler selects the O2 placement described above and owns analysis reuse. The upstream
pass preserves CFG analyses (`InstructionCombining.cpp:6396,6401`), so this
entry does not require rebuilding a supplied dominator tree merely because it
changed instructions. Source extension operations HeapAlloc/FileIn/FileOut
remain in the worklist/effect/dead-code machinery without invented visitors.

Publication deliberately selected files rather than copying every staging
`.cs`: Core ConstantRange/APInt ownership has already moved, duplicate rounding
partials existed, and staged shared-query overlays predated live CondContext,
vector, ConstantExpr and lazy-cache improvements. Those current implementations
are retained and only actual missing APIs are merged. The staged ICmp dispatch
name is aligned to its real entry, and a duplicated FP sign-strip helper is
unified with the source `InstCombiner.h` implementation.

Published entry/family closures include Add/Sub/FP Add and GEP; vector
helpers use real standard intrinsic descriptors rather than ordinary function
name guesses. Exact floating sign-consumer queries are shared ValueTracking
helpers. The builder's constant-folding return contract is retained: an
intrinsic creation can return a Constant, so adapters do not force it back to
a CallInstruction or create a temporary instruction to infer a declaration.

Focused runner entries: `--instcombine-only` exercises actual source worklist
and arithmetic/memory/GEP/vector transformations. It reports each property
group's result or exception, then fails if any group failed, without skipping
independent groups after the first failure. `--instcombine-source-journey` and
`--instcombine-gep-journey` lower real source and execute direct plus Native2
roundtrips; their passing execution counts are recorded above. Final performance
receipts remain separate from these focused correctness results.

The select-arm conditional KnownBits fold is now the pinned source's final
`visitSelectInst` block (`InstCombineSelect.cpp:5465–5498`) rather than an
unresolved helper call. Its affected-operand walk follows source
`hasAffectedValue` (4466), including the shared depth-six limit and PHI handling;
it avoids querying arms unaffected by the select condition. The query preserves
CondContext, assumptions, dominators, layout and instruction context, and sends
actual operand replacement through the worklist. Its scalar restriction is the
upstream lane-crossing restriction. Telemetry distinguishes affected-operand
work, actual arm queries, no affected values, and true/false arm rewrites.

## Integrated execution and timing result

The actual source families and default O2 selection are now integrated with the
compact Native target's Function memory selection. Final linux-x64 AOT midend
O0/O2 source/object/bound and Range six-route/33-file journeys passed. Function
SSA, builtin records (including enumerated Function values), and IEEE predicate
execution are covered by the focused receipts. The seven-application matched
benchmark is complete; results are mixed, with real added compilation cost, not
a claim that every source route became faster. See
[`llvm-performance-regression-review.md`](llvm-performance-regression-review.md#final-integrated-result)
for payload identities, exact route results, work counts and portable receipts.
The separately documented vector-query scope remains unchanged.
