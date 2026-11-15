// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp,
// cmpExcludesZero, isKnownNonNullFromDominatingCondition, isGEPKnownNonNull,
// stripNullTest. Source default: dom-conditions-max-uses=20.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static bool ConditionExcludesZero(LlvmValue condition, LlvmValue value, bool isTrue)
    {
        if (condition is not LlvmICmpInstruction comparison) return false;
        var predicate = comparison.Predicate;
        LlvmValue other;
        if (ReferenceEquals(comparison.Left, value)) other = comparison.Right;
        else if (ReferenceEquals(comparison.Right, value))
        { other = comparison.Left; predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
        else return false;
        if (!isTrue) predicate = LlvmCmpInstruction.GetInversePredicate(predicate);
        return ComparisonExcludesZero(predicate, other);
    }
    private static bool ComparisonExcludesZero(LlvmCmpPredicate predicate, LlvmValue other)
    {
        if (predicate == LlvmCmpPredicate.ICmpUgt) return true;
        if (predicate == LlvmCmpPredicate.ICmpNe) return other is LlvmConstant { IsNullValue: true };
        return other is LlvmConstantInt integer && !LlvmConstantRange.MakeExactICmpRegion(predicate, integer.Value).Contains(LlvmAPInt.GetZero(integer.Value.BitWidth));
    }

    private static bool IsNonZeroFromDominatingCondition(LlvmValue value, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (context?.Parent is null || tree is null) return false;
        int uses = 0;
        foreach (var use in value.Uses)
        {
            if (uses++ >= 20) { Count(metrics, "value_facts.nonzero.dom_use_limit"); break; }
            Count(metrics, "value_facts.nonzero.dom_uses");
            if (use.User is not LlvmInstruction instruction) continue;
            if (value.Type is LlvmPointerType { AddressSpace: 0 } &&
                ((instruction is LlvmLoadInstruction load && ReferenceEquals(load.PointerOperand, value)) ||
                 (instruction is LlvmStoreInstruction store && ReferenceEquals(store.PointerOperand, value))) && tree.Dominates(instruction, context)) return true;
            if (instruction is LlvmBinaryOperator { Opcode: LlvmOpcode.SDiv or LlvmOpcode.UDiv or LlvmOpcode.SRem or LlvmOpcode.URem } div &&
                ReferenceEquals(div.Right, value) && LlvmValueTracking.IsValidAssumeForContext(instruction, context, tree, allowEphemerals, metrics)) return true;
            bool nonZeroIfTrue;
            if (ConditionExcludesZero(instruction, value, true)) nonZeroIfTrue = true;
            else if (ConditionExcludesZero(instruction, value, false)) nonZeroIfTrue = false;
            else if (instruction is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc && trunc.Type.IsIntegerTy(1) && ReferenceEquals(trunc.Operand, value)) nonZeroIfTrue = true;
            else continue;
            var work = new Stack<LlvmUser>();
            var visited = new HashSet<LlvmUser>(ReferenceEqualityComparer.Instance);
            foreach (var user in instruction.Users) if (visited.Add(user)) work.Push(user);
            while (work.TryPop(out var user))
            {
                if (nonZeroIfTrue && IsLogicalAnd(user))
                {
                    foreach (var next in user.Users) if (visited.Add(next)) work.Push(next);
                    continue;
                }
                if (user is LlvmCondBrInstruction branch && tree.DominatesEdge(branch.Parent!, branch.GetSuccessor(nonZeroIfTrue ? 0 : 1), context.Parent))
                { Count(metrics, "value_facts.nonzero.dom_fact"); return true; }
            }
        }
        return false;
    }

    private static bool IsLogicalAnd(LlvmUser user) =>
        user is LlvmBinaryOperator { Opcode: LlvmOpcode.And } binary && binary.Type.IsIntegerTy(1) ||
        user is LlvmSelectInstruction { FalseValue: LlvmConstantInt { IsZero: true } } select && select.Type.IsIntegerTy(1);

    private static bool IsGepKnownNonNull(LlvmGetElementPtrInstruction gep, uint depth, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        if (!gep.NoWrapFlags.HasNoUnsignedWrap && !(gep.NoWrapFlags.IsInBounds && gep.Type is LlvmPointerType { AddressSpace: 0 })) return false;
        if (IsKnownNonZero(gep.PointerOperand, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        var layout = gep.Parent!.Module!.DataLayout;
        var element = gep.SourceElementType;
        for (int n = 0; n < gep.IndexCount; n++)
        {
            var index = gep.GetIndex(n);
            if (n > 0 && element is LlvmStructType structure)
            {
                uint member = (uint)((LlvmConstantInt)index).Value.GetLimitedValue();
                if (layout.GetStructLayout(structure).GetElementOffset(member).IsNonZero) return true;
                element = structure.GetElementType((int)member);
                continue;
            }
            if (n > 0) element = LlvmGetElementPtrInstruction.GetTypeAtIndex(element, index)!;
            if (layout.GetTypeAllocSize(element).IsZero) continue;
            if (index is LlvmConstantInt constant) { if (!constant.IsZero) return true; continue; }
            if (depth++ >= MaxDepth) continue;
            if (IsKnownNonZero(index, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext)) return true;
        }
        return false;
    }

    private static LlvmValue? StripNullTest(LlvmValue value)
    {
        if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Add or LlvmOpcode.Or } binary)
        {
            var stripped = MatchNullShift(binary.Left, binary.Right) ?? MatchNullShift(binary.Right, binary.Left);
            if (stripped is not null) return stripped;
        }
        if (value is LlvmSelectInstruction select)
        {
            if (IsNegationOf(select.TrueValue, select.FalseValue)) return select.FalseValue;
            if (IsNegationOf(select.FalseValue, select.TrueValue)) return select.TrueValue;
        }
        return null;
    }

    private static LlvmValue? MatchNullShift(LlvmValue left, LlvmValue right)
    {
        if (left is not LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt shift } shifted ||
            right is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt, Operand: LlvmICmpInstruction
                { Predicate: LlvmCmpPredicate.ICmpNe, Right: LlvmConstant { IsNullValue: true }, Left: LlvmBinaryOperator
                    { Opcode: LlvmOpcode.And, Right: LlvmConstantInt mask } masked } }) return null;
        var bits = mask.Value;
        bool lowMask = (bits & (bits + new LlvmAPInt(bits.BitWidth, 1))).IsZero && !bits.IsZero;
        return ReferenceEquals(shifted.Left, masked.Left) && lowMask && bits.PopCount() == shift.Value.GetLimitedValue() ? shifted.Left : null;
    }
}
