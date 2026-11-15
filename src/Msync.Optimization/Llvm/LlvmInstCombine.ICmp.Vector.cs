// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// foldVectorCmp (7782–7851) and foldReductionIdiom (7941–7986).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldVectorCmp(LlvmCmpInstruction compare)
        {
            var left = compare.Left; var right = compare.Right;
            LlvmInstruction CreateReverse(LlvmValue x, LlvmValue y)
            {
                var value = compare is LlvmICmpInstruction
                    ? Builder.CreateICmp(compare.Predicate, x, y, compare.Name)
                    : Builder.CreateFCmp(compare.Predicate, x, y, compare.Name);
                if (value is LlvmInstruction instruction) instruction.CopyIRFlags(compare);
                Count("compare.vector.reverse");
                return LlvmIntrinsic.CreateVectorIntrinsic(LlvmIntrinsicKind.VectorReverse, [value], value.Type,
                    compare.Parent!.Module!, Metrics);
            }
            if (CallsIntrinsic(left, LlvmIntrinsicKind.VectorReverse, out var reverseLeft))
            {
                if (CallsIntrinsic(right, LlvmIntrinsicKind.VectorReverse, out var reverseRight) && (left.HasOneUse || right.HasOneUse))
                    return CreateReverse(reverseLeft.GetArgument(0), reverseRight.GetArgument(0));
                if (left.HasOneUse && LlvmValueTracking.IsSplatValue(right, metrics: Metrics))
                    return CreateReverse(reverseLeft.GetArgument(0), right);
            }
            else if (LlvmValueTracking.IsSplatValue(left, metrics: Metrics) && right.HasOneUse &&
                CallsIntrinsic(right, LlvmIntrinsicKind.VectorReverse, out var reverseRight))
                return CreateReverse(left, reverseRight.GetArgument(0));
            if (left is not LlvmShuffleVectorInstruction { Right: LlvmUndefValue } lhs) return null;
            var mask = lhs.ShuffleMask;
            LlvmValue CreateCompare(LlvmValue x, LlvmValue y) => compare is LlvmICmpInstruction
                ? Builder.CreateICmp(compare.Predicate, x, y) : Builder.CreateFCmp(compare.Predicate, x, y);
            if (right is LlvmShuffleVectorInstruction { Right: LlvmUndefValue } rhs &&
                lhs.Left.Type == rhs.Left.Type && mask.SequenceEqual(rhs.ShuffleMask) && (left.HasOneUse || right.HasOneUse))
            {
                Count("compare.vector.shuffle_pair");
                return new LlvmShuffleVectorInstruction(CreateCompare(lhs.Left, rhs.Left), mask);
            }
            if (!left.HasOneUse || right is not LlvmConstant constant || constant.GetSplatValue(true) is not { } scalar) return null;
            int index = -1;
            foreach (int lane in mask)
            {
                if (lane < 0) continue;
                if (index >= 0 && lane != index) return null;
                index = lane;
            }
            if (index < 0) return null;
            var splat = LlvmConstantVector.GetSplat(((LlvmVectorType)lhs.Left.Type).ElementCount, scalar);
            Count("compare.vector.shuffle_splat");
            return new LlvmShuffleVectorInstruction(CreateCompare(lhs.Left, splat), Enumerable.Repeat(index, mask.Count).ToArray());
        }

        private LlvmInstruction? FoldReductionIdiom(LlvmICmpInstruction compare)
        {
            if (compare.Type.IsVectorTy() || !ICmpIsEquality(compare.Predicate) ||
                compare.Right is not LlvmConstantInt { IsZero: true } ||
                compare.Left is not LlvmCastInstruction { Opcode: LlvmOpcode.BitCast, HasOneUse: true,
                    Operand: LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpNe, HasOneUse: true } inner } ||
                inner.Left.Type is not LlvmFixedVectorType { ElementType: LlvmIntegerType element } vector) return null;
            uint width = vector.NumElements * element.BitWidth;
            if (!DL.IsLegalInteger(width)) return null;
            var type = LlvmIntegerType.Get(compare.Context, width);
            var left = Builder.CreateBitCast(inner.Left, type, inner.Left.Name + ".scalar");
            var right = Builder.CreateBitCast(inner.Right, type, inner.Right.Name + ".scalar");
            Count("icmp.vector.reduction_idiom");
            var result = new LlvmICmpInstruction(compare.Predicate, left, right);
            result.SetName(compare.Name);
            return result;
        }
    }
}
