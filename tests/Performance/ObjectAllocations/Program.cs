using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;
using Compiler = Msync.Compiler.Compiler;

internal sealed record Result(string Image, string Sha256, string RuntimeBuild, string Case, int Size,
    string ArtifactSha256, int Instructions, int Calls, int MappedCalls, int DirectCalls,
    double PrepareMilliseconds, double FirstRunMilliseconds, double[] Milliseconds,
    double P50Milliseconds, double P95Milliseconds, double AllocatedBytesPerOperation);

[JsonSerializable(typeof(Result))]
internal sealed partial class ReportContext : JsonSerializerContext;

internal static class Program
{
    private static Value? retained;

    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Contains("--check", StringComparer.Ordinal)) { await Check(); return 0; }
        if (arguments.Contains("--check-source-order", StringComparer.Ordinal)) { await CheckSourceOrder(); return 0; }
        string name = Option(arguments, "--case", "direct");
        int size = int.Parse(Option(arguments, "--size", "256"), System.Globalization.CultureInfo.InvariantCulture);
        int samples = int.Parse(Option(arguments, "--samples", "15"), System.Globalization.CultureInfo.InvariantCulture);
        int iterations = int.Parse(Option(arguments, "--iterations", "8"), System.Globalization.CultureInfo.InvariantCulture);
        int level = int.Parse(Option(arguments, "--optimization", "2"), System.Globalization.CultureInfo.InvariantCulture);
        string root = Path.GetFullPath(Option(arguments, "--root", ".temp/object-allocations/work"));
        string output = Path.GetFullPath(Option(arguments, "--output", ".temp/object-allocations/result.json"));
        Directory.CreateDirectory(root);
        if (name == "io") File.WriteAllBytes(Path.Combine(root, "input"), Enumerable.Repeat((byte)120, 4096).ToArray());
        long preparation = Stopwatch.GetTimestamp();
        Module module = Binding.Bind(Compiler.Compile(Source(name, size), name + ".msync", level), new(root, root));
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
            typeof(Interpreter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            name, size, Convert.ToHexStringLower(SHA256.HashData(bytes)), code.Length,
            code.Count(instruction => instruction.Op == OpCode.Call),
            code.Count(instruction => instruction.Op == OpCode.Call && instruction.Text is not null),
            code.Count(instruction => instruction.Op == OpCode.Call && instruction.B != 0),
            prepared, firstRun, times, Quantile(sorted, 0.5), Quantile(sorted, 0.95), allocated);
        if (name == "io") Require(File.ReadAllBytes(Path.Combine(root, "output")).All(value => value == 120) && new FileInfo(Path.Combine(root, "output")).Length == 4096, "Copy bytes differ");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, ReportContext.Default.Result));
        Console.WriteLine($"{name} n={size}: {result.P50Milliseconds:F4} ms / {allocated:F0} B");
        GC.KeepAlive(retained);
        return 0;
    }

    private static async Task Run(Module module, ExecutionOptions runtime, string name, int size, int iterations)
    {
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            retained = await Interpreter.ExecuteAsync(module, runtime);
            string expected = name is "literal" or "io" ? name : "right";
            Require(retained is StringValue text && text.Data == expected, $"{name}/{size}: {retained}");
        }
    }

    private static string Source(string name, int count)
    {
        if (name == "literal") return "\"literal\"";
        if (name == "io") return "./input -> ./output; \"io\"";
        string range = string.Join(",", Enumerable.Range(0, count));
        if (name == "objects") return $$"""
            fn make(value) { scope { value: value, read: fn { value } } };
            let result = "";
            for item in [{{range}}] {
                let object = make(if exists ./flag { "left" } else { "right" });
                result = object::read();
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
                Module source = Compiler.Compile(Source(name, 16), name + ".msync", level);
                foreach (Module program in new[] { source, Decode(Encode(source)), Decode(Encode(Binding.Bind(source, new(root, root)))) })
                    await Run(program, runtime, name, 16, 1);
            }
            Require(await Interpreter.ExecuteAsync(EvaluationOrderModule(), runtime) is StringValue { Data: "AB" }, "Typed IR argument evaluation retargeted the already evaluated callee");
            await NativeCalls(runtime);
            await SessionContexts(root);
            await Owners(root, runtime);
            await EmptyClosureIdentity(runtime);
            Console.WriteLine("PASS: O0/O2/O3 source/mso/mexe calls, objects, paths, reassignment, capture identity, native ABI, selected error, deferred sessions and nested task ownership");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task EmptyClosureIdentity(ExecutionOptions runtime)
    {
        Module module = new([], [
            new("main", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.MakeClosure, 1),
                new(OpCode.MakeRange, 2), new(OpCode.NativeCall, 1, Text: "ranges.unique"),
                new(OpCode.Return)], new(TypeKind.Range, TypeRef.Function)),
            new("operation", 0, 0, [new(OpCode.Null), new(OpCode.Return)], TypeRef.Null)
        ], OptimizationLevel: 0);
        var values = (RangeValue)await Interpreter.ExecuteAsync(module, runtime);
        int count = 0;
        await foreach (var value in values.Items) count++;
        Require(count == 2, "Sharing argument arrays merged distinct closure capture identities");
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
            foreach (var program in new[] { module, Decode(Encode(module)), Decode(Encode(Binding.Bind(module, new(root, root)))) })
            {
                Require(await Interpreter.ExecuteAsync(program, runtime) is StringValue { Data: "joined" }, "Nested task ownership changed");
                foreach (string name in new[] { "outer", "inner", "released" })
                    Require(File.ReadAllText(Path.Combine(root, name)) == name, "Owner lost work: " + name);
            }
        }
    }

    private static Module EvaluationOrderModule() => new(
        [new FunctionValue(1), new FunctionValue(2), new StringValue("A"), new StringValue("B")],
        [
            new("main", 0, 5,
            [
                new(OpCode.Constant, 0), new(OpCode.StoreLocal, 0),
                new(OpCode.LoadCell, 0), new(OpCode.MakeClosure, 3, 1), new(OpCode.StoreLocal, 2),
                new(OpCode.LoadLocal, 0), new(OpCode.LoadLocal, 2), new(OpCode.Call, 0, 4),
                new(OpCode.Call, 1, Text: "1:1;2:2"), new(OpCode.StoreLocal, 3),
                new(OpCode.LoadLocal, 0), new(OpCode.False), new(OpCode.Call, 1, Text: "1:1;2:2"), new(OpCode.StoreLocal, 4),
                new(OpCode.LoadLocal, 3), new(OpCode.LoadLocal, 4), new(OpCode.NativeCall, 2, Text: "string.concat"), new(OpCode.Return)
            ], TypeRef.String),
            new("A", 1, 1, [new(OpCode.Constant, 2), new(OpCode.Return)], TypeRef.String, ParameterTypes: [TypeRef.Bool]),
            new("B", 1, 1, [new(OpCode.Constant, 3), new(OpCode.Return)], TypeRef.String, ParameterTypes: [TypeRef.Bool]),
            new("change", 0, 1, [new(OpCode.Constant, 1), new(OpCode.StoreLocal, 0), new(OpCode.True), new(OpCode.Return)], TypeRef.Bool, CaptureCount: 1)
        ], OptimizationLevel: 0);

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
            var value = await Interpreter.ExecuteAsync(Compiler.Compile(source, optimization: level), new(Output: TextWriter.Null, Error: TextWriter.Null));
            Console.WriteLine($"O{level}: {value}");
            accepted &= value is StringValue { Data: "AB" };
        }
        Require(accepted, "The source compiler did not preserve argument mutation at every level");
    }

    private static async Task NativeCalls(ExecutionOptions runtime)
    {
        var functions = new FunctionCode[]
        {
            new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.True), new(OpCode.Call, 1, Text: "1:2"), new(OpCode.Return)], TypeRef.Bool),
            new("unused", 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Bool, ParameterTypes: [TypeRef.Bool]),
            new("other", 1, 1, [new(OpCode.LoadLocal, 0), new(OpCode.Return)], TypeRef.Bool, ParameterTypes: [TypeRef.Bool])
        };
        Module native = new([new FunctionValue(1, NativeName: "bool.negate")], functions, OptimizationLevel: 0);
        Artifact.Validate(native);
        Require(await Interpreter.ExecuteAsync(native, runtime) is BoolValue { Data: false }, "Native callee entered textual specialization");
        native = native with { Functions = functions.Select((function, index) => index == 0 ? function with
            { Instructions = [new(OpCode.Constant, 0), new(OpCode.True), new(OpCode.Call, 1, 3), new(OpCode.Return)] } : function).ToArray() };
        Require(await Interpreter.ExecuteAsync(native, runtime) is BoolValue { Data: false }, "B override changed native identity");
        Module missing = native with { Constants = [new FunctionValue(1)], Functions = functions.Select((function, index) => index == 0 ? function with
            { Instructions = [new(OpCode.Constant, 0), new(OpCode.True), new(OpCode.Call, 1, Text: "2:2"), new(OpCode.Return)] } : function).ToArray() };
        try { await Interpreter.ExecuteAsync(missing, runtime); throw new InvalidOperationException("Missing dispatch unexpectedly succeeded"); }
        catch (MsyncException error) when (error.Message.Contains("no compiled specialization", StringComparison.Ordinal)) { }
        Module badOverride = native with { Constants = [new PathValue("never-execute")] };
        try { await Interpreter.ExecuteAsync(badOverride, runtime); throw new InvalidOperationException("Invalid B override unexpectedly ran a process"); }
        catch (InvalidCastException) { }
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
            ExecutionSession execution = new(new(Output: TextWriter.Null, Error: TextWriter.Null));
            async Task<Value> Submit(string source)
            {
                var value = await execution.ExecuteAsync(compiler.Compile(source));
                compiler.AcceptSubmission(execution.GetLocalValue);
                return value;
            }
            await Submit("let operation = fn { ./a }; let delayed = $std::ranges::transform([true], fn(item) { operation() });");
            Environment.CurrentDirectory = b;
            await Submit("operation = fn { ./b };");
            var first = await Submit("$std::ranges::front(delayed)");
            Require(first is PathValue path && Path.GetFullPath(path.LocalPath(b)) == Path.Combine(a, "b"), "Old deferred caller lost directory A");
            await Submit("let captured = ./b; operation = fn { captured };");
            var second = await Submit("$std::ranges::front(delayed)");
            Require(second is PathValue item && Path.GetFullPath(item.LocalPath(a)) == Path.Combine(b, "b"), "New actual closure lost captured directory B");
        }
        finally { Environment.CurrentDirectory = previous; }
    }

    private static byte[] Encode(Module module) { using MemoryStream output = new(); Artifact.Write(module, output); return output.ToArray(); }
    private static Module Decode(byte[] bytes) { using MemoryStream input = new(bytes); return Artifact.Read(input); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Option(string[] args, string name, string fallback) { int index = Array.IndexOf(args, name); return index < 0 ? fallback : args[index + 1]; }
    private static double Quantile(double[] sorted, double q) { double p = (sorted.Length - 1) * q; int a = (int)p, b = Math.Min(a + 1, sorted.Length - 1); return sorted[a] + (sorted[b] - sorted[a]) * (p - a); }
}
