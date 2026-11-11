using System.Diagnostics;

namespace Msync.Optimization;

internal sealed class OptimizationProfile(CompilationTrace trace)
{
    internal enum Pass
    {
        Linear, Predicates, Affine, Memory, ScopeScalarization, TailCalls, Iteration,
        MemoryEffects, Effects, Graph, Evaluation, Locals, Interprocedural, AggregateArguments,
        Inlining, Directional, GlobalRedundancy, IndexedMemory, NaturalLoops, ValueNumbering, Compact,
        Count
    }

    internal readonly record struct Mark(long Ticks, long Allocated);

    private struct Sample
    {
        public long FirstStart;
        public long Duration;
        public long Allocated;
        public long Count;
        public long Changed;
        public CompilationSize Input;
        public CompilationSize Output;
    }

    private static readonly string[] Names =
    [
        "linear-effects", "predicates", "affine-loop", "cross-block-memory", "scope-scalarization",
        "tail-calls", "iterator-loop", "memory-effects", "effect-analysis", "graph",
        "compile-time-evaluation", "local-slots", "interprocedural", "aggregate-arguments",
        "inlining", "directional", "global-redundancy", "indexed-memory", "natural-loops", "value-numbering",
        "compact"
    ];

    private readonly long ticksOrigin = Stopwatch.GetTimestamp();
    private readonly long unixOrigin = CompilationTrace.UnixNow();
    private readonly Sample[] samples = new Sample[(int)Pass.Count];

    internal Mark Start() => new(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());

    internal void Add(Pass pass, Mark start, FunctionCode input, FunctionCode output)
    {
        ref Sample sample = ref Complete(pass, start);
        sample.Input += CompilationSize.Of(input);
        sample.Output += CompilationSize.Of(output);
        if (Changed(input, output)) sample.Changed++;
    }

    internal void Add(Pass pass, Mark start, Module input, Module output)
    {
        ref Sample sample = ref Complete(pass, start);
        sample.Input += CompilationSize.Of(input);
        sample.Output += CompilationSize.Of(output);
        if (Changed(input, output)) sample.Changed++;
    }

    internal void AddAnalysis(Pass pass, Mark start, Module input)
    {
        ref Sample sample = ref Complete(pass, start);
        sample.Input += CompilationSize.Of(input);
    }

    internal void Publish()
    {
        for (int index = 0; index < samples.Length; index++)
        {
            ref Sample sample = ref samples[index];
            if (sample.Count == 0) continue;
            bool analysis = (Pass)index is Pass.MemoryEffects or Pass.Effects;
            trace.Report(new($"optimizer.{Names[index]}", sample.FirstStart, sample.Duration,
                sample.Allocated, CompilationOutcome.Ok, sample.Input,
                analysis ? null : sample.Output, sample.Count,
                analysis ? null : sample.Changed, Aggregated: true));
        }
    }

    private ref Sample Complete(Pass pass, Mark start)
    {
        ref Sample sample = ref samples[(int)pass];
        sample.Duration += CompilationTrace.Elapsed(start.Ticks);
        sample.Allocated += GC.GetAllocatedBytesForCurrentThread() - start.Allocated;
        if (sample.Count++ == 0)
            sample.FirstStart = unixOrigin + (long)Stopwatch.GetElapsedTime(ticksOrigin, start.Ticks).TotalNanoseconds;
        return ref sample;
    }

    private static bool Changed(FunctionCode input, FunctionCode output) => !ReferenceEquals(input, output)
        && (input.LocalCount != output.LocalCount || input.ParameterCount != output.ParameterCount
            || input.CaptureCount != output.CaptureCount || !input.Instructions.SequenceEqual(output.Instructions));

    private static bool Changed(Module input, Module output)
    {
        if (ReferenceEquals(input, output)) return false;
        if (input.Constants.Count != output.Constants.Count || input.Functions.Count != output.Functions.Count
            || input.EntryPoint != output.EntryPoint) return true;
        for (int index = 0; index < input.Functions.Count; index++)
            if (Changed(input.Functions[index], output.Functions[index])) return true;
        return false;
    }
}
