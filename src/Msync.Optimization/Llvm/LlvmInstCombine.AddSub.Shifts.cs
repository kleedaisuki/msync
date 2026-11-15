// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// canonicalizeLowbitMask/combineAddSubWithShlAddSub/foldAddToAshr/
// foldAddLikeCommutative/factorizeMathWithShlOps/checkDivCeilNUW/foldDivCeil.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmConstantInt AddSubInt(LlvmValue value, ulong number) => LlvmConstantInt.Get(value.Context, new LlvmAPInt(value.Type.GetScalarSizeInBits(), number));
        private static LlvmValue? AddSubNegated(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt { IsZero: true } } sub ? sub.Right : null;
        private static bool MatchAddSubLowMask(LlvmValue value, out LlvmAPInt mask)
        { mask = value is LlvmConstantInt integer ? integer.Value : default; return value is LlvmConstantInt && !mask.IsZero && (mask.IsAllOnes || (mask + new LlvmAPInt(mask.BitWidth, 1)).IsPowerOf2); }
        private LlvmInstruction? CanonicalizeLowbitMask(LlvmBinaryOperator instruction)
        {
            if (instruction.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasOneUse: true, Left: LlvmConstantInt { IsOne: true } } shift ||
                instruction.Right is not LlvmConstantInt { IsMinusOne: true }) return null;
            var mask = Builder.CreateBinary(LlvmOpcode.Shl, LlvmConstantInt.Get(shift.Right.Context, LlvmAPInt.GetAllOnes(shift.Right.Type.GetScalarSizeInBits())), shift.Right);
            if (mask is LlvmBinaryOperator binary) { binary.HasNoSignedWrap = true; binary.HasNoUnsignedWrap = instruction.HasNoUnsignedWrap; }
            return new LlvmBinaryOperator(LlvmOpcode.Xor, mask, LlvmConstantInt.Get(mask.Context, LlvmAPInt.GetAllOnes(mask.Type.GetScalarSizeInBits())));
        }
        private LlvmInstruction? CombineAddSubWithShlAddSub(LlvmBinaryOperator instruction)
        {
            for (int side = 0; side != 2; ++side)
            {
                var candidate = side == 0 ? instruction.Left : instruction.Right;
                var other = side == 0 ? instruction.Right : instruction.Left;
                if (candidate is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasOneUse: true } shift && shift.Left.HasOneUse && AddSubNegated(shift.Left) is { } value)
                    return new LlvmBinaryOperator(LlvmOpcode.Sub, other, Builder.CreateBinary(LlvmOpcode.Shl, value, shift.Right));
            }
            return null;
        }
        private static LlvmInstruction? FoldAddToAshr(LlvmBinaryOperator add)
        {
            if (add.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.SDiv, Right: LlvmConstantInt divisor } div || !divisor.Value.IsPowerOf2 || divisor.Value.IsNegative ||
                add.Right is not LlvmCastInstruction { Opcode: LlvmOpcode.SExt, Operand: LlvmICmpInstruction { Left: LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt mask } and, Right: LlvmConstantInt comparisonMask } compare } ||
                !ReferenceEquals(and.Left, div.Left)) return null;
            bool unsignedGreater = compare.Predicate == LlvmCmpPredicate.ICmpUgt && comparisonMask.Value.IsMinSignedValue;
            bool equal = compare.Predicate == LlvmCmpPredicate.ICmpEq && comparisonMask.Value == mask.Value;
            if (!unsignedGreater && !equal) return null;
            var one = new LlvmAPInt(mask.Value.BitWidth, 1);
            var minimum = LlvmAPInt.GetSignedMinValue(add.Type.GetScalarSizeInBits());
            if (unsignedGreater ? mask.Value != (minimum | (divisor.Value - one)) : divisor.Value != new LlvmAPInt(divisor.Value.BitWidth, 2) || mask.Value != minimum + one) return null;
            return new LlvmBinaryOperator(LlvmOpcode.AShr, div.Left, AddSubInt(add, divisor.Value.GetActiveBits() - 1));
        }
        private LlvmInstruction? FoldAddLikeCommutative(LlvmValue left, LlvmValue right, bool nsw, bool nuw)
        {
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } first && right is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } second && ReferenceEquals(first.Left, second.Right))
                return new LlvmBinaryOperator(LlvmOpcode.Sub, second.Left, first.Right)
                { HasNoSignedWrap = nsw && first.HasNoSignedWrap && second.HasNoSignedWrap, HasNoUnsignedWrap = first.HasNoUnsignedWrap && second.HasNoUnsignedWrap };
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmBinaryOperator { Opcode: LlvmOpcode.SDiv, Right: LlvmConstantInt divisor } division, Right: LlvmConstantInt shift } &&
                ReferenceEquals(division.Left, right) && -divisor.Value == new LlvmAPInt(shift.Value.BitWidth, 1).Shl(shift.Value))
                return new LlvmBinaryOperator(LlvmOpcode.SRem, right, LlvmConstantInt.Get(right.Context, -divisor.Value));
            if (left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add || right is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } and) return null;
            for (int side = 0; side != 2; ++side)
            {
                var constant = (side == 0 ? add.Right : add.Left) as LlvmConstantInt;
                var a = side == 0 ? add.Left : add.Right;
                if (constant is null) continue;
                LlvmValue? b = and.Right is LlvmConstantInt rhs && rhs.Value == ~constant.Value ? and.Left :
                    and.Left is LlvmConstantInt lhs && lhs.Value == ~constant.Value ? and.Right : null;
                if (b is null || !left.HasOneUse && !right.HasOneUse) continue;
                return new LlvmBinaryOperator(LlvmOpcode.Add, a, Builder.CreateOr(b, constant))
                { HasNoSignedWrap = nsw && add.HasNoSignedWrap, HasNoUnsignedWrap = nuw && add.HasNoUnsignedWrap };
            }
            return null;
        }
        private LlvmInstruction? FactorizeMathWithShlOps(LlvmBinaryOperator instruction)
        {
            if (instruction.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } left ||
                instruction.Right is not LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } right ||
                !ReferenceEquals(left.Right, right.Right) || !left.HasOneUse && !right.HasOneUse) return null;
            bool nsw = instruction.HasNoSignedWrap && left.HasNoSignedWrap && right.HasNoSignedWrap;
            bool nuw = instruction.HasNoUnsignedWrap && left.HasNoUnsignedWrap && right.HasNoUnsignedWrap;
            var math = Builder.CreateBinary(instruction.Opcode, left.Left, right.Left);
            if (math is LlvmBinaryOperator binary) { binary.HasNoSignedWrap = nsw; binary.HasNoUnsignedWrap = nuw; }
            return new LlvmBinaryOperator(LlvmOpcode.Shl, math, left.Right) { HasNoSignedWrap = nsw, HasNoUnsignedWrap = nuw };
        }
        private LlvmInstruction? FoldDivCeil(LlvmBinaryOperator instruction)
        {
            return FoldDivCeilSide(instruction.Left, instruction.Right, instruction) ?? FoldDivCeilSide(instruction.Right, instruction.Left, instruction);
        }
        private LlvmInstruction? FoldDivCeilSide(LlvmValue divSide, LlvmValue compareSide, LlvmBinaryOperator instruction)
        {
            if (!divSide.HasOneUse) return null;
            LlvmValue divisionValue = divSide is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } cast ? cast.Operand : divSide;
            if (divisionValue is not LlvmBinaryOperator { Opcode: LlvmOpcode.UDiv, HasOneUse: true } division ||
                compareSide is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true,
                    Operand: LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpNe, HasOneUse: true,
                        Left: LlvmBinaryOperator { Opcode: LlvmOpcode.URem, HasOneUse: true } rem, Right: LlvmConstantInt { IsZero: true } } } ||
                !ReferenceEquals(division.Left, rem.Left) || !ReferenceEquals(division.Right, rem.Right)) return null;
            var q = Query(instruction);
            var xRange = LlvmConstantRangeAnalysis.Compute(division.Left, context: instruction, tree: DT, metrics: Metrics, assumptions: AC, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
            var yRange = LlvmConstantRangeAnalysis.Compute(division.Right, context: instruction, tree: DT, metrics: Metrics, assumptions: AC, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
            if (yRange.GetUnsignedMin().IsZero || xRange.GetUnsignedMax().Ugt(-yRange.GetUnsignedMax())) return null;
            var yMinusOne = Builder.CreateAdd(division.Right, LlvmConstantInt.Get(division.Right.Context, LlvmAPInt.GetAllOnes(division.Right.Type.GetScalarSizeInBits())));
            var add = Builder.CreateAdd(division.Left, yMinusOne, nuw: true);
            if (division.Left.Type != instruction.Type)
                return new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateBinary(LlvmOpcode.UDiv, add, division.Right), instruction.Type);
            return new LlvmBinaryOperator(LlvmOpcode.UDiv, add, division.Right);
        }
    }
}
