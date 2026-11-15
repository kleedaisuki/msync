using Msync.Compiler;
using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class NativeTailChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Linked, NativeJitPolicy.Force })
        {
            await BoundResult(policy, check);
            await ExposedResult(policy, check);
            await FailureSpan(policy, check);
        }
        await LogicalProfiles(check);
    }

    private static NativeProgram Detached(NativeProgram program)
    {
        program = Msync.Optimization.NativeOptimizer.PlanStorage(program);
        NativeArtifact.Validate(program);
        using MemoryStream stream = new();
        NativeArtifact.Write(program, stream);
        stream.Position = 0;
        return NativeArtifact.Read(stream);
    }

    private static NativeFunctionCode FortyTwo() => new("forty-two", [new(0)],
        [new(NativeOp.Constant, 0, 0), new(NativeOp.Return, A: 0)], new([], 0, []));

    private static async Task BoundResult(NativeJitPolicy policy, Action<bool, string> check)
    {
        NativeProgram program = Detached(new([new(NativeKind.Number, Number: 42)],
            [new("bound-result", [new(2, NativeSlotRole.Parameter), new(0), new(1)],
                [new(NativeOp.BindLocation, A: 1, B: 0), new(NativeOp.MakeFunction, 2, 1, C: 0),
                 new(NativeOp.Call, 1, 2, 1), new(NativeOp.Return, A: 1)], new([2], 0, []), ParameterCount: 1), FortyTwo()],
            [new(TypeKind.Number), new(TypeKind.Function), new(TypeKind.Never, Pointee: 0)], []));
        NativeAddress output = new NativeAllocation(new NativeLayout(NativeType.Of(NativeKind.Number))).Field(0);
        output.Store(NativeValue.FromNumber(1));
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(policy) });
        NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromAddress(output)]);
        check(result.AsNumber() == 42 && output.Load().AsNumber() == 42,
            $"Tail elimination lost an externally bound result write ({policy})");
    }

    private static async Task ExposedResult(NativeJitPolicy policy, Action<bool, string> check)
    {
        NativeProgram program = Detached(new([new(NativeKind.Number, Number: 42)],
            [new("exposed-result", [new(3, NativeSlotRole.Parameter), new(0), new(2), new(1)],
                [new(NativeOp.FrameAddress, 2, 1), new(NativeOp.Store, A: 0, B: 2, C: 1, Type: 2),
                 new(NativeOp.MakeFunction, 3, 1, C: 0), new(NativeOp.Call, 1, 3, 1), new(NativeOp.Return, A: 1)],
                new([3], 0, []), ParameterCount: 1), FortyTwo()],
            [new(TypeKind.Number), new(TypeKind.Function), new(TypeKind.Never, Pointee: 0),
             new(TypeKind.Never, Pointee: 2)], []));
        NativeAddress saved = new NativeAllocation(new NativeLayout(NativeType.Of(NativeKind.Address))).Field(0);
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(policy) });
        NativeValue result = await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [NativeValue.FromAddress(saved)]);
        check(result.AsNumber() == 42 && saved.Load().AsAddress().Load().AsNumber() == 42,
            $"Tail elimination lost a write through an escaped frame location ({policy})");
    }

    private static async Task FailureSpan(NativeJitPolicy policy, Action<bool, string> check)
    {
        SourceSpan source = new("tail-call.msync", 12, 7);
        NativeProgram program = Detached(new([], [
            new("caller", [new(1), new(0)],
                [new(NativeOp.MakeFunction, 0, 1, C: 0), new(NativeOp.Call, 1, 0, Source: source),
                 new(NativeOp.Return, A: 1)], new([], 0, [])),
            new("template", [], [], new([], 0, []), IsTemplate: true)],
            [new(TypeKind.Number), new(TypeKind.Function)], []));
        await using NativeInterpreter interpreter = new(program, new() { Tiers = new(policy) });
        try
        {
            await interpreter.ExecuteAsync();
            check(false, $"A template tail target unexpectedly executed ({policy})");
        }
        catch (MsyncException error)
        {
            check(error.Span == source && error.Message.Contains("specialization", StringComparison.Ordinal),
                $"Tail target failure lost the original call-site diagnostic ({policy}): {error}");
        }
    }

    private static async Task LogicalProfiles(Action<bool, string> check)
    {
        const string source = "fn sum(n, acc) { if n == 0 { acc } else { sum(n - 1, acc + n) } }; sum(500, 0)";
        NativeProgram program = Detached(Compiler.Compile(source, "profile-tail.msync", 2));
        SortedDictionary<string, ulong>? baseline = null;
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Linked, NativeJitPolicy.Force })
        {
            RuntimeProfile profile = new(instructionSites: true);
            await using NativeInterpreter interpreter = new(program, new() { Profile = profile, Tiers = new(policy) });
            check((await interpreter.ExecuteAsync()).AsNumber() == 125250, $"Deep typed tail execution failed ({policy})");
            SortedDictionary<string, ulong> counters = new(StringComparer.Ordinal);
            foreach (RuntimeProfileSample sample in profile.Drain().Where(sample => sample.Name is
                "vm.instruction" or "vm.opcode" or "vm.edge" or "vm.call"))
            {
                string key = sample.Name + ":" + string.Join(";", sample.Attributes.OrderBy(pair => pair.Key)
                    .Select(pair => pair.Key + "=" + pair.Value));
                counters.Add(key, sample.Metrics["hits"]);
            }
            baseline ??= counters;
            check(baseline.SequenceEqual(counters), $"Tail execution changed logical instructions/edges/targets ({policy})");
        }
    }
}
