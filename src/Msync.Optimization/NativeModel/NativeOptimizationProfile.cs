using System.Diagnostics;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Optimization;

internal sealed class NativeOptimizationProfile(CompilationTrace trace)
{
    internal enum Pass { ControlFlow, EscapeAnalysis, LocalValues, DeadMemory, DeadResults, LastUseMoves, Placement, Compact, GlobalFacts, Inlining, GlobalMemory, Slots, Count }
    internal readonly record struct Mark(long Ticks, long Allocated);

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
    private readonly long ticksOrigin = Stopwatch.GetTimestamp();
    private readonly long unixOrigin = CompilationTrace.UnixNow();

    internal Mark Start() => new(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

    internal void Add(Pass pass, Mark start, NativeFunctionCode function, bool changed = false)
        => Add(pass, start, function, function, changed);

    internal void Add(Pass pass, Mark start, NativeFunctionCode input, NativeFunctionCode output, bool changed)
    {
        ref Sample sample = ref samples[(int)pass];
        sample.Duration += CompilationTrace.Elapsed(start.Ticks);
        sample.Allocated += GC.GetAllocatedBytesForCurrentThread() - start.Allocated;
        if (sample.Invocations++ == 0)
            sample.FirstStart = unixOrigin + (long)Stopwatch.GetElapsedTime(ticksOrigin, start.Ticks).TotalNanoseconds;
        sample.Input += CompilationSize.Of(input);
        sample.Output += CompilationSize.Of(output);
        if (changed) sample.Changed++;
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
