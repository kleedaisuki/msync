using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class NumberAccessChecks
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        foreach (NativeBinary operation in Enum.GetValues<NativeBinary>().Where(op => op <= NativeBinary.GreaterEqual))
        {
            int result = operation >= NativeBinary.Equal ? 1 : 0;
            NativeProgram program = new([], [new("numbers", [new(0, NativeSlotRole.Parameter),
                new(0, NativeSlotRole.Parameter), new(result)],
                [new(NativeOp.Binary, 2, 0, 1, Immediate: (long)operation), new(NativeOp.Return, A: 2)],
                new([0, 0], result, []), ParameterCount: 2)], [new(TypeKind.Number), new(TypeKind.Bool)], []);
            program = TierFixture.Plan(program);
            NativeArtifact.Validate(program);
            NativeExecutionFunction linked = new(TierFixture.Link(program), 0, new(NativeJitPolicy.Off));
            if (linked.Instructions[0].Handler != NativeInterpreter.ExecutionHandlerAddress(NativeOp.Binary, number: true))
                throw new InvalidOperationException("Monomorphic arithmetic kept the generic value handler.");
            foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Linked, NativeJitPolicy.Force })
            {
                await using NativeInterpreter vm = new(program, new() { Tiers = new(policy, 1, 1, 1) });
                foreach (double left in new[] { -0d, 0d, -3d, double.NaN, double.PositiveInfinity })
                foreach (double right in new[] { -0d, 2d })
                {
                    NativeValue actual = await vm.InvokeAsync(NativeValue.FromFunction(new(0)),
                        [NativeValue.FromNumber(left), NativeValue.FromNumber(right)]);
                    bool correct = result == 1 ? actual.AsBool() == (operation switch
                    {
                        NativeBinary.Equal => left == right, NativeBinary.NotEqual => left != right,
                        NativeBinary.Less => left < right, NativeBinary.LessEqual => left <= right,
                        NativeBinary.Greater => left > right, _ => left >= right
                    }) : Same(actual.AsNumber(), operation switch
                    {
                        NativeBinary.Add => left + right, NativeBinary.Subtract => left - right,
                        NativeBinary.Multiply => left * right, NativeBinary.Divide => left / right,
                        NativeBinary.Remainder => left % right, _ => Math.Pow(left, right)
                    });
                    if (!correct) throw new InvalidOperationException($"{policy}/{operation}: typed number memory changed IEEE behavior.");
                    checks++;
                }
            }
        }
        NativeProgram pending = new([new(NativeKind.Number, Number: 99)],
            [new("snapshot", [new(0, NativeSlotRole.Parameter), new(1, NativeSlotRole.Parameter), new(0), new(0)],
                [new(NativeOp.CallNative, 2, Symbol: "process.exit_code", Inputs: [1]),
                 new(NativeOp.Binary, 3, 0, 2, Immediate: (long)NativeBinary.Add), new(NativeOp.Constant, 0, 0),
                 new(NativeOp.Return, A: 3)], new([0, 1], 0, []), ParameterCount: 2)],
            [new(TypeKind.Number), new(TypeKind.Process)], []);
        pending = TierFixture.Plan(pending);
        NativeArtifact.Validate(pending);
        using MemoryStream bytes = new(); NativeArtifact.Write(pending, bytes); bytes.Position = 0;
        pending = NativeArtifact.Read(bytes);
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Linked, NativeJitPolicy.Force })
        {
            TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using NativeInterpreter vm = new(pending, new() { Tiers = new(policy, 1, 1, 1) });
            Task<NativeValue> answer = vm.InvokeAsync(NativeValue.FromFunction(new(0)),
                [NativeValue.FromNumber(13), NativeValue.FromProcess(new(0, "", "") { Completion = completion.Task })]).AsTask();
            if (answer.IsCompleted) throw new InvalidOperationException("A pending operand was read as raw number storage.");
            completion.SetResult(new(7, "", ""));
            if ((await answer.WaitAsync(TimeSpan.FromSeconds(5))).AsNumber() != 20)
                throw new InvalidOperationException("Pending arithmetic reread the mutated ready operand.");
            checks++;
        }
        return checks;
    }

    private static bool Same(double actual, double expected) => double.IsNaN(expected)
        ? double.IsNaN(actual) : BitConverter.DoubleToInt64Bits(actual) == BitConverter.DoubleToInt64Bits(expected);
}
