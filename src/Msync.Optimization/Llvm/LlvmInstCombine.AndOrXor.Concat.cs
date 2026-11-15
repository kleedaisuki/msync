// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineAndOrXor.cpp matchOrConcat (3237–3301).
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? MatchOrConcat(LlvmInstruction instruction)
        {
            Debug.Assert(instruction.Opcode == LlvmOpcode.Or);
            Count("instcombine.logic.concat.queries");
            uint width = instruction.Type.GetScalarSizeInBits();
            if ((width & 1) != 0) return null;
            uint half = width / 2;
            LlvmValue low = instruction.GetOperand(0)!, high = instruction.GetOperand(1)!;
            if (low is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt }) (low, high) = (high, low);
            if (low is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } lowExtend ||
                high is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasOneUse: true,
                    Left: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } highExtend } shift ||
                ConcatAPInt(shift.Right) is not { } amount) return null;
            LlvmValue lowSource = lowExtend.Operand, highSource = highExtend.Operand;
            if (amount.Value != new LlvmAPInt(amount.BitWidth, half) || lowSource.Type != highSource.Type ||
                lowSource.Type.GetScalarSizeInBits() != half) return null;
            if (ConcatIntrinsicOperand(lowSource, LlvmIntrinsicKind.BSwap) is { } lowSwap &&
                ConcatIntrinsicOperand(highSource, LlvmIntrinsicKind.BSwap) is { } highSwap)
            {
                Count("instcombine.logic.concat.bswap_pushed");
                return ConcatIntrinsicCalls(LlvmIntrinsicKind.BSwap, highSwap, lowSwap, instruction.Type, half);
            }
            if (ConcatIntrinsicOperand(lowSource, LlvmIntrinsicKind.BitReverse) is { } lowReverse &&
                ConcatIntrinsicOperand(highSource, LlvmIntrinsicKind.BitReverse) is { } highReverse)
            {
                Count("instcombine.logic.concat.bitreverse_pushed");
                return ConcatIntrinsicCalls(LlvmIntrinsicKind.BitReverse, highReverse, lowReverse, instruction.Type, half);
            }
            LlvmValue value = lowSource is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } lowSign ? lowSign.Operand : lowSource;
            LlvmValue sign = highSource is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } highSign ? highSign.Operand : highSource;
            if (sign is not LlvmBinaryOperator { Opcode: LlvmOpcode.AShr } signShift || signShift.Left != value ||
                ConcatAPInt(signShift.Right) is not { } signAmount ||
                signAmount.Value != new LlvmAPInt(signAmount.BitWidth, value.Type.GetScalarSizeInBits() - 1)) return null;
            Count("instcombine.logic.concat.sign_extend");
            return Builder.CreateSExt(value, instruction.Type);
        }

        private LlvmValue ConcatIntrinsicCalls(LlvmIntrinsicKind kind, LlvmValue low, LlvmValue high,
            LlvmType type, uint halfWidth)
        {
            LlvmValue lower = Builder.CreateZExt(low, type);
            LlvmValue upper = Builder.CreateZExt(high, type);
            upper = Builder.CreateBinary(LlvmOpcode.Shl, upper,
                LlvmConstantInt.Get(type, new LlvmAPInt(type.GetScalarSizeInBits(), halfWidth)));
            LlvmValue packed = CreateDisjointLogicOr(lower, upper);
            return Builder.CreateIntrinsic(kind, [packed], type);
        }

        private static LlvmValue? ConcatIntrinsicOperand(LlvmValue value, LlvmIntrinsicKind kind) =>
            value is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } call && descriptor.Kind == kind
                ? call.GetArgument(0) : null;

        private static LlvmConstantInt? ConcatAPInt(LlvmValue value) => value as LlvmConstantInt ??
            (value.Type.IsVectorTy() && value is LlvmConstant constant ? constant.GetSplatValue() as LlvmConstantInt : null);
    }
}
