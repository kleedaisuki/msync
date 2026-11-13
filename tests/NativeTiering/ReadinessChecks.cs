using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class ReadinessChecks
{
    internal static async Task<int> RunAsync()
    {
        NativeProgram program = new([new(NativeKind.Number, Number: 1), new(NativeKind.Number, Number: 0)], [
            new("reader", [new(1, NativeSlotRole.Parameter), new(0), new(0), new(0), new(2)],
                [new(NativeOp.BindLocation, A: 1, B: 0), new(NativeOp.Constant, 2, 0),
                 new(NativeOp.CallNative, 4, Symbol: "io.read_line", Inputs: []),
                 new(NativeOp.Binary, 3, 1, 2, Immediate: (long)NativeBinary.Add), new(NativeOp.Return, A: 3)],
                new([1], 0, []), ParameterCount: 1),
            new("writer", [new(1, NativeSlotRole.Parameter), new(3, NativeSlotRole.Parameter), new(0), new(0)],
                [new(NativeOp.CallNative, 2, Symbol: "process.exit_code", Inputs: [1]),
                 new(NativeOp.Store, A: 0, B: 2, C: 0, Type: 0), new(NativeOp.Constant, 3, 1), new(NativeOp.Return, A: 3)],
                new([1, 3], 0, []), ParameterCount: 2)
        ], [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0), new(TypeKind.String | TypeKind.Null), new(TypeKind.Process)], []);
        NativeArtifact.Validate(program);
        foreach (bool copy in new[] { false, true })
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            NativeProgram selected = !copy ? program : program with { Functions = [program.Functions[0] with
            { Instructions = program.Functions[0].Instructions.Select(instruction => instruction.Op == NativeOp.Binary
                ? new NativeInstruction(NativeOp.Copy, 3, 1, Type: 0) : instruction).ToArray() }, program.Functions[1]] };
            GateReader gate = new();
            TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            NativeAddress address = new NativeAllocation(new(NativeType.Of(NativeKind.Number))).Field(0);
            address.Store(NativeValue.FromNumber(0));
            NativeValue pointer = NativeValue.FromAddress(address);
            await using NativeInterpreter interpreter = new(selected, new() { Input = gate, Tiers = new(policy, 1, 1, 1) });
            Task<NativeValue> reader = interpreter.InvokeAsync(NativeValue.FromFunction(new(0)), [pointer]).AsTask();
            try
            {
                await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await interpreter.InvokeAsync(NativeValue.FromFunction(new(1)),
                    [pointer, NativeValue.FromProcess(new(0, "", "") { Completion = completion.Task })]);
                address.Store(NativeValue.FromNumber(9));
                gate.Release.SetResult("resume");
                await Task.Delay(10);
                if (reader.IsCompleted) throw new InvalidOperationException("Physical init bytes incorrectly overrode canonical pending semantics.");
                completion.SetResult(new(23, "", ""));
                if ((await reader.WaitAsync(TimeSpan.FromSeconds(5))).AsNumber() != (copy ? 23 : 24) || gate.Reads != 1)
                    throw new InvalidOperationException("A resumed operand lost its pending payload or replayed input.");
                if (!copy && policy == NativeJitPolicy.Force && interpreter.TierStatistics.GuardMisses == 0)
                    throw new InvalidOperationException("Native arithmetic did not leave its genuinely pending operand.");
            }
            finally { gate.Release.TrySetResult("resume"); completion.TrySetResult(new(23, "", "")); }
        }
        await FixedCopyAsync();
        return 6;
    }

    private static async Task FixedCopyAsync()
    {
        NativeProgram program = new([], [new("fixed-copy", [new(4, NativeSlotRole.Parameter), new(3, NativeSlotRole.Parameter),
            new(0), new(1), new(0), new(2)],
            [new(NativeOp.CallNative, 2, Symbol: "process.exit_code", Inputs: [1]), new(NativeOp.FrameAddress, 3, 2),
             new(NativeOp.Store, A: 0, B: 3, C: 0, Type: 1), new(NativeOp.CallNative, 5, Symbol: "io.read_line", Inputs: []),
             new(NativeOp.Copy, 4, 2, Type: 0), new(NativeOp.Return, A: 4)], new([4, 3], 0, []), ParameterCount: 2)],
            [new(TypeKind.Number), new(TypeKind.Never, Pointee: 0), new(TypeKind.String | TypeKind.Null),
             new(TypeKind.Process), new(TypeKind.Never, Pointee: 1)], []);
        NativeArtifact.Validate(program);
        NativeExecutionFunction code = new(new(program, new()), 0, new(NativeJitPolicy.Off));
        if (code.Operands[4].Type is null) throw new InvalidOperationException("Fixed copy fixture did not exercise linked same-ABI transfer.");
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            GateReader gate = new();
            TaskCompletionSource<NativeProcess> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            NativeAddress published = new NativeAllocation(new(NativeType.Of(NativeKind.Address))).Field(0);
            await using NativeInterpreter interpreter = new(program, new() { Input = gate, Tiers = new(policy, 1, 1, 1) });
            Task<NativeValue> result = interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                [NativeValue.FromAddress(published), NativeValue.FromProcess(new(0, "", "") { Completion = completion.Task })]).AsTask();
            try
            {
                await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                published.Load().AsAddress().Store(NativeValue.FromNumber(9));
                gate.Release.SetResult("resume");
                await Task.Delay(10);
                if (result.IsCompleted) throw new InvalidOperationException("Linked fixed-ABI copy copied shadow bytes instead of its pending payload.");
                completion.SetResult(new(23, "", ""));
                if ((await result.WaitAsync(TimeSpan.FromSeconds(5))).AsNumber() != 23 || gate.Reads != 1)
                    throw new InvalidOperationException("Linked fixed-ABI copy changed Future identity/value or replayed input.");
            }
            finally { gate.Release.TrySetResult("resume"); completion.TrySetResult(new(23, "", "")); }
        }
    }
}
