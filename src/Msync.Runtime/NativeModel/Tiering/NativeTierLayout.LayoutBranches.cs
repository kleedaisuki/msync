using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed partial class NativeTierLayout
{
    private static HashSet<int>? FindLayoutBranches(NativeExecutionFunction function, NativeFastOperation[] operations)
    {
        HashSet<int>? candidates = null;
        for (int pc = 0; pc + 1 < operations.Length; pc += function.Instructions[pc].Span)
        {
            if (operations[pc] != NativeFastOperation.GuestHasLayout) continue;
            NativeExecutionInstruction test = function.Instructions[pc];
            int next = pc + test.Span;
            if (next < operations.Length && operations[next] == NativeFastOperation.Branch
                && function.Instructions[next].A == test.Result)
                (candidates ??= []).Add(pc);
        }
        if (candidates is null) return null;

        int[] reads = new int[function.Definition.Slots.Count];
        HashSet<int> entries = [];
        foreach (NativeInstruction instruction in function.Metadata)
        {
            foreach (int slot in NativeInstructionFacts.Reads(instruction)) reads[slot]++;
            switch (instruction.Op)
            {
                case NativeOp.Jump:
                    entries.Add(instruction.A);
                    break;
                case NativeOp.Branch:
                    entries.Add(instruction.C);
                    goto case NativeOp.NextIterator;
                case NativeOp.NextIterator:
                    entries.Add(instruction.B);
                    break;
            }
        }
        candidates.RemoveWhere(pc =>
        {
            NativeExecutionInstruction test = function.Instructions[pc];
            return reads[test.Result] != 1 || entries.Contains(pc + test.Span);
        });
        return candidates.Count == 0 ? null : candidates;
    }
}
