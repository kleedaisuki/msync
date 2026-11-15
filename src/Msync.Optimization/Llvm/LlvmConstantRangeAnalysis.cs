// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/ValueTracking.cpp computeConstantRange / computeConstantRangeIncludingKnownBits.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantRangeAnalysis
{
    private const uint MaxDepth = 6;
    public static LlvmConstantRange Compute(LlvmValue value, bool forSigned = false, uint depth = 0,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Debug.Assert(value.Type.IsIntegerTy());
        Count(metrics, "constant_range.visits");
        uint width = value.Type.GetIntegerBitWidth();
        if (depth == MaxDepth) { Count(metrics, "constant_range.depth_limit"); return LlvmConstantRange.Full(width); }
        if (value is LlvmConstant constant)
        {
            Count(metrics, "constant_range.constant");
            // Constants.cpp Constant::toConstantRange scalar branch.
            return constant is LlvmConstantInt integer ? new(integer.Value) : LlvmConstantRange.Full(width);
        }
        var range = LlvmConstantRange.Full(width);
        switch (value)
        {
            case LlvmBinaryOperator binary:
                Count(metrics, "constant_range.binary_limits");
                range = BinOpLimits(binary, forSigned, useInstructionInfo);
                break;
            case LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } descriptor } call:
                Count(metrics, "constant_range.intrinsic");
                range = RangeForIntrinsic(call, descriptor, useInstructionInfo);
                break;
            case LlvmSelectInstruction select:
                Count(metrics, "constant_range.select");
                var whenTrue = Compute(select.TrueValue, forSigned, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
                var whenFalse = Compute(select.FalseValue, forSigned, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
                range = whenTrue.UnionWith(whenFalse).IntersectWith(RangeForSelectPattern(select, useInstructionInfo, metrics));
                break;
            case LlvmCastInstruction cast when cast.Opcode is LlvmOpcode.Trunc or LlvmOpcode.ZExt or LlvmOpcode.SExt:
                Count(metrics, "constant_range.cast");
                var input = Compute(cast.Operand, forSigned, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
                range = cast.Opcode switch
                {
                    LlvmOpcode.Trunc => input.Truncate(width), LlvmOpcode.ZExt => input.ZeroExtend(width),
                    _ => input.SignExtend(width)
                };
                break;
            case LlvmCastInstruction cast when cast.Opcode is LlvmOpcode.FPToUI or LlvmOpcode.FPToSI:
                Count(metrics, "constant_range.fp_to_int");
                if (cast.Operand.Type.IsHalfTy())
                {
                    if (cast.Opcode == LlvmOpcode.FPToSI && width >= 17)
                        range = new(new LlvmAPInt(width, unchecked((ulong)-65504L), isSigned: true), new LlvmAPInt(width, 65505));
                    if (cast.Opcode == LlvmOpcode.FPToUI && width >= 16)
                        range = new(LlvmAPInt.GetZero(width), new(width, 65505));
                }
                break;
            default:
                // Other intrinsic IDs, range attributes/metadata,
                // frexp aggregate intrinsic facts are not represented.
                Count(metrics, "constant_range.no_source_facts");
                break;
        }
        return IntersectAssumedRanges(value, range, depth, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext);
    }
    public static LlvmConstantRange ComputeIncludingKnownBits(LlvmValue value, bool forSigned = false,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null, bool useInstructionInfo = true, LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        var known = LlvmConstantRange.FromKnownBits(LlvmKnownBitsAnalysis.Compute(value, context: context, tree: tree, metrics: metrics, useInstructionInfo: useInstructionInfo, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext), forSigned);
        var range = Compute(value, forSigned, context: context, tree: tree, metrics: metrics, useInstructionInfo: useInstructionInfo, assumptions: assumptions, allowEphemerals: allowEphemerals, conditions: conditions, condContext: condContext);
        return known.IntersectWith(range, forSigned ? LlvmPreferredRangeType.Signed : LlvmPreferredRangeType.Unsigned);
    }
    private static LlvmConstantRange RangeForIntrinsic(LlvmCallBase call, LlvmIntrinsicDescriptor descriptor, bool useInstructionInfo)
    {
        uint width = call.Type.GetIntegerBitWidth();
        if (descriptor.Kind == LlvmIntrinsicKind.CTPop)
            return LlvmConstantRange.GetNonEmpty(LlvmAPInt.GetZero(width), LlvmConstantRange.Inc(new LlvmAPInt(width, width)));
        if (descriptor.Kind == LlvmIntrinsicKind.CTTZ)
        {
            var upper = new LlvmAPInt(width, width);
            if (!useInstructionInfo || call.GetArgument(1) is not LlvmConstantInt { IsOne: true }) upper = LlvmConstantRange.Inc(upper);
            return LlvmConstantRange.GetNonEmpty(LlvmAPInt.GetZero(width), upper);
        }
        if (descriptor.HasComparisonRange)
            return LlvmConstantRange.GetNonEmpty(LlvmAPInt.GetAllOnes(width), new LlvmAPInt(width, 2));
        if (!LlvmIntrinsic.IsMinMax(descriptor.Kind)) return LlvmConstantRange.Full(width);
        var constant = call.GetArgument(0) as LlvmConstantInt ?? call.GetArgument(1) as LlvmConstantInt;
        if (constant is null) return LlvmConstantRange.Full(width);
        var kind = descriptor.Kind;
        if (kind == LlvmIntrinsicKind.UMin) return LlvmConstantRange.GetNonEmpty(LlvmAPInt.GetZero(width), LlvmConstantRange.Inc(constant.Value));
        if (kind == LlvmIntrinsicKind.UMax) return LlvmConstantRange.GetNonEmpty(constant.Value, LlvmAPInt.GetZero(width));
        if (kind == LlvmIntrinsicKind.SMin) return LlvmConstantRange.GetNonEmpty(LlvmAPInt.GetSignedMinValue(width), LlvmConstantRange.Inc(constant.Value));
        if (kind == LlvmIntrinsicKind.SMax) return LlvmConstantRange.GetNonEmpty(constant.Value, LlvmConstantRange.Inc(LlvmAPInt.GetSignedMaxValue(width)));
        return LlvmConstantRange.Full(width);
    }
    private static void Count(Dictionary<string, ulong>? metrics, string name)
    {
        if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1;
    }
}
