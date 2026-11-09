using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Msync.Core;
using Module = Msync.Core.Module;
using SourceCompiler = Msync.Compiler.Compiler;

if (args.Length < 2)
    throw new ArgumentException("Usage: msync-hir-compilation SOURCE LEVEL [SAMPLES] [--retention-only]");

string sourcePath = Path.GetFullPath(args[0]);
string source = File.ReadAllText(sourcePath);
int level = int.Parse(args[1], CultureInfo.InvariantCulture);
int samples = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal)
    ? int.Parse(args[2], CultureInfo.InvariantCulture) : 9;
const string sourceName = "hir-compilation.msync";

if (args.Contains("--retention-only", StringComparer.Ordinal))
{
    Console.WriteLine(RetentionObservation.Compile(source, sourceName, level).ToJsonString());
    return;
}

long startBytes = GC.GetAllocatedBytesForCurrentThread();
long started = Stopwatch.GetTimestamp();
Module result = SourceCompiler.Compile(source, sourceName, level);
double coldMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
long coldBytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
for (int warmup = 0; warmup < 3; warmup++)
    result = SourceCompiler.Compile(source, sourceName, level);

double[] times = new double[samples];
long[] allocations = new long[samples];
for (int sample = 0; sample < samples; sample++)
{
    startBytes = GC.GetAllocatedBytesForCurrentThread();
    started = Stopwatch.GetTimestamp();
    result = SourceCompiler.Compile(source, sourceName, level);
    times[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    allocations[sample] = GC.GetAllocatedBytesForCurrentThread() - startBytes;
}

using MemoryStream encoded = new();
Artifact.Write(result, encoded);
var report = new JsonObject
{
    ["stage"] = "compile_total",
    ["optimization"] = level,
    ["source_sha256"] = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source))),
    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ["configuration"] = typeof(SourceCompiler).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
    ["compiler_build"] = typeof(SourceCompiler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    ["cold_ms"] = coldMilliseconds,
    ["cold_allocated_bytes"] = coldBytes,
    ["warm_ms"] = new JsonArray(times.Select(value => JsonValue.Create(value)).ToArray()),
    ["warm_allocated_bytes"] = new JsonArray(allocations.Select(value => JsonValue.Create(value)).ToArray()),
    ["functions"] = result.Functions.Count,
    ["instructions"] = result.Functions.Sum(function => function.Instructions.Count),
    ["constants"] = result.Constants.Count,
    ["locals"] = result.Functions.Sum(function => function.LocalCount),
    ["artifact_bytes"] = encoded.Length,
    ["artifact_sha256"] = Convert.ToHexStringLower(SHA256.HashData(encoded.GetBuffer().AsSpan(0, (int)encoded.Length))),
    ["assemblies"] = new JsonObject(new[] { "Msync.Core.dll", "Msync.Optimization.dll", "Msync.Compiler.dll" }
        .Select(name => KeyValuePair.Create<string, JsonNode?>(name, JsonValue.Create(
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name))))))))
};
Console.WriteLine(report.ToJsonString());
GC.KeepAlive(result);
