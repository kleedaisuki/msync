// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// foldSelectCtlzToCttz/foldSelectCttzCtlz/foldSelectFunnelShift.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectCtlzToCttz(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            uint width = whenTrue.Type.GetScalarSizeInBits();
            if (compare.Predicate is not (LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe) || !SelectIsZero(compare.Right)) return null;
            if (compare.Predicate == LlvmCmpPredicate.ICmpNe) (whenTrue, whenFalse) = (whenFalse, whenTrue);
            LlvmValue count;
            if (whenFalse is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt xorMask } xor &&
                xorMask.Value == new LlvmAPInt(xorMask.Value.BitWidth, width - 1, implicitTrunc: true))
            {
                if (!System.Numerics.BitOperations.IsPow2(width)) return null;
                count = xor.Left;
            }
            else if (whenFalse is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt subConstant } sub &&
                subConstant.Value == new LlvmAPInt(subConstant.Value.BitWidth, width - 1, implicitTrunc: true)) count = sub.Right;
            else return null;
            if (count is not LlvmCallInstruction call || call.CalledFunction?.IntrinsicDescriptor?.Kind != LlvmIntrinsicKind.CTLZ ||
                whenTrue is not LlvmConstantInt trueConstant || trueConstant.Value != new LlvmAPInt(trueConstant.Value.BitWidth, width, implicitTrunc: true)) return null;
            LlvmValue value = compare.Left;
            if (call.GetArgument(0) is not LlvmBinaryOperator { Opcode: LlvmOpcode.And } and) return null;
            bool matched = ReferenceEquals(and.Left, value) && SelectMatchNeg(and.Right, out LlvmValue negative) && ReferenceEquals(negative, value);
            if (!matched) matched = ReferenceEquals(and.Right, value) && SelectMatchNeg(and.Left, out negative) && ReferenceEquals(negative, value);
            if (!matched) return null;
            return LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.CTTZ, [value, LlvmConstantInt.Get(LlvmType.GetInt1Ty(value.Context), 0)], F.Parent!, Metrics);
        }
        private LlvmValue? FoldSelectCttzCtlz(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            if (compare.Predicate is not (LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe)) return null;
            LlvmValue selected = whenFalse, onZero = whenTrue;
            if (compare.Predicate == LlvmCmpPredicate.ICmpNe) (selected, onZero) = (onZero, selected);
            LlvmValue count = selected is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.Trunc } cast ? cast.Operand : selected;
            if (count is not LlvmCallInstruction call || call.CalledFunction?.IntrinsicDescriptor is not { } descriptor ||
                (descriptor.Kind != LlvmIntrinsicKind.CTTZ && descriptor.Kind != LlvmIntrinsicKind.CTLZ)) return null;
            LlvmValue input = call.GetArgument(0);
            bool matches = ReferenceEquals(input, compare.Left) && SelectIsZero(compare.Right);
            matches |= SelectMatchNot(input, out LlvmValue not) && ReferenceEquals(not, compare.Left) && SelectIsOnes(compare.Right);
            matches |= input is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor &&
                ((ReferenceEquals(xor.Left, compare.Left) && ReferenceEquals(xor.Right, compare.Right)) ||
                (ReferenceEquals(xor.Right, compare.Left) && ReferenceEquals(xor.Left, compare.Right)));
            if (!matches) return null;
            if (onZero is LlvmConstantInt constant && constant.Value == new LlvmAPInt(constant.Value.BitWidth, count.Type.GetScalarSizeInBits(), implicitTrunc: true))
            {
                call.DropPoisonGeneratingAnnotations(); Worklist.Add(call);
                Count("select.count_zero_guard.rewrites");
                return selected;
            }
            if (call.HasOneUse && selected.HasOneUse && !SelectIsOne(call.GetArgument(1)))
            {
                call.SetArgument(1, LlvmConstantInt.Get(LlvmType.GetInt1Ty(call.Context), 1));
                call.DropUBImplyingAttrsAndMetadata(); Worklist.Add(call);
                Count("select.count_zero_guard.poison_relaxed");
            }
            return null;
        }
        private LlvmInstruction? FoldSelectFunnelShift(LlvmSelectInstruction select)
        {
            uint width = select.Type.GetScalarSizeInBits();
            if (!System.Numerics.BitOperations.IsPow2(width) || select.FalseValue is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or } or ||
                !or.HasOneUse || or.Left is not LlvmBinaryOperator first || or.Right is not LlvmBinaryOperator second ||
                !first.HasOneUse || !second.HasOneUse || first.Opcode is not (LlvmOpcode.Shl or LlvmOpcode.LShr) ||
                second.Opcode is not (LlvmOpcode.Shl or LlvmOpcode.LShr) || first.Opcode == second.Opcode) return null;
            LlvmValue source0 = first.Left, source1 = second.Left;
            LlvmValue amount0 = first.Right is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } firstCast ? firstCast.Operand : first.Right;
            LlvmValue amount1 = second.Right is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } secondCast ? secondCast.Operand : second.Right;
            if (first.Opcode == LlvmOpcode.LShr)
            { (first, second) = (second, first); (source0, source1) = (source1, source0); (amount0, amount1) = (amount1, amount0); }
            bool MatchOpposite(LlvmValue opposite, LlvmValue amount) => opposite is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } sub && sub.HasOneUse &&
                ReferenceEquals(sub.Right, amount) && sub.Left is LlvmConstantInt constant && constant.Value == new LlvmAPInt(constant.Value.BitWidth, width, implicitTrunc: true);
            LlvmValue shift;
            if (MatchOpposite(amount1, amount0)) shift = amount0;
            else if (MatchOpposite(amount0, amount1)) shift = amount1;
            else return null;
            bool isLeft = ReferenceEquals(shift, amount0);
            if ((isLeft && !ReferenceEquals(select.TrueValue, source0)) || (!isLeft && !ReferenceEquals(select.TrueValue, source1)) ||
                select.Condition is not LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpEq } compare || !compare.HasOneUse ||
                !ReferenceEquals(compare.Left, shift) || !SelectIsZero(compare.Right)) return null;
            if (!ReferenceEquals(source0, source1))
            {
                if (isLeft && !LlvmValueTracking.IsGuaranteedNotToBePoison(source1)) source1 = Builder.CreateFreeze(source1);
                else if (!isLeft && !LlvmValueTracking.IsGuaranteedNotToBePoison(source0)) source0 = Builder.CreateFreeze(source0);
            }
            shift = Builder.CreateZExt(shift, select.Type);
            return LlvmIntrinsic.CreateIntrinsic(isLeft ? LlvmIntrinsicKind.FShl : LlvmIntrinsicKind.FShr, [source0, source1, shift], F.Parent!, Metrics);
        }
    }
}
