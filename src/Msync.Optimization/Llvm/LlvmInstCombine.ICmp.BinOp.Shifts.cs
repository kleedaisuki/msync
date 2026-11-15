// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 4967-5124.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool BinLogicalShift(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl or LlvmOpcode.LShr };
        private LlvmValue? FoldShiftIntoShiftInAnotherHandOfAndInICmp(LlvmICmpInstruction compare, LlvmInstructionSimplify.BinaryQuery query)
        {
            Count("icmp.binop.opposite-shifts.queries");
            if (!ICmpIsEquality(compare.Predicate) || !ICmpConstantMatches(compare.Right, bits => bits.IsZero) ||
                compare.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } and) return null;
            LlvmBinaryOperator? xs = null, ys = null; LlvmValue? maybeTrunc = null;
            for (int side = 0; side < 2; side++)
            {
                var left = side == 0 ? and.Left : and.Right; var right = side == 0 ? and.Right : and.Left;
                var inner = right is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc ? trunc.Operand : right;
                if (!BinLogicalShift(left) || !BinLogicalShift(inner)) continue;
                xs = (LlvmBinaryOperator)left; ys = (LlvmBinaryOperator)inner; maybeTrunc = right; break;
            }
            if (xs is null || ys is null) return null;
            var widest = ys; var narrowest = xs;
            LlvmType wideTy = widest.Type, narrowTy = narrowest.Type;
            bool hadTrunc = wideTy != and.Type;
            if (ys.Opcode == LlvmOpcode.LShr) (xs, ys) = (ys, xs);
            if (xs.Opcode == ys.Opcode) { Count("icmp.binop.opposite-shifts.same-direction"); return null; }
            LlvmValue x = xs.Left, y = ys.Left;
            var xAmt = xs.Right is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } xe ? xe.Operand : xs.Right;
            var yAmt = ys.Right is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } ye ? ye.Operand : ys.Right;
            if (x is not LlvmConstant && y is not LlvmConstant)
            {
                if (!(BinLogicalShift(and.Left) && and.Left.HasOneUse || BinLogicalShift(and.Right) && and.Right.HasOneUse)) return null;
                if (hadTrunc && !maybeTrunc!.HasOneUse && !narrowest.Right.HasOneUse) return null;
            }
            if (xAmt.Type != yAmt.Type) { Count("icmp.binop.opposite-shifts.amount-type-mismatch"); return null; }
            uint wideWidth = wideTy.GetScalarSizeInBits(), narrowWidth = narrowTy.GetScalarSizeInBits();
            uint maxTotal = wideWidth - 1 + narrowWidth - 1;
            if (LlvmAPInt.GetAllOnes(xAmt.Type.GetScalarSizeInBits()).GetLimitedValue() < maxTotal)
            { Count("icmp.binop.opposite-shifts.amount-may-overflow"); return null; }
            if (LlvmInstructionSimplify.SimplifyBinOp(LlvmOpcode.Add, xAmt, yAmt, query) is not LlvmConstant amount)
            { Count("icmp.binop.opposite-shifts.amount-not-constant"); return null; }
            if (amount.Type != wideTy)
            {
                var folded = LlvmConstantFolding.ConstantFoldCastOperand(LlvmOpcode.ZExt, amount, wideTy, DL, Metrics);
                if (folded is null) return null;
                amount = folded;
            }
            if (!ICmpConstantMatches(amount, bits => bits.Ult(new LlvmAPInt(bits.BitWidth, wideWidth))))
            { Count("icmp.binop.opposite-shifts.amount-out-of-range"); return null; }
            if (hadTrunc && widest.Opcode == LlvmOpcode.LShr && !BinCanFoldTruncatedShifts(amount, wideWidth, narrowest, widest, query))
            { Count("icmp.binop.opposite-shifts.trunc-legality-unproved"); return null; }
            Count(hadTrunc ? "icmp.binop.opposite-shifts.truncated" : "icmp.binop.opposite-shifts.direct");
            x = Builder.CreateZExt(x, wideTy); y = Builder.CreateZExt(y, wideTy);
            var shifted = xs.Opcode == LlvmOpcode.LShr ? Builder.CreateLShr(x, amount) : Builder.CreateShl(x, amount);
            return Builder.CreateICmp(compare.Predicate, Builder.CreateAnd(shifted, y), LlvmConstant.GetNullValue(wideTy));
        }
        private bool BinCanFoldTruncatedShifts(LlvmConstant amount, uint width, LlvmBinaryOperator narrowest,
            LlvmBinaryOperator widest, LlvmInstructionSimplify.BinaryQuery query)
        {
            var splat = ICmpAPInt(amount);
            if (splat is not null && (splat.IsZero || splat.Value == new LlvmAPInt(splat.Value.BitWidth, width - 1))) return true;
            if (narrowest.Left is LlvmConstant narrow)
            {
                var known = ComputeKnownBits(narrow);
                uint zeros = known.CountMinLeadingZeros();
                if (known.BitWidth - zeros <= 1 || splat is not null && splat.Value.Ule(new LlvmAPInt(splat.Value.BitWidth, zeros))) return true;
            }
            if (widest.Left is LlvmConstant wide)
            {
                var known = ComputeKnownBits(wide);
                uint zeros = known.CountMinLeadingZeros();
                if (known.BitWidth - zeros <= 1 || splat is not null &&
                    (new LlvmAPInt(splat.Value.BitWidth, width - 1) - splat.Value).Ule(new LlvmAPInt(splat.Value.BitWidth, zeros))) return true;
            }
            return false;
        }
    }
}
