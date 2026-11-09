using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

int count = int.Parse(Option("--calls", "1024"), CultureInfo.InvariantCulture);
int samples = int.Parse(Option("--samples", "9"), CultureInfo.InvariantCulture);
int iterations = int.Parse(Option("--iterations", "32"), CultureInfo.InvariantCulture);
string root = Path.GetFullPath(".temp/native-callback/work");
Directory.CreateDirectory(root);
ExecutionOptions options = new(WorkingDirectory: root, Output: TextWriter.Null, Error: TextWriter.Null);
int checks = 0;
const string callbackSource = """
    let factor = 2;
    let callback = fn(value: Number) { value * factor };
    let first = $std::ranges::fold($std::ranges::transform([1, 2, 3], callback), 0,
        fn(total, item) { total + item });
    factor = 3;
    let second = $std::ranges::fold($std::ranges::transform([1, 2, 3], callback), 0,
        fn(total, item) { total + item });
    callback = fn(value: Number) { value + 10 };
    let third = $std::ranges::fold($std::ranges::transform([1, 2, 3], callback), 0,
        fn(total, item) { total + item });
    first == 12 and second == 18 and third == 36
    """;
foreach (int level in new[] { 0, 2, 3 })
{
    Module source = Compiler.Compile(callbackSource, "native-callback.msync", level);
    foreach (Module variant in new[] { source, RoundTrip(source), RoundTrip(Binding.Bind(source, new(root, root))) })
        for (int execution = 0; execution < 2; execution++)
        {
            Check(Interpreter.ExecuteAsync(variant, options).GetAwaiter().GetResult() is BoolValue { Data: true },
                "Cached native invocation retained an old closure, cell, callee or interpreter");
        }
}
List<Observation> observations = [];
foreach ((string name, Module raw, double expected) in new[]
{
    ("buffer_get", BufferModule(count), 1d),
    ("literal", new Module([new NumberValue(42)], [new("main", 0, 0,
        [new(OpCode.Constant, 0), new(OpCode.Return)], TypeRef.Number)], OptimizationLevel: 0), 42d)
})
{
    Module executable = RoundTrip(Binding.Bind(raw, new(root, root)));
    foreach (Module variant in new[] { raw, RoundTrip(raw), executable })
        CheckNumber(Interpreter.ExecuteAsync(variant, options).GetAwaiter().GetResult(), expected);
    using MemoryStream bytes = new();
    Artifact.Write(executable, bytes);
    string artifactSha = Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    Interpreter prepared = new(executable, options);
    foreach ((string stage, Func<Value> run) in new (string, Func<Value>)[]
    {
        ("prepared", () => prepared.ExecuteAsync().GetAwaiter().GetResult()),
        ("new_interpreter", () => Interpreter.ExecuteAsync(executable, options).GetAwaiter().GetResult())
    })
    {
        for (int warmup = 0; warmup < 32; warmup++) CheckNumber(run(), expected);
        double[] times = new double[samples];
        for (int sample = 0; sample < samples; sample++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int iteration = 0; iteration < iterations; iteration++) CheckNumber(run(), expected);
            times[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        }
        long before = GC.GetTotalAllocatedBytes(precise: true);
        for (int iteration = 0; iteration < iterations; iteration++) CheckNumber(run(), expected);
        double allocation = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)iterations;
        double[] sorted = times.Order().ToArray();
        observations.Add(new(name, stage, name == "buffer_get" ? count + 2 : 0, artifactSha,
            times, sorted[sorted.Length / 2], allocation));
        Console.WriteLine($"{name}/{stage}: {observations[^1].P50Milliseconds:F4} ms / {allocation:F0} B");
    }
}
string image = Path.Combine(AppContext.BaseDirectory, "Msync.Runtime.dll");
Report report = new(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(image))),
    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Msync.Core.dll")))),
    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Msync.Compiler.dll")))),
    checks, observations.ToArray());
string output = Path.GetFullPath(Option("--output", ".temp/native-callback/results.json"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, ReportJson.Default.Report));
Console.WriteLine($"PASS {checks} callback freshness and Buffer/literal checks");

void CheckNumber(Value value, double expected) => Check(value is NumberValue number && number.Data == expected, "Buffer/literal result changed");
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
string Option(string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index < 0 ? fallback : args[index + 1];
}
static Module BufferModule(int count)
{
    List<Instruction> code = [new(OpCode.Constant, 0), new(OpCode.MakeRange, 1),
        new(OpCode.NativeCall, 1, Text: "storage.buffer_new"), new(OpCode.InitializeLocal, 0)];
    for (int index = 0; index < count; index++)
    {
        code.Add(new(OpCode.LoadLocal, 0)); code.Add(new(OpCode.Constant, 1));
        code.Add(new(OpCode.NativeCall, 2, Text: "storage.buffer_get")); code.Add(new(OpCode.Pop));
    }
    code.Add(new(OpCode.LoadLocal, 0)); code.Add(new(OpCode.NativeCall, 1, Text: "storage.buffer_size"));
    code.Add(new(OpCode.Return));
    return new([new NumberValue(1), new NumberValue(0)], [new("main", 0, 1, code, TypeRef.Number)], OptimizationLevel: 0);
}
static Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes); bytes.Position = 0; return Artifact.Read(bytes);
}
internal sealed record Observation(string Case, string Stage, int NativeCalls, string ArtifactSha256,
    double[] Milliseconds, double P50Milliseconds, double AllocatedBytes);
internal sealed record Report(string RuntimeSha256, string CoreSha256, string CompilerSha256, int Checks, Observation[] Observations);
[JsonSerializable(typeof(Report))]
internal sealed partial class ReportJson : JsonSerializerContext;
