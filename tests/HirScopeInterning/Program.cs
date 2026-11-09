using System.Collections.Immutable;
using System.Diagnostics;
using Msync.Core;
using Msync.Hir;

int checks = 0;
HirBuilder builder = new();
HirTypeId number = builder.Type(TypeRef.Number), text = builder.Type(TypeRef.String);
HirScopeLayout original = new([new("value", number)]);
builder.ScopeLayouts.Add(original);
Check(builder.InternScopeLayout(new([new("value", number)])) == 0, "Direct bound rows must keep their IDs.");
builder.ScopeLayouts.Add(original);
Check(builder.InternScopeLayout(new([new("value", number)])) == 0, "The first equal row must stay canonical.");
Check(builder.ScopeLayouts.Count == 2, "A repeated query must not append a row.");
using (builder.BeginTransaction())
{
    int pending = builder.InternScopeLayout(new([new("new", text)]));
    Check(pending == 2, "A pending layout must append normally.");
}
Check(builder.ScopeLayouts.Count == 2, "Abandoned generation retained a layout.");
builder.ScopeLayouts.Add(new([new("replacement", text)]));
Check(builder.InternScopeLayout(new([new("replacement", text)])) == 2, "Rollback left a stale indexing cursor.");
Check(builder.InternScopeLayout(new([new("new", text)])) == 3, "Rollback retained a stale canonical ID.");

Random random = new(739);
for (int index = 0; index < 5000; index++)
{
    ImmutableArray<HirField> fields = [new("field_" + random.Next(40), random.Next(2) == 0 ? number : text,
        random.Next(2) == 0), new("tail", number)];
    HirScopeLayout candidate = new(fields);
    int expected = Linear(builder, candidate);
    Check(builder.InternScopeLayout(candidate) == expected, "Indexed structural equality differs from linear equality.");
}
Console.WriteLine($"PASS {checks} structural layout identity/rollback checks");
if (args.Contains("--benchmark", StringComparer.Ordinal))
{
    foreach (int count in new[] { 64, 512, 2048 }) Benchmark(count);
}

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

static int Linear(HirBuilder target, HirScopeLayout layout)
{
    for (int index = 0; index < target.ScopeLayouts.Count; index++)
        if (target.ScopeLayouts[index].Fields.AsSpan().SequenceEqual(layout.Fields.AsSpan())) return index;
    int result = target.ScopeLayouts.Count;
    target.ScopeLayouts.Add(layout);
    return result;
}

static void Benchmark(int count)
{
    HirBuilder target = new();
    for (int index = 0; index < count; index++) target.ScopeLayouts.Add(new([new("field_" + index, new(0)), new("tail", new(1))]));
    HirScopeLayout query = new([.. target.ScopeLayouts[^1].Fields]);
    long firstBytes = GC.GetAllocatedBytesForCurrentThread();
    long first = Stopwatch.GetTimestamp();
    int canonical = target.InternScopeLayout(query);
    double cold = Stopwatch.GetElapsedTime(first).TotalMicroseconds;
    firstBytes = GC.GetAllocatedBytesForCurrentThread() - firstBytes;
    for (int index = 0; index < 1000; index++) { _ = Linear(target, query); _ = target.InternScopeLayout(query); }
    const int iterations = 20_000;
    long linearStart = Stopwatch.GetTimestamp();
    for (int index = 0; index < iterations; index++) _ = Linear(target, query);
    double linear = Stopwatch.GetElapsedTime(linearStart).TotalMilliseconds;
    long bytes = GC.GetAllocatedBytesForCurrentThread();
    long indexedStart = Stopwatch.GetTimestamp();
    for (int index = 0; index < iterations; index++) _ = target.InternScopeLayout(query);
    double indexed = Stopwatch.GetElapsedTime(indexedStart).TotalMilliseconds;
    bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
    if (canonical != count - 1 || bytes != 0) throw new InvalidOperationException("Steady interning changed identity or allocated.");
    Console.WriteLine($"layouts={count} lookups={iterations} linear_ms={linear:F3} indexed_ms={indexed:F3} cold_us={cold:F3} cold_B={firstBytes} steady_B={bytes}");
}
