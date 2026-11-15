// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp 2684–2797
// foldOverflowingAddSubSelect.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldOverflowingAddSubSelect(LlvmSelectInstruction select)
        {
            Count("select.overflow_saturate.candidates");
            if (select.Condition is not LlvmExtractValueInstruction { IndexCount: 1 } flag || flag.Indices[0] != 1 ||
                flag.AggregateOperand is not LlvmCallInstruction operation || operation.CalledFunction?.IntrinsicDescriptor is not { } descriptor ||
                !LlvmIntrinsic.IsOverflowIntrinsic(descriptor.Kind) ||
                select.FalseValue is not LlvmExtractValueInstruction { IndexCount: 1 } result || result.Indices[0] != 0 || result.AggregateOperand != operation) return null;
            var x = operation.GetArgument(0); var y = operation.GetArgument(1);
            bool IsSignedSaturateLimit(LlvmValue limit, bool isAdd)
            {
                if (limit is not LlvmSelectInstruction { Condition: LlvmICmpInstruction { Right: LlvmConstantInt constant } compare } choice) return false;
                var operand = compare.Left; var c = constant.Value;
                if (operand != x && operand != y) return false;
                uint width = limit.Type.GetScalarSizeInBits();
                var min = LlvmAPInt.GetSignedMinValue(width); var max = LlvmAPInt.GetSignedMaxValue(width);
                bool MinMax(LlvmValue minimum, LlvmValue maximum) => SaturationSpecific(minimum, min) && SaturationSpecific(maximum, max);
                static bool ZeroOrOne(LlvmAPInt value) => value.IsZero || value.IsOne;
                var one = new LlvmAPInt(c.BitWidth, 1); var two = new LlvmAPInt(c.BitWidth, 2, implicitTrunc: true);
                if (isAdd)
                {
                    if (compare.Predicate == LlvmCmpPredicate.ICmpSlt && ZeroOrOne(c) && MinMax(choice.TrueValue, choice.FalseValue)) return true;
                    if (compare.Predicate == LlvmCmpPredicate.ICmpSgt && ZeroOrOne(c + one) && MinMax(choice.FalseValue, choice.TrueValue)) return true;
                }
                else
                {
                    if (operand == x && compare.Predicate == LlvmCmpPredicate.ICmpSlt && ZeroOrOne(c + one) && MinMax(choice.TrueValue, choice.FalseValue)) return true;
                    if (operand == x && compare.Predicate == LlvmCmpPredicate.ICmpSgt && ZeroOrOne(c + two) && MinMax(choice.FalseValue, choice.TrueValue)) return true;
                    if (operand == y && compare.Predicate == LlvmCmpPredicate.ICmpSlt && ZeroOrOne(c) && MinMax(choice.FalseValue, choice.TrueValue)) return true;
                    if (operand == y && compare.Predicate == LlvmCmpPredicate.ICmpSgt && ZeroOrOne(c + one) && MinMax(choice.TrueValue, choice.FalseValue)) return true;
                }
                return false;
            }
            LlvmIntrinsicKind kind;
            if (descriptor.Kind == LlvmIntrinsicKind.UAddWithOverflow && SelectIsOnes(select.TrueValue)) kind = LlvmIntrinsicKind.UAddSat;
            else if (descriptor.Kind == LlvmIntrinsicKind.USubWithOverflow && SelectIsZero(select.TrueValue)) kind = LlvmIntrinsicKind.USubSat;
            else if (descriptor.Kind == LlvmIntrinsicKind.SAddWithOverflow && IsSignedSaturateLimit(select.TrueValue, true)) kind = LlvmIntrinsicKind.SAddSat;
            else if (descriptor.Kind == LlvmIntrinsicKind.SSubWithOverflow && IsSignedSaturateLimit(select.TrueValue, false)) kind = LlvmIntrinsicKind.SSubSat;
            else return null;
            Count("select.overflow_saturate.rewritten");
            return LlvmIntrinsic.CreateIntrinsic(kind, [x,y], F.Parent!, Metrics);
        }
    }
}
