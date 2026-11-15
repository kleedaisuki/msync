// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp shifts and simplifyShrShlDemandedBits.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private uint DemandedNumSignBits(LlvmValue value, LlvmInstructionSimplify.BinaryQuery query, uint depth) =>
            LlvmKnownBitsAnalysis.ComputeNumSignBits(value, depth, query.Context, query.Tree, query.Metrics,
                query.UseInstructionInfo, query.Assumptions, query.AllowEphemerals, query.DC);

        private LlvmValue? SimplifyDemandedShift(LlvmBinaryOperator instruction, LlvmAPInt demanded,
            ref LlvmKnownBits known, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            uint width = demanded.BitWidth;
            if (instruction.Opcode == LlvmOpcode.Shl)
            {
                if (instruction.Right is not LlvmConstantInt constant)
                {
                    uint leading = demanded.CountLeadingZeros();
                    if (leading != 0 && SimplifyDemandedBits(instruction, 0, LlvmAPInt.GetLowBitsSet(width, width - leading), ref known, query, depth + 1))
                    { instruction.DropPoisonGeneratingFlags(); return instruction; }
                    known = ComputeDemandedKnownBits(instruction, query, depth); return null;
                }
                if (instruction.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr or LlvmOpcode.LShr, Right: LlvmConstantInt innerAmount } inner &&
                    SimplifyShrShlDemandedBits(inner, innerAmount.Value, instruction, constant.Value, demanded, ref known) is { } merged) return merged;
                if (PreserveDemandedRotate(instruction))
                { known = ComputeDemandedKnownBits(instruction, query, depth); return null; }
                uint amount = (uint)constant.Value.GetLimitedValue(width - 1);
                if (demanded.CountTrailingZeros() >= amount)
                {
                    if (instruction.HasNoSignedWrap)
                    {
                        uint sign = DemandedNumSignBits(instruction.Left, query, depth + 1);
                        if (sign > amount && sign - amount >= width - demanded.CountTrailingZeros()) return instruction.Left;
                    }
                    if (instruction.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Left: LlvmConstant original } shifted)
                    {
                        var count = LlvmConstantInt.Get((LlvmIntegerType)instruction.Type, amount);
                        if (LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Shl, original, count, DL, Metrics) is { } shiftedConstant &&
                            LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.LShr, shiftedConstant, count, DL, Metrics) == original)
                            return InsertNewInstWith(new LlvmBinaryOperator(LlvmOpcode.LShr, shiftedConstant, shifted.Right), instruction);
                    }
                }
                var input = demanded.LShr(amount);
                if (instruction.HasNoSignedWrap) input |= LlvmAPInt.GetHighBitsSet(width, amount + 1);
                else if (instruction.HasNoUnsignedWrap) input |= LlvmAPInt.GetHighBitsSet(width, amount);
                if (SimplifyDemandedBits(instruction, 0, input, ref known, query, depth + 1)) return instruction;
                known = LlvmKnownBits.Shl(known, LlvmKnownBits.MakeConstant(new LlvmAPInt(width, amount)), instruction.HasNoUnsignedWrap, instruction.HasNoSignedWrap);
                return null;
            }
            if (instruction.Opcode == LlvmOpcode.LShr)
            {
                if (instruction.Right is not LlvmConstantInt constant)
                {
                    if (SimplifyShiftSelectingPackedElement(instruction, demanded, query, depth) is { } selected) return selected;
                    known = ComputeDemandedKnownBits(instruction, query, depth); return null;
                }
                uint amount = (uint)constant.Value.GetLimitedValue(width - 1);
                if (PreserveDemandedRotate(instruction))
                { known = ComputeDemandedKnownBits(instruction, query, depth); return null; }
                if (demanded.CountLeadingZeros() >= amount)
                {
                    if (DemandedNumSignBits(instruction.Left, query, depth + 1) >= width - demanded.CountTrailingZeros()) return instruction.Left;
                    if (instruction.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstant original } shifted)
                    {
                        var count = LlvmConstantInt.Get((LlvmIntegerType)instruction.Type, amount);
                        if (LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.LShr, original, count, DL, Metrics) is { } shiftedConstant &&
                            LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Shl, shiftedConstant, count, DL, Metrics) == original)
                            return InsertNewInstWith(new LlvmBinaryOperator(LlvmOpcode.Shl, shiftedConstant, shifted.Right), instruction);
                    }
                    if (instruction.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasOneUse: true, Right: LlvmConstantInt factor } multiply && factor.Value.CountTrailingZeros() >= amount)
                        return InsertNewInstWith(new LlvmBinaryOperator(LlvmOpcode.Mul, multiply.Left,
                            LlvmConstantInt.Get(instruction.Context, factor.Value.LShr(amount))), instruction);
                }
                if (SimplifyDemandedBits(instruction, 0, demanded.Shl(amount), ref known, query, depth + 1))
                { instruction.DropPoisonGeneratingFlags(); return instruction; }
                known = new(known.Zero.LShr(amount) | LlvmAPInt.GetHighBitsSet(width, amount), known.One.LShr(amount));
                return null;
            }
            if (DemandedNumSignBits(instruction.Left, query, depth + 1) >= width - demanded.CountTrailingZeros()) return instruction.Left;
            if (demanded.IsOne) return InsertNewInstWith(new LlvmBinaryOperator(LlvmOpcode.LShr, instruction.Left, instruction.Right), instruction);
            if (instruction.Right is LlvmConstantInt signAmount)
            {
                uint amount = (uint)signAmount.Value.GetLimitedValue(width - 1);
                var input = demanded.Shl(amount);
                bool shiftedIn = demanded.CountLeadingZeros() < amount;
                if (shiftedIn) input |= LlvmAPInt.GetSignedMinValue(width);
                if (SimplifyDemandedBits(instruction, 0, input, ref known, query, depth + 1))
                { instruction.DropPoisonGeneratingFlags(); return instruction; }
                if (known.Zero.IsNegative || !shiftedIn)
                {
                    var logical = new LlvmBinaryOperator(LlvmOpcode.LShr, instruction.Left, instruction.Right) { IsExact = instruction.IsExact };
                    logical.TakeName(instruction); return InsertNewInstWith(logical, instruction);
                }
                known = LlvmKnownBits.Shift(LlvmOpcode.AShr, known, LlvmKnownBits.MakeConstant(new LlvmAPInt(width, amount)), exact: instruction.IsExact, nonZero: amount != 0);
            }
            else known = ComputeDemandedKnownBits(instruction, query, depth);
            return null;
        }

        private bool PreserveDemandedRotate(LlvmBinaryOperator instruction)
        {
            if (!instruction.HasOneUse || instruction.Users.First() is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or } combine) return false;
            var conversion = ConvertOrOfShiftsToFunnelShift(combine);
            return conversion is { } result && (result.Kind == LlvmIntrinsicKind.FShl || result.Kind == LlvmIntrinsicKind.FShr) && result.Arguments[0] == result.Arguments[1];
        }

        private LlvmValue? SimplifyShrShlDemandedBits(LlvmBinaryOperator shiftRight, LlvmAPInt rightAmount,
            LlvmBinaryOperator shiftLeft, LlvmAPInt leftAmount, LlvmAPInt demanded, ref LlvmKnownBits known)
        {
            uint width = demanded.BitWidth;
            if (leftAmount.IsZero || rightAmount.IsZero || leftAmount.Uge(new LlvmAPInt(width, width)) || rightAmount.Uge(new LlvmAPInt(width, width))) return null;
            uint left = (uint)leftAmount.GetZExtValue(), right = (uint)rightAmount.GetZExtValue();
            known = new((known.Zero | LlvmAPInt.GetLowBitsSet(width, left - 1)) & demanded, LlvmAPInt.GetZero(width));
            var ones = LlvmAPInt.GetAllOnes(width);
            bool logical = shiftRight.Opcode == LlvmOpcode.LShr;
            var originalMask = (logical ? ones.LShr(right) : ones.AShr(right)).Shl(left);
            var newMask = right <= left ? ones.Shl(left - right) : logical ? ones.LShr(right - left) : ones.AShr(right - left);
            if ((originalMask & demanded) != (newMask & demanded)) return null;
            if (right == left) return shiftRight.Left;
            if (!shiftRight.HasOneUse) return null;
            LlvmBinaryOperator result;
            if (right < left)
                result = new(LlvmOpcode.Shl, shiftRight.Left, LlvmConstantInt.Get((LlvmIntegerType)shiftRight.Left.Type, left - right))
                { HasNoSignedWrap = shiftLeft.HasNoSignedWrap, HasNoUnsignedWrap = shiftLeft.HasNoUnsignedWrap };
            else result = new(logical ? LlvmOpcode.LShr : LlvmOpcode.AShr, shiftRight.Left,
                LlvmConstantInt.Get((LlvmIntegerType)shiftRight.Left.Type, right - left)) { IsExact = shiftRight.IsExact };
            return InsertNewInstWith(result, shiftLeft);
        }

        private LlvmValue? SimplifyShiftSelectingPackedElement(LlvmBinaryOperator instruction, LlvmAPInt demanded,
            LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            if (instruction.Left is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or, IsDisjoint: true, HasOneUse: true } packed) return null;
            LlvmBinaryOperator? upper = packed.Left as LlvmBinaryOperator;
            LlvmValue lower = packed.Right;
            if (upper is not { Opcode: LlvmOpcode.Shl, HasOneUse: true, Right: LlvmConstantInt })
            { upper = packed.Right as LlvmBinaryOperator; lower = packed.Left; }
            if (upper is not { Opcode: LlvmOpcode.Shl, HasOneUse: true, Right: LlvmConstantInt constant } || constant.Value.TryZExtValue() is not { } amount ||
                !System.Numerics.BitOperations.IsPow2(amount) || demanded.GetActiveBits() > amount ||
                upper.Left.Type.GetIntegerBitWidth() < amount + demanded.GetActiveBits()) return null;
            var lowerBits = ComputeDemandedKnownBits(lower, query, depth);
            if (lowerBits.GetMaxValue().GetActiveBits() > amount) return null;
            var shiftBits = ComputeDemandedKnownBits(instruction.Right, query, depth);
            if ((~shiftBits.Zero).TryZExtValue() != amount) return null;
            using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
            Builder.SetInsertPoint(instruction);
            var condition = Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, instruction.Right, LlvmConstant.GetNullValue(instruction.Right.Type), instruction.Right.Name + ".z");
            var result = Builder.CreateSelect(condition, lower, upper.Left);
            result.TakeName(instruction);
            return result;
        }
    }
}
