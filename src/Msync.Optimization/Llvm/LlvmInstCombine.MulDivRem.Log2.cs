// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: takeLog2 / tryGetLog2.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? TryGetLog2(LlvmValue value, bool assumeNonZero = false) =>
            TakeLog2(value, 0, assumeNonZero, false) is null ? null : TakeLog2(value, 0, assumeNonZero, true);
        private LlvmValue? TakeLog2(LlvmValue value, uint depth, bool assumeNonZero, bool fold)
        {
            Count(fold ? "instcombine.log2.fold_visits" : "instcombine.log2.probe_visits");
            // Source probe returns a non-null sentinel whose payload is never read.
            // The existing input is the managed non-null witness; no synthetic IR is allocated.
            if (value is LlvmConstantInt constant && constant.Value.IsPowerOf2)
                return fold ? MdrInt(value, constant.Value.GetActiveBits() - 1) : value;
            if (depth++ == 6) { Count("instcombine.log2.depth_limit"); return null; }
            if (value is LlvmCastInstruction cast && (cast.Opcode == LlvmOpcode.ZExt ||
                cast.Opcode == LlvmOpcode.Trunc && (assumeNonZero || cast.HasNoUnsignedWrap)))
            {
                if (TakeLog2(cast.Operand, depth, assumeNonZero, fold) is not { } log) return null;
                if (!fold) return value;
                var result = Builder.CreateCast(cast.Opcode, log, value.Type);
                if (cast.Opcode == LlvmOpcode.Trunc && result is LlvmCastInstruction trunc) trunc.HasNoUnsignedWrap = cast.HasNoUnsignedWrap;
                return result;
            }
            if (value is LlvmBinaryOperator binary)
            {
                bool shift = binary.Opcode == LlvmOpcode.Shl && (assumeNonZero || binary.HasNoUnsignedWrap || binary.HasNoSignedWrap);
                bool shr = binary.Opcode == LlvmOpcode.LShr && (assumeNonZero || binary.IsExact);
                if ((shift || shr) && TakeLog2(binary.Left, depth, assumeNonZero, fold) is { } log)
                    return !fold ? value : shift ? Builder.CreateAdd(log, binary.Right) : Builder.CreateSub(log, binary.Right);
                if (assumeNonZero && binary.Opcode == LlvmOpcode.And)
                {
                    if (TakeLog2(binary.Left, depth, true, fold) is { } lhs) return fold ? lhs : value;
                    if (TakeLog2(binary.Right, depth, true, fold) is { } rhs) return fold ? rhs : value;
                }
            }
            if (value is LlvmSelectInstruction select && TakeLog2(select.TrueValue, depth, assumeNonZero, fold) is { } a &&
                TakeLog2(select.FalseValue, depth, assumeNonZero, fold) is { } b)
                return fold ? Builder.CreateSelect(select.Condition, a, b) : value;
            if (value is LlvmCallInstruction { HasOneUse: true, CalledFunction.IntrinsicDescriptor: { } descriptor } call &&
                (descriptor.Kind == LlvmIntrinsicKind.UMin || descriptor.Kind == LlvmIntrinsicKind.UMax) &&
                TakeLog2(call.GetArgument(0), depth, false, fold) is { } minLeft && TakeLog2(call.GetArgument(1), depth, false, fold) is { } minRight)
                return fold ? Builder.CreateBinaryIntrinsic(descriptor.Kind, minLeft, minRight) : value;
            if (!value.Type.IsIntegerTy(1) && value is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt { IsOne: true } } add &&
                MdrKnown(add.Left, add).CountMaxActiveBits() == 1) return fold ? add.Left : value;
            return null;
        }
    }
}
