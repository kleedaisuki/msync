// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp visitSub 2441–3143.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitSub(LlvmBinaryOperator instruction)
        {
            Count("addsub.sub.visits");
            if (LlvmInstructionSimplify.SimplifyInstruction(instruction, Query(instruction)) is { } simple) return SubReplace(instruction, simple, "simplify");
            if (FoldVectorBinop(instruction) is { } vector) return vector;
            if (FoldBinopWithPhiOperands(instruction) is { } phi) return phi;
            LlvmValue left = instruction.Left, right = instruction.Right;
            if (DynCastNegVal(right) is { } opposite)
            {
                bool signed = right is LlvmBinaryOperator binary ? binary.HasNoSignedWrap : SubNotMinSignedValue((LlvmConstant)right);
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Add, left, opposite) { HasNoSignedWrap = signed && instruction.HasNoSignedWrap }, "subtract_negative");
            }
            if (FactorizeMathWithShlOps(instruction) is { } factored) return factored;
            if (left is LlvmConstant constant && IsAddSubImmediateConstant(constant) &&
                SubAddLike(right, out var value, out var inner) && inner is LlvmConstant innerConstant && IsAddSubImmediateConstant(innerConstant))
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Sub, LlvmConstantExpr.GetSub(constant, innerConstant), value)
                {
                    HasNoSignedWrap = instruction.HasNoSignedWrap && SubNSW(right) && WillNotOverflowSignedSub(constant, innerConstant, instruction),
                    HasNoUnsignedWrap = instruction.HasNoUnsignedWrap && SubNUW(right)
                }, "constant_minus_add");
            if (FoldSubNUWOfMaskAndZExt(instruction) is { } narrowMask) return narrowMask;
            bool negation = SubZero(left);
            bool absoluteSelect = instruction.Users.Any(user => user is LlvmSelectInstruction select &&
                (select.TrueValue == right && select.FalseValue == instruction || select.FalseValue == right && select.TrueValue == instruction));
            if ((!negation || !absoluteSelect) && Negator.Negate(negation, negation && instruction.HasNoSignedWrap, right, this) is { } negated)
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Add, negated, left), "negator");
            if (negation) return SubNarrowDeduceFlags(instruction);
            if (FoldUsingDistributiveLaws(instruction) is { } distributed) return SubReplace(instruction, distributed, "distribute");
            if (instruction.Type.GetScalarType().IsIntegerTy(1)) return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Xor, left, right), "boolean");
            if (SubOnes(left)) return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Xor, right, LlvmConstant.GetAllOnesValue(instruction.Type)), "all_ones");
            if (SubBinary(left, LlvmOpcode.Add, out var x, out var minusOne) && left.HasOneUse && SubOnes(minusOne))
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Add, Builder.CreateNot(right), x), "add_minus_one");
            if (SubFoldMaskedSubset(instruction) is { } subset) return subset;
            if (SubReassociate(instruction) is { } reassociated) return reassociated;
            if (IsFreeToInvert(left, left.HasOneUse, out bool consumesLeft) && IsFreeToInvert(right, right.HasOneUse, out bool consumesRight) && (consumesLeft || consumesRight))
            {
                var invertedLeft = GetFreelyInverted(left, left.HasOneUse, Builder)!;
                var invertedRight = GetFreelyInverted(right, right.HasOneUse, Builder)!;
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Sub, invertedRight, invertedLeft), "invert_both");
            }
            if (SubIntrinsic(left, LlvmIntrinsicKind.VectorReduceAdd, out var leftReduction) && left.HasOneUse &&
                SubIntrinsic(right, LlvmIntrinsicKind.VectorReduceAdd, out var rightReduction) && right.HasOneUse && leftReduction.GetArgument(0).Type == rightReduction.GetArgument(0).Type)
            {
                var difference = Builder.CreateSub(leftReduction.GetArgument(0), rightReduction.GetArgument(0));
                return SubReplace(instruction, Builder.CreateIntrinsic(LlvmIntrinsicKind.VectorReduceAdd, [difference], difference.Type.GetScalarType()), "vector_reduction");
            }
            if (SubConstants(instruction) is { } constantFold) return constantFold;
            if (SubBitwiseIdentities(instruction) is { } bitwise) return bitwise;
            if (TryFoldInstWithCtpopWithNot(instruction) is { } ctpop) return ctpop;
            if (FoldSubOfMinMax(instruction) is { } minmax) return minmax;
            if (SubSelectAndMinMax(instruction) is { } selects) return selects;
            if (SubPointerDifference(instruction) is { } pointer) return pointer;
            if (SubAbsoluteAndMasks(instruction) is { } absolute) return absolute;
            if (CanonicalizeCondSignextOfHighBitExtractToSignextHighBitExtract(instruction) is { } signExtract) return signExtract;
            if (SubSaturatingAndSquares(instruction) is { } saturation) return saturation;
            if (FoldBinOpOfSelectAndCastOfSelectCondition(instruction) is { } condition) return condition;
            if (SubSignExtendedAdds(instruction) is { } signExtended) return signExtended;
            if (SubBinary(left, LlvmOpcode.And, out var sum, out var andMask) && SubAPInt(andMask) is { } andConstant &&
                SubBinary(sum, LlvmOpcode.Add, out var addend, out var addMask) && addend == right && SubAPInt(addMask, true) is { } addConstant &&
                addConstant.Value.IsPowerOf2 && addConstant.Value == ~andConstant.Value)
                return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.And, right, SubConstant(instruction.Type, addConstant.Value)), "power2_mask");
            return SubNarrowDeduceFlags(instruction);
        }
        private LlvmInstruction? SubNarrowDeduceFlags(LlvmBinaryOperator instruction)
        {
            if (NarrowMathIfNoOverflow(instruction) is { } narrow) return narrow;
            bool changed = false;
            if (!instruction.HasNoSignedWrap && WillNotOverflowSignedSub(instruction.Left, instruction.Right, instruction))
            { instruction.HasNoSignedWrap = true; changed = true; Count("addsub.sub.flags.nsw"); }
            if (!instruction.HasNoUnsignedWrap && WillNotOverflowUnsignedSub(instruction.Left, instruction.Right, instruction))
            { instruction.HasNoUnsignedWrap = true; changed = true; Count("addsub.sub.flags.nuw"); }
            return changed ? instruction : null;
        }
    }
}
