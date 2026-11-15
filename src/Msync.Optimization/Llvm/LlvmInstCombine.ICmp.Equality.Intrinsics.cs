// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// foldICmpIntrinsicWithIntrinsic (4074–4130), foldICmpPow2Test (6218–6279).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpIntrinsicWithIntrinsic(LlvmICmpInstruction instruction)
        {
            if (instruction.Left is not LlvmCallInstruction left || instruction.Right is not LlvmCallInstruction right ||
                left.CalledFunction?.IntrinsicDescriptor is not { } descriptor ||
                right.CalledFunction?.IntrinsicDescriptor?.Kind != descriptor.Kind) return null;
            Count("icmp.equality.intrinsic.attempts");
            var kind = descriptor.Kind;
            if (kind == LlvmIntrinsicKind.BSwap || kind == LlvmIntrinsicKind.BitReverse)
                return ICmpEqualityRewrite(instruction.Predicate, left.GetArgument(0), right.GetArgument(0), "bit_permutation");
            if ((kind != LlvmIntrinsicKind.FShl && kind != LlvmIntrinsicKind.FShr) ||
                left.GetArgument(0) != left.GetArgument(1) || right.GetArgument(0) != right.GetArgument(1)) return null;
            var leftAmount = left.GetArgument(2); var rightAmount = right.GetArgument(2);
            if (leftAmount == rightAmount)
                return ICmpEqualityRewrite(instruction.Predicate, left.GetArgument(0), right.GetArgument(0), "rotate_cancel");
            int oneUses = (left.HasOneUse ? 1 : 0) + (right.HasOneUse ? 1 : 0);
            uint width = left.Type.GetScalarSizeInBits();
            if ((oneUses == 2 || oneUses == 1 && ICmpImmediate(leftAmount) && ICmpImmediate(rightAmount)) && (width & (width - 1)) == 0)
            {
                var amount = Builder.CreateSub(leftAmount, rightAmount);
                var combined = Builder.CreateIntrinsic(kind, [left.GetArgument(0), left.GetArgument(0), amount]);
                return ICmpEqualityRewrite(instruction.Predicate, right.GetArgument(0), combined, "rotate_difference");
            }
            return null;
        }
        private static bool ICmpIsDecrementOf(LlvmValue value, LlvmValue a) => value is LlvmBinaryOperator
            { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { IsMinusOne: true } } add && add.Left == a;
        private static bool ICmpBinaryDecrement(LlvmValue value, LlvmOpcode opcode, LlvmValue a) => value is LlvmBinaryOperator
            { HasOneUse: true } binary && binary.Opcode == opcode &&
            (binary.Left == a && ICmpIsDecrementOf(binary.Right, a) || binary.Right == a && ICmpIsDecrementOf(binary.Left, a));
        private static bool ICmpIsNegated(LlvmValue value, LlvmValue a) => value is LlvmBinaryOperator
            { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt { IsZero: true } } neg && neg.Right == a;
        private static bool ICmpAndNegated(LlvmValue value, LlvmValue a) => value is LlvmBinaryOperator
            { Opcode: LlvmOpcode.And, HasOneUse: true } and &&
            (and.Left == a && ICmpIsNegated(and.Right, a) || and.Right == a && ICmpIsNegated(and.Left, a));
        private LlvmInstruction? FoldICmpPow2Test(LlvmICmpInstruction instruction)
        {
            Count("icmp.pow2_test.attempts");
            var op0 = instruction.Left; var op1 = instruction.Right; var predicate = instruction.Predicate;
            LlvmValue? a = null; bool checkIs = false;
            if (ICmpIsEquality(predicate))
            {
                if (op1 is LlvmConstantInt { IsZero: true } && op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } and)
                {
                    if (ICmpIsDecrementOf(and.Left, and.Right)) a = and.Right;
                    else if (ICmpIsDecrementOf(and.Right, and.Left)) a = and.Left;
                }
                if (ICmpAndNegated(op0, op1)) a = op1;
                else if (ICmpAndNegated(op1, op0)) a = op0;
                checkIs = predicate == LlvmCmpPredicate.ICmpEq;
            }
            else if (ICmpIsUnsigned(predicate))
            {
                if (predicate is LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUlt && ICmpBinaryDecrement(op0, LlvmOpcode.Xor, op1))
                { a = op1; checkIs = predicate == LlvmCmpPredicate.ICmpUge; }
                else if (predicate is LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUle && ICmpBinaryDecrement(op1, LlvmOpcode.Xor, op0))
                { a = op0; checkIs = predicate == LlvmCmpPredicate.ICmpUle; }
            }
            if (a is null) return null;
            Count("icmp.pow2_test.ctpop_rewrite");
            return new LlvmICmpInstruction(checkIs ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpUgt,
                Builder.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [a]), LlvmConstantInt.Get((LlvmIntegerType)a.Type, checkIs ? 2UL : 1UL));
        }
    }
}
