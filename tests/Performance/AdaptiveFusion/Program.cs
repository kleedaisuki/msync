using Msync.Optimization;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Core;
using Msync.Runtime;
using Compiler = Msync.Compiler.Compiler;
using Module = Msync.Core.Module;

internal sealed record Observation(string Scenario, int Size, string Stage, int Sites, int Instructions,
    int Iterations, double[] Milliseconds, double P50Milliseconds, double P95Milliseconds, double AllocatedBytes);
internal sealed record Report(string Executable, string Sha256, string RuntimeVersion, Observation[] Observations, int Checks);
[JsonSerializable(typeof(Report))]
internal sealed partial class ReportJson : JsonSerializerContext;

internal static class Program
{
    private static object? retained;
    private static int checks;

    private static int Main(string[] args)
    {
        string root = Path.GetFullPath(Option(args, "--root", Path.Combine(Environment.CurrentDirectory, ".temp/runtime-adaptive/fusion/work")));
        Directory.CreateDirectory(root);
        int samples = int.Parse(Option(args, "--samples", "11"), CultureInfo.InvariantCulture);
        int[] sizes = Option(args, "--sizes", "1,32,4096").Split(',').Select(int.Parse).ToArray();
        bool verify = args.Contains("--verify", StringComparer.Ordinal);
        List<Observation> observations = [];
        foreach (string scenario in Option(args, "--cases", "sum,captured,last,literal,copy").Split(','))
            foreach (int size in sizes)
            {
                if ((scenario is "copy" or "literal") && size != sizes[0]) continue;
                Profile(root, scenario, size, samples, verify, observations);
            }
        ValidateFallback(root);
        string executable = Environment.ProcessPath!;
        Report report = new(executable, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))),
            typeof(Interpreter).Assembly.GetName().Version!.ToString(), observations.ToArray(), checks);
        string output = Path.GetFullPath(Option(args, "--output", Path.Combine(root, "results.json")));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, ReportJson.Default.Report));
        Console.WriteLine($"PASS: {checks} execution/codec/shape/error contracts; output={output}");
        return 0;
    }

    private static string Option(string[] args, string name, string fallback)
    {
        int index = Array.IndexOf(args, name);
        return index < 0 ? fallback : args[index + 1];
    }

    private static void Profile(string root, string scenario, int size, int samples, bool verify, List<Observation> observations)
    {
        if (scenario == "copy") File.WriteAllBytes(Path.Combine(root, "input"), Enumerable.Range(0, 4096).Select(index => (byte)index).ToArray());
        string source = Source(scenario, size);
        Module input = Compiler.Compile(source, $"fusion-{scenario}-{size}.msync", 2);
        Module module = Binding.Bind(input, new(root, root));
        var options = new ExecutionOptions(WorkingDirectory: root, Output: TextWriter.Null, Error: TextWriter.Null);
        int sites = Sites(module);
        Check(scenario is not ("sum" or "captured") || sites > 0, $"{scenario}: O2 eliminated the proposed fusion pattern");
        Check(scenario is not ("last" or "literal" or "copy") || sites == 0, $"{scenario}: negative control unexpectedly has fusion sites");
        CheckResult(Interpreter.ExecuteAsync(input, options).GetAwaiter().GetResult(), scenario, size, root);
        CheckResult(Interpreter.ExecuteAsync(module, options).GetAwaiter().GetResult(), scenario, size, root);
        CheckResult(Interpreter.ExecuteAsync(RoundTrip(input), options).GetAwaiter().GetResult(), scenario, size, root);
        CheckResult(Interpreter.ExecuteAsync(RoundTrip(module), options).GetAwaiter().GetResult(), scenario, size, root);
        if (verify) return;
        var interpreter = new Interpreter(module, options);
        (string Name, Func<object> Run)[] stages =
        [
            ("prepare", () => new Interpreter(module, options)),
            ("prepared_vm", () => interpreter.ExecuteAsync().GetAwaiter().GetResult()),
            ("first_execution", () => Interpreter.ExecuteAsync(module, options).GetAwaiter().GetResult())
        ];
        foreach (var stage in stages)
        {
            var observation = Measure(scenario, size, stage.Name, sites, module.Functions.Sum(function => function.Instructions.Count), samples, stage.Run);
            observations.Add(observation);
            Console.WriteLine($"{scenario} n={size} {stage.Name}: {observation.P50Milliseconds:F6} ms, {observation.AllocatedBytes:F0} B");
        }
        CheckResult(interpreter.ExecuteAsync().GetAwaiter().GetResult(), scenario, size, root);
    }

    private static string Source(string scenario, int size)
    {
        string items = string.Join(",", Enumerable.Range(1, size).Select(index => index % 17 + 1));
        return scenario switch
        {
            "sum" => $"fn sum(values: Range) {{ let total = 0; for item in values {{ total = total + item; }}; total }}; sum([{items}])",
            "captured" => $"let total = 0; fn sum(values: Range) {{ global total; for item in values {{ total = total + item; }}; total }}; sum([{items}])",
            "last" => $"fn last(values: Range) {{ let value = 0; for item in values {{ value = item; }}; value }}; last([{items}])",
            "literal" => "42",
            "copy" => "./input -> ./output;",
            _ => throw new ArgumentException($"Unknown scenario '{scenario}'")
        };
    }

    private static int Sites(Module module)
    {
        int sites = 0;
        foreach (var block in module.Blocks!)
        {
            var code = module.Functions[block.Function].Instructions;
            for (int pc = block.Start; pc + 2 < block.End; pc++)
                if (code[pc].Op == OpCode.LoadLocal && code[pc + 1].Op == OpCode.LoadLocal && code[pc + 2].Op == OpCode.Add) sites++;
        }
        return sites;
    }

    private static void CheckResult(Value result, string scenario, int size, string root)
    {
        if (scenario == "copy")
        {
            Check(File.ReadAllBytes(Path.Combine(root, "input")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, "output"))), "Copied payload changed");
            return;
        }
        double expected = scenario == "literal" ? 42 : scenario == "last" ? size % 17 + 1 : Enumerable.Range(1, size).Sum(index => index % 17 + 1);
        Check(result is NumberValue number && number.Data == expected, $"{scenario}: expected {expected}, got {result}");
    }

    private static Module RoundTrip(Module module)
    {
        using MemoryStream bytes = new();
        Artifact.Write(module, bytes); bytes.Position = 0;
        return Artifact.Read(bytes);
    }

    private static void ValidateFallback(string root)
    {
        var options = new ExecutionOptions(WorkingDirectory: root);
        foreach ((Value left, Value right, Value expected) in new (Value, Value, Value)[]
        {
            (new StringValue("left"), new StringValue("right"), new StringValue("leftright")),
            (new NumberValue(-0.0), new NumberValue(-0.0), new NumberValue(-0.0)),
            (new NumberValue(double.PositiveInfinity), new NumberValue(double.NegativeInfinity), new NumberValue(double.NaN))
        })
        {
            Module module = AddModule(left, right, root);
            Value actual = Interpreter.ExecuteAsync(module, options).GetAwaiter().GetResult();
            bool same = expected is NumberValue e && actual is NumberValue a
                ? double.IsNaN(e.Data) ? double.IsNaN(a.Data) : BitConverter.DoubleToInt64Bits(e.Data) == BitConverter.DoubleToInt64Bits(a.Data)
                : Equals(expected, actual);
            Check(same, $"Generic fallback or IEEE result changed: {left} + {right}");
            Check(module.Functions[0].Instructions[6].Op == OpCode.Add, "Experimental execution mutated canonical bytecode");
        }
        Module failure = AddModule(new ExplodingType(), new NumberValue(1), root);
        try { _ = Interpreter.ExecuteAsync(failure, options).GetAwaiter().GetResult(); throw new InvalidOperationException("Missing selected type failure"); }
        catch (MsyncException error)
        {
            Check(error.Message == "selected marker", "Selected failure changed");
            Check(error.Span == failure.Functions[0].Instructions[6].Span, "Fused failure lost original Add span");
        }
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        try { _ = Interpreter.ExecuteAsync(AddModule(new NumberValue(1), new NumberValue(2), root), options, cancellation.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("Missing cancellation"); }
        catch (OperationCanceledException) { checks++; }
    }

    private sealed record ExplodingType : Value
    {
        public override TypeRef Type => throw new MsyncException("selected marker");
    }

    private static Module AddModule(Value left, Value right, string root) => BlockBuilder.Form(new([left, right],
        [new("add", 0, 2,
        [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.Constant, 1), new(OpCode.InitializeLocal, 1),
         new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 1), new(OpCode.Add, Span: new("selected-fusion-error.msync", 12, 3)), new(OpCode.Return)], TypeRef.Any)],
        Stage: ArtifactStage.Executable, Context: new(root, root), OptimizationLevel: 2));

    private static Observation Measure(string scenario, int size, string stage, int sites, int instructions, int samples, Func<object> operation)
    {
        long start = Stopwatch.GetTimestamp(); retained = operation();
        double calibration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        int iterations = Math.Clamp((int)Math.Ceiling(2 / Math.Max(.001, calibration)), 1, 256);
        for (int warmup = 0; warmup < 3; warmup++) Run(operation, iterations);
        double[] values = new double[samples];
        for (int sample = 0; sample < samples; sample++)
        {
            start = Stopwatch.GetTimestamp(); Run(operation, iterations);
            values[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        }
        long before = GC.GetTotalAllocatedBytes(precise: true); Run(operation, iterations);
        double bytes = (double)(GC.GetTotalAllocatedBytes(precise: true) - before) / iterations;
        double[] sorted = values.Order().ToArray();
        GC.KeepAlive(retained);
        return new(scenario, size, stage, sites, instructions, iterations, values, Percentile(sorted, .5), Percentile(sorted, .95), bytes);
    }

    private static void Run(Func<object> operation, int iterations)
    {
        for (int index = 0; index < iterations; index++) retained = operation();
    }

    private static double Percentile(double[] values, double percentile)
    {
        double position = (values.Length - 1) * percentile;
        int first = (int)position, second = Math.Min(first + 1, values.Length - 1);
        return values[first] + (values[second] - values[first]) * (position - first);
    }

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
