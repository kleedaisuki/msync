using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirFileByteChecks
{
    private const string Name = "file-bytes.msync";
    private const string Shared = """
        ./seed.bin -> ./target.bin;
        let seen = 0;
        let map = $std::ranges::transform([0, 255], fn(value: Number) {
            seen = seen + 1;
            value
        });
        $std::io::println(seen as String);
        $std::io::append_bytes("./target.bin", map);
        $std::io::println(seen as String);
        """;

    private const string Bulk = """
        let values = $std::ranges::materialize(
            $std::ranges::transform($std::ranges::iota(0, 8231), fn(_: Number) { 42 })
        );
        $std::io::write_bytes(./large.bin, values);
        """;

    private static IEnumerable<bool> Engines() => HpirMachineImage.IsSupported ? [false, true] : [false];

    internal static void Run()
    {
        string root = Directory.CreateDirectory(Path.Combine(".temp", "hpir", $"byte-write-{Guid.NewGuid():N}")).FullName;
        string other = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
        try
        {
            foreach (int level in new[] { 0, 1, 2, 3 })
            {
                var program = Compile(Shared, root, level);
                foreach (bool native in Engines()) CheckAppend(program, root, other, native);
            }
            var bulk = Compile(Bulk, root, 2);
            var invalid = Compile("$std::io::write_bytes(./target.bin, ["
                + string.Join(", ", Enumerable.Repeat("42", 40)) + ", 256]);", root, 2);
            foreach (bool native in Engines())
            {
                CheckBulk(bulk, root, native);
                CheckInvalid(invalid, root, native);
                CheckAppend(Compile(Shared, root, 2), root, other, native, dryRun: true);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS HPIR detached byte writes: bulk, append ordering, demand, dry-run and staged rollback");
    }

    private static void CheckAppend(HpirProgram program, string root, string other, bool native, bool dryRun = false)
    {
        File.WriteAllBytes(Path.Combine(root, "seed.bin"), [15, 16]);
        File.Delete(Path.Combine(root, "target.bin"));
        using var output = new StringWriter { NewLine = "\n" };
        Execute(program, new(WorkingDirectory: other, Output: output, DryRun: dryRun), native);
        Require(output.ToString().EndsWith(dryRun ? "0\n0\n" : "0\n2\n", StringComparison.Ordinal),
            "Byte callbacks ran at construction or were not consumed exactly once.");
        if (dryRun) Require(!File.Exists(Path.Combine(root, "target.bin")), "Dry run published a file.");
        else Require(File.ReadAllBytes(Path.Combine(root, "target.bin")).AsSpan().SequenceEqual(new byte[] { 15, 16, 0, 255 }),
            "Append bypassed the preceding transfer or changed byte values.");
        Require(!Directory.EnumerateFileSystemEntries(other).Any(), "The detached artifact escaped its bound directory.");
    }

    private static void CheckBulk(HpirProgram program, string root, bool native)
    {
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        Execute(program, new ExecutionOptions(BufferSize: 8192) { Timeline = new(events.Enqueue, "byte-bulk") }, native);
        byte[] bytes = File.ReadAllBytes(Path.Combine(root, "large.bin"));
        Require(bytes.Length == 8231 && bytes.All(static item => item == 42), "Byte staging lost data at a chunk boundary.");
        Require(events.Count(item => item.Name == "hpir.file_bytes.chunk") == 3
            && events.Any(item => item.Name == "hpir.file_bytes.write"
                && item.Metrics.GetValueOrDefault("source.bytes_consumed") == 8231), "Physical writer work was not observed.");
    }

    private static void CheckInvalid(HpirProgram program, string root, bool native)
    {
        File.WriteAllBytes(Path.Combine(root, "target.bin"), [0xa1, 0xb2]);
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        try
        {
            Execute(program, new ExecutionOptions(BufferSize: 16) { Timeline = new(events.Enqueue, "byte-failure") }, native);
            throw new Exception("A byte above 255 was accepted.");
        }
        catch (MsyncException error)
        {
            Require(error.Message.Contains(HpirFileByteImports.InvalidByte, StringComparison.Ordinal)
                && error.Span is { Source: Name }, "Invalid bytes lost their source diagnostic.");
        }
        Require(File.ReadAllBytes(Path.Combine(root, "target.bin")).AsSpan().SequenceEqual(new byte[] { 0xa1, 0xb2 })
            && !Directory.EnumerateFiles(root, ".msync-*").Any(), "Failed replacement changed the original or leaked its stage.");
        Require(events.Count(item => item.Name == "hpir.file_bytes.chunk") >= 2,
            "The failure did not exercise cleanup of an already streamed replacement.");
    }

    private static HpirProgram Compile(string source, string root, int level)
    {
        HpirProgram program = RoundTrip(Compiler.LowerHpir(Compiler.Analyze(source, Name), level));
        return RoundTrip(HpirBinding.Prepare(program, new(root, root)));
    }

    private static void Execute(HpirProgram program, ExecutionOptions options, bool native)
    {
        using var image = native ? HpirMachineImage.Compile(program) : null;
        if (image is null) HpirExecutor.Run(program, options);
        else HpirExecutor.RunMachineCandidate(image, options);
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
