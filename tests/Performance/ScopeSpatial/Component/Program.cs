using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Core;

int count = int.Parse(Option("--rows", "32768"), CultureInfo.InvariantCulture);
int passes = int.Parse(Option("--passes", "32"), CultureInfo.InvariantCulture);
int samples = int.Parse(Option("--samples", "5"), CultureInfo.InvariantCulture);
int warmups = int.Parse(Option("--warmups", "3"), CultureInfo.InvariantCulture);
ScopeLayout layout = new(["x", "y", "z", "w"]);
double expected = 1.25 * count * (count + 1);
List<Observation> observations = [];
foreach (bool packed in new[] { false, true })
    observations.Add(Measure(packed));
Report report = new(count, passes, RuntimeInformation.FrameworkDescription,
    Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
    Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(ScopeValue).Assembly.Location))),
    observations.ToArray());
string output = Path.GetFullPath(Option("--output", ".temp/scope-spatial/component.json"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(report, ReportJson.Default.Report));
Console.WriteLine($"PASS plain/packed {count} dynamic four-Number rows; report {output}");

[MethodImpl(MethodImplOptions.NoInlining)]
Observation Measure(bool packed)
{
    long before = GC.GetTotalMemory(forceFullCollection: true);
    long allocationStart = GC.GetAllocatedBytesForCurrentThread();
    ScopeValue[] rows = Build(layout, count, packed);
    long constructionBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
    Mechanism mechanism = VerifyRows(rows, packed);
    long retainedBytes = GC.GetTotalMemory(forceFullCollection: true) - before;
    for (int iteration = 0; iteration < warmups; iteration++) Verify(Scan(rows, passes), expected);
    double[] milliseconds = new double[samples];
    long[] allocations = new long[samples];
    for (int sample = 0; sample < samples; sample++)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        double result = Scan(rows, passes);
        milliseconds[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        allocations[sample] = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Verify(result, expected);
    }
    GC.KeepAlive(rows);
    return new(packed ? "packed" : "plain", constructionBytes, retainedBytes, mechanism,
        milliseconds, allocations);
}

string Option(string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    return index < 0 ? fallback : args[index + 1];
}

[MethodImpl(MethodImplOptions.NoInlining)]
static ScopeValue[] Build(ScopeLayout layout, int count, bool packed)
{
    ScopeValue[] rows = new ScopeValue[count];
    ScopeAllocator allocator = new();
    for (int index = 0; index < count; index++)
    {
        Value[] input = [new NumberValue(index + .25), new NumberValue(index * 2 + .5),
            new NumberValue(-index + .75), new NumberValue(index * .5 + 1)];
        rows[index] = packed ? allocator.Create(layout, input) : new ScopeValue(layout, input);
    }
    return rows;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static double Scan(ScopeValue[] rows, int passes)
{
    double total = 0;
    for (int pass = 0; pass < passes; pass++)
    {
        total = 0;
        foreach (ScopeValue row in rows)
        {
            _ = row.TryGetNumber(0, out double x);
            _ = row.TryGetNumber(1, out double y);
            _ = row.TryGetNumber(2, out double z);
            _ = row.TryGetNumber(3, out double w);
            total += x;
            total += y;
            total += z;
            total += w;
        }
    }
    return total;
}

[MethodImpl(MethodImplOptions.NoInlining)]
static Mechanism VerifyRows(ScopeValue[] rows, bool packed)
{
    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
    PropertyInfo packedProperty = typeof(ScopeValue).GetProperty("IsPacked", flags)!;
    FieldInfo storageField = typeof(ScopeValue).GetField("fields", flags)!;
    HashSet<ScopeValue> identities = new(ReferenceEqualityComparer.Instance);
    HashSet<double[]> pages = new(ReferenceEqualityComparer.Instance);
    int packedRows = 0;
    for (int index = 0; index < rows.Length; index++)
    {
        ScopeValue row = rows[index];
        if (!identities.Add(row)) throw new InvalidOperationException("Scope construction reused an identity.");
        if (packedProperty.GetValue(row) is true)
        {
            packedRows++;
            object storage = storageField.GetValue(row)!;
            FieldInfo pageField = storage.GetType().GetFields(flags).Single(field => field.FieldType == typeof(double[]));
            pages.Add((double[])pageField.GetValue(storage)!);
        }
        double[] expected = [index + .25, index * 2 + .5, -index + .75, index * .5 + 1];
        for (int field = 0; field < expected.Length; field++)
        {
            if (!row.TryGetNumber(field, out double number)) throw new InvalidOperationException("Number field missing.");
            Verify(number, expected[field]);
        }
    }
    if (packedRows != (packed ? rows.Length : 0)) throw new InvalidOperationException("Requested storage mechanism was not used.");
    return new(packedRows, identities.Count, pages.Count, pages.Sum(page => (long)page.Length * sizeof(double)),
        (long)rows.Length * 4 * sizeof(double));
}

static void Verify(double actual, double expected)
{
    if (BitConverter.DoubleToInt64Bits(actual) != BitConverter.DoubleToInt64Bits(expected))
        throw new InvalidOperationException($"Expected {expected:R}, got {actual:R}.");
}

internal sealed record Mechanism(int PackedRows, int DistinctIdentities, int NumericPages,
    long AllocatedNumericPageBytes, long LogicalNumericPayloadBytes);
internal sealed record Observation(string Mode, long ConstructionAllocatedBytes, long ApproximateRetainedBytes,
    Mechanism Mechanism, double[] Milliseconds, long[] ScanAllocatedBytes);
internal sealed record Report(int Rows, int Passes, string Framework, string? TieredCompilation,
    string CoreSha256, Observation[] Observations);
[JsonSerializable(typeof(Report))]
internal sealed partial class ReportJson : JsonSerializerContext;
