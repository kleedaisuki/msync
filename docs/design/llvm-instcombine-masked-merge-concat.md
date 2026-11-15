# InstCombine masked merge and concat helpers

These two Work helpers translate the pinned LLVM revision
`a55e64432a516fd6507d3ecfba7407cfe425f940`,
`llvm/lib/Transforms/InstCombine/InstCombineAndOrXor.cpp`:

- `visitMaskedMerge`, lines 5022–5058, is
  `LlvmInstCombine.AndOrXor.MaskedMerge.cs`.
- `matchOrConcat`, lines 3237–3301, is
  `LlvmInstCombine.AndOrXor.Concat.cs`.

## Masked merge

The matcher retains all commuted forms of the outer xor, one-use and, and
inner xor with the deferred baseline operand. It first de-inverts a not mask
and swaps the final xor operand. That path does not add a one-use requirement
on the inner difference. The constant-mask path does require that difference
to have one use; it uses the actual Constant::replaceUndefsWith port to choose
all-ones for undefined elements before forming the two ands and a plain or.
No disjoint flag is invented for that returned or.

The shared LogicNot matcher must retain LLVM m_AllOnes semantics for scalar
and vector masks, including poison lanes when at least one non-poison lane
matches. It is not interchangeable with the poison-forbidden concat matcher.

## Concatenation

The helper requires an even result width, matching half-width source types,
and the three upstream one-use conditions on the zero extensions and shift.
The exact three transform families are retained:

1. Two bswaps move after packing, with the original high/low operands swapped.
2. Two bitreverses move after packing, with the same swap.
3. A sign-extended value and its arithmetic sign smear become a single sign
   extension. Each half independently permits the source sext-or-self form.

The new packing or receives the actual disjoint flag through the shared
CreateDisjointLogicOr helper. Intrinsic creation uses real registered kinds
and the existing TargetFolder builder. Shift matching uses scalar constants
or vector splats **without poison**, matching both m_APInt and m_SpecificInt;
it does not reuse a poison-permitting funnel matcher.

## Integration and telemetry

Both helpers are ordinary parts of the existing xor/or visitors and use the
shared Work, builder and source mutation return protocol. They introduce no
new pass, Range representation, opcode or execution check. Actual visitor spans
already retain function/instruction/source context. Fixed metric keys count
queries and the exact successful rewrite family; disabled recording constructs
no diagnostic dictionary or dynamic metric key.

The two coherent product files were published at the integration lead's
request. Shared vector LogicNot maintenance belongs to its named owner. No
independent build, benchmark or commit was performed for these helpers;
coordinator-owned integration execution remains required.
