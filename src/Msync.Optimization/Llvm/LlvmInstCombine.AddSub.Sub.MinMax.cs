// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp 2371–2440.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSubOfMinMax(LlvmBinaryOperator instruction)
        {
            var first = instruction.Left; var second = instruction.Right;
            if (second is not LlvmCallInstruction minmax || minmax.CalledFunction?.IntrinsicDescriptor is not { } descriptor || !LlvmIntrinsic.IsMinMax(descriptor.Kind)) return null;
            var x = minmax.GetArgument(0); var y = minmax.GetArgument(1);
            var inverse = descriptor.Kind == LlvmIntrinsicKind.SMin ? LlvmIntrinsicKind.SMax : descriptor.Kind == LlvmIntrinsicKind.SMax ? LlvmIntrinsicKind.SMin :
                descriptor.Kind == LlvmIntrinsicKind.UMin ? LlvmIntrinsicKind.UMax : LlvmIntrinsicKind.UMin;
            if (SubPair(first, LlvmOpcode.Add, x, y) && (first.HasOneUse || second.HasOneUse))
                return SubRewrite(LlvmIntrinsic.CreateIntrinsic(inverse, [x, y], F.Parent!, Metrics), "add_minus_minmax");
            if (descriptor.Kind == LlvmIntrinsicKind.UMin && second.HasOneUse && first.HasOneUse)
            {
                if (SubCommuted(first, LlvmOpcode.Add, x, out var other))
                    return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Add, other, Builder.CreateIntrinsic(LlvmIntrinsicKind.USubSat, [x, y])), "add_minus_umin");
                if (SubCommuted(first, LlvmOpcode.Add, y, out other))
                    return SubRewrite(new LlvmBinaryOperator(LlvmOpcode.Add, other, Builder.CreateIntrinsic(LlvmIntrinsicKind.USubSat, [y, x])), "add_minus_umin");
            }
            if ((descriptor.Kind == LlvmIntrinsicKind.SMin || descriptor.Kind == LlvmIntrinsicKind.SMax) && SubZero(y) &&
                SubBinary(x, LlvmOpcode.Sub, out var lhs, out var z) && lhs == first && SubNSW(x))
                return SubRewrite(LlvmIntrinsic.CreateIntrinsic(inverse, [first, z], F.Parent!, Metrics), "subtract_signed_minmax_zero");
            return null;
        }
        private LlvmInstruction? FoldSubNUWOfMaskAndZExt(LlvmBinaryOperator instruction)
        {
            if (!instruction.HasNoUnsignedWrap) return null;
            bool Match(LlvmValue maskOperand, LlvmValue extension, out LlvmValue x, out LlvmValue y)
            {
                x = y = null!;
                return maskOperand.HasOneUse && extension.HasOneUse && SubBinary(maskOperand, LlvmOpcode.And, out x, out var constant) &&
                    SubAPInt(constant, true) is { } mask && !mask.Value.IsZero && (mask.Value & (mask.Value + new LlvmAPInt(mask.Value.BitWidth, 1))).IsZero &&
                    SubCast(extension, LlvmOpcode.ZExt, out y) && mask.Value.CountTrailingOnes() == y.Type.GetScalarSizeInBits();
            }
            bool maskIsLeft = Match(instruction.Left, instruction.Right, out var value, out var narrow);
            if (!maskIsLeft && !Match(instruction.Right, instruction.Left, out value, out narrow)) return null;
            var truncated = Builder.CreateTrunc(value, narrow.Type);
            var subtraction = maskIsLeft ? Builder.CreateSub(truncated, narrow, nuw: true) : Builder.CreateSub(narrow, truncated, nuw: true);
            return SubRewrite(new LlvmCastInstruction(LlvmOpcode.ZExt, subtraction, instruction.Type), "nuw_mask_zext");
        }
    }
}
