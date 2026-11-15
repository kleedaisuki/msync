// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// canonicalizeCondSignextOfHighBitExtractToSignextHighBitExtract/foldBoxMultiply.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmValue SkipAddSubCast(LlvmValue value, LlvmOpcode opcode) => value is LlvmCastInstruction cast && cast.Opcode == opcode ? cast.Operand : value;
        private LlvmInstruction? CanonicalizeCondSignextOfHighBitExtractToSignextHighBitExtract(LlvmBinaryOperator instruction)
        {
            for (int side = 0; side != 2; ++side)
            {
                LlvmValue extractValue = side == 0 ? instruction.Left : instruction.Right;
                LlvmValue selected = side == 0 ? instruction.Right : instruction.Left;
                if (instruction.Opcode == LlvmOpcode.Sub && side != 0) continue;
                var extraction = SkipAddSubCast(extractValue, LlvmOpcode.Trunc) as LlvmBinaryOperator;
                if (extraction is not { Opcode: LlvmOpcode.LShr, Right: LlvmInstruction lowBits }) continue;
                LlvmValue x = extraction.Left;
                bool truncate = instruction.Type != x.Type;
                if (truncate && !instruction.Left.HasOneUse && !instruction.Right.HasOneUse) continue;
                if (SkipAddSubCast(lowBits, LlvmOpcode.ZExt) is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt width } difference ||
                    width.Value != new LlvmAPInt(width.Value.BitWidth, x.Type.GetScalarSizeInBits())) continue;
                LlvmValue bits = SkipAddSubCast(difference.Right, LlvmOpcode.ZExt);
                var extensionKind = instruction.Opcode == LlvmOpcode.Sub ? LlvmOpcode.ZExt : LlvmOpcode.SExt;
                if (SkipAddSubCast(selected, extensionKind) is not LlvmSelectInstruction { Condition: LlvmICmpInstruction { Right: LlvmConstantInt threshold } compare } select ||
                    !ReferenceEquals(compare.Left, x) || !LlvmValueTracking.IsSignBitCheck(compare.Predicate, threshold.Value, out bool signExtend)) continue;
                LlvmValue extending = signExtend ? select.TrueValue : select.FalseValue;
                LlvmValue zero = signExtend ? select.FalseValue : select.TrueValue;
                if (zero is not LlvmConstantInt { IsZero: true } ||
                    SkipAddSubCast(extending, extensionKind) is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstant basis } shift ||
                    !ReferenceEquals(SkipAddSubCast(shift.Right, LlvmOpcode.ZExt), bits) ||
                    (instruction.Opcode == LlvmOpcode.Sub ? basis is not LlvmConstantInt { IsOne: true } : basis is not LlvmConstantInt { IsMinusOne: true })) continue;
                var arithmetic = new LlvmBinaryOperator(LlvmOpcode.AShr, x, lowBits);
                arithmetic.CopyIRFlags(extraction);
                if (!truncate) return arithmetic;
                Builder.Insert(arithmetic);
                return new LlvmCastInstruction(LlvmOpcode.Trunc, arithmetic, instruction.Type);
            }
            return null;
        }
        private static bool MatchBoxCrossProduct(LlvmValue value, LlvmValue shiftOperand, LlvmValue full, LlvmValue low, uint half)
        {
            if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Mul } multiply) return false;
            for (int side = 0; side != 2; ++side)
            {
                LlvmValue shifted = side == 0 ? multiply.Left : multiply.Right;
                LlvmValue other = side == 0 ? multiply.Right : multiply.Left;
                if ((ReferenceEquals(other, full) || ReferenceEquals(other, low)) &&
                    shifted is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt amount } shift &&
                    ReferenceEquals(shift.Left, shiftOperand) && amount.Value == new LlvmAPInt(amount.Value.BitWidth, half)) return true;
            }
            return false;
        }
        private static LlvmInstruction? FoldBoxMultiply(LlvmBinaryOperator instruction)
        {
            uint width = instruction.Type.GetScalarSizeInBits();
            if ((width & 1) != 0) return null;
            uint half = width / 2;
            var mask = LlvmAPInt.GetLowBitsSet(width, half);
            for (int side = 0; side != 2; ++side)
            {
                LlvmValue shifted = side == 0 ? instruction.Left : instruction.Right;
                LlvmValue multiplied = side == 0 ? instruction.Right : instruction.Left;
                if (shifted is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt amount, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.Add } cross } ||
                    amount.Value != new LlvmAPInt(width, half) || multiplied is not LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasOneUse: true,
                        Left: LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt yMask } yLow,
                        Right: LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt xMask } xLow } || xMask.Value != mask || yMask.Value != mask) continue;
                var x = xLow.Left; var y = yLow.Left;
                if (MatchBoxCrossProduct(cross.Left, y, x, xLow, half) && MatchBoxCrossProduct(cross.Right, x, y, yLow, half) ||
                    MatchBoxCrossProduct(cross.Right, y, x, xLow, half) && MatchBoxCrossProduct(cross.Left, x, y, yLow, half))
                    return new LlvmBinaryOperator(LlvmOpcode.Mul, x, y);
            }
            return null;
        }
    }
}
