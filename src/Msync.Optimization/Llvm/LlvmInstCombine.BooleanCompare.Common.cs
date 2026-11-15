// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// comparison builders, insertRangeTest, scalar PatternMatch adapters.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue GetNewICmpValue(uint code, bool signed, LlvmValue left, LlvmValue right)
            => LlvmCmpInstAnalysis.GetPredForICmpCode(code, signed, left.Type, out var predicate)
                ?? Builder.CreateICmp(predicate, left, right);
        private LlvmValue GetFCmpValue(uint code, LlvmValue left, LlvmValue right, LlvmFastMathFlags flags)
            => LlvmCmpInstAnalysis.GetPredForFCmpCode(code, left.Type, out var predicate)
                ?? Builder.CreateFCmp(predicate, left, right, fastMathFlags: flags);
        private static bool BoolCmpSigned(LlvmCmpPredicate predicate) => predicate is >= LlvmCmpPredicate.ICmpSgt and <= LlvmCmpPredicate.ICmpSle;
        private static bool BoolCmpEq(LlvmCmpPredicate predicate) => predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe;
        private static bool BoolCmpZero(LlvmValue value) => value is LlvmConstant { IsNullValue: true };
        private static bool BoolCmpOnes(LlvmValue value) => value is LlvmConstantInt { IsMinusOne: true };
        private static bool BoolCmpOne(LlvmValue value) => value is LlvmConstantInt { IsOne: true };
        private static LlvmConstantInt BoolCmpC(LlvmValue value, LlvmAPInt bits) => LlvmConstantInt.Get(value.Context, bits);
        private static LlvmConstantInt BoolCmpC(LlvmValue value, ulong bits) => LlvmConstantInt.Get((LlvmIntegerType)value.Type, bits);
        private LlvmValue BoolCmpRewrite(LlvmValue value, string rule)
        { Count("boolean_compare.rewrite." + rule); return value; }
        private static bool BoolCmpLike(LlvmValue value, out LlvmCmpPredicate predicate, out LlvmValue left, out LlvmValue right)
        {
            if (value is LlvmICmpInstruction compare)
            { predicate = compare.Predicate; left = compare.Left; right = compare.Right; return true; }
            if (value.Type.IsIntOrIntVectorTy(1) && value is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasNoUnsignedWrap: true } trunc)
            { predicate = LlvmCmpPredicate.ICmpNe; left = trunc.Operand; right = BoolCmpC(left, 0); return true; }
            predicate = default; left = right = null!; return false;
        }
        private bool BoolCmpDefined(LlvmValue value, LlvmInstruction? context = null) =>
            LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(value, context is null ? null : DT, context, Metrics, assumptions: context is null ? null : AC, allowEphemerals: SQ.AllowEphemerals);
        private bool BoolCmpNonPoison(LlvmValue value, LlvmInstruction? context = null) =>
            LlvmValueTracking.IsGuaranteedNotToBePoison(value, context is null ? null : DT, context, Metrics, assumptions: context is null ? null : AC, allowEphemerals: SQ.AllowEphemerals);
        private bool BoolCmpPower(LlvmValue value, bool orZero, LlvmInstruction? context) =>
            LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(value, orZero, context: context, tree: DT,
                metrics: Metrics, useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals);
        private bool BoolCmpNonZero(LlvmValue value, LlvmInstruction? context) =>
            LlvmKnownBitsAnalysis.IsKnownNonZero(value, context: context, tree: DT, metrics: Metrics,
                useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals);
        private LlvmValue InsertRangeTest(LlvmValue value, LlvmAPInt lower, LlvmAPInt upper, bool signed, bool inside)
        {
            Debug.Assert(signed ? lower.Slt(upper) : lower.Ult(upper));
            LlvmCmpPredicate predicate = inside ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpUge;
            if (signed ? lower.IsMinSignedValue : lower.IsZero)
            {
                if (signed) predicate = inside ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpSge;
                return Builder.CreateICmp(predicate, value, BoolCmpC(value, upper));
            }
            LlvmValue offset = Builder.CreateSub(value, BoolCmpC(value, lower));
            BoolCmpAppendName(offset, value, ".off"u8);
            return Builder.CreateICmp(predicate, offset, BoolCmpC(value, upper - lower));
        }
    }
}
