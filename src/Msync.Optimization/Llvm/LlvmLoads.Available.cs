// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/Loads.cpp,
// getAvailableLoadStore and FindAvailableLoadedValue (deferred BatchAA queries).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public readonly record struct LlvmLoadStoreProperties(LlvmAtomicOrdering Ordering, bool IsElementwise);

public static partial class LlvmLoads
{
    public static LlvmValue? FindAvailableLoadedValue(LlvmLoadInstruction load, LlvmBatchAAResults alias,
        out bool isLoadCse, Dictionary<string, ulong>? metrics = null,
        uint maxInstructionsToScan = DefaultMaxInstructionsToScan)
    {
        isLoadCse = false;
        Count(metrics, "loads.available.queries");
        if (!load.IsUnordered) return null;
        var layout = load.Parent!.Module!.DataLayout;
        var pointer = LlvmValueTracking.StripPointerCasts(load.PointerOperand, metrics);
        var properties = new LlvmLoadStoreProperties(load.Ordering, load.IsElementwise);
        List<LlvmInstruction>? mustNotAlias = null;
        LlvmValue? available = null;
        for (var instruction = load.Previous; instruction is not null; instruction = instruction.Previous)
        {
            Count(metrics, "loads.available.instructions_scanned");
            if (maxInstructionsToScan-- == 0)
            { Count(metrics, "loads.available.scan_limit"); return null; }
            available = GetAvailableLoadStore(instruction, pointer, load.Type, properties, layout, out isLoadCse, metrics);
            if (available is not null) break;
            if (instruction.MayWriteToMemory()) (mustNotAlias ??= []).Add(instruction);
        }
        if (available is null) return null;
        if (mustNotAlias is not null)
        {
            var location = LlvmMemoryLocation.Get(load, layout);
            foreach (var instruction in mustNotAlias)
            {
                Count(metrics, "loads.available.alias_queries");
                if ((alias.GetModRefInfo(instruction, location) & LlvmModRefInfo.Mod) != 0)
                { Count(metrics, "loads.available.clobber"); return null; }
            }
        }
        Count(metrics, isLoadCse ? "loads.available.load_cse" : "loads.available.forwarded");
        return available;
    }

    private static bool HasCompatibleAtomicAccessSize(LlvmType type, LlvmLoadStoreProperties properties,
        LlvmType otherType, LlvmLoadStoreProperties otherProperties, LlvmDataLayout layout) =>
        properties.Ordering == LlvmAtomicOrdering.NotAtomic || layout.GetTypeStoreSize(
            properties.IsElementwise ? type.GetScalarType() : type) == layout.GetTypeStoreSize(
            otherProperties.IsElementwise ? otherType.GetScalarType() : otherType);

    private static LlvmValue? GetAvailableLoadStore(LlvmInstruction instruction, LlvmValue pointer,
        LlvmType accessType, LlvmLoadStoreProperties properties, LlvmDataLayout layout,
        out bool isLoadCse, Dictionary<string, ulong>? metrics)
    {
        isLoadCse = false;
        bool atomic = properties.Ordering != LlvmAtomicOrdering.NotAtomic;
        if (instruction is LlvmLoadInstruction load)
        {
            if (!load.IsAtomic && atomic || !AreEquivalentAddressValues(
                LlvmValueTracking.StripPointerCasts(load.PointerOperand, metrics), pointer)) return null;
            if (HasCompatibleAtomicAccessSize(accessType, properties, load.Type,
                new(load.Ordering, load.IsElementwise), layout) &&
                LlvmCastInstruction.IsBitOrNoopPointerCastable(load.Type, accessType, layout))
            { isLoadCse = true; return load; }
        }
        if (instruction is LlvmStoreInstruction store)
        {
            if (!store.IsAtomic && atomic || !AreEquivalentAddressValues(
                LlvmValueTracking.StripPointerCasts(store.PointerOperand, metrics), pointer)) return null;
            var value = store.ValueOperand;
            if (!HasCompatibleAtomicAccessSize(accessType, properties, value.Type,
                new(store.Ordering, store.IsElementwise), layout)) return null;
            if (LlvmCastInstruction.IsBitOrNoopPointerCastable(value.Type, accessType, layout)) return value;
            if (LlvmTypeSize.IsKnownLE(layout.GetTypeSizeInBits(accessType), layout.GetTypeSizeInBits(value.Type)) &&
                value is LlvmConstant constant)
                return LlvmConstantFolding.ConstantFoldLoadFromConst(constant, accessType, layout, metrics);
        }
        if (!LlvmMemIntrinsic.TryGet(instruction, out var memory) || !memory.IsMemSet || atomic ||
            memory.Value is not LlvmConstantInt byteValue || memory.Length is not LlvmConstantInt length) return null;
        var storeOffset = LlvmAPInt.GetZero(layout.GetIndexTypeSizeInBits(memory.RawDest.Type));
        var loadOffset = LlvmAPInt.GetZero(layout.GetIndexTypeSizeInBits(pointer.Type));
        var storeBase = LlvmValueTracking.StripAndAccumulateConstantOffsets(memory.RawDest, layout, ref storeOffset, metrics: metrics);
        var loadBase = LlvmValueTracking.StripAndAccumulateConstantOffsets(pointer, layout, ref loadOffset, metrics: metrics);
        long signedStoreOffset = storeOffset.SExtOrTrunc(64).GetSExtValue();
        long signedLoadOffset = loadOffset.SExtOrTrunc(64).GetSExtValue();
        if (storeBase != loadBase || signedLoadOffset < signedStoreOffset) return null;
        var loadTypeSize = layout.GetTypeSizeInBits(accessType);
        if (loadTypeSize.IsScalable) return null;
        ulong loadSize = loadTypeSize.GetFixedValue();
        var writtenBits = length.Value * new LlvmAPInt(length.Value.BitWidth, 8);
        ulong requiredBits = unchecked(loadSize + (ulong)(signedLoadOffset - signedStoreOffset) * 8);
        if (writtenBits.BitWidth <= 64 ? writtenBits.GetZExtValue() < requiredBits :
            writtenBits.Ult(new LlvmAPInt(writtenBits.BitWidth, requiredBits))) return null;
        uint width = (uint)loadSize;
        var splat = byteValue.Value.ZExtOrTrunc(width);
        for (uint offset = 8; offset < width; offset += 8) splat |= byteValue.Value.ZExtOrTrunc(width).Shl(offset);
        var splatConstant = LlvmConstantInt.Get(accessType.Context, splat);
        return LlvmCastInstruction.IsBitOrNoopPointerCastable(splatConstant.Type, accessType, layout) ? splatConstant : null;
    }
}
