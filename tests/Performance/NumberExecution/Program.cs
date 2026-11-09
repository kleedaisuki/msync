using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

int size = int.Parse(Option("--size", "1024"), CultureInfo.InvariantCulture);
int samples = int.Parse(Option("--samples", "15"), CultureInfo.InvariantCulture);
int iterations = int.Parse(Option("--iterations", "16"), CultureInfo.InvariantCulture);
string root = Path.GetFullPath(Option("--root", ".temp/number-runtime/work"));
Directory.CreateDirectory(root);
ExecutionOptions options = new(WorkingDirectory: root, Output: TextWriter.Null, Error: TextWriter.Null);
List<Observation> observations = [];
int checks = 0;
CheckRepresentations();
foreach (string scenario in Option("--cases", "sum,arithmetic,powers,comparisons,captured,last,literal").Split(','))
{
    string items = string.Join(',', Enumerable.Range(1, size).Select(index => (index % 17 + 1).ToString(CultureInfo.InvariantCulture)));
    string source = scenario switch
    {
        "sum" => $"fn run(items: Range) {{ let total = 0; for item in items {{ total = total + item; }}; total }}; run([{items}])",
        "arithmetic" => $"fn run(items: Range) {{ let total = 0; for item in items {{ total = total + ((item*3.5+2)/1.25-4)%17; }}; total }}; run([{items}])",
        "powers" => $"fn run(items: Range) {{ let total = 0; for item in items {{ total = total + (item+1)**2/4; }}; total }}; run([{items}])",
        "comparisons" => $"fn run(items: Range) {{ let total = 0; for item in items {{ if item*3.5+2 >= 20 {{ total = total + item; }}; }}; total }}; run([{items}])",
        "captured" => $"let total = 0; fn run(items: Range) {{ global total; for item in items {{ total = total + ((item*3.5+2)/1.25-4)%17; }}; total }}; run([{items}])",
        "last" => $"fn run(items: Range) {{ let last = 0; for item in items {{ last = item; }}; last }}; run([{items}])",
        "literal" => "42",
        _ => throw new ArgumentException(scenario)
    };
    double expected = scenario switch
    {
        "literal" => 42,
        "last" => size % 17 + 1,
        "arithmetic" or "captured" => Enumerable.Range(1, size).Aggregate(0d, (total, index) => total + (((index % 17 + 1) * 3.5 + 2) / 1.25 - 4) % 17),
        "powers" => Enumerable.Range(1, size).Sum(index => Math.Pow(index % 17 + 2, 2) / 4),
        "comparisons" => Enumerable.Range(1, size).Where(index => (index % 17 + 1) * 3.5 + 2 >= 20).Sum(index => (double)(index % 17 + 1)),
        _ => Enumerable.Range(1, size).Sum(index => (double)(index % 17 + 1))
    };
    foreach (int level in new[] { 0, 2, 3 })
    {
        Module relocatable = Compiler.Compile(source, scenario + ".msync", level);
        foreach (Module variant in new[] { relocatable, RoundTrip(relocatable), RoundTrip(Binding.Bind(relocatable, new(root, root))) })
            Verify(Interpreter.ExecuteAsync(variant, options).GetAwaiter().GetResult(), expected, scenario + " O" + level);
    }
    if (args.Contains("--check", StringComparer.Ordinal)) continue;
    Module executable = RoundTrip(Binding.Bind(Compiler.Compile(source, scenario + ".msync", 2), new(root, root)));
    Interpreter vm = new(executable, options);
    Value Run() => vm.ExecuteAsync().GetAwaiter().GetResult();
    for (int warmup = 0; warmup < 64; warmup++) Verify(Run(), expected, scenario);
    double[] times = new double[samples];
    for (int sample = 0; sample < samples; sample++)
    {
        long start = Stopwatch.GetTimestamp();
        for (int iteration = 0; iteration < iterations; iteration++) Verify(Run(), expected, scenario);
        times[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
    }
    long before = GC.GetTotalAllocatedBytes(precise: true);
    for (int iteration = 0; iteration < iterations; iteration++) Verify(Run(), expected, scenario);
    double allocated = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)iterations;
    double[] sorted = times.Order().ToArray();
    using MemoryStream imageBytes = new();
    Artifact.Write(executable, imageBytes);
    Instruction[] code = executable.Functions.SelectMany(function => function.Instructions).ToArray();
    int numericSites = code.Count(instruction => instruction.Op is OpCode.Add or OpCode.Subtract or OpCode.Multiply
        or OpCode.Divide or OpCode.Remainder or OpCode.Negate || instruction.Op == OpCode.NativeCall && instruction.Text == "math.pow");
    observations.Add(new(scenario, size, code.Length, numericSites,
        Convert.ToHexStringLower(SHA256.HashData(imageBytes.ToArray())), times,
        sorted[sorted.Length / 2], sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], allocated));
    Console.WriteLine($"{scenario}: {observations[^1].P50Milliseconds:F4} ms, {allocated:F0} B");
}
string image = Path.Combine(AppContext.BaseDirectory, "Msync.Runtime.dll");
string runtimeIdentity = File.Exists(image) ? image : Environment.ProcessPath!;
Report report = new(runtimeIdentity, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(runtimeIdentity))),
    System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, checks, observations.ToArray());
string output = Path.GetFullPath(Option("--output", ".temp/number-runtime/result.json"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, ReportJson.Default.Report));
Console.WriteLine($"PASS {checks} source/mso/mexe and measured execution checks");

void CheckRepresentations()
{
    double[] values = [0d, -0d, 1d, -1d, double.Epsilon, double.MaxValue,
        double.PositiveInfinity, double.NegativeInfinity,
        BitConverter.Int64BitsToDouble(0x7ff8000000000042),
        BitConverter.Int64BitsToDouble(unchecked((long)0xfff8000000000024))];
    foreach (OpCode operation in new[] { OpCode.Add, OpCode.Subtract, OpCode.Multiply, OpCode.Divide, OpCode.Remainder })
        foreach (double left in values)
            foreach (double right in values)
            {
                double expected = operation switch
                {
                    OpCode.Add => left + right,
                    OpCode.Subtract => left - right,
                    OpCode.Multiply => left * right,
                    OpCode.Divide => left / right,
                    _ => left % right
                };
                Module module = new([new NumberValue(left), new NumberValue(right)],
                    [new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Constant, 1),
                        new(operation), new(OpCode.Return)], TypeRef.Number)], OptimizationLevel: 0);
                foreach (Module variant in new[] { module, RoundTrip(module), RoundTrip(Binding.Bind(module, new(root, root))) })
                    Verify(Interpreter.ExecuteAsync(variant, options).GetAwaiter().GetResult(), expected, "IEEE " + operation);
            }
    foreach (double number in values)
    {
        Module module = new([new NumberValue(number)], [new("main", 0, 0,
            [new(OpCode.Constant, 0), new(OpCode.Negate), new(OpCode.Dup), new(OpCode.Add),
                new(OpCode.Pop), new(OpCode.Constant, 0), new(OpCode.Return)], TypeRef.Number)], OptimizationLevel: 0);
        Verify(Interpreter.ExecuteAsync(module, options).GetAwaiter().GetResult(), number, "duplicate/discard retains original constant");
    }
}

void Verify(Value actual, double expected, string scenario)
{
    checks++;
    if (actual is not NumberValue number || BitConverter.DoubleToInt64Bits(number.Data) != BitConverter.DoubleToInt64Bits(expected))
        throw new InvalidOperationException($"{scenario}: expected {expected}, found {actual}");
}
string Option(string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index < 0 ? fallback : args[index + 1];
}
static Module RoundTrip(Module module)
{
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes); bytes.Position = 0;
    return Artifact.Read(bytes);
}
internal sealed record Observation(string Scenario, int Size, int Instructions, int NumericSites, string ArtifactSha256, double[] Milliseconds,
    double P50Milliseconds, double P95Milliseconds, double AllocatedBytes);
internal sealed record Report(string RuntimeImage, string RuntimeSha256, string Framework, int Checks, Observation[] Observations);
[JsonSerializable(typeof(Report))]
internal sealed partial class ReportJson : JsonSerializerContext;
