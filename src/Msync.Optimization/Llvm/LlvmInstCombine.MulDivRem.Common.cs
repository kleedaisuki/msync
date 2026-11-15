// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineMulDivRem.cpp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmConstantInt MdrInt(LlvmValue value, ulong number) => LlvmConstantInt.Get(value.Context, new LlvmAPInt(value.Type.GetIntegerBitWidth(), number));
        private static LlvmConstantInt MdrInt(LlvmValue value, LlvmAPInt number) => LlvmConstantInt.Get(value.Context, number);
        private static bool MdrOne(LlvmValue value) => value is LlvmConstantInt { IsOne: true };
        private static bool MdrZero(LlvmValue value) => value is LlvmConstant { IsNullValue: true };
        private static bool MdrAllOnes(LlvmValue value) => value is LlvmConstantInt { IsMinusOne: true };
        private static bool MdrOneUse(LlvmValue value) => value.HasOneUse;
        private static LlvmValue? MdrNegated(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt { IsZero: true } } neg ? neg.Right : null;
        private static bool MdrHas(LlvmBinaryOperator value, LlvmFastMathFlags flags) => (value.FastMathFlags & flags) == flags;
        private static LlvmBinaryOperator MdrBin(LlvmOpcode opcode, LlvmValue left, LlvmValue right, bool nuw = false,
            bool nsw = false, bool exact = false, LlvmFastMathFlags fmf = LlvmFastMathFlags.None) =>
            new(opcode, left, right) { HasNoUnsignedWrap = nuw, HasNoSignedWrap = nsw, IsExact = exact, FastMathFlags = fmf };
        private T MdrRewrite<T>(T instruction, string counter) where T : LlvmInstruction { Count(counter); return instruction; }
        private LlvmInstruction? MdrReplace(LlvmInstruction instruction, LlvmValue value, string counter)
        { Count(counter); return ReplaceInstUsesWith(instruction, value); }
        private LlvmKnownBits MdrKnown(LlvmValue value, LlvmInstruction context) => ComputeKnownBits(value, context);
        private bool MdrNonZero(LlvmValue value, LlvmInstruction context) => IsKnownNonZero(value, context);
        private bool MdrPower(LlvmValue value, bool orZero, LlvmInstruction context) => IsKnownToBeAPowerOfTwo(value, orZero, context);
        // Source isGuaranteedNotToBeUndef(X) in these use-increasing transforms deliberately has no query context/cache.
        private bool MdrNoUndef(LlvmValue value) => LlvmValueTracking.IsGuaranteedNotToBeUndef(value, metrics: Metrics);
        private LlvmValue MdrFreeze(LlvmValue value)
        {
            if (MdrNoUndef(value)) return value;
            Count("instcombine.muldivrem.freeze_inserted");
            return Builder.CreateFreeze(value, value.Name + ".fr");
        }
        private LlvmValue? MdrSimplify(LlvmBinaryOperator instruction) => LlvmInstructionSimplify.SimplifyInstruction(instruction, Query(instruction));
        private static bool MdrMatchMul(LlvmValue value, LlvmValue factor, out LlvmValue other)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } mul)
            {
                if (ReferenceEquals(mul.Left, factor)) { other = mul.Right; return true; }
                if (ReferenceEquals(mul.Right, factor)) { other = mul.Left; return true; }
            }
            other = null!; return false;
        }
        private static bool MdrMultiple(LlvmAPInt numerator, LlvmAPInt denominator, bool signed, out LlvmAPInt quotient)
        {
            quotient = LlvmAPInt.GetZero(numerator.BitWidth);
            if (denominator.IsZero || signed && numerator.IsMinSignedValue && denominator.IsAllOnes) return false;
            quotient = signed ? numerator.SDiv(denominator) : numerator.UDiv(denominator);
            return (signed ? numerator.SRem(denominator) : numerator.URem(denominator)).IsZero;
        }
        private LlvmValue? SimplifyValueKnownNonZero(LlvmValue value, LlvmInstruction context)
        {
            if (!MdrOneUse(value)) { Count("instcombine.muldivrem.nonzero_context.multi_use_stop"); return null; }
            Count("instcombine.muldivrem.nonzero_context.visits");
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } shl } shr &&
                MdrOneUse(shl) && MdrOne(shl.Left))
                return Builder.CreateBinary(LlvmOpcode.Shl, shl.Left, Builder.CreateSub(shl.Right, shr.Right));
            if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl or LlvmOpcode.LShr } shift || !MdrPower(shift.Left, false, context)) return null;
            bool changed = false;
            var point = Builder.SaveInsertionPoint();
            Builder.SetInsertPoint(shift);
            try
            {
                if (SimplifyValueKnownNonZero(shift.Left, context) is { } replacement)
                { ReplaceOperand(shift, 0, replacement); changed = true; }
            }
            finally { Builder.RestoreInsertionPoint(point); }
            if (shift.Opcode == LlvmOpcode.LShr && !shift.IsExact) { shift.IsExact = true; changed = true; }
            if (shift.Opcode == LlvmOpcode.Shl && !shift.HasNoUnsignedWrap) { shift.HasNoUnsignedWrap = true; changed = true; }
            if (changed) Count("instcombine.muldivrem.nonzero_context.shift_flags");
            return changed ? value : null;
        }

        private bool SimplifyDivRemOfSelectWithZeroOp(LlvmBinaryOperator instruction)
        {
            if (instruction.Right is not LlvmSelectInstruction select) return false;
            bool trueZero = MdrZero(select.TrueValue);
            if (!trueZero && !MdrZero(select.FalseValue)) return false;
            var nonzero = trueZero ? select.FalseValue : select.TrueValue;
            ReplaceOperand(instruction, 1, nonzero);
            Count("instcombine.muldivrem.select_zero_divisor");
            LlvmValue? condition = select.Condition;
            LlvmSelectInstruction? remaining = select;
            if (select.UseEmpty && condition.HasOneUse) return true;
            for (var previous = instruction.Previous; previous is not null; previous = previous.Previous)
            {
                if (!LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(previous, Metrics)) break;
                foreach (var use in previous.OperandUses)
                {
                    if (ReferenceEquals(use.Value, remaining)) { ReplaceUse(use, nonzero); Worklist.Push(previous); }
                    else if (ReferenceEquals(use.Value, condition))
                    { ReplaceUse(use, LlvmConstantInt.GetBool(instruction.Context, !trueZero)); Worklist.Push(previous); }
                }
                if (ReferenceEquals(previous, remaining)) remaining = null;
                if (ReferenceEquals(previous, condition)) condition = null;
                if (remaining is null && condition is null) break;
            }
            return true;
        }

        private LlvmInstruction? CommonIDivRemTransforms(LlvmBinaryOperator instruction)
        {
            if (FoldBinopWithPhiOperands(instruction) is { } phi) return phi;
            if (SimplifyValueKnownNonZero(instruction.Right, instruction) is { } divisor)
                return ReplaceOperand(instruction, 1, divisor);
            if (SimplifyDivRemOfSelectWithZeroOp(instruction)) return instruction;
            if (instruction.Left is LlvmConstant && instruction.Right is LlvmSelectInstruction
                { TrueValue: LlvmConstant, FalseValue: LlvmConstant } select)
                return FoldOpIntoSelect(instruction, select, true);
            return null;
        }

        private LlvmInstruction? NarrowUDivURem(LlvmBinaryOperator instruction)
        {
            var numerator = instruction.Left; var denominator = instruction.Right;
            if (numerator is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } a && denominator is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } b &&
                ReferenceEquals(a.Operand.Type, b.Operand.Type) && (a.HasOneUse || b.HasOneUse))
                return MdrRewrite(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateBinOp(instruction.Opcode, a.Operand, b.Operand), instruction.Type), "instcombine.muldivrem.narrow_zext_pair");
            if (numerator is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } n && denominator is LlvmConstant dc &&
                GetLosslessUnsignedTrunc(dc, n.Operand.Type, DL) is { } narrowD)
                return MdrRewrite(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateBinOp(instruction.Opcode, n.Operand, narrowD), instruction.Type), "instcombine.muldivrem.narrow_zext_dividend");
            if (denominator is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } d && numerator is LlvmConstant nc &&
                GetLosslessUnsignedTrunc(nc, d.Operand.Type, DL) is { } narrowN)
                return MdrRewrite(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateBinOp(instruction.Opcode, narrowN, d.Operand), instruction.Type), "instcombine.muldivrem.narrow_zext_divisor");
            return null;
        }
    }
}
