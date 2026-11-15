// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSelect.cpp foldICmpUSubSatWithAndForMostSignificantBitCmp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpUSubSatWithAndForMostSignificantBitCmp(LlvmSelectInstruction select, LlvmICmpInstruction compare)
        {
            if (!select.HasOneUse || !compare.HasOneUse || !SelectIsZero(compare.Right) ||
                compare.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or } or || !or.HasOneUse) return null;
            bool Match(LlvmValue value, out LlvmValue operand, out LlvmAPInt constant)
            {
                operand = value; constant = default;
                if (value is not LlvmCallInstruction call || !call.HasOneUse || call.CalledFunction?.IntrinsicDescriptor?.Kind != LlvmIntrinsicKind.USubSat ||
                    call.GetArgument(1) is not LlvmConstantInt ci) return false;
                operand = call.GetArgument(0); constant = ci.Value; return true;
            }
            if (!Match(or.Left, out LlvmValue a, out LlvmAPInt c1) || !Match(or.Right, out LlvmValue b, out LlvmAPInt c2)) return null;
            bool IsSignMask(LlvmValue value) => value is LlvmConstantInt ci && ci.Value.IsMinSignedValue;
            if (!((compare.Predicate == LlvmCmpPredicate.ICmpEq && SelectIsZero(select.TrueValue) && IsSignMask(select.FalseValue)) ||
                (compare.Predicate == LlvmCmpPredicate.ICmpNe && IsSignMask(select.TrueValue) && SelectIsZero(select.FalseValue)))) return null;
            if (!c1.IsNegative || !c2.IsNegative) return null;
            LlvmAPInt signMask = LlvmAPInt.GetSignedMinValue(c1.BitWidth), one = new(c1.BitWidth, 1, implicitTrunc: true);
            LlvmValue adjustedA = Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.USubSat, a, LlvmConstantInt.Get((LlvmIntegerType)a.Type, c1 - signMask + one));
            LlvmValue adjustedB = Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.USubSat, b, LlvmConstantInt.Get((LlvmIntegerType)a.Type, c2 - signMask + one));
            return new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateOr(adjustedA, adjustedB), LlvmConstantInt.Get((LlvmIntegerType)a.Type, signMask));
        }
    }
}
