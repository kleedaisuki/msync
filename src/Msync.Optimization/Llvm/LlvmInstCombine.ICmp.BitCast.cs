// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp 3509–3677.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldICmpBitCast(LlvmICmpInstruction compare)
        {
            if (compare.Left is not LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } bitcast) return null;
            var predicate = compare.Predicate;
            var rhs = compare.Right;
            var operand = bitcast.Operand;
            var sourceType = operand.Type;
            var destinationType = bitcast.Type;
            if (sourceType.IsVectorTy() == destinationType.IsVectorTy() &&
                sourceType.GetScalarSizeInBits() == destinationType.GetScalarSizeInBits())
            {
                if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.SIToFP } signed)
                {
                    if (predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpNe or LlvmCmpPredicate.ICmpSgt &&
                        ICmpConstantMatches(rhs, static bits => bits.IsZero))
                        return Rewrite(predicate, signed.Operand, ICmpZero(signed.Operand), "sitofp_zero");
                    if (predicate == LlvmCmpPredicate.ICmpSlt && ICmpConstantMatches(rhs, static bits => bits.IsOne))
                        return Rewrite(predicate, signed.Operand, LlvmConstantInt.Get(signed.Operand.Type,
                            new LlvmAPInt(signed.Operand.Type.GetScalarSizeInBits(), 1)), "sitofp_one");
                    if (predicate == LlvmCmpPredicate.ICmpSgt && ICmpConstantMatches(rhs, static bits => bits.IsAllOnes))
                        return Rewrite(predicate, signed.Operand, ICmpOnes(signed.Operand), "sitofp_ones");
                }
                if (operand is LlvmCastInstruction { Opcode: LlvmOpcode.UIToFP } unsigned && ICmpIsEquality(predicate) &&
                    ICmpConstantMatches(rhs, static bits => bits.IsZero))
                    return Rewrite(predicate, unsigned.Operand, ICmpZero(unsigned.Operand), "uitofp_zero");
                if (bitcast.HasOneUse && ICmpAPInt(rhs) is { } constant)
                {
                    if (LlvmValueTracking.IsSignBitCheck(predicate, constant.Value, out bool negative) &&
                        operand is LlvmCastInstruction { Opcode: LlvmOpcode.FPExt or LlvmOpcode.FPTrunc } floating &&
                        !floating.Operand.Type.IsPPC_FP128Ty() && !sourceType.IsPPC_FP128Ty())
                    {
                        var newType = floating.Operand.Type.GetWithNewType(LlvmIntegerType.Get(compare.Context, floating.Operand.Type.GetScalarSizeInBits()));
                        var cast = Builder.CreateBitCast(floating.Operand, newType);
                        return Rewrite(negative ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpSgt, cast,
                            negative ? LlvmConstant.GetNullValue(newType) : LlvmConstant.GetAllOnesValue(newType), "floating_sign");
                    }
                    if (!compare.Parent!.Parent!.NoImplicitFloat && ICmpIsEquality(predicate) && sourceType.GetScalarType().IsIEEELikeFPTy())
                    {
                        var value = new LlvmAPFloat(sourceType.GetScalarType().GetFltSemantics(), constant.Value);
                        LlvmFPClassTest mask = value.IsInfinity ? value.IsNegative ? LlvmFPClassTest.NegInfinity : LlvmFPClassTest.PosInfinity
                            : value.IsZero ? value.IsNegative ? LlvmFPClassTest.NegZero : LlvmFPClassTest.PosZero : LlvmFPClassTest.None;
                        if (mask != LlvmFPClassTest.None)
                        {
                            if (predicate == LlvmCmpPredicate.ICmpNe) mask = LlvmFPClassTest.All & ~mask;
                            Count("icmp.bitcast.fpclass");
                            return ReplaceInstUsesWith(compare, Builder.CreateIsFPClass(operand, mask));
                        }
                    }
                }
            }
            if (ICmpAPInt(rhs) is not { } scalarConstant || !destinationType.IsIntegerTy() || !sourceType.IsIntOrIntVectorTy()) return null;
            if (ICmpIsEquality(predicate) && scalarConstant.IsMinusOne && bitcast.HasOneUse &&
                GetFreelyInverted(operand, operand.HasOneUse, Builder) is { } inverted)
                return Rewrite(predicate, Builder.CreateBitCast(inverted, destinationType), LlvmConstant.GetNullValue(destinationType), "invert_all_lanes");
            if (ICmpIsEquality(predicate) && scalarConstant.IsZero && bitcast.HasOneUse &&
                operand is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt, Operand.Type: LlvmFixedVectorType narrow } extension)
            {
                var newType = LlvmIntegerType.Get(compare.Context, narrow.NumElements * narrow.ElementType.GetScalarSizeInBits());
                return Rewrite(predicate, Builder.CreateBitCast(extension.Operand, newType), LlvmConstant.GetNullValue(newType), "narrow_zero");
            }
            if (operand is LlvmShuffleVectorInstruction { Right: LlvmUndefValue, Left.Type: LlvmVectorType { ElementType: LlvmIntegerType element } } shuffle &&
                shuffle.ShuffleMask.All(lane => lane == shuffle.ShuffleMask[0]))
            {
                uint width = element.BitWidth;
                var pattern = scalarConstant.Value.Trunc(width);
                if (IsRepeatedPattern(scalarConstant.Value, pattern))
                    return Rewrite(predicate, Builder.CreateExtractElement(shuffle.Left, unchecked((ulong)shuffle.ShuffleMask[0])),
                        LlvmConstantInt.Get(element, pattern), "extract_splat");
            }
            return null;

            static bool IsRepeatedPattern(LlvmAPInt value, LlvmAPInt pattern)
            {
                for (uint offset = 0; offset < value.BitWidth; offset += pattern.BitWidth)
                    if (value.ExtractBits(pattern.BitWidth, offset) != pattern) return false;
                return true;
            }

            LlvmInstruction Rewrite(LlvmCmpPredicate pred, LlvmValue left, LlvmValue right, string kind)
            {
                if (Metrics is not null) Count("icmp.bitcast." + kind);
                return new LlvmICmpInstruction(pred, left, right);
            }
        }
    }
}
