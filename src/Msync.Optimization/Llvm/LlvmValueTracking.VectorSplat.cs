// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/VectorUtils.cpp getSplatValue; PatternMatch.h m_ZeroMask.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static bool IsSplatValue(LlvmValue value, int index = -1, uint depth = 0,
        Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(depth <= MaxAnalysisRecursionDepth);
        Count(metrics, "value_tracking.is_splat.queries");
        if (value.Type.IsVectorTy())
        {
            if (value is LlvmUndefValue) return true;
            if (value is LlvmConstant constant) return constant.GetSplatValue() is not null;
        }
        if (value is LlvmShuffleVectorInstruction shuffle)
        {
            for (int element = 1; element < shuffle.ShuffleMask.Count; ++element)
                if (shuffle.ShuffleMask[element] != shuffle.ShuffleMask[0]) return false;
            return index == -1 || shuffle.GetMaskValue(index) == index;
        }
        if (depth++ == MaxAnalysisRecursionDepth)
        {
            Count(metrics, "value_tracking.is_splat.depth");
            return false;
        }
        if (value is LlvmBinaryOperator binary)
            return IsSplatValue(binary.Left, index, depth, metrics) && IsSplatValue(binary.Right, index, depth, metrics);
        if (value is LlvmConstantExpr { Opcode: >= LlvmOpcode.Add and <= LlvmOpcode.Xor } expression)
            return IsSplatValue(expression.GetConstantOperand(0), index, depth, metrics) &&
                IsSplatValue(expression.GetConstantOperand(1), index, depth, metrics);
        if (value is LlvmSelectInstruction select)
            return IsSplatValue(select.Condition, index, depth, metrics) && IsSplatValue(select.TrueValue, index, depth, metrics) &&
                IsSplatValue(select.FalseValue, index, depth, metrics);
        return false;
    }

    public static LlvmValue? GetSplatValue(LlvmValue value, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "value_tracking.splat.queries");
        if (value.Type.IsVectorTy() && value is LlvmConstant constant)
        {
            LlvmConstant? result = constant.GetSplatValue();
            Count(metrics, result is null ? "value_tracking.splat.constant_not_splat" : "value_tracking.splat.constant");
            return result;
        }
        if (value is LlvmShuffleVectorInstruction { Left: LlvmInsertElementInstruction { IndexOperand: LlvmConstantInt { IsZero: true } } insert } shuffle &&
            shuffle.ShuffleMask.All(index => index is 0 or -1))
        {
            Count(metrics, "value_tracking.splat.broadcast");
            return insert.InsertedElementOperand;
        }
        Count(metrics, "value_tracking.splat.not_splat");
        return null;
    }
}
