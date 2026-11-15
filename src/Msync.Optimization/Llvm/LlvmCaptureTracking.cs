// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/ModRef.h,
// Analysis/CaptureTracking.h/.cpp PointerMayBeCaptured and SimpleCaptureTracker.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public readonly record struct LlvmCaptureResult(LlvmCaptureComponents WithoutRet, LlvmCaptureComponents WithRet);
public readonly record struct LlvmUseCaptureInfo(LlvmCaptureComponents UseCC, LlvmCaptureComponents ResultCC = LlvmCaptureComponents.None)
{
    public static LlvmUseCaptureInfo Passthrough => new(LlvmCaptureComponents.None, LlvmCaptureComponents.All);
    public bool IsPassthrough => UseCC == LlvmCaptureComponents.None && ResultCC != LlvmCaptureComponents.None;
}

public enum LlvmCaptureAction : byte { Stop, Continue, ContinueIgnoringReturn }

public abstract class LlvmCaptureTracker
{
    public abstract void TooManyUses();
    public virtual bool ShouldExplore(LlvmUse use) => true;
    public abstract LlvmCaptureAction Captured(LlvmUse use, LlvmUseCaptureInfo info);
}

public static partial class LlvmCaptureTracking
{
    public const uint DefaultMaxUsesToExplore = 150;
    public static bool CapturesNothing(this LlvmCaptureComponents components) => components == LlvmCaptureComponents.None;
    public static bool CapturesAnything(this LlvmCaptureComponents components) => components != LlvmCaptureComponents.None;
    public static bool CapturesAddressIsNullOnly(this LlvmCaptureComponents components) =>
        (components & LlvmCaptureComponents.Address) == LlvmCaptureComponents.AddressIsNull;
    public static bool CapturesAddress(this LlvmCaptureComponents components) =>
        (components & LlvmCaptureComponents.Address) != LlvmCaptureComponents.None;
    public static bool CapturesReadProvenanceOnly(this LlvmCaptureComponents components) =>
        (components & LlvmCaptureComponents.Provenance) == LlvmCaptureComponents.ReadProvenance;
    public static bool CapturesFullProvenance(this LlvmCaptureComponents components) =>
        (components & LlvmCaptureComponents.Provenance) == LlvmCaptureComponents.Provenance;
    public static bool CapturesAnyProvenance(this LlvmCaptureComponents components) =>
        (components & LlvmCaptureComponents.Provenance) != LlvmCaptureComponents.None;
    public static bool CapturesAll(this LlvmCaptureComponents components) => components == LlvmCaptureComponents.All;

    public static bool PointerMayBeCaptured(LlvmValue value, bool returnCaptures, uint maxUsesToExplore = 0,
        Dictionary<string, ulong>? metrics = null)
    {
        var result = PointerMayBeCaptured(value, LlvmCaptureComponents.All, maxUsesToExplore: maxUsesToExplore, metrics: metrics);
        return (returnCaptures ? result.WithRet : result.WithoutRet).CapturesAnything();
    }

    public static LlvmCaptureResult PointerMayBeCaptured(LlvmValue value, LlvmCaptureComponents mask,
        Func<LlvmCaptureComponents, bool>? stop = null, uint maxUsesToExplore = 0, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(value is not LlvmGlobalValue);
        var tracker = new SimpleCaptureTracker(mask, stop ?? CapturesAnything);
        PointerMayBeCaptured(value, tracker, maxUsesToExplore, metrics);
        Count(metrics, tracker.WithoutReturn.CapturesAnything() ? "capture.captured" : "capture.not_captured");
        return new(tracker.WithoutReturn, tracker.WithReturn);
    }

    public static void PointerMayBeCaptured(LlvmValue value, LlvmCaptureTracker tracker, uint maxUsesToExplore = 0,
        Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(value.Type.IsPointerTy());
        Count(metrics, "capture.queries");
        if (maxUsesToExplore == 0) maxUsesToExplore = DefaultMaxUsesToExplore;
        List<LlvmUse> work = new((int)DefaultMaxUsesToExplore);
        HashSet<LlvmUse> visited = new(ReferenceEqualityComparer.Instance);
        if (!AddUses(value, tracker, maxUsesToExplore, work, visited, metrics)) return;
        while (work.Count != 0)
        {
            var use = work[^1]; work.RemoveAt(work.Count - 1);
            Count(metrics, "capture.uses_examined");
            var info = DetermineUseCaptureKind(use, value, metrics);
            if (info.UseCC.CapturesAnything())
            {
                Count(metrics, "capture.candidate");
                switch (tracker.Captured(use, info))
                {
                    case LlvmCaptureAction.Stop:
                        Count(metrics, "capture.early_stop");
                        return;
                    case LlvmCaptureAction.ContinueIgnoringReturn:
                        continue;
                    case LlvmCaptureAction.Continue:
                        if ((info.ResultCC & ~info.UseCC).CapturesNothing()) continue;
                        break;
                }
            }
            if (info.ResultCC.CapturesAnything() && !AddUses(use.User, tracker, maxUsesToExplore, work, visited, metrics)) return;
        }
    }

    private static bool AddUses(LlvmValue value, LlvmCaptureTracker tracker, uint max,
        List<LlvmUse> work, HashSet<LlvmUse> visited, Dictionary<string, ulong>? metrics)
    {
        foreach (var use in value.Uses)
        {
            if ((uint)visited.Count >= max)
            { Count(metrics, "capture.use_budget_exhausted"); tracker.TooManyUses(); return false; }
            if (!visited.Add(use)) { Count(metrics, "capture.revisited_use"); continue; }
            if (!tracker.ShouldExplore(use)) { Count(metrics, "capture.pruned_use"); continue; }
            work.Add(use);
        }
        return true;
    }

    private sealed class SimpleCaptureTracker(LlvmCaptureComponents mask, Func<LlvmCaptureComponents, bool> stop) : LlvmCaptureTracker
    {
        internal LlvmCaptureComponents WithoutReturn, WithReturn;
        public override void TooManyUses() => WithoutReturn = WithReturn = mask;
        public override LlvmCaptureAction Captured(LlvmUse use, LlvmUseCaptureInfo info)
        {
            var captures = info.UseCC & mask;
            if (captures.CapturesNothing()) return LlvmCaptureAction.Continue;
            WithReturn |= captures;
            if (use.User is not LlvmReturnInstruction) WithoutReturn |= captures;
            return stop(WithoutReturn) ? LlvmCaptureAction.Stop : LlvmCaptureAction.Continue;
        }
    }

    private static void Count(Dictionary<string, ulong>? metrics, string name)
    { if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1; }
}
