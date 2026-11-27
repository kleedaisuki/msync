using Msync.Optimization;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;
using Msync.Probes;
using Msync.Runtime;
using SourceCompiler = Msync.Compiler.Compiler;

namespace Msync.Components;

internal sealed record PipelineOptions(string Root, string Output, int[] Sizes, int Samples, int Warmups, string[] Cases, int[] Levels)
{
    internal static PipelineOptions Parse(string[] args)
    {
        string root = Environment.CurrentDirectory;
        string output = Path.Combine(root, ".temp/optimizer-performance/components.json");
        int[] sizes = [1, 32, 128];
        int samples = 15, warmups = 3;
        string[] cases = ["calls", "scope_calls", "finite_loop", "branch_reuse"];
        int[] levels = [0, 1, 2, 3];
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] == "--optimizer-pipeline") continue;
            string option = args[index], value = args[++index];
            switch (option)
            {
                case "--root": root = Path.GetFullPath(value); break;
                case "--output": output = Path.GetFullPath(value); break;
                case "--sizes": sizes = Integers(value); break;
                case "--samples": samples = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--warmups": warmups = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "--cases": cases = value.Split(','); break;
                case "--levels": levels = Integers(value); break;
                default: throw new ArgumentException($"Unknown optimizer probe option '{option}'");
            }
        }
        if (sizes.Any(size => size < 1) || samples < 1 || warmups < 0 || levels.Any(level => level is < 0 or > 3))
            throw new ArgumentException("Sizes and samples must be positive, warmups nonnegative and levels within O0..O3.");
        return new(root, output, sizes, samples, warmups, cases, levels);
    }

    private static int[] Integers(string value) => value.Split(',').Select(item => int.Parse(item, CultureInfo.InvariantCulture)).ToArray();
}

internal sealed record PipelineIdentity(string Executable, string Sha256, string CompilerBuild, string CoreBuild, string RuntimeBuild,
    string Framework, string Architecture, string? JitPolicy);
internal sealed record PipelineShape(string Workload, int Size, int Level, string Stage, int SourceBytes, int Constants, int Functions,
    int Instructions, int Locals, int? Blocks, int ArtifactBytes, IReadOnlyDictionary<string, int> Opcodes);
internal sealed record PipelineObservation(string Workload, int Size, int Level, string Stage, int IterationsPerSample,
    double[] Milliseconds, double P50Milliseconds, double P95Milliseconds, double AllocatedBytesPerOperation, int[] Collections);
internal sealed record PipelineReport(PipelineIdentity Image, int Samples, int Warmups, PipelineShape[] Shapes, PipelineObservation[] Observations);

[JsonSerializable(typeof(PipelineReport))]
internal sealed partial class PipelineJsonContext : JsonSerializerContext;

internal static class OptimizerPipelineProbe
{
    private static object? retained;

    internal static bool TryRun(string[] args)
    {
        if (!args.Contains("--optimizer-pipeline", StringComparer.Ordinal)) return false;
        PipelineOptions options = PipelineOptions.Parse(args);
        Directory.CreateDirectory(Path.GetDirectoryName(options.Output)!);
        string executable = Environment.ProcessPath!;
        PipelineIdentity identity = new(executable, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))),
            Build(typeof(SourceCompiler)), Build(typeof(NativeOptimizer)), Build(typeof(NativeInterpreter)),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), Environment.GetEnvironmentVariable("MSYNC_JIT"));
        List<PipelineShape> shapes = [];
        List<PipelineObservation> observations = [];
        foreach (string workload in options.Cases)
            foreach (int size in options.Sizes)
                Profile(options, workload, size, shapes, observations);
        PipelineReport report = new(identity, options.Samples, options.Warmups, shapes.ToArray(), observations.ToArray());
        File.WriteAllText(options.Output, JsonSerializer.Serialize(report, PipelineJsonContext.Default.PipelineReport));
        GC.KeepAlive(retained);
        return true;
    }

    private static void Profile(PipelineOptions options, string workload, int size, List<PipelineShape> shapes, List<PipelineObservation> observations)
    {
        bool handIr = workload == "call_dag";
        bool transfers = workload == "transfers";
        bool memory = MemoryPipelineWorkloads.IsMemory(workload);
        MemoryPipelineWorkloads.Prepare(workload, options.Root);
        string source = handIr ? "" : Source(workload, size);
        string sourceName = $"optimizer-{workload}-{size}.msync";
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(options.Output)!, sourceName), source);
        NativeProgram raw = handIr ? CallDag(size) : SourceCompiler.Compile(source, sourceName, 0);
        BindingContext binding = new(options.Root, options.Root, Path.Combine(options.Root, sourceName));
        ExecutionOptions runtime = new(WorkingDirectory: options.Root, SourceFile: binding.SourceFile, Output: TextWriter.Null, Error: TextWriter.Null);
        foreach (int level in options.Levels)
        {
            NativeProgram optimized = handIr ? NativeOptimizer.Optimize(raw, level) : SourceCompiler.Compile(source, sourceName, level);
            NativeProgram executable = NativeProbe.Prepare(optimized, binding);
            if (transfers)
            {
                NativeArtifact.Validate(optimized);
                NativeArtifact.Validate(executable);
                NativeArtifact.Validate(RoundTrip(optimized));
                NativeArtifact.Validate(RoundTrip(executable));
            }
            else
            {
                VerifyResult(optimized);
                VerifyResult(executable);
                VerifyResult(RoundTrip(optimized));
                VerifyResult(RoundTrip(executable));
            }
            shapes.Add(Shape(workload, size, level, "relocatable", source, optimized));
            shapes.Add(Shape(workload, size, level, "executable", source, executable));
            (string Name, Func<object> Run)[] stages =
            [
                ("compile", () => SourceCompiler.Compile(source, sourceName, level)),
                ("optimize", () => NativeOptimizer.Optimize(raw, level)),
                ("bind", () => NativeProbe.Prepare(optimized, binding)),
                ("run_relocatable", () => Execute(optimized)),
                ("run_executable", () => Execute(executable))
            ];
            foreach (var stage in stages)
            {
                if (handIr && stage.Name == "compile") continue;
                if (transfers && stage.Name.StartsWith("run_", StringComparison.Ordinal)) continue;
                PipelineObservation observation = Measure(options, workload, size, level, stage.Name, stage.Run);
                observations.Add(observation);
                Console.WriteLine($"{workload,-13} n={size,-4} O{level} {stage.Name,-15} p50={observation.P50Milliseconds:F4} ms p95={observation.P95Milliseconds:F4} ms alloc={observation.AllocatedBytesPerOperation:F0} B/op");
            }
        }

        object Execute(NativeProgram module) => memory ? MemoryPipelineWorkloads.Execute(module, runtime, size, workload)
            : NativeProbe.RunAsync(module, runtime).GetAwaiter().GetResult();
        void VerifyResult(NativeProgram module)
        {
            if (memory) MemoryPipelineWorkloads.Verify(module, runtime, size, workload);
            else Verify(module, runtime, size, handIr);
        }
    }

    private static PipelineShape Shape(string workload, int size, int level, string stage, string source, NativeProgram module) =>
        new(workload, size, level, stage, Encoding.UTF8.GetByteCount(source), module.Constants.Count, module.Functions.Count,
            module.Functions.Sum(function => function.Instructions.Count), module.Functions.Sum(function => function.Slots.Count), null,
            Encode(module).Length, module.Functions.SelectMany(function => function.Instructions).GroupBy(instruction => instruction.Op.ToString())
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));

    private static PipelineObservation Measure(PipelineOptions options, string workload, int size, int level, string stage, Func<object> operation)
    {
        long start = Stopwatch.GetTimestamp();
        retained = operation();
        double calibration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        int iterations = Math.Clamp((int)Math.Ceiling(2 / Math.Max(0.001, calibration)), 1, 256);
        for (int warmup = 0; warmup < options.Warmups; warmup++) Run(operation, iterations);
        int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        double[] values = new double[options.Samples];
        for (int sample = 0; sample < values.Length; sample++)
        {
            start = Stopwatch.GetTimestamp();
            Run(operation, iterations);
            values[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        }
        for (int index = 0; index < collections.Length; index++) collections[index] = GC.CollectionCount(index) - collections[index];
        long before = GC.GetTotalAllocatedBytes(precise: true);
        Run(operation, iterations);
        double bytes = (double)(GC.GetTotalAllocatedBytes(precise: true) - before) / iterations;
        double[] sorted = values.Order().ToArray();
        return new(workload, size, level, stage, iterations, values, Percentile(sorted, 0.5), Percentile(sorted, 0.95), bytes, collections);
    }

    private static void Run(Func<object> operation, int iterations)
    {
        for (int index = 0; index < iterations; index++) retained = operation();
    }

    private static double Percentile(double[] sorted, double quantile)
    {
        double position = (sorted.Length - 1) * quantile;
        int lower = (int)position, upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static void Verify(NativeProgram module, ExecutionOptions runtime, int size, bool handIr)
    {
        NativeValue result = NativeProbe.RunAsync(module, runtime).GetAwaiter().GetResult();
        if (handIr)
        {
            if (result.Kind != NativeKind.Number || result.AsNumber() != 3 * Math.Pow(2, size))
                throw new InvalidOperationException($"Call DAG returned '{result}', expected {3 * Math.Pow(2, size)}");
            return;
        }
        if (result.Kind != NativeKind.Path || !result.AsPath().Raw.EndsWith($"input_{size - 1}", StringComparison.Ordinal))
            throw new InvalidOperationException($"Optimizer pipeline returned '{result}', expected Path ending input_{size - 1}");
    }

    private static byte[] Encode(NativeProgram module)
    {
        using MemoryStream stream = new();
        NativeArtifact.Write(module, stream);
        return stream.ToArray();
    }

    private static NativeProgram RoundTrip(NativeProgram module)
    {
        using MemoryStream stream = new(Encode(module), writable: false);
        return NativeArtifact.Read(stream);
    }

    private static NativeProgram CallDag(int depth)
    {
        List<NativeFunctionCode> functions =
        [new("main", [new(0), new(1), new(0)],
            [new(NativeOp.MakeFunction, 1, depth + 1, C: 0), new(NativeOp.Constant, 0, 0),
             new(NativeOp.Call, 2, 1, depth + 1, Inputs: [0]), new(NativeOp.Return, A: 2)], new([], 0, [])),
         new("f0", [new(0, NativeSlotRole.Parameter), new(0), new(0)],
            [new(NativeOp.Constant, 1, 1), new(NativeOp.Binary, 2, 0, 1, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 2)],
            new([0], 0, []), ParameterCount: 1)];
        for (int index = 1; index <= depth; index++)
            functions.Add(new($"f{index}", [new(0, NativeSlotRole.Parameter), new(1), new(0), new(0), new(0)],
                [new(NativeOp.MakeFunction, 1, index, C: 0),
                 new(NativeOp.Call, 2, 1, index, Inputs: [0]), new(NativeOp.Call, 3, 1, index, Inputs: [0]),
                 new(NativeOp.Binary, 4, 2, 3, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 4)],
                new([0], 0, []), ParameterCount: 1));
        return new([new(NativeKind.Number, Number: 2), new(NativeKind.Number, Number: 1)], functions,
            [new(TypeKind.Number), new(TypeKind.Function)], []);
    }

    private static string Source(string workload, int size)
    {
        if (MemoryPipelineWorkloads.TrySource(workload, size, out string memory)) return memory;
        StringBuilder text = new();
        if (workload == "finite_sum")
        {
            text.Append("fn sum() { let value = 0; for item in [");
            text.AppendJoin(",", Enumerable.Range(1, size));
            text.AppendLine("] { value = value + item; }; value };");
            string expected = ((double)size * (size + 1) / 2).ToString(CultureInfo.InvariantCulture);
            return text.Append($"if sum() == {expected} {{ ./input_{size - 1} }} else {{ ./incorrect }}").ToString();
        }
        if (workload == "transfers")
        {
            for (int index = 0; index < size; index++) text.AppendLine($"./source_{index} -> ./destination_{index};");
            return text.ToString();
        }
        if (workload == "mutable_scopes")
        {
            text.AppendLine("object Factory { create: fn { object { filename?: null } } };");
            for (int index = 0; index < size; index++)
                text.AppendLine($"let object_{index} = Factory::create(); object_{index}::filename = ./input_{index};");
            return text.Append($"object_{size - 1}::filename").ToString();
        }
        if (workload == "scope_calls")
        {
            text.AppendLine("object Factory { create: fn(file: Path) { object { get: fn { file } } } };");
            for (int index = 0; index < size; index++) text.AppendLine($"let object_{index} = Factory::create(./input_{index});");
            return text.Append($"object_{size - 1}::get()").ToString();
        }
        if (workload == "branch_reuse")
            text.AppendLine("fn route(file: Path, chosen: Bool) { let result = if chosen { ./root / file } else { ./root / file }; if chosen { result } else { result } };");
        else if (workload is "calls" or "finite_loop") text.AppendLine("fn route(file: Path) { ./root / file };");
        else throw new ArgumentException($"Unknown optimizer workload '{workload}'");
        if (workload == "finite_loop")
        {
            text.Append("for file in [");
            text.AppendJoin(", ", Enumerable.Range(0, size).Select(index => $"./input_{index}"));
            return text.Append($"] {{ route(file); }}; route(./input_{size - 1})").ToString();
        }
        string argument = workload == "branch_reuse" ? ", true" : "";
        for (int index = 0; index < size; index++) text.AppendLine($"let value_{index} = route(./input_{index}{argument});");
        return text.Append($"value_{size - 1}").ToString();
    }

    private static string Build(Type type) => type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
}
