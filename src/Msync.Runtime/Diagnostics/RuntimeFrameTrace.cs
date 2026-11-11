using Msync.Core;

namespace Msync.Runtime;

internal sealed class RuntimeFrameTrace(RuntimeTimeline timeline)
{
    public long Suspensions { get; private set; }

    public ValueTask<T> Observe<T>(ValueTask<T> pending, string reason, int pc = -1, Instruction? instruction = null) =>
        pending.IsCompleted ? pending : ObserveIncompleteAsync(pending, reason, pc, instruction);

    public ValueTask Observe(ValueTask pending, string reason, int pc = -1, Instruction? instruction = null) =>
        pending.IsCompleted ? pending : ObserveIncompleteAsync(pending, reason, pc, instruction);

    public Task<T> Observe<T>(Task<T> pending, string reason, int pc, Instruction instruction) =>
        pending.IsCompleted ? pending : ObserveIncompleteAsync(pending, reason, pc, instruction);

    public double Power(double left, double right, SourceSpan? source)
    {
        using var span = BeginNative(timeline, "math.pow", "inline", source);
        try { return NativeRegistry.Power(left, right); }
        catch (Exception error) { span.Fail(error); throw; }
    }

    public RuntimeTimelineScope BeginInlineNative(Instruction instruction) =>
        BeginNative(timeline, instruction.Text!, "inline", instruction.Span);

    public static RuntimeTimelineScope BeginNative(RuntimeTimeline timeline, string name, string route, SourceSpan? source = null)
    {
        var span = timeline.Begin("vm.native");
        span.SetAttribute("native.name", name);
        span.SetAttribute("native.route", route);
        span.SetAttribute("duration.kind", "inclusive");
        span.SetSource(source);
        return span;
    }

    private async ValueTask<T> ObserveIncompleteAsync<T>(ValueTask<T> pending, string reason, int pc, Instruction? instruction)
    {
        using var span = BeginAwait(reason, pc, instruction);
        try { return await pending; }
        catch (Exception error) { span.Fail(error); throw; }
    }

    private async ValueTask ObserveIncompleteAsync(ValueTask pending, string reason, int pc, Instruction? instruction)
    {
        using var span = BeginAwait(reason, pc, instruction);
        try { await pending; }
        catch (Exception error) { span.Fail(error); throw; }
    }

    private async Task<T> ObserveIncompleteAsync<T>(Task<T> pending, string reason, int pc, Instruction instruction)
    {
        using var span = BeginAwait(reason, pc, instruction);
        span.Link("task_completion", pending);
        try { return await pending; }
        catch (Exception error) { span.Fail(error); throw; }
    }

    private RuntimeTimelineScope BeginAwait(string reason, int pc, Instruction? instruction)
    {
        Suspensions++;
        var span = timeline.Begin("vm.await");
        span.SetAttribute("duration.kind", "observed_incomplete");
        span.SetAttribute("wait.reason", reason);
        if (instruction is not null)
        {
            span.SetMetric("instruction.pc", pc);
            span.SetAttribute("opcode", instruction.Op.ToString());
            span.SetSource(instruction.Span);
            if (instruction.Op == OpCode.NativeCall && instruction.Text is { } name)
                span.SetAttribute("native.name", name);
        }
        return span;
    }
}
