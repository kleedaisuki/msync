using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Msync.Compiler;

int count = int.Parse(args[0], CultureInfo.InvariantCulture);
int samples = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 9;
StringBuilder input = new();
for (int index = 0; index < count; index++)
    input.Append("let item_").Append(index).Append(" = ./item_").Append(index).AppendLine(";");
input.Append("fn select(value) { let local = scope { item: value }; local::");
string source = input.ToString();
const string sourceName = "editor-scaling.msync";

long started = Stopwatch.GetTimestamp();
long allocated = GC.GetAllocatedBytesForCurrentThread();
int bindings = Analyze();
double coldMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
long coldBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
for (int warmup = 0; warmup < 3; warmup++) Analyze();
double[] times = new double[samples];
long[] bytes = new long[samples];
for (int sample = 0; sample < samples; sample++)
{
    allocated = GC.GetAllocatedBytesForCurrentThread();
    started = Stopwatch.GetTimestamp();
    Analyze();
    times[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    bytes[sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
}
JsonObject report = new()
{
    ["stage"] = "editor_analyze_and_context",
    ["bindings"] = count,
    ["visible_bindings"] = bindings,
    ["source_bytes"] = Encoding.UTF8.GetByteCount(source),
    ["source_sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ["tiered_compilation"] = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    ["cold_ms"] = coldMilliseconds,
    ["cold_allocated_bytes"] = coldBytes,
    ["warm_ms"] = new JsonArray(times.Select(value => JsonValue.Create(value)).ToArray()),
    ["warm_allocated_bytes"] = new JsonArray(bytes.Select(value => JsonValue.Create(value)).ToArray()),
    ["compiler_sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(EditorCompiler).Assembly.Location)))
};
Console.WriteLine(report.ToJsonString());

int Analyze()
{
    EditorAnalysis result = EditorCompiler.Analyze(source, sourceName);
    EditorContext context = result.At(source.Length);
    if (context.Receiver?.Members?.Any(member => member.Name == "item") != true
        || !context.Bindings.Any(member => member.Name == "value"))
        throw new InvalidOperationException("The incomplete generic body lost its lexical context.");
    return context.Bindings.Count;
}
