using Msync.Core;
using Msync.Core.Native;
using Msync.Optimization;

internal static class LivenessChecks
{
    internal static int Run()
    {
        Random random = new(91753);
        int observations = 0;
        foreach (int slots in new[] { 2, 8, 63, 64, 65, 127, 128, 129, 192 })
            for (int sample = 0; sample < 12; sample++)
            {
                NativeProgram program = Build(random, slots, sample);
                var body = program.Functions[0];
                NativeControlFlow flow = new(body.Instructions);
                var expected = Reference(program, flow);
                NativeLiveness actual = new(program, 0, flow);
                for (int pc = 0; pc < expected.Length; pc++)
                {
                    if (!expected[pc].SetEquals(actual.After[pc]))
                        throw new InvalidOperationException($"Liveness mismatch: slots={slots} sample={sample} pc={pc}");
                    observations++;
                }
            }
        return observations;
    }

    private static NativeProgram Build(Random random, int count, int sample)
    {
        NativeSlot[] slots = Enumerable.Range(0, count).Select(index => new NativeSlot(index == 0 ? 1 : 0,
            NativeSlotRole.Local, Escapes: random.Next(7) == 0)).ToArray();
        NativeInstruction[] code = new NativeInstruction[24 + sample * 3];
        int Number() => random.Next(1, count);
        for (int pc = 0; pc < code.Length; pc++)
            code[pc] = random.Next(7) switch
            {
                0 => new(NativeOp.Constant, Number(), 0),
                1 => new(NativeOp.Copy, Number(), Number()),
                2 => new(NativeOp.Drop, A: Number()),
                3 => new(NativeOp.Binary, Number(), Number(), Number(), Immediate: (long)NativeBinary.Add),
                4 => new(NativeOp.Branch, A: 0, B: random.Next(code.Length), C: random.Next(code.Length)),
                5 => new(NativeOp.Jump, A: random.Next(code.Length)),
                _ => new(NativeOp.Nop)
            };
        code[^1] = new(NativeOp.Return, A: Number());
        NativeFunctionCode body = new("property", slots, code, new([], 0, []));
        return new([new(NativeKind.Number)], [body], [new(TypeKind.Number), new(TypeKind.Bool)], [], 0)
        { PreserveLocals = sample % 2 == 0, SessionRoots = [0, count - 1] };
    }

    private static HashSet<int>[] Reference(NativeProgram program, NativeControlFlow flow)
    {
        NativeFunctionCode body = program.Functions[0];
        HashSet<int> observable = Enumerable.Range(0, body.Slots.Count).Where(slot => body.Slots[slot].Escapes).ToHashSet();
        if (program.PreserveLocals) observable.UnionWith(program.SessionRoots!);
        HashSet<int>[] before = Enumerable.Range(0, body.Instructions.Count).Select(_ => new HashSet<int>()).ToArray();
        HashSet<int>[] after = Enumerable.Range(0, body.Instructions.Count).Select(_ => new HashSet<int>()).ToArray();
        bool changed;
        do
        {
            changed = false;
            for (int pc = before.Length - 1; pc >= 0; pc--)
            {
                HashSet<int> live = new(observable);
                foreach (int successor in flow.Successors[pc]) live.UnionWith(before[successor]);
                after[pc] = live;
                live = new(live);
                live.ExceptWith(NativeInstructionFacts.Definitions(body.Instructions[pc]));
                live.UnionWith(NativeInstructionFacts.Reads(body.Instructions[pc]));
                if (before[pc].SetEquals(live)) continue;
                before[pc] = live;
                changed = true;
            }
        } while (changed);
        return after;
    }
}
