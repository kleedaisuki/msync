// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/Loads.cpp,
// AreEquivalentAddressValues and isSafeToLoadUnconditionally.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLoads
{
    public const uint DefaultMaxInstructionsToScan = 6;

    public static bool MustSuppressSpeculation(LlvmLoadInstruction load) =>
        !load.IsUnordered || SuppressSpeculativeLoadForSanitizers(load);

    private static bool SuppressSpeculativeLoadForSanitizers(LlvmInstruction context) =>
        context.Parent?.Parent is { } function && (function.SanitizeThread ||
            function.SanitizeConcurrency || function.SanitizeAddress || function.SanitizeHWAddress);

    internal static bool IsSafeToLoadUnconditionally(LlvmValue pointer, LlvmType type,
        LlvmAlign alignment, LlvmInstructionSimplify.BinaryQuery query, bool ignoreFree = false)
    {
        var size = query.Layout.GetTypeStoreSize(type);
        if (size.IsScalable) return false;
        return IsSafeToLoadUnconditionally(pointer, alignment,
            new LlvmAPInt(query.Layout.GetIndexTypeSizeInBits(pointer.Type), size.GetFixedValue()), query, ignoreFree);
    }

    public static bool IsSafeToLoadUnconditionally(LlvmValue pointer, LlvmType type,
        LlvmAlign alignment, LlvmInstruction context, Dictionary<string, ulong>? metrics = null,
        bool ignoreFree = false) => IsSafeToLoadUnconditionally(pointer, type, alignment,
            new LlvmInstructionSimplify.BinaryQuery(context.Parent!.Module!.DataLayout, context, metrics), ignoreFree);

    public static bool IsSafeToLoadUnconditionally(LlvmValue pointer, LlvmAlign alignment,
        LlvmAPInt size, LlvmInstruction context, Dictionary<string, ulong>? metrics = null,
        bool ignoreFree = false) => IsSafeToLoadUnconditionally(pointer, alignment, size,
            new LlvmInstructionSimplify.BinaryQuery(context.Parent!.Module!.DataLayout, context, metrics), ignoreFree);

    internal static bool IsSafeToLoadUnconditionally(LlvmValue pointer, LlvmAlign alignment,
        LlvmAPInt size, LlvmInstructionSimplify.BinaryQuery query, bool ignoreFree = false)
    {
        Count(query.Metrics, "loads.unconditional.queries");
        if (LlvmValueTracking.IsDereferenceableAndAlignedPointer(pointer, alignment, size,
            query.Layout, query.Context, query.Tree, ignoreFree, query.Metrics, query.Assumptions,
            query.AllowEphemerals, query.DC) &&
            (query.Context is null || !SuppressSpeculativeLoadForSanitizers(query.Context)))
        {
            Count(query.Metrics, "loads.unconditional.dereferenceable");
            return true;
        }
        if (query.Context is null || size.BitWidth > 64) return false;
        var loadSize = LlvmTypeSize.Fixed(size.GetZExtValue());
        pointer = LlvmValueTracking.StripPointerCasts(pointer, query.Metrics);
        for (var instruction = query.Context.Previous; instruction is not null; instruction = instruction.Previous)
        {
            Count(query.Metrics, "loads.unconditional.instructions_scanned");
            if (!ignoreFree && instruction is LlvmCallInstruction && instruction.MayWriteToMemory() &&
                !instruction.IsLifetimeStartOrEnd)
            {
                Count(query.Metrics, "loads.unconditional.call_clobber");
                return false;
            }
            LlvmValue accessedPointer;
            LlvmType accessedType;
            LlvmAlign accessedAlignment;
            if (instruction is LlvmLoadInstruction { IsVolatile: false } load)
                (accessedPointer, accessedType, accessedAlignment) = (load.PointerOperand, load.Type, load.Alignment);
            else if (instruction is LlvmStoreInstruction { IsVolatile: false } store)
                (accessedPointer, accessedType, accessedAlignment) = (store.PointerOperand, store.ValueOperand.Type, store.Alignment);
            else continue;
            if (accessedAlignment < alignment) continue;
            if (LlvmTypeSize.IsKnownLE(loadSize, query.Layout.GetTypeStoreSize(accessedType)) &&
                (ReferenceEquals(accessedPointer, pointer) || AreEquivalentAddressValues(
                    LlvmValueTracking.StripPointerCasts(accessedPointer, query.Metrics), pointer)))
            {
                Count(query.Metrics, "loads.unconditional.prior_access");
                return true;
            }
        }
        Count(query.Metrics, "loads.unconditional.no_prior_access");
        return false;
    }

    private static bool AreEquivalentAddressValues(LlvmValue first, LlvmValue second) =>
        ReferenceEquals(first, second) || first is LlvmCastInstruction or LlvmPhiInstruction or LlvmGetElementPtrInstruction &&
        second is LlvmInstruction other && ((LlvmInstruction)first).IsIdenticalToWhenDefined(other);

    private static void Count(Dictionary<string, ulong>? metrics, string name)
    {
        if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1;
    }
}
