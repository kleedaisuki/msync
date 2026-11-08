using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;
using SourceCompiler = Msync.Compiler.Compiler;

namespace Msync.Components;

internal sealed record Options(string Root, string Output, int[] Sizes, int Samples, int Warmups, string[]? Stages)
{
    public static Options Parse(string[] args)
    {
        string root = Environment.CurrentDirectory;
        string? output = null;
        int[] sizes = [1, 128, 1000];
        int samples = 31, warmups = 3;
        string[]? stages = null;
        for (int i = 0; i < args.Length; i++)
        {
            string value = args[++i];
            switch (args[i - 1])
            {
                case "--root": root = Path.GetFullPath(value); break;
                case "--output": output = Path.GetFullPath(value); break;
                case "--sizes": sizes = value.Split(',').Select(ParseInt).ToArray(); break;
                case "--samples": samples = ParseInt(value); break;
                case "--warmups": warmups = ParseInt(value); break;
                case "--stages": stages = value.Split(','); break;
                default: throw new ArgumentException($"Unknown option '{args[i - 1]}'");
            }
        }
        output ??= Path.Combine(root, ".temp/performance-v080/current/components/results.json");
        return new(root, output, sizes, samples, warmups, stages);
    }

    private static int ParseInt(string text) => int.Parse(text, CultureInfo.InvariantCulture);
}

internal sealed record ImageIdentity(string Executable, string Sha256, string CheckoutCommitAtRun, string CompilerBuild,
    string CoreBuild, string RuntimeBuild, string Framework, string Architecture);
internal sealed record InputShape(string Workload, int Size, int SourceBytes, int Constants, int Functions, int Instructions, int Locals, int Blocks, int ArtifactBytes);
internal sealed record Observation(string Workload, int Size, string Stage, int IterationsPerSample, double[] Microseconds,
    double P50Microseconds, double P95Microseconds, double MinMicroseconds, double MaxMicroseconds,
    double AllocatedBytesPerOperation, int[] Collections);
internal sealed record Report(ImageIdentity Image, int Samples, int Warmups, InputShape[] Inputs, Observation[] Observations);

[JsonSerializable(typeof(Report))]
internal sealed partial class ReportJsonContext : JsonSerializerContext;

internal static class Program
{
    private static object? retained;

    public static int Main(string[] args)
    {
        if (SourceReadProbe.TryRun(args)) return 0;
        if (args.Contains("--check-interner", StringComparer.Ordinal)) { InternerChecks.Run(); return 0; }
        if (args.Contains("--check-snapshots", StringComparer.Ordinal)) { SnapshotChecks.Run(); return 0; }
        if (args.Contains("--emit-capture", StringComparer.Ordinal))
        {
            using Stream output = Console.OpenStandardOutput();
            SnapshotChecks.WriteCapture(output);
            return 0;
        }
        Options options = Options.Parse(args);
        Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
        string executable = Environment.ProcessPath!;
        ImageIdentity image = new(executable, Digest(executable), Commit(options.Root),
            Build(typeof(SourceCompiler)), Build(typeof(Artifact)), Build(typeof(Interpreter)),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
        List<InputShape> inputs = [];
        List<Observation> observations = [];
        foreach (string workload in new[] { "transfers", "generic_values", "scope_values" })
            foreach (int size in options.Sizes)
                Profile(options, workload, size, inputs, observations);
        Report report = new(image, options.Samples, options.Warmups, inputs.ToArray(), observations.ToArray());
        File.WriteAllText(options.Output, JsonSerializer.Serialize(report, ReportJsonContext.Default.Report));
        Console.WriteLine(options.Output);
        GC.KeepAlive(retained);
        return 0;
    }

    private static void Profile(Options options, string workload, int size, List<InputShape> inputs, List<Observation> observations)
    {
        string source = Source(workload, size);
        string sourceName = $"{workload}-{size}.msync";
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(options.Output)!, sourceName), source);
        Module raw = SourceCompiler.Compile(source, sourceName, 0);
        Module optimized = SourceCompiler.Compile(source, sourceName, 1);
        byte[] encoded = Encode(optimized);
        BindingContext binding = new(options.Root, options.Root, Path.Combine(options.Root, sourceName));
        Module bound = Binding.Bind(optimized, binding);
        ExecutionOptions runtime = new(WorkingDirectory: options.Root, SourceFile: binding.SourceFile, Output: TextWriter.Null, Error: TextWriter.Null);
        inputs.Add(new(workload, size, Encoding.UTF8.GetByteCount(source), raw.Constants.Count, raw.Functions.Count,
            raw.Functions.Sum(f => f.Instructions.Count), raw.Functions.Sum(f => f.LocalCount), raw.Blocks?.Count ?? 0, encoded.Length));
        List<(string Name, Func<object> Run)> stages =
        [
            ("parse", () => Parser.Parse(source, sourceName)),
            ("compile_o0", () => SourceCompiler.Compile(source, sourceName, 0)),
            ("compile_o1", () => SourceCompiler.Compile(source, sourceName, 1)),
            ("optimize_o1", () => Optimizer.Optimize(raw, 1)),
            ("form_blocks", () => Optimizer.FormBlocks(raw)),
            ("artifact_validate", () => { Artifact.Validate(optimized); return optimized; }),
            ("artifact_encode", () => Encode(optimized)),
            ("artifact_decode", () => Decode(encoded)),
            ("bind_and_lower", () => Binding.Bind(optimized, binding)),
            ("execution_lower", () => ExecutionLowering.Lower(bound))
        ];
        if (workload != "transfers")
        {
            Value result = Interpreter.ExecuteAsync(bound, runtime).GetAwaiter().GetResult();
            if (result is not PathValue path || !path.Raw.EndsWith($"input_{size - 1}", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected no-I/O result: {result}");
            stages.Add(("vm_no_io", () => Interpreter.ExecuteAsync(bound, runtime).GetAwaiter().GetResult()));
        }
        foreach (var stage in stages)
        {
            if (options.Stages is not null && !options.Stages.Contains(stage.Name, StringComparer.Ordinal)) continue;
            Observation measurement = Measure(options, workload, size, stage.Name, stage.Run);
            observations.Add(measurement);
            Console.WriteLine($"{workload,-14} {size,4} {stage.Name,-18} p50={measurement.P50Microseconds,10:F3} us p95={measurement.P95Microseconds,10:F3} us alloc={measurement.AllocatedBytesPerOperation,12:F0} B/op");
        }
    }

    private static Observation Measure(Options options, string workload, int size, string stage, Func<object> operation)
    {
        long start = Stopwatch.GetTimestamp();
        retained = operation();
        double calibration = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        int iterations = Math.Clamp((int)Math.Ceiling(2000 / Math.Max(1, calibration)), 1, 256);
        for (int i = 0; i < options.Warmups; i++) Run(operation, iterations);
        int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        double[] times = new double[options.Samples];
        for (int sample = 0; sample < times.Length; sample++)
        {
            start = Stopwatch.GetTimestamp();
            Run(operation, iterations);
            times[sample] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / iterations;
        }
        for (int i = 0; i < collections.Length; i++) collections[i] = GC.CollectionCount(i) - collections[i];
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        Run(operation, iterations);
        double bytes = (double)(GC.GetTotalAllocatedBytes(precise: true) - allocated) / iterations;
        double[] sorted = times.Order().ToArray();
        return new(workload, size, stage, iterations, times, Percentile(sorted, 0.50), Percentile(sorted, 0.95), sorted[0], sorted[^1], bytes, collections);
    }

    private static void Run(Func<object> operation, int iterations)
    {
        for (int i = 0; i < iterations; i++) retained = operation();
    }

    private static double Percentile(double[] sorted, double fraction) => sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];

    private static byte[] Encode(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        return stream.ToArray();
    }

    private static Module Decode(byte[] bytes)
    {
        using MemoryStream stream = new(bytes, writable: false);
        return Artifact.Read(stream);
    }

    private static string Source(string workload, int size)
    {
        StringBuilder text = new();
        if (workload == "transfers")
        {
            for (int i = 0; i < size; i++) text.AppendLine($"./source_{i} -> ./destination_{i};");
            return text.ToString();
        }
        bool scoped = workload == "scope_values";
        text.AppendLine(scoped ? "scope routes { route: fn(file: Path) { ./root / file }, };" : "fn route(file: Path) { ./root / file };");
        string function = scoped ? "routes::route" : "route";
        for (int i = 0; i < size; i++) text.AppendLine($"let value_{i} = {function}(./input_{i});");
        text.Append($"value_{size - 1}");
        return text.ToString();
    }

    private static string Digest(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Build(Type type) => type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    private static string Commit(string root)
    {
        ProcessStartInfo info = new("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("rev-parse"); info.ArgumentList.Add("HEAD");
        using Process process = Process.Start(info)!;
        string result = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Cannot identify compiler source commit");
        return result;
    }
}
