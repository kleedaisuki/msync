using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class PreparationCostProbe
{
    internal static async Task RunAsync(string scratch, IReadOnlyList<int>? sizes = null)
    {
        List<object> rows = [];
        foreach (int count in sizes ?? new[] { 1, 128, 1024 })
        {
            string directory = Path.Combine(scratch, "prepare-cost-" + count);
            Directory.CreateDirectory(directory);
            StringBuilder source = new();
            for (int index = 0; index < count; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, $"input-{index}"), "payload");
                source.Append("./input-").Append(index).Append(" -> ./output-").Append(index).AppendLine(";");
            }
            string text = source.ToString();
            Module bound = Binding.Bind(Compiler.Compile(text, Path.Combine(directory, "main.msync")),
                new(directory, directory));
            ExecutionOptions options = new(WorkingDirectory: directory, Jobs: 4);
            Module prepared = bound;
            for (int warmup = 0; warmup < 3; warmup++) prepared = ContextualBackend.Prepare(bound, options);
            List<double> times = [];
            List<long> allocations = [];
            for (int sample = 0; sample < 5; sample++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                prepared = ContextualBackend.Prepare(bound, options);
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                times.Add(elapsed);
                allocations.Add(bytes);
                int nodes = prepared.ExecutionPlans?.Sum(plan => plan.Transfers.Count) ?? 0;
                if (nodes != count) throw new InvalidOperationException($"Independent {count}-file preparation produced {nodes} transfer nodes");
            }
            if (Directory.EnumerateFileSystemEntries(directory, "output-*").Any())
                throw new InvalidOperationException("Preparation executed file transfers");
            var plans = prepared.ExecutionPlans ?? throw new InvalidOperationException("Preparation returned no execution plan table");
            rows.Add(new
            {
                inputTransfers = count,
                sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))),
                rawMilliseconds = times,
                medianMilliseconds = Median(times),
                rawAllocatedBytes = allocations,
                medianAllocatedBytes = Median(allocations.Select(value => (double)value).ToArray()),
                planNodes = plans.Sum(plan => plan.Transfers.Count),
                batches = plans.Sum(plan => plan.Batches.Count),
                instructions = prepared.Functions.Sum(function => function.Instructions.Count),
                blocks = prepared.Blocks?.Count ?? 0
            });
        }
        var report = new
        {
            scope = "Managed preparation component only; compile/bind/fixtures outside timing, no VM execution or Native throughput claim",
            warmups = 3,
            samples = 5,
            allocationCounter = "GC.GetAllocatedBytesForCurrentThread",
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
            assemblies = new[] { typeof(Module).Assembly, typeof(Compiler).Assembly, typeof(ContextualBackend).Assembly }
                .ToDictionary(assembly => Path.GetFileName(assembly.Location),
                    assembly => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location)))),
            rows
        };
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
