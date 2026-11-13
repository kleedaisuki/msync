using System.Collections.Concurrent;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using static RuntimeFixture;

internal static class RuntimeProfileChecks
{
    internal static async Task RunAsync()
    {
        NativeProgram original = Loop();
        RuntimeProfile profile = new(instructionSites: true);
        foreach (NativeProgram input in new[] { Reload(original), Reload(Bind(original, Root)) })
        {
            Check((await Run(input, new() { Profile = profile })).AsNumber() == 27, "Profile changed executed value");
            IReadOnlyList<RuntimeProfileSample> samples = profile.Drain();
            Check(profile.Drain().Count == 0, "Drain repeated counters");
            RuntimeProfileSample main = samples.Single(sample => Match(sample, "vm.function", "function.name", "main"));
            RuntimeProfileSample increment = samples.Single(sample => Match(sample, "vm.function", "function.name", "increment"));
            Check(main.Metrics["calls"] == 1 && main.Metrics["instructions"] == 22, "Incorrect native entry metrics");
            Check(increment.Metrics["calls"] == 3 && increment.Metrics["instructions"] == 9, "Incorrect native callee metrics");
            Check(samples.Single(sample => sample.Name == "vm.native").Attributes["native.name"] == "math.pow"
                && samples.Single(sample => sample.Name == "vm.native").Metrics["calls"] == 1, "Native boundary not counted");
            Check(samples.Single(sample => Match(sample, "vm.opcode", "opcode", "Branch")).Metrics["hits"] == 4, "Branch visits changed");
            RuntimeProfileSample site = samples.Single(sample => Match(sample, "vm.instruction", "instruction.pc", "4") && sample.Attributes["function.name"] == "main");
            Check(site.Attributes["source.offset"] == "13" && site.Metrics["hits"] == 4, "Branch source or site count lost");
            foreach (var (from, to, hits) in new[] { (4, 5, 3UL), (4, 8, 1UL), (7, 3, 3UL) })
                Check(samples.Single(sample => Match(sample, "vm.edge", "instruction.pc", from.ToString())
                    && sample.Attributes["target.pc"] == to.ToString()).Metrics["hits"] == hits, "CFG edge count wrong");
            RuntimeProfileSample call = samples.Single(sample => sample.Name == "vm.call");
            Check(call.Attributes["target.function.id"] == "1" && call.Attributes["target.function.name"] == "increment"
                && call.Attributes["target.image.id"] == main.Attributes["image.id"] && call.Metrics["hits"] == 3, "Call identity lost");
        }
        RuntimeProfile aggregate = new();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => Check((await Run(original, new() { Profile = aggregate })).AsNumber() == 27, "Concurrent result changed")));
        IReadOnlyList<RuntimeProfileSample> totals = aggregate.Drain();
        Check(totals.All(sample => sample.Name is "vm.function" or "vm.opcode" or "vm.native"), "Aggregate emitted site records");
        Check(totals.Where(sample => sample.Name == "vm.function").Sum(sample => (long)sample.Metrics["instructions"]) == 8 * 31, "Concurrent visits lost");
        string identity = totals.Single(sample => Match(sample, "vm.function", "function.name", "main")).Attributes["image.id"];
        await Run(original, new() { Profile = aggregate });
        Check(aggregate.Drain().Single(sample => Match(sample, "vm.function", "function.name", "main")).Attributes["image.id"] == identity, "Live image identity changed across drains");
        await Suspension();
        await SchedulerTimeline();
        await BorrowedFrameChecks.RunAsync();
        Console.WriteLine("PASS native profile counters, CFG edges, suspension and scheduler observations");
    }
    private static NativeProgram Loop() => new([new(NativeKind.Number, Number: 0), new(NativeKind.Number, Number: 1), new(NativeKind.Number, Number: 3)],
        [new("main", [new(0), new(0), new(1), new(2), new(0), new(0)],
            [new(NativeOp.Constant, 0, 0), new(NativeOp.Constant, 1, 2), new(NativeOp.MakeFunction, 3, 1, C: 0),
             new(NativeOp.Binary, 2, 0, 1, Immediate: (long)NativeBinary.Less), new(NativeOp.Branch, A: 2, B: 5, C: 8, Source: new("profile-fixture.msync", 13, 5)),
             new(NativeOp.Call, 4, 3, 1, Inputs: [0]), new(NativeOp.Copy, 0, 4), new(NativeOp.Jump, A: 3),
             new(NativeOp.CallNative, 5, Symbol: "math.pow", Inputs: [0, 1]), new(NativeOp.Return, A: 5)], new([], 0, [])),
         new("increment", [new(0, NativeSlotRole.Parameter), new(0), new(0)],
            [new(NativeOp.Constant, 1, 1), new(NativeOp.Binary, 2, 0, 1, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 2)], new([0], 0, []), ParameterCount: 1)],
        [new(TypeKind.Number), new(TypeKind.Bool), new(TypeKind.Function)], []);
    private static async Task Suspension()
    {
        NativeProgram read = new([], [new("read", [new(0)],
            [new(NativeOp.CallNative, 0, Symbol: "io.read_line", Source: new("profile-timeline.msync", 21, 18)), new(NativeOp.Return, A: 0)], new([], 0, []))],
            [new(TypeKind.String | TypeKind.Null)], []);
        RuntimeProfile readyProfile = new();
        Check((await Run(read, new() { Input = new StringReader("ready\n"), Profile = readyProfile })).AsString() == "ready", "Ready native result changed");
        Check(readyProfile.Drain().Single(sample => sample.Name == "vm.function").Metrics["dispatch_pauses"] == 0, "Ready work reported suspension");
        using GatedReader reader = new(); RuntimeProfile profile = new();
        Task<NativeValue> pending = Run(read, new() { Input = reader, Profile = profile });
        Check(!pending.IsCompleted, "Input failed to suspend"); reader.Release("gated");
        Check((await pending).AsString() == "gated", "Resume changed guest result");
        RuntimeProfileSample frame = profile.Drain().Single(sample => sample.Name == "vm.function");
        Check(frame.Metrics["dispatch_pauses"] == 1 && frame.Metrics["dispatch_continuations"] == 1, "Native suspension ownership not paired");
        using GatedReader cancelled = new(); using CancellationTokenSource cancel = new();
        Task<NativeValue> interrupted = Run(read, new() { Input = cancelled }, cancel.Token); Check(!interrupted.IsCompleted, "Cancellation probe did not gate"); cancel.Cancel();
        try { await interrupted; throw new InvalidOperationException("Cancellation ignored"); } catch (OperationCanceledException) { Check(true, "cancelled"); }
        using GatedReader failed = new(); Task<NativeValue> fault = Run(read, new() { Input = failed }); failed.Fail();
        try { await fault; throw new InvalidOperationException("Input failure hidden"); }
        catch (MsyncException error) { Check(error.Span == new SourceSpan("profile-timeline.msync", 21, 18) && error.ToString().Contains("gated input failure", StringComparison.Ordinal), "Failure lost source/native cause"); }
    }
    private static async Task SchedulerTimeline()
    {
        string root = Path.Combine(Root, "timeline-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            ConcurrentQueue<RuntimeTimelineRecord> records = new(); ConcurrentQueue<RuntimeObservation> observed = new();
            await Run(Compile("\"observed\" -> ./a; ./a -> ./b"), new(WorkingDirectory: root, Observe: observed.Enqueue)
                { Timeline = new(records.Enqueue, "root") });
            Check(await File.ReadAllTextAsync(Path.Combine(root, "b")) == "observed", "Observation changed effects");
            Check(records.Any(record => record.Name == "scheduler.admission" && record.ParentSpanId == "root"), "Scheduler admission interval missing");
            Check(observed.Count(record => record.Phase == "transfer" && record.Outcome == "success") == 2, "Selected transfer observations missing");
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class GatedReader : TextReader
    {
        private readonly TaskCompletionSource<string?> line = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => new(line.Task.WaitAsync(cancellationToken));
        internal void Release(string value) => line.SetResult(value);
        internal void Fail() => line.SetException(new IOException("gated input failure"));
    }
    private static bool Match(RuntimeProfileSample sample, string name, string key, string value) => sample.Name == name && sample.Attributes.GetValueOrDefault(key) == value;
}
