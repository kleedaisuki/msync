// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp
// foldSelectInstWithICmpConst/tryToReuseConstantFromSelectInComparison.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? FoldSelectInstWithICmpConst(LlvmSelectInstruction select, LlvmICmpInstruction compare)
        {
            if (compare.Right is not LlvmConstantInt constant) return null;
            LlvmValue value = compare.Left;
            LlvmAPInt c = constant.Value, one = new(c.BitWidth, 1, implicitTrunc: true);
            if (compare.Predicate == LlvmCmpPredicate.ICmpEq && ReferenceEquals(value, select.FalseValue) && select.TrueValue is LlvmConstantInt trueConstant)
            {
                if (c.IsMinValue && trueConstant.Value == c + one) return Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.UMax, value, trueConstant);
                if (c.IsMaxValue && trueConstant.Value == c - one) return Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.UMin, value, trueConstant);
                if (c.IsMinSignedValue && trueConstant.Value == c + one) return Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.SMax, value, trueConstant);
                if (c.IsMaxSignedValue && trueConstant.Value == c - one) return Builder.CreateBinaryIntrinsic(LlvmIntrinsicKind.SMin, value, trueConstant);
            }
            LlvmInstruction operation; LlvmAPInt target; LlvmCmpPredicate predicate;
            if (select.TrueValue is LlvmConstantInt trueC && select.FalseValue is LlvmInstruction falseInstruction)
            { operation = falseInstruction; target = trueC.Value; predicate = compare.Predicate; }
            else if (select.FalseValue is LlvmConstantInt falseC && select.TrueValue is LlvmInstruction trueInstruction)
            { operation = trueInstruction; target = falseC.Value; predicate = LlvmCmpInstruction.GetInversePredicate(compare.Predicate); }
            else return null;
            LlvmConstantRange domain = LlvmConstantRange.MakeExactICmpRegion(predicate, c);
            if (operation is LlvmBinaryOperator binary && ReferenceEquals(binary.Left, value) && binary.Right is LlvmConstantInt operand)
            {
                LlvmConstantRange range = domain.BinaryOp(binary.Opcode, new(operand.Value));
                if (range.GetSingleElement() == target) { binary.DropPoisonGeneratingFlags(); return binary; }
            }
            if (operation is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor is { } descriptor &&
                LlvmIntrinsic.IsMinMax(descriptor.Kind) && ReferenceEquals(call.GetArgument(0), value) && call.GetArgument(1) is LlvmConstantInt intrinsicConstant)
            {
                LlvmConstantRange range = LlvmConstantRange.Intrinsic(descriptor.Kind, [domain, new(intrinsicConstant.Value)]);
                if (range.GetSingleElement() == target) { call.DropPoisonGeneratingAnnotations(); return call; }
            }
            return null;
        }
        private LlvmInstruction? TryToReuseConstantFromSelectInComparison(LlvmSelectInstruction select, LlvmICmpInstruction compare)
        {
            if (!compare.HasOneUse || compare.Right is not LlvmConstantInt constant ||
                compare.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe || !IsCanonicalPredicate(compare.Predicate) ||
                !ReferenceEquals(constant.Type, select.Type)) return null;
            if (compare.Predicate == LlvmCmpPredicate.ICmpUlt && compare.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstant }) return null;
            if (select.TrueValue is not LlvmConstantInt && select.FalseValue is not LlvmConstantInt) return null;
            if (ReferenceEquals(constant, select.TrueValue) || ReferenceEquals(constant, select.FalseValue)) return null;
            var flipped = LlvmValueTracking.GetFlippedStrictnessPredicateAndConstant(compare.Predicate, constant, compare.SameSign);
            if (!flipped.HasValue || (!ReferenceEquals(flipped.Value.Constant, select.TrueValue) && !ReferenceEquals(flipped.Value.Constant, select.FalseValue))) return null;
            LlvmTransformBuilder.InsertionPoint saved = Builder.SaveInsertionPoint();
            try
            {
                Builder.SetInsertPoint(select);
                LlvmCmpPredicate swapped = LlvmCmpInstruction.GetSwappedPredicate(compare.Predicate);
                LlvmValue replacement = Builder.CreateICmp(swapped, compare.Left, flipped.Value.Constant, compare.Name + ".inv", compare.SameSign);
                ReplaceOperand(select, 0, replacement);
                LlvmValue trueValue = select.TrueValue;
                ReplaceOperand(select, 1, select.FalseValue); ReplaceOperand(select, 2, trueValue);
                return select;
            }
            finally { Builder.RestoreInsertionPoint(saved); }
        }
    }
}
