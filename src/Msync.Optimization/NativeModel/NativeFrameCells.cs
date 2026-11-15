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
        bool frameEscaped = HasEscapingFrameAddress(function, addresses, escape);
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            if (instruction.Op != NativeOp.BindLocation) continue;
            NativeAddressAnalysis.Points source = addresses.At(pc, instruction.B);
            if (source.Unknown && source.MayAliasFrameSlot(null, function, frameEscaped)) return;
            foreach (NativeAddressAnalysis.Target target in source.Targets)
                if (target.Allocation < 0) aliased.Add(target.Slot);
        }
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            if (instruction.Op is not (NativeOp.AddressOffset or NativeOp.ProjectAddress or NativeOp.IndexAddress
                or NativeOp.CopyMemory or NativeOp.ClearMemory or NativeOp.RangeFromMemory)) continue;
            if (ReachesFrame(addresses.At(pc, instruction.A), frameEscaped)) return;
            if (instruction.Op == NativeOp.CopyMemory && ReachesFrame(addresses.At(pc, instruction.B), frameEscaped)) return;
        }
        known = true;
    }

    internal bool Known => known;

    internal bool IsPrivate(int slot) => known && !bound.Contains(slot) && !aliased.Contains(slot)
        && function.Slots[slot].Role != NativeSlotRole.Capture && !function.Slots[slot].Escapes
        && !escape.EscapingLocations.Contains(slot)
        && (!program.PreserveLocals || function.Slots[slot].Role == NativeSlotRole.Temporary);

    private bool ReachesFrame(NativeAddressAnalysis.Points points, bool frameEscaped)
        => points.MayAliasFrameSlot(bound, function, frameEscaped);

    private static bool HasEscapingFrameAddress(NativeFunctionCode function, NativeAddressAnalysis addresses, NativeEscapeAnalysis escape)
    {
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            if (instruction.Op == NativeOp.FrameAddress)
            {
                int slot = instruction.A;
                if (function.Slots[slot].Escapes || escape.EscapingLocations.Contains(slot)
                    || addresses.At(pc, instruction.Result).Top)
                    return true;
            }
            else if (instruction.Op == NativeOp.BindLocation
                && (function.Slots[instruction.A].Escapes || escape.EscapingLocations.Contains(instruction.A))
                && addresses.At(pc, instruction.B).MayAliasCurrentFrame)
                return true;
        }
        return false;
    }
}
