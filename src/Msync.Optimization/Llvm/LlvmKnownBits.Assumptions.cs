// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// computeKnownBitsFromContext (AssumptionCache expression/operand-bundle cases).
using System.Numerics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    public static LlvmKnownBits ComputeFromContext(LlvmValue value, LlvmKnownBits known, uint depth = 0,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
    {
        if (condContext is not null && condContext.AffectedValues.Contains(value))
        {
            known = known.UnionWith(FromCondition(value, condContext.Cond, condContext.Invert,
                depth, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout));
            Count(metrics, "known_bits.injected_condition.applied");
        }
        if (context is null) return known;
        if (conditions is not null && tree is not null)
        {
            foreach (var branch in conditions.ConditionsFor(value))
            {
                Count(metrics, "known_bits.dom_conditions.visited");
                if (tree.DominatesEdge(branch.Parent!, branch.GetSuccessor(0), context.Parent!))
                {
                    known = known.UnionWith(FromCondition(value, branch.Condition, false, depth, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout));
                    Count(metrics, "known_bits.dom_conditions.true_edge");
                }
                if (tree.DominatesEdge(branch.Parent!, branch.GetSuccessor(1), context.Parent!))
                {
                    known = known.UnionWith(FromCondition(value, branch.Condition, true, depth, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout));
                    Count(metrics, "known_bits.dom_conditions.false_edge");
                }
            }
            if (known.HasConflict) { known = new(known.BitWidth); Count(metrics, "known_bits.dom_conditions.conflict_reset"); }
        }
        if (assumptions is null) return known;
        foreach (var entry in assumptions.AssumptionsFor(value))
        {
            if (entry.Assume.Value is not LlvmCallInstruction assumption) continue;
            Count(metrics, "known_bits.assume.entries");
            if (entry.Index != LlvmAssumptionCache.ExprResultIdx)
            {
                var bundle = assumption.GetOperandBundleAt((int)entry.Index);
                if (bundle.Tag == "align")
                {
                    var alignment = LlvmAssumeBundleQueries.GetAssumeAlignInfo(bundle);
                    if (ReferenceEquals(alignment.Pointer, value) && alignment.Alignment is { } align && alignment.Offset is { } offset &&
                        BitOperations.IsPow2(align) && IsValid(assumption))
                    {
                        known = new(known.Zero | new LlvmAPInt(known.BitWidth, (align - 1) & ~offset, implicitTrunc: true),
                            known.One | new LlvmAPInt(known.BitWidth, (align - 1) & offset, implicitTrunc: true));
                        Count(metrics, "known_bits.assume.align");
                    }
                }
                else if (bundle.Tag == "range")
                {
                    var range = LlvmAssumeBundleQueries.GetAssumeRangeInfo(bundle);
                    if (ReferenceEquals(range.Value, value) && IsValid(assumption))
                    { known = known.UnionWith(range.Range.ToKnownBits()); Count(metrics, "known_bits.assume.range"); }
                }
                continue;
            }
            var condition = assumption.GetArgument(0);
            if (ReferenceEquals(condition, value) && IsValid(assumption))
            { Count(metrics, "known_bits.assume.boolean_true"); return LlvmKnownBits.MakeConstant(LlvmAPInt.GetAllOnes(known.BitWidth)); }
            if (condition is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt { IsMinusOne: true } } not &&
                ReferenceEquals(not.Left, value) && IsValid(assumption))
            { Count(metrics, "known_bits.assume.boolean_false"); return LlvmKnownBits.MakeConstant(LlvmAPInt.GetZero(known.BitWidth)); }
            if (condition is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } trunc && ReferenceEquals(trunc.Operand, value) && IsValid(assumption))
            {
                Count(metrics, "known_bits.assume.trunc");
                return trunc.HasNoUnsignedWrap ? LlvmKnownBits.MakeConstant(new(known.BitWidth, 1)) :
                    new(known.Zero, known.One | new LlvmAPInt(known.BitWidth, 1));
            }
            if (depth == MaxDepth || condition is not LlvmICmpInstruction comparison || !IsValid(assumption)) continue;
            known = known.UnionWith(FromCondition(value, comparison, false, depth, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout));
            Count(metrics, "known_bits.assume.icmp");
        }
        if (known.HasConflict) { Count(metrics, "known_bits.assume.conflict_reset"); return new(known.BitWidth); }
        return known;
        bool IsValid(LlvmInstruction assumption) => LlvmValueTracking.IsValidAssumeForContext(assumption, context, tree, allowEphemerals, metrics);
    }
}
