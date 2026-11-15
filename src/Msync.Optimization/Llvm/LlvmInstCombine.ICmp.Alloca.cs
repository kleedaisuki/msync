// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp foldAllocaCmp.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private bool FoldAllocaCmp(LlvmAllocaInstruction alloca)
        {
            var tracker = new AllocaCompareCaptureTracker(alloca, Metrics);
            LlvmCaptureTracking.PointerMayBeCaptured(alloca, tracker, metrics: Metrics);
            if (tracker.HasCapture) return false;
            bool changed = false;
            foreach (var (compare, operands) in tracker.Comparisons)
            {
                if (operands == 3) continue;
                var value = LlvmConstantInt.GetBool(compare.Context, compare.Predicate == LlvmCmpPredicate.ICmpNe);
                ReplaceInstUsesWith(compare, value);
                EraseInstFromFunction(compare);
                Count("icmp.alloca.comparisons_folded");
                changed = true;
            }
            return changed;
        }

        private sealed class AllocaCompareCaptureTracker(LlvmAllocaInstruction alloca,
            Dictionary<string, ulong>? metrics) : LlvmCaptureTracker
        {
            internal bool HasCapture;
            internal readonly Dictionary<LlvmICmpInstruction, uint> Comparisons = new(ReferenceEqualityComparer.Instance);
            public override void TooManyUses() => HasCapture = true;
            public override LlvmCaptureAction Captured(LlvmUse use, LlvmUseCaptureInfo info)
            {
                if (use.User is LlvmICmpInstruction compare && ICmpIsEquality(compare.Predicate) &&
                    LlvmValueTracking.GetUnderlyingObject(use.Value!, metrics: metrics) == alloca)
                {
                    Comparisons[compare] = Comparisons.GetValueOrDefault(compare) | (1U << use.OperandNo);
                    return LlvmCaptureAction.Continue;
                }
                HasCapture = true;
                return LlvmCaptureAction.Stop;
            }
        }
    }
}
