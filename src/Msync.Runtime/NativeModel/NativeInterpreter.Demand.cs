using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private static T Demand<T>(ValueTask<T> pending) => pending.IsCompleted
        ? pending.Result : pending.AsTask().GetAwaiter().GetResult();

    private static void Demand(ValueTask pending)
    {
        if (pending.IsCompleted) pending.GetAwaiter().GetResult();
        else pending.AsTask().GetAwaiter().GetResult();
    }

    private NativeStep DemandStep(NativeStep pending, Activation activation, int pc)
    {
        if (pending.Pending is not { } completion) return pending;
        if (completion.IsCompleted) return completion.GetAwaiter().GetResult();
        using RuntimeTimelineScope? wait = context.Timeline?.Begin("vm.demand");
        wait?.SetAttribute("function.name", activation.Function.Name);
        wait?.SetAttribute("instruction.op", activation.Execution.Metadata[pc].Op.ToString());
        wait?.SetMetric("instruction.pc", pc);
        activation.Work.Counters.Pause();
        try { return completion.GetAwaiter().GetResult(); }
        finally { activation.Work.Counters.Continue(); }
    }
}
