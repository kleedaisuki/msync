// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp OptimizePointerDifference.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? OptimizePointerDifference(LlvmValue left, LlvmValue right, LlvmType type, bool isNUW)
        {
            var basis = LlvmCommonPointerBase.Compute(left, right);
            if (basis.Ptr is null || basis.IsExpensive()) return null;
            bool rewrite = basis.LHSGEPs.Count != 0 && basis.RHSGEPs.Count != 0;
            var indexType = DL.GetIndexType(left.Type);
            var result = EmitGEPOffsets(basis.LHSGEPs, basis.LHSNW, indexType, rewrite);
            var offset = EmitGEPOffsets(basis.RHSGEPs, basis.RHSNW, indexType, rewrite);
            bool offsetZero = offset is LlvmConstant { IsNullValue: true };
            if (result is LlvmBinaryOperator binary && isNUW && offsetZero && basis.LHSNW.IsInBounds &&
                (binary.UseEmpty || binary.HasOneUse) && binary.HasNoSignedWrap && !binary.HasNoUnsignedWrap &&
                (binary.Opcode == LlvmOpcode.Shl || binary.Opcode == LlvmOpcode.Mul && binary.Right is LlvmConstantInt { Value.IsNegative: false }))
                binary.HasNoUnsignedWrap = true;
            if (!offsetZero) result = Builder.CreateSub(result, offset, "gepdiff",
                isNUW && basis.LHSNW.HasNoUnsignedWrap && basis.RHSNW.HasNoUnsignedWrap,
                basis.LHSNW.IsInBounds && basis.RHSNW.IsInBounds);
            return Builder.CreateIntCast(result, type, true);
        }
    }
}
