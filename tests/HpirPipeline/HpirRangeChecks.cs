using System.Collections.Concurrent;
using System.Globalization;
using Msync.Compiler;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirRangeChecks
{
    private const string PendingLimit = """
        let child = release /bin/sh("-c", "while [ ! -f allow-exit ]; do sleep 0.01; done; exit 5");
        let limit = child::exit_code;
        """;

    internal static void Run()
    {
        const string window = """
            let view = $std::ranges::take($std::ranges::drop($std::ranges::iota(0, 12), 2), 3);
            let total = 0;
            for n in view { total = total + n; };
            $std::io::println(total as String);
            """;
        for (int level = 0; level <= 3; level++)
            Run($"numeric-window-O{level}", window, "9", 3, 1, level);
        Run("descending", Sum("$std::ranges::drop($std::ranges::sequence(7, 1, -2), 1)"), "8", 2, 1);
        Run("wide-drop", Sum("$std::ranges::drop($std::ranges::iota(0, 4), 9223372036854774784)"), "0", 0, 1);
        const string stalled = "$std::ranges::iota(9007199254740992, 9007199254741000)";
        Run("zero-demand", Sum($"$std::ranges::take($std::ranges::drop({stalled}, 100), 0)"), "0", 0, 1);
        Run("one-demand", Sum($"$std::ranges::take({stalled}, 1)"), "9007199254740992", 1, 1);
        Fails("stalled-prefix", Sum($"$std::ranges::drop($std::ranges::take({stalled}, 2), 3)"), "no longer advances");
        Fails("invalid-count", Sum("$std::ranges::take($std::ranges::iota(0, 4), 9223372036854775808)"),
            "nonnegative integral count");
        const string reopen = """
            let view = $std::ranges::take($std::ranges::drop($std::ranges::iota(0, 12), 2), 3);
            let total = 0;
            for n in view { total = n; break; };
            for n in view { total = total + n; };
            $std::io::println(total as String);
            """;
        Run("cursor-after-break", reopen, "11", 4, 1);
        for (int level = 0; !OperatingSystem.IsWindows() && level <= 3; level++)
        {
            CheckDeferredBounds(level);
            CheckWindowDemand(level);
        }
        if (!OperatingSystem.IsWindows()) CheckMixedRangeValidation();
        Console.WriteLine("PASS HPIR typed iota/drop/take/for: binary64 demand fences and physical stack cursor, O0–O3");
    }

    private static void CheckDeferredBounds(int level)
    {
        const string setup = """
            let view = $std::ranges::iota(0, limit);
            let total = 0;
            """;
        const string churn = """
            fn wrap(seed: Number) { fn { seed } };
            let index = 0;
            loop {
                if index >= 8000 { break; };
                let get = wrap(index);
                total = total + get() % 1;
                index = index + 1;
            };
            """;
        const string consume = """
            $std::io::println("constructed");
            for n in view { total = total + n; };
            for n in view { total = total + n; };
            total
            """;
        string source = string.Join(Environment.NewLine, PendingLimit, setup, level == 2 ? churn : "", consume);
        HpirProgram program = Compile("deferred-range", source, level);
        Require(program.Imports.Any(import => import.Service == HpirService.RangeDeferredSequence),
            "numeric sequence lost its deferred builder through the guest call ABI");
        var events = RunGated(program, 20);
        Require(events.Any(item => item.Name == "hpir.range.capture" && item.Metrics["range.pending_bounds"] == 1)
            && events.Count(item => item.Name == "hpir.range.resolve" && item.Outcome == "ok") == 1,
            "construction must capture the pending bound and both opens must share its one resolution");
        if (level == 2)
            Require(events.Any(item => item.Name == "hpir.heap.collect"),
                "the unopened plan and its bound must survive a real intervening guest collection");
    }

    private static void CheckWindowDemand(int level)
    {
        const string source = PendingLimit + """

            let view = $std::ranges::iota(0, 12);
            $std::io::println("constructed");
            let selected = $std::ranges::take(view, limit);
            $std::io::println("bounded");
            let total = 0;
            for n in selected { total = total + n; };
            total
            """;
        HpirProgram program = Compile("deferred-window", source, level);
        Require(program.Imports.Any(import => import.Service == HpirService.RangeTake
            && HpirRangeImports.DecodeWindow(import.Binding).Count != HpirFutureInput.Ready),
            "window count lost its physical Future<Number> carrier");
        var events = RunGated(program, 10);
        Require(events.Any(item => item.Name == "hpir.future.demand" && item.Attributes.ContainsKey("source.file")),
            "range window demand lost its actual source site");
    }

    private static void CheckMixedRangeValidation()
    {
        const string source = PendingLimit + """

            let pending = $std::ranges::iota(0, limit);
            let invalid = $std::ranges::iota(0, 1 / 0);
            $std::io::println("never");
            for n in pending { };
            for n in invalid { };
            """;
        string directory = NewGateDirectory();
        HpirProgram program = Bind(Compile("mixed-range-validation", source), directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        ConcurrentQueue<RuntimeTimelineRecord> observations = new();
        using var output = new StringWriter();
        try
        {
            HpirExecutor.Run(program, new(WorkingDirectory: directory, Output: output)
                { Timeline = new(observations.Enqueue, "hpir-range-mixed") }, timeout.Token);
        }
        catch (MsyncException error) when (error.Message.Contains("requires finite Number arguments", StringComparison.Ordinal))
        {
            Require(output.GetStringBuilder().Length == 0
                && observations.Any(item => item.Name == "hpir.range.capture" && item.Metrics["range.pending_bounds"] == 1)
                && observations.Any(item => item.Name == "hpir.range.capture" && item.Metrics["range.pending_bounds"] == 0
                    && item.Outcome == "error")
                && !observations.Any(item => item.Name == "hpir.range.resolve"),
                "a mixed ready invalid bound must fail at construction without demanding an independent pending plan");
            return;
        }
        finally { Directory.Delete(directory, true); }
        throw new Exception("A mixed ready invalid Number failed to validate at sequence construction.");
    }

    private static string NewGateDirectory() => Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".temp", "hpir",
        "range-gate-" + Guid.NewGuid().ToString("N"))).FullName;

    private static IReadOnlyList<RuntimeTimelineRecord> RunGated(HpirProgram portable, double expected)
    {
        string directory = NewGateDirectory();
        string unrelated = Directory.CreateDirectory(Path.Combine(directory, "caller")).FullName;
        HpirProgram program = Bind(portable, directory);
        string release = Path.Combine(directory, "allow-exit");
        using var output = new MarkerWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        ConcurrentQueue<RuntimeTimelineRecord> observations = new();
        var options = new ExecutionOptions(WorkingDirectory: unrelated, Output: output)
        {
            Timeline = new(observations.Enqueue, "hpir-range")
        };
        Task<ulong> run = Task.Run(() => HpirExecutor.Run(program, options, timeout.Token));
        try
        {
            Task observed = Task.WhenAny(output.Marked.Task, run).WaitAsync(timeout.Token).GetAwaiter().GetResult();
            if (observed == run) _ = run.GetAwaiter().GetResult();
            Require(output.Marked.Task.IsCompletedSuccessfully && !output.Bounded && !run.IsCompleted,
                "range construction did not complete independently of the gated input");
            File.WriteAllText(release, "");
            Require(BitConverter.UInt64BitsToDouble(run.WaitAsync(timeout.Token).GetAwaiter().GetResult()) == expected,
                "typed cursor consumed incorrect completed Number bounds");
            return observations.ToArray();
        }
        finally
        {
            timeout.Cancel();
            if (!File.Exists(release)) File.WriteAllText(release, "");
            if (!run.IsCompleted)
                try { _ = run.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            Directory.Delete(directory, true);
        }
    }

    private sealed class MarkerWriter : StringWriter
    {
        internal TaskCompletionSource Marked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int bounded;
        internal bool Bounded => Volatile.Read(ref bounded) != 0;

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
        {
            if (buffer.Span.SequenceEqual("constructed")) Marked.SetResult();
            else if (buffer.Span.SequenceEqual("bounded")) Volatile.Write(ref bounded, 1);
            else throw new InvalidOperationException("Unexpected guest output.");
            return Task.CompletedTask;
        }
    }

    private static string Sum(string expression) => $$"""
        let total = 0;
        for n in {{expression}} { total = total + n; };
        $std::io::println(total as String);
        """;

    private static void Run(string name, string source, string expected, int yields, int exhausted, int level = 2)
    {
        var prepared = Prepare(name, source, level);
        CheckRun(name, prepared, expected, yields, exhausted, null);
        if (!HpirMachineImage.IsSupported) return;
        using var machine = HpirMachineImage.Compile(prepared, observe: true);
        CheckRun(name, prepared, expected, yields, exhausted, machine);
    }

    private static void CheckRun(string name, HpirProgram prepared, string expected, int yields, int exhausted, HpirMachineImage? machine)
    {
        var profile = new RuntimeProfile(instructionSites: true);
        using var output = new StringWriter();
        var options = new ExecutionOptions(Output: output) { Profile = profile };
        if (machine is null) HpirExecutor.Run(prepared, options);
        else HpirExecutor.RunMachineCandidate(machine, options);
        Require(output.ToString().TrimEnd('\r', '\n') == expected, name + ": " + output);
        (long observedYields, long observedExhaustions) = ObservePullOutcomes(prepared, profile.Drain(), name + ".msync");
        Require(observedYields == yields && observedExhaustions == exhausted,
            $"{name}: expected cursor outcomes {yields}/{exhausted}, observed {observedYields}/{observedExhaustions}");
    }

    private static void Fails(string name, string source, string reason)
    {
        var prepared = Prepare(name, source);
        try { HpirExecutor.Run(prepared); }
        catch (MsyncException error) when (error.Message.Contains(reason, StringComparison.Ordinal)
            && error.Span?.Source == name + ".msync") { return; }
        throw new Exception($"{name}: expected source-attributed failure: {reason}");
    }

    private static HpirProgram Prepare(string name, string source, int level = 2)
        => Bind(Compile(name, source, level), Environment.CurrentDirectory);

    private static HpirProgram Bind(HpirProgram portable, string directory)
        => RoundTrip(HpirBinding.Prepare(portable, new(directory, HostContext.HomeDirectory)));

    private static HpirProgram Compile(string name, string source, int level = 2)
    {
        List<CompilationObservation> events = [];
        var trace = new CompilationTrace(events.Add, includeOptimizerPasses: true);
        var compiled = Compiler.LowerHpir(Compiler.Analyze(source, name + ".msync", trace), level, trace);
        Require(events.Any(item => item.Metrics?.GetValueOrDefault("range.resource_free_closes") > 0),
            name + ": no statically resource-free cursor close");
        Require(compiled.Imports.Any(import => import.Service == HpirService.RangeOpen)
            && compiled.Functions.Any(function => function.Instructions.Any(instruction => instruction.Op == HpirOp.LtU64))
            && compiled.Imports.All(import => import.Service.ToString() != "RangeNext"), name + ": missing physical cursor advance");
        return RoundTrip(compiled);
    }

    private static HpirProgram RoundTrip(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static (long Yields, long Exhaustions) ObservePullOutcomes(HpirProgram program,
        IReadOnlyList<RuntimeProfileSample> samples, string source)
    {
        // The successful pull sets true and joins past the same result's
        // false exhaustion. Inspect those concrete PCs, not generic branch counts.
        Dictionary<(int Function, int Pc), bool> outcomes = [];
        for (int function = 0; function < program.Functions.Count; function++)
        {
            var code = program.Functions[function].Instructions;
            for (int pc = 0; pc + 1 < code.Length; pc++)
            {
                if (code[pc] is not { Op: HpirOp.ConstU8, Immediate: 1 } success
                    || code[pc + 1] is not { Op: HpirOp.Jump, A: var join } || join <= pc + 2) continue;
                if (code[join - 1] is not { Op: HpirOp.ConstU8, Immediate: 0 } exhausted || exhausted.D != success.D) continue;
                outcomes.Add((function, pc), true);
                outcomes.Add((function, join - 1), false);
            }
        }

        long yields = 0, exhaustions = 0;
        foreach (var sample in samples)
        {
            if (sample.Name != "vm.instruction") continue;
            var function = int.Parse(sample.Attributes["function.id"], CultureInfo.InvariantCulture);
            var pc = int.Parse(sample.Attributes["instruction"], CultureInfo.InvariantCulture);
            if (!outcomes.TryGetValue((function, pc), out bool yielded)) continue;
            Require(sample.Attributes.GetValueOrDefault("source.file") == source, "cursor outcome lacks source attribution");
            long hits = (long)sample.Metrics["hits"];
            if (yielded) yields += hits;
            else exhaustions += hits;
        }
        return (yields, exhaustions);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
