# InstCombine Select saturation, overflow and bit-ceil

Pinned source: LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`,
`lib/Transforms/InstCombine/InstCombineSelect.cpp`.
These helpers are staged as members of the central `LlvmInstCombine.Work`, not a
new pass, backend or native-width execution gate.

| Managed staged file | Exact source function boundaries |
| --- | --- |
| `LlvmInstCombine.Select.Saturation.Unsigned.cs` | `canonicalizeSaturatedSubtractUnsigned` (L1149), `canonicalizeSaturatedAddUnsigned` (L1258) |
| `.Select.Saturation.Signed.cs` | `canonicalizeSaturatedSubtractSigned` (L1216), `canonicalizeSaturatedSubtract` (L1242), `canonicalizeSaturatedAddSigned` (L1359), `canonicalizeSaturatedAdd` (L1478) |
| `.Select.Saturation.AbsDiff.cs` | `foldAbsDiff` (L1493) |
| `.Select.Saturation.Overflow.cs` | `foldOverflowingAddSubSelect` (L2685) |
| `.Select.Saturation.BitCeil.cs` | `isSafeToRemoveBitCeilSelect` (L4148), `foldBitCeil` (L4243) |

## Mechanisms preserved

The saturation matchers retain the upstream normalization of select arms and
comparison direction, strict/non-strict threshold cases, constant exceptions
(all-ones/zero), signed minimum/maximum thresholds and NSW subtract requirements.
The unsigned subtract negation case retains the source one-use profitability
condition; saturated addition retains the comparison one-use requirement.
Scalar APInt matchers do not treat poison as a literal. Source AllowPoison
matcher variants differ only for vector splats, which are not represented here.
Constants and arithmetic retain their full declared integer bit width.

Absolute-difference folding has all five source forms. The first form mutates
the surviving subtract's flags exactly as source: clear NUW and only introduce
NSW if it has one use. It creates `llvm.abs` with the source minimum-integer
poison argument, not an invented branch or host absolute-value operation.

Overflow-select folding identifies the *registered intrinsic descriptor* behind
the pair of extractvalue indices 1 and 0. It retains every source signed clamp
comparison/constant case, then creates the corresponding standard saturating
intrinsic. Ordinary functions with similar names are not recognized. The
integer-intrinsic registry owns actual `{iN,i1}` overflow signatures and IDs.

Bit-ceil removal uses the actual source ConstantRange algorithm: construct the
false-condition range; follow at most one parent backward and one operation
forward through the common ancestor; transfer addition/subtraction/complement;
then evaluate the final unsigned range predicate. This is a small source-local
symbolic computation, not a new general proof engine. The source power-of-two
*type-bit-width* requirement remains; it is not a target Native width gate.
Source `APInt::getSignMask` uses the existing equal-valued managed
`GetSignedMinValue` constructor. Only the matched arithmetic's nowrap flags are
dropped, and the actual CTLZ's poison-generating annotations are removed and the
call queued before building the negated, masked shift count.

## Integration and diagnostics

Shared Select patterns, flipped-strictness helper, Core flag operations,
ConstantRange, TargetFolder builder and integer intrinsic registry are explicit
source dependencies. This leaf introduces no runtime execution or source
language feature. Ordinary Range objects and file operations have no special
handling. New result annotation inheritance stays in the central replacement
path; globally uniqued constants are not retagged.

Detailed metrics count actual candidates, exact successful pattern variants,
source one-use rejections, flag changes, common-ancestor matching, range-result
outcomes and concrete rewrites. The existing Work metric sink owns function and
source context; disabled recording does not allocate a family metric object.

For a real journey, carry a source comparison/add/sub/select pattern through the
new IR to the existing target and compare both output and the exact rewrite
reason. For bit-ceil and overflow, exercise the source clamp boundary as well as
an ordinary interior value. The parent owns integrated builds and end-to-end
execution; these files remain staged until the entire central cohort is coherent.
