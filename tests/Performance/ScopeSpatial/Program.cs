using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;
using Module = Msync.Core.Module;

int size = int.Parse(Option("--size", "512"), CultureInfo.InvariantCulture);
int samples = int.Parse(Option("--samples", "5"), CultureInfo.InvariantCulture);
int warmups = int.Parse(Option("--warmups", "3"), CultureInfo.InvariantCulture);
int optimization = int.Parse(Option("--level", "2"), CultureInfo.InvariantCulture);
string root = Path.GetFullPath(Option("--root", ".temp/scope-spatial/work"));
Directory.CreateDirectory(root);
ExecutionOptions options = new(WorkingDirectory: root, Output: TextWriter.Null, Error: TextWriter.Null);
List<Observation> observations = [];
foreach (string scenario in Option("--cases", "computed,literals,mixed,references").Split(','))
{
    string source = Source(scenario, size);
    double expected = scenario switch
    {
        "computed" => 4.5 * size * (size + 1) / 2 - 2 * size,
        "literals" => size * 10,
        "mixed" => 3 * size * (size + 1) / 2 + size,
        "references" => size,
        _ => throw new ArgumentException(scenario)
    };
    Module symbolic = Compiler.Compile(source, "spatial-" + scenario + ".msync", optimization);
    Module module = Binding.Bind(symbolic, new(root, root));
    int makeScopes = module.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == OpCode.MakeScope));
    int fieldReads = module.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == OpCode.GetMember));
    if (makeScopes == 0 || fieldReads == 0)
        throw new InvalidOperationException($"{scenario}: workload lost its physical object/field sites ({makeScopes}/{fieldReads}).");
    using MemoryStream encoded = new();
    Artifact.Write(module, encoded);
    string artifact = Convert.ToHexStringLower(SHA256.HashData(encoded.ToArray()));
    for (int iteration = 0; iteration < warmups; iteration++) Verify(Run(), expected, scenario);
    double[] milliseconds = new double[samples];
    long[] allocations = new long[samples];
    for (int sample = 0; sample < samples; sample++)
    {
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        long start = Stopwatch.GetTimestamp();
        Value result = Run();
        milliseconds[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocations[sample] = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        Verify(result, expected, scenario);
    }
    observations.Add(new(scenario, size, optimization, Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source))),
        artifact, makeScopes, fieldReads, module.Functions.Sum(function => function.Instructions.Count), milliseconds, allocations));

    Value Run() => Interpreter.ExecuteAsync(module, options).GetAwaiter().GetResult();
}
var assemblies = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (string name in new[] { "Msync.Core", "Msync.Hir", "Msync.Compiler", "Msync.Optimization", "Msync.Runtime" })
{
    string path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
    assemblies.Add(name, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
}
Report report = new(RuntimeInformation.FrameworkDescription, Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    assemblies, observations.ToArray());
string output = Path.GetFullPath(Option("--output", ".temp/scope-spatial/report.json"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, ReportJson.Default.Report));
Console.WriteLine($"PASS {observations.Count} physical Scope build/scan scenarios; report {output}");

string Option(string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index < 0 ? fallback : args[index + 1];
}

static string Source(string scenario, int size)
{
    string fields = scenario switch
    {
        "computed" => "x: value + 1, y: value * 2, z: value - 3, w: value / 2",
        "literals" => "x: 1, y: 2, z: 3, w: 4",
        "mixed" => "x: value + 1, y: value * 2, label: \"kept\", path: ./fixed",
        "references" => "label: \"kept\", path: ./fixed, flag: true",
        _ => throw new ArgumentException(scenario)
    };
    string scan = scenario switch
    {
        "computed" or "literals" => "total = total + row::x + row::y + row::z + row::w;",
        "mixed" => "if row::label == \"kept\" { total = total + row::x + row::y; };",
        "references" => "if row::label == \"kept\" and row::flag { total = total + 1; };",
        _ => throw new ArgumentException(scenario)
    };
    string values = string.Join(',', Enumerable.Range(1, size));
    return $$"""
        fn build(values: Range) {
            let records = $std::storage::Buffer::new([]);
            for value in values {
                $std::storage::Buffer::push(records, scope { {{fields}} });
            };
            $std::storage::Buffer::snapshot(records)
        };
        let rows = build([{{values}}]);
        let total = 0;
        for row in rows { {{scan}} };
        total
        """;
}

static void Verify(Value value, double expected, string scenario)
{
    if (value is not NumberValue number
        || BitConverter.DoubleToInt64Bits(number.Data) != BitConverter.DoubleToInt64Bits(expected))
        throw new InvalidOperationException($"{scenario}: expected {expected:R}, got {value}.");
}

internal sealed record Observation(string Scenario, int Size, int Optimization, string SourceSha256,
    string ArtifactSha256, int MakeScopeSites, int FieldReadSites, int Instructions,
    double[] Milliseconds, long[] AllocatedBytes);
internal sealed record Report(string Framework, string? TieredCompilation,
    Dictionary<string, string> Assemblies, Observation[] Observations);
[JsonSerializable(typeof(Report))]
internal sealed partial class ReportJson : JsonSerializerContext;
