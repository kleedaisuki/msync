// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp foldSelectWithConstOpToBinOp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? FoldSelectWithConstOpToBinOp(LlvmICmpInstruction compare, LlvmValue whenTrue, LlvmValue whenFalse)
        {
            if (compare.Right is not LlvmConstant first || compare.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe) return null;
            LlvmValue x = compare.Left; LlvmCmpPredicate predicate = compare.Predicate;
            if (whenTrue is LlvmConstant) { (whenTrue, whenFalse) = (whenFalse, whenTrue); predicate = LlvmCmpInstruction.GetInversePredicate(predicate); }
            if (whenFalse is not LlvmConstant third || !whenTrue.HasOneUse) return null;
            LlvmConstant second; LlvmOpcode opcode = default; LlvmIntrinsicKind? intrinsic = null;
            if (whenTrue is LlvmBinaryOperator binary)
            {
                opcode = binary.Opcode;
                if (opcode is LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem ||
                    !ReferenceEquals(binary.Left, x) || binary.Right is not LlvmConstant operand) return null;
                second = operand;
            }
            else if (whenTrue is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor is { } descriptor &&
                LlvmIntrinsic.IsMinMax(descriptor.Kind) && ReferenceEquals(call.GetArgument(0), x) && call.GetArgument(1) is LlvmConstant operand)
            { intrinsic = descriptor.Kind; second = operand; }
            else return null;
            LlvmConstant? Fold(LlvmConstant left, LlvmConstant right) => intrinsic.HasValue ?
                LlvmConstantFolding.ConstantFoldIntrinsic(intrinsic.Value, [left, right], left.Type, Metrics) :
                LlvmConstantFolding.ConstantFoldBinaryOpOperands(opcode, left, right, DL, Metrics);
            var flipped = LlvmValueTracking.GetFlippedStrictnessPredicateAndConstant(predicate, first, compare.SameSign);
            LlvmValue rhs; LlvmSelectPatternFlavor flavor;
            if (ReferenceEquals(third, Fold(first, second))) { flavor = LlvmSelectPattern.GetSelectPattern(predicate); rhs = first; }
            else if (flipped.HasValue && ReferenceEquals(third, Fold(flipped.Value.Constant, second)))
            { flavor = LlvmSelectPattern.GetSelectPattern(flipped.Value.Predicate); rhs = flipped.Value.Constant; }
            else return null;
            LlvmValue minmax = Builder.CreateBinaryIntrinsic(SelectMinMaxIntrinsic(flavor), x, rhs);
            if (intrinsic.HasValue) return Builder.CreateBinaryIntrinsic(intrinsic.Value, minmax, second);
            LlvmValue result = Builder.CreateBinOp(opcode, minmax, second);
            if (result is LlvmBinaryOperator replacement && opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul)
            {
                LlvmBinaryOperator old = (LlvmBinaryOperator)whenTrue;
                if (old.HasNoSignedWrap && WillNotOverflow(opcode, rhs, second, replacement, true)) replacement.HasNoSignedWrap = true;
                if (old.HasNoUnsignedWrap && WillNotOverflow(opcode, rhs, second, replacement, false)) replacement.HasNoUnsignedWrap = true;
            }
            return result;
        }
    }
}
