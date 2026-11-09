using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Msync.Hir;
using SourceCompiler = Msync.Compiler.Compiler;

if (args.Length < 2)
    throw new ArgumentException("Usage: msync-hir-compilation SOURCE LEVEL [SAMPLES] [--retention-only | --trace-once]");

string source = File.ReadAllText(Path.GetFullPath(args[0]));
int level = int.Parse(args[1], CultureInfo.InvariantCulture);
int samples = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)
    ? int.Parse(args[2], CultureInfo.InvariantCulture) : 9;
const string sourceName = "hir-compilation.msync";
bool retention = args.Contains("--retention-only", StringComparer.Ordinal);
bool includeCompile = args.Contains("--include-compile", StringComparer.Ordinal);
if (args.Contains("--trace-once", StringComparer.Ordinal))
{
    TraceOnce(source, sourceName, level);
    return;
}
if (args.Contains("--validate-only", StringComparer.Ordinal))
{
    var validated = SourceCompiler.Compile(source, sourceName, level);
    Console.WriteLine(new JsonObject
    {
        ["stage"] = "compile_preflight",
        ["functions"] = validated.Functions.Count,
        ["instructions"] = validated.Functions.Sum(function => function.Instructions.Count)
    }.ToJsonString());
    return;
}
JsonArray phases = [];
if (includeCompile)
{
    Func<Msync.Core.Module> compile = () => SourceCompiler.Compile(source, sourceName, level);
    if (retention)
        phases.Add(RetentionObservation.Measure(compile, "normal_compile_retention"));
    else
        phases.Add(Observe(compile, "normal_compile_total", samples).Report);
}
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
    ["compiler_facade_used"] = includeCompile,
    ["optimizer_used"] = includeCompile,
    ["normal_compile_optimization_level"] = includeCompile ? level : null,
    ["lower_phase_optimizer_used"] = false,
    ["lowering_options_level"] = level,
    ["lowering_retain_layout"] = false,
    ["source_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ["compiler_build"] = typeof(SourceCompiler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    ["hir_functions"] = hir.Functions.Length,
    ["hir_templates"] = hir.Templates.Length,
    ["hir_nodes"] = hir.Nodes.Length,
    ["hir_locations"] = hir.Locations.Length,
    ["hir_types"] = hir.Types.Length,
    ["hir_scope_layouts"] = hir.ScopeLayouts.Length,
    ["hir_specialized_template_functions"] = hir.Functions.Count(function => function.Template is not null),
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

static void TraceOnce(string source, string sourceName, int level)
{
    JsonObject assemblies = new();
    foreach (string path in Directory.GetFiles(AppContext.BaseDirectory, "Msync*.dll").Order(StringComparer.Ordinal))
        assemblies[Path.GetFileName(path)] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    Console.WriteLine(new JsonObject
    {
        ["stage"] = "input",
        ["source_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
        ["optimization"] = level,
        ["assemblies"] = assemblies
    }.ToJsonString());
    Console.Out.Flush();
    HirModule hir = Stage("analyze", () => SourceCompiler.Analyze(source, sourceName), value => new()
    {
        ["functions"] = value.Functions.Length,
        ["templates"] = value.Templates.Length,
        ["nodes"] = value.Nodes.Length,
        ["locations"] = value.Locations.Length,
        ["types"] = value.Types.Length,
        ["scope_layouts"] = value.ScopeLayouts.Length
    });
    var module = Stage("lower", () => HirLowerer.Lower(hir,
        new HirLoweringOptions(level, RetainLayout: false)).Module, CodeCounts);
    var optimized = Stage("optimize", () => Msync.Optimization.Optimizer.Optimize(module, level), CodeCounts);
    GC.KeepAlive(hir);
    GC.KeepAlive(optimized);

    static JsonObject CodeCounts(Msync.Core.Module value) => new()
    {
        ["functions"] = value.Functions.Count,
        ["instructions"] = value.Functions.Sum(function => function.Instructions.Count)
    };

    static T Stage<T>(string name, Func<T> action, Func<T, JsonObject> counts)
    {
        Console.WriteLine(new JsonObject { ["stage"] = name, ["event"] = "start" }.ToJsonString());
        Console.Out.Flush();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        T result = action();
        double milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - bytes;
        JsonObject report = counts(result);
        report["stage"] = name;
        report["event"] = "end";
        report["elapsed_ms"] = milliseconds;
        report["allocated_bytes"] = allocated;
        Console.WriteLine(report.ToJsonString());
        Console.Out.Flush();
        return result;
    }
}

internal sealed record Observation<T>(T Result, JsonObject Report);
