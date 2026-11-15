// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: CaptureTracking.cpp
// EarliestCaptures and FindEarliestCapture.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmCaptureTracking
{
    public static (LlvmInstruction? Earliest, LlvmCaptureResult Captures) FindEarliestCapture(LlvmValue value,
        LlvmFunction function, LlvmDominatorTree tree, LlvmCaptureComponents mask,
        uint maxUsesToExplore = 0, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(value is not LlvmGlobalValue);
        Count(metrics, "capture.earliest.queries");
        var tracker = new EarliestCapturesTracker(function, tree, mask, metrics);
        PointerMayBeCaptured(value, tracker, maxUsesToExplore, metrics);
        Count(metrics, tracker.WithoutReturn.CapturesAnything() ? "capture.earliest.captured" : "capture.earliest.not_captured");
        return (tracker.Earliest, new(tracker.WithoutReturn, tracker.WithReturn));
    }

    private sealed class EarliestCapturesTracker(LlvmFunction function, LlvmDominatorTree tree,
        LlvmCaptureComponents mask, Dictionary<string, ulong>? metrics) : LlvmCaptureTracker
    {
        internal LlvmInstruction? Earliest;
        internal LlvmCaptureComponents WithoutReturn;
        internal LlvmCaptureComponents WithReturn;
        public override void TooManyUses()
        {
            Count(metrics, "capture.earliest.use_limit");
            WithoutReturn = WithReturn = mask;
            Earliest = function.BasicBlocks[0].FirstInstruction;
        }
        public override LlvmCaptureAction Captured(LlvmUse use, LlvmUseCaptureInfo info)
        {
            var instruction = (LlvmInstruction)use.User;
            var captured = info.UseCC & mask;
            if (captured.CapturesAnything())
            {
                WithReturn |= captured;
                if (instruction is not LlvmReturnInstruction)
                {
                    Count(metrics, "capture.earliest.nonreturn_captures");
                    Earliest = Earliest is null ? instruction : tree.FindNearestCommonDominator(Earliest, instruction, metrics);
                    WithoutReturn |= captured;
                }
            }
            return LlvmCaptureAction.Continue;
        }
    }
}
