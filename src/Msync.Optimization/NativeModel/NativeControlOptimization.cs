using Msync.Core.Native;

namespace Msync.Optimization;

internal static class NativeControlOptimization
{
    internal sealed record Result(NativeFunctionCode Function, int[] Positions, bool[] Retained);

    internal static Result Compact(NativeFunctionCode function)
    {
        NativeInstruction[] code = function.Instructions.Select(instruction =>
            NativeCodeRelocation.Targets(instruction, target => Follow(function.Instructions, target))).ToArray();
        bool[] reachable = Reachable(code);
        bool[] retained = new bool[code.Length];
        int next = code.Length;
        for (int pc = code.Length - 1; pc >= 0; pc--)
        {
            retained[pc] = reachable[pc] && code[pc].Op != NativeOp.Nop
                && !(code[pc].Op == NativeOp.Jump && code[pc].A == next);
            if (retained[pc]) next = pc;
        }
        int[] positions = new int[code.Length + 1];
        int count = 0;
        for (int pc = 0; pc < code.Length; pc++)
        {
            positions[pc] = count;
            if (retained[pc]) count++;
        }
        positions[code.Length] = count;
        NativeInstruction[] compact = new NativeInstruction[count];
        for (int pc = 0; pc < code.Length; pc++)
            if (retained[pc]) compact[positions[pc]] = NativeCodeRelocation.Targets(code[pc], target => positions[target]);
        return new(function with { Instructions = compact }, positions, retained);
    }

    private static int Follow(IReadOnlyList<NativeInstruction> code, int target)
    {
        HashSet<int> visited = [];
        while (target < code.Count && visited.Add(target))
        {
            NativeInstruction next = code[target];
            if (next.Op == NativeOp.Nop) { target++; continue; }
            if (next.Op == NativeOp.Jump) { target = next.A; continue; }
            break;
        }
        return target;
    }

    private static bool[] Reachable(IReadOnlyList<NativeInstruction> code)
    {
        bool[] reached = new bool[code.Count];
        if (code.Count == 0) return reached;
        NativeControlFlow flow = new(code);
        Stack<int> pending = new();
        pending.Push(0);
        while (pending.TryPop(out int pc))
        {
            if (reached[pc]) continue;
            reached[pc] = true;
            foreach (int successor in flow.Successors[pc]) pending.Push(successor);
        }
        return reached;
    }
}
