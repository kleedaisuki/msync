using Msync.Optimization;
using System.Reflection;
using Msync.Core;
using Module = Msync.Core.Module;

internal static class DirectionalChecks
{
    internal static int Run()
    {
        MethodInfo optimize = typeof(Optimizer).Assembly.GetType("Msync.Optimization.DirectionalNormalization")!
            .GetMethod("Optimize", BindingFlags.NonPublic | BindingFlags.Static)!;
        int assertions = 0;
        for (int length = 2; length <= 7; length++)
        {
            int count = (int)Math.Pow(3, length);
            for (int number = 0; number < count; number++)
            {
                int[] events = new int[length];
                int remaining = number;
                for (int index = 0; index < length; index++) { events[index] = remaining % 3; remaining /= 3; }
                if (!events.Contains(0) || events.All(item => item == 0)) continue;
                FunctionCode original = Build(events);
                Module module = new([new StringValue("bytes"), new PathValue("output")], [original], OptimizationLevel: 0);
                FunctionCode result = (FunctionCode)optimize.Invoke(null, [module, original])!;
                (int[] markers, int[] slots, int[] transfers) = Observe(result);
                (int[] expectedMarkers, int[] originalSlots, int[] expectedTransfers) = Observe(original);
                Check(markers.SequenceEqual(expectedMarkers), "Marker order changed", events);
                Check(transfers.SequenceEqual(expectedTransfers), "Transfer order changed", events);
                var intervals = originalSlots.Select(slot => Bounds(events.Where(item => item != 0).ToArray(), slot)).ToArray();
                for (int index = 0; index < slots.Length; index++)
                    Check(slots[index] >= intervals[index].Lower && slots[index] <= intervals[index].Upper, "Transfer crossed a constrained marker", events);
                int groups = slots.Distinct().Count();
                Check(groups == MinimumGroups(intervals, markers.Length), $"Nonminimal transfer grouping {groups}", events);
            }
        }
        FunctionCode paired = new("paired", 0, 0,
            [new(OpCode.Barrier, 1, 1, "owner"), new(OpCode.Constant, 0), new(OpCode.Constant, 1),
             new(OpCode.Transfer), new(OpCode.Pop), new(OpCode.Barrier, 2, Text: "owner"), new(OpCode.Null), new(OpCode.Return)], TypeRef.Null);
        Module pairedModule = new([new StringValue("bytes"), new PathValue("output")], [paired]);
        FunctionCode unchanged = (FunctionCode)optimize.Invoke(null, [pairedModule, paired])!;
        Check(unchanged.Instructions.SequenceEqual(paired.Instructions), "Paired admission markers were moved", []);
        return assertions;

        void Check(bool condition, string message, int[] events)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message + ": " + string.Join(',', events));
        }
    }

    private static FunctionCode Build(int[] events)
    {
        List<Instruction> code = [];
        for (int index = 0; index < events.Length; index++)
        {
            SourceSpan identity = new("directional", index, 1);
            if (events[index] != 0) { code.Add(new(OpCode.Barrier, events[index], Span: identity)); continue; }
            code.Add(new(OpCode.Constant, 0)); code.Add(new(OpCode.Constant, 1));
            code.Add(new(OpCode.Transfer, Span: identity)); code.Add(new(OpCode.Pop));
        }
        code.Add(new(OpCode.Null)); code.Add(new(OpCode.Return));
        return new("directional", 0, 0, code, TypeRef.Null);
    }

    private static (int[] Markers, int[] Slots, int[] Transfers) Observe(FunctionCode function)
    {
        List<int> markers = [], slots = [], transfers = [];
        foreach (Instruction instruction in function.Instructions)
        {
            if (instruction.Op == OpCode.Barrier) markers.Add(instruction.Span!.Value.Start);
            if (instruction.Op != OpCode.Transfer) continue;
            slots.Add(markers.Count); transfers.Add(instruction.Span!.Value.Start);
        }
        return (markers.ToArray(), slots.ToArray(), transfers.ToArray());
    }

    private static (int Lower, int Upper) Bounds(int[] markers, int slot)
    {
        int lower = 0, upper = markers.Length;
        for (int index = 0; index < markers.Length; index++)
        {
            if (index < slot && markers[index] == 2) lower = index + 1;
            if (index >= slot && markers[index] == 1) { upper = index; break; }
        }
        return (lower, upper);
    }

    private static int MinimumGroups((int Lower, int Upper)[] intervals, int markerCount)
    {
        // Independent dynamic program enumerates every legal nondecreasing slot
        // assignment; unlike the implementation, it does not greedily pick an end.
        var costs = Enumerable.Repeat(int.MaxValue, markerCount + 1).ToArray();
        for (int slot = intervals[0].Lower; slot <= intervals[0].Upper; slot++) costs[slot] = 1;
        foreach ((int lower, int upper) in intervals.Skip(1))
        {
            var next = Enumerable.Repeat(int.MaxValue, markerCount + 1).ToArray();
            for (int slot = lower; slot <= upper; slot++)
            for (int previous = 0; previous <= slot; previous++)
                if (costs[previous] != int.MaxValue) next[slot] = Math.Min(next[slot], costs[previous] + (previous == slot ? 0 : 1));
            costs = next;
        }
        return costs.Min();
    }
}
