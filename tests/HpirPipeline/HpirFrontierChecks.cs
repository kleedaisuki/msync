using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirFrontierChecks
{
    private const string Gated = """
        let unrelated = release /bin/sh("-c", "while [ ! -f unrelated.gate ]; do sleep 0.01; done; printf unrelated");
        unrelated::stdout -> unrelated.stdout;
        let selected = release /bin/sh("-c", "while [ ! -f selected.gate ]; do sleep 0.01; done; printf payload");
        let total = 0;
        for item in [1, 2] {
            acquire {
                if item == 1 { selected::stdout -> selected.stdout; continue; };
            };
            total = total + item;
        };
        $std::io::println(total as String);
        let verifier = /bin/sh("-c", "test $(cat selected.stdout) = payload && printf verified");
        verifier::stdout -> verified.txt;
        "open" -> unrelated.gate;
        acquire unrelated;
        """;

    private const string Break = """
        let background = release /bin/sh("-c", "while [ ! -f background.gate ]; do sleep 0.01; done; printf background");
        acquire loop {
            acquire { background::stdout -> stdout.txt; "open" -> background.gate; break; };
        };
        let verifier = /bin/sh("-c", "test $(cat stdout.txt) = background && printf verified");
        verifier::stdout -> verified.txt;
        """;

    internal static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", "frontier-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            for (int level = 0; level <= 3; level++)
            {
                foreach (bool native in HpirMachineImage.IsSupported ? new[] { false, true } : [false])
                {
                    string path = Path.Combine(root, $"{level}-{native}");
                    Directory.CreateDirectory(path);
                    CheckPrepared(path, level, native);
                    if (OperatingSystem.IsWindows()) continue;
                    CheckGated(path, level, native);
                    CheckBreak(path, level, native);
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS HPIR acquire frontiers on actual exits: continue, break, prepared transfer and selective process join");
    }

    private static void CheckPrepared(string path, int level, bool native)
    {
        const string source = """
            acquire { "first" -> first.txt; };
            "second" -> second.txt;
            acquire;
            """;
        HpirProgram program = Prepare(source, path, level);
        Require(program.PreparedTransfers.Count == 2
            && program.Imports.Count(import => import.Service == HpirService.SharedTransfer) == 2, "prepared work was replaced by residual work");
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        using var machine = native ? HpirMachineImage.Compile(program) : null;
        Run(program, machine, path, events, TextWriter.Null, token.Token);
        Require(File.ReadAllText(Path.Combine(path, "first.txt")) == "first"
            && File.ReadAllText(Path.Combine(path, "second.txt")) == "second"
            && events.Any(record => record.Name == "hpir.work.lexical_publish")
            && events.Any(record => record.Name == "hpir.work.owner_join"), "prepared effects did not reach their lexical and owner joins");
    }

    private static void CheckGated(string path, int level, bool native)
    {
        HpirProgram program = Prepare(Gated, path, level);
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        using var machine = native ? HpirMachineImage.Compile(program) : null;
        using var marker = new MarkerWriter { NewLine = "\n" };
        var orderedLaunch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Task.Run(() => Run(program, machine, path, events, marker, token.Token, record =>
        {
            if (record.Attributes.GetValueOrDefault("dependency.kind") == "work.lexical_predecessor")
                orderedLaunch.TrySetResult();
        }));
        try
        {
            Task first = Task.WhenAny(marker.Written.Task, run).WaitAsync(token.Token).GetAwaiter().GetResult();
            if (first == run) run.GetAwaiter().GetResult();
            first = Task.WhenAny(orderedLaunch.Task, run).WaitAsync(token.Token).GetAwaiter().GetResult();
            if (first == run) run.GetAwaiter().GetResult();
            Require(events.Count(record => record.Name == "hpir.work.lexical_publish") == 2
                && events.Any(record => record.Name == "hpir.work.lexical_publish"
                    && record.Metrics.GetValueOrDefault("work.process_flows") == 1)
                && !File.Exists(Path.Combine(path, "selected.stdout"))
                && !File.Exists(Path.Combine(path, "unrelated.stdout")), "continue did not publish exactly the selected pending flow");
            File.WriteAllText(Path.Combine(path, "selected.gate"), "open");
            run.WaitAsync(token.Token).GetAwaiter().GetResult();
        }
        finally
        {
            token.Cancel();
            if (!run.IsCompleted)
                try { run.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
        }
        Require(marker.ToString() == "2\n"
            && File.ReadAllText(Path.Combine(path, "selected.stdout")) == "payload"
            && File.ReadAllText(Path.Combine(path, "verified.txt")) == "verified"
            && File.ReadAllText(Path.Combine(path, "unrelated.stdout")) == "unrelated"
            && events.Any(record => record.Name == "hpir.work.process_join"
                && record.Metrics.GetValueOrDefault("work.joined") == 1), "selected process did not join its own transfer after continue");
    }

    private static void CheckBreak(string path, int level, bool native)
    {
        File.Delete(Path.Combine(path, "verified.txt"));
        HpirProgram program = Prepare(Break, path, level);
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        using var machine = native ? HpirMachineImage.Compile(program) : null;
        Run(program, machine, path, events, TextWriter.Null, token.Token);
        Require(events.Count(record => record.Name == "hpir.work.lexical_publish") == 2
            && File.ReadAllText(Path.Combine(path, "stdout.txt")) == "background"
            && File.ReadAllText(Path.Combine(path, "verified.txt")) == "verified", "nested break lost a lexical publication");
    }

    private static HpirProgram Prepare(string source, string path, int level)
    {
        string file = Path.Combine(path, "frontier.msync");
        File.WriteAllText(file, source);
        HpirProgram obj = Reload(Compiler.LowerHpir(Compiler.Analyze(File.ReadAllText(file), file), level));
        File.Delete(file);
        return Reload(HpirBinding.Prepare(obj, new(path, path)));
    }

    private static HpirProgram Reload(HpirProgram program)
    {
        using MemoryStream image = new();
        HpirArtifact.WriteProduced(program, image);
        image.Position = 0;
        return HpirArtifact.ReadTrusted(image);
    }

    private static void Run(HpirProgram program, HpirMachineImage? machine, string path, ConcurrentQueue<RuntimeTimelineRecord> events,
        TextWriter output, CancellationToken token, Action<RuntimeTimelineRecord>? notify = null)
    {
        var options = new ExecutionOptions(WorkingDirectory: path, Output: output)
        {
            Timeline = new(record =>
            {
                events.Enqueue(record);
                notify?.Invoke(record);
            }, "frontier")
        };
        HpirRunOutcome outcome = machine is null ? HpirExecutor.RunOutcome(program, options, token)
            : HpirExecutor.RunMachineOutcome(machine, options, token);
        Require(outcome.FailedTransfers == 0, "a selected transfer failed");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class MarkerWriter : StringWriter
    {
        internal readonly TaskCompletionSource Written = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteLineAsync(ReadOnlyMemory<char> text, CancellationToken token = default)
        {
            WriteLine(text.Span);
            Written.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
