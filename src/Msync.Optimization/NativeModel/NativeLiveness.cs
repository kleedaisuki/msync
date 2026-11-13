using Msync.Core.Native;

namespace Msync.Optimization;

internal sealed class NativeLiveness
{
    internal HashSet<int>[] After { get; }
    internal HashSet<int> Observable { get; }

    internal NativeLiveness(NativeProgram program, int function, NativeControlFlow flow)
    {
        NativeFunctionCode body = program.Functions[function];
        Observable = Enumerable.Range(0, body.Slots.Count).Where(slot => body.Slots[slot].Escapes).ToHashSet();
        if (function == program.EntryPoint && program.PreserveLocals)
            Observable.UnionWith(program.SessionRoots ?? Enumerable.Range(0, body.Slots.Count)
                .Where(slot => body.Slots[slot].Role != NativeSlotRole.Temporary).ToArray());
        HashSet<int>[] before = Enumerable.Range(0, body.Instructions.Count).Select(_ => new HashSet<int>()).ToArray();
        After = Enumerable.Range(0, body.Instructions.Count).Select(_ => new HashSet<int>()).ToArray();
        List<int>[] predecessors = Enumerable.Range(0, before.Length).Select(_ => new List<int>()).ToArray();
        for (int pc = 0; pc < before.Length; pc++)
            foreach (int successor in flow.Successors[pc]) predecessors[successor].Add(pc);
        Queue<int> pending = new(Enumerable.Range(0, before.Length).Reverse());
        bool[] queued = Enumerable.Repeat(true, before.Length).ToArray();
        while (pending.TryDequeue(out int pc))
        {
            queued[pc] = false;
            HashSet<int> live = new(Observable);
            foreach (int successor in flow.Successors[pc]) live.UnionWith(before[successor]);
            After[pc] = live;
            live = new(live);
            live.ExceptWith(NativeInstructionFacts.Definitions(body.Instructions[pc]));
            live.UnionWith(NativeInstructionFacts.Reads(body.Instructions[pc]));
            if (before[pc].SetEquals(live)) continue;
            before[pc] = live;
            foreach (int predecessor in predecessors[pc])
                if (!queued[predecessor]) { queued[predecessor] = true; pending.Enqueue(predecessor); }
        }
    }
}
