using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static class NativeWorkPlanning
{
    private const int DirectDepth = 1;
    internal static NativeBackendFunction[] Refine(NativeProgram program, NativeBackendFunction[] functions, CompilationTrace? trace)
    {
        NativeWorkFacts facts = NativeWorkFacts.Analyze(program);
        List<int>[] callers = facts.Callers;
        bool[] owns = facts.Owns;
        int[] remaining = facts.Remaining;
        Queue<int> pending = new();
        int[] depth = Enumerable.Repeat(-1, functions.Length).ToArray();
        int[] maximumChild = new int[functions.Length];
        for (int index = 0; index < functions.Length; index++)
            if (!owns[index] && remaining[index] == 0) { depth[index] = 0; pending.Enqueue(index); }
        while (pending.TryDequeue(out int child))
            foreach (int caller in callers[child])
            {
                maximumChild[caller] = Math.Max(maximumChild[caller], depth[child]);
                if (--remaining[caller] != 0 || owns[caller]) continue;
                depth[caller] = maximumChild[caller] + 1;
                pending.Enqueue(caller);
            }
        int directCalls = 0;
        for (int index = 0; index < functions.Length; index++)
        {
            NativeBackendInstruction[] code = functions[index].Instructions.ToArray();
            for (int pc = 0; pc < code.Length; pc += code[pc].Span)
            {
                NativeInstruction call = program.Functions[index].Instructions[pc];
                if (code[pc].Op != NativeOp.Call || code[pc].Handler == NativeBackendHandler.Nop || call.C >= 0) continue;
                bool bounded = call.B >= 0 ? depth[call.B] is >= 0 and <= DirectDepth
                    : call.Symbol is { } dispatch && FunctionDispatch.Targets(dispatch).Values.All(target => depth[target] is >= 0 and <= DirectDepth);
                if (!bounded) continue;
                code[pc] = code[pc] with { Handler = NativeBackendHandler.DirectCall };
                directCalls++;
            }
            functions[index] = functions[index] with { PureSynchronous = !owns[index], Instructions = code };
        }
        trace?.Metric("direct_call.sites", directCalls);
        trace?.Metric("direct_call.maximum_callee_depth", DirectDepth);
        trace?.Metric("work_scope.deferred_constructors", facts.DeferredConstructors);
        trace?.Metric("functions", functions.Length);
        trace?.Metric("call_edges", facts.Edges);
        trace?.Metric("work_scope.required", owns.Count(value => value));
        trace?.Metric("work_scope.elided", owns.Count(value => !value));
        trace?.Metric("work_scope.propagated", facts.Propagated);
        return functions;
    }

}
