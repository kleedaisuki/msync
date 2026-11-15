// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ConstantFolding.cpp
// getLosslessInvCast/getLosslessUnsignedTrunc/getLosslessSignedTrunc (5171..5230).
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        internal sealed class PreservedCastFlags
        {
            internal bool NNeg, NUW, NSW;
        }
        private LlvmConstant? GetLosslessInvCast(LlvmConstant value, LlvmType type, LlvmOpcode opcode,
            LlvmDataLayout layout, PreservedCastFlags? flags = null)
        {
            Count("casts.lossless.queries");
            LlvmConstant? Fold(LlvmOpcode operation, LlvmConstant operand, LlvmType destination) =>
                LlvmConstantFolding.ConstantFoldCastOperand(operation, operand, destination, layout, Metrics);
            switch (opcode)
            {
                case LlvmOpcode.BitCast: return Fold(LlvmOpcode.BitCast, value, type);
                case LlvmOpcode.Trunc:
                    var extended = Fold(LlvmOpcode.ZExt, value, type);
                    if (flags is not null)
                    {
                        flags.NUW = true;
                        flags.NSW = ReferenceEquals(extended, Fold(LlvmOpcode.SExt, value, type));
                    }
                    return extended;
                case LlvmOpcode.SExt: case LlvmOpcode.ZExt:
                    var narrowed = LlvmConstantExpr.GetTrunc(value, type);
                    var restored = Fold(opcode, narrowed, value.Type);
                    if (restored is null || !ReferenceEquals(restored, value)) return null;
                    if (flags is not null && opcode == LlvmOpcode.ZExt)
                        flags.NNeg = ReferenceEquals(restored, Fold(LlvmOpcode.SExt, narrowed, value.Type));
                    return narrowed;
                case LlvmOpcode.FPExt:
                    var truncated = Fold(LlvmOpcode.FPTrunc, value, type);
                    return truncated is not null && ReferenceEquals(Fold(opcode, truncated, value.Type), value) ? truncated : null;
                default: return null;
            }
        }
        private LlvmConstant? GetLosslessUnsignedTrunc(LlvmConstant value, LlvmType type) =>
            GetLosslessUnsignedTrunc(value, type, DL);
        private LlvmConstant? GetLosslessUnsignedTrunc(LlvmConstant value, LlvmType type, LlvmDataLayout layout,
            PreservedCastFlags? flags = null) => GetLosslessInvCast(value, type, LlvmOpcode.ZExt, layout, flags);
        private LlvmConstant? GetLosslessSignedTrunc(LlvmConstant value, LlvmType type, LlvmDataLayout layout,
            PreservedCastFlags? flags = null) => GetLosslessInvCast(value, type, LlvmOpcode.SExt, layout, flags);
    }
}
