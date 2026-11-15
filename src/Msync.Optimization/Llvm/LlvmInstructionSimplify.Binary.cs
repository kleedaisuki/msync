// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp simplifyBinOp and foldOrCommuteConstant.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    internal readonly record struct BinaryQuery(LlvmDataLayout Layout, LlvmInstruction? Context,
        Dictionary<string, ulong>? Metrics, LlvmDominatorTree? Tree = null,
        bool UseInstructionInfo = true, bool CanUseUndef = true,
        LlvmAssumptionCache? Assumptions = null, bool AllowEphemerals = false,
        LlvmDomConditionCache? DC = null, LlvmTargetLibraryInfo? TLI = null,
        LlvmCondContext? CondContext = null)
    {
        internal BinaryQuery WithCondContext(LlvmCondContext context) => this with { CondContext = context };
        internal BinaryQuery WithoutCondContext() => this with { CondContext = null };
        internal bool IsUndef(LlvmValue value) => CanUseUndef && value is LlvmUndefValue;
    }

    public static LlvmValue? SimplifyBinOp(LlvmOpcode opcode, LlvmValue left, LlvmValue right,
        LlvmDataLayout layout, LlvmInstruction? context = null, Dictionary<string, ulong>? metrics = null,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false) =>
        SimplifyBinOp(opcode, left, right, LlvmFastMathFlags.None, layout, context, metrics, assumptions, allowEphemerals);

    public static LlvmValue? SimplifyBinOp(LlvmOpcode opcode, LlvmValue left, LlvmValue right,
        LlvmFastMathFlags flags, LlvmDataLayout layout, LlvmInstruction? context = null,
        Dictionary<string, ulong>? metrics = null,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false) =>
        SimplifyBinary(opcode, left, right, flags, new(layout, context, metrics, Assumptions: assumptions, AllowEphemerals: allowEphemerals), 3);

    private static LlvmValue? SimplifyBinary(LlvmOpcode opcode, LlvmValue left, LlvmValue right,
        LlvmFastMathFlags flags, BinaryQuery query, uint recurse)
    {
        Count(query.Metrics, "simplify.binary.queries");
        Debug.Assert(ReferenceEquals(left.Type, right.Type));
        LlvmValue? result = opcode switch
        {
            LlvmOpcode.FAdd or LlvmOpcode.FSub or LlvmOpcode.FMul or LlvmOpcode.FDiv or LlvmOpcode.FRem =>
                SimplifyFloatingBinary(opcode, left, right, flags, query),
            _ => SimplifyIntegerBinary(opcode, left, right, false, false, false, query, recurse)
        };
        Count(query.Metrics, result is null ? "simplify.binary.unchanged" : "simplify.binary.folded");
        return result;
    }

    private static LlvmConstant? FoldOrCommuteConstant(LlvmOpcode opcode, ref LlvmValue left,
        ref LlvmValue right, BinaryQuery query)
    {
        if (left is not LlvmConstant lhs) return null;
        if (right is LlvmConstant rhs)
        {
            Count(query.Metrics, "simplify.binary.constant_operands");
            if (opcode is LlvmOpcode.FAdd or LlvmOpcode.FSub or LlvmOpcode.FMul or LlvmOpcode.FDiv or LlvmOpcode.FRem &&
                query.Context is not null)
                return LlvmConstantFolding.ConstantFoldFPInstOperands(opcode, lhs, rhs, query.Layout, query.Context,
                    metrics: query.Metrics);
            return LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, lhs, rhs, query.Layout, query.Metrics);
        }
        if (opcode is LlvmOpcode.Add or LlvmOpcode.Mul or LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor or
            LlvmOpcode.FAdd or LlvmOpcode.FMul)
        {
            (left, right) = (right, left);
            Count(query.Metrics, "simplify.binary.commuted");
        }
        return null;
    }
}
