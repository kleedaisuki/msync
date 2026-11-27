using System.Collections.Concurrent;
using System.Globalization;
using Msync.Compiler;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;
using Msync.Runtime.Native;

internal static class HpirPromotionChecks
{
    private const string SourceName = "hpir-live-promotion.msync";
    private const string Source = """
        object Ledger { tally: 3 };
        fn step(n: Number) -> Number {
            Ledger::tally = Ledger::tally + 1;
            n + 2
        };
        fn work(limit: Number) -> Bool {
            let pending? = $std::io::read_line();
            $std::io::println("before");
            let i = 0;
            loop {
                if i >= limit { break i; };
                i = step(i);
            };
            $std::io::println(Ledger::tally as String);
            $std::io::println("after");
            pending is null
        };
        fn audit(limit: Number) -> Number {
            let i = 0;
            loop {
                if i >= limit { break i; };
                i = step(i);
            }
        };
        $std::io::println("root");
        let count? = $std::io::read_line();
        let limit = if count is null { 0 } else { count as Number };
        if work(limit) { $std::io::println("eof"); };
        $std::io::println(audit(limit) as String);
        """;

    internal static void Run()
    {
        var produced = Compiler.LowerHpir(Compiler.Analyze(Source, SourceName), 2);
        string cwd = Environment.CurrentDirectory;
        HpirProgram program = Reload(HpirBinding.Prepare(Reload(produced), new(cwd, cwd, SourceName)));

        Check(program, 2, adaptive: true, hot: false);
        var control = Check(program, 40_000, adaptive: false, hot: false);
        bool enabled = HpirMachineImage.IsSupported;
        var promoted = Check(program, 40_000, adaptive: true, hot: enabled);
        Require(Census(control).SequenceEqual(Census(promoted)),
            "the physical prefix and machine suffix must retain each instruction, edge, call, and import exactly once");
        if (enabled) Cancellation(program);
        Console.WriteLine("PASS HPIR adaptive continuation, cold bypass, owned read, exact census and cancellation");
    }

    private static IReadOnlyList<RuntimeProfileSample> Check(HpirProgram program, int limit, bool adaptive, bool hot)
    {
        using MarkerWriter output = new() { NewLine = "\n" };
        using Input input = new(limit);
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        ConcurrentQueue<RuntimeTimelineRecord> events = new();
        RuntimeProfile profile = new(instructionSites: true);
        ExecutionOptions options = new(Output: output) { Input = input, Profile = profile, Timeline = new(events.Enqueue, "promotion") };
        Task<HpirRunOutcome> run = Task.Run(() => adaptive ? HpirExecutor.RunAdaptive(program, options, stop.Token)
            : HpirExecutor.RunOutcome(program, options, stop.Token));
        try
        {
            output.After.Task.WaitAsync(stop.Token).GetAwaiter().GetResult();
            Require(!run.IsCompleted, "the child-owned read must stay pending across the loop and source outputs");
            input.Line.SetResult(null);
            Require(run.WaitAsync(stop.Token).GetAwaiter().GetResult().FailedTransfers == 0, "unexpected failed work");
        }
        finally
        {
            stop.Cancel();
            if (!run.IsCompleted)
                try { _ = run.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
        }

        Require(output.ToString() == $"root\nbefore\n{3 + limit / 2}\nafter\neof\n{limit}\n" && input.Reads == 2,
            "promotion must preserve singleton updates and perform each guest-side read/write exactly once");
        var execute = events.Single(item => item.Name == "hpir.execute");
        var samples = profile.Drain();
        if (hot)
        {
            var promotions = events.Where(item => item.Name == "hpir.machine.promote").ToArray();
            var emitted = events.Where(item => item.Name == "hpir.machine.emit.function").ToArray();
            Require(promotions.Length > 1 && promotions.All(promotion =>
                    promotion.ParentSpanId == execute.SpanId && promotion.Attributes["source.file"] == SourceName
                    && promotion.Attributes["decision"] == "promoted"
                    && (promotion.Metrics["guest.instructions_observed"] >= promotion.Metrics["guest.instruction_budget"]
                        || promotion.Metrics["guest.native_waiting_calls"] >= promotion.Metrics["guest.native_waiting_call_budget"])
                    && events.Single(item => item.Name == "hpir.machine.emit"
                        && item.ParentSpanId == promotion.SpanId).Metrics["functions"] == 1)
                && emitted.Length == promotions.Length
                && emitted.Select(item => item.Attributes["function.id"]).Distinct().Count() == emitted.Length
                && emitted.Any(item => item.Metrics["guest.direct_late_sites"] != 0)
                && emitted.Any(item => item.Metrics["guest.direct_cached_sites"] != 0)
                && execute.Attributes["dispatch.engine"] == "physical.interpreter+machine.x64"
                && samples.Where(item => item.Name == "vm.function").Any(item => item.Metrics["machine_calls"] > 1_000),
                "observable, single publication per function with late binding and cached native calls");
        }
        else Require(events.All(item => item.Name is not ("hpir.machine.emit" or "hpir.machine.promote"))
            && execute.Attributes["dispatch.engine"] == "physical.interpreter",
            "a short or unsupported execution must not emit");
        return samples;
    }

    private static void Cancellation(HpirProgram program)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var input = new Input(40_000);
        using var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        var events = new ConcurrentQueue<RuntimeTimelineRecord>();
        var timeline = new RuntimeTimeline(item =>
        {
            events.Enqueue(item);
            if (item.Name == "hpir.machine.promote" && item.Attributes.GetValueOrDefault("decision") == "promoted")
                stop.Cancel();
        }, "promotion-cancel");
        try
        {
            _ = HpirExecutor.RunAdaptive(program, new(Output: output) { Input = input, Timeline = timeline }, stop.Token);
            throw new InvalidOperationException("Cancellation published at the handoff was discarded.");
        }
        catch (OperationCanceledException caught) when (caught.CancellationToken == stop.Token) { }
        var execute = events.Single(item => item.Name == "hpir.execute");
        Require(output.ToString() == "root\nbefore\n"
            && execute.Outcome == "cancelled"
            && execute.Attributes["dispatch.engine"] == "physical.interpreter+machine.x64"
            && execute.Metrics["stack.frames_discarded"] >= 2,
            "native cancellation must retain the in-flight frames and not replay the first effects");
    }

    private static IEnumerable<string> Census(IReadOnlyList<RuntimeProfileSample> samples) => samples
        .Where(item => item.Name is "vm.instruction" or "vm.edge" or "vm.call" or "vm.native")
        .Select(item => item.Name + ":" + string.Join(",", item.Attributes.OrderBy(pair => pair.Key))
            + ":" + item.Metrics.GetValueOrDefault(item.Name == "vm.native" ? "calls" : "hits"))
        .Order(StringComparer.Ordinal);

    private static HpirProgram Reload(HpirProgram program)
    {
        using var stream = new MemoryStream();
        HpirArtifact.WriteProduced(program, stream);
        stream.Position = 0;
        return HpirArtifact.ReadTrusted(stream);
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("HPIR promotion: " + reason);
    }

    private sealed class Input(int count) : TextReader, IAsyncLineInput
    {
        internal TaskCompletionSource<string?> Line { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads { get; private set; }
        public override ValueTask<string?> ReadLineAsync(CancellationToken token) => ++Reads == 1
            ? ValueTask.FromResult<string?>(count.ToString(CultureInfo.InvariantCulture))
            : new(Line.Task.WaitAsync(token));
    }

    private sealed class MarkerWriter : StringWriter
    {
        internal TaskCompletionSource After { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
        {
            WriteLine(buffer.Span);
            if (buffer.Span.SequenceEqual("after")) After.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
