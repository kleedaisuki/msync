using System.Diagnostics;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Optimization;

internal sealed class NativeOptimizationProfile(CompilationTrace trace)
{
    internal enum Pass { ControlFlow, EscapeAnalysis, LocalValues, DeadMemory, DeadResults, LastUseMoves, Placement, Compact, GlobalFacts, Inlining, GlobalMemory, Slots, Count }
    internal readonly record struct Mark(long Ticks, long Allocated, long Unix, CompilationSize Input,
        int ActiveInstructions, string Function, int FunctionId, int Iteration, Pass Pass);

    private struct Sample
    {
        internal long FirstStart;
        internal long Duration;
        internal long Allocated;
        internal long Invocations;
        internal long Changed;
        internal CompilationSize Input;
        internal CompilationSize Output;
    }

    private static readonly string[] names =
    [
        "native-control-flow", "native-escape-analysis", "native-local-values", "native-dead-memory",
        "native-dead-results", "native-last-use-moves", "native-allocation-placement",
        "native-compact", "native-global-facts", "native-inlining", "native-global-memory", "native-virtual-slots"
    ];
    private readonly Sample[] samples = new Sample[(int)Pass.Count];

    internal CompilationTrace Trace => trace;
    private int functionId;
    private int iteration;
    private Mark? active;

    internal void Function(int index) { functionId = index; iteration = 0; }
    internal void Iteration(int value) => iteration = value;

    internal Mark Start(Pass pass, NativeFunctionCode function)
    {
        CompilationSize size = CompilationSize.Of(function);
        int instructions = function.Instructions.Count(item => item.Op != NativeOp.Nop);
        long unix = CompilationTrace.UnixNow();
        Mark mark = new(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread(), unix, size,
            instructions, function.Name, functionId, iteration, pass);
        active = mark;
        return mark;
    }

    internal void Fail(Exception error)
    {
        if (active is not { } mark) return;
        active = null;
        trace.Report(new($"optimizer.pass.{names[(int)mark.Pass]}", mark.Unix, CompilationTrace.Elapsed(mark.Ticks),
            GC.GetAllocatedBytesForCurrentThread() - mark.Allocated,
            error is OperationCanceledException ? CompilationOutcome.Cancelled : CompilationOutcome.Error, mark.Input)
        { Attributes = Attributes(mark), Error = error });
    }

    private static Dictionary<string, string> Attributes(Mark mark) => new()
    {
        ["function.name"] = mark.Function,
        ["function.id"] = mark.FunctionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["optimizer.iteration"] = mark.Iteration.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    internal void Add(Pass pass, Mark start, NativeFunctionCode function, bool changed = false)
        => Add(pass, start, function, function, changed);

    internal void Add(Pass pass, Mark start, NativeFunctionCode input, NativeFunctionCode output, bool changed,
        IReadOnlyDictionary<string, ulong>? metrics = null)
    {
        long duration = CompilationTrace.Elapsed(start.Ticks);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start.Allocated;
        active = null;
        ref Sample sample = ref samples[(int)pass];
        sample.Duration += duration;
        sample.Allocated += allocated;
        if (sample.Invocations++ == 0)
            sample.FirstStart = start.Unix;
        sample.Input += start.Input;
        sample.Output += CompilationSize.Of(output);
        if (changed) sample.Changed++;
        Dictionary<string, ulong> details = new()
        {
            ["input.active_instructions"] = (ulong)start.ActiveInstructions,
            ["output.active_instructions"] = (ulong)output.Instructions.Count(item => item.Op != NativeOp.Nop)
        };
        if (metrics is not null) foreach (var (key, value) in metrics) details[key] = value;
        trace.Report(new($"optimizer.pass.{names[(int)pass]}", start.Unix, duration, allocated,
            CompilationOutcome.Ok, start.Input, CompilationSize.Of(output), ChangedInvocations: changed ? 1 : 0)
        {
            Attributes = Attributes(start),
            Metrics = details
        });
    }

    internal void Publish()
    {
        for (int index = 0; index < samples.Length; index++)
        {
            Sample sample = samples[index];
            if (sample.Invocations == 0) continue;
            bool analysis = (Pass)index is Pass.ControlFlow or Pass.EscapeAnalysis;
            trace.Report(new($"optimizer.{names[index]}", sample.FirstStart, sample.Duration,
                sample.Allocated, CompilationOutcome.Ok, sample.Input, analysis ? null : sample.Output,
                sample.Invocations, analysis ? null : sample.Changed, Aggregated: true));
        }
    }
}
