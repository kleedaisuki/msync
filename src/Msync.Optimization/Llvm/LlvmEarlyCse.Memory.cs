// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: EarlyCSE.cpp,
// ParseMemoryInst, getMatchingValue, overridingStores and generation tracking.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmEarlyCse
{
    private readonly record struct LoadValue(LlvmInstruction? Definition, uint Generation, int MatchingId, bool Atomic, bool IsLoad);

    private sealed partial class Work
    {
        private void ProcessLoad(LlvmLoadInstruction load, ref LlvmInstruction? lastStore)
        {
            if (load.IsVolatile || !load.IsUnordered)
            { lastStore = null; AdvanceGeneration("memory.kill.ordered_load"); }
            if (load.HasInvariantLoadMetadata)
                RecordInvariant(LlvmMemoryLocation.Get(load, Layout), "invariant.load.scope_inserted");
            Count("load.lookup");
            var available = loads.Lookup(load.PointerOperand);
            if (MatchingValue(available, new(load)) is { } replacement)
            {
                if (available.IsLoad && replacement is LlvmInstruction kept)
                    LlvmLocal.CombineMetadataForCse(kept, load, doesMove: false, Metrics);
                if (!load.UseEmpty) load.ReplaceAllUsesWith(replacement);
                Erase(load, "load-cse"); return;
            }
            loads.Insert(load.PointerOperand, new(load, generation, -1, load.IsAtomic, true));
            Count("load.insert"); lastStore = null;
        }

        private LlvmValue? MatchingValue(LoadValue available, MemoryInst memory)
        {
            if (available.Definition is not { } definition) { Count("load.miss.unavailable"); return null; }
            if (LlvmMemIntrinsic.TryGet(definition, out var memset) && memset.IsMemSet)
                return MatchingMemSet(available, memory, memset);
            if (available.MatchingId != memory.MatchingId) { Count("load.miss.matching_id"); return null; }
            if (memory.IsVolatile || !memory.IsUnordered) { Count("load.miss.ordered_or_volatile"); return null; }
            if (memory.IsLoad && memory.IsAtomic && !available.Atomic) { Count("load.miss.atomicity"); return null; }
            LlvmInstruction matching = memory.IsLoad ? definition : memory.Instruction;
            LlvmInstruction other = memory.IsLoad ? memory.Instruction : definition;
            LlvmValue? result = memory.IsStore ? GetResult(matching, other.Type) : null;
            if (memory.IsStore && definition != result) { Count("load.miss.writeback_value"); return null; }
            if (!IsOperatingOnInvariantMemoryAt(memory.Instruction, available.Generation) &&
                !IsSameMemoryGeneration(available.Generation, generation, definition, memory.Instruction))
            { Count("load.miss.generation"); return null; }
            return result ?? GetResult(matching, other.Type);
        }

        private LlvmValue? MatchingMemSet(LoadValue available, MemoryInst memory, LlvmMemIntrinsic memset)
        {
            Count("memset.forwarding_queries");
            if (!memory.IsLoad || memory.IsVolatile || !memory.IsUnordered || memory.MatchingId != -1)
            { Count("memset.miss.not_unordered_regular_load"); return null; }
            if (memset.IsVolatile) { Count("memset.miss.volatile"); return null; }
            if (memset.Value is not LlvmConstantInt { IsZero: true })
            { Count("memset.miss.nonzero_or_variable_byte"); return null; }
            if (memset.LengthInBytes is not { } length) { Count("memset.miss.variable_length"); return null; }
            if (memory.ValueType is not { } type) { Count("memset.miss.no_access_type"); return null; }
            var size = Layout.GetTypeStoreSize(type);
            if (size.IsScalable || length.GetLimitedValue() < size.GetFixedValue())
            { Count("memset.miss.insufficient_fixed_length"); return null; }
            if (!IsOperatingOnInvariantMemoryAt(memory.Instruction, available.Generation) &&
                !IsSameMemoryGeneration(available.Generation, generation, available.Definition!, memory.Instruction))
            { Count("memset.miss.generation"); return null; }
            Count("memset.zero_forwarded");
            return LlvmConstant.GetNullValue(type);
        }

        private static LlvmValue? GetResult(LlvmInstruction instruction, LlvmType expected)
        {
            LlvmValue value = instruction is LlvmLoadInstruction ? instruction : ((LlvmStoreInstruction)instruction).ValueOperand;
            return value.Type == expected ? value : null;
        }

        private bool ProcessWriteback(MemoryInst memory)
        {
            var available = loads.Lookup(memory.PointerOperand!);
            if (available.Definition is null || available.Definition != MatchingValue(available, memory)) return false;
            Erase(memory.Instruction, "writeback-dse"); return true;
        }

        private void ProcessStore(MemoryInst memory, ref LlvmInstruction? lastStore)
        {
            if (lastStore is not null && OverridingStores(new(lastStore, Metrics), memory))
            { Erase(lastStore, "overwritten-store"); lastStore = null; }
            loads.Insert(memory.PointerOperand!, new(memory.Instruction, generation, memory.MatchingId, memory.IsAtomic, memory.IsLoad));
            Count(memory.IsMemSet ? "memset.insert" : "store.insert");
            lastStore = memory.IsUnordered && !memory.IsVolatile ? memory.Instruction : null;
        }
    }
}
