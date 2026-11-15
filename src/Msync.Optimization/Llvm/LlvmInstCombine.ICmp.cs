// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp visitICmpInst.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitICmp(LlvmICmpInstruction compare)
        {
            if (compare.Left.Type is not (LlvmIntegerType or LlvmPointerType)) return null;
            Count("icmp.visits");
            bool changed = false;
            var query = Query(compare);
            var left = compare.Left; var right = compare.Right;
            if (GetComplexity(left) < GetComplexity(right))
            { compare.SwapOperands(); (left, right) = (right, left); changed = true; Count("icmp.commuted"); }
            if (LlvmInstructionSimplify.SimplifyICmp(compare.Predicate, left, right, query) is { } simplified) return ReplaceInstUsesWith(compare, simplified);
            if (FoldICmpTruncWithTruncOrExt(compare, query) is { } truncated) return truncated;
            if (left.Type.IsIntegerTy(1)) return CanonicalizeICmpBool(compare);
            if (CanonicalizeCmpWithConstant(compare) is { } strictness) return strictness;
            if (CanonicalizeICmpPredicate(compare) is { } canonical) return canonical;
            if (FoldICmpWithConstant(compare) is { } constant) return constant;
            if (FoldICmpWithDominatingICmp(compare) is { } dominated) return dominated;
            if (FoldICmpUsingBoolRange(compare) is { } booleanRange) return booleanRange;
            if (FoldICmpUsingKnownBits(compare) is { } known) return known;
            if (FoldIsMultipleOfAPowerOfTwo(compare) is { } multiple) return multiple;
            if (compare.HasOneUse && compare.Users.First() is LlvmSelectInstruction pattern &&
                LlvmSelectPattern.Match(pattern, Metrics).Flavor != LlvmSelectPatternFlavor.Unknown) return null;
            if (FoldICmpWithZero(compare) is { } zero) return zero;
            var predicate = compare.Predicate;
            if (CallsIntrinsic(left, LlvmIntrinsicKind.UMax, out var maximum) && maximum.GetArgument(1) is LlvmConstantInt bound &&
                ICmpMatchNot(right) == maximum.GetArgument(0))
            {
                var value = maximum.GetArgument(0);
                if (predicate == LlvmCmpPredicate.ICmpUgt)
                    return !bound.Value.IsNegative ? new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSlt, value, ICmpZero(value))
                        : new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUgt, value, LlvmConstantInt.Get(value.Context, ~bound.Value));
                if (predicate == LlvmCmpPredicate.ICmpUlt)
                    return !bound.Value.IsNegative ? new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSgt, value, ICmpOnes(value))
                        : new LlvmICmpInstruction(LlvmCmpPredicate.ICmpUlt, value, LlvmConstantInt.Get(value.Context, ~bound.Value));
            }
            if (right is LlvmConstantInt sign)
            {
                if (predicate == LlvmCmpPredicate.ICmpUgt && sign.Value.IsMaxSignedValue) return new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSlt, left, ICmpZero(left));
                if (predicate == LlvmCmpPredicate.ICmpUlt && sign.Value.IsMinSignedValue) return new LlvmICmpInstruction(LlvmCmpPredicate.ICmpSgt, left, ICmpOnes(left));
            }
            if (FoldICmpBinOp(compare, query) is { } binop) return binop;
            if (FoldICmpInstWithConstant(compare) is { } withConstant) return withConstant;
            if (FoldSignBitTest(compare) is { } signBit) return signBit;
            if (left is LlvmPhiInstruction lphi && FoldOpIntoPhi(compare, lphi) is { } lthread) return lthread;
            if (right is LlvmPhiInstruction rphi && FoldOpIntoPhi(compare, rphi) is { } rthread) return rthread;
            if (FoldICmpInstWithConstantNotInt(compare) is { } notInteger) return notInteger;
            if (FoldICmpCommutative(predicate, left, right, compare) is { } commute) return commute;
            if (FoldICmpCommutative(LlvmCmpInstruction.GetSwappedPredicate(predicate), right, left, compare) is { } swapped) return swapped;
            if (ICmpIsEquality(predicate) && MatchSymmetricPair(compare.Left, compare.Right) is { } pair)
            { ReplaceOperand(compare, 0, pair.First); ReplaceOperand(compare, 1, pair.Second); return compare; }
            if (FoldCmpSelectOfConstants(compare) is { } table) return table;
            if (left is LlvmSelectInstruction a && right is LlvmSelectInstruction b && a.Condition == b.Condition && (left.HasOneUse || right.HasOneUse))
            {
                if (LlvmInstructionSimplify.SimplifyICmp(predicate, a.TrueValue, b.TrueValue, SQ) is { } whenTrue)
                    return new LlvmSelectInstruction(a.Condition, whenTrue, Builder.CreateICmp(predicate, a.FalseValue, b.FalseValue)) { Source = a.Source };
                if (LlvmInstructionSimplify.SimplifyICmp(predicate, a.FalseValue, b.FalseValue, SQ) is { } whenFalse)
                    return new LlvmSelectInstruction(a.Condition, Builder.CreateICmp(predicate, a.TrueValue, b.TrueValue), whenFalse) { Source = a.Source };
            }
            for (int side = 0; side < 2; ++side)
            {
                var subValue = side == 0 ? left : right; var addValue = side == 0 ? right : left;
                var matchedPredicate = side == 0 ? predicate : LlvmCmpInstruction.GetSwappedPredicate(predicate);
                if (subValue is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } sub || addValue is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add ||
                    !(sub.Left == add.Left && sub.Right == add.Right || sub.Left == add.Right && sub.Right == add.Left)) continue;
                if (ICmpIsUnsigned(predicate) && sub.HasNoUnsignedWrap && add.HasNoUnsignedWrap ||
                    ICmpIsSigned(predicate) && sub.HasNoSignedWrap && add.HasNoSignedWrap ||
                    ICmpIsEquality(predicate) && (sub.HasNoUnsignedWrap || sub.HasNoSignedWrap) && (add.HasNoUnsignedWrap || add.HasNoSignedWrap))
                    return new LlvmICmpInstruction(LlvmCmpInstruction.GetSwappedPredicate(matchedPredicate), sub.Right, ICmpZero(sub.Right)) { SameSign = compare.SameSign };
            }
            if (left.Type.IsPointerTy() && ICmpIsEquality(predicate))
            {
                if (LlvmValueTracking.GetUnderlyingObject(left, metrics: Metrics) is LlvmAllocaInstruction lalloca && FoldAllocaCmp(lalloca)) return null;
                if (LlvmValueTracking.GetUnderlyingObject(right, metrics: Metrics) is LlvmAllocaInstruction ralloca && FoldAllocaCmp(ralloca)) return null;
            }
            if (FoldICmpBitCast(compare) is { } bitcast) return bitcast;
            if (FoldICmpWithCastOp(compare) is { } cast) return cast;
            if (compare.Right is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } extension && compare.Left is LlvmBinaryOperator
                { Opcode: LlvmOpcode.And, HasOneUse: true, Left: LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } truncation, Right: LlvmConstantInt mask })
            {
                uint width = extension.Operand.Type.GetIntegerBitWidth();
                if (mask.Value == LlvmAPInt.GetLowBitsSet(mask.Value.BitWidth, width) && ShouldChangeType(compare.Left.Type, extension.Operand.Type))
                    return new LlvmICmpInstruction(ICmpUnsignedPredicate(predicate), Builder.CreateTrunc(truncation.Operand, extension.Operand.Type), extension.Operand);
            }
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and && ICmpMatchNot(and.Right) is { } inverse &&
                right is LlvmConstantInt { IsZero: true } && ICmpIsEquality(predicate) &&
                LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(and.Left, false, context: compare, tree: DT, metrics: Metrics))
                return new LlvmICmpInstruction(LlvmCmpInstruction.GetInversePredicate(predicate), Builder.CreateAnd(and.Left, inverse), right);
            if (left.Type.IsIntegerTy() && IsFreeToInvert(left, left.HasOneUse, out bool consumeLeft) &&
                IsFreeToInvert(right, right.HasOneUse, out bool consumeRight) && (consumeLeft || consumeRight))
                return new LlvmICmpInstruction(LlvmCmpInstruction.GetSwappedPredicate(predicate),
                    GetFreelyInverted(left, left.HasOneUse, Builder)!, GetFreelyInverted(right, right.HasOneUse, Builder)!) { SameSign = compare.SameSign };
            if (ICmpMatchUAddWithOverflow(compare, out var x, out var y, out var operation) && x.Type.IsIntegerTy() && operation.Opcode == LlvmOpcode.Add &&
                OptimizeOverflowCheck(LlvmOpcode.Add, false, x, y, operation, out var result, out var overflow))
            {
                ReplaceInstUsesWith(operation, result); EraseInstFromFunction(operation);
                return ReplaceInstUsesWith(compare, overflow);
            }
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add or LlvmOpcode.Mul, HasNoUnsignedWrap: true,
                Left: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt }, Right: LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } } &&
                right is LlvmConstantInt extendedConstant && ProcessUZExtIdiom(compare, left, extendedConstant.Value) is { } widened) return widened;
            if ((ICmpIsUnsigned(predicate) || ICmpIsEquality(predicate)) && right is LlvmCastInstruction
                { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } ext && ext.Operand.Type.IsIntegerTy(1) && (left.HasOneUse || right.HasOneUse) &&
                left is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr or LlvmOpcode.AShr, Right: LlvmConstantInt signShift } shifted &&
                signShift.Value.GetLimitedValue() == left.Type.GetIntegerBitWidth() - 1 &&
                (ext.Opcode == LlvmOpcode.ZExt && shifted.Opcode == LlvmOpcode.LShr || ext.Opcode == LlvmOpcode.SExt && shifted.Opcode == LlvmOpcode.AShr))
            {
                var signTest = Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, shifted.Left, ICmpZero(shifted.Left));
                return ReplaceInstUsesWith(compare, Builder.CreateICmp(predicate, signTest, ext.Operand, compare.Name));
            }
            if (FoldICmpEquality(compare) is { } equality) return equality;
            if (FoldICmpPow2Test(compare) is { } power) return power;
            if (FoldICmpOfUAddOv(compare) is { } carry) return carry;
            if (FoldICmpWithHighBitMask(compare) is { } high) return high;
            if (compare.Type.IsVectorTy() && FoldVectorCmp(compare) is { } vector) return vector;
            if (FoldICmpInvariantGroup(compare) is { } invariant) return invariant;
            if (FoldReductionIdiom(compare) is { } reduction) return reduction;
            if (ICmpIsEquality(predicate) && left is LlvmBinaryOperator { Opcode: LlvmOpcode.And,
                Left: LlvmCastInstruction { Opcode: LlvmOpcode.SExt } sext, Right: LlvmConstantInt outerMask } && right is LlvmConstantInt rhs)
            {
                uint width = sext.Operand.Type.GetIntegerBitWidth();
                if (rhs.Value.GetActiveBits() < width)
                {
                    var truncatedMask = outerMask.Value.Trunc(width);
                    if (outerMask.Value.Uge(LlvmAPInt.GetOneBitSet(outerMask.Value.BitWidth, width))) truncatedMask |= LlvmAPInt.GetSignedMinValue(width);
                    var masked = Builder.CreateAnd(sext.Operand, LlvmConstantInt.Get(compare.Context, truncatedMask));
                    return new LlvmICmpInstruction(predicate, masked, LlvmConstantInt.Get(compare.Context, rhs.Value.Trunc(width)));
                }
            }
            return changed ? compare : null;
        }
    }
}
