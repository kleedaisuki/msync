using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirSharedTransferChecks
{
    private const string Name = "shared-transfer.msync";

    internal static void Run()
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".temp", "hpir", $"shared-transfer-{Guid.NewGuid():N}");
        string detached = Directory.CreateDirectory(Path.Combine(root, "detached")).FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "source.txt"), "replacement 🧪");
            File.WriteAllText(Path.Combine(root, "other.txt"), "other");
            Forward(root, detached);
            Reverse(root, detached);
            Failure(root, detached);
            Console.WriteLine("PASS HPIR shared transfer frontier: forward and reverse cross-edges, independent ready read, one reported failure");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Forward(string root, string detached)
    {
        const string source = """
            let io = $std::io;
            ./source.txt -> ./related.txt;
            io::println(io::file_size(./other.txt) as String);
            io::read_text(./related.txt) -> ./forward.txt;
            """;
        File.WriteAllText(Path.Combine(root, "related.txt"), "stale");
        HpirProgram executable = Compile(source, root);
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        using var release = new ManualResetEventSlim();
        using var output = new StringWriter { NewLine = "\n" };
        _ = HpirExecutor.Run(executable, new(WorkingDirectory: detached, Output: output, Jobs: 1)
        {
            Timeline = new(item =>
            {
                events.Enqueue(item);
                if (!OperatingSystem.IsWindows() && item.Name == "file.sync" && !release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("A disjoint read or pending transfer waited on an unrelated prepared writer.");
                if (IsDestination(item, "forward.txt")) release.Set();
            }, "prepared-to-residual")
        });
        if (output.ToString() != "5\n" || !Matches(root, "forward.txt")
            || !events.Any(item => item.Name == "hpir.file_read.admit" && item.Attributes["file.operation"] == "io.file_size"
                && item.Metrics.GetValueOrDefault("dependencies.cross") == 0 && item.Attributes["work.outcome"] == "ready")
            || !OperatingSystem.IsWindows() && !events.Any(item => item.Name == "hpir.file_read.admit"
                && item.Attributes["file.operation"] == "io.read_text" && item.Metrics.GetValueOrDefault("dependencies.cross") == 1))
            throw new Exception("Prepared writer did not delay only the overlapping residual read.");
    }

    private static void Reverse(string root, string detached)
    {
        const string source = """
            $std::io::read_text(./source.txt) -> ./later.txt;
            ./later.txt -> ./reverse.txt;
            """;
        File.WriteAllText(Path.Combine(root, "later.txt"), "stale");
        HpirProgram executable = Compile(source, root);
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        using var release = new ManualResetEventSlim();
        _ = HpirExecutor.Run(executable, new(WorkingDirectory: detached, Jobs: 1)
        {
            Timeline = new(item =>
            {
                events.Enqueue(item);
                if (item.Name == "hpir.file_read.read" && !release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The prepared transfer was not admitted while its residual writer held the resource.");
                if (IsDestination(item, "reverse.txt")) release.Set();
            }, "residual-to-prepared")
        });
        if (!Matches(root, "reverse.txt") || !events.Any(item => IsDestination(item, "reverse.txt")
            && item.Attributes.GetValueOrDefault("transfer.origin") == "prepared" && item.Metrics.GetValueOrDefault("dependencies.cross") == 1))
            throw new Exception("Prepared transfer did not wait for its residual predecessor.");
    }

    private static void Failure(string root, string detached)
    {
        const string source = """
            ./missing.txt -> ./failed.txt;
            ./source.txt -> ./failed.txt;
            $std::io::read_text(./source.txt) -> ./survivor.txt;
            """;
        HpirProgram executable = Compile(source, root, preparedCount: 2);
        if (!executable.PreparedTransfers[1].Predecessors.SequenceEqual([0]))
            throw new Exception("Mixed execution dropped the precomputed bound write order.");
        var errors = new ConcurrentQueue<MsyncException>();
        var result = HpirExecutor.RunOutcome(executable, new(WorkingDirectory: detached) { ReportError = errors.Enqueue });
        if (result.FailedTransfers != 1 || errors.Count != 1 || !errors.TryPeek(out var error) || error.Span?.Source != Name
            || !error.Message.Contains("missing.txt", StringComparison.Ordinal) || !Matches(root, "survivor.txt") || !Matches(root, "failed.txt"))
            throw new Exception("A failed shared transfer did not report once or obstructed independent work.");
    }

    private static bool IsDestination(RuntimeTimelineRecord item, string name) => item.Name == "hpir.effect.admit"
        && item.Attributes.GetValueOrDefault("transfer.destination")?.EndsWith(name, StringComparison.Ordinal) == true;

    private static bool Matches(string root, string destination) => File.ReadAllText(Path.Combine(root, destination)) == "replacement 🧪";

    private static HpirProgram Compile(string source, string root, int preparedCount = 1)
    {
        HpirProgram objectImage = RoundTrip(Compiler.LowerHpir(Compiler.Analyze(source, Name), 2));
        HpirProgram executable = HpirBinding.Prepare(objectImage, new(root, root));
        var shared = executable.Imports.Where(import => import.Service == HpirService.SharedTransfer).ToArray();
        if (shared.Length != preparedCount || shared.Any(import => import.Result != HpirWidth.Void || import.Arguments.Length != 0)
            || executable.PreparedTransfers.Count != preparedCount
            || !executable.Imports.Any(import => import.Service == HpirService.LiveTransferFutureText)
            || !ReferenceEquals(objectImage.Functions, executable.Functions))
            throw new Exception("The producer did not select bound effect-only calls coexisting with residual file work.");
        return RoundTrip(executable);
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var image = new MemoryStream();
        HpirArtifact.WriteProduced(program, image);
        image.Position = 0;
        return HpirArtifact.ReadTrusted(image);
    }
}
