using System.Diagnostics;
using System.Globalization;
using Msync.Compiler;
using Msync.Compiler.Syntax;

namespace Msync.Components;

internal static class EditorAnalysisProbe
{
    private static object? retained;

    public static bool TryRun(string[] args)
    {
        if (!args.Contains("--editor-analysis", StringComparer.Ordinal)) return false;
        Options options = Options.Parse(args.Where(arg => arg != "--editor-analysis").ToArray());
        Console.WriteLine("workload,size,stage,p50_ms,p95_ms,allocated_bytes");
        foreach (string workload in new[] { "scalars", "paths", "scopes" })
            foreach (int size in options.Sizes)
                Measure(options, workload, size);
        GC.KeepAlive(retained);
        return true;
    }

    private static void Measure(Options options, string workload, int size)
    {
        string source = workload switch
        {
            "scalars" => string.Concat(Enumerable.Range(0, size).Select(index => $"let item_{index} = {index};\n")),
            "paths" => string.Concat(Enumerable.Range(0, size).Select(index => $"let item_{index} = ./input_{index};\n")),
            _ => "fn create(file: Path) { scope { file: file, copy: fn(des: Path) { file -> des } } };\n"
                + string.Concat(Enumerable.Range(0, size).Select(index => $"let item_{index} = create(./input_{index});\n"))
        };
        EditorAnalysis analysis = EditorCompiler.Analyze(source, workload + ".msync");
        if (analysis.Diagnostic is { } diagnostic) throw new InvalidOperationException(diagnostic.EditorMessage);
        if (analysis.At(source.Length).Bindings.Count != size + (workload == "scopes" ? 1 : 0))
            throw new InvalidOperationException("The probe lost visible bindings.");
        (string Name, Func<object> Run)[] stages =
        [
            ("parse", () => Parser.ParseForEditor(source)),
            ("analyze", () => EditorCompiler.Analyze(source, workload + ".msync")),
            ("at_end", () => analysis.At(source.Length)),
            ("at_start", () => analysis.At(Math.Min(source.Length, 5)))
        ];
        foreach (var stage in stages)
        {
            if (options.Stages is not null && !options.Stages.Contains(stage.Name, StringComparer.Ordinal)) continue;
            for (int i = 0; i < options.Warmups; i++) retained = stage.Run();
            long bytes = 0;
            double[] elapsed = new double[options.Samples];
            for (int i = 0; i < elapsed.Length; i++)
            {
                long startBytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                retained = stage.Run();
                elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                bytes += GC.GetAllocatedBytesForCurrentThread() - startBytes;
            }
            Array.Sort(elapsed);
            Console.WriteLine(string.Join(',', workload, size, stage.Name,
                Percentile(elapsed, 0.5).ToString("F4", CultureInfo.InvariantCulture),
                Percentile(elapsed, 0.95).ToString("F4", CultureInfo.InvariantCulture),
                ((double)bytes / elapsed.Length).ToString("F0", CultureInfo.InvariantCulture)));
        }
    }

    private static double Percentile(double[] values, double fraction)
    {
        double rank = fraction * (values.Length - 1);
        int lower = (int)rank;
        return values[lower] + (values[Math.Min(lower + 1, values.Length - 1)] - values[lower]) * (rank - lower);
    }
}
