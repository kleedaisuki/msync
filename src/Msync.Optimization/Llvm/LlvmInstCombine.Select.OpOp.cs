// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp foldSelectOpOp.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldSelectOpOp(LlvmSelectInstruction select, LlvmInstruction whenTrue, LlvmInstruction whenFalse)
        {
            LlvmValue condition = select.Condition;
            if (whenTrue is LlvmCastInstruction trueCast && whenTrue.OperandCount == 1)
            {
                if (!ReferenceEquals(trueCast.Operand.Type, whenFalse.GetOperand(0)!.Type) || !whenTrue.HasOneUse || !whenFalse.HasOneUse) return null;
                LlvmValue newSelect = Builder.CreateSelect(condition, trueCast.Operand, whenFalse.GetOperand(0)!, select.Name + ".v");
                return new LlvmCastInstruction(whenTrue.Opcode, newSelect, whenTrue.Type);
            }
            LlvmValue otherTrue = null!, otherFalse = null!;
            bool commonIsZero = false;
            LlvmValue? GetCommonOp(bool commute, bool swapped = false)
            {
                if (!swapped)
                {
                    if (ReferenceEquals(whenTrue.GetOperand(0), whenFalse.GetOperand(0)))
                    { otherTrue = whenTrue.GetOperand(1)!; otherFalse = whenFalse.GetOperand(1)!; commonIsZero = true; return whenTrue.GetOperand(0); }
                    if (ReferenceEquals(whenTrue.GetOperand(1), whenFalse.GetOperand(1)))
                    { otherTrue = whenTrue.GetOperand(0)!; otherFalse = whenFalse.GetOperand(0)!; commonIsZero = false; return whenTrue.GetOperand(1); }
                }
                if (!commute && !swapped) return null;
                if (ReferenceEquals(whenTrue.GetOperand(0), whenFalse.GetOperand(1)))
                { otherTrue = whenTrue.GetOperand(1)!; otherFalse = whenFalse.GetOperand(0)!; commonIsZero = true; return whenTrue.GetOperand(0); }
                if (ReferenceEquals(whenTrue.GetOperand(1), whenFalse.GetOperand(0)))
                { otherTrue = whenTrue.GetOperand(0)!; otherFalse = whenFalse.GetOperand(1)!; commonIsZero = false; return whenTrue.GetOperand(1); }
                return null;
            }
            if (whenTrue.HasOneUse || whenFalse.HasOneUse)
            {
                if (SelectMatchFNeg(whenTrue, out LlvmValue x) && SelectMatchFNeg(whenFalse, out LlvmValue y))
                {
                    LlvmFastMathFlags flags = ((ILlvmFastMathInstruction)whenTrue).FastMathFlags & ((ILlvmFastMathInstruction)whenFalse).FastMathFlags;
                    flags |= select.FastMathFlags;
                    LlvmValue newSelect = Builder.CreateSelect(condition, x, y, select.Name + ".v");
                    if (newSelect is ILlvmFastMathInstruction floating) floating.FastMathFlags = flags;
                    return new LlvmUnaryOperator(newSelect) { FastMathFlags = flags };
                }
                if (whenTrue is LlvmCallInstruction trueCall && whenFalse is LlvmCallInstruction falseCall &&
                    trueCall.CalledFunction?.IntrinsicDescriptor is { } descriptor && falseCall.CalledFunction?.IntrinsicDescriptor?.Kind == descriptor.Kind)
                {
                    if (LlvmIntrinsic.IsMinMax(descriptor.Kind) && GetCommonOp(true) is { } common)
                    {
                        LlvmValue newSelect = Builder.CreateSelect(condition, otherTrue, otherFalse, "minmaxop");
                        return new LlvmCallInstruction(trueCall.FunctionType, trueCall.CalledFunction!, [newSelect, common]);
                    }
                    if (descriptor.Kind == LlvmIntrinsicKind.Ldexp && ReferenceEquals(trueCall.GetArgument(1).Type, falseCall.GetArgument(1).Type))
                    {
                        LlvmFastMathFlags flags = (trueCall.FastMathFlags & falseCall.FastMathFlags) | select.FastMathFlags;
                        LlvmValue selectedValue = Builder.CreateSelect(condition, trueCall.GetArgument(0), falseCall.GetArgument(0));
                        LlvmValue selectedExponent = Builder.CreateSelect(condition, trueCall.GetArgument(1), falseCall.GetArgument(1));
                        LlvmCallInstruction replacement = new(trueCall.FunctionType, trueCall.CalledFunction!, [selectedValue, selectedExponent]) { FastMathFlags = flags };
                        return ReplaceInstUsesWith(select, Builder.Insert(replacement));
                    }
                }
                if (whenTrue is LlvmICmpInstruction trueCmp && whenFalse is LlvmICmpInstruction falseCmp)
                {
                    LlvmInstruction? CreateCmpSel((LlvmCmpPredicate Predicate, bool SameSign)? predicate, bool swapped)
                    {
                        if (!predicate.HasValue) return null;
                        bool equality = predicate.Value.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe;
                        LlvmValue? common = GetCommonOp(equality, !equality && swapped);
                        if (common is null) return null;
                        LlvmValue selected = Builder.CreateSelect(condition, otherTrue, otherFalse, select.Name + ".v");
                        LlvmCmpPredicate final = commonIsZero ? predicate.Value.Predicate : LlvmCmpInstruction.GetSwappedPredicate(predicate.Value.Predicate);
                        return new LlvmICmpInstruction(final, common, selected) { SameSign = predicate.Value.SameSign };
                    }
                    if (CreateCmpSel(GetMatchingCmpPredicate(trueCmp.Predicate, trueCmp.SameSign, falseCmp.Predicate, falseCmp.SameSign), false) is { } compare) return compare;
                    if (CreateCmpSel(GetMatchingCmpPredicate(trueCmp.Predicate, trueCmp.SameSign,
                        LlvmCmpInstruction.GetSwappedPredicate(falseCmp.Predicate), falseCmp.SameSign), true) is { } swappedCompare) return swappedCompare;
                }
            }
            if (whenTrue.OperandCount != 2 || whenFalse.OperandCount != 2 || !whenTrue.IsSameOperationAs(whenFalse) ||
                whenTrue is not (LlvmBinaryOperator or LlvmGetElementPtrInstruction) || !whenTrue.HasOneUse || !whenFalse.HasOneUse) return null;
            LlvmValue? matched = GetCommonOp(whenTrue is LlvmBinaryOperator binary && binary.IsCommutative);
            if (matched is null) return null;
            if (whenTrue is LlvmBinaryOperator div && div.Opcode is LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem &&
                !LlvmValueTracking.IsGuaranteedNotToBePoison(condition))
                if (div.Opcode is LlvmOpcode.SDiv or LlvmOpcode.SRem || commonIsZero) condition = Builder.CreateFreeze(condition);
            LlvmValue selectedOther = Builder.CreateSelect(condition, otherTrue, otherFalse, select.Name + ".v");
            LlvmValue left = commonIsZero ? matched : selectedOther, right = commonIsZero ? selectedOther : matched;
            if (whenTrue is LlvmBinaryOperator source)
            {
                LlvmBinaryOperator result = new(source.Opcode, left, right);
                result.CopyIRFlags(whenTrue); result.AndIRFlags(whenFalse);
                return result;
            }
            LlvmGetElementPtrInstruction trueGep = (LlvmGetElementPtrInstruction)whenTrue, falseGep = (LlvmGetElementPtrInstruction)whenFalse;
            return new LlvmGetElementPtrInstruction(trueGep.SourceElementType, left, [right], trueGep.NoWrapFlags & falseGep.NoWrapFlags);
        }
        private static bool SelectMatchFNeg(LlvmValue value, out LlvmValue operand)
        {
            operand = value;
            if (value is LlvmUnaryOperator unary) { operand = unary.Operand; return true; }
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, Left: LlvmConstantFP zero } sub && zero.Value.IsZero && zero.Value.IsNegative)
            { operand = sub.Right; return true; }
            return false;
        }
        private static (LlvmCmpPredicate Predicate, bool SameSign)? GetMatchingCmpPredicate(
            LlvmCmpPredicate left, bool leftSameSign, LlvmCmpPredicate right, bool rightSameSign)
        {
            LlvmCmpPredicateInfo? matching = LlvmCmpPredicateInfo.GetMatching(new(left, leftSameSign), new(right, rightSameSign));
            return matching.HasValue ? (matching.Value.Predicate, matching.Value.HasSameSign) : null;
        }
    }
}
