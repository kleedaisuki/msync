// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/Local.cpp tryEnforceAlignment/getOrEnforceKnownAlignment (1545–1630).
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLocal
{
    public static LlvmAlign TryEnforceAlignment(LlvmValue value, LlvmAlign preferred,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        CountAlignment(metrics, "local.alignment.enforce.queries");
        value = LlvmValueTracking.StripPointerCasts(value, metrics);
        if (value is LlvmAllocaInstruction allocation)
        {
            LlvmAlign current = allocation.Alignment;
            if (preferred <= current) return current;
            if (layout.GetStackAlignment() is { } natural && preferred > natural)
            { CountAlignment(metrics, "local.alignment.enforce.stack_realign"); return current; }
            allocation.Alignment = preferred;
            CountAlignment(metrics, "local.alignment.enforce.alloca_changed");
            return preferred;
        }
        if (value is not LlvmGlobalVariable global)
        { CountAlignment(metrics, "local.alignment.enforce.not_storage"); return new(1); }
        LlvmAlign existing = LlvmValueTracking.GetPointerAlignment(global, layout, metrics);
        if (preferred <= existing) return existing;
        if (!global.CanIncreaseAlignment())
        { CountAlignment(metrics, "local.alignment.enforce.global_fixed"); return existing; }
        if (global.IsThreadLocal)
        {
            uint maximum = global.Parent!.GetMaxTLSAlignment() / 8;
            if (maximum != 0 && preferred > new LlvmAlign(maximum))
            { preferred = new(maximum); CountAlignment(metrics, "local.alignment.enforce.tls_clamped"); }
        }
        global.Alignment = preferred;
        CountAlignment(metrics, "local.alignment.enforce.global_changed");
        return preferred;
    }

    public static LlvmAlign GetOrEnforceKnownAlignment(LlvmValue value, LlvmAlign? preferred,
        LlvmDataLayout layout, LlvmInstruction? context = null, LlvmAssumptionCache? assumptions = null,
        LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(value.Type.IsPointerTy(), "getOrEnforceKnownAlignment expects a pointer!");
        CountAlignment(metrics, "local.alignment.known.queries");
        LlvmKnownBits known = LlvmKnownBitsAnalysis.Compute(value, context: context, tree: tree,
            metrics: metrics, assumptions: assumptions, layout: layout);
        uint zeros = Math.Min(known.CountMinTrailingZeros(), LlvmValue.MaxAlignmentExponent);
        LlvmAlign alignment = new(1UL << (int)Math.Min(known.BitWidth - 1, zeros));
        if (preferred is { } requested && requested > alignment)
            alignment = LlvmAlign.Max(alignment, TryEnforceAlignment(value, requested, layout, metrics));
        return alignment;
    }

    private static void CountAlignment(Dictionary<string, ulong>? metrics, string name)
    {
        if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1;
    }
}

public static partial class LlvmLocalCfg
{
    public static LlvmAlign TryEnforceAlignment(LlvmValue value, LlvmAlign preferred,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null) =>
        LlvmLocal.TryEnforceAlignment(value, preferred, layout, metrics);
}
