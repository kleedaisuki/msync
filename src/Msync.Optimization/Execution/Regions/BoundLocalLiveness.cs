namespace Msync.Optimization.Execution;

// Used to omit writes only when runtime admission proves the destination
// is a private non-Cell slot. Storage/capture access counts as a read.
internal static class BoundLocalLiveness
{
    internal static bool ReadsBeforeWrite(ReadOnlySpan<Instruction> code, int start, int slot)
    {
        var visited = new bool[code.Length];
        var pending = new Stack<int>();
        pending.Push(start);
        while (pending.TryPop(out int pc))
        {
            while ((uint)pc < (uint)code.Length && !visited[pc])
            {
                visited[pc] = true;
                var instruction = code[pc];
                if (instruction.A == slot)
                {
                    if (instruction.Op is OpCode.LoadLocal or OpCode.LoadStorage or OpCode.LoadCell or OpCode.IteratorNext)
                        return true;
                    if (instruction.Op is OpCode.StoreLocal or OpCode.InitializeLocal) break;
                }
                if (instruction.Op == OpCode.Return) break;
                if (instruction.Op == OpCode.Jump)
                {
                    pc = instruction.A;
                    continue;
                }
                if (instruction.Op is OpCode.JumpIfTrue or OpCode.JumpIfFalse or OpCode.JumpIfStaticReady) pending.Push(instruction.A);
                if (instruction.Op == OpCode.IteratorNext) pending.Push(instruction.B);
                pc++;
            }
        }
        return false;
    }
}
