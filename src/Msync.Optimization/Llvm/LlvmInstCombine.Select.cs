// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineSelect.cpp visitSelectInst.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitSelectInst(LlvmSelectInstruction select)
        {
            Count("select.visits");
            LlvmValue condition = select.Condition, whenTrue = select.TrueValue, whenFalse = select.FalseValue;
            LlvmType type = select.Type;
            LlvmFastMathFlags flags = type.IsFPOrFPVectorTy() ? select.FastMathFlags : LlvmFastMathFlags.None;
            if (LlvmInstructionSimplify.SimplifySelect(condition, whenTrue, whenFalse, flags, Query(select)) is { } simplified)
                return ReplaceInstUsesWith(select, simplified);
            // The represented CFG is scalar; source shuffle/vector-only and
            // atomic cmpxchg visitors require their actual instruction classes.
            if (whenFalse is LlvmCallInstruction count && count.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.CTPop &&
                ReferenceEquals(count.GetArgument(0), whenTrue) && condition is LlvmICmpInstruction { Predicate: LlvmCmpPredicate.ICmpUlt } popCompare &&
                ReferenceEquals(popCompare.Left, whenTrue) && popCompare.Right is LlvmConstantInt two && two.Value == new LlvmAPInt(two.Value.BitWidth, 2, implicitTrunc: true))
            { count.DropPoisonGeneratingAnnotations(); Worklist.Add(count); return ReplaceInstUsesWith(select, count); }
            if (condition is not LlvmConstant)
            {
                if (LlvmInstructionSimplify.SimplifyWithOpReplaced(whenTrue, condition, LlvmConstantInt.Get((LlvmIntegerType)condition.Type, 1), SQ, true) is { } trueSimplified)
                    return ReplaceOperand(select, 1, trueSimplified);
                if (LlvmInstructionSimplify.SimplifyWithOpReplaced(whenFalse, condition, LlvmConstantInt.Get((LlvmIntegerType)condition.Type, 0), SQ, true) is { } falseSimplified)
                    return ReplaceOperand(select, 2, falseSimplified);
                if (ReplaceInInstruction(whenTrue, condition, LlvmConstantInt.Get((LlvmIntegerType)condition.Type, 1)) ||
                    ReplaceInInstruction(whenFalse, condition, LlvmConstantInt.Get((LlvmIntegerType)condition.Type, 0))) return select;
            }
            if (FoldSelectOfBools(select) is { } boolean) return boolean;
            if (type.IsIntegerTy() && !type.IsIntegerTy(1))
            {
                if (SelectIsOne(whenTrue) && SelectIsZero(whenFalse)) return new LlvmCastInstruction(LlvmOpcode.ZExt, condition, type);
                if (SelectIsOnes(whenTrue) && SelectIsZero(whenFalse)) return new LlvmCastInstruction(LlvmOpcode.SExt, condition, type);
                if (SelectIsZero(whenTrue) && SelectIsOne(whenFalse)) return new LlvmCastInstruction(LlvmOpcode.ZExt, Builder.CreateNot(condition, "not." + condition.Name), type);
                if (SelectIsZero(whenTrue) && SelectIsOnes(whenFalse)) return new LlvmCastInstruction(LlvmOpcode.SExt, Builder.CreateNot(condition, "not." + condition.Name), type);
            }
            if (FoldSelectNegNot(select) is { } negNot) return negNot;
            if (FoldSelectAndOrPowerOfTwo(select) is { } power) return power;
            if (FoldSelectFloatingComparisons(select) is { } floatingCompare) return floatingCompare;
            if (FoldSelectFloatingMinMax(select) is { } floatingMinMax) return floatingMinMax;
            if (FoldSelectWithFCmpToFabs(select) is { } abs) return abs;
            if (FoldSelectOfOrderedFAbsCmpOfNaNScrubbedValue(select) is { } scrubbed) return scrubbed;
            if (condition is LlvmCmpInstruction compare && FoldSelectValueEquivalence(select, compare) is { } equivalent) return equivalent;
            if (condition is LlvmICmpInstruction integerCompare && FoldSelectInstWithICmp(select, integerCompare) is { } integer) return integer;
            if (FoldSelectICmpAndAnd(type, condition, whenTrue, whenFalse) is { } masked) return masked;
            if (FoldSelectBitTest(select, condition, whenTrue, whenFalse) is { } bitTest) return ReplaceInstUsesWith(select, bitTest);
            if (FoldAddSubSelect(select) is { } addSub) return addSub;
            if (FoldOverflowingAddSubSelect(select) is { } overflow) return overflow;
            if (FoldSetClearBits(select) is { } setClear) return setClear;
            if (FoldSelectZeroOrFixedOp(select) is { } fixedOp) return fixedOp;
            if (whenTrue is LlvmInstruction trueInstruction && whenFalse is LlvmInstruction falseInstruction && trueInstruction.Opcode == falseInstruction.Opcode &&
                FoldSelectOpOp(select, trueInstruction, falseInstruction) is { } operation) return operation;
            if (FoldSelectIntrinsic(select) is { } intrinsic) return intrinsic;
            if (FoldSelectExtConst(select) is { } extension) return extension;
            if (FoldSelectWithSRem(select) is { } remainder) return remainder;
            LlvmInstruction? SelectGepWithBase(LlvmGetElementPtrInstruction gep, LlvmValue basis, bool swap)
            {
                if (gep.OperandCount != 2 || !ReferenceEquals(gep.PointerOperand, basis) || !gep.HasOneUse) return null;
                LlvmValue newTrue = gep.GetOperand(1)!, newFalse = LlvmConstant.GetNullValue(gep.GetOperand(1)!.Type);
                if (swap) (newTrue, newFalse) = (newFalse, newTrue);
                LlvmValue index = Builder.CreateSelect(condition, newTrue, newFalse, select.Name + ".idx");
                return new LlvmGetElementPtrInstruction(gep.SourceElementType, gep.PointerOperand, [index], gep.NoWrapFlags);
            }
            if (whenTrue is LlvmGetElementPtrInstruction trueGep && SelectGepWithBase(trueGep, whenFalse, false) is { } selectedTrueGep) return selectedTrueGep;
            if (whenFalse is LlvmGetElementPtrInstruction falseGep && SelectGepWithBase(falseGep, whenTrue, true) is { } selectedFalseGep) return selectedFalseGep;
            if (type.IsIntegerTy() || type.IsFloatingPointTy())
            {
                if (FoldSelectIntoOp(select, whenTrue, whenFalse) is { } foldedOperation) return foldedOperation;
                LlvmSelectPatternResult pattern = LlvmSelectPattern.Match(select, Metrics);
                if (pattern.Flavor != LlvmSelectPatternFlavor.Unknown)
                {
                    LlvmSelectPatternResult inner = LlvmSelectPattern.Match(pattern.LHS!, Metrics);
                    if (inner.Flavor != LlvmSelectPatternFlavor.Unknown && FoldSPFofSPF((LlvmInstruction)pattern.LHS!, inner.Flavor,
                        inner.LHS!, inner.RHS!, select, pattern.Flavor, pattern.RHS!) is { } lhsPattern) return lhsPattern;
                    inner = LlvmSelectPattern.Match(pattern.RHS!, Metrics);
                    if (inner.Flavor != LlvmSelectPatternFlavor.Unknown && FoldSPFofSPF((LlvmInstruction)pattern.RHS!, inner.Flavor,
                        inner.LHS!, inner.RHS!, select, pattern.Flavor, pattern.LHS!) is { } rhsPattern) return rhsPattern;
                }
                if (pattern.IsMinOrMax)
                {
                    bool castNeeded = !ReferenceEquals(pattern.LHS!.Type, type);
                    LlvmCmpInstruction comparison = (LlvmCmpInstruction)condition;
                    LlvmValue a = pattern.LHS!, b = pattern.RHS!;
                    if (castNeeded || (a.Type.IsFloatingPointTy() && ((!ReferenceEquals(comparison.Left, a) && !ReferenceEquals(comparison.Left, b)) ||
                        (!ReferenceEquals(comparison.Right, a) && !ReferenceEquals(comparison.Right, b)))))
                    {
                        LlvmCmpPredicate predicate = LlvmSelectPattern.GetMinMaxPred(pattern.Flavor, pattern.Ordered);
                        LlvmValue newCompare;
                        if ((byte)predicate >= 32)
                        { DropPoisonGeneratingAnnotationsForSPFCanonicalization(condition, a, b); newCompare = Builder.CreateICmp(predicate, a, b); }
                        else newCompare = Builder.CreateFCmp(predicate, a, b, fastMathFlags: ((ILlvmFastMathInstruction)condition).FastMathFlags);
                        LlvmValue result = Builder.CreateSelect(newCompare, a, b, select.Name);
                        if (castNeeded) result = Builder.CreateCast(pattern.CastOpcode!.Value, result, type);
                        return ReplaceInstUsesWith(select, result);
                    }
                }
            }
            if (condition is LlvmPhiInstruction phi && FoldOpIntoPhi(select, phi) is { } phiFold) return phiFold;
            if (whenTrue is LlvmSelectInstruction trueSelect && ReferenceEquals(trueSelect.Condition.Type, condition.Type))
            {
                if (SimplifyNestedSelectsUsingImpliedCond(trueSelect, condition, true) is { } implied) return ReplaceOperand(select, 1, implied);
                if (trueSelect.HasOneUse)
                {
                    LlvmValue? and = null, other = null;
                    if (ReferenceEquals(trueSelect.FalseValue, whenFalse)) { and = Builder.CreateLogicalAnd(condition, trueSelect.Condition); other = trueSelect.TrueValue; }
                    else if (ReferenceEquals(trueSelect.TrueValue, whenFalse)) { and = Builder.CreateLogicalAnd(condition, Builder.CreateNot(trueSelect.Condition)); other = trueSelect.FalseValue; }
                    if (and is not null) { ReplaceOperand(select, 0, and); ReplaceOperand(select, 1, other!); return select; }
                }
            }
            if (whenFalse is LlvmSelectInstruction falseSelect && ReferenceEquals(falseSelect.Condition.Type, condition.Type))
            {
                if (SimplifyNestedSelectsUsingImpliedCond(falseSelect, condition, false) is { } implied) return ReplaceOperand(select, 2, implied);
                if (falseSelect.HasOneUse)
                {
                    LlvmValue? or = null, other = null;
                    if (ReferenceEquals(falseSelect.TrueValue, whenTrue)) { or = Builder.CreateLogicalOr(condition, falseSelect.Condition); other = falseSelect.FalseValue; }
                    else if (ReferenceEquals(falseSelect.FalseValue, whenTrue)) { or = Builder.CreateLogicalOr(condition, Builder.CreateNot(falseSelect.Condition)); other = falseSelect.TrueValue; }
                    if (or is not null) { ReplaceOperand(select, 0, or); ReplaceOperand(select, 2, other!); return select; }
                }
            }
            if (whenTrue is LlvmBinaryOperator trueBinary && trueBinary.HasOneUse && trueBinary.Opcode is not (LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem))
                for (int index = 0; index != 2; ++index)
                    if (trueBinary.GetOperand(index) is LlvmSelectInstruction inner && ReferenceEquals(inner.Condition, condition))
                    { ReplaceOperand(trueBinary, index, inner.TrueValue); Worklist.Push(trueBinary); return select; }
            if (whenFalse is LlvmBinaryOperator falseBinary && falseBinary.HasOneUse && falseBinary.Opcode is not (LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem))
                for (int index = 0; index != 2; ++index)
                    if (falseBinary.GetOperand(index) is LlvmSelectInstruction inner && ReferenceEquals(inner.Condition, condition))
                    { ReplaceOperand(falseBinary, index, inner.FalseValue); Worklist.Push(falseBinary); return select; }
            if (SelectMatchNot(condition, out LlvmValue notCondition) && !ShouldAvoidAbsorbingNotIntoSelect(select))
            {
                ReplaceOperand(select, 0, notCondition); ReplaceOperand(select, 1, whenFalse); ReplaceOperand(select, 2, whenTrue); return select;
            }
            if (AC.Assumptions().Count != 0)
            {
                LlvmKnownBits known = LlvmKnownBitsAnalysis.Compute(condition, context: select, tree: DT, metrics: Metrics, assumptions: AC, conditions: SQ.DC);
                if (known.One.IsOne) return ReplaceInstUsesWith(select, whenTrue);
                if (known.Zero.IsOne) return ReplaceInstUsesWith(select, whenFalse);
            }
            if (FoldSelectCmpBitcasts(select) is { } bitcasts) return bitcasts;
            if (FoldSelectBinOpIdentity(select) is { } identity) return identity;
            if (FoldSelectFunnelShift(select) is { } funnel) return funnel;
            if (FoldSelectToCopysign(select) is { } copySign) return copySign;
            if (FoldSelectToPhi(select) is { } selectedPhi) return ReplaceInstUsesWith(select, selectedPhi);
            if (FoldRoundUpIntegerWithPow2Alignment(select) is { } aligned) return ReplaceInstUsesWith(select, aligned);
            if (FoldSelectIntoAddConstant(select) is { } addConstant) return ReplaceInstUsesWith(select, addConstant);
            if (FoldSelectOfSymmetricSelect(select) is { } symmetric) return symmetric;
            if (FoldNestedSelects(select) is { } nested) return nested;
            if (SinkNotIntoOtherHandOfLogicalOp(select)) return select;
            if (FoldBitCeil(select) is { } bitCeil) return bitCeil;
            if (FoldSelectToCmp(select) is { } toCmp) return toCmp;
            if (FoldSelectEqualityTest(select) is { } equality) return equality;
            if (FoldSelectWithAndOrCondition(select) is { } andOr) return andOr;
            if (ReferenceEquals(condition.Type, type) && LlvmValueTracking.IsKnownInversion(whenFalse, whenTrue))
                return new LlvmBinaryOperator(LlvmOpcode.Xor, condition, whenFalse);
            if (FoldSelectConditionKnownBits(select) is { } knownArm) return knownArm;
            return FoldSelectTruncCondition(select);
        }
    }
}
