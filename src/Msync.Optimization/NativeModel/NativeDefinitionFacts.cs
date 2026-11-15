using Msync.Core.Native;

namespace Msync.Optimization;

internal static class NativeDefinitionFacts
{
    internal static bool[] SingleAssignmentTemporaries(NativeFunctionCode function)
    {
        int[] writes = new int[function.Slots.Count];
        foreach (NativeInstruction instruction in function.Instructions)
        {
            if (instruction.Op == NativeOp.Drop) continue;
            foreach (int slot in NativeInstructionFacts.Definitions(instruction))
                if (instruction.Op != NativeOp.Move || slot == instruction.Result) writes[slot]++;
        }
        return function.Slots.Select((slot, index) =>
            slot.Role == NativeSlotRole.Temporary && !slot.Escapes && writes[index] == 1).ToArray();
    }
}
