// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// foldBitOrderCrossLogicOp and bitreverse/bswap scalar visitors.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldBitOrder(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            Count("calls.bit_order.visited");
            LlvmValue value = call.GetArgument(0);
            if (kind == LlvmIntrinsicKind.BitReverse)
            {
                if (value is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } extension && extension.Operand.Type.IsIntegerTy(1))
                    return new LlvmSelectInstruction(extension.Operand,
                        CallsInt(call.Type, LlvmAPInt.GetSignedMinValue(call.Type.GetScalarSizeInBits())), CallsInt(call.Type, 0));
                return FoldBitOrderCrossLogicOp(value, kind);
            }
            if (value is LlvmBinaryOperator { HasOneUse: true } shift && shift.Opcode is LlvmOpcode.Shl or LlvmOpcode.LShr
                && MaskedValueIsZero(shift.Right, LlvmAPInt.GetLowBitsSet(value.Type.GetScalarSizeInBits(), 3), call))
                return new LlvmBinaryOperator(shift.Opcode == LlvmOpcode.Shl ? LlvmOpcode.LShr : LlvmOpcode.Shl,
                    Builder.CreateIntrinsic(kind, [shift.Left]), shift.Right);
            LlvmKnownBits known = CallsKnownBits(value, call);
            uint leading = known.CountMinLeadingZeros() / 8 * 8, trailing = known.CountMinTrailingZeros() / 8 * 8;
            if (known.BitWidth - leading - trailing == 8)
                return leading > trailing
                    ? new LlvmBinaryOperator(LlvmOpcode.Shl, value, CallsInt(value.Type, leading - trailing)) { HasNoUnsignedWrap = true }
                    : new LlvmBinaryOperator(LlvmOpcode.LShr, value, CallsInt(value.Type, trailing - leading)) { IsExact = true };
            if (value is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } truncation
                && CallsIntrinsic(truncation.Operand, LlvmIntrinsicKind.BSwap, out var inner))
            {
                LlvmValue operand = inner.GetArgument(0);
                uint amount = operand.Type.GetScalarSizeInBits() - known.BitWidth;
                return new LlvmCastInstruction(LlvmOpcode.Trunc,
                    Builder.CreateBinary(LlvmOpcode.LShr, operand, CallsInt(operand.Type, amount)), value.Type);
            }
            if (FoldBitOrderCrossLogicOp(value, kind) is { } logic) return logic;
            return MatchBSwapOrBitReverse(call, false, true);
        }
        private LlvmInstruction? FoldBitOrderCrossLogicOp(LlvmValue value, LlvmIntrinsicKind kind)
        {
            if (value is not LlvmBinaryOperator { HasOneUse: true } logic || logic.Opcode is not (LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor)) return null;
            bool first = CallsIntrinsic(logic.Left, kind, out var left), second = CallsIntrinsic(logic.Right, kind, out var right);
            if (first && second) return new LlvmBinaryOperator(logic.Opcode, left.GetArgument(0), right.GetArgument(0));
            if (first && left.HasOneUse) return new LlvmBinaryOperator(logic.Opcode, left.GetArgument(0), Builder.CreateIntrinsic(kind, [logic.Right]));
            if (second && right.HasOneUse) return new LlvmBinaryOperator(logic.Opcode, Builder.CreateIntrinsic(kind, [logic.Left]), right.GetArgument(0));
            return null;
        }
        private LlvmValue? FoldIdempotentBinaryIntrinsicRecurrence(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            if (!LlvmIntrinsic.IsMinMax(kind) || call.ArgumentCount != 2 || !ReferenceEquals(call.Type, call.GetArgument(0).Type)
                || !ReferenceEquals(call.Type, call.GetArgument(1).Type)) return null;
            LlvmPhiInstruction? phi = call.GetArgument(0) as LlvmPhiInstruction ?? call.GetArgument(1) as LlvmPhiInstruction;
            if (phi?.IncomingCount != 2) return null;
            for (int index = 0; index < 2; ++index)
            {
                if (phi.GetIncomingValue(index) is not LlvmCallInstruction operation || operation.CalledFunction?.IntrinsicDescriptor is null
                    || operation.OperandCount < 2 || !ReferenceEquals(operation.GetOperand(0), phi) && !ReferenceEquals(operation.GetOperand(1), phi)) continue;
                if (!ReferenceEquals(operation, call)) return null;
                LlvmValue other = ReferenceEquals(operation.GetOperand(0), phi) ? operation.GetOperand(1)! : operation.GetOperand(0)!;
                if (!DT.Dominates(other, phi)) return null;
                return Builder.CreateIntrinsic(kind, [phi.GetIncomingValue(1 - index), other]);
            }
            return null;
        }
    }
}
