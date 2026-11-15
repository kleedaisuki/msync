// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombiner.h query wrappers and ValueTracking.cpp isKnownPositive.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmKnownBits ComputeKnownBits(LlvmValue value, LlvmInstruction? context = null, uint depth = 0) =>
            LlvmKnownBitsAnalysis.Compute(value, depth, context, DT, Metrics, SQ.UseInstructionInfo, AC, SQ.AllowEphemerals, DC, condContext: SQ.CondContext);
        private bool IsKnownToBeAPowerOfTwo(LlvmValue value, bool orZero = false, LlvmInstruction? context = null, uint depth = 0) =>
            LlvmKnownBitsAnalysis.IsKnownToBeAPowerOfTwo(value, orZero, depth, context, DT, Metrics,
                useInstructionInfo: SQ.UseInstructionInfo, assumptions: AC, allowEphemerals: SQ.AllowEphemerals, conditions: DC, condContext: SQ.CondContext);
        private bool MaskedValueIsZero(LlvmValue value, LlvmAPInt mask, LlvmInstruction? context = null, uint depth = 0) =>
            LlvmKnownBitsAnalysis.MaskedValueIsZero(value, mask, depth, context, DT, Metrics, SQ.UseInstructionInfo, AC, SQ.AllowEphemerals, DC, condContext: SQ.CondContext);
        private uint ComputeNumSignBits(LlvmValue value, LlvmInstruction? context = null, uint depth = 0) =>
            LlvmKnownBitsAnalysis.ComputeNumSignBits(value, depth, context, DT, Metrics, SQ.UseInstructionInfo, AC, SQ.AllowEphemerals, DC, condContext: SQ.CondContext);
        private uint ComputeMaxSignificantBits(LlvmValue value, LlvmInstruction? context = null, uint depth = 0) =>
            LlvmKnownBitsAnalysis.ComputeMaxSignificantBits(value, depth, context, DT, Metrics, SQ.UseInstructionInfo, AC, SQ.AllowEphemerals, DC, condContext: SQ.CondContext);
        private bool IsKnownNonZero(LlvmValue value, LlvmInstruction? context = null, uint depth = 0) =>
            LlvmKnownBitsAnalysis.IsKnownNonZero(value, depth, context, DT, Metrics, SQ.UseInstructionInfo, AC, SQ.AllowEphemerals, DC, condContext: SQ.CondContext);
        private bool IsKnownNonNegative(LlvmValue value, LlvmInstruction? context = null, uint depth = 0) =>
            ComputeKnownBits(value, context, depth).IsNonNegative;
        private bool IsKnownNegative(LlvmValue value, LlvmInstruction? context = null, uint depth = 0) =>
            ComputeKnownBits(value, context, depth).IsNegative;
        private bool IsKnownPositive(LlvmValue value, LlvmInstruction? context = null, uint depth = 0)
        {
            if (value is LlvmConstantInt constant) return !constant.IsZero && !constant.Value.IsNegative;
            LlvmKnownBits known = ComputeKnownBits(value, context, depth);
            return known.IsNonNegative && (known.IsNonZero || IsKnownNonZero(value, context, depth));
        }
        private LlvmOverflowResult ComputeOverflowForUnsignedAdd(LlvmValue left, LlvmValue right, LlvmInstruction? context) =>
            LlvmValueTracking.ComputeOverflowForUnsignedAdd(left, right, DL, context, DT, Metrics,
                SQ.UseInstructionInfo, AC, SQ.AllowEphemerals, DC, condContext: SQ.CondContext);
        private LlvmOverflowResult ComputeOverflowForSignedAdd(LlvmValue left, LlvmValue right, LlvmInstruction? context) =>
            LlvmValueTracking.ComputeOverflowForSignedAdd(left, right, DL, context, DT, Metrics,
                SQ.UseInstructionInfo, AC, SQ.AllowEphemerals, DC, condContext: SQ.CondContext);
        private LlvmOverflowResult ComputeOverflowForUnsignedSub(LlvmValue left, LlvmValue right, LlvmInstruction? context) =>
            LlvmValueTracking.ComputeOverflowForUnsignedSub(left, right, DL, context, DT, Metrics,
                SQ.UseInstructionInfo, AC, SQ.AllowEphemerals, DC, condContext: SQ.CondContext);
        private LlvmOverflowResult ComputeOverflowForSignedSub(LlvmValue left, LlvmValue right, LlvmInstruction? context) =>
            LlvmValueTracking.ComputeOverflowForSignedSub(left, right, DL, context, DT, Metrics,
                SQ.UseInstructionInfo, AC, SQ.AllowEphemerals, DC, condContext: SQ.CondContext);
        private LlvmOverflowResult ComputeOverflowForUnsignedMul(LlvmValue left, LlvmValue right, LlvmInstruction? context, bool isNSW = false) =>
            LlvmValueTracking.ComputeOverflowForUnsignedMul(left, right, DL, context, DT, Metrics,
                SQ.UseInstructionInfo, AC, SQ.AllowEphemerals, isNSW: isNSW, conditions: DC, condContext: SQ.CondContext);
        private LlvmOverflowResult ComputeOverflowForSignedMul(LlvmValue left, LlvmValue right, LlvmInstruction? context) =>
            LlvmValueTracking.ComputeOverflowForSignedMul(left, right, DL, context, DT, Metrics,
                SQ.UseInstructionInfo, AC, SQ.AllowEphemerals, conditions: DC, condContext: SQ.CondContext);
        private bool WillNotOverflowSignedAdd(LlvmValue left, LlvmValue right, LlvmInstruction context) =>
            ComputeOverflowForSignedAdd(left, right, context) == LlvmOverflowResult.NeverOverflows;
        private bool WillNotOverflowUnsignedAdd(LlvmValue left, LlvmValue right, LlvmInstruction context) =>
            ComputeOverflowForUnsignedAdd(left, right, context) == LlvmOverflowResult.NeverOverflows;
        private bool WillNotOverflowSignedSub(LlvmValue left, LlvmValue right, LlvmInstruction context) =>
            ComputeOverflowForSignedSub(left, right, context) == LlvmOverflowResult.NeverOverflows;
        private bool WillNotOverflowUnsignedSub(LlvmValue left, LlvmValue right, LlvmInstruction context) =>
            ComputeOverflowForUnsignedSub(left, right, context) == LlvmOverflowResult.NeverOverflows;
        private bool WillNotOverflowSignedMul(LlvmValue left, LlvmValue right, LlvmInstruction context) =>
            ComputeOverflowForSignedMul(left, right, context) == LlvmOverflowResult.NeverOverflows;
        private bool WillNotOverflowUnsignedMul(LlvmValue left, LlvmValue right, LlvmInstruction context, bool isNSW = false) =>
            ComputeOverflowForUnsignedMul(left, right, context, isNSW) == LlvmOverflowResult.NeverOverflows;
        private bool WillNotOverflowAdd(LlvmValue left, LlvmValue right, LlvmInstruction context, bool isSigned) =>
            isSigned ? WillNotOverflowSignedAdd(left, right, context) : WillNotOverflowUnsignedAdd(left, right, context);
        private bool WillNotOverflowSub(LlvmValue left, LlvmValue right, LlvmInstruction context, bool isSigned) =>
            isSigned ? WillNotOverflowSignedSub(left, right, context) : WillNotOverflowUnsignedSub(left, right, context);
        private bool WillNotOverflowMul(LlvmValue left, LlvmValue right, LlvmInstruction context, bool isSigned) =>
            isSigned ? WillNotOverflowSignedMul(left, right, context) : WillNotOverflowUnsignedMul(left, right, context);
        private bool WillNotOverflow(LlvmOpcode opcode, LlvmValue left, LlvmValue right, LlvmInstruction context, bool isSigned) => opcode switch
        {
            LlvmOpcode.Add => WillNotOverflowAdd(left, right, context, isSigned),
            LlvmOpcode.Sub => WillNotOverflowSub(left, right, context, isSigned),
            LlvmOpcode.Mul => WillNotOverflowMul(left, right, context, isSigned),
            _ => throw new InvalidOperationException("The source overflow query expects add, sub or mul.")
        };
    }
}
