using System.Diagnostics;

namespace Msync.Core;

public enum CompilationOutcome : byte { Ok, Error, Cancelled }

public readonly record struct CompilationSize(long Functions, long Instructions, long Locals)
{
    public static CompilationSize Of(FunctionCode function) =>
        new(1, function.Instructions.Count, function.LocalCount);

    public static CompilationSize Of(Module module)
    {
        long instructions = 0;
        long locals = 0;
        foreach (var function in module.Functions)
        {
            instructions += function.Instructions.Count;
            locals += function.LocalCount;
        }
        return new(module.Functions.Count, instructions, locals);
    }

    public static CompilationSize operator +(CompilationSize left, CompilationSize right) =>
        new(left.Functions + right.Functions, left.Instructions + right.Instructions, left.Locals + right.Locals);
}

public readonly record struct CompilationObservation(string Name, long StartUnixNanoseconds,
    long DurationNanoseconds, long AllocatedBytes, CompilationOutcome Outcome,
    CompilationSize? Input = null, CompilationSize? Output = null, long Invocations = 1,
    long? ChangedInvocations = null, bool Aggregated = false);

/// <summary>Observes synchronous compiler work without ambient state or dependencies on a storage sink.</summary>
public sealed class CompilationTrace(Action<CompilationObservation> report, bool includeOptimizerPasses = false)
{
    public bool IncludeOptimizerPasses => includeOptimizerPasses;

    public void Report(CompilationObservation observation) => report(observation);

    public T Measure<TState, T>(string name, TState state, Func<TState, T> action,
        CompilationSize? input = null, Func<T, CompilationSize>? size = null)
    {
        long unixStart = UnixNow();
        long start = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        T output;
        try { output = action(state); }
        catch (Exception error)
        {
            Report(new(name, unixStart, Elapsed(start), GC.GetAllocatedBytesForCurrentThread() - allocated,
                error is OperationCanceledException ? CompilationOutcome.Cancelled : CompilationOutcome.Error, input));
            throw;
        }
        long elapsed = Elapsed(start);
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Report(new(name, unixStart, elapsed, allocatedBytes, CompilationOutcome.Ok, input, size?.Invoke(output)));
        return output;
    }

    public static long UnixNow() => (DateTimeOffset.UtcNow.Ticks - DateTimeOffset.UnixEpoch.Ticks) * 100;
    public static long Elapsed(long start) => (long)Stopwatch.GetElapsedTime(start).TotalNanoseconds;
}
