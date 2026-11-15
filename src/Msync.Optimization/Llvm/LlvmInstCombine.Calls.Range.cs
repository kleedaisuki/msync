// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCalls.cpp
// foldCttzCtlz and foldCtpop return-range attribute refinement.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? CallsSetCountZeroRange(LlvmCallInstruction call, uint definite, uint possible)
        {
            uint width = call.Type.GetScalarSizeInBits();
            if (width == 1 || call.HasRetAttr(LlvmAttributeKind.Range)) return null;
            call.AddRangeRetAttr(new LlvmAPInt(width, definite), new LlvmAPInt(width, possible + 1));
            Count("calls.count_zeros.range_added"); return call;
        }
        private LlvmInstruction? CallsSetCtpopRange(LlvmCallInstruction call, LlvmKnownBits known)
        {
            uint width = call.Type.GetScalarSizeInBits();
            if (width == 1) return null;
            LlvmConstantRange old = call.GetRange() ?? LlvmConstantRange.Full(width);
            uint lower = known.One.PopCount(), upper = (~known.Zero).PopCount() + 1;
            if (lower == 0 && old.Contains(LlvmAPInt.GetZero(width)) && CallsKnownNonZero(call.GetArgument(0), call)) lower = 1;
            LlvmConstantRange range = new(new LlvmAPInt(width, lower), new LlvmAPInt(width, upper));
            range = range.IntersectWith(old, LlvmPreferredRangeType.Unsigned);
            if (range == old) return null;
            call.AddRangeRetAttr(range.Lower, range.Upper); Count("calls.ctpop.range_refined"); return call;
        }
    }
}
