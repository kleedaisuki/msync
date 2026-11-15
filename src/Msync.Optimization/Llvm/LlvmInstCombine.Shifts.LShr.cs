// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineShifts.cpp visitLShr.
using System.Numerics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitLShr(LlvmBinaryOperator instruction)
        {
            Count("instcombine.shift.lshr.visits");
            if (LlvmInstructionSimplify.SimplifyInstruction(instruction, Query(instruction)) is { } simplified) return ReplaceInstUsesWith(instruction, simplified);
            if (CommonShiftTransforms(instruction) is { } common) return common;
            LlvmValue operand = instruction.Left, amount = instruction.Right;
            LlvmType type = instruction.Type;
            uint width = type.GetScalarSizeInBits();
            if (operand is LlvmConstantInt { IsOne: true })
                return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, amount, LlvmConstant.GetNullValue(type)), type), "instcombine.shift.lshr.one_to_iszero");
            if (operand.HasOneUse && ShiftNot(operand, out LlvmValue inverted) && amount is LlvmConstantInt sign && sign.Value.GetLimitedValue(width) == width - 1)
                return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpSgt, inverted, ShiftInt(type, LlvmAPInt.GetAllOnes(width))), type), "instcombine.shift.lshr.not_signbit");
            if (instruction.IsExact && operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoUnsignedWrap: true, HasOneUse: true } subtract)
            {
                if (subtract.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasNoUnsignedWrap: true } left && ReferenceEquals(left.Right, amount))
                {
                    LlvmValue rhs = Builder.CreateBinary(LlvmOpcode.LShr, subtract.Right, amount, exact: true);
                    return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Sub, left.Left, rhs) { HasNoUnsignedWrap = true, HasNoSignedWrap = subtract.HasNoSignedWrap }, "instcombine.shift.lshr.exact_sub_left");
                }
            }
            if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add && amount is LlvmConstantInt { IsOne: true } &&
                ShiftKnown(add.Left, instruction).CountMaxActiveBits() <= 1 && ShiftKnown(add.Right, instruction).CountMaxActiveBits() <= 1)
                return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, add.Left, add.Right), "instcombine.shift.lshr.add_booleans");
            if (instruction.IsExact && operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoUnsignedWrap: true, HasOneUse: true, Right: LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasNoUnsignedWrap: true } right } sub && ReferenceEquals(right.Right, amount))
            {
                LlvmValue lhs = Builder.CreateBinary(LlvmOpcode.LShr, sub.Left, amount, exact: true);
                return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Sub, lhs, right.Left) { HasNoUnsignedWrap = true, HasNoSignedWrap = sub.HasNoSignedWrap }, "instcombine.shift.lshr.exact_sub_right");
            }
            if (operand is LlvmBinaryOperator binary && binary.HasOneUse && binary.Opcode is LlvmOpcode.Add or LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor)
            {
                LlvmValue shiftCandidate = binary.Left, y = binary.Right;
                bool MatchNuwShl(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasNoUnsignedWrap: true } shift && ReferenceEquals(shift.Right, amount);
                if (!MatchNuwShl(shiftCandidate)) (shiftCandidate, y) = (y, shiftCandidate);
                if (MatchNuwShl(shiftCandidate) && (binary.Opcode != LlvmOpcode.Add || binary.HasNoUnsignedWrap))
                {
                    LlvmValue shifted = Builder.CreateBinary(LlvmOpcode.LShr, y, amount, exact: instruction.IsExact && binary.Opcode != LlvmOpcode.And);
                    var result = new LlvmBinaryOperator(binary.Opcode, shifted, ((LlvmBinaryOperator)shiftCandidate).Left);
                    if (binary.Opcode == LlvmOpcode.Add) { result.HasNoUnsignedWrap = true; result.HasNoSignedWrap = binary.HasNoSignedWrap; }
                    else if (binary.Opcode == LlvmOpcode.Or) result.IsDisjoint = binary.IsDisjoint;
                    return ShiftResult(result, "instcombine.shift.lshr.distribute_nuw_left");
                }
            }
            if (amount is LlvmConstantInt constant)
            {
                uint bits = (uint)constant.Value.GetZExtValue();
                LlvmAPInt lowMask = LlvmAPInt.GetLowBitsSet(width, width - bits);
                if ((width & (width - 1)) == 0 && BitOperations.Log2(width) == bits &&
                    (ShiftIntrinsic(operand, LlvmIntrinsicKind.CTLZ, out var count) || ShiftIntrinsic(operand, LlvmIntrinsicKind.CTTZ, out count) || ShiftIntrinsic(operand, LlvmIntrinsicKind.CTPop, out count)))
                {
                    bool pop = count.CalledFunction!.IntrinsicDescriptor!.Kind == LlvmIntrinsicKind.CTPop;
                    LlvmValue compare = Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, count.GetArgument(0), ShiftInt(type, pop ? LlvmAPInt.GetAllOnes(width) : LlvmAPInt.GetZero(width)));
                    return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, compare, type), "instcombine.shift.lshr.count_extreme");
                }
                if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt innerAmount } left && innerAmount.Value.GetLimitedValue(width) < width)
                {
                    uint innerBits = (uint)innerAmount.Value.GetZExtValue();
                    if (innerBits < bits)
                    {
                        var result = new LlvmBinaryOperator(LlvmOpcode.LShr, left.Left, ShiftInt(type, bits - innerBits)) { IsExact = instruction.IsExact };
                        if (left.HasNoUnsignedWrap) return ShiftResult(result, "instcombine.shift.lshr.reduce_nuw_left");
                        if (left.HasOneUse)
                        { Builder.Insert(result); return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, result, ShiftInt(type, lowMask)), "instcombine.shift.lshr.reduce_left_mask"); }
                        result.DropAllReferences();
                    }
                    else if (innerBits > bits)
                    {
                        if (left.HasNoUnsignedWrap)
                            return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Shl, left.Left, ShiftInt(type, innerBits - bits)) { HasNoUnsignedWrap = true, HasNoSignedWrap = bits > 0 }, "instcombine.shift.lshr.cancel_nuw_left");
                        if (left.HasOneUse)
                            return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateBinOp(LlvmOpcode.Shl, left.Left, ShiftInt(type, innerBits - bits)), ShiftInt(type, lowMask)), "instcombine.shift.lshr.cancel_left_mask");
                    }
                    else return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, left.Left, ShiftInt(type, lowMask)), "instcombine.shift.lshr.equal_opposite_mask");
                }
                if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, HasOneUse: true } addition)
                {
                    LlvmValue possible = addition.Left, y = addition.Right;
                    bool Match(LlvmValue value) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasOneUse: true } shift && ReferenceEquals(shift.Right, amount);
                    if (!Match(possible)) (possible, y) = (y, possible);
                    if (Match(possible))
                    {
                        LlvmValue sum = Builder.CreateAdd(Builder.CreateBinOp(LlvmOpcode.LShr, y, amount), ((LlvmBinaryOperator)possible).Left);
                        return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, sum, ShiftInt(type, lowMask)), "instcombine.shift.lshr.add_left_mask");
                    }
                }
                if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } extension && ShouldChangeType(type, extension.Operand.Type))
                    return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateBinOp(LlvmOpcode.LShr, extension.Operand, ShiftInt(extension.Operand.Type, bits)), type), "instcombine.shift.lshr.narrow_zext");
                if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } signedExtension)
                {
                    LlvmValue x = signedExtension.Operand;
                    uint sourceWidth = x.Type.GetScalarSizeInBits();
                    if (sourceWidth == 1)
                        return ShiftResult(new LlvmSelectInstruction(x, ShiftInt(type, lowMask), LlvmConstant.GetNullValue(type)), "instcombine.shift.lshr.boolean_sext_select");
                    if (ShouldChangeType(type, x.Type) && signedExtension.HasOneUse)
                    {
                        if (bits == width - 1)
                            return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateBinOp(LlvmOpcode.LShr, x, ShiftInt(x.Type, sourceWidth - 1)), type), "instcombine.shift.lshr.extract_sext_sign");
                        if (bits == width - sourceWidth)
                            return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateBinOp(LlvmOpcode.AShr, x, ShiftInt(x.Type, Math.Min(bits, sourceWidth - 1))), type), "instcombine.shift.lshr.narrow_sext");
                    }
                }
                if (bits == width - 1 && operand.HasOneUse)
                {
                    if (ShiftOrNegation(operand, out var x))
                        return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpNe, x, LlvmConstant.GetNullValue(type)), type), "instcombine.shift.lshr.nonzero_sign");
                    if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true } subtraction)
                        return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, subtraction.Left, subtraction.Right), type), "instcombine.shift.lshr.signed_compare");
                    if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.SRem, Right: LlvmConstantInt remainder } rem && remainder.Value == new LlvmAPInt(width, 2))
                        return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, Builder.CreateBinOp(LlvmOpcode.LShr, rem.Left, amount), rem.Left), "instcombine.shift.lshr.negative_odd");
                    if (ShiftDecrementAndNot(operand, out var zero))
                        return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateICmp(LlvmCmpPredicate.ICmpEq, zero, LlvmConstant.GetNullValue(type)), type), "instcombine.shift.lshr.zero_sign");
                }
                if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasOneUse: true, Operand: LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt truncAmount } inner } truncation)
                {
                    uint sourceWidth = inner.Left.Type.GetScalarSizeInBits();
                    ulong firstBits = truncAmount.Value.GetZExtValue();
                    ulong sum = bits + firstBits;
                    if (sum < sourceWidth && (inner.HasOneUse || firstBits >= sourceWidth - width))
                    {
                        LlvmValue combined = Builder.CreateBinOp(LlvmOpcode.LShr, inner.Left, ShiftInt(inner.Left.Type, sum), "sum.shift");
                        LlvmValue truncated = Builder.CreateTrunc(combined, type, instruction.Name);
                        return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, truncated, ShiftInt(type, lowMask)), "instcombine.shift.lshr.truncated_composite");
                    }
                }
                if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasNoUnsignedWrap: true, Right: LlvmConstantInt multiplier } multiply)
                {
                    if (width > 2 && (multiplier.Value - new LlvmAPInt(width, 1)).IsPowerOf2 && multiplier.Value.GetActiveBits() - 1 == bits)
                    {
                        if (bits * 2 == width) return ReplaceInstUsesWith(instruction, multiply.Left);
                        if (multiply.HasOneUse)
                            return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Add, multiply.Left, Builder.CreateBinary(LlvmOpcode.LShr, multiply.Left, amount, exact: instruction.IsExact))
                            { HasNoUnsignedWrap = true, HasNoSignedWrap = multiply.HasNoSignedWrap }, "instcombine.shift.lshr.nuw_multiply_splat");
                    }
                    LlvmAPInt newConstant = multiplier.Value.LShr(bits);
                    if (multiply.HasOneUse && multiplier.Value == newConstant.Shl(bits))
                        return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Mul, multiply.Left, ShiftInt(type, newConstant)) { HasNoUnsignedWrap = true, HasNoSignedWrap = true }, "instcombine.shift.lshr.divide_nuw_multiplier");
                }
                if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Mul, HasNoSignedWrap: true, HasOneUse: true, Right: LlvmConstantInt signedMultiplier } signedMultiply &&
                    width > 2 && (signedMultiplier.Value - new LlvmAPInt(width, 1)).IsPowerOf2 && signedMultiplier.Value.GetActiveBits() - 1 == bits)
                    return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Add, signedMultiply.Left, Builder.CreateBinary(LlvmOpcode.LShr, signedMultiply.Left, amount, exact: instruction.IsExact)) { HasNoSignedWrap = true }, "instcombine.shift.lshr.nsw_multiply_splat");
                if (operand.HasOneUse && ShiftIntrinsic(operand, LlvmIntrinsicKind.BSwap, out var swap) &&
                    swap.GetArgument(0) is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, HasOneUse: true } swapExtension)
                {
                    LlvmValue x = swapExtension.Operand;
                    uint sourceWidth = x.Type.GetScalarSizeInBits();
                    uint difference = width - sourceWidth;
                    if (sourceWidth % 16 == 0)
                    {
                        LlvmValue narrowSwap = Builder.CreateIntrinsic(LlvmIntrinsicKind.BSwap, [x]);
                        if (bits >= difference)
                            return ShiftResult(new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateBinOp(LlvmOpcode.LShr, narrowSwap, ShiftInt(x.Type, bits - difference)), type), "instcombine.shift.lshr.narrow_bswap_right");
                        LlvmValue extended = Builder.CreateZExt(narrowSwap, type);
                        return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.Shl, extended, ShiftInt(type, difference - bits)), "instcombine.shift.lshr.narrow_bswap_left");
                    }
                }
            }
            if (SetShiftFlags(instruction)) return instruction;
            if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, HasOneUse: true } variableLeft && ReferenceEquals(variableLeft.Right, amount))
            {
                LlvmValue mask = Builder.CreateBinOp(LlvmOpcode.LShr, ShiftInt(type, LlvmAPInt.GetAllOnes(width)), amount);
                return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.And, mask, variableLeft.Left), "instcombine.shift.lshr.variable_cancel_mask");
            }
            if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Left: LlvmConstantInt { IsMinusOne: true } } allOnes && ReferenceEquals(allOnes.Right, amount))
                return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.LShr, ShiftInt(type, LlvmAPInt.GetAllOnes(width)), amount), "instcombine.shift.lshr.variable_allones");
            if (FoldLShrOverflowBit(instruction) is { } overflow) return overflow;
            if (operand is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } shl0 && ShiftIntrinsic(amount, LlvmIntrinsicKind.CTTZ, out var trailing) &&
                trailing.GetArgument(0) is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl } shl1 && ReferenceEquals(shl0.Left, shl1.Left) &&
                LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(shl0.Left, orZero: true, context: instruction, tree: DT, metrics: Metrics, useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals))
            {
                bool nuw = shl0.HasNoUnsignedWrap && shl1.HasNoUnsignedWrap;
                bool nsw = shl0.HasNoSignedWrap && shl1.HasNoSignedWrap;
                if (nuw || nsw)
                {
                    LlvmValue normalized = Builder.CreateBinary(LlvmOpcode.Shl, ShiftInt(shl1.Type, 1), shl0.Right, nuw: nuw, nsw: nsw);
                    return ShiftResult(new LlvmBinaryOperator(LlvmOpcode.LShr, normalized, shl1.Right), "instcombine.shift.lshr.normalize_cttz_power");
                }
            }
            return null;
        }
    }
}
