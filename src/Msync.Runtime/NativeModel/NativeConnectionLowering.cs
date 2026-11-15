using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static class NativeConnectionLowering
{
    internal static NativeBackendFunction Link(NativeProgram program, int index, NativeBackendFunction backend,
        Dictionary<NativeConnection, int> connections)
    {
        NativeFunctionCode function = program.Functions[index];
        NativeBackendInstruction[] code = backend.Instructions.ToArray();
        if (!code.Any(instruction => instruction.Op == NativeOp.Binary && (NativeBinary)instruction.Immediate == NativeBinary.Qualify)) return backend;
        NativeControlFlow flow = new(function.Instructions);
        List<int>[] predecessors = Enumerable.Range(0, flow.Blocks.Count).Select(_ => new List<int>()).ToArray();
        for (int block = 0; block < flow.Blocks.Count; block++)
            foreach (int successor in flow.Blocks[block].Successors) predecessors[successor].Add(block);
        int[]?[] exits = new int[flow.Blocks.Count][];
        int[]?[] entries = new int[flow.Blocks.Count][];
        bool changed;
        do
        {
            changed = false;
            for (int block = 0; block < flow.Blocks.Count; block++)
            {
                int[]? state = block == 0 ? Enumerable.Repeat(-1, function.Slots.Count).ToArray() : null;
                foreach (int predecessor in predecessors[block])
                {
                    if (exits[predecessor] is not { } input) continue;
                    if (state is null) state = (int[])input.Clone();
                    else for (int slot = 0; slot < state.Length; slot++) if (state[slot] != input[slot]) state[slot] = -1;
                }
                if (state is null || entries[block]?.SequenceEqual(state) == true) continue;
                entries[block] = (int[])state.Clone();
                for (int pc = flow.Blocks[block].Start; pc < flow.Blocks[block].End; pc += code[pc].Span)
                    Transfer(code[pc], state, program, connections);
                if (exits[block]?.SequenceEqual(state) != true) changed = true;
                exits[block] = state;
            }
        } while (changed);
        for (int block = 0; block < flow.Blocks.Count; block++)
        {
            if (entries[block] is not { } state) continue;
            for (int pc = flow.Blocks[block].Start; pc < flow.Blocks[block].End; pc += code[pc].Span)
            {
                NativeBackendInstruction instruction = code[pc];
                if (instruction.Op == NativeOp.Binary && (NativeBinary)instruction.Immediate == NativeBinary.Qualify && state[instruction.A] >= 0)
                    code[pc] = instruction with { Handler = NativeBackendHandler.ConnectionQualify, Connection = state[instruction.A] };
                Transfer(instruction, state, program, connections);
            }
        }
        return backend with { Instructions = code };
    }

    private static void Transfer(NativeBackendInstruction code, int[] state, NativeProgram program, Dictionary<NativeConnection, int> connections)
    {
        if (code.Handler == NativeBackendHandler.Nop) return;
        int result = -1;
        if (code.Op == NativeOp.Constant && program.Constants[code.A] is { Kind: NativeKind.Connection } value)
            result = connections[new(value.ConnectionKind, value.Text ?? "")];
        else if (code.Op is NativeOp.Copy or NativeOp.Move or NativeOp.Narrow || code.Handler == NativeBackendHandler.FrameStore)
            result = state[code.A];
        if (code.Op == NativeOp.Store || NativeInstructionFacts.MemoryBarrier(new(code.Op)))
            Array.Fill(state, -1);
        if (code.Op is NativeOp.Move or NativeOp.Drop or NativeOp.BindLocation) state[code.A] = -1;
        if (code.Result >= 0) state[code.Result] = result;
    }
}
