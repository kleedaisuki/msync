// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionCombining.cpp foldExtractOfOverflowIntrinsic.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldExtractOfOverflowIntrinsic(LlvmExtractValueInstruction extract)
        {
            if (extract.AggregateOperand is not LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } call ||
                !LlvmIntrinsic.IsOverflowIntrinsic(descriptor.Kind)) return null;
            LlvmIntrinsicKind kind = descriptor.Kind;
            LlvmValue left = call.GetArgument(0), right = call.GetArgument(1);
            LlvmAPInt? constant = right is LlvmConstantInt rhs ? rhs.Value : null;
            if (constant is { } integer && extract.Indices[0] == 0 &&
                (kind == LlvmIntrinsicKind.SMulWithOverflow || kind == LlvmIntrinsicKind.UMulWithOverflow))
            {
                if (integer.IsAllOnes)
                    return new LlvmBinaryOperator(LlvmOpcode.Sub, LlvmConstant.GetNullValue(left.Type), left);
                if (integer.IsPowerOf2)
                    return new LlvmBinaryOperator(LlvmOpcode.Shl, left,
                        LlvmConstantInt.Get(left.Context, new LlvmAPInt(integer.BitWidth, integer.GetActiveBits() - 1)));
            }
            if (!call.HasOneUse) return null;
            if (extract.Indices[0] == 0)
            {
                LlvmOpcode opcode = LlvmIntrinsic.GetOverflowOpcode(kind);
                ReplaceInstUsesWith(call, LlvmPoisonValue.Get(call.Type));
                EraseInstFromFunction(call);
                Count("aggregate.overflow.result-only");
                return new LlvmBinaryOperator(opcode, left, right);
            }
            Debug.Assert(extract.Indices[0] == 1);
            if (kind == LlvmIntrinsicKind.USubWithOverflow)
                return new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUlt, left, right);
            if (kind == LlvmIntrinsicKind.SMulWithOverflow && left.Type.IsIntOrIntVectorTy(1))
                return new LlvmBinaryOperator(LlvmOpcode.And, left, right);
            if (kind == LlvmIntrinsicKind.UMulWithOverflow && left == right)
            {
                uint width = left.Type.GetScalarSizeInBits();
                if (width % 2 == 0)
                    return new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUgt, left,
                        LlvmConstantInt.Get(left.Context, LlvmAPInt.GetLowBitsSet(width, width / 2)));
            }
            if (constant is not { } value) return null;
            LlvmConstantRange noWrap = LlvmConstantRange.MakeExactNoWrapRegion(LlvmIntrinsic.GetOverflowOpcode(kind),
                value, LlvmIntrinsic.GetOverflowNoWrapKind(kind));
            noWrap.GetEquivalentICmp(out LlvmCmpPredicate predicate, out LlvmAPInt rhsValue, out LlvmAPInt offset);
            LlvmValue lhs = left;
            if (!offset.IsZero) lhs = Builder.CreateAdd(lhs, LlvmConstantInt.Get(left.Context, offset));
            Count("aggregate.overflow.flag-only-constant");
            return new LlvmICmpInstruction(LlvmCmpInstruction.GetInversePredicate(predicate), lhs,
                LlvmConstantInt.Get(right.Context, rhsValue));
        }
    }
}
