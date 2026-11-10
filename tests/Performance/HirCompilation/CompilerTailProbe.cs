using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Msync.Core;
using Msync.Hir;
using Msync.Optimization;
using SourceCompiler = Msync.Compiler.Compiler;

internal static class CompilerTailProbe
{
    private readonly record struct Sample(double Milliseconds, long Bytes, int Gen0, int Gen1, int Gen2)
    {
        internal JsonObject ToJson() => new()
        {
            ["milliseconds"] = Milliseconds,
            ["allocated_bytes"] = Bytes,
            ["gc_collections"] = new JsonArray(Gen0, Gen1, Gen2)
        };
    }

    private static object? retained;

    internal static bool TryRun(string[] args)
    {
        if (args.Length == 0 || args[0] != "--compiler-tail") return false;
        if (args.Length != 4)
            throw new ArgumentException("Usage: --compiler-tail SOURCE LEVEL SAMPLES");
        string sourcePath = Path.GetFullPath(args[1]);
        string source = File.ReadAllText(sourcePath);
        int level = int.Parse(args[2], CultureInfo.InvariantCulture);
        int samples = int.Parse(args[3], CultureInfo.InvariantCulture);
        if (samples < 1) throw new ArgumentOutOfRangeException(nameof(samples));
        const string sourceName = "compiler-tail.msync";

        JsonArray phases = [];
        var analyze = Measure(() => SourceCompiler.Analyze(source, sourceName), "analyze", samples);
        phases.Add(analyze.Report);
        HirModule hir = analyze.Result;
        var lower = Measure(() => HirLowerer.Lower(hir,
            new HirLoweringOptions(level, RetainLayout: false)).Module, "lower_unoptimized", samples);
        phases.Add(lower.Report);
        Module module = lower.Result;
        var optimize = Measure(() => Optimizer.Optimize(module, level), "optimize", samples);
        phases.Add(optimize.Report);

        JsonObject assemblies = new();
        foreach (string path in Directory.GetFiles(AppContext.BaseDirectory, "Msync*.dll").Order(StringComparer.Ordinal))
            assemblies[Path.GetFileName(path)] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        Console.WriteLine(new JsonObject
        {
            ["stage"] = "compiler_tail",
            ["interval"] = "One synchronous phase invocation; input, counters and reporting outside interval",
            ["percentiles"] = "Nearest rank, ceil(N*p); each cold first call is separate from warm observations",
            ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["source_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
            ["source_bytes"] = Encoding.UTF8.GetByteCount(source),
            ["optimization"] = level,
            ["assemblies"] = assemblies,
            ["hir_nodes"] = hir.Nodes.Length,
            ["hir_locations"] = hir.Locations.Length,
            ["unoptimized_instructions"] = module.Functions.Sum(function => function.Instructions.Count),
            ["optimized_instructions"] = optimize.Result.Functions.Sum(function => function.Instructions.Count),
            ["optimized_functions"] = optimize.Result.Functions.Count,
            ["unoptimized_shape"] = Shape(module),
            ["optimized_shape"] = Shape(optimize.Result),
            ["phases"] = phases
        }.ToJsonString());
        GC.KeepAlive(retained);
        return true;
    }

    private static JsonObject Shape(Module module) => new()
    {
        ["locals"] = module.Functions.Sum(function => function.LocalCount),
        ["opcodes"] = new JsonObject(module.Functions.SelectMany(function => function.Instructions)
            .GroupBy(instruction => instruction.Op).OrderBy(group => group.Key.ToString(), StringComparer.Ordinal)
            .Select(group => KeyValuePair.Create<string, JsonNode?>(group.Key.ToString(), JsonValue.Create(group.Count()))))
    };

    private static (T Result, JsonObject Report) Measure<T>(Func<T> create, string phase, int samples)
        where T : class
    {
        (T result, Sample cold) = Once(create);
        const int warmups = 5;
        for (int index = 0; index < warmups; index++) retained = create();
        Sample[] observations = new Sample[samples];
        double[] times = new double[samples];
        long[] allocations = new long[samples];
        for (int index = 0; index < samples; index++)
        {
            var sample = Once(create);
            result = sample.Result;
            times[index] = sample.Observation.Milliseconds;
            allocations[index] = sample.Observation.Bytes;
            observations[index] = sample.Observation;
        }
        Array.Sort(times);
        Array.Sort(allocations);
        return (result, new JsonObject
        {
            ["phase"] = phase,
            ["cold"] = cold.ToJson(),
            ["warmups"] = warmups,
            ["samples"] = samples,
            ["p50_ms"] = Percentile(times, 0.50),
            ["p95_ms"] = Percentile(times, 0.95),
            ["p99_ms"] = Percentile(times, 0.99),
            ["max_ms"] = times[^1],
            ["allocated_bytes_p50"] = allocations[(int)Math.Ceiling(samples * 0.50) - 1],
            ["observations"] = new JsonArray(observations.Select(sample => sample.ToJson()).ToArray())
        });
    }

    private static (T Result, Sample Observation) Once<T>(Func<T> create)
        where T : class
    {
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        T result = create();
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        int delta0 = GC.CollectionCount(0) - gen0;
        int delta1 = GC.CollectionCount(1) - gen1;
        int delta2 = GC.CollectionCount(2) - gen2;
        retained = result;
        return (result, new(elapsed, bytes, delta0, delta1, delta2));
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];
}
