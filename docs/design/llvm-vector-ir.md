# Standard LLVM vector IR carriers

## Pinned source and scope

The carrier and mask utilities translate LLVM commit
[`a55e64432a516fd6507d3ecfba7407cfe425f940`](https://github.com/llvm/llvm-project/tree/a55e64432a516fd6507d3ecfba7407cfe425f940):

- `llvm/include/llvm/IR/Instructions.h` and `llvm/lib/IR/Instructions.cpp`:
  `ExtractElementInst`, `InsertElementInst`, `ShuffleVectorInst`, clone methods,
  mask conversion/classification, commuting, interleave and bit-rotation queries.
- `llvm/include/llvm/IR/Constants.h`, `llvm/lib/IR/Constants.cpp` and
  `lib/IR/IROptions.td`: vector constant factories, scalar splats, typed numeric
  payloads, canonicalization and operand-change handling.
- `llvm/include/llvm/IR/IRBuilder.h`, `llvm/lib/IR/IRBuilder.cpp` and
  `ConstantFolder.h`: vector builder entry points and insert/shuffle splat
  construction. TargetFolder is the existing source strategy, not a second
  vector simplification algorithm.
- `llvm/include/llvm/IR/Dominators.h` and `llvm/lib/IR/Dominators.cpp`:
  source block-edge/use dominance overloads required by SSA transformations.

These are the existing standard opcode IDs 63–65, not project extensions.
HeapAlloc, FileIn and FileOut remain the only project extension instructions.
Vectors are compiler IR values, not a reinterpretation of the language's Range
object or an assumption that source Number has an integer machine ABI.

## State and operand ownership

Extraction and insertion retain their index as a real SSA operand. Index
validity is not converted into an admission check: the source permits an
out-of-range operation, whose constant folding can produce poison.

A shuffle has exactly two SSA operands. Its mask is owned integer data, with the
source poison sentinel `-1`, and a corresponding canonical constant mask for
bitcode/text rendering. Mask mutation and cloning do not share mutable arrays.
Commuting remaps lanes and swaps the actual operand Uses, including their
project per-Use ABI annotations. Instruction equivalence compares the mask as
special state; two equal operand pairs with different masks are not equal.

Scalable masks retain the upstream currently permitted zero/poison forms. No
fixed-lane or `vscale == 1` assumption is introduced by these carriers.

## Constants and source defaults

`LlvmConstantVector` uses the same aggregate interning key mechanism as existing
array/struct constants, with its own context table. Its canonical factory
preserves upstream ordering: pointer-null splat, zero, poison, undef, native FP
splat, configured native integer splat, native Byte splat, and compatible typed
numeric data. Ordinary residual vector aggregates retain real constant Uses.

`LlvmConstantDataVector` stores numeric lanes in the source host-endian raw
constant representation. It is distinct from an array, keeps its actual vector
and lane types, and has no incoming operand-use list, just like the existing
constant-data representation. Decoding uses the lane's APInt/APFloat semantics.
An all-zero raw payload canonicalizes to aggregate zero.

The source temporary `use-constant-int-for-fixed-length-splat` and
`use-constant-int-for-scalable-splat` options both default to **false**. The
corresponding context properties keep these defaults. Explicit source native
Int/Byte/FP splat factories are separately uniqued by element count and scalar
bits. A nontrivial scalable integer splat under default options uses the actual
source constant-expression insert/shuffle construction. It is not stored as a
fake fixed vector or a host scalar.

Vector operand replacement first runs the actual canonical factory, then merges
an existing uniqued value or updates the original table entry in place. All
references are dropped and the existing value-handle deletion notification is
used when a canonical replacement consumes the old aggregate. Existing array
and struct paths are retained.

## Builder and analysis integration

`LlvmTransformBuilder.Vectors.cs` provides extract, insert, shuffle and vector
splat entry points. ConstantFolder uses the source ConstantExpr factories;
TargetFolder uses its actual DataLayout-aware folding strategy; NoFolder emits
the standard instructions even for constant inputs. Splat construction inserts
one scalar at lane zero of a poison vector, then applies the zero shuffle mask.
Builder counters distinguish the three operation families, folded results and
actual instruction insertion, under the calling transform's trace ownership.
The IR carrier classes do not add their own trace sink.

`LlvmBasicBlockEdge` and the new dominator overloads preserve incoming-edge PHI
uses. Block-to-Use dominance for an ordinary instruction requires proper block
dominance, while the PHI case queries its incoming predecessor. Value-to-Use
queries retain source reachability, same-block ordering and invoke-edge rules.
Edge-to-edge dominance is equality or dominance of the latter edge's start.

## Focused checks and target boundary

`VectorIrProperties` covers real SSA Uses and RAUW, clone isolation, mask-aware
equivalence, standard text forms, constant interning/canonical collisions,
scalable/default and explicit-native splats, and both builder folding strategies
plus NoFolder. All valid same-length masks for one through three lanes are
commuted twice and compared lane by lane. Specific source examples cover
subvector insertion/extraction, replication, transpose, interleave,
deinterleave and bit rotations. The coordinator owns builds and execution; no
independent MSBuild is launched by this component owner.

These carrier checks do not claim a Native packed-vector execution ABI. That is
an explicit separate target-lowering implementation. ValueTracking demanded-lane
analysis and optimizer vector visitors likewise need their own represented
source paths; adding a carrier does not replace those algorithms with scalar
fallbacks.

## Publication state

The carrier/constant/builder files described above are now published under
`src/Msync.Core/Llvm/` and `src/Msync.Optimization/Llvm/`. Existing aggregate,
integer-constant, instruction-equivalence and instruction-writer files were
merged against the exact recorded pre-publication hashes; the format-aware
numeric tranche and typed ConstantFP implementation were already identical to
its publication recipe. `VectorIrProperties.cs` and
`ApFloatFormatProperties.cs` are present in the shared `tests/LlvmIr/` project,
without modifying its runner or starting a competing build. The coordinator
must run them with the TargetFolder builder strategy integrated. This
publication is not evidence of target vector execution or full optimizer
vector-family coverage.

### Constant element queries for instruction combination

`LlvmConstant.ElementQueries.cs` now supplies the pinned `Constants.cpp`
`containsUndefOrPoisonElement`, `containsPoisonElement`, `containsUndefElement`,
`containsConstantExpression`, `getAllOnesValue`, `replaceUndefsWith`, and typed
`ConstantInt::getTrue/getFalse/getBool` entry points. The traversal prunes
ConstantData and only walks fixed-vector/struct/array elements, exactly as the
source does; scalar undef is not an aggregate-element result. The undef matcher
also accepts nested aggregates containing nothing except undef/poison, using
the source worklist and seen-set algorithm from `lib/IR/PatternMatch.cpp`.
Replacement preserves the source restriction to fixed vectors after the whole
constant match. Floating all-ones construction uses raw APInt bits in the
actual format, not numeric `-1`. `ConstantElementQueryProperties.Run()` covers
nested aggregate matching, poison/undef distinction, partial replacement,
scalable whole-undef handling, typed Boolean splats and all four IEEE formats.
These pure factories/queries use caller-owned transform telemetry; they do not
install a new trace sink or global analysis cache.
