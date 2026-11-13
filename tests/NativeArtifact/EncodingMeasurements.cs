using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Msync.Core.Native;

internal static class EncodingMeasurements
{
    internal sealed record Sample(double Milliseconds, long AllocatedBytes);
    internal sealed record Pair<T>(T Unpooled, T Compact);
    internal sealed record Report(string Input, string InputSha256, int UnpooledBytes, int CompactBytes,
        string UnpooledSha256, string CompactSha256, int Functions, int Instructions, string Boundary,
        string Runtime, Pair<IReadOnlyList<Sample>> Samples, Pair<Sample> Median);

    internal static void Run(string input, string output)
    {
        byte[] unpooled = File.ReadAllBytes(input);
        if (unpooled.Length < 14 || BinaryPrimitives.ReadInt32LittleEndian(unpooled.AsSpan(10)) < 0)
            throw new ArgumentException("Encoding comparison requires an original unpooled native-v1 artifact; use a frozen input, not a reconstructed reference writer.", nameof(input));
        NativeProgram program = NativeArtifact.Read(input);
        using MemoryStream compactBytes = new(); NativeArtifact.Write(program, compactBytes);
        byte[] compact = compactBytes.ToArray();
        string expected = NativeArtifact.ToJson(program);
        Check(unpooled); Check(compact);
        List<Sample> before = [], after = [];
        for (int pair = 0; pair < 7; pair++)
        {
            if (pair % 2 == 0) { before.Add(Measure(unpooled)); after.Add(Measure(compact)); }
            else { after.Add(Measure(compact)); before.Add(Measure(unpooled)); }
        }
        string directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.ChangeExtension(output, ".compact.mso"), compact);
        Report report = new(Path.GetFullPath(input), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(input))),
            unpooled.Length, compact.Length, Convert.ToHexStringLower(SHA256.HashData(unpooled)),
            Convert.ToHexStringLower(SHA256.HashData(compact)), program.Functions.Count,
            program.Functions.Sum(function => function.Instructions.Count),
            "Warmed in-memory parse, full validation and allocation-hint normalization; no source compilation or guest execution.",
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            new(before, after), new(Median(before), Median(after)));
        File.WriteAllText(output, JsonSerializer.Serialize(report, EncodingMeasurementJson.Default.Report) + "\n");
        Console.WriteLine($"Encoding {unpooled.Length} -> {compact.Length} bytes; checked document identity");

        void Check(byte[] bytes)
        {
            if (NativeArtifact.ToJson(NativeArtifact.Read(new MemoryStream(bytes))) != expected)
                throw new InvalidOperationException("Encoding benchmark changed semantic/document metadata");
        }
    }

    private static Sample Measure(byte[] bytes)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread(), started = Stopwatch.GetTimestamp();
        NativeProgram program = NativeArtifact.Read(new MemoryStream(bytes, writable: false));
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long count = GC.GetAllocatedBytesForCurrentThread() - allocated;
        GC.KeepAlive(program);
        return new(elapsed, count);
    }

    private static Sample Median(IReadOnlyList<Sample> samples) => new(
        samples.Select(sample => sample.Milliseconds).Order().ElementAt(samples.Count / 2),
        samples.Select(sample => sample.AllocatedBytes).Order().ElementAt(samples.Count / 2));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(EncodingMeasurements.Report))]
internal partial class EncodingMeasurementJson : JsonSerializerContext;
