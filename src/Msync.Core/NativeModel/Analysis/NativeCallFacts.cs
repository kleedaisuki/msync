namespace Msync.Core.Native;

internal static class NativeCallFacts
{
    internal static bool[] ReadOnlyParameters(NativeFunctionCode function, NativeAddressAnalysis addresses)
    {
        bool[] readOnly = new bool[function.Slots.Count];
        bool[] addressed = new bool[function.Slots.Count];
        int[] uses = new int[function.Slots.Count];
        for (int index = 0; index < function.ParameterCount; index++) readOnly[function.CaptureCount + index] = true;
        foreach (NativeInstruction instruction in function.Instructions)
        {
            if (instruction.Op is NativeOp.FrameAddress or NativeOp.BindLocation) addressed[instruction.A] = true;
            if (instruction.Op != NativeOp.Drop)
                foreach (int slot in NativeInstructionFacts.ValueReads(instruction)) uses[slot]++;
            foreach (int slot in NativeInstructionFacts.Definitions(instruction)) readOnly[slot] = false;
            if (instruction.Op is NativeOp.BindLocation or NativeOp.Move or NativeOp.Drop) readOnly[instruction.A] = false;
        }
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            if (instruction.Op == NativeOp.FrameAddress && readOnly[instruction.A]
                && (addressed[instruction.Result] || uses[instruction.Result] != 1
                    || pc + 1 == function.Instructions.Count
                    || function.Instructions[pc + 1] is not { Op: NativeOp.Load } load || load.A != instruction.Result))
                readOnly[instruction.A] = false;
            if (WideFrameAccess(instruction, pc, addresses))
            {
                readOnly.AsSpan().Clear();
                break;
            }
        }
        return readOnly;
    }

    private static bool WideFrameAccess(NativeInstruction instruction, int pc, NativeAddressAnalysis addresses) =>
        instruction.Op is NativeOp.IndexAddress or NativeOp.CopyMemory or NativeOp.ClearMemory or NativeOp.RangeFromMemory
        && (addresses.At(pc, instruction.A).MayAliasCurrentFrame
            || instruction.Op == NativeOp.CopyMemory && addresses.At(pc, instruction.B).MayAliasCurrentFrame);

    internal static bool[] PrivateSlots(NativeProgram program, int index, NativeAddressAnalysis addresses)
    {
        NativeFunctionCode function = program.Functions[index];
        bool[] result = function.Slots.Select(slot => slot.Role == NativeSlotRole.Temporary && !slot.Escapes).ToArray();
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            if (instruction.Op is NativeOp.FrameAddress or NativeOp.BindLocation) result[instruction.A] = false;
            if (WideFrameAccess(instruction, pc, addresses))
            {
                result.AsSpan().Clear();
                return result;
            }
        }
        return result;
    }
}
