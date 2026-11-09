using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

internal sealed record Shape(string Name, int Iterations, int Functions, int Instructions, int Members, int UnhintedMembers, int[] MemberHints, string[] ConstructorLayouts, string Result);
internal sealed record Measurement(string Name, int Iterations, double[] Milliseconds, double P50, double P95, long AllocatedBytes);
internal sealed record Report(string Image, string Sha256, string CompilerBuild, string RuntimeBuild, Shape[] Shapes, Measurement[] Measurements);
[JsonSerializable(typeof(Report))]
internal sealed partial class ProbeJson : JsonSerializerContext;

internal static class Program
{
    private static object? retained;

    private static async Task Main(string[] args)
    {
        string mode = args.Length == 0 ? "--check" : args[0];
        if (mode == "--check") { await Check(); return; }
        if (mode != "--measure") throw new ArgumentException("Use --check or --measure OUTPUT [SAMPLES] [ITERATIONS].");
        int samples = args.Length > 2 ? int.Parse(args[2]) : 9;
        int count = args.Length > 3 ? int.Parse(args[3]) : 2048;
        List<Shape> shapes = [];
        List<Measurement> measurements = [];
        foreach (string name in new[] { "source-polymorphic", "source-hinted", "artifact-unhinted", "tiny", "literal", "file-copy" })
        {
            int iterations = name is "tiny" or "file-copy" ? 1 : count;
            Module module = Build(name, iterations);
            Value result = await Interpreter.ExecuteAsync(module);
            string expected = name == "literal" ? "literal" : name == "tiny" ? "left" : "right";
            Verify(name, expected, result);
            using MemoryStream encoded = new(); Artifact.Write(module, encoded); encoded.Position = 0;
            Verify(name, expected, await Interpreter.ExecuteAsync(Artifact.Read(encoded)));
            var members = module.Functions.SelectMany(function => function.Instructions).Where(instruction => instruction.Op == OpCode.GetMember).ToArray();
            shapes.Add(new(name, iterations, module.Functions.Count, module.Functions.Sum(function => function.Instructions.Count),
                members.Length, members.Count(instruction => instruction.A == 0), members.Select(instruction => instruction.A).ToArray(),
                module.Functions.SelectMany(function => function.Instructions).Where(instruction => instruction.Op == OpCode.MakeScope)
                    .Select(instruction => instruction.Text ?? "").ToArray(), result.ToString()!));
            for (int warmup = 0; warmup < 3; warmup++) retained = await Interpreter.ExecuteAsync(module);
            double[] times = new double[samples];
            for (int sample = 0; sample < samples; sample++)
            {
                long start = Stopwatch.GetTimestamp();
                retained = await Interpreter.ExecuteAsync(module);
                times[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            long before = GC.GetTotalAllocatedBytes(precise: true);
            retained = await Interpreter.ExecuteAsync(module);
            long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            double[] sorted = times.Order().ToArray();
            measurements.Add(new(name, iterations, times, Quantile(sorted, 0.5), Quantile(sorted, 0.95), allocated));
            Console.WriteLine($"{name}: {members.Length} GetMember ({members.Count(instruction => instruction.A == 0)} unhinted), p50 {Quantile(sorted, 0.5):F6} ms, {allocated} B");
        }
        string executable = Environment.ProcessPath!;
        Report report = new(executable, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))),
            typeof(Compiler).Assembly.GetName().Version!.ToString(), typeof(Interpreter).Assembly.GetName().Version!.ToString(), shapes.ToArray(), measurements.ToArray());
        File.WriteAllText(args[1], JsonSerializer.Serialize(report, ProbeJson.Default.Report));
        GC.KeepAlive(retained);
    }

    internal static Module Build(string name, int count)
    {
        if (name == "file-copy")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CopyPath())!);
            string text = "\"" + new string('x', 4096) + "\" -> \"" + CopyPath().Replace("\"", "\"\"", StringComparison.Ordinal) + "\";";
            return Binding.Bind(Compiler.Compile(text, "adaptive-file", 2), Context());
        }
        if (name == "literal") return Binding.Bind(Compiler.Compile("\"literal\"", "adaptive-literal", 2), Context());
        string first = name is "source-hinted" or "tiny" ? "item: \"left\", other: \"padding\"" : "other: \"padding\", item: \"left\"";
        string second = "item: \"right\", other: \"padding\"";
        string items = string.Join(", ", Enumerable.Range(0, count));
        string source = $$"""
            fn pick(left: Scope, right: Scope, choose: Bool) -> Scope {
                if choose { left } else { right }
            };
            fn read(left: Scope, right: Scope, items: Range<Number>) -> String {
                let result = "";
                for index in items {
                    let object = pick(left, right, index < {{Math.Max(1, count / 2)}});
                    result = object::item;
                };
                result
            };
            let left = scope { {{first}} };
            let right = scope { {{second}} };
            read(left, right, [{{items}}])
            """;
        Module module = Compiler.Compile(source, "adaptive-" + name, 2);
        if (name == "artifact-unhinted")
            module = module with
            {
                Functions = module.Functions.Select(function => function with
                { Instructions = function.Instructions.Select(instruction => instruction.Op == OpCode.GetMember ? instruction with { A = 0 } : instruction).ToArray() }).ToArray(),
                Blocks = null
            };
        module = Binding.Bind(module, Context());
        if (name != "literal" && !module.Functions.SelectMany(function => function.Instructions).Any(instruction => instruction.Op == OpCode.GetMember))
            throw new InvalidOperationException(name + ": O2 removed the field operation; not a valid runtime-cache workload.");
        return module;
    }

    private static string CopyPath() => Path.Combine(Environment.CurrentDirectory, ".temp/runtime-adaptive/inline-cache/io-control/output");
    private static void Verify(string name, string expected, Value result)
    {
        if (name != "file-copy") { Equal(expected, result, name); return; }
        if (result is not NullValue || File.ReadAllText(CopyPath()) != new string('x', 4096))
            throw new InvalidOperationException("File negative control changed its result or actual copied bytes.");
    }

    private static BindingContext Context() => new(Environment.CurrentDirectory, Environment.CurrentDirectory);
    private static double Quantile(double[] values, double quantile)
    {
        double position = (values.Length - 1) * quantile;
        int lower = (int)position, upper = Math.Min(lower + 1, values.Length - 1);
        return values[lower] + (values[upper] - values[lower]) * (position - lower);
    }

    private static void Equal(string expected, Value actual, string context)
    {
        if (actual is not StringValue text || text.Data != expected)
            throw new InvalidOperationException($"{context}: expected '{expected}', received '{actual}'");
    }

    private static Module Unhint(Module module) => module with
    {
        Functions = module.Functions.Select(function => function with
        { Instructions = function.Instructions.Select(instruction => instruction.Op == OpCode.GetMember ? instruction with { A = 0 } : instruction).ToArray() }).ToArray(),
        Blocks = null
    };

    private static async Task MutationAndFallback()
    {
        const string source = """
            fn inspect(box: Scope, items: Range<Number>) -> String {
                let result = "";
                for index in items {
                    result = box::item;
                    if index == 0 { box::change(); };
                };
                result
            };
            scope Box { item: "before", change: fn { item = "after"; } };
            inspect(Box, [0, 1, 2])
            """;
        foreach (int level in new[] { 0, 2 })
        {
            Module module = Unhint(Compiler.Compile(source, "adaptive-cell", level));
            Equal("after", await Interpreter.ExecuteAsync(module), "captured-cell mutation");
            using MemoryStream bytes = new(); Artifact.Write(module, bytes); bytes.Position = 0;
            Equal("after", await Interpreter.ExecuteAsync(Artifact.Read(bytes)), "captured-cell codec");
        }
        Module missing = Build("artifact-unhinted", 32);
        bool changed = false;
        missing = missing with
        {
            Functions = missing.Functions.Select(function => function with
            {
                Instructions = function.Instructions.Select(instruction =>
            {
                if (instruction.Op != OpCode.MakeScope || instruction.Text != "item\0other") return instruction;
                changed = true;
                return instruction with { Text = "gone\0other" };
            }).ToArray()
            }).ToArray(),
            Blocks = null
        };
        if (!changed) throw new InvalidOperationException("Missing-member falsifier did not replace the intended runtime layout.");
        Artifact.Validate(missing);
        try { await Interpreter.ExecuteAsync(missing); throw new InvalidOperationException("Missing field became a cache hit."); }
        catch (MsyncException error) when (error.Message.Contains("has no member 'item'", StringComparison.Ordinal)) { }
        Console.WriteLine("PASS mutable raw-cell reads and selected missing-field fallback");
    }

    private static async Task Generation()
    {
        CompilerSession compiler = new();
        ExecutionSession session = new();
        async Task<Value> Submit(string source)
        {
            Module module = Unhint(compiler.Compile(source));
            Value value = await session.ExecuteAsync(module);
            compiler.AcceptSubmission(session.GetLocalValue);
            return value;
        }
        await Submit("""
            let record = scope { item: "old", other: "new" };
            let query = fn(box: Scope) -> String { box::item };
            fn build() { $std::ranges::transform([record], fn(row: Scope) -> String { query(row) }) };
            let first = build();
            let second = build();
            $std::ranges::front(first)
            """);
        Equal("new", await Submit("query = fn(box: Scope) -> String { box::other }; $std::ranges::front(second)"), "session image/capture update");
        Console.WriteLine("PASS deferred callback after session code generation change");
    }

    private static async Task Check()
    {
        foreach (string name in new[] { "source-polymorphic", "source-hinted", "artifact-unhinted", "tiny", "literal", "file-copy" })
        {
            Module module = Build(name, name == "tiny" ? 1 : 32);
            string expected = name == "literal" ? "literal" : name == "tiny" ? "left" : "right";
            Verify(name, expected, await Interpreter.ExecuteAsync(module));
            using MemoryStream encoded = new(); Artifact.Write(module, encoded); encoded.Position = 0;
            Verify(name, expected, await Interpreter.ExecuteAsync(Artifact.Read(encoded)));
            Console.WriteLine($"PASS {name}: {module.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == OpCode.GetMember))} residual O2 field reads");
        }
        await MutationAndFallback();
        await Generation();
        Console.WriteLine("PASS: retained O2 source, live cells, layout fallback and session generation oracles");
    }
}
