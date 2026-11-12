using System.Collections.Concurrent;
using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class NumericRangePayloadChecks
{
    internal static async Task Run(string root, Action<bool, string> require)
    {
        RuntimeContext context = new(new(WorkingDirectory: root));
        using CancellationTokenSource producer = new();
        RangeValue source = await Sequence(-0.0, 1, .25, producer.Token);
        RangeValue snapshot = (RangeValue)await Invoke("materialize", [source]);
        RangeValue rematerialized = (RangeValue)await Invoke("materialize", [snapshot]);
        require(snapshot.RetainedValues is null && rematerialized.RetainedValues is null,
            "a primitive snapshot cannot retain a Value reference lane");
        require(!snapshot.Equals(rematerialized) && snapshot.OwnedSnapshot?.Count == 4,
            "rematerialization creates its own Range descriptor");
        double[] expected = [-0.0, .25, .5, .75];
        int index = 0;
        await foreach (Value item in rematerialized.Items)
        {
            require(index < expected.Length && item is NumberValue number
                && BitConverter.DoubleToInt64Bits(number.Data) == BitConverter.DoubleToInt64Bits(expected[index]),
                "the compatibility projection preserves the exact numeric payload");
            index++;
        }
        require(index == expected.Length, "the primitive snapshot changed element count");

        await using IAsyncEnumerator<Value> enumerator = source.Items.GetAsyncEnumerator();
        require(await enumerator.MoveNextAsync(), "the lazy sequence did not produce its first item");
        producer.Cancel();
        try
        {
            _ = await enumerator.MoveNextAsync();
            throw new InvalidOperationException("the native cursor lost the producer's cancellation");
        }
        catch (OperationCanceledException)
        {
            require(((NumberValue)rematerialized.OwnedSnapshot![3]).Data == .75,
                "an eager snapshot must not depend on its producer's cancellation");
        }
        await SuspendedDemand(root, require);

        async ValueTask<RangeValue> Sequence(double start, double stop, double step, CancellationToken token) =>
            (RangeValue)await Invoke("sequence", [new NumberValue(start), new NumberValue(stop), new NumberValue(step)], token);

        ValueTask<Value> Invoke(string operation, Value[] arguments, CancellationToken token = default) =>
            NativeRegistry.InvokeAsync("ranges." + operation, arguments, context,
                (_, _, _) => throw new InvalidOperationException("Unexpected numeric Range callback"), token);
    }

    private static async Task SuspendedDemand(string root, Action<bool, string> require)
    {
        const string producer = """
            let io = $std::io;
            let ranges = $std::ranges;
            fn items() {
                io::println("produce");
                let text? = io::read_line();
                if text is null { EMPTY_EXPRESSION }
                else { RANGE_EXPRESSION }
            };
            let stream = items();
            io::println("before");
            let sum = 0;
            for value in stream { sum = sum + value; };
            io::println(sum as String);
            """;
        foreach (var (empty, expression, sum) in new[]
        {
            ("ranges::sequence(0, 0, 1)", "ranges::sequence(0, text as Number, 1)", 3),
            ("ranges::repeat(0, 0)", "ranges::repeat(text as Number, 2)", 6),
        })
        {
            string source = producer.Replace("EMPTY_EXPRESSION", empty, StringComparison.Ordinal)
                .Replace("RANGE_EXPRESSION", expression, StringComparison.Ordinal);
            Module portable = RoundTrip(Msync.Compiler.Compiler.Compile(source, "numeric-deferred", 2));
            foreach (Module image in new[] { portable, RoundTrip(Binding.Bind(portable, new(root, root))) })
            {
                using var input = new GatedInput();
                using var output = new StringWriter();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                ConcurrentQueue<RuntimeTimelineRecord> observations = new();
                RuntimeTimeline timeline = new(observations.Enqueue, "entry");
                var options = new ExecutionOptions(WorkingDirectory: root) { Input = input, Output = output, Timeline = timeline };
                var execution = Interpreter.ExecuteAsync(image, options, deadline.Token).AsTask();
                await input.Requested.Task.WaitAsync(deadline.Token);
                require(!execution.IsCompleted && output.ToString() == $"before{output.NewLine}produce{output.NewLine}",
                    "a deferred producer must suspend only after the iterator first demands it");
                input.Line.SetResult("3");
                await execution.WaitAsync(deadline.Token);
                require(input.Reads == 1 && output.ToString() == $"before{output.NewLine}produce{output.NewLine}{sum}{output.NewLine}",
                    "resuming a deferred cursor must not replay its producer");
                require(observations.Count(item => item.Name == "vm.await"
                    && item.Attributes.GetValueOrDefault("wait.reason") == "iterator_source") == 1,
                    "the first IteratorNext must own the single suspended producer admission");
            }
        }
    }

    private static Module RoundTrip(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream);
    }

    private sealed class GatedInput : TextReader
    {
        internal readonly TaskCompletionSource Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<string?> Line = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads { get; private set; }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Reads++;
            Requested.TrySetResult();
            return new(Line.Task.WaitAsync(cancellationToken));
        }
    }
}
