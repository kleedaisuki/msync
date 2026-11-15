// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Transforms/Scalar/LICM.h LICMOptions; ScalarOptions.td source defaults;
// LoopUtils.h SinkAndHoistLICMFlags / LICM.cpp constructor.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed record LlvmLicmOptions(uint? MssaOptCap = null, uint? MssaNoAccForPromotionCap = null,
    bool AllowSpeculation = true);

public sealed record LlvmLicmScalarOptions(bool DisablePromotion = false, uint MssaOptimizationCap = 100,
    uint MaxAccessesForPromotion = 250, uint MaxFpReassociations = 5, uint MaxIntReassociations = 5,
    uint MaxUsesTraversed = 8);

public static partial class LlvmLicm
{
    private sealed class SinkAndHoistFlags
    {
        internal bool IsSink;
        private readonly uint optimizationCap;
        private uint clobberCalls;
        internal readonly bool TooManyMemoryAccesses;
        internal bool TooManyClobberingCalls => clobberCalls >= optimizationCap;
        internal void IncrementClobberingCalls() => ++clobberCalls;

        internal SinkAndHoistFlags(uint optCap, uint promotionCap, bool isSink, LlvmLoop loop, LlvmMemorySsa memory)
        {
            optimizationCap = optCap;
            IsSink = isSink;
            uint accesses = 0;
            foreach (LlvmBasicBlock block in loop.Blocks)
            {
                if (memory.GetBlockAccesses(block) is not { } list) continue;
                foreach (var access in list)
                    if (++accesses > promotionCap) { TooManyMemoryAccesses = true; return; }
            }
        }
    }
}
