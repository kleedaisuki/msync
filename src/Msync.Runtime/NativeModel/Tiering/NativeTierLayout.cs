using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeTierLayout(int[] scalars, int[] nativePcs)
{
    internal int[] Scalars { get; } = scalars;
    internal int[] NativePcs { get; } = nativePcs;
    internal int ScalarCount { get; } = scalars.Count(index => index >= 0);
    internal int[] RefreshSlots { get; } = Enumerable.Range(0, scalars.Length).Where(slot => scalars[slot] >= 0).ToArray();
    internal int[] Hits { get; } = HitMap(nativePcs);
    internal nuint Bytes => checked((nuint)(40 + ScalarCount * 32 + NativePcs.Length * 8));

    internal static NativeTierLayout Create(NativeExecutionFunction function, NativeFastOperation[] operations)
    {
        HashSet<int> slots = [];
        int[] pcs = Enumerable.Range(0, operations.Length).Where(pc => operations[pc] != NativeFastOperation.Bridge).ToArray();
        foreach (int pc in pcs)
        {
            NativeExecutionInstruction instruction = function.Instructions[pc];
            slots.Add(instruction.Result);
            if (operations[pc] is not (NativeFastOperation.NumberConstant or NativeFastOperation.BoolConstant)) slots.Add(instruction.A);
            if (instruction.Op == NativeOp.Binary) slots.Add(instruction.B);
        }
        int[] map = Enumerable.Repeat(-1, function.Definition.Slots.Count).ToArray();
        int index = 0;
        foreach (int slot in slots.Order()) map[slot] = index++;
        return new(map, pcs);
    }

    internal static NativeTierLayout Identity(NativeExecutionFunction function) => new(
        Enumerable.Range(0, function.Definition.Slots.Count).ToArray(), Enumerable.Range(0, function.Metadata.Length).ToArray());

    private static int[] HitMap(int[] pcs)
    {
        int[] map = Enumerable.Repeat(-1, pcs.Length == 0 ? 0 : pcs[^1] + 1).ToArray();
        for (int index = 0; index < pcs.Length; index++) map[pcs[index]] = index;
        return map;
    }
}
