using Msync.Core;

namespace Msync.Runtime;

// This describes what KnownCalls can emit, not all effects a function may have.
internal sealed class CopyReachability(IReadOnlyList<FunctionCode> functions)
{
    private readonly HashSet<int> noCopies = [];

    internal bool MayEmitCopy(IReadOnlyList<Instruction> code, int start, int end, out bool sawCall)
    {
        Search search = new(functions, noCopies);
        bool direct = search.Follow(code, start, end);
        sawCall = search.SawCall;
        if (direct) return true;
        while (search.Pending is { } pending && pending.TryPop(out int target))
            if (search.Follow(functions[target].Instructions, 0, functions[target].Instructions.Count))
                return true;
        if (search.Visited is { } visited) noCopies.UnionWith(visited);
        return false;
    }

    private struct Search(IReadOnlyList<FunctionCode> functions, HashSet<int> noCopies)
    {
        internal HashSet<int>? Visited;
        internal Stack<int>? Pending;
        internal bool SawCall;

        internal bool Follow(IReadOnlyList<Instruction> code, int start, int end)
        {
            for (int pc = start; pc < end; pc++)
            {
                Instruction instruction = code[pc];
                if (instruction.Op is OpCode.Transfer or OpCode.LocalTransfer) return true;
                if (instruction.Op != OpCode.Call) continue;
                SawCall = true;
                if (instruction.B < 0) continue;
                // Native alternatives can return values, but KnownCalls never emits copies for them.
                if (instruction.Text is { } dispatch)
                {
                    foreach (int target in FunctionDispatch.Parse(dispatch).Values)
                        if (Add(target)) return true;
                }
                else if (instruction.B == 0 || Add(instruction.B - 1)) return true;
            }
            return false;
        }

        private bool Add(int target)
        {
            if ((uint)target >= (uint)functions.Count) return true;
            if (noCopies.Contains(target)) return false;
            if ((Visited ??= []).Add(target)) (Pending ??= []).Push(target);
            return false;
        }
    }
}
