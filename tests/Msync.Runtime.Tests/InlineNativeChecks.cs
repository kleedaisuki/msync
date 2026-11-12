using Msync.Core;
using Msync.Optimization;
using Msync.Runtime;

internal static class InlineNativeChecks
{
    internal static async Task RunAsync()
    {
        var program = new Module([new NumberValue(0), new NumberValue(1), new NumberValue(41.75)], [
            new("inline_native", 0, 1, [
                new(OpCode.Constant, 0), new(OpCode.MakeRange, 1), Native("storage.vector_new", 1), new(OpCode.InitializeLocal, 0),
                new(OpCode.LoadLocal, 0), new(OpCode.Constant, 0), new(OpCode.Constant, 2), Native("math.floor", 1),
                Native("storage.vector_set", 3), new(OpCode.Pop),
                new(OpCode.LoadCell, 0), new(OpCode.Constant, 0), Native("storage.vector_get", 2), new(OpCode.Pop),
                new(OpCode.LoadLocal, 0), new(OpCode.Constant, 1), new(OpCode.Constant, 1), new(OpCode.Subtract),
                Native("storage.vector_get", 2), new(OpCode.MakeRange, 1), Native("ranges.front", 1),
                new(OpCode.MakeRange, 1), new(OpCode.Constant, 0), Native("ranges.at", 2),
                new(OpCode.LoadLocal, 0), Native("storage.vector_size", 1), new(OpCode.Add), new(OpCode.Return)
            ], TypeRef.Number)
        ], OptimizationLevel: 0);
        string cwd = Environment.CurrentDirectory;
        foreach (var module in new[] { program, RoundTrip(program), RoundTrip(Binding.Bind(program, new(cwd, cwd))) })
        {
            List<RuntimeTimelineRecord> records = [];
            RuntimeProfile profile = new(instructionSites: true);
            var result = await Interpreter.ExecuteAsync(module, new()
            {
                Profile = profile,
                Timeline = new(records.Add, "root")
            });
            Require(result is NumberValue { Data: 42 }, "Inline operations changed the computed value.");
            var calls = profile.Drain().Where(record => record.Name == "vm.native")
                .ToDictionary(record => record.Attributes["native.name"]);
            foreach (string operation in new[] { "math.floor", "storage.vector_set", "storage.vector_size", "ranges.front", "ranges.at" })
                Require(calls[operation].Metrics["fast_calls"] == 1 && calls[operation].Metrics["calls"] == 1,
                    $"Expected the {operation} stack path.");
            Require(calls["storage.vector_get"].Metrics["fast_calls"] == 1 && calls["storage.vector_get"].Metrics["calls"] == 2,
                "Raw Cell operand did not use the original normalization boundary.");
            Require(records.Count(record => record.Name == "vm.native" && record.Attributes["native.route"] == "inline") == 6,
                "Detailed diagnostics lost inline native spans.");
        }

        await CheckFaultAsync(new NumberValue(-1), "ranges.at", "nonnegative integral count");
        await CheckFaultAsync(new NumberValue(double.PositiveInfinity), "ranges.at", "finite Number arguments");
        await CheckFaultAsync(new NumberValue(1), "ranges.at", "no item at index 1");
        await CheckFaultAsync(new NumberValue(0.5), "storage.vector_get", "index 0.5 is outside");
        await CheckUnownedRangeAsync();
        Console.WriteLine("Inline native checks passed.");
    }

    private static async Task CheckFaultAsync(NumberValue index, string operation, string detail)
    {
        List<Instruction> code = [new(OpCode.Constant, 0), new(OpCode.MakeRange, 1)];
        if (operation == "storage.vector_get") code.Add(Native("storage.vector_new", 1));
        code.Add(new(OpCode.Constant, 1));
        code.Add(Native(operation, 2));
        code.Add(new(OpCode.Return));
        Module module = RoundTrip(new([new NumberValue(1), index], [new("fault", 0, 0, code, TypeRef.Any)], OptimizationLevel: 0));
        List<RuntimeTimelineRecord> records = [];
        try
        {
            await Interpreter.ExecuteAsync(module, new() { Timeline = new(records.Add, "root") });
            throw new InvalidOperationException($"Expected {operation} to fail.");
        }
        catch (MsyncException error)
        {
            Require(error.Message.Contains(detail, StringComparison.Ordinal) && error.Span == Source,
                $"Incorrect inline {operation} diagnostic: {error}");
            var span = records.Single(record => record.Name == "vm.native" && record.Attributes["native.name"] == operation);
            Require(span.Outcome == "error" && span.Attributes["native.route"] == "inline", "Inline failure was not observed.");
        }
    }

    private static async Task CheckUnownedRangeAsync()
    {
        int yielded = 0;
        async IAsyncEnumerable<Value> Lazy()
        {
            yielded++;
            yield return new NumberValue(17);
            await Task.CompletedTask;
        }

        Module module = new([new RangeValue(Lazy(), TypeRef.Number)], [
            new("fallback", 0, 0, [new(OpCode.Constant, 0), Native("ranges.front", 1), new(OpCode.Return)], TypeRef.Number)
        ], OptimizationLevel: 0);
        RuntimeProfile profile = new();
        Require(await Interpreter.ExecuteAsync(module, new() { Profile = profile }) is NumberValue { Data: 17 } && yielded == 1,
            "Unowned Range did not execute its selected producer.");
        Require(profile.Drain().Single(record => record.Name == "vm.native").Metrics["fast_calls"] == 0,
            "Unowned Range was admitted to the owned-snapshot path.");
    }

    private static Instruction Native(string operation, int arity) => new(OpCode.NativeCall, arity, Text: operation, Span: Source);
    private static readonly SourceSpan Source = new("inline-native.msync", 11, 4);

    private static Module RoundTrip(Module module)
    {
        using MemoryStream stream = new();
        Artifact.Write(module, stream);
        stream.Position = 0;
        return Artifact.Read(stream, formBlocks: false);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
