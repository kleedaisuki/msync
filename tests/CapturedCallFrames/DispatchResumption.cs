using System.Collections.Concurrent;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class DispatchResumption
{
    internal static void Check(string root)
    {
        Module portable = RoundTrip(new(
            [new NumberValue(100), new NumberValue(3), new NumberValue(4), new TypeValue(TypeRef.Number)],
            [
                new FunctionCode("repeat-main", 0, 0, [
                    new(OpCode.Constant, 0), new(OpCode.Constant, 0), new(OpCode.Add),
                    new(OpCode.MakeClosure, 1), new(OpCode.Constant, 1), new(OpCode.Constant, 2),
                    new(OpCode.Add), new(OpCode.Call, 1, 2), new(OpCode.Add), new(OpCode.Return),
                ], TypeRef.Number) { CodeSignature = new([], 0, []) },
                new FunctionCode("repeat-read", 1, 1, [
                    new(OpCode.LoadLocal, 0),
                    new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.Constant, 3),
                    new(OpCode.NativeCall, 2, Text: "cast"), new(OpCode.Add),
                    new(OpCode.NativeCall, Text: "io.read_line"), new(OpCode.Constant, 3),
                    new(OpCode.NativeCall, 2, Text: "cast"), new(OpCode.Add), new(OpCode.Return),
                ], TypeRef.Number, ParameterTypes: [TypeRef.Number]) { CodeSignature = new([0], 0, []) },
            ], OptimizationLevel: 0) { CodeTypes = [new(TypeKind.Number)] });
        foreach (Module image in new[] { portable, RoundTrip(Binding.Bind(portable, new(root, root))) })
        {
            using GateInput input = new();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ConcurrentQueue<RuntimeTimelineRecord> spans = new();
            RuntimeTimeline timeline = new(spans.Enqueue, "entry");
            RuntimeProfile profile = new(instructionSites: true);
            var options = new ExecutionOptions(WorkingDirectory: root) { Input = input, Profile = profile, Timeline = timeline };
            using var pump = new GatePump();
            var previous = SynchronizationContext.Current;
            Value result;
            try
            {
                SynchronizationContext.SetSynchronizationContext(pump);
                Task<Value> execution = Interpreter.ExecuteAsync(image, options, deadline.Token).AsTask();
                pump.Until(input.Requested(0), deadline.Token);
                if (execution.IsCompleted) throw new InvalidOperationException("The first selected read did not suspend.");
                input.Release(0, "11");
                pump.Until(input.Requested(1), deadline.Token);
                if (execution.IsCompleted) throw new InvalidOperationException("The second selected read did not suspend.");
                input.Release(1, "13");
                pump.Until(execution, deadline.Token);
                result = execution.GetAwaiter().GetResult();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            if (result is not NumberValue { Data: 231 } || input.Reads != 2)
                throw new InvalidOperationException("Resumption replayed a selected read or lost the suspended operands.");
            ulong[] pcs = spans.Where(span => span.Name == "vm.await"
                && span.Attributes.GetValueOrDefault("native.name") == "io.read_line")
                .Select(span => span.Metrics["instruction.pc"]).ToArray();
            if (pcs is not [1, 5])
                throw new InvalidOperationException("Selected waits did not retain the two original PCs.");
            var frame = profile.Drain().Single(sample => sample.Name == "vm.function"
                && sample.Attributes["function.name"] == "repeat-read");
            if (frame.Metrics["borrowed_frames"] != 1 || frame.Metrics["raw_arguments"] != 1)
                throw new InvalidOperationException("The caller's raw argument slice did not survive both suspensions.");
        }
    }

    // Each release runs only after the VM callback has returned to this pump.
    private sealed class GatePump : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> ready = new();
        public override void Post(SendOrPostCallback callback, object? state) => ready.Add((callback, state));
        internal void Until(Task target, CancellationToken cancellationToken)
        {
            while (!target.IsCompleted)
            {
                var (callback, state) = ready.Take(cancellationToken);
                callback(state);
            }
        }
        public void Dispose() => ready.Dispose();
    }

    private static Module RoundTrip(Module image)
    {
        using MemoryStream stream = new();
        Artifact.Write(image, stream);
        stream.Position = 0;
        return Artifact.Read(stream, formBlocks: false);
    }

    private sealed class GateInput : TextReader
    {
        private readonly TaskCompletionSource[] requested =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        private readonly TaskCompletionSource<string?>[] lines =
            [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        internal int Reads { get; private set; }
        internal Task Requested(int index) => requested[index].Task;
        internal void Release(int index, string line) => lines[index].SetResult(line);
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            int index = Reads++;
            requested[index].SetResult();
            return new(lines[index].Task.WaitAsync(cancellationToken));
        }
    }
}
