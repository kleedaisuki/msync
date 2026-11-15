// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp foldGEPICmp.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldGEPICmp(LlvmGepOperator left, LlvmValue right,
            LlvmCmpPredicate predicate, LlvmInstruction context, bool sameSign = false)
        {
            if (ICmpIsSigned(predicate)) return null;
            if (right is not LlvmGetElementPtrInstruction) right = LlvmValueTracking.StripPointerCasts(right, Metrics);
            bool CanFold(LlvmGepNoWrapFlags flags) => ICmpIsEquality(predicate) || flags != LlvmGepNoWrapFlags.None;
            LlvmICmpInstruction NewCompare(LlvmGepNoWrapFlags flags, LlvmValue a, LlvmValue b) => !flags.HasNoUnsignedWrap
                ? new(ICmpSignedPredicate(predicate), a, b)
                : new(predicate, a, b) { SameSign = flags.HasNoUnsignedSignedWrap };
            var common = LlvmCommonPointerBase.Compute(left.User, right, Metrics);
            var indexType = DL.GetIndexType(left.Context, ((LlvmPointerType)left.Type).AddressSpace);
            if (common.Ptr == right && CanFold(common.LHSNW) && !common.IsExpensive())
            {
                var offset = EmitGEPOffsets(common.LHSGEPs, common.LHSNW, indexType, true);
                Count("icmp.gep.offset_vs_zero");
                return NewCompare(common.LHSNW, offset, ICmpZero(offset));
            }
            if (left.NoWrapFlags.IsInBounds && ICmpIsEquality(predicate) && right is LlvmConstantPointerNull &&
                !LlvmFunction.NullPointerIsDefined(context.Parent!.Parent, ((LlvmPointerType)right.Type).AddressSpace))
            {
                Count("icmp.gep.inbounds_null");
                return new LlvmICmpInstruction(predicate, left.PointerOperand, LlvmConstantPointerNull.Get((LlvmPointerType)left.PointerOperand.Type)) { SameSign = sameSign };
            }
            if (!LlvmGepOperator.TryGet(right, out var rhs)) return TransformToIndexedCompare(left, right, predicate);
            var noWrap = left.NoWrapFlags & rhs.NoWrapFlags;
            if (left.PointerOperand != rhs.PointerOperand)
            {
                bool identicalIndices = left.IndexCount == rhs.IndexCount && left.PointerOperand.Type == rhs.PointerOperand.Type &&
                    left.SourceElementType == rhs.SourceElementType;
                if (identicalIndices)
                    for (int i = 0; i < left.IndexCount; ++i)
                        if (left.GetIndex(i) != rhs.GetIndex(i)) { identicalIndices = false; break; }
                if (identicalIndices && CanFold(noWrap))
                    return new LlvmICmpInstruction(predicate, left.PointerOperand, rhs.PointerOperand) { SameSign = sameSign };
                if (left.NoWrapFlags.IsInBounds && rhs.NoWrapFlags.IsInBounds && (left.HasAllConstantIndices || left.HasOneUse) &&
                    (rhs.HasAllConstantIndices || rhs.HasOneUse) &&
                    LlvmValueTracking.StripPointerCasts(left.PointerOperand, Metrics) == LlvmValueTracking.StripPointerCasts(rhs.PointerOperand, Metrics))
                {
                    var lo = EmitGEPOffset(left); var ro = EmitGEPOffset(rhs);
                    if (lo.Type != ro.Type)
                    {
                        if (lo.Type.GetIntegerBitWidth() < ro.Type.GetIntegerBitWidth()) ro = Builder.CreateTrunc(ro, lo.Type);
                        else lo = Builder.CreateTrunc(lo, ro.Type);
                    }
                    return ReplaceInstUsesWith(context, Builder.CreateICmp(ICmpSignedPredicate(predicate), lo, ro));
                }
            }
            if (left.PointerOperand == rhs.PointerOperand && left.IndexCount == rhs.IndexCount && left.SourceElementType == rhs.SourceElementType)
            {
                uint differences = 0; int differing = 0;
                for (int i = 0; i < left.IndexCount; ++i)
                {
                    if (left.GetIndex(i) == rhs.GetIndex(i)) continue;
                    if (left.GetIndex(i).Type.GetIntegerBitWidth() != rhs.GetIndex(i).Type.GetIntegerBitWidth()) { differences = 2; break; }
                    if (differences++ != 0) break;
                    differing = i;
                }
                if (differences == 0) return ReplaceInstUsesWith(context, LlvmConstantInt.GetBool(context.Context, ICmpTrueWhenEqual(predicate)));
                if (differences == 1 && noWrap != LlvmGepNoWrapFlags.None)
                    return NewCompare(noWrap, left.GetIndex(differing), rhs.GetIndex(differing));
            }
            if (common.Ptr is not null && !common.IsExpensive())
            {
                bool fold = CanFold(common.LHSNW & common.RHSNW);
                if (!fold && common.Ptr.Type.IsPointerTy())
                {
                    uint width = DL.GetIndexTypeSizeInBits(left.Type);
                    var alignment = new LlvmAPInt(width, LlvmValueTracking.GetPointerAlignment(common.Ptr, DL, Metrics).Value, implicitTrunc: true);
                    var lo = LlvmAPInt.GetZero(width); var ro = LlvmAPInt.GetZero(width);
                    if (LlvmValueTracking.StripAndAccumulateConstantOffsets(left.User, DL, ref lo, true, metrics: Metrics) == common.Ptr &&
                        LlvmValueTracking.StripAndAccumulateConstantOffsets(rhs.User, DL, ref ro, true, metrics: Metrics) == common.Ptr)
                        fold = ICmpRoundSDivDown(lo, alignment) == ICmpRoundSDivDown(ro, alignment);
                }
                if (fold)
                {
                    var lo = EmitGEPOffsets(common.LHSGEPs, common.LHSNW, indexType, true);
                    var ro = EmitGEPOffsets(common.RHSGEPs, common.RHSNW, indexType, true);
                    Count("icmp.gep.common_offset");
                    return NewCompare(common.LHSNW & common.RHSNW, lo, ro);
                }
            }
            return TransformToIndexedCompare(left, right, predicate);
        }

        private static LlvmAPInt ICmpRoundSDivDown(LlvmAPInt value, LlvmAPInt divisor)
        {
            var quotient = value.SDiv(divisor);
            return !value.SRem(divisor).IsZero && value.IsNegative != divisor.IsNegative
                ? quotient - new LlvmAPInt(value.BitWidth, 1) : quotient;
        }
    }
}
