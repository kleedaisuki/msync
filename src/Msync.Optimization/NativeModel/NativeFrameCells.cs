using Msync.Core.Native;

namespace Msync.Optimization;

internal sealed class NativeFrameCells
{
    private readonly NativeProgram program;
    private readonly NativeFunctionCode function;
    private readonly NativeEscapeAnalysis escape;
    private readonly HashSet<int> bound;
    private readonly HashSet<int> aliased = [];
    private readonly bool known;

    internal NativeFrameCells(NativeProgram program, NativeFunctionCode function, NativeEscapeAnalysis escape,
        NativeAddressAnalysis? addresses = null)
    {
        this.program = program; this.function = function; this.escape = escape;
        bound = function.Instructions.Where(instruction => instruction.Op == NativeOp.BindLocation)
            .Select(instruction => instruction.A).ToHashSet();
        addresses ??= escape.Addresses;
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            if (instruction.Op != NativeOp.BindLocation) continue;
            NativeAddressAnalysis.Points source = addresses.At(pc, instruction.B);
            if (source.Unknown && source.MayAliasCurrentFrame) return;
            foreach (NativeAddressAnalysis.Target target in source.Targets)
                if (target.Allocation < 0) aliased.Add(target.Slot);
        }
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            if (instruction.Op is not (NativeOp.AddressOffset or NativeOp.ProjectAddress or NativeOp.IndexAddress
                or NativeOp.CopyMemory or NativeOp.ClearMemory or NativeOp.RangeFromMemory)) continue;
            if (ReachesFrame(addresses.At(pc, instruction.A))) return;
            if (instruction.Op == NativeOp.CopyMemory && ReachesFrame(addresses.At(pc, instruction.B))) return;
        }
        known = true;
    }

    internal bool IsPrivate(int slot) => known && !bound.Contains(slot) && !aliased.Contains(slot)
        && function.Slots[slot].Role != NativeSlotRole.Capture && !function.Slots[slot].Escapes
        && !escape.EscapingLocations.Contains(slot)
        && (!program.PreserveLocals || function.Slots[slot].Role == NativeSlotRole.Temporary);

    private bool ReachesFrame(NativeAddressAnalysis.Points points) => points.MayAliasCurrentFrame
        && (points.Unknown || points.Targets.Any(target => target.Allocation < 0 && !bound.Contains(target.Slot)
            && function.Slots[target.Slot].Role != NativeSlotRole.Capture));
}
