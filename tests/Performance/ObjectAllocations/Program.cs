using Msync.Optimization;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;
using Msync.Probes;
using Msync.Runtime;
using Compiler = Msync.Compiler.Compiler;

internal sealed record Result(string Image, string Sha256, string RuntimeBuild, string? JitPolicy, string Case, int Size,
    string SourceSha256, string Representation, string ArtifactSha256, int Instructions, int Calls, int MappedCalls, int DirectCalls,
    double PrepareMilliseconds, double FirstRunMilliseconds, double[] Milliseconds,
    double P50Milliseconds, double P95Milliseconds, double AllocatedBytesPerOperation);

[JsonSerializable(typeof(Result))]
internal sealed partial class ReportContext : JsonSerializerContext;

internal static class Program
{
    private static NativeValue retained;

    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Contains("--check", StringComparer.Ordinal)) { await Check(); return 0; }
        if (arguments.Contains("--check-source-order", StringComparer.Ordinal)) { await CheckSourceOrder(); return 0; }
        string name = Option(arguments, "--case", "direct");
        int size = int.Parse(Option(arguments, "--size", "256"), System.Globalization.CultureInfo.InvariantCulture);
        int samples = int.Parse(Option(arguments, "--samples", "15"), System.Globalization.CultureInfo.InvariantCulture);
        int iterations = int.Parse(Option(arguments, "--iterations", "8"), System.Globalization.CultureInfo.InvariantCulture);
        int level = int.Parse(Option(arguments, "--optimization", "2"), System.Globalization.CultureInfo.InvariantCulture);
        Require(size > 0 && samples > 0 && iterations > 0, "Size, samples and iterations must be positive");
        string root = Path.GetFullPath(Option(arguments, "--root", ".temp/object-allocations/work"));
        string output = Path.GetFullPath(Option(arguments, "--output", ".temp/object-allocations/result.json"));
        Directory.CreateDirectory(root);
        if (name == "io") File.WriteAllBytes(Path.Combine(root, "input"), Enumerable.Repeat((byte)120, 4096).ToArray());
        long preparation = Stopwatch.GetTimestamp();
        string source = Source(name, size);
        NativeProgram module = NativeProbe.Prepare(Compiler.Compile(source, name + ".msync", level), new(root, root));
        byte[] bytes = Encode(module);
        module = Decode(bytes);
        double prepared = Stopwatch.GetElapsedTime(preparation).TotalMilliseconds;
        var runtime = new ExecutionOptions(WorkingDirectory: root, Output: TextWriter.Null, Error: TextWriter.Null);
        long first = Stopwatch.GetTimestamp();
        await Run(module, runtime, name, size, 1);
        double firstRun = Stopwatch.GetElapsedTime(first).TotalMilliseconds;
        foreach (int warmup in Enumerable.Range(0, 3)) await Run(module, runtime, name, size, iterations);
        double[] times = new double[samples];
        for (int sample = 0; sample < samples; sample++)
        {
            long start = Stopwatch.GetTimestamp();
            await Run(module, runtime, name, size, iterations);
            times[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        }
        long before = GC.GetTotalAllocatedBytes(precise: true);
        await Run(module, runtime, name, size, iterations);
        double allocated = (double)(GC.GetTotalAllocatedBytes(precise: true) - before) / iterations;
        double[] sorted = times.Order().ToArray();
        string executable = Environment.ProcessPath!;
        var code = module.Functions.SelectMany(function => function.Instructions).ToArray();
        Result result = new(executable, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))),
            typeof(NativeInterpreter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            Environment.GetEnvironmentVariable("MSYNC_JIT"), name, size, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source))),
            NativeProgram.Representation, Convert.ToHexStringLower(SHA256.HashData(bytes)), code.Length,
            code.Count(instruction => instruction.Op == NativeOp.Call),
            code.Count(instruction => instruction.Op == NativeOp.Call && instruction.Symbol is not null),
            code.Count(instruction => instruction.Op == NativeOp.Call && instruction.B >= 0),
            prepared, firstRun, times, Quantile(sorted, 0.5), Quantile(sorted, 0.95), allocated);
        if (name == "io") Require(File.ReadAllBytes(Path.Combine(root, "output")).All(value => value == 120) && new FileInfo(Path.Combine(root, "output")).Length == 4096, "Copy bytes differ");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, ReportContext.Default.Result));
        Console.WriteLine($"{name} n={size}: {result.P50Milliseconds:F4} ms / {allocated:F0} B");
        GC.KeepAlive(retained);
        return 0;
    }

    private static async Task Run(NativeProgram module, ExecutionOptions runtime, string name, int size, int iterations)
    {
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            retained = await NativeProbe.RunAsync(module, runtime);
            string expected = name is "literal" or "io" ? name : "right";
            Require(retained.Kind == NativeKind.String && retained.AsString() == expected, $"{name}/{size}: {retained}");
        }
    }

    private static string Source(string name, int count)
    {
        if (name == "literal") return "\"literal\"";
        if (name == "io") return "./input -> ./output; \"io\"";
        string range = string.Join(",", Enumerable.Range(0, count));
        if (name == "objects") return $$"""
            fn make(value) { object { value: value, read: fn { value } } };
            let result = "";
            for item in [{{range}}] {
                let record = make(if exists ./flag { "left" } else { "right" });
                result = record::read();
            };
            result
            """;
        if (name == "paths") return $$"""
            let result = ./left;
            for item in [{{range}}] {
                result = if exists ./flag { ./left } else { ./right };
            };
            $std::path::filename(result) as String
            """;
        string factory = name == "captures"
            ? "fn make(value) { fn { value } }; let a = make(\"left\"); let b = make(\"right\");"
            : "let a = fn { \"left\" }; let b = fn { \"right\" };";
        string choice = name == "swap" ? $"selected = if item < {count / 2} {{ a }} else {{ b }};" : "";
        return $$"""
            fn invoke(operation) { operation() };
            {{factory}}
            let selected = if exists ./flag { a } else { b };
            let result = "";
            for item in [{{range}}] { {{choice}} result = invoke(selected); };
            result
            """;
    }

    private static async Task Check()
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".temp/object-allocations/check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ExecutionOptions runtime = new(WorkingDirectory: root, Output: TextWriter.Null, Error: TextWriter.Null);
            foreach (int level in new[] { 0, 2, 3 })
                foreach (string name in new[] { "direct", "swap", "captures", "objects", "paths", "literal" })
                {
                    NativeProgram source = Compiler.Compile(Source(name, 16), name + ".msync", level);
                    foreach (NativeProgram program in new[] { source, Decode(Encode(source)), Decode(Encode(NativeProbe.Prepare(source, new(root, root)))) })
                        await Run(program, runtime, name, 16, 1);
                }
            Require((await NativeProbe.RunAsync(EvaluationOrderModule(), runtime)).AsString() == "AB", "Typed IR argument evaluation retargeted the already evaluated callee");
            await NativeCalls(runtime);
            await Owners(root, runtime);
            await EmptyClosureIdentity(runtime);
            await SessionContexts(root);
            Console.WriteLine("PASS: O0/O2/O3 source/mso/mexe calls, objects, paths, reassignment, capture identity, native ABI, selected error, deferred sessions and nested task ownership");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task EmptyClosureIdentity(ExecutionOptions runtime)
    {
        NativeProgram module = new([new(NativeKind.Null)], [
            new("main", [new(0), new(0), new(1), new(1), new(2)],
                [new(NativeOp.MakeFunction, 0, 1), new(NativeOp.MakeFunction, 1, 1),
                 new(NativeOp.MakeRange, 2, Type: 1, Inputs: [0, 1]),
                 new(NativeOp.CallNative, 3, Symbol: "ranges.unique", Inputs: [2]),
                 new(NativeOp.CallNative, 4, Symbol: "ranges.size", Inputs: [3]), new(NativeOp.Return, A: 4)], new([], 2, [])),
            new("operation", [new(3)], [new(NativeOp.Constant, 0, 0), new(NativeOp.Return, A: 0)], new([], 3, []))
        ], [new(TypeKind.Function), new(TypeKind.Range, Element: 0), new(TypeKind.Number), new(TypeKind.Null)], []);
        NativeValue count = await NativeProbe.RunAsync(Decode(Encode(module)), runtime);
        Require(count.AsNumber() == 2, "Sharing empty environments merged distinct closure identities");
    }

    private static async Task Owners(string root, ExecutionOptions runtime)
    {
        const string source = """
            fn leaf(file: Path, value: String) { value -> file; };
            fn nested() {
                "outer" -> ./outer;
                leaf(./inner, "inner");
                release { "released" -> ./released; };
                acquire;
                if exists ./inner { "joined" } else { "missing" }
            };
            nested()
            """;
        foreach (int level in new[] { 0, 2, 3 })
        {
            var module = Compiler.Compile(source, "nested-owners", level);
            foreach (var program in new[] { module, Decode(Encode(module)), Decode(Encode(NativeProbe.Prepare(module, new(root, root)))) })
            {
                Require((await NativeProbe.RunAsync(program, runtime)).AsString() == "joined", "Nested task ownership changed");
                foreach (string name in new[] { "outer", "inner", "released" })
                    Require(File.ReadAllText(Path.Combine(root, name)) == name, "Owner lost work: " + name);
            }
        }
    }

    private static NativeProgram EvaluationOrderModule() => Compiler.Compile("""
        let action = fn(value: Bool) { "A" };
        let replacement = fn(value: Bool) { "B" };
        fn change() { action = replacement; true };
        let first = action(change());
        let second = action(false);
        $std::string::concat(first, second)
        """, optimization: 0);

    private static async Task CheckSourceOrder()
    {
        const string source = """
            let action = fn(value: Bool) { "A" };
            let replacement = fn(value: Bool) { "B" };
            fn change() { action = replacement; true };
            let first = action(change());
            let second = action(false);
            $std::string::concat(first, second)
            """;
        bool accepted = true;
        foreach (int level in new[] { 0, 1, 2, 3 })
        {
            var value = await NativeProbe.RunAsync(Compiler.Compile(source, optimization: level), new(Output: TextWriter.Null, Error: TextWriter.Null));
            Console.WriteLine($"O{level}: {value}");
            accepted &= value.Kind == NativeKind.String && value.AsString() == "AB";
        }
        Require(accepted, "The source compiler did not preserve argument mutation at every level");
    }

    private static async Task NativeCalls(ExecutionOptions runtime)
    {
        NativeFunctionCode[] functions = [
            new("main", [new(0, NativeSlotRole.Parameter), new(1), new(1)],
                [new(NativeOp.Constant, 1, 0), new(NativeOp.Call, 2, 0, 2, Inputs: [1]), new(NativeOp.Return, A: 2)],
                new([0], 1, []), ParameterCount: 1),
            new("unused", [new(1, NativeSlotRole.Parameter)], [new(NativeOp.Return, A: 0)], new([1], 1, []), ParameterCount: 1),
            new("other", [new(1, NativeSlotRole.Parameter)], [new(NativeOp.Return, A: 0)], new([1], 1, []), ParameterCount: 1)];
        NativeProgram native = new([new(NativeKind.Bool, Boolean: true)], functions, [new(TypeKind.Function), new(TypeKind.Bool)], []);
        NativeArtifact.Validate(native);
        await using NativeInterpreter interpreter = new(Decode(Encode(native)), runtime);
        NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
            [NativeValue.FromFunction(new(1, NativeName: "bool.negate"))]);
        Require(!result.AsBool(), "A direct source target override changed evaluated native identity");
        native = native with
        {
            Functions = functions.Select((function, index) => index == 0 ? function with
            { Instructions = [new(NativeOp.Constant, 1, 0), new(NativeOp.Call, 2, 0, Symbol: "2:2", Inputs: [1]), new(NativeOp.Return, A: 2)] } : function).ToArray()
        };
        await using NativeInterpreter dispatched = new(Decode(Encode(native)), runtime);
        Require(!(await dispatched.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromFunction(new(1, NativeName: "bool.negate"))])).AsBool(),
            "Finite source dispatch changed evaluated native identity");
        try
        {
            await dispatched.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromFunction(new(1))]);
            throw new InvalidOperationException("Missing source dispatch unexpectedly succeeded");
        }
        catch (MsyncException error) when (error.Message.Contains("compiled specialization", StringComparison.Ordinal)) { }
        NativeProgram bad = native with
        {
            Functions = [functions[0] with { Slots = [new(2, NativeSlotRole.Parameter), new(1), new(1)] }, .. functions.Skip(1)],
            Types = [.. native.Types, new(TypeKind.Path)]
        };
        try { NativeArtifact.Validate(bad); throw new InvalidOperationException("A typed function Call admitted a Path process callee"); }
        catch (MsyncException) { }
    }

    private static async Task SessionContexts(string root)
    {
        string previous = Environment.CurrentDirectory;
        string a = Path.Combine(root, "A"), b = Path.Combine(root, "B");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        try
        {
            Environment.CurrentDirectory = a;
            CompilerSession compiler = new(optimization: 2);
            await using NativeExecutionSession execution = new(new(Output: TextWriter.Null, Error: TextWriter.Null));
            async Task<NativeValue> Submit(string source)
            {
                var value = await execution.ExecuteAsync(compiler.Compile(source));
                compiler.AcceptSubmission(execution.GetLocalValue);
                return value;
            }
            await Submit("let operation = fn { ./a }; let delayed = $std::ranges::transform([true], fn(item) { operation() });");
            Environment.CurrentDirectory = b;
            await Submit("operation = fn { ./b };");
            var first = await Submit("$std::ranges::front(delayed)");
            Require(first.Kind == NativeKind.Path && Path.GetFullPath(first.AsPath().LocalPath(b)) == Path.Combine(a, "b"), "Old deferred caller lost directory A");
            await Submit("let captured = ./b; operation = fn { captured };");
            var second = await Submit("$std::ranges::front(delayed)");
            Require(second.Kind == NativeKind.Path && Path.GetFullPath(second.AsPath().LocalPath(a)) == Path.Combine(b, "b"), "New actual closure lost captured directory B");
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    private static byte[] Encode(NativeProgram module) { using MemoryStream output = new(); NativeArtifact.Write(module, output); return output.ToArray(); }
    private static NativeProgram Decode(byte[] bytes) { using MemoryStream input = new(bytes); return NativeArtifact.Read(input); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Option(string[] args, string name, string fallback) { int index = Array.IndexOf(args, name); return index < 0 ? fallback : args[index + 1]; }
    private static double Quantile(double[] sorted, double q) { double p = (sorted.Length - 1) * q; int a = (int)p, b = Math.Min(a + 1, sorted.Length - 1); return sorted[a] + (sorted[b] - sorted[a]) * (p - a); }
}
