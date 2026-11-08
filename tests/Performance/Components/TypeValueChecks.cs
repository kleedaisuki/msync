using System.Diagnostics;
using System.Text.Json;
using Msync.Core;

namespace Msync.Components;

internal static class TypeValueChecks
{
    private static readonly TypeRef[] Scalars = [TypeRef.Bool, TypeRef.String, TypeRef.Path, TypeRef.Function,
        TypeRef.Scope, TypeRef.Process, TypeRef.Connection, TypeRef.Number];
    private static object? retained;

    public static bool TryRun(string[] arguments)
    {
        if (!arguments.Contains("--check-type-values", StringComparer.Ordinal)) return false;
        CheckRendering();
        CheckOperations();
        Console.Error.WriteLine("TypeRef finite formatting domain, recursive elements and value-operation properties passed.");
        if (arguments.Contains("--benchmark-type-values", StringComparer.Ordinal)) Benchmark();
        return true;
    }

    private static void CheckRendering()
    {
        for (int bits = 0; bits <= ushort.MaxValue; bits++)
        {
            var type = new TypeRef((TypeKind)bits);
            Check(type.ToString() == OldText(type), $"Type text changed for bits {bits}");
        }
        TypeRef[] elements = [TypeRef.Null, TypeRef.Bool, TypeRef.Any, new(TypeKind.Range, TypeRef.Path),
            new(TypeKind.Range | TypeKind.Null, new(TypeKind.Range, new(TypeKind.String | TypeKind.Path)))];
        foreach (TypeRef element in elements)
            for (int bits = 0; bits <= (ushort)TypeKind.Any; bits++)
            {
                var type = new TypeRef((TypeKind)bits, element);
                Check(type.ToString() == OldText(type), $"Element type text changed for bits {bits}");
            }
    }

    private static void CheckOperations()
    {
        for (int bits = 0; bits <= ushort.MaxValue; bits++)
        {
            var type = new TypeRef((TypeKind)bits, bits % 3 == 0 ? TypeRef.Path : null);
            Check(type.WithNull == new TypeRef(type.Kind | TypeKind.Null, type.Element), "Null admission changed");
            Check(type.WithoutNull == new TypeRef(type.Kind & ~TypeKind.Null, type.Element), "Null removal changed");
            Check(ReferenceEquals(type.Nullable ? type.WithNull : type.WithoutNull, type), "No-op null conversion allocated a new value");
        }
        Random random = new(808);
        for (int iteration = 0; iteration < 20_000; iteration++)
        {
            TypeRef left = RandomType(random, iteration % 4);
            TypeRef right = iteration % 5 == 0 ? left with { } : RandomType(random, iteration % 3);
            TypeRef expected = OldUnion(left, right);
            TypeRef actual = TypeRef.Union(left, right);
            Check(actual == expected, "Union structure changed");
            Check(actual.Accepts(left) && actual.Accepts(right), "Union lost an admitted input");
            Check(actual == TypeRef.Union(right, left), "Union is not commutative");
            Check(ReferenceEquals(TypeRef.Union(left, left), left), "Identity union allocated a new value");
        }
        var nullable = TypeRef.Bool.WithNull;
        Check(ReferenceEquals(TypeRef.Union(TypeRef.Bool, nullable), nullable), "Union did not reuse its unchanged right value");
    }

    private static TypeRef RandomType(Random random, int depth) =>
        new((TypeKind)random.Next(ushort.MaxValue + 1), depth == 0 ? null : RandomType(random, depth - 1));

    private static TypeRef OldUnion(TypeRef left, TypeRef right) => new(left.Kind | right.Kind,
        left.Element is null ? right.Element : right.Element is null ? left.Element : OldUnion(left.Element, right.Element));

    private static string OldText(TypeRef type)
    {
        TypeKind core = type.Kind & ~TypeKind.Null;
        if (core == TypeKind.Never) return type.Kind == TypeKind.Null ? "Null" : "Never";
        var names = Enum.GetValues<TypeKind>().Where(kind => kind != TypeKind.Never && kind != TypeKind.Null
                && kind != TypeKind.Any && (core & kind) != 0)
            .Select(kind => kind == TypeKind.Range && type.Element is not null ? $"Range<{OldText(type.Element)}>" : kind.ToString());
        return string.Join(" | ", names) + (type.Nullable ? "?" : "");
    }

    private static void Benchmark()
    {
        int cursor = 0;
        var nullable = Scalars.Select(type => type.WithNull).ToArray();
        using var output = Console.OpenStandardOutput();
        using Utf8JsonWriter json = new(output, new() { Indented = true });
        json.WriteStartObject();
        json.WriteString("framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        json.WriteString("architecture", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
        json.WriteNumber("samples", 9);
        json.WriteNumber("iterations_per_sample", 20_000);
        json.WriteStartArray("operations");
        Report("old_scalar_text", () => OldText(Scalars[cursor++ & 7]), json);
        Report("current_scalar_text", () => Scalars[cursor++ & 7].ToString(), json);
        Report("old_without_null", () => { TypeRef type = Scalars[cursor++ & 7]; return new TypeRef(type.Kind & ~TypeKind.Null, type.Element); }, json);
        Report("current_without_null", () => Scalars[cursor++ & 7].WithoutNull, json);
        Report("old_with_null", () => { TypeRef type = nullable[cursor++ & 7]; return new TypeRef(type.Kind | TypeKind.Null, type.Element); }, json);
        Report("current_with_null", () => nullable[cursor++ & 7].WithNull, json);
        Report("old_identity_union", () => { TypeRef type = Scalars[cursor++ & 7]; return OldUnion(type, type); }, json);
        Report("current_identity_union", () => { TypeRef type = Scalars[cursor++ & 7]; return TypeRef.Union(type, type); }, json);
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void Report(string name, Func<object> operation, Utf8JsonWriter json)
    {
        const int iterations = 20_000;
        for (int warmup = 0; warmup < iterations; warmup++) retained = operation();
        double[] samples = new double[9];
        double allocated = 0;
        for (int sample = 0; sample < samples.Length; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int iteration = 0; iteration < iterations; iteration++) retained = operation();
            samples[sample] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations;
            allocated += (double)(GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        }
        double[] sorted = samples.Order().ToArray();
        json.WriteStartObject();
        json.WriteString("operation", name);
        json.WriteNumber("p50_nanoseconds", sorted[4]);
        json.WriteNumber("p95_nanoseconds", sorted[7] + (sorted[8] - sorted[7]) * 0.6);
        json.WriteNumber("allocated_bytes", allocated / samples.Length);
        json.WriteStartArray("raw_nanoseconds");
        foreach (double sample in samples) json.WriteNumberValue(sample);
        json.WriteEndArray();
        json.WriteEndObject();
        GC.KeepAlive(retained);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
