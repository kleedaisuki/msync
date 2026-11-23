using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirFileRenameChecks
{
    private const string Name = "rename-journey.msync";
    private const string Gated = """
        let first = release /bin/sh("-c", "while [ ! -f first ]; do sleep 0.01; done; printf fresh");
        let second = release /bin/sh("-c", "while [ ! -f second ]; do sleep 0.01; done; printf old");
        let third = release /bin/sh("-c", "while [ ! -f third ]; do sleep 0.01; done; printf unrelated");
        first::stdout -> ./from.txt;
        second::stdout -> ./to.txt;
        third::stdout -> ./other.txt;
        $std::io::println("submitted");
        $std::fs::rename(./from.txt, ./to.txt);
        $std::io::println("renamed");
        """;

    internal static void Run()
    {
        string root = Directory.CreateDirectory(Path.Combine(".temp", "hpir", $"rename-{Guid.NewGuid():N}")).FullName;
        try
        {
            foreach (bool native in HpirMachineImage.IsSupported ? new[] { false, true } : [false])
            {
                string work = Directory.CreateDirectory(Path.Combine(root, native ? "native" : "physical")).FullName;
                CheckPrepared(work, native);
                CheckDryRunAndFailure(work, native);
                if (!OperatingSystem.IsWindows() && File.Exists("/bin/sh")) CheckGated(work, native);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS HPIR synchronous rename: both write dependencies, disjoint work, dry-run and source failure");
    }

    private static void CheckPrepared(string root, bool native)
    {
        const string source = """
            ./payload.txt -> ./from.txt;
            ./stale.txt -> ./to.txt;
            $std::fs::rename(./from.txt, ./to.txt);
            ./to.txt -> ./copied.txt;
            """;
        File.WriteAllText(Path.Combine(root, "payload.txt"), "fresh");
        File.WriteAllText(Path.Combine(root, "stale.txt"), "old");
        HpirProgram program = Compile(source, root);
        Require(program.Imports.Count(import => import.Service == HpirService.SharedTransfer) == 3,
            "A rename and the prepared transfers must share the live resource frontier.");
        Require(Run(program, new(), native).FailedTransfers == 0, "The prepared transfers should succeed.");
        Require(!File.Exists(Path.Combine(root, "from.txt"))
            && File.ReadAllText(Path.Combine(root, "to.txt")) == "fresh"
            && File.ReadAllText(Path.Combine(root, "copied.txt")) == "fresh",
            "Both writes must precede the synchronous rename, and the later copy must see the new bytes.");
    }

    private static void CheckDryRunAndFailure(string root, bool native)
    {
        HpirProgram missing = Compile("$std::fs::rename(./missing.txt, ./to.txt);", root);
        Require(Run(missing, new(DryRun: true), native).FailedTransfers == 0
            && File.ReadAllText(Path.Combine(root, "to.txt")) == "fresh",
            "A dry-run must not attempt the host rename or modify its destination.");
        try { _ = Run(missing, new(), native); }
        catch (MsyncException error) when (error.Span?.Source == Name && error.Message.Contains("missing.txt", StringComparison.Ordinal))
        {
            Require(File.ReadAllText(Path.Combine(root, "to.txt")) == "fresh", "A failed rename did not preserve its destination.");
            return;
        }
        throw new InvalidOperationException("The host rename failure lost its original source attribution.");
    }

    private static void CheckGated(string parent, bool native)
    {
        string root = Directory.CreateDirectory(Path.Combine(parent, "gates")).FullName;
        HpirProgram program = Compile(Gated, root);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var output = new Markers();
        var records = new ConcurrentQueue<RuntimeTimelineRecord>();
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new ExecutionOptions(Output: output, Jobs: 4)
        {
            Timeline = new(record =>
            {
                records.Enqueue(record);
                if (record.Name == "hpir.work.submit" && record.Attributes.GetValueOrDefault("work.kind") == "FileWrite"
                    && record.Metrics.ContainsKey("work.id"))
                    admitted.SetResult();
            }, "rename-dependencies")
        };
        Task<HpirRunOutcome> running = Task.Run(() => Run(program, options, native, stop.Token));
        try
        {
            AwaitMarker(output.Submitted.Task, running, stop.Token);
            AwaitMarker(admitted.Task, running, stop.Token);
            Release("first");
            Release("second");
            AwaitMarker(output.Renamed.Task, running, stop.Token);
            Require(!File.Exists(Path.Combine(root, "other.txt")) && !File.Exists(Path.Combine(root, "from.txt"))
                && File.ReadAllText(Path.Combine(root, "to.txt")) == "fresh", "Rename must wait for both matching writes, not the disjoint child.");
            Release("third");
            Require(running.WaitAsync(stop.Token).GetAwaiter().GetResult().FailedTransfers == 0
                && File.ReadAllText(Path.Combine(root, "other.txt")) == "unrelated",
                "Independent host work should complete normally.");
            RuntimeTimelineRecord admission = records.Single(record => record.Name == "hpir.file_rename.admit");
            ulong writes = admission.Metrics["resources.writes"];
            ulong dependencies = admission.Metrics["dependencies.live"];
            Require(dependencies == 2 && writes is >= 2 and <= 4
                && admission.Attributes["file.source"].EndsWith("from.txt", StringComparison.Ordinal)
                && admission.Attributes["file.destination"].EndsWith("to.txt", StringComparison.Ordinal),
                $"Rename must register both writes plus any physical aliases and only their predecessors: {writes} writes, {dependencies} dependencies.");
        }
        finally
        {
            stop.Cancel();
            if (!running.IsCompleted)
                try { _ = running.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
        }

        void Release(string gate) => File.WriteAllText(Path.Combine(root, gate), "");
    }

    private static void AwaitMarker(Task marker, Task<HpirRunOutcome> running, CancellationToken token)
    {
        Task first = Task.WhenAny(marker, running).WaitAsync(token).GetAwaiter().GetResult();
        if (first == running) _ = running.GetAwaiter().GetResult();
        Require(marker.IsCompletedSuccessfully, "Expected the rename journey output marker.");
    }

    private static HpirRunOutcome Run(HpirProgram program, ExecutionOptions options, bool native, CancellationToken token = default)
    {
        if (!native) return HpirExecutor.RunOutcome(program, options, token);
        using var image = HpirMachineImage.Compile(program);
        return HpirExecutor.RunMachineOutcome(image, options, token);
    }

    private static HpirProgram Compile(string source, string directory)
    {
        HpirProgram portable = RoundTrip(Compiler.LowerHpir(Compiler.Analyze(source, Name), 2));
        Require(portable.Imports.Any(import => import.Service == HpirService.RenameFile), "Expected the typed rename service.");
        return RoundTrip(HpirBinding.Prepare(portable, new(directory, directory)));
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var image = new MemoryStream();
        HpirArtifact.WriteProduced(program, image);
        image.Position = 0;
        return HpirArtifact.ReadTrusted(image);
    }

    private sealed class Markers : StringWriter
    {
        internal TaskCompletionSource Submitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Renamed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Span.SequenceEqual("submitted")) Submitted.SetResult();
            else if (buffer.Span.SequenceEqual("renamed")) Renamed.SetResult();
            else throw new InvalidOperationException("Unexpected output in rename journey.");
            return Task.CompletedTask;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
