using System.Collections.Concurrent;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class RuntimeProfileChecks
{
    public static async Task RunAsync()
    {
        Module relocatable = new([new NumberValue(0), new NumberValue(1), new NumberValue(3)],
            [new("main", 0, 1,
                [new(OpCode.Constant, 0), new(OpCode.InitializeLocal, 0), new(OpCode.LoadLocal, 0), new(OpCode.Constant, 2),
                 new(OpCode.Less), new(OpCode.JumpIfFalse, 11, Span: new("profile-fixture.msync", 13, 5)),
                 new(OpCode.MakeClosure, 1), new(OpCode.LoadLocal, 0), new(OpCode.Call, 1, 2), new(OpCode.StoreLocal, 0),
                 new(OpCode.Jump, 2), new(OpCode.LoadLocal, 0), new(OpCode.Constant, 2),
                 new(OpCode.NativeCall, 2, Text: "math.pow"), new(OpCode.Return)], TypeRef.Number),
             new("increment", 1, 1,
                [new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.Add), new(OpCode.Return)], TypeRef.Number,
                ParameterTypes: [TypeRef.Number])], OptimizationLevel: 0);
        string root = Environment.CurrentDirectory;
        Module executable = Binding.Bind(relocatable, new(root, root));
        RuntimeProfile profile = new(instructionSites: true);
        foreach (Module input in new[] { relocatable, executable })
        {
            using MemoryStream artifact = new();
            Artifact.Write(input, artifact);
            artifact.Position = 0;
            Module decoded = Artifact.Read(artifact, formBlocks: false);
            NumberValue result = (NumberValue)await Interpreter.ExecuteAsync(decoded, new(WorkingDirectory: root) { Profile = profile });
            Require(result.Data == 27, "Profile changed the executed value");
            IReadOnlyList<RuntimeProfileSample> samples = profile.Drain();
            Require(profile.Drain().Count == 0, "Drain repeated counters");
            var main = samples.Single(sample => Match(sample, "vm.function", "function.name", "main"));
            var increment = samples.Single(sample => Match(sample, "vm.function", "function.name", "increment"));
            Require(main.Metrics["calls"] == 1 && main.Metrics["instructions"] == 37 && main.Metrics["number_boxes"] == 1,
                "Incorrect entry function metrics");
            Require(increment.Metrics["calls"] == 3 && increment.Metrics["instructions"] == 12 && increment.Metrics["number_boxes"] == 3,
                "Incorrect callee metrics");
            Require(increment.Metrics["borrowed_frames"] == 3 && increment.Metrics["detached_frames"] == 0
                && main.Metrics["borrowed_frames"] == 0, "Read-only source calls did not use borrowed frames");
            Require(samples.Single(sample => Match(sample, "vm.native", "native.name", "math.pow"))
                .Metrics["fast_calls"] == 1, "Direct numeric math fast path was not counted");
            Require(samples.Single(sample => Match(sample, "vm.opcode", "opcode", "Less"))
                .Metrics["hits"] == 4, "Incorrect opcode aggregate");
            var branch = samples.Single(sample => Match(sample, "vm.instruction", "instruction.pc", "5")
                && sample.Attributes["function.name"] == "main");
            Require(branch.Attributes["source.offset"] == "13" && branch.Metrics["hits"] == 4,
                "Branch site lost source or count");
            Require(samples.Single(sample => Match(sample, "vm.edge", "target.pc", "11")).Metrics["hits"] == 1,
                "Incorrect conditional taken edge count");
            Require(samples.Single(sample => Match(sample, "vm.edge", "target.pc", "6")).Metrics["hits"] == 3,
                "Incorrect conditional fallthrough edge count");
            Require(samples.Single(sample => Match(sample, "vm.edge", "target.pc", "2")).Metrics["hits"] == 3,
                "Incorrect unconditional backedge count");
            var call = samples.Single(sample => sample.Name == "vm.call");
            Require(call.Attributes["target.function.id"] == "1" && call.Attributes["target.function.name"] == "increment"
                && call.Attributes["target.image.id"] == main.Attributes["image.id"] && call.Metrics["hits"] == 3,
                "Resolved target did not retain caller/callee identity");
        }
        RuntimeProfile aggregate = new();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var result = await Interpreter.ExecuteAsync(executable, new() { Profile = aggregate });
            Require(result is NumberValue { Data: 27 }, "Concurrent profiled execution changed the result");
        })));
        var totals = aggregate.Drain();
        Require(totals.All(sample => sample.Name is "vm.function" or "vm.opcode" or "vm.native"),
            "Aggregate profile emitted site records");
        Require(totals.Where(sample => sample.Name == "vm.function").Sum(sample => (long)sample.Metrics["instructions"]) == 8 * 49,
            "Aggregate profile lost concurrent visits");
        string image = totals.Single(sample => Match(sample, "vm.function", "function.name", "main")).Attributes["image.id"];
        await Interpreter.ExecuteAsync(executable, new() { Profile = aggregate });
        var repeated = aggregate.Drain().Single(sample => Match(sample, "vm.function", "function.name", "main"));
        Require(repeated.Attributes["image.id"] == image && repeated.Metrics["calls"] == 1,
            "Live image identity or per-drain counts changed across executions");
        await CheckTimelineAsync();
        await BorrowedFrameChecks.RunAsync();
        Console.WriteLine("Runtime profiling checks passed.");
    }

    private static async Task CheckTimelineAsync()
    {
        TypeRef resultType = TypeRef.String.WithNull;
        Module module = new([], [
            new("main", 0, 0, [new(OpCode.MakeClosure, 1), new(OpCode.Call, 0, 2), new(OpCode.Return)], resultType),
            new("read", 0, 0, [new(OpCode.NativeCall, 0, Text: "io.read_line",
                Span: new("profile-timeline.msync", 21, 18)), new(OpCode.Return)], resultType)
        ], OptimizationLevel: 0);
        string cwd = Environment.CurrentDirectory;
        Module executable = Binding.Bind(module, new(cwd, cwd));
        ConcurrentQueue<RuntimeTimelineRecord> events = new();
        RuntimeTimeline timeline = new(events.Enqueue, "root");
        ExecutionOptions options = new() { Timeline = timeline, Profile = new(instructionSites: true) };
        await CheckNativeTimelineAsync(options, events);
        using (StringReader reader = new("ready\n"))
        {
            Require(await Interpreter.ExecuteAsync(executable, options with { Input = reader }) is StringValue { Data: "ready" },
                "Timeline changed a synchronous guest result");
            var vm = VmEvents(events);
            Require(vm.Length == 3 && vm.Count(record => record.Name == "vm.frame"
                && record.Outcome == "ok" && record.Attributes["frame.exit"] == "return"
                && record.Metrics["suspensions"] == 0) == 2
                && vm.Any(record => record.Name == "vm.native" && record.Attributes["native.name"] == "io.read_line"),
                "Ready native work lost its operation span or was reported as a suspension");
            events.Clear();
        }
        using (GatedReader reader = new())
        {
            var pending = Interpreter.ExecuteAsync(executable, options with { Input = reader });
            Require(!pending.IsCompleted, "Input gate failed to suspend the guest");
            reader.Release("gated");
            Require(await pending is StringValue { Data: "gated" }, "Timeline changed a resumed guest result");
            CheckObservedWait(events, "ok");
            events.Clear();
        }
        using (GatedReader reader = new())
        using (CancellationTokenSource cancel = new())
        {
            var pending = Interpreter.ExecuteAsync(executable, options with { Input = reader }, cancel.Token);
            Require(!pending.IsCompleted, "Input gate failed to suspend before cancellation");
            cancel.Cancel();
            try { await pending; throw new InvalidOperationException("Cancellation did not reach the caller"); }
            catch (OperationCanceledException) { }
            CheckObservedWait(events, "cancelled");
            events.Clear();
        }
        using (GatedReader reader = new())
        {
            var pending = Interpreter.ExecuteAsync(executable, options with { Input = reader, Profile = null });
            Require(!pending.IsCompleted, "Input gate failed to suspend before failure");
            reader.Fail();
            try { await pending; throw new InvalidOperationException("Input failure did not reach the caller"); }
            catch (MsyncException error) when (error.InnerException is IOException) { }
            CheckObservedWait(events, "error");
        }
    }

    private static async Task CheckNativeTimelineAsync(ExecutionOptions options, ConcurrentQueue<RuntimeTimelineRecord> events)
    {
        Module inline = new([new NumberValue(3), new NumberValue(2)], [
            new("main", 0, 0, [new(OpCode.Constant, 0), new(OpCode.Constant, 1),
                new(OpCode.NativeCall, 2, Text: "math.pow"), new(OpCode.Return)], TypeRef.Number)
        ], OptimizationLevel: 0);
        Require(await Interpreter.ExecuteAsync(inline, options) is NumberValue { Data: 9 }, "Inline native result changed");
        Require(VmEvents(events).Length == 2 && events.Single(record => record.Name == "vm.native").Attributes["native.route"] == "inline",
            "Synchronous inline native operation lost its interval");
        events.Clear();

        Module indirect = inline with
        {
            Constants = [new FunctionValue(-1, NativeName: "math.pow"), new NumberValue(3), new NumberValue(2)],
            Functions = [inline.Functions[0] with
            {
                Instructions = [new(OpCode.Constant, 0), new(OpCode.Constant, 1), new(OpCode.Constant, 2),
                    new(OpCode.Call, 2), new(OpCode.Return)]
            }]
        };
        Require(await Interpreter.ExecuteAsync(indirect, options) is NumberValue { Data: 9 }, "Dynamic native result changed");
        Require(VmEvents(events).Length == 2 && events.Single(record => record.Name == "vm.native").Attributes["native.route"] == "dynamic",
            "A native callable value lost its operation interval");
        events.Clear();

        TaskCompletionSource<ProcessValue> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Module deferred = inline with
        {
            Constants = [new ProcessValue(0, "", "") { Completion = exit.Task }, new NumberValue(2)],
            Functions = [inline.Functions[0] with
            {
                Instructions = [new(OpCode.Constant, 0), new(OpCode.NativeCall, 1, Text: "process.exit_code"), new(OpCode.Constant, 1),
                    new(OpCode.NativeCall, 2, Text: "math.pow"), new(OpCode.Return)]
            }]
        };
        var pending = Interpreter.ExecuteAsync(deferred, options);
        Require(!pending.IsCompleted, "Deferred native input was not gated");
        exit.SetResult(new ProcessValue(3, "", ""));
        Require(await pending is NumberValue { Data: 9 }, "Deferred native result changed");
        var calls = events.Where(record => record.Name == "vm.native" && record.Attributes["native.name"] == "math.pow").ToArray();
        var admission = calls.Single(record => record.Attributes["native.route"] == "direct");
        var completion = calls.Single(record => record.Attributes["native.route"] == "deferred_completion");
        Require(admission.Attributes["native.result"] == "deferred" && completion.ParentSpanId == admission.SpanId
            && completion.Outcome == "ok", "Deferred native completion was lost or conflated with admission");
        Require(events.Single(record => record.Name == "vm.await").Attributes["wait.reason"] == "final_result",
            "Deferred native result lost its outer completion wait");
        events.Clear();
    }

    private static void CheckObservedWait(ConcurrentQueue<RuntimeTimelineRecord> events, string outcome)
    {
        var vm = VmEvents(events);
        var frames = vm.Where(record => record.Name == "vm.frame").ToArray();
        var caller = frames.Single(record => record.Attributes["function.name"] == "main");
        var callee = frames.Single(record => record.Attributes["function.name"] == "read");
        Require(caller.ParentSpanId == "root" && callee.ParentSpanId == caller.SpanId, "Frame nesting lost the guest caller");
        var waits = vm.Where(record => record.Name == "vm.await").ToArray();
        var callerWait = waits.Single(record => record.Attributes["wait.reason"] == "source_call");
        var nativeWait = waits.Single(record => record.Attributes["wait.reason"] == "native_call");
        Require(callerWait.ParentSpanId == caller.SpanId && callerWait.Metrics["instruction.pc"] == 1
            && nativeWait.ParentSpanId == callee.SpanId && nativeWait.Metrics["instruction.pc"] == 0
            && nativeWait.Attributes["native.name"] == "io.read_line" && nativeWait.Metrics["source.offset"] == 21,
            "Waits lost frame, source, or instruction identity");
        var native = vm.Single(record => record.Name == "vm.native");
        Require(native.ParentSpanId == callee.SpanId && native.Attributes["native.name"] == "io.read_line"
            && native.Metrics["source.offset"] == 21 && native.Attributes["duration.kind"] == "inclusive",
            "Native operation timing lost its frame, source, or duration attribution");
        Require(vm.Length == 5 && vm.All(record => record.DurationNanoseconds > 0 && record.Outcome == outcome)
            && frames.All(record => record.Metrics["suspensions"] == 1), "Wait or frame outcomes/counts are incorrect");
    }

    private static RuntimeTimelineRecord[] VmEvents(ConcurrentQueue<RuntimeTimelineRecord> events) =>
        events.Where(record => record.Name.StartsWith("vm.", StringComparison.Ordinal)).ToArray();

    private sealed class GatedReader : TextReader
    {
        private readonly TaskCompletionSource<string?> line = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => new(line.Task.WaitAsync(cancellationToken));
        public void Release(string text) => line.SetResult(text);
        public void Fail() => line.SetException(new IOException("gated input failure"));
    }

    private static bool Match(RuntimeProfileSample sample, string name, string key, string value) =>
        sample.Name == name && sample.Attributes.TryGetValue(key, out string? actual) && actual == value;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
