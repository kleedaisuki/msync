namespace Msync.Core.Native;

internal sealed record NativeWorkFacts(List<int>[] Callers, bool[] Owns, int[] Remaining,
    int Edges, int DeferredConstructors, int Propagated)
{
    internal static NativeWorkFacts Analyze(NativeProgram program)
    {
        List<int>[] callers = Enumerable.Range(0, program.Functions.Count).Select(_ => new List<int>()).ToArray();
        bool[] owns = new bool[program.Functions.Count];
        Queue<int> pending = new();
        int edges = 0, deferredConstructors = 0;
        int[] remaining = new int[program.Functions.Count];
        for (int index = 0; index < program.Functions.Count; index++)
        {
            foreach (NativeInstruction instruction in program.Functions[index].Instructions)
            {
                if (instruction.Op == NativeOp.Call && instruction.C < 0 && instruction.B >= 0)
                {
                    callers[instruction.B].Add(index);
                    remaining[index]++;
                    edges++;
                    continue;
                }
                if (instruction.Op == NativeOp.Call && instruction.C < 0 && instruction.Symbol is { } dispatch)
                {
                    foreach (int target in FunctionDispatch.Targets(dispatch).Values.Distinct())
                    {
                        callers[target].Add(index);
                        remaining[index]++;
                        edges++;
                    }
                    continue;
                }
                if (instruction.Op == NativeOp.CallNative
                    && NativeSignatures.TryGet(instruction.Symbol!, out var native)
                    && native.Behavior.GetValueOrDefault().HasFlag(NativeBehavior.Deferred | NativeBehavior.Invokes))
                    deferredConstructors++;
                owns[index] |= OwnsWork(instruction);
            }
            if (owns[index]) pending.Enqueue(index);
        }
        int propagated = 0;
        while (pending.TryDequeue(out int target))
            foreach (int caller in callers[target])
            {
                if (owns[caller]) continue;
                owns[caller] = true;
                pending.Enqueue(caller);
                propagated++;
            }
        return new(callers, owns, remaining, edges, deferredConstructors, propagated);
    }

    private static bool OwnsWork(NativeInstruction instruction)
    {
        if (instruction.Op == NativeOp.CallNative)
            return !NativeSignatures.TryGet(instruction.Symbol!, out var signature)
                || signature.RequiresWorkScope;
        return instruction.Op is NativeOp.Call or NativeOp.Transfer or NativeOp.OpenIterator
            or NativeOp.NextIterator or NativeOp.CloseIterator or NativeOp.ProcessCall
            or NativeOp.SelectPath or NativeOp.Barrier or NativeOp.Acquire
            or NativeOp.ExecutePlan or NativeOp.PlanResult;
    }

}
