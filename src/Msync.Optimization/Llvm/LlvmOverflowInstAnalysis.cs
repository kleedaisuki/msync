// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/OverflowInstAnalysis.cpp isCheckForZeroAndMulWithOverflow overloads.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static class LlvmOverflowInstAnalysis
{
    public static bool IsCheckForZeroAndMulWithOverflow(LlvmValue first, LlvmValue second, bool and,
        out LlvmUse? otherOperand, Dictionary<string, ulong>? metrics = null)
    {
        otherOperand = null;
        Count(metrics, "overflow.check_zero_mul.queries");
        if (first is not LlvmICmpInstruction comparison || !MatchZero(comparison.Right)) return false;
        LlvmValue input = comparison.Left;
        bool Match(LlvmValue candidate, out LlvmCallInstruction call, out int inputIndex)
        {
            call = null!; inputIndex = -1;
            if (candidate is not LlvmExtractValueInstruction { Indices.Count: 1 } extract || extract.Indices[0] != 1 ||
                extract.AggregateOperand is not LlvmCallInstruction intrinsic) return false;
            var kind = intrinsic.CalledFunction?.IntrinsicDescriptor?.Kind;
            if (kind != LlvmIntrinsicKind.UMulWithOverflow && kind != LlvmIntrinsicKind.SMulWithOverflow) return false;
            if (intrinsic.GetArgument(0) == input) inputIndex = 0;
            else if (intrinsic.GetArgument(1) == input) inputIndex = 1;
            else return false;
            call = intrinsic; return true;
        }
        LlvmCallInstruction call; int index;
        if (and)
        {
            if (comparison.Predicate != LlvmCmpPredicate.ICmpNe || !Match(second, out call, out index)) return false;
        }
        else
        {
            if (comparison.Predicate != LlvmCmpPredicate.ICmpEq || !MatchNot(second, out var operand) ||
                !Match(operand, out call, out index)) return false;
        }
        otherOperand = call.GetOperandUse(index == 0 ? 1 : 0);
        Count(metrics, "overflow.check_zero_mul.matched");
        return true;
    }
    public static bool IsCheckForZeroAndMulWithOverflow(LlvmValue first, LlvmValue second, bool and,
        Dictionary<string, ulong>? metrics = null) => IsCheckForZeroAndMulWithOverflow(first, second, and, out _, metrics);
    private static bool MatchZero(LlvmValue value)
        => value is LlvmConstant { IsNullValue: true } || value.Type.IsVectorTy() &&
            value is LlvmConstant constant && constant.GetSplatValue(allowPoison: true) is LlvmConstantInt { IsZero: true };
    private static bool MatchNot(LlvmValue value, out LlvmValue operand)
    {
        bool Ones(LlvmValue candidate) => candidate is LlvmConstantInt { IsMinusOne: true } ||
            candidate.Type.IsVectorTy() && candidate is LlvmConstant constant &&
                constant.GetSplatValue(allowPoison: true) is LlvmConstantInt { IsMinusOne: true };
        if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor)
        {
            if (Ones(xor.Left)) { operand = xor.Right; return true; }
            if (Ones(xor.Right)) { operand = xor.Left; return true; }
        }
        operand = null!; return false;
    }
    private static void Count(Dictionary<string, ulong>? metrics, string key)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
}
