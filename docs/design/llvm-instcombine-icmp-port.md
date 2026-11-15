# InstCombine integer and pointer comparisons (in progress)

Source revision: LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`.
Primary reference: `llvm/lib/Transforms/InstCombine/InstCombineCompares.cpp`.
Managed family: `LlvmInstCombine.ICmp*.cs`, using the shared InstCombine Work,
worklist, TargetFolder builder and analysis query. This is a managed source
translation, not a selection of the LLVM backend or its optimization pipeline.

## Family boundaries

| Reference symbols | Managed implementation / mechanism |
| --- | --- |
| `visitICmpInst` | `ICmp.cs`: source ordering of simplification, canonicalization, constant/binary/known-bit folds, min/max recognition, PHI distribution, pointer, cast and intrinsic folds |
| `canonicalizeCmpWithConstant`, `canonicalizeICmpPredicate`, `canonicalizeICmpBool` | `ICmp.Canonical.cs`: strictness adjustment, user-aware predicate inversion, boolean truth tables |
| `getFlippedStrictnessPredicateAndConstant` | Shared ValueTracking helper: increment/decrement boundary rules include the other signedness boundary when SameSign is present |
| `foldCmpLoadFromIndexedGlobal` | `ICmp.IndexedGlobal.cs`: actual scalar constant-load enumeration, single/double/range state machines and magic-bitvector selection |
| `decomposeLinearExpression` | `ICmp.Linear.cs`: source Loads.cpp single-variable GEP decomposition, optional multiply/shift index scaling and exact GEP nowrap-flag intersection |
| `foldGEPICmp` | `ICmp.Pointer.cs`: common-base offset emission, matching-index cancellation, constant-offset alignment-boundary comparison and indexed rewrite |
| `canRewriteGEPAsOffset`, `rewriteGEPAsOffset`, `transformToIndexedCompare` | `ICmp.PointerIndexed.cs`: bounded postorder GEP/PHI traversal and three-phase integer-PHI reconstruction, followed by pointer reconstruction for external users |
| `foldAllocaCmp` | `ICmp.Alloca.cs`: collect and fold all eligible comparisons together through the source capture tracker, preserving consistency between uses |
| `foldICmpCommutative`, `foldICmpWithHighBitMask` | `ICmp.Commutative.cs`, `ICmp.HighBitMask.cs`: source min/max, add/div/shift, saturation, mask and shifted-value folds |
| `foldICmpOfUAddOv`, `foldICmpInvariantGroup`, `UAddWithOverflow_match` | `ICmp.Intrinsic.cs`, `ICmp.OverflowMatch.cs`: actual intrinsic identities and the complete scalar source unsigned-overflow matcher |

Constant-arithmetic/bitwise compare rules (source 950–3407) are separate
`ICmp.Constant.*` partials. Other constant dispatch, general binary, equality,
cast and known-bit source families join the same Work rather than creating
another optimization pass or replacing them with an InstructionSimplify call.
Floating comparison rules after source line 8546 are owned by `FCmp.*` and share
the truth-table and indexed-global helpers.

## Data and rewrite contracts

Source visitor results retain their distinct meanings: null means no returned
replacement, the original instruction denotes an in-place change, and a new
detached instruction is installed by the central combiner. Explicit RAUW/erase
operations update the common worklist and dominance-condition cache. Nested
builder-created instructions use the actual insertion callback and TargetFolder;
no family-private worklist or global heap solver is introduced.

Pointer comparison reasoning uses the existing GEP layout and flags. The
shared CommonPointerBase and EmitGEPOffset(s) implementations are also used by
pointer subtraction and casts. Constant expressions use the real GEPOperator
view. The indexed pointer rewrite retains the source limit of 100 explored
values; it does not infer new allocation or lifetime properties. HeapAlloc's
successful-result facts are supplied by the explicit project adapter, not by
pretending HeapAlloc is an Alloca or an allocation-library call.

Constant-global load enumeration retains `MaxArraySize` (default 1024), stride
validity, modular-index masking and the source state-machine order. Legal
integer widths come from DataLayout: `GetSmallestLegalIntType` follows the
source LegalIntWidths traversal, not a guessed host width. Integer analysis
intervals here do not alter the language-level built-in Range object.

SameSign, wrapping and exactness flags must follow the specific source rule:
some transforms preserve them, others construct a new raw predicate or weaken
them. SameSign strictness changes must not cross either signedness boundary.
The core predicate-information carrier preserves the raw predicate enum's
external contract. Project ABI expectations remain on operand uses; rewritten
pointer results retain an existing instruction hint only for a newly built
GEP, never by mutating a context-uniqued constant or a folded existing base.

## Coverage and integration

Scalar integer/pointer source paths are the current workload. Vector-specific
reverse/shuffle/reduction and atomic-cmpxchg compare branches require their
actual instruction, constant and intrinsic carriers; they are not simulated by
scalar loops or always-unknown helpers. Intrinsic folds use registry identities,
not callee-name guesses. Debug/profile payload paths follow the actual metadata
representation rather than inventing records.

Detailed counters cover visitor order, strictness/inversion outcomes, constant
load elements, emitted range/bitvector forms, GEP decomposition/offset rewrites,
indexed graph work and alloca comparison replacement. Recording-disabled Work
paths keep the nullable sink lean. Builds and end-to-end source execution are
owned by the parent integration coordinator; staged files are not evidence of
an executable or performance result.
