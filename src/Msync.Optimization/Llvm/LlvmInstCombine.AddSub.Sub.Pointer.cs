// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp 2932–2978.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool SubPointerCast(LlvmValue value, out LlvmValue pointer) =>
            SubCast(value, LlvmOpcode.PtrToInt, out pointer) || SubCast(value, LlvmOpcode.PtrToAddr, out pointer);
        private bool SubSameSizePtrToInt(LlvmValue value, out LlvmValue pointer) =>
            SubCast(value, LlvmOpcode.PtrToInt, out pointer) && value.Type.GetScalarSizeInBits() == DL.GetPointerTypeSizeInBits(pointer.Type);
        private bool SubZExtPointerPair(LlvmValue left, LlvmValue right, out LlvmValue first, out LlvmValue second)
        {
            first = second = null!;
            if (!SubCast(left, LlvmOpcode.ZExt, out var innerLeft)) return false;
            if (SubCast(right, LlvmOpcode.ZExt, out var innerRight))
            {
                if (SubSameSizePtrToInt(innerLeft, out first) && SubSameSizePtrToInt(innerRight, out second)) return true;
                if (SubCast(innerLeft, LlvmOpcode.PtrToAddr, out first) && SubCast(innerRight, LlvmOpcode.PtrToAddr, out second)) return true;
            }
            return SubSameSizePtrToInt(innerLeft, out first) && SubCast(right, LlvmOpcode.PtrToInt, out second);
        }
        private LlvmInstruction? SubPointerDifference(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            if (SubPointerCast(left, out var lhs) && SubPointerCast(right, out var rhs) &&
                OptimizePointerDifference(lhs, rhs, instruction.Type, instruction.HasNoUnsignedWrap) is { } direct)
                return SubReplace(instruction, direct, "pointer_difference");
            if (SubCast(left, LlvmOpcode.Trunc, out var leftCast) && SubCast(right, LlvmOpcode.Trunc, out var rightCast) &&
                SubPointerCast(leftCast, out lhs) && SubPointerCast(rightCast, out rhs) &&
                OptimizePointerDifference(lhs, rhs, instruction.Type, false) is { } truncated)
                return SubReplace(instruction, truncated, "truncated_pointer_difference");
            if (!SubZExtPointerPair(left, right, out lhs, out rhs) || !LlvmGepOperator.TryGet(lhs, out var gep) || gep.PointerOperand != rhs ||
                !gep.HasNoUnsignedWrap && !gep.HasNoUnsignedSignedWrap) return null;
            var offset = EmitGEPOffset(gep);
            LlvmValue result;
            if (gep.HasNoUnsignedWrap)
            {
                result = Builder.CreateZExt(offset, instruction.Type);
                if (result is LlvmCastInstruction cast) cast.IsNonNegative = gep.HasNoUnsignedSignedWrap;
            }
            else result = Builder.CreateSExt(offset, instruction.Type);
            return SubReplace(instruction, result, "extended_pointer_offset");
        }
    }
}
