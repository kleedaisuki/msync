using System.Collections.Concurrent;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirNumberEffectChecks
{
    private const string Name = "number-effect.msync";
    private const string Ready = """
        let value = if exists ./select {
            let child = release /bin/sh("-c", "exit 12");
            child::exit_code + 1
        } else { 0 };
        let receipt = (value as String) -> ./ready.txt;
        receipt
        """;
    private const string Gated = """
        fn churn(limit: Number) -> Number {
            let i = 0;
            loop {
                if i >= limit { break i; };
                let text = i as String;
                i = (text as Number) + 1;
            }
        };
        let child = release /bin/sh("-c", "while [ ! -f go ]; do sleep 0.01; done; cat seed.txt");
        let seed = child::stdout as Number;
        let rows = $std::ranges::materialize([scope { x: seed, y: seed + 1 },
            scope { x: seed + 2, y: seed + 3 }]);
        let total = 0;
        for row in rows { total = total + row::x + row::y; };
        let receipt = (total as String) -> ./fold.txt;
        let ballast = churn(18000);
        if ballast != 18000 { return; };
        $std::io::println("admitted");
        """;

    internal static void Run()
    {
        if (OperatingSystem.IsWindows()) return;
        string root = Path.GetFullPath(Path.Combine(".temp", "hpir", $"number-effect-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(root);
        try
        {
            for (int level = 0; level <= 3; level++)
            {
                HpirProgram program = Compile(root, Gated, level);
                Check(program, root, native: false, seed: "7", expected: "34");
                if (HpirMachineImage.IsSupported) Check(program, root, native: true, seed: "7", expected: "34");
                CheckReady(Compile(root, Ready, level), root);
            }
            HpirProgram observed = Compile(root, Gated.Replace("if ballast != 18000 { return; };", "",
                StringComparison.Ordinal) + "\nreceipt", 2);
            Check(observed, root, native: false, seed: "x", expected: null);
            if (HpirMachineImage.IsSupported) Check(observed, root, native: true, seed: "x", expected: null);
            int effect = Gated.IndexOf("let receipt", StringComparison.Ordinal);
            string inside = Gated[..effect] + "fn emit(total: Number) {\n" + Gated[effect..] + "};\nemit(total);\n";
            HpirProgram framed = Compile(root, inside, 2);
            Check(framed, root, native: false, seed: "7", expected: "34", callerRoot: true);
            if (HpirMachineImage.IsSupported) Check(framed, root, native: true, seed: "7", expected: "34", callerRoot: true);
            Check(framed, root, native: false, seed: "7", expected: "34", cancel: true);
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS pending deferred Number file admission, exact GC root, selected original failure and false receipt");
    }

    private static void CheckReady(HpirProgram program, string root)
    {
        string file = Path.Combine(root, "ready.txt");
        File.Delete(file);
        var observations = new ConcurrentQueue<RuntimeTimelineRecord>();
        var options = new ExecutionOptions(WorkingDirectory: root) { Timeline = new(observations.Enqueue, Name) };
        HpirRunOutcome result = HpirExecutor.RunOutcome(program, options);
        Require(result.Value == 1 && result.FailedTransfers == 0 && File.ReadAllText(file) == "0"
            && !observations.Any(item => item.Name == "hpir.guest.effect_number.publish")
            && observations.Any(item => item.Name == "hpir.effect.number_arguments"
                && item.Attributes.GetValueOrDefault("number.carrier") == "inline"),
            "A mixed carrier's ready zero must bypass the pending handoff.");
    }

    private static HpirProgram Compile(string root, string source, int level)
    {
        var observations = new List<CompilationObservation>();
        var trace = new CompilationTrace(observations.Add, includeOptimizerPasses: true);
        HpirProgram portable = Reload(Compiler.LowerHpir(Compiler.Analyze(source, Name, trace), level, trace));
        HpirProgram program = Reload(HpirBinding.Prepare(portable, new(root, root)));
        Require(program.Imports.Count(import => import.Service == HpirService.LiveTransferNumberText) == 1
            && observations.Any(item => item.Metrics?.GetValueOrDefault("paths.transfer_number_text") == 1),
            "The fold must retain its typed Number until the real FileOut observer.");
        return program;
    }

    private static void Check(HpirProgram program, string root, bool native, string seed, string? expected, bool cancel = false,
        bool callerRoot = false)
    {
        File.Delete(Path.Combine(root, "go"));
        File.Delete(Path.Combine(root, "fold.txt"));
        File.WriteAllText(Path.Combine(root, "seed.txt"), seed);
        var observations = new ConcurrentQueue<RuntimeTimelineRecord>();
        var failures = new ConcurrentQueue<MsyncException>();
        using var output = new Marker();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var options = new ExecutionOptions(WorkingDirectory: root, Output: output)
        {
            Timeline = new(observations.Enqueue, Name),
            ReportError = failures.Enqueue
        };
        Task<HpirRunOutcome> running = Task.Run(() =>
        {
            if (!native) return HpirExecutor.RunOutcome(program, options, stop.Token);
            using var machine = HpirMachineImage.Compile(program);
            return HpirExecutor.RunMachineOutcome(machine, options, stop.Token);
        });
        try
        {
            Task first = Task.WhenAny(output.Admitted.Task, running).WaitAsync(stop.Token).GetAwaiter().GetResult();
            if (first == running) _ = running.GetAwaiter().GetResult();
            string file = Path.Combine(root, "fold.txt");
            Require(first == output.Admitted.Task && !running.IsCompleted && !File.Exists(file),
                "An admitted Number file effect blocked unrelated guest progress or wrote before the Process supplied bytes.");
            if (cancel)
            {
                stop.Cancel();
                try { _ = running.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
                catch (OperationCanceledException)
                {
                    Require(!File.Exists(file) && failures.IsEmpty, "A cancelled Number effect left a file or reported a false failure.");
                    return;
                }
                throw new Exception("Cancelling an unfinished Number effect did not cancel the guest.");
            }
            File.WriteAllText(Path.Combine(root, "go"), "");
            HpirRunOutcome result = running.WaitAsync(stop.Token).GetAwaiter().GetResult();
            Require(observations.Count(item => item.Name == "hpir.guest.effect_number.publish") == 1,
                "A pending worker should own exactly one typed handoff.");
            Require(observations.Any(item => item.Name == "runtime.dependency"
                && item.Attributes.GetValueOrDefault("dependency.kind") == "transfer.number_input"
                && item.Attributes.ContainsKey("dependency.span_id")),
                "The resource admission lost the causal link to its typed Number handoff.");
            if (expected is { } bytes)
            {
                string? observed = File.Exists(file) ? File.ReadAllText(file) : null;
                Require(result.FailedTransfers == 0 && observed == bytes,
                    $"Expected fold {bytes}, observed {observed ?? "(missing)"}; {string.Join("; ", failures.Select(error => error.Message))}");
                Require(callerRoot || observations.Any(item => item.Name == "hpir.heap.collect"
                    && item.Metrics.GetValueOrDefault("roots.effect_number_marked") > 0),
                    "The pending Number must remain a precise root after its only guest use.");
            }
            else
            {
                Require(result.FailedTransfers == 1 && result.Value == 0 && !File.Exists(file),
                    "The failed original parse must publish a false receipt and no file.");
                Require(failures.TryDequeue(out var error) && error.Span is { Source: Name } span
                    && span.Start <= Gated.IndexOf("child::stdout", StringComparison.Ordinal)
                    && span.Start + span.Length >= Gated.IndexOf("as Number;", StringComparison.Ordinal)
                    && error.Message.Contains("Cannot cast String 'x' to Number", StringComparison.Ordinal),
                    "The effect lost the originating cast failure.");
            }
            Require(failures.IsEmpty, "The Number effect emitted an unexpected or duplicate diagnostic.");
        }
        finally
        {
            stop.Cancel();
            if (!running.IsCompleted)
                try { _ = running.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
        }
    }

    private static HpirProgram Reload(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private sealed class Marker : StringWriter
    {
        internal readonly TaskCompletionSource Admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Require(buffer.Span.SequenceEqual("admitted"), "Unexpected Number effect gate output.");
            Admitted.SetResult();
            return Task.CompletedTask;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
