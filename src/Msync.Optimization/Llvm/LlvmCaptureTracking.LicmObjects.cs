// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// AliasAnalysis.cpp isNotVisibleOnUnwind/isWritableObject;
// LICM.cpp isNotCapturedBeforeOrInLoop/isNotVisibleOnUnwindInLoop/isThreadLocalObject.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static class LlvmLicmObjectFacts
{
    public static bool IsNotCapturedBeforeOrInLoop(LlvmValue value, LlvmLoop loop, LlvmDominatorTree tree,
        Dictionary<string, ulong>? metrics = null) =>
        LlvmCaptureTracking.PointerMayBeCapturedBefore(value, true, loop.Header.GetTerminator(), tree,
            false, LlvmCaptureComponents.Provenance, metrics: metrics).CapturesNothing();

    public static bool IsNotVisibleOnUnwind(LlvmValue value, out bool requiresNoCaptureBeforeUnwind,
        Dictionary<string, ulong>? metrics = null)
    {
        requiresNoCaptureBeforeUnwind = false;
        Count(metrics, "licm.object.unwind_queries");
        if (value is LlvmAllocaInstruction)
        { Count(metrics, "licm.object.unwind_alloca"); return true; }
        if (value is LlvmHeapAllocInst)
        {
            requiresNoCaptureBeforeUnwind = true;
            Count(metrics, "licm.object.unwind_heap_fresh");
            return true;
        }
        // Argument ByVal/DeadOnUnwind and call NoAlias attributes are absent
        // from the represented IR; their source attribute queries have no fact.
        Count(metrics, "licm.object.unwind_no_attribute_fact");
        return false;
    }

    public static bool IsNotVisibleOnUnwindInLoop(LlvmValue value, LlvmLoop loop, LlvmDominatorTree tree,
        Dictionary<string, ulong>? metrics = null)
    {
        if (!IsNotVisibleOnUnwind(value, out bool requiresNoCapture, metrics)) return false;
        return !requiresNoCapture || IsNotCapturedBeforeOrInLoop(value, loop, tree, metrics);
    }

    public static bool IsThreadLocalObject(LlvmValue value, LlvmLoop loop, LlvmDominatorTree tree,
        Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "licm.object.thread_local_queries");
        if (LlvmBasicAliasAnalysis.IsIdentifiedFunctionLocal(value) && IsNotCapturedBeforeOrInLoop(value, loop, tree, metrics))
        { Count(metrics, "licm.object.thread_local_uncaptured"); return true; }
        bool single = loop.Header.Module!.GetThreadModel() == LlvmThreadModel.Single;
        Count(metrics, single ? "licm.object.thread_model_single" : "licm.object.thread_model_not_single");
        return single;
    }

    public static bool IsWritableObject(LlvmValue value, out bool explicitlyDereferenceableOnly,
        Dictionary<string, ulong>? metrics = null)
    {
        explicitlyDereferenceableOnly = false;
        Count(metrics, "licm.object.writable_queries");
        if (value is LlvmAllocaInstruction)
        { Count(metrics, "licm.object.writable_alloca"); return true; }
        if (value is LlvmHeapAllocInst)
        { Count(metrics, "licm.object.writable_heap"); return true; }
        // Source deliberately does not infer writable provenance for globals.
        // Writable+NoAlias/ByVal arguments and NoAlias calls need attributes
        // which the current argument/call carriers do not represent.
        Count(metrics, "licm.object.writable_no_attribute_fact");
        return false;
    }

    private static void Count(Dictionary<string, ulong>? metrics, string name)
    { if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1; }
}
