// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/KnownBits.cpp
// makeGE, umax, umin, smax, smin, flipSignBit.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static LlvmKnownBits ComputeMinMaxIntrinsic(LlvmCallInstruction call, LlvmIntrinsicKind kind, BitsQuery query)
    {
        Count(query.Metrics, "known_bits.minmax_intrinsic");
        var left = query.Recurse(call.GetArgument(0));
        var right = query.Recurse(call.GetArgument(1));
        var known = kind == LlvmIntrinsicKind.UMin ? LlvmKnownBits.UMin(left, right) :
            kind == LlvmIntrinsicKind.UMax ? LlvmKnownBits.UMax(left, right) :
            kind == LlvmIntrinsicKind.SMin ? LlvmKnownBits.SMin(left, right) : LlvmKnownBits.SMax(left, right);
        if ((kind == LlvmIntrinsicKind.SMin || kind == LlvmIntrinsicKind.SMax) &&
            LlvmSelectPattern.TrySignedMinMaxIntrinsicClamp(call, out var low, out var high, query.Metrics))
        {
            Count(query.Metrics, "known_bits.minmax_intrinsic.clamp");
            known = known.UnionWith(LlvmConstantRange.GetNonEmpty(low, high + new LlvmAPInt(high.BitWidth, 1)).ToKnownBits());
        }
        Count(query.Metrics, known.IsUnknown ? "known_bits.minmax_intrinsic.unknown" : "known_bits.minmax_intrinsic.facts");
        return known;
    }

    private static LlvmKnownBits ComputeMinMaxPhiRecurrence(LlvmPhiInstruction phi, uint depth,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
    {
        uint width = phi.Type.GetIntegerBitWidth();
        for (int n = 0; n < phi.IncomingCount; n++)
            if (phi.GetIncomingValue(n) is LlvmBinaryOperator binary &&
                (ReferenceEquals(binary.Left, phi) || ReferenceEquals(binary.Right, phi))) return new(width);
        for (int n = 0; n < phi.IncomingCount; n++)
        {
            if (phi.GetIncomingValue(n) is not LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } call ||
                !LlvmIntrinsic.IsMinMax(descriptor.Kind)) continue;
            bool leftPhi = ReferenceEquals(call.GetArgument(0), phi), rightPhi = ReferenceEquals(call.GetArgument(1), phi);
            if (!leftPhi && !rightPhi) continue;
            var kind = descriptor.Kind;
            if (kind != LlvmIntrinsicKind.UMin && kind != LlvmIntrinsicKind.UMax) return new(width);
            var start = phi.GetIncomingValue(1 - n);
            var step = call.GetArgument(leftPhi ? 1 : 0);
            int startIndex = ReferenceEquals(phi.GetIncomingValue(0), start) ? 0 : 1;
            var initial = Compute(start, depth + 1, phi.GetIncomingBlock(startIndex).GetTerminator(), tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null, layout);
            var increment = Compute(step, depth + 1, phi.GetIncomingBlock(1 - startIndex).GetTerminator(), tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, null, layout);
            Count(metrics, "known_bits.phi.minmax_recurrence");
            return kind == LlvmIntrinsicKind.UMin ?
                new(LlvmAPInt.GetHighBitsSet(width, initial.CountMinLeadingZeros()),
                    LlvmAPInt.GetHighBitsSet(width, Math.Min(initial.CountMinLeadingOnes(), increment.CountMinLeadingOnes()))) :
                new(LlvmAPInt.GetHighBitsSet(width, Math.Min(initial.CountMinLeadingZeros(), increment.CountMinLeadingZeros())),
                    LlvmAPInt.GetHighBitsSet(width, initial.CountMinLeadingOnes()));
        }
        return new(width);
    }
}
