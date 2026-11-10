using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Compiler;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;
using Module = Msync.Core.Module;

int rows = NumberOption("--rows", 8192);
int passes = NumberOption("--passes", 4);
int warmups = NumberOption("--warmups", 5);
int samples = NumberOption("--samples", 1000);
int batchSize = NumberOption("--batch-size", samples);
bool gated = Option("--gate", "false") == "true";
string scenario = Option("--case", "references");
string root = Path.GetFullPath(Option("--root", ".temp/scope-tails/work"));
Directory.CreateDirectory(root);
string source = Workload(scenario, rows, passes);
string inputArtifact = Option("--input-artifact", "");
string emittedArtifact = Option("--emit-artifact", "");
Module module = inputArtifact.Length == 0
    ? Binding.Bind(Compiler.Compile(source, "scope-tail-" + scenario + ".msync", 2), new(root, root))
    : Artifact.Read(inputArtifact);
using MemoryStream encoded = new();
Artifact.Write(module, encoded);
if (emittedArtifact.Length != 0) File.WriteAllBytes(emittedArtifact, encoded.ToArray());
string artifactSha256 = Hash(inputArtifact.Length == 0 ? encoded.ToArray() : File.ReadAllBytes(inputArtifact));
int fieldReads = module.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == OpCode.GetMember));
int constructors = module.Functions.Sum(function => function.Instructions.Count(instruction => instruction.Op == OpCode.MakeScope));
if (fieldReads == 0 || constructors == 0) throw new InvalidOperationException("The workload lost its physical Scope sites.");
ExecutionOptions options = new(WorkingDirectory: root, Output: TextWriter.Null, Error: TextWriter.Null);
double expected = scenario switch
{
    "references" or "manifest" => (double)rows * passes,
    "computed" => (4.5 * rows * (rows - 1) / 2 - 2 * rows) * passes,
    _ => throw new ArgumentException(scenario)
};
for (int warmup = 0; warmup < warmups; warmup++) Verify(Run());
Sample[] measurements = new Sample[samples];
long allocatedBytes = 0;
long allocatedBefore = 0;
for (int index = 0; index < samples; index++)
{
    if (index % batchSize == 0)
    {
        if (gated)
        {
            Console.WriteLine($"READY {index}");
            Console.Out.Flush();
            if (Console.ReadLine() != "RUN") throw new InvalidOperationException("Expected a RUN measurement batch.");
        }
        allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    }
    int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
    long start = Stopwatch.GetTimestamp();
    Value result = Run();
    double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    measurements[index] = new(elapsed, GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
    Verify(result);
    if ((index + 1) % batchSize == 0 || index + 1 == samples)
    {
        allocatedBytes += GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        if (gated)
        {
            Console.WriteLine($"BATCH {index + 1}");
            Console.Out.Flush();
        }
    }
}
var assemblies = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (string name in new[] { "Msync.Core", "Msync.Hir", "Msync.Compiler", "Msync.Optimization", "Msync.Runtime" })
    assemblies.Add(name, Hash(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name + ".dll"))));
assemblies.Add("probe", Hash(File.ReadAllBytes(typeof(Report).Assembly.Location)));
double[] sorted = measurements.Select(sample => sample.Milliseconds).Order().ToArray();
var report = new Report(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
    Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), assemblies, scenario, rows, passes, warmups,
    source, Hash(Encoding.UTF8.GetBytes(source)), artifactSha256, constructors, fieldReads, allocatedBytes,
    new(Percentile(0.50), Percentile(0.95), Percentile(0.99), sorted.Length == 0 ? 0 : sorted[^1]), measurements);
string output = Path.GetFullPath(Option("--output", ".temp/scope-tails/report.json"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, TailJson.Default.Report));
Console.WriteLine($"PASS {scenario}: {rows} rows x {passes} passes; {samples} checked samples; report {output}");

Value Run() => Interpreter.ExecuteAsync(module, options).GetAwaiter().GetResult();
void Verify(Value value)
{
    if (value is not NumberValue number || BitConverter.DoubleToInt64Bits(number.Data) != BitConverter.DoubleToInt64Bits(expected))
        throw new InvalidOperationException($"Expected {expected:R}, got {value}.");
}
double Percentile(double quantile) => sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(quantile * sorted.Length) - 1];
int NumberOption(string name, int fallback) => int.Parse(Option(name, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
string Option(string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index < 0 ? fallback : args[index + 1];
}
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
static string Workload(string scenario, int rows, int passes)
{
    if (scenario == "manifest") return ManifestSource.Build(rows, passes);
    string fields = scenario switch
    {
        "references" => "label: \"kept\", path: ./fixed, flag: true",
        "computed" => "x: value + 1, y: value * 2, z: value - 3, w: value / 2",
        _ => throw new ArgumentException(scenario)
    };
    string scan = scenario == "references"
        ? "if row::label == \"kept\" and row::flag { total = total + 1; };"
        : "total = total + row::x + row::y + row::z + row::w;";
    return $$"""
        fn build(values: Range) {
            let records = $std::storage::Buffer::new([]);
            for value in values {
                $std::storage::Buffer::push(records, scope { {{fields}} });
            };
            $std::storage::Buffer::snapshot(records)
        };
        let rows = build($std::ranges::iota(0, {{rows}}));
        let total = 0;
        for pass in $std::ranges::iota(0, {{passes}}) {
            for row in rows { {{scan}} };
        };
        total
        """;
}

internal readonly record struct Sample(double Milliseconds, int Gen0Collections, int Gen1Collections, int Gen2Collections);
internal sealed record Quantiles(double P50Milliseconds, double P95Milliseconds, double P99Milliseconds, double MaximumMilliseconds);
internal sealed record Report(string Framework, string OS, string? TieredCompilation,
    Dictionary<string, string> Assemblies, string Scenario, int Rows, int Passes, int Warmups,
    string Source, string SourceSha256, string ArtifactSha256, int MakeScopeSites, int FieldReadSites, long TotalAllocatedBytes,
    Quantiles Quantiles, Sample[] Samples);
[JsonSerializable(typeof(Report))]
internal sealed partial class TailJson : JsonSerializerContext;
