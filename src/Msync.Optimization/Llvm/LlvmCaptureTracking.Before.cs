// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: CaptureTracking.cpp
// CapturesBefore and PointerMayBeCapturedBefore, using Analysis/CFG.cpp reachability.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmCaptureTracking
{
    public static bool PointerMayBeCapturedBefore(LlvmValue value, bool returnCaptures, LlvmInstruction instruction,
        LlvmDominatorTree? tree, bool includeInstruction = false, uint maxUsesToExplore = 0,
        LlvmLoopInfo? loops = null, Dictionary<string, ulong>? metrics = null) =>
        PointerMayBeCapturedBefore(value, returnCaptures, instruction, tree, includeInstruction,
            LlvmCaptureComponents.All, loops: loops, maxUsesToExplore: maxUsesToExplore, metrics: metrics).CapturesAnything();

    public static LlvmCaptureComponents PointerMayBeCapturedBefore(LlvmValue value, bool returnCaptures,
        LlvmInstruction instruction, LlvmDominatorTree? tree, bool includeInstruction, LlvmCaptureComponents mask,
        Func<LlvmCaptureComponents, bool>? stop = null, LlvmLoopInfo? loops = null,
        uint maxUsesToExplore = 0, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(value is not LlvmGlobalValue);
        Count(metrics, "capture.before.queries");
        if (tree is null)
        {
            Count(metrics, "capture.before.without_dominators");
            var result = PointerMayBeCaptured(value, mask, stop, maxUsesToExplore, metrics);
            return returnCaptures ? result.WithRet : result.WithoutRet;
        }
        var tracker = new CapturesBeforeTracker(returnCaptures, instruction, tree, includeInstruction,
            loops, mask, stop ?? CapturesAnything, metrics);
        PointerMayBeCaptured(value, tracker, maxUsesToExplore, metrics);
        Count(metrics, tracker.Components.CapturesAnything() ? "capture.before.captured" : "capture.before.not_captured");
        return tracker.Components;
    }

    private sealed class CapturesBeforeTracker(bool returnCaptures, LlvmInstruction before,
        LlvmDominatorTree tree, bool includeInstruction, LlvmLoopInfo? loops,
        LlvmCaptureComponents mask, Func<LlvmCaptureComponents, bool> stop,
        Dictionary<string, ulong>? metrics) : LlvmCaptureTracker
    {
        internal LlvmCaptureComponents Components;
        public override void TooManyUses() => Components = mask;
        public override LlvmCaptureAction Captured(LlvmUse use, LlvmUseCaptureInfo info)
        {
            var instruction = (LlvmInstruction)use.User;
            if (instruction is LlvmReturnInstruction && !returnCaptures) return LlvmCaptureAction.ContinueIgnoringReturn;
            if (IsSafeToPrune(instruction))
            { Count(metrics, "capture.before.pruned_capture"); return LlvmCaptureAction.ContinueIgnoringReturn; }
            if ((info.UseCC & mask).CapturesNothing()) return LlvmCaptureAction.Continue;
            Components |= info.UseCC & mask;
            return stop(Components) ? LlvmCaptureAction.Stop : LlvmCaptureAction.Continue;
        }

        private bool IsSafeToPrune(LlvmInstruction instruction)
        {
            if (instruction == before) return !includeInstruction;
            if (!tree.IsReachableFromEntry(instruction.Parent!))
            { Count(metrics, "capture.before.unreachable_use"); return true; }
            Count(metrics, "capture.before.reachability_queries");
            return !LlvmCfgReachability.IsPotentiallyReachable(instruction, before, tree: tree, loops: loops, metrics: metrics);
        }
    }
}
