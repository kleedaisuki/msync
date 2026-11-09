using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Msync.Core;
using SourceCompiler = Msync.Compiler.Compiler;

internal static class RetentionObservation
{
    public static JsonObject Compile(string source, string sourceName, int level)
    {
        Warm(source, sourceName, level);
        _ = Snapshot();
        using HeapSampler sampler = new();
        MemorySnapshot before = Snapshot();
        sampler.Begin(GC.GetTotalMemory(false));
        HeldResult held = CompileAndHold(source, sourceName, level);
        sampler.Finish();
        MemorySnapshot released = Snapshot();
        bool aliveAfterRelease = held.Reference.TryGetTarget(out _);
        return new JsonObject
        {
            ["stage"] = "compile_retention",
            ["optimization"] = level,
            ["requested_sampling_interval_ms"] = 1,
            ["observed_peak_managed_heap_bytes"] = sampler.Peak,
            ["heap_observations"] = sampler.Observations,
            ["before"] = before.ToJson(),
            ["held_result"] = held.Memory.ToJson(),
            ["released_result"] = released.ToJson(),
            ["module_alive_while_held"] = held.Alive,
            ["module_alive_after_release"] = aliveAfterRelease,
            ["functions"] = held.Functions,
            ["instructions"] = held.Instructions,
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Warm(string source, string sourceName, int level)
    {
        for (int index = 0; index < 3; index++)
            GC.KeepAlive(SourceCompiler.Compile(source, sourceName, level));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static HeldResult CompileAndHold(string source, string sourceName, int level)
    {
        var module = SourceCompiler.Compile(source, sourceName, level);
        WeakReference<Module> reference = new(module);
        MemorySnapshot held = Snapshot();
        bool alive = reference.TryGetTarget(out _);
        HeldResult result = new(reference, held, alive, module.Functions.Count,
            module.Functions.Sum(function => function.Instructions.Count));
        GC.KeepAlive(module);
        return result;
    }

    private static MemorySnapshot Snapshot()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long managed = GC.GetTotalMemory(forceFullCollection: false);
        GCMemoryInfo heap = GC.GetGCMemoryInfo();
        using Process process = Process.GetCurrentProcess();
        return new(managed, heap.HeapSizeBytes, heap.TotalCommittedBytes,
            process.WorkingSet64, process.PeakWorkingSet64);
    }

    private readonly record struct MemorySnapshot(long Managed, long Heap, long Committed,
        long WorkingSet, long PeakWorkingSet)
    {
        public JsonObject ToJson() => new()
        {
            ["managed_after_collection_bytes"] = Managed,
            ["gc_heap_size_bytes"] = Heap,
            ["gc_committed_bytes"] = Committed,
            ["process_working_set_bytes"] = WorkingSet,
            ["process_lifetime_peak_working_set_bytes"] = PeakWorkingSet
        };
    }

    private sealed record HeldResult(WeakReference<Module> Reference, MemorySnapshot Memory,
        bool Alive, int Functions, int Instructions);

    private sealed class HeapSampler : IDisposable
    {
        private readonly Thread thread;
        private readonly ManualResetEventSlim ready = new();
        private volatile bool stopping;
        private volatile bool observing;
        private long peak;
        private long observations;

        public HeapSampler()
        {
            thread = new(Sample) { IsBackground = true, Name = "compiler-heap-observation" };
            thread.Start();
            ready.Wait();
        }

        public long Peak => Interlocked.Read(ref peak);
        public long Observations => Interlocked.Read(ref observations);
        public void Begin(long initial) { Interlocked.Exchange(ref peak, initial); observing = true; }
        public void Finish() { observing = false; stopping = true; thread.Join(); }

        private void Sample()
        {
            ready.Set();
            while (!stopping)
            {
                if (observing)
                {
                    long current = GC.GetTotalMemory(false);
                    if (current > peak) Interlocked.Exchange(ref peak, current);
                    Interlocked.Increment(ref observations);
                }
                Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            if (!stopping) Finish();
            ready.Dispose();
        }
    }
}
