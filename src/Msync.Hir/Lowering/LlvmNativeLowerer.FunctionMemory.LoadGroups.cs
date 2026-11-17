using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private sealed class FunctionMemoryLoadGroup(FunctionMemoryLocation location,
            LlvmLoadInstruction[] loads, LlvmInsertValueInstruction[] inserts)
        {
            internal readonly FunctionMemoryLocation Location = location;
            internal readonly LlvmLoadInstruction[] Loads = loads;
            internal readonly LlvmInsertValueInstruction[] Inserts = inserts;
            internal readonly int[] Slots = [-1, -1, -1];
            internal readonly bool[] NeededLeaves = FunctionFieldsUsed(inserts[2]);
            internal int Whole = -1;
        }

        private Dictionary<LlvmLoadInstruction, FunctionMemoryLoadGroup>? functionMemoryLoadGroups;
        private Dictionary<LlvmInsertValueInstruction, FunctionMemoryLoadGroup>? functionMemoryLoadInserts;
        private int functionMemoryLoadGroupCandidates, functionMemoryLoadGroupsEmitted;
        private int functionMemoryGroupComponentsOmitted, functionMemoryReassembliesReused;

        private bool TryFunctionMemoryLoadGroup(LlvmLoadInstruction load, FunctionMemoryLocation location, int component)
        {
            FunctionMemoryLoadGroup? group = null;
            functionMemoryLoadGroups?.TryGetValue(load, out group);
            if (group is null)
            {
                if (component != 0 || !load.IsSimple) return false;
                ++functionMemoryLoadGroupCandidates;
                group = CollectFunctionMemoryLoadGroup(load, location);
                if (group is null) return false;
                functionMemoryLoadGroups ??= [];
                functionMemoryLoadInserts ??= [];
                foreach (var member in group.Loads) functionMemoryLoadGroups.Add(member, group);
                foreach (var insert in group.Inserts) functionMemoryLoadInserts.Add(insert, group);
            }
            if (component == 0)
            {
                group.Whole = Register(group.Location.Type);
                Add(new(NativeOp.Load, group.Whole, FunctionMemoryAddress(group.Location), Type: group.Location.Type, Source: load.Source));
                ++functionComponentLoads;
                ++functionMemoryLoadGroupsEmitted;
            }
            if (!group.NeededLeaves[component]
                && !load.Uses.Any(use => !ReferenceEquals(use.User, group.Inserts[component]) && RetainedCaptureUse(use)))
            {
                ++functionMemoryGroupComponentsOmitted;
                return true;
            }
            int type = FunctionMemoryProjectionType(load, component);
            SetSelectedValueType(load, type);
            group.Slots[component] = values[load];
            Add(new(NativeOp.FunctionComponent, values[load], group.Whole, Immediate: component, Source: load.Source));
            RecordFunctionProjection(type, component);
            return true;
        }

        private bool TryFunctionMemoryReassembly(LlvmInsertValueInstruction insert)
        {
            if (functionMemoryLoadInserts is null || !functionMemoryLoadInserts.TryGetValue(insert, out var group)) return false;
            var shape = AggregateShapeFor(insert.Type);
            bool final = ReferenceEquals(insert, group.Inserts[2]);
            aggregateSlots.Add(insert, new(shape, group.Slots.ToArray(), final ? group.Whole : -1));
            if (final) ++functionMemoryReassembliesReused;
            return true;
        }

        private FunctionMemoryLoadGroup? CollectFunctionMemoryLoadGroup(LlvmLoadInstruction first,
            FunctionMemoryLocation location)
        {
            LlvmLoadInstruction[] loads = new LlvmLoadInstruction[3];
            LlvmInsertValueInstruction[] inserts = new LlvmInsertValueInstruction[3];
            int loaded = 0, assembled = 0;
            for (LlvmInstruction? instruction = first; instruction is not null; instruction = instruction.Next)
            {
                if (instruction is LlvmGetElementPtrInstruction or LlvmCastInstruction) continue;
                if (instruction is LlvmLoadInstruction { IsSimple: true } member)
                {
                    if (loaded != assembled || loaded == 3 ||
                        !FunctionMemoryComponent(member.GetOperandUse(0), member.Type, out var target, out int component) ||
                        target != location || component != loaded)
                    {
                        trace?.Metric("functions.memory_load_group_reject.member", 1);
                        return null;
                    }
                    loads[loaded++] = member;
                    continue;
                }
                if (instruction is not LlvmInsertValueInstruction insert || loaded != assembled + 1 ||
                    !IsFunctionAggregate(insert.Type) || insert.Indices.Count != 1 || insert.Indices[0] != assembled ||
                    !ReferenceEquals(insert.InsertedValueOperand, loads[assembled]))
                {
                    trace?.Metric("functions.memory_load_group_reject.reassembly", 1);
                    return null;
                }
                if (assembled == 0 ? insert.AggregateOperand is not LlvmUndefValue :
                    !ReferenceEquals(insert.AggregateOperand, inserts[assembled - 1]))
                {
                    trace?.Metric("functions.memory_load_group_reject.chain", 1);
                    return null;
                }
                inserts[assembled++] = insert;
                if (assembled != 3) continue;
                if (!inserts[0].HasOneUse || !inserts[1].HasOneUse)
                {
                    trace?.Metric("functions.memory_load_group_reject.intermediate_use", 1);
                    return null;
                }
                return new(location, loads, inserts);
            }
            trace?.Metric("functions.memory_load_group_reject.block_end", 1);
            return null;
        }
    }
}
