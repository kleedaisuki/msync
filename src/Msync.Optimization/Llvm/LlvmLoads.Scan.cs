// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/Loads.cpp,
// findAvailablePtrLoadStore and areNonOverlapSameBaseLoadAndStore.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLoads
{
    public static LlvmValue? FindAvailableLoadedValue(LlvmLoadInstruction load, LlvmBasicBlock scanBlock,
        ref LlvmInstruction? scanFrom, uint maxInstructionsToScan, LlvmBatchAAResults? alias,
        out bool isLoadCse, out uint instructionsScanned, Dictionary<string, ulong>? metrics = null)
    {
        isLoadCse = false;
        instructionsScanned = 0;
        if (!load.IsUnordered) return null;
        return FindAvailablePtrLoadStore(LlvmMemoryLocation.Get(load), load.Type,
            new(load.Ordering, load.IsElementwise), scanBlock, ref scanFrom, maxInstructionsToScan,
            alias, out isLoadCse, out instructionsScanned, metrics);
    }

    // scanFrom is the next instruction to execute (null is the block end), matching
    // the source iterator's position, including its restoration at clobbers/limits.
    public static LlvmValue? FindAvailablePtrLoadStore(LlvmMemoryLocation location, LlvmType accessType,
        LlvmLoadStoreProperties properties, LlvmBasicBlock scanBlock, ref LlvmInstruction? scanFrom,
        uint maxInstructionsToScan, LlvmBatchAAResults? alias, out bool isLoadCse,
        out uint instructionsScanned, Dictionary<string, ulong>? metrics = null)
    {
        isLoadCse = false;
        instructionsScanned = 0;
        if (maxInstructionsToScan == 0) maxInstructionsToScan = uint.MaxValue;
        var layout = scanBlock.Module!.DataLayout;
        var pointer = LlvmValueTracking.StripPointerCasts(location.Ptr!, metrics);
        while ((scanFrom is null ? scanBlock.LastInstruction : scanFrom.Previous) is { } instruction)
        {
            ++instructionsScanned;
            Count(metrics, "loads.scan.instructions_scanned");
            if (maxInstructionsToScan-- == 0)
            { Count(metrics, "loads.scan.scan_limit"); return null; }
            scanFrom = instruction;
            if (GetAvailableLoadStore(instruction, pointer, accessType, properties, layout,
                out isLoadCse, metrics) is { } available)
            {
                Count(metrics, isLoadCse ? "loads.scan.load_cse" : "loads.scan.forwarded");
                return available;
            }
            if (instruction is LlvmStoreInstruction store)
            {
                var storePointer = LlvmValueTracking.StripPointerCasts(store.PointerOperand, metrics);
                if (pointer is LlvmAllocaInstruction or LlvmGlobalVariable &&
                    storePointer is LlvmAllocaInstruction or LlvmGlobalVariable && pointer != storePointer) continue;
                if (alias is null)
                {
                    if (AreNonOverlapSameBaseLoadAndStore(location.Ptr!, accessType,
                        store.PointerOperand, store.ValueOperand.Type, layout, metrics)) continue;
                }
                else if ((alias.GetModRefInfo(store, location) & LlvmModRefInfo.Mod) == 0) continue;
                scanFrom = instruction.Next;
                Count(metrics, "loads.scan.store_clobber");
                return null;
            }
            if (instruction.MayWriteToMemory())
            {
                if (alias is not null && (alias.GetModRefInfo(instruction, location) & LlvmModRefInfo.Mod) == 0) continue;
                scanFrom = instruction.Next;
                Count(metrics, "loads.scan.memory_clobber");
                return null;
            }
        }
        return null;
    }

    private static bool AreNonOverlapSameBaseLoadAndStore(LlvmValue loadPointer, LlvmType loadType,
        LlvmValue storePointer, LlvmType storeType, LlvmDataLayout layout, Dictionary<string, ulong>? metrics)
    {
        uint width = layout.GetIndexTypeSizeInBits(loadPointer.Type);
        if (width != layout.GetIndexTypeSizeInBits(storePointer.Type)) return false;
        var loadOffset = LlvmAPInt.GetZero(width);
        var storeOffset = LlvmAPInt.GetZero(width);
        var loadBase = LlvmValueTracking.StripAndAccumulateConstantOffsets(loadPointer, layout, ref loadOffset,
            allowNonInbounds: false, metrics: metrics);
        var storeBase = LlvmValueTracking.StripAndAccumulateConstantOffsets(storePointer, layout, ref storeOffset,
            allowNonInbounds: false, metrics: metrics);
        if (loadBase != storeBase) return false;
        ulong loadSize = LlvmLocationSize.Precise(layout.GetTypeStoreSize(loadType)).ToRaw();
        ulong storeSize = LlvmLocationSize.Precise(layout.GetTypeStoreSize(storeType)).ToRaw();
        var loadRange = new LlvmConstantRange(loadOffset, loadOffset + new LlvmAPInt(width, loadSize));
        var storeRange = new LlvmConstantRange(storeOffset, storeOffset + new LlvmAPInt(width, storeSize));
        return loadRange.IntersectWith(storeRange).IsEmptySet;
    }
}
