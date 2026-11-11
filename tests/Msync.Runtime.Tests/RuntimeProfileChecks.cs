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
        Console.WriteLine("Runtime profiling checks passed.");
    }

    private static bool Match(RuntimeProfileSample sample, string name, string key, string value) =>
        sample.Name == name && sample.Attributes.TryGetValue(key, out string? actual) && actual == value;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
