using Msync.Core.Native;

namespace Msync.Optimization;

internal static class NativeDeadMemory
{
    internal static bool Run(NativeProgram program, int index, NativeInstruction[] code, NativeEscapeAnalysis escape,
        NativeOptimizationProfile? profile = null)
    {
        NativeFunctionCode function = program.Functions[index];
        NativeAddressAnalysis addresses = escape.Addresses;
        NativeFrameCells cells = new(program, function, escape, addresses);
        profile?.Trace.Metric("dead_memory.frame_cells_known", cells.Known ? 1 : 0);
        HashSet<NativeAddressAnalysis.Target> read = Reads(function, code, addresses, escape, program);
        bool changed = false;
        int stores = 0, bindings = 0;
        for (int pc = 0; pc < code.Length; pc++)
        {
            NativeInstruction instruction = code[pc];
            if (instruction.Op == NativeOp.Store && cells.IsPrivate(instruction.B)
                && DeadStore(instruction, addresses, read, escape, function))
            { code[pc] = NativeLocalOptimization.Nop(instruction); changed = true; stores++; }
            if (instruction.Op == NativeOp.BindLocation && DeadBinding(instruction, addresses, read, escape, function))
            { code[pc] = NativeLocalOptimization.Nop(instruction); changed = true; bindings++; }
        }
        if (profile is not null)
        {
            profile.Trace.Metric("dead_memory.eliminated_stores", stores);
            profile.Trace.Metric("dead_memory.eliminated_bindings", bindings);
        }
        return changed;
    }

    private static HashSet<NativeAddressAnalysis.Target> Reads(NativeFunctionCode function,
        NativeInstruction[] code, NativeAddressAnalysis addresses, NativeEscapeAnalysis escape, NativeProgram program)
    {
        HashSet<NativeAddressAnalysis.Target> read = [];
        foreach (NativeInstruction instruction in code)
        {
            if (instruction.Op == NativeOp.Load) read.UnionWith(addresses.MemoryTargets(instruction));
            foreach (int source in NativeInstructionFacts.ValueReads(instruction))
            {
                read.Add(new(-1, source));
                read.UnionWith(addresses.Binding(source).Targets);
            }
            if (DoesNotReadFields(instruction.Op)) continue;
            foreach (int source in NativeInstructionFacts.ValueReads(instruction))
                foreach (int allocation in escape.AllocationsOf(source))
                    AddFields(read, program, function, allocation);
        }
        return read;
    }

    private static bool DoesNotReadFields(NativeOp op) => op is NativeOp.Nop or NativeOp.Constant
        or NativeOp.Copy or NativeOp.Move or NativeOp.FrameAddress or NativeOp.BindLocation
        or NativeOp.Allocate or NativeOp.AddressOffset or NativeOp.ProjectAddress or NativeOp.Load
        or NativeOp.Store or NativeOp.Jump or NativeOp.Branch or NativeOp.Narrow
        or NativeOp.MakeFunction or NativeOp.MakeRange or NativeOp.MakeLazyRange or NativeOp.DeferRange;

    private static void AddFields(HashSet<NativeAddressAnalysis.Target> read, NativeProgram program,
        NativeFunctionCode function, int allocation)
    {
        NativeCodeLayout layout = program.Layouts[function.Instructions[allocation].A];
        read.Add(new(allocation, -1));
        for (int field = 0; field < layout.Fields.Count; field++) read.Add(new(allocation, -1, field));
    }

    private static bool DeadStore(NativeInstruction instruction, NativeAddressAnalysis addresses,
        HashSet<NativeAddressAnalysis.Target> read, NativeEscapeAnalysis escape, NativeFunctionCode function)
    {
        if (addresses[instruction.A].Unknown) return false;
        NativeAddressAnalysis.Target[] targets = addresses.MemoryTargets(instruction).ToArray();
        return targets.Length != 0 && targets.All(target => Private(target, escape, function) && !read.Contains(target));
    }

    private static bool DeadBinding(NativeInstruction instruction, NativeAddressAnalysis addresses,
        HashSet<NativeAddressAnalysis.Target> read, NativeEscapeAnalysis escape, NativeFunctionCode function)
    {
        int slot = instruction.A;
        if (escape.EscapingLocations.Contains(slot) || function.Slots[slot].Escapes
            || function.Slots[slot].Role == NativeSlotRole.Capture || addresses[instruction.B].Unknown) return false;
        NativeAddressAnalysis.Target[] targets = addresses[instruction.B].Targets.ToArray();
        return targets.Length != 0 && targets.All(target => Private(target, escape, function) && !read.Contains(target));
    }

    private static bool Private(NativeAddressAnalysis.Target target, NativeEscapeAnalysis escape,
        NativeFunctionCode function) => target.Allocation >= 0
        ? !escape.EscapingAllocations.Contains(target.Allocation)
        : !escape.EscapingLocations.Contains(target.Slot) && !function.Slots[target.Slot].Escapes
            && function.Slots[target.Slot].Role != NativeSlotRole.Capture;
}
