// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineAndOrXor.cpp matchBSwapOrBitReverse.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? MatchBSwapOrBitReverse(LlvmInstruction instruction, bool matchSwaps, bool matchReversals)
        {
            List<LlvmInstruction> inserted = [];
            if (!LlvmLocal.RecognizeBSwapOrBitReverseIdiom(instruction, matchSwaps, matchReversals, inserted, Metrics)) return null;
            var result = inserted[^1]; inserted.RemoveAt(inserted.Count - 1);
            result.RemoveFromParent();
            foreach (var intermediate in inserted) { intermediate.Source = instruction.Source; Worklist.Push(intermediate); }
            Count("instcombine.logic.bit_order_rewrites");
            return result;
        }
    }
}
