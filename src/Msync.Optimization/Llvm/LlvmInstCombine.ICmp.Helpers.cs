// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Instructions.cpp
// ICmp predicate utilities; InstCombineCompares.cpp hasBranchUse/isSignTest.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool ICmpIsEquality(LlvmCmpPredicate predicate) => predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe;
        private static bool ICmpIsSigned(LlvmCmpPredicate predicate) => predicate is >= LlvmCmpPredicate.ICmpSgt and <= LlvmCmpPredicate.ICmpSle;
        private static bool ICmpIsUnsigned(LlvmCmpPredicate predicate) => predicate is >= LlvmCmpPredicate.ICmpUgt and <= LlvmCmpPredicate.ICmpUle;
        private static LlvmCmpPredicate ICmpUnsignedPredicate(LlvmCmpPredicate predicate) => ICmpIsSigned(predicate) ? (LlvmCmpPredicate)((byte)predicate - 4) : predicate;
        private static LlvmCmpPredicate ICmpSignedPredicate(LlvmCmpPredicate predicate) => ICmpIsUnsigned(predicate) ? (LlvmCmpPredicate)((byte)predicate + 4) : predicate;
        private static bool ICmpTrueWhenEqual(LlvmCmpPredicate predicate) => predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpUge
            or LlvmCmpPredicate.ICmpUle or LlvmCmpPredicate.ICmpSge or LlvmCmpPredicate.ICmpSle;
        private static LlvmConstant ICmpZero(LlvmValue value) => LlvmConstant.GetNullValue(value.Type);
        private static LlvmConstant ICmpOnes(LlvmValue value) => LlvmConstant.GetAllOnesValue(value.Type);
        private static LlvmValue? ICmpMatchNot(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } binary
            ? ICmpConstantMatches(binary.Right, static bits => bits.IsAllOnes) ? binary.Left : ICmpConstantMatches(binary.Left, static bits => bits.IsAllOnes) ? binary.Right : null : null;
        // PatternMatch cstval_pred_ty: poison lanes do not prevent a match, but
        // at least one defined lane must satisfy the predicate.
        private static bool ICmpConstantMatches(LlvmValue value, Func<LlvmAPInt, bool> predicate)
        {
            if (value is not LlvmConstant constant || constant is LlvmUndefValue) return false;
            if (constant is LlvmConstantInt integer) return predicate(integer.Value);
            if (constant.Type is not LlvmVectorType vector) return false;
            if (constant.GetSplatValue(true) is LlvmConstantInt splat) return predicate(splat.Value);
            if (vector is not LlvmFixedVectorType fixedVector) return false;
            bool defined = false;
            for (uint lane = 0; lane < fixedVector.NumElements; ++lane)
            {
                var element = constant.GetAggregateElement(lane);
                if (element is LlvmPoisonValue) continue;
                if (element is not LlvmConstantInt laneInteger || !predicate(laneInteger.Value)) return false;
                defined = true;
            }
            return defined;
        }

        private static LlvmConstantInt? ICmpAPInt(LlvmValue value, bool allowPoison = false) => value is LlvmConstantInt integer
            ? integer : value is LlvmConstant { Type: LlvmVectorType } constant ? constant.GetSplatValue(allowPoison) as LlvmConstantInt : null;

        private static bool ICmpHasBranchUse(LlvmICmpInstruction compare) => compare.Users.Any(user => user is LlvmCondBrInstruction);
        private static bool ICmpIsSignTest(ref LlvmCmpPredicate predicate, LlvmAPInt constant)
        {
            if (!ICmpIsSigned(predicate)) return false;
            if (constant.IsZero) return true;
            if (constant.IsOne && predicate == LlvmCmpPredicate.ICmpSlt) { predicate = LlvmCmpPredicate.ICmpSle; return true; }
            if (constant.IsAllOnes && predicate == LlvmCmpPredicate.ICmpSgt) { predicate = LlvmCmpPredicate.ICmpSge; return true; }
            return false;
        }
    }
}
