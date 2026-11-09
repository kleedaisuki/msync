using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Msync.Hir;
using SourceCompiler = Msync.Compiler.Compiler;

if (args.Length < 2)
    throw new ArgumentException("Usage: msync-hir-compilation SOURCE LEVEL [SAMPLES] [--retention-only]");

string source = File.ReadAllText(Path.GetFullPath(args[0]));
int level = int.Parse(args[1], CultureInfo.InvariantCulture);
int samples = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)
    ? int.Parse(args[2], CultureInfo.InvariantCulture) : 9;
const string sourceName = "hir-compilation.msync";
bool retention = args.Contains("--retention-only", StringComparer.Ordinal);
JsonArray phases = [];
Func<HirModule> analyze = () => SourceCompiler.Analyze(source, sourceName);
HirModule hir;
if (retention)
{
    phases.Add(RetentionObservation.Measure(analyze, "analyze_full_hir_retention"));
    hir = analyze();
    phases.Add(RetentionObservation.Measure(Lower, "lower_with_hir_input_retention"));
    phases.Add(RetentionObservation.Measure(Serialize, "serialize_with_hir_input_retention"));
}
else
{
    var analysis = Observe(analyze, "analyze", samples);
    hir = analysis.Result;
    phases.Add(analysis.Report);
    var lowering = Observe(Lower, "lower_unoptimized", samples);
    phases.Add(lowering.Report);
    phases.Add(Observe(Serialize, "hir_serialize", samples).Report);
    GC.KeepAlive(lowering.Result);
}

using MemoryStream encoded = Serialize();
var module = Lower();
Console.WriteLine(new JsonObject
{
    ["stage"] = retention ? "typed_hir_retention" : "typed_hir_phases",
    ["compiler_facade_used"] = false,
    ["optimizer_used"] = false,
    ["lowering_options_level"] = level,
    ["lowering_retain_layout"] = false,
    ["source_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ["compiler_build"] = typeof(SourceCompiler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    ["hir_functions"] = hir.Functions.Length,
    ["hir_templates"] = hir.Templates.Length,
    ["hir_nodes"] = hir.Nodes.Length,
    ["hir_locations"] = hir.Locations.Length,
    ["functions"] = module.Functions.Count,
    ["instructions"] = module.Functions.Sum(function => function.Instructions.Count),
    ["hir_artifact_bytes"] = encoded.Length,
    ["hir_artifact_sha256"] = Convert.ToHexStringLower(SHA256.HashData(encoded.GetBuffer().AsSpan(0, (int)encoded.Length))),
    ["phases"] = phases
}.ToJsonString());
GC.KeepAlive(hir);

Msync.Core.Module Lower() => HirLowerer.Lower(hir,
    new HirLoweringOptions(level, RetainLayout: false)).Module;

MemoryStream Serialize()
{
    MemoryStream stream = new();
    HirArtifact.Write(hir, stream);
    return stream;
}

static Observation<T> Observe<T>(Func<T> create, string stage, int samples) where T : class
{
    long startBytes = GC.GetAllocatedBytesForCurrentThread();
    long start = Stopwatch.GetTimestamp();
    T result = create();
    double coldMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    long coldBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
    for (int index = 0; index < 3; index++) result = create();
    double[] times = new double[samples];
    long[] allocations = new long[samples];
    for (int index = 0; index < samples; index++)
    {
        startBytes = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        result = create();
        times[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocations[index] = GC.GetAllocatedBytesForCurrentThread() - startBytes;
    }
    return new(result, new JsonObject
    {
        ["stage"] = stage,
        ["cold_ms"] = coldMilliseconds,
        ["cold_allocated_bytes"] = coldBytes,
        ["warm_ms"] = new JsonArray(times.Select(value => JsonValue.Create(value)).ToArray()),
        ["warm_allocated_bytes"] = new JsonArray(allocations.Select(value => JsonValue.Create(value)).ToArray())
    });
}

internal sealed record Observation<T>(T Result, JsonObject Report);
