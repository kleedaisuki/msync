using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Msync.Core;
using Module = Msync.Core.Module;
using SourceCompiler = Msync.Compiler.Compiler;

string workload = args.Length == 0 ? "bool" : args[0];
int samples = args.Length < 2 ? 15 : int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
string source = workload switch
{
    "none" => "let value = true; value",
    "bool" => "$std::bool::both(true, false)",
    "ranges_common" => "$std::ranges::first($std::ranges::take([1, 2, 3], 2))",
    "ranges" => "$std::ranges::size($std::ranges::take([1, 2, 3], 2))",
    "cast" => "\"42\" as Number",
    "functional" => "let op = $std::functional::compose($std::string::to_uppercase, $std::string::trim); op(\" hello \")",
    _ => throw new ArgumentException($"Unknown workload '{workload}'")
};

long allocated = GC.GetTotalAllocatedBytes(precise: true);
long started = Stopwatch.GetTimestamp();
Module cold = SourceCompiler.Compile(source, "stdlib-load.msync", optimization: 0);
double coldMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
long coldBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
for (int index = 0; index < 3; index++) SourceCompiler.Compile(source, "stdlib-load.msync", optimization: 0);
double[] milliseconds = new double[samples];
long[] bytes = new long[samples];
Module? retained = null;
for (int index = 0; index < samples; index++)
{
    allocated = GC.GetTotalAllocatedBytes(precise: true);
    started = Stopwatch.GetTimestamp();
    retained = SourceCompiler.Compile(source, "stdlib-load.msync", optimization: 0);
    milliseconds[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    bytes[index] = GC.GetTotalAllocatedBytes(precise: true) - allocated;
}

double[] sorted = milliseconds.Order().ToArray();
double Percentile(double fraction)
{
    double position = (sorted.Length - 1) * fraction;
    int first = (int)position;
    int last = Math.Min(first + 1, sorted.Length - 1);
    return sorted[first] + (sorted[last] - sorted[first]) * (position - first);
}
var executable = Environment.ProcessPath!;
var report = new JsonObject
{
    ["workload"] = workload,
    ["source"] = source,
    ["executable"] = executable,
    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))),
    ["compiler_build"] = typeof(SourceCompiler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    ["core_build"] = typeof(Optimizer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ["cold_o0_milliseconds"] = coldMilliseconds,
    ["cold_o0_allocated_bytes"] = coldBytes,
    ["warm_o0_p50_milliseconds"] = Percentile(0.5),
    ["warm_o0_p95_milliseconds"] = Percentile(0.95),
    ["warm_o0_allocated_bytes_mean"] = bytes.Average(),
    ["warm_o0_milliseconds"] = new JsonArray(milliseconds.Select(value => JsonValue.Create(value)).ToArray()),
    ["warm_o0_allocated_bytes"] = new JsonArray(bytes.Select(value => JsonValue.Create(value)).ToArray()),
    ["raw_functions"] = cold.Functions.Count,
    ["raw_constants"] = cold.Constants.Count,
    ["raw_instructions"] = cold.Functions.Sum(function => function.Instructions.Count),
    ["raw_locals"] = cold.Functions.Sum(function => function.LocalCount),
    ["raw_function_names"] = new JsonArray(cold.Functions.Select(function => JsonValue.Create(function.Name)).ToArray())
};
Console.WriteLine(report.ToJsonString());
GC.KeepAlive(retained);
