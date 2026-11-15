using Msync.Core;
using Msync.Core.Native;

namespace Msync.Optimization;

internal sealed class NativeLiveness
{
    internal NativeSlotSet[] After { get; }

    internal NativeLiveness(NativeProgram program, int function, NativeControlFlow flow, CompilationTrace? trace = null)
    {
        NativeFunctionCode body = program.Functions[function];
        After = new NativeSlotSet[body.Instructions.Count];
        var state = (self: this, program, function, flow, trace);
        if (trace is null) Solve(program, function, flow, null);
        else trace.Run("optimizer.analysis.liveness", state,
            static item => item.self.Solve(item.program, item.function, item.flow, item.trace),
            attributes: new Dictionary<string, string>
            {
                ["function.name"] = body.Name,
                ["function.id"] = function.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
    }

    private void Solve(NativeProgram program, int function, NativeControlFlow flow, CompilationTrace? trace)
    {
        NativeFunctionCode body = program.Functions[function];
        NativeSlotSet observable = new(body.Slots.Count);
        for (int slot = 0; slot < body.Slots.Count; slot++)
            if (body.Slots[slot].Escapes) observable.Add(slot);
        if (function == program.EntryPoint && program.PreserveLocals)
            foreach (int slot in program.SessionRoots ?? Enumerable.Range(0, body.Slots.Count)
                .Where(slot => body.Slots[slot].Role != NativeSlotRole.Temporary).ToArray()) observable.Add(slot);
        NativeSlotSet[] before = Sets(flow.Blocks.Count, body.Slots.Count);
        NativeSlotSet[] generated = Sets(flow.Blocks.Count, body.Slots.Count);
        NativeSlotSet[] killed = Sets(flow.Blocks.Count, body.Slots.Count);
        List<int>[] predecessors = Enumerable.Range(0, before.Length).Select(_ => new List<int>()).ToArray();
        for (int index = 0; index < flow.Blocks.Count; index++)
        {
            NativeControlFlow.Block block = flow.Blocks[index];
            for (int pc = block.End - 1; pc >= block.Start; pc--)
            {
                Transfer(generated[index], observable, body.Instructions[pc]);
                foreach (int slot in NativeInstructionFacts.Definitions(body.Instructions[pc])) killed[index].Add(slot);
            }
            foreach (int successor in block.Successors) predecessors[successor].Add(index);
        }
        Queue<int> pending = new(Enumerable.Range(0, before.Length).Reverse());
        bool[] queued = Enumerable.Repeat(true, before.Length).ToArray();
        long transfers = 0;
        while (pending.TryDequeue(out int index))
        {
            queued[index] = false;
            transfers++;
            NativeSlotSet live = Out(flow.Blocks[index], before, body.Slots.Count);
            live.ExceptWith(killed[index]);
            live.UnionWith(generated[index]);
            if (before[index].Same(live)) continue;
            before[index] = live;
            foreach (int predecessor in predecessors[index])
                if (!queued[predecessor]) { queued[predecessor] = true; pending.Enqueue(predecessor); }
        }
        foreach (NativeControlFlow.Block block in flow.Blocks)
        {
            NativeSlotSet live = Out(block, before, body.Slots.Count);
            for (int pc = block.End - 1; pc >= block.Start; pc--)
            {
                live.UnionWith(observable);
                After[pc] = live.Copy();
                Transfer(live, observable, body.Instructions[pc]);
            }
        }
        trace?.Metric("blocks", flow.Blocks.Count);
        trace?.Metric("slots", body.Slots.Count);
        trace?.Metric("instructions", body.Instructions.Count);
        trace?.Metric("solver.transfers", transfers);
        trace?.Metric("after.words", (long)body.Instructions.Count * observable.WordCount);
    }

    private static NativeSlotSet[] Sets(int count, int slots) =>
        Enumerable.Range(0, count).Select(_ => new NativeSlotSet(slots)).ToArray();

    private static NativeSlotSet Out(NativeControlFlow.Block block, NativeSlotSet[] before, int slots)
    {
        NativeSlotSet result = new(slots);
        foreach (int successor in block.Successors) result.UnionWith(before[successor]);
        return result;
    }

    private static void Transfer(NativeSlotSet live, NativeSlotSet observable, NativeInstruction instruction)
    {
        live.UnionWith(observable);
        foreach (int slot in NativeInstructionFacts.Definitions(instruction)) live.Remove(slot);
        foreach (int slot in NativeInstructionFacts.Reads(instruction)) live.Add(slot);
    }
}
